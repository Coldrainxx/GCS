using System;
using System.Collections.Generic;
using System.Text;

namespace GCS.Core.Advisor;

/// <summary>
/// What the SETUP screens know: which calibrations have been done, how the flight
/// modes are assigned, and the state of the pre-arm checks.
///
/// Most of this is derived from parameters rather than separate telemetry, so the
/// snapshot is assembled by the app layer and handed over rather than read here.
/// </summary>
public sealed class SetupSnapshot
{
    /// <summary>Pre-arm checks and their results, as the PREARM screen shows them.</summary>
    public IReadOnlyList<(string Name, string Status, string? Reason)> PreflightChecks { get; init; }
        = Array.Empty<(string, string, string?)>();

    /// <summary>Flight mode assigned to each switch position, 1-6.</summary>
    public IReadOnlyList<(int Position, string Mode)> FlightModes { get; init; }
        = Array.Empty<(int, string)>();

    /// <summary>Servo output functions that have been assigned, e.g. "9: Motor 1".</summary>
    public IReadOnlyList<string> ServoFunctions { get; init; } = Array.Empty<string>();

    public string? FrameDescription { get; init; }
    public string? FirmwareVersion { get; init; }

    public bool IsEmpty =>
        PreflightChecks.Count == 0 && FlightModes.Count == 0 &&
        ServoFunctions.Count == 0 && FrameDescription is null;

    public string BuildSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SETUP / CONFIGURATION ===");

        if (IsEmpty)
        {
            sb.Append("No setup information has been read yet. The operator can open " +
                      "the SETUP screens to load it.");
            return sb.ToString();
        }

        if (!string.IsNullOrWhiteSpace(FrameDescription))
            sb.AppendLine($"Airframe: {FrameDescription}");

        if (!string.IsNullOrWhiteSpace(FirmwareVersion))
            sb.AppendLine($"Firmware: {FirmwareVersion}");

        if (PreflightChecks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pre-arm checks:");
            foreach (var (name, status, reason) in PreflightChecks)
            {
                sb.Append("  ").Append(name).Append(": ").Append(status);
                if (!string.IsNullOrWhiteSpace(reason)) sb.Append(" — ").Append(reason);
                sb.AppendLine();
            }
        }

        if (FlightModes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Flight mode switch:");
            foreach (var (position, mode) in FlightModes)
                sb.AppendLine($"  Position {position}: {mode}");
        }

        if (ServoFunctions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Servo output assignments:");
            foreach (var fn in ServoFunctions) sb.AppendLine("  " + fn);
        }

        return sb.ToString().TrimEnd();
    }
}
