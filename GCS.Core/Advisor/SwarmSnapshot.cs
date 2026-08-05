using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GCS.Core.Advisor;

/// <summary>One aircraft in the fleet, as the advisor sees it.</summary>
public sealed record SwarmVehicleInfo(
    byte SystemId,
    string Name,
    bool IsLeader,
    bool IsActive,
    string FlightMode,
    bool IsArmed,
    int BatteryPercent,
    float Voltage,
    string GpsFix,
    int Satellites,
    double AltitudeRelM,
    string Alert = "",
    string Station = "");

/// <summary>
/// The whole fleet.
///
/// The rest of the grounding describes one vehicle — whichever is active — because
/// that is what the HUD and health rules follow. Without this section the advisor
/// answered "there is only one aircraft" while three were connected, which is worse
/// than not answering: it is a confident description of a fleet it could not see.
/// </summary>
public sealed class SwarmSnapshot
{
    public IReadOnlyList<SwarmVehicleInfo> Vehicles { get; init; } = Array.Empty<SwarmVehicleInfo>();

    /// <summary>Formation currently selected in the swarm panel, if any.</summary>
    public string? FormationName { get; init; }
    public double SpacingM { get; init; }

    /// <summary>Fleet-level health line shown on the swarm panel.</summary>
    public string? FleetHealth { get; init; }

    public int Count => Vehicles.Count;
    public bool IsSwarm => Count > 1;

    public SwarmVehicleInfo? Leader => Vehicles.FirstOrDefault(v => v.IsLeader);
    public SwarmVehicleInfo? Active => Vehicles.FirstOrDefault(v => v.IsActive);

    public string BuildSection()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        sb.AppendLine("=== FLEET ===");

        if (Count == 0)
        {
            sb.Append("No vehicles are connected.");
            return sb.ToString();
        }

        sb.AppendLine($"{Count} aircraft connected" + (IsSwarm ? " (swarm mode)." : "."));

        if (IsSwarm)
        {
            sb.AppendLine(Leader is null
                ? "No leader has been assigned."
                : $"Leader: {Leader.Name}.");

            if (!string.IsNullOrWhiteSpace(FormationName))
                sb.AppendLine($"Formation: {FormationName}, {SpacingM.ToString("0.#", ci)} m spacing.");

            if (!string.IsNullOrWhiteSpace(FleetHealth))
                sb.AppendLine($"Fleet health: {FleetHealth}");
        }

        sb.AppendLine();
        sb.AppendLine("Each aircraft:");

        foreach (var v in Vehicles.OrderBy(v => v.SystemId))
        {
            sb.Append("  ").Append(v.Name);

            var roles = new List<string>();
            if (v.IsLeader) roles.Add("leader");
            if (v.IsActive) roles.Add("shown in the main display");
            if (roles.Count > 0) sb.Append(" (").Append(string.Join(", ", roles)).Append(')');

            sb.Append(": ").Append(v.FlightMode);
            sb.Append(v.IsArmed ? ", ARMED" : ", disarmed");

            // Zero percent before the autopilot reports means unknown, not empty.
            if (v.Voltage > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", {v.Voltage:F1} V");
                if (v.BatteryPercent > 0) sb.Append(CultureInfo.InvariantCulture, $" ({v.BatteryPercent}%)");
            }
            else
            {
                sb.Append(", battery not monitored");
            }

            sb.Append(", ").Append(v.GpsFix);
            if (v.Satellites > 0) sb.Append(CultureInfo.InvariantCulture, $" {v.Satellites} sats");

            sb.Append(CultureInfo.InvariantCulture, $", {v.AltitudeRelM:F0} m");

            if (!string.IsNullOrWhiteSpace(v.Station)) sb.Append(" · station ").Append(v.Station);
            if (!string.IsNullOrWhiteSpace(v.Alert)) sb.Append(" · ALERT: ").Append(v.Alert);

            sb.AppendLine();
        }

        if (IsSwarm)
        {
            sb.AppendLine();
            sb.AppendLine("The telemetry snapshot above describes only the aircraft marked " +
                          "\"shown in the main display\". Use this fleet list when asked about " +
                          "how many aircraft there are, or about any vehicle other than that one.");
        }

        return sb.ToString().TrimEnd();
    }
}
