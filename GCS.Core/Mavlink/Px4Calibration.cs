using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Mavlink;

/// <summary>Where a PX4 calibration has got to.</summary>
public enum Px4CalibrationPhase
{
    Idle,
    Started,
    /// <summary>Waiting for the operator to move the vehicle to an unmeasured side.</summary>
    AwaitingSide,
    /// <summary>A side was recognised; the vehicle must be held still.</summary>
    Measuring,
    Done,
    Failed,
}

/// <summary>
/// Progress of a PX4 calibration, rebuilt from its STATUSTEXT stream.
///
/// PX4 does not use a command handshake for calibration the way ArduPilot does with
/// MAV_CMD_ACCELCAL_VEHICLE_POS. It sends "[cal] ..." status messages and detects
/// orientation itself, so the GCS listens and reports rather than driving. Parsing
/// is kept here, pure, because it is the part that can be tested without hardware.
/// </summary>
public sealed record Px4CalibrationState(
    Px4CalibrationPhase Phase,
    string Sensor = "",
    int ProgressPercent = 0,
    IReadOnlyList<string>? PendingSides = null,
    string? CurrentSide = null,
    string Message = "")
{
    public static readonly Px4CalibrationState Idle = new(Px4CalibrationPhase.Idle);

    public IReadOnlyList<string> Pending => PendingSides ?? Array.Empty<string>();

    public bool IsRunning => Phase is Px4CalibrationPhase.Started
                                   or Px4CalibrationPhase.AwaitingSide
                                   or Px4CalibrationPhase.Measuring;

    /// <summary>What the operator should be told to do right now.</summary>
    public string Instruction => Phase switch
    {
        Px4CalibrationPhase.Started => $"Starting {Sensor} calibration…",
        Px4CalibrationPhase.AwaitingSide when Pending.Count > 0 =>
            $"Rotate the vehicle to: {string.Join(", ", Pending)}",
        Px4CalibrationPhase.AwaitingSide => "Rotate the vehicle to the next position.",
        Px4CalibrationPhase.Measuring =>
            $"Hold still{(CurrentSide is null ? "" : $" ({CurrentSide})")} — {ProgressPercent}%",
        Px4CalibrationPhase.Done => $"{Sensor} calibration complete.",
        Px4CalibrationPhase.Failed => string.IsNullOrEmpty(Message) ? "Calibration failed." : Message,
        _ => "",
    };
}

/// <summary>
/// Folds PX4's "[cal]" STATUSTEXT messages into a calibration state.
/// </summary>
public static class Px4CalibrationParser
{
    /// <summary>True for the status messages that belong to a calibration.</summary>
    public static bool IsCalibrationMessage(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.TrimStart().StartsWith("[cal]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apply one status message. Anything unrecognised leaves the state untouched,
    /// so an unfamiliar firmware message cannot derail a calibration in progress.
    /// </summary>
    public static Px4CalibrationState Apply(Px4CalibrationState current, string? text)
    {
        if (!IsCalibrationMessage(text)) return current;

        string body = text!.TrimStart()[5..].Trim();          // drop "[cal]"
        string lower = body.ToLowerInvariant();

        if (lower.StartsWith("calibration started"))
        {
            return new Px4CalibrationState(
                Px4CalibrationPhase.Started, Sensor: SensorFrom(body), Message: body);
        }

        if (lower.StartsWith("calibration done"))
        {
            return current with
            {
                Phase = Px4CalibrationPhase.Done,
                ProgressPercent = 100,
                Sensor = SensorFrom(body) is { Length: > 0 } s ? s : current.Sensor,
                PendingSides = Array.Empty<string>(),
                Message = body,
            };
        }

        if (lower.StartsWith("calibration failed") || lower.Contains("aborted"))
            return current with { Phase = Px4CalibrationPhase.Failed, Message = body };

        // "pending: down front left right up back"
        if (lower.StartsWith("pending:"))
        {
            var sides = body[(body.IndexOf(':') + 1)..]
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            return current with
            {
                Phase = Px4CalibrationPhase.AwaitingSide,
                PendingSides = sides,
                CurrentSide = null,
                Message = body,
            };
        }

        // "up orientation detected" / "detected rest position, hold still..."
        if (lower.Contains("orientation detected") || lower.Contains("rest position"))
        {
            return current with
            {
                Phase = Px4CalibrationPhase.Measuring,
                CurrentSide = FirstWordOrNull(body),
                Message = body,
            };
        }

        // "progress <0-100>"
        if (lower.StartsWith("progress"))
        {
            var digits = new string(body.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int pct)
                ? current with { ProgressPercent = Math.Clamp(pct, 0, 100), Message = body }
                : current;
        }

        // "up side done, rotate to a pending side"
        if (lower.Contains("side done"))
        {
            return current with
            {
                Phase = Px4CalibrationPhase.AwaitingSide,
                CurrentSide = null,
                Message = body,
            };
        }

        // Anything else is informational — keep it visible without changing phase.
        return current with { Message = body };
    }

    /// <summary>"calibration started: 2 accel" -> "accel".</summary>
    private static string SensorFrom(string body)
    {
        int colon = body.IndexOf(':');
        if (colon < 0) return "";

        var parts = body[(colon + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // The numeric progress-id, when present, comes before the sensor name.
        return parts.LastOrDefault(p => !p.All(char.IsDigit)) ?? "";
    }

    private static string? FirstWordOrNull(string body)
    {
        var word = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(word) ? null : word;
    }
}

/// <summary>
/// The MAV_CMD_PREFLIGHT_CALIBRATION parameter sets PX4 expects. Each calibration is
/// one command; PX4 then runs the whole procedure itself.
/// </summary>
public static class Px4CalibrationCommands
{
    public const ushort PreflightCalibration = 241;

    // param order: gyro, mag, ground pressure, radio, accel, airspeed/level, esc
    public static (float P1, float P2, float P3, float P4, float P5, float P6, float P7) Gyro
        => (1, 0, 0, 0, 0, 0, 0);

    public static (float P1, float P2, float P3, float P4, float P5, float P6, float P7) Magnetometer
        => (0, 1, 0, 0, 0, 0, 0);

    public static (float P1, float P2, float P3, float P4, float P5, float P6, float P7) Accelerometer
        => (0, 0, 0, 0, 1, 0, 0);

    /// <summary>Level horizon: accel param set to 2 rather than 1.</summary>
    public static (float P1, float P2, float P3, float P4, float P5, float P6, float P7) LevelHorizon
        => (0, 0, 0, 0, 2, 0, 0);

    /// <summary>All zeroes cancels whatever is running.</summary>
    public static (float P1, float P2, float P3, float P4, float P5, float P6, float P7) Cancel
        => (0, 0, 0, 0, 0, 0, 0);
}
