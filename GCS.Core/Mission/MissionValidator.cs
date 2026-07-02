using System.Collections.Generic;
using GCS.Core.Domain;

namespace GCS.Core.Mission;

/// <summary>
/// Sanity checks run before uploading a mission. These are advisory warnings
/// (the user can still upload) - not hard errors.
/// </summary>
public static class MissionValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<MissionItem> items)
    {
        var warnings = new List<string>();

        if (items == null || items.Count == 0)
        {
            warnings.Add("Mission is empty.");
            return warnings;
        }

        var last = items[items.Count - 1];
        if (last.Command != MavCmd.Land && last.Command != MavCmd.ReturnToLaunch)
            warnings.Add("Mission does not end with LAND or RTL.");

        bool hasTakeoff = false;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Command == MavCmd.Takeoff) hasTakeoff = true;

            // Altitude sanity for altitude-carrying, relative/terrain-framed items
            // (home at index 0 and RTL/LAND legitimately carry 0).
            if (i != 0 && it.Frame != 0 && CarriesAltitude(it.Command) && it.AltitudeMeters <= 0)
                warnings.Add($"Item {i} ({Name(it.Command)}) has altitude ≤ 0.");
        }

        // A takeoff, if present, should be the first commanded item after home.
        if (hasTakeoff && items.Count > 1 && items[1].Command != MavCmd.Takeoff)
            warnings.Add("TAKEOFF is not the first item after home.");

        return warnings;
    }

    private static bool CarriesAltitude(ushort cmd) =>
        cmd == MavCmd.Waypoint || cmd == MavCmd.Loiter ||
        cmd == MavCmd.LoiterTurns || cmd == MavCmd.LoiterTime ||
        cmd == MavCmd.Takeoff;

    private static string Name(ushort cmd) => cmd switch
    {
        MavCmd.Waypoint => "WAYPOINT",
        MavCmd.Takeoff => "TAKEOFF",
        MavCmd.Land => "LAND",
        MavCmd.Loiter => "LOITER",
        MavCmd.LoiterTurns => "LOITER_TURNS",
        MavCmd.LoiterTime => "LOITER_TIME",
        MavCmd.ReturnToLaunch => "RTL",
        _ => $"CMD {cmd}"
    };
}
