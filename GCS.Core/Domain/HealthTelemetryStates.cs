using System;
using System.Linq;

namespace GCS.Core.Domain;

/// <summary>
/// VIBRATION (msg 241). The standard ArduPilot vibration health signal.
/// Clipping counts are cumulative since boot, so only their growth is meaningful.
/// </summary>
public sealed record VibrationState(
    float VibrationX,
    float VibrationY,
    float VibrationZ,
    uint Clipping0,
    uint Clipping1,
    uint Clipping2,
    DateTime TimestampUtc
) : TimestampedState(TimestampUtc)
{
    public float Worst => Math.Max(VibrationX, Math.Max(VibrationY, VibrationZ));
    public uint TotalClipping => Clipping0 + Clipping1 + Clipping2;
}

/// <summary>
/// EKF_STATUS_REPORT (msg 193). Variances are ratios: below 0.5 is healthy,
/// above 1.0 means the estimator is struggling to reconcile its sensors.
/// </summary>
public sealed record EkfStatusState(
    ushort Flags,
    float VelocityVariance,
    float PosHorizVariance,
    float PosVertVariance,
    float CompassVariance,
    float TerrainAltVariance,
    DateTime TimestampUtc
) : TimestampedState(TimestampUtc)
{
    public float WorstVariance => Math.Max(VelocityVariance,
        Math.Max(PosHorizVariance, Math.Max(PosVertVariance, CompassVariance)));

    // EKF_STATUS_FLAGS bits that indicate a usable estimate.
    private const ushort AttitudeOk = 1;
    private const ushort HorizVelOk = 2;
    private const ushort VertVelOk = 4;

    public bool AttitudeHealthy => (Flags & AttitudeOk) != 0;
    public bool VelocityHealthy => (Flags & HorizVelOk) != 0 && (Flags & VertVelOk) != 0;
}

/// <summary>
/// SERVO_OUTPUT_RAW (msg 36), kept as state so motor balance can be judged.
/// Only the outputs actually driving motors are meaningful, so the consumer says
/// how many to look at.
/// </summary>
public sealed record ServoOutputState(
    ushort[] Raw,
    DateTime TimestampUtc
) : TimestampedState(TimestampUtc)
{
    /// <summary>Outputs that are live (non-zero), which is how many the frame uses.</summary>
    public ushort[] Active(int max = 8) =>
        Raw.Take(max).Where(v => v > 0).ToArray();
}

/// <summary>
/// BATTERY_STATUS (msg 147): richer than SYS_STATUS — per-cell voltages,
/// consumed capacity and pack temperature.
/// </summary>
public sealed record BatteryStatusState(
    ushort[] CellVoltagesMv,
    int ConsumedMah,
    float TemperatureC,
    int RemainingPercent,
    DateTime TimestampUtc
) : TimestampedState(TimestampUtc)
{
    /// <summary>Cells actually reported; unused entries are UINT16_MAX.</summary>
    public float[] CellVolts =>
        CellVoltagesMv.Where(mv => mv is > 0 and < ushort.MaxValue)
                      .Select(mv => mv / 1000f)
                      .ToArray();

    public int CellCount => CellVolts.Length;

    /// <summary>
    /// Spread between the strongest and weakest cell. A healthy pack stays within
    /// a few hundredths of a volt; a wide spread means a failing cell.
    /// </summary>
    public float CellImbalanceVolts
    {
        get
        {
            var cells = CellVolts;
            return cells.Length < 2 ? 0 : cells.Max() - cells.Min();
        }
    }

    public bool HasTemperature => TemperatureC > -270;
}

/// <summary>
/// POWER_STATUS (msg 125). A sagging 5 V rail is a brownout waiting to happen.
/// </summary>
public sealed record PowerStatusState(
    float RailVolts,
    float ServoRailVolts,
    ushort Flags,
    DateTime TimestampUtc
) : TimestampedState(TimestampUtc)
{
    // MAV_POWER_STATUS bits.
    private const ushort BrickValid = 1;
    private const ushort ServoValid = 2;
    private const ushort PeriphOvercurrent = 16;
    private const ushort PeriphHipowerOvercurrent = 32;
    private const ushort Changed = 64;

    public bool BrickPowerValid => (Flags & BrickValid) != 0;
    public bool ServoPowerValid => (Flags & ServoValid) != 0;
    public bool Overcurrent =>
        (Flags & PeriphOvercurrent) != 0 || (Flags & PeriphHipowerOvercurrent) != 0;
    public bool ConfigurationChanged => (Flags & Changed) != 0;
}

/// <summary>One ESC's telemetry, from ESC_TELEMETRY_* (msgs 291-293).</summary>
public readonly record struct EscReading(byte TemperatureC, ushort Rpm, ushort VoltageCv, ushort CurrentCa);

/// <summary>
/// ESC telemetry, present only when the hardware supports it (bidirectional DShot
/// or a serial ESC telemetry line). Absent on most setups, which the health rules
/// report as unmonitored rather than healthy.
/// </summary>
public sealed record EscTelemetryState(
    EscReading[] Escs,
    DateTime TimestampUtc
) : TimestampedState(TimestampUtc)
{
    public EscReading[] Active => Escs.Where(e => e.Rpm > 0 || e.TemperatureC > 0).ToArray();

    public byte MaxTemperatureC => Active.Length == 0 ? (byte)0 : Active.Max(e => e.TemperatureC);

    /// <summary>
    /// Spread in RPM across the ESCs. On a multirotor holding a hover this is the
    /// clearest sign of an underperforming motor or a heavy arm.
    /// </summary>
    public int RpmSpread
    {
        get
        {
            var active = Active;
            return active.Length < 2 ? 0 : active.Max(e => e.Rpm) - active.Min(e => e.Rpm);
        }
    }
}
