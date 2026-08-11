using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GCS.Core.Domain;
using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;

namespace GCS.Core.Mavlink.Messages;

/// <summary>Shared field reading. MavLinkSharp hands back arrays in several shapes.</summary>
internal static class FrameFields
{
    public static float F32(Frame frame, string name) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToSingle(v) : 0f;

    public static uint U32(Frame frame, string name) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToUInt32(v) : 0u;

    public static ushort U16(Frame frame, string name) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToUInt16(v) : (ushort)0;

    public static byte U8(Frame frame, string name) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToByte(v) : (byte)0;

    public static short I16(Frame frame, string name) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToInt16(v) : (short)0;

    public static int I32(Frame frame, string name) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToInt32(v) : 0;

    /// <summary>
    /// Read an array field. The decoder may return a typed array, a list, or a
    /// single value for a length-1 array, so all three are handled.
    /// </summary>
    public static T[] Array<T>(Frame frame, string name, int expected, Func<object, T> convert)
    {
        var result = new T[expected];
        if (!frame.Fields.TryGetValue(name, out var raw) || raw is null) return result;

        if (raw is IEnumerable seq and not string)
        {
            int i = 0;
            foreach (var item in seq)
            {
                if (i >= expected) break;
                result[i++] = convert(item);
            }
            return result;
        }

        result[0] = convert(raw);
        return result;
    }
}

/// <summary>VIBRATION (msg 241).</summary>
public sealed class VibrationHandler : IMavlinkMessageHandler
{
    public uint MessageId => 241;
    private readonly Action<byte, VibrationState> _onVibration;

    public VibrationHandler(Action<byte, VibrationState> onVibration) => _onVibration = onVibration;

    public void Handle(Frame frame)
    {
        try
        {
            _onVibration(frame.SystemId, new VibrationState(
                FrameFields.F32(frame, "vibration_x"),
                FrameFields.F32(frame, "vibration_y"),
                FrameFields.F32(frame, "vibration_z"),
                FrameFields.U32(frame, "clipping_0"),
                FrameFields.U32(frame, "clipping_1"),
                FrameFields.U32(frame, "clipping_2"),
                DateTime.UtcNow));
        }
        catch (Exception ex) { Debug.WriteLine($"[Vibration] {ex.Message}"); }
    }
}

/// <summary>EKF_STATUS_REPORT (msg 193).</summary>
public sealed class EkfStatusHandler : IMavlinkMessageHandler
{
    public uint MessageId => 193;
    private readonly Action<byte, EkfStatusState> _onEkf;

    public EkfStatusHandler(Action<byte, EkfStatusState> onEkf) => _onEkf = onEkf;

    public void Handle(Frame frame)
    {
        try
        {
            _onEkf(frame.SystemId, new EkfStatusState(
                FrameFields.U16(frame, "flags"),
                FrameFields.F32(frame, "velocity_variance"),
                FrameFields.F32(frame, "pos_horiz_variance"),
                FrameFields.F32(frame, "pos_vert_variance"),
                FrameFields.F32(frame, "compass_variance"),
                FrameFields.F32(frame, "terrain_alt_variance"),
                DateTime.UtcNow));
        }
        catch (Exception ex) { Debug.WriteLine($"[EkfStatus] {ex.Message}"); }
    }
}

/// <summary>
/// ESTIMATOR_STATUS (msg 230) — PX4's equivalent of ArduPilot's EKF_STATUS_REPORT.
/// Mapped onto the same state so the health rules do not need a second code path.
/// </summary>
public sealed class EstimatorStatusHandler : IMavlinkMessageHandler
{
    public uint MessageId => 230;
    private readonly Action<byte, EkfStatusState> _onEkf;

    public EstimatorStatusHandler(Action<byte, EkfStatusState> onEkf) => _onEkf = onEkf;

    public void Handle(Frame frame)
    {
        try
        {
            // ESTIMATOR_STATUS_FLAGS uses different bits from EKF_STATUS_FLAGS, so
            // flags are not forwarded — only the variances, which mean the same
            // thing and drive the thresholds. Passing 0 keeps the flag checks
            // inactive rather than misreading PX4's bits as ArduPilot's.
            _onEkf(frame.SystemId, new EkfStatusState(
                Flags: 0,
                VelocityVariance: FrameFields.F32(frame, "vel_ratio"),
                PosHorizVariance: FrameFields.F32(frame, "pos_horiz_ratio"),
                PosVertVariance: FrameFields.F32(frame, "pos_vert_ratio"),
                CompassVariance: FrameFields.F32(frame, "mag_ratio"),
                TerrainAltVariance: FrameFields.F32(frame, "hagl_ratio"),
                TimestampUtc: DateTime.UtcNow));
        }
        catch (Exception ex) { Debug.WriteLine($"[EstimatorStatus] {ex.Message}"); }
    }
}

