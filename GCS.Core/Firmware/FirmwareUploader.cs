using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Firmware;

/// <summary>Info read from a board's bootloader.</summary>
public sealed record BootloaderInfo(uint BoardId, uint BootloaderRev, uint FlashSize);

/// <summary>Result of a detection attempt (never throws for the "not found" case).</summary>
public sealed record DetectResult(bool Success, BootloaderInfo? Info, string Message);

/// <summary>Progress update during a flash.</summary>
public sealed record FlashProgress(string Phase, double Percent);

/// <summary>
/// Talks the ArduPilot/PX4 bootloader protocol over a raw serial port (115200):
/// board detection and the erase/program/verify/reboot flash sequence. Protocol
/// matches ArduPilot's Tools/scripts/uploader.py. NOTE: serial timing is validated
/// on real hardware.
/// </summary>
public sealed class FirmwareUploader
{
    // Protocol bytes.
    private const byte INSYNC = 0x12;
    private const byte EOC = 0x20;
    private const byte OK = 0x10;
    private const byte FAILED = 0x11;
    private const byte INVALID = 0x13;
    private const byte GET_SYNC = 0x21;
    private const byte GET_DEVICE = 0x22;
    private const byte CHIP_ERASE = 0x23;
    private const byte PROG_MULTI = 0x27;
    private const byte GET_CRC = 0x29;   // rev3+
    private const byte REBOOT = 0x30;

    // GET_DEVICE info ids.
    private const byte INFO_BL_REV = 1;
    private const byte INFO_BOARD_ID = 2;
    private const byte INFO_FLASH_SIZE = 4;

    private const int PROG_MULTI_MAX = 252; // must be a multiple of 4

    // MAVLink start bytes - seeing these means the board is running normal
    // firmware (streaming telemetry), not sitting in the bootloader.
    private const byte MAVLINK_V2_STX = 0xFD;
    private const byte MAVLINK_V1_STX = 0xFE;

    // ── Detection ────────────────────────────────────────────────────

    /// <summary>
    /// Try to read the board's bootloader. Returns a result rather than throwing
    /// for the common "not in bootloader / no board" cases, so it never breaks
    /// into the debugger or bubbles up as an unhandled exception.
    /// </summary>
    public Task<DetectResult> DetectAsync(string portName, CancellationToken ct = default)
        => Task.Run(() => Detect(portName), ct);

    private static DetectResult Detect(string portName)
    {
        try
        {
            using var port = Open(portName);
            if (!TrySync(port, out bool sawMavlink))
            {
                return new DetectResult(false, null, sawMavlink
                    ? "Board is running normal firmware (MAVLink detected), not in bootloader mode. Reboot to bootloader first, or unplug/replug and Detect within a few seconds."
                    : "No bootloader response on this port. Put the board in bootloader mode and try again.");
            }

            var info = ReadInfo(port);
            return new DetectResult(true, info,
                $"Board ID: {info.BoardId}   Bootloader rev: {info.BootloaderRev}   Flash: {info.FlashSize:N0} bytes");
        }
        catch (Exception ex)
        {
            return new DetectResult(false, null, $"Detect failed: {ex.Message}");
        }
    }

    // ── Flashing ─────────────────────────────────────────────────────

    public Task FlashAsync(
        string portName,
        ApjFirmware firmware,
        IProgress<FlashProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Flash(portName, firmware, progress, ct), ct);

    private static void Flash(string portName, ApjFirmware fw, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        byte[] image = PadTo4(fw.Image);

        using var port = Open(portName);

        Report(progress, "Syncing", 0);
        RequireSync(port);
        var info = ReadInfo(port);

        if (fw.BoardId != 0 && info.BoardId != (uint)fw.BoardId)
            throw new IOException(
                $"Board mismatch: firmware is for board {fw.BoardId}, connected board is {info.BoardId}. Aborted (nothing was written).");
        if ((uint)image.Length > info.FlashSize)
            throw new IOException(
                $"Firmware ({image.Length:N0} bytes) is larger than this board's flash ({info.FlashSize:N0} bytes).");

        ct.ThrowIfCancellationRequested();
        Report(progress, "Erasing (do not disconnect power)", 0);
        Erase(port);

        Report(progress, "Programming", 0);
        Program(port, image, progress, ct);

        Report(progress, "Verifying", 0);
        Verify(port, image, info.FlashSize);

        Report(progress, "Rebooting", 100);
        Reboot(port);

        Report(progress, "Done", 100);
    }

    // ── Protocol steps ───────────────────────────────────────────────

    private static SerialPort Open(string portName)
    {
        var port = new SerialPort(portName, 115200)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        port.Open();
        return port;
    }

    private static void RequireSync(SerialPort port)
    {
        if (TrySync(port, out bool sawMavlink)) return;
        throw new IOException(sawMavlink
            ? "Board is running normal firmware (MAVLink detected), not in bootloader mode. " +
              "Reboot it to the bootloader first (use \"Reboot to bootloader\", or unplug/replug and retry within a few seconds)."
            : "No response from the bootloader on this port. Put the board in bootloader mode and try again.");
    }

