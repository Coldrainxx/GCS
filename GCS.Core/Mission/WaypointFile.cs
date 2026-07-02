using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GCS.Core.Domain;

namespace GCS.Core.Mission;

/// <summary>
/// Reads and writes the QGC WPL 110 ".waypoints" format (tab-separated, the
/// same format Mission Planner and QGroundControl use).
/// Columns: seq, current, frame, command, p1, p2, p3, p4, x(lat), y(lon), z(alt), autocontinue.
/// </summary>
public static class WaypointFile
{
    private const string Header = "QGC WPL 110";

    public static string Serialize(IReadOnlyList<MissionItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var wp in items)
        {
            int current = wp.Sequence == 0 ? 1 : 0;
            int autoContinue = wp.AutoContinue ? 1 : 0;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0}\t{1}\t{2}\t{3}\t{4:F6}\t{5:F6}\t{6:F6}\t{7:F6}\t{8:F8}\t{9:F8}\t{10:F6}\t{11}",
                wp.Sequence, current, wp.Frame, wp.Command,
                wp.Param1, wp.Param2, wp.Param3, wp.Param4,
                wp.LatitudeDeg, wp.LongitudeDeg, wp.AltitudeMeters, autoContinue));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a .waypoints file. Malformed lines are skipped rather than aborting
    /// the whole import. Throws <see cref="FormatException"/> only if the header is missing.
    /// </summary>
    public static IReadOnlyList<MissionItem> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("QGC WPL", StringComparison.Ordinal))
            throw new FormatException("Not a QGC WPL waypoints file (missing header).");

        var items = new List<MissionItem>();
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var p = line.Split('\t');
            if (p.Length < 12) continue;

            var inv = CultureInfo.InvariantCulture;
            if (!int.TryParse(p[0], NumberStyles.Integer, inv, out int seq)) continue;
            if (!byte.TryParse(p[2], NumberStyles.Integer, inv, out byte frame)) continue;
            if (!ushort.TryParse(p[3], NumberStyles.Integer, inv, out ushort command)) continue;
            if (!float.TryParse(p[4], NumberStyles.Float, inv, out float p1)) continue;
            if (!float.TryParse(p[5], NumberStyles.Float, inv, out float p2)) continue;
            if (!float.TryParse(p[6], NumberStyles.Float, inv, out float p3)) continue;
            if (!float.TryParse(p[7], NumberStyles.Float, inv, out float p4)) continue;
            if (!double.TryParse(p[8], NumberStyles.Float, inv, out double lat)) continue;
            if (!double.TryParse(p[9], NumberStyles.Float, inv, out double lon)) continue;
            if (!float.TryParse(p[10], NumberStyles.Float, inv, out float alt)) continue;
            bool autoContinue = !(p[11].Trim() == "0");

            items.Add(new MissionItem(seq, command, lat, lon, alt, p1, p2, p3, p4, frame, autoContinue));
        }
        return items;
    }
}