/// <summary>BATTERY_STATUS (msg 147).</summary>
public sealed class BatteryStatusHandler : IMavlinkMessageHandler
{
    public uint MessageId => 147;
    private readonly Action<byte, BatteryStatusState> _onBattery;

    public BatteryStatusHandler(Action<byte, BatteryStatusState> onBattery) => _onBattery = onBattery;

    public void Handle(Frame frame)
    {
        try
        {
            var cells = FrameFields.Array(frame, "voltages", 10, o => Convert.ToUInt16(o));

            _onBattery(frame.SystemId, new BatteryStatusState(
                cells,
                FrameFields.I32(frame, "current_consumed"),
                // Decidegrees C; INT16_MAX means "not measured".
                FrameFields.I16(frame, "temperature") == short.MaxValue
                    ? -300f
                    : FrameFields.I16(frame, "temperature") / 100f,
                Convert.ToSByte(frame.Fields.TryGetValue("battery_remaining", out var r) ? r : (sbyte)-1),
                DateTime.UtcNow));
        }
        catch (Exception ex) { Debug.WriteLine($"[BatteryStatus] {ex.Message}"); }
    }
}

/// <summary>POWER_STATUS (msg 125).</summary>
public sealed class PowerStatusHandler : IMavlinkMessageHandler
{
    public uint MessageId => 125;
    private readonly Action<byte, PowerStatusState> _onPower;

    public PowerStatusHandler(Action<byte, PowerStatusState> onPower) => _onPower = onPower;

    public void Handle(Frame frame)
    {
        try
        {
            _onPower(frame.SystemId, new PowerStatusState(
                FrameFields.U16(frame, "Vcc") / 1000f,
                FrameFields.U16(frame, "Vservo") / 1000f,
                FrameFields.U16(frame, "flags"),
                DateTime.UtcNow));
        }
        catch (Exception ex) { Debug.WriteLine($"[PowerStatus] {ex.Message}"); }
    }
}

/// <summary>
/// ESC_TELEMETRY_1_TO_4 / _5_TO_8 / _9_TO_12 (msgs 291-293). Each message carries
/// four ESCs, so the block index decides where they land in the combined array.
/// </summary>
public sealed class EscTelemetryHandler : IMavlinkMessageHandler
{
    public uint MessageId { get; }
    private readonly int _blockIndex;
    private readonly Action<byte, int, EscReading[]> _onEsc;

    public EscTelemetryHandler(uint messageId, int blockIndex, Action<byte, int, EscReading[]> onEsc)
    {
        MessageId = messageId;
        _blockIndex = blockIndex;
        _onEsc = onEsc;
    }

    /// <summary>ArduPilot dialect ids; the 291-293 range is unrelated in Common.</summary>
    public const uint Block1To4 = 11030;
    public const uint Block5To8 = 11031;
    public const uint Block9To12 = 11032;

    public static IEnumerable<EscTelemetryHandler> All(Action<byte, int, EscReading[]> onEsc) => new[]
    {
        new EscTelemetryHandler(Block1To4, 0, onEsc),
        new EscTelemetryHandler(Block5To8, 1, onEsc),
        new EscTelemetryHandler(Block9To12, 2, onEsc),
    };

    public void Handle(Frame frame)
    {
        try
        {
            var temps = FrameFields.Array(frame, "temperature", 4, o => Convert.ToByte(o));
            var volts = FrameFields.Array(frame, "voltage", 4, o => Convert.ToUInt16(o));
            var amps = FrameFields.Array(frame, "current", 4, o => Convert.ToUInt16(o));
            var rpm = FrameFields.Array(frame, "rpm", 4, o => Convert.ToUInt16(o));

            var readings = new EscReading[4];
            for (int i = 0; i < 4; i++)
                readings[i] = new EscReading(temps[i], rpm[i], volts[i], amps[i]);

            _onEsc(frame.SystemId, _blockIndex, readings);
        }
        catch (Exception ex) { Debug.WriteLine($"[EscTelemetry] {ex.Message}"); }
    }
}