    /// <summary>Try to get INSYNC/OK from the bootloader, tolerating MAVLink noise.</summary>
    private static bool TrySync(SerialPort port, out bool sawMavlink)
    {
        sawMavlink = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { port.DiscardInBuffer(); } catch { /* ignore */ }
            port.Write(new byte[] { GET_SYNC, EOC }, 0, 2);

            try
            {
                int b1 = port.ReadByte();
                if (b1 == MAVLINK_V2_STX || b1 == MAVLINK_V1_STX)
                {
                    sawMavlink = true;
                    Thread.Sleep(50);
                    continue;
                }
                if (b1 != INSYNC) continue;
                if (port.ReadByte() == OK) return true;
            }
            catch (TimeoutException) { /* retry */ }
        }
        return false;
    }

    private static BootloaderInfo ReadInfo(SerialPort port)
    {
        uint blRev = GetInfo(port, INFO_BL_REV);
        uint boardId = GetInfo(port, INFO_BOARD_ID);
        uint flashSize = GetInfo(port, INFO_FLASH_SIZE);
        return new BootloaderInfo(boardId, blRev, flashSize);
    }

    private static uint GetInfo(SerialPort port, byte param)
    {
        port.Write(new byte[] { GET_DEVICE, param, EOC }, 0, 3);
        uint value = ReadUInt32(port);
        ExpectSync(port);
        return value;
    }

    private static void Erase(SerialPort port)
    {
        int previous = port.ReadTimeout;
        port.ReadTimeout = 25000; // chip erase can take ~20 s; bootloader acks when done
        try
        {
            port.Write(new byte[] { CHIP_ERASE, EOC }, 0, 2);
            ExpectSync(port);
        }
        finally
        {
            port.ReadTimeout = previous;
        }
    }

    private static void Program(SerialPort port, byte[] image, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        int total = image.Length;
        int sent = 0;
        var buf = new byte[PROG_MULTI_MAX + 3];

        while (sent < total)
        {
            ct.ThrowIfCancellationRequested();

            int len = Math.Min(PROG_MULTI_MAX, total - sent);
            buf[0] = PROG_MULTI;
            buf[1] = (byte)len;
            Array.Copy(image, sent, buf, 2, len);
            buf[2 + len] = EOC;

            port.Write(buf, 0, len + 3);
            ExpectSync(port);

            sent += len;
            progress?.Report(new FlashProgress("Programming", 100.0 * sent / total));
        }
    }

    private static void Verify(SerialPort port, byte[] image, uint flashSize)
    {
        uint expected = ExpectedCrc(image, flashSize);
        port.Write(new byte[] { GET_CRC, EOC }, 0, 2);
        uint reported = ReadUInt32(port);
        ExpectSync(port);
        if (reported != expected)
            throw new IOException(
                $"Verification failed: board CRC 0x{reported:X8} != expected 0x{expected:X8}. " +
                "The board is still in the bootloader; you can retry.");
    }

    private static void Reboot(SerialPort port)
    {
        port.Write(new byte[] { REBOOT, EOC }, 0, 2);
        port.BaseStream.Flush();
    }

    private static void ExpectSync(SerialPort port)
    {
        int insync = port.ReadByte();
        if (insync != INSYNC)
            throw new IOException($"Bootloader: expected INSYNC, got 0x{insync:X2}.");
        int status = port.ReadByte();
        if (status == FAILED) throw new IOException("Bootloader reported OPERATION FAILED.");
        if (status == INVALID) throw new IOException("Bootloader reported INVALID OPERATION.");
        if (status != OK) throw new IOException($"Bootloader: unexpected status 0x{status:X2} (expected OK).");
    }

    private static uint ReadUInt32(SerialPort port)
    {
        var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)port.ReadByte();
        return BitConverter.ToUInt32(b, 0);
    }

    // ── CRC (matches uploader.py: table CRC-32, init 0, NO final XOR) ──

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint state)
    {
        foreach (byte b in data)
            state = CrcTable[(state ^ b) & 0xff] ^ (state >> 8);
        return state;
    }

    /// <summary>
    /// CRC the image then pad with 0xFF up to the flash size, exactly like the
    /// bootloader computes it over the whole application flash region.
    /// </summary>
    private static uint ExpectedCrc(byte[] image, uint flashSize)
    {
        uint state = Crc32(image, 0);
        Span<byte> ff = stackalloc byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        for (long i = image.Length; i < (long)flashSize - 1; i += 4)
            state = Crc32(ff, state);
        return state;
    }

    private static byte[] PadTo4(byte[] image)
    {
        int pad = (4 - image.Length % 4) % 4;
        if (pad == 0) return image;
        var result = new byte[image.Length + pad];
        Array.Copy(image, result, image.Length);
        for (int i = image.Length; i < result.Length; i++) result[i] = 0xFF;
        return result;
    }

    private static void Report(IProgress<FlashProgress>? progress, string phase, double percent)
        => progress?.Report(new FlashProgress(phase, percent));
}
