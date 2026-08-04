using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace GCS.Core.Logging;

/// <summary>One packet as it was recorded: when it arrived, and its raw bytes.</summary>
public readonly record struct TlogRecord(DateTime TimestampUtc, ReadOnlyMemory<byte> Packet);

/// <summary>
/// Reads the .tlog files written by <see cref="TelemetryLogger"/>: an 8-byte
/// big-endian microsecond timestamp followed by one complete MAVLink packet,
/// repeated.
///
/// This cannot reuse <see cref="Mavlink.MavlinkFrameBuffer"/>, which scans a
/// continuous byte stream — here the stream is deliberately interrupted by a
/// timestamp before every packet, so the length must be taken from the header and
/// exactly that many bytes consumed.
///
/// Recording can stop mid-packet if the app is killed, so a truncated tail is
/// treated as end-of-file rather than an error.
/// </summary>
public static class TlogReader
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const byte MavlinkV2Start = 0xFD;
    private const byte MavlinkV1Start = 0xFE;
    private const int V2HeaderLen = 10;
    private const int V1HeaderLen = 6;
    private const int ChecksumLen = 2;
    private const int SignatureLen = 13;
    private const byte IflagSigned = 0x01;

    /// <summary>Sanity bound: a MAVLink payload is at most 255 bytes.</summary>
    private const int MaxPacketLen = V2HeaderLen + 255 + ChecksumLen + SignatureLen;

    public static IEnumerable<TlogRecord> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        foreach (var record in Read(stream)) yield return record;
    }

    public static IEnumerable<TlogRecord> Read(Stream stream)
    {
        var timestamp = new byte[8];
        var header = new byte[V2HeaderLen];

        while (true)
        {
            if (!ReadExactly(stream, timestamp, 8)) yield break;

            ulong micros = BinaryPrimitives.ReadUInt64BigEndian(timestamp);
            DateTime time = ToUtc(micros);

            // Resynchronise on the start byte: a partially written packet or any
            // stray byte would otherwise throw the rest of the file out of step.
            int start = stream.ReadByte();
            while (start >= 0 && start != MavlinkV2Start && start != MavlinkV1Start)
                start = stream.ReadByte();

            if (start < 0) yield break;

            bool v2 = start == MavlinkV2Start;
            int headerLen = v2 ? V2HeaderLen : V1HeaderLen;

            header[0] = (byte)start;
            if (!ReadExactly(stream, header, headerLen - 1, offset: 1)) yield break;

            int payloadLen = header[1];
            int signature = v2 && (header[2] & IflagSigned) != 0 ? SignatureLen : 0;
            int total = headerLen + payloadLen + ChecksumLen + signature;

            if (total > MaxPacketLen) continue;   // corrupt length: skip to next record

            var packet = new byte[total];
            Array.Copy(header, packet, headerLen);
            if (!ReadExactly(stream, packet, total - headerLen, offset: headerLen)) yield break;

            yield return new TlogRecord(time, packet);
        }
    }

    /// <summary>
    /// Timestamps come from the recording machine's clock. A wildly out-of-range
    /// value means a corrupt record rather than a real time, so clamp instead of
    /// throwing out of an iterator.
    /// </summary>
    private static DateTime ToUtc(ulong micros)
    {
        try
        {
            var time = UnixEpoch.AddTicks((long)micros * 10);
            return time.Year is < 2000 or > 2200 ? DateTime.MinValue : time;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count, int offset = 0)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, offset + read, count - read);
            if (n <= 0) return false;   // truncated tail — treat as end of file
            read += n;
        }
        return true;
    }
}
