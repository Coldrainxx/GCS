using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GCS.Core.Advisor;

/// <summary>One parameter as the advisor sees it.</summary>
public sealed record ParameterInfo(
    string Name,
    float Value,
    string Units = "",
    string Description = "",
    float? Min = null,
    float? Max = null,
    bool OutOfRange = false);

/// <summary>
/// The vehicle's parameters, made available to the assistant.
///
/// An ArduPilot vehicle has well over a thousand parameters — far too many to put
/// in a prompt. So this exposes them in two bounded ways: a small curated set that
/// is always included, and lookup of any parameter the operator actually names.
/// </summary>
public sealed class ParameterSnapshot
{
    private readonly Dictionary<string, ParameterInfo> _byName;

    public ParameterSnapshot(IEnumerable<ParameterInfo>? parameters = null)
    {
        _byName = (parameters ?? Array.Empty<ParameterInfo>())
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _byName.Count;
    public bool IsEmpty => _byName.Count == 0;

    public IReadOnlyCollection<ParameterInfo> All => _byName.Values;

    public ParameterInfo? Find(string name) =>
        _byName.TryGetValue(name.Trim(), out var p) ? p : null;

    public IReadOnlyList<ParameterInfo> OutOfRange =>
        _byName.Values.Where(p => p.OutOfRange).OrderBy(p => p.Name).ToList();

    /// <summary>
    /// Parameters worth stating unprompted: they decide how the aircraft behaves
    /// and are the usual answer to "why is it doing that".
    /// </summary>
    private static readonly string[] KeyParameters =
    {
        // Airframe
        "FRAME_CLASS", "FRAME_TYPE", "Q_ENABLE", "Q_FRAME_CLASS", "Q_FRAME_TYPE",
        // Battery / power
        "BATT_MONITOR", "BATT_CAPACITY", "BATT_LOW_VOLT", "BATT_CRT_VOLT",
        "BATT_LOW_MAH", "BATT_FS_LOW_ACT", "BATT_FS_CRT_ACT",
        // Failsafe
        "FS_SHORT_ACTN", "FS_LONG_ACTN", "FS_GCS_ENABL", "THR_FAILSAFE",
        // Arming and safety
        "ARMING_CHECK", "ARMING_REQUIRE", "RTL_ALTITUDE", "FENCE_ENABLE",
        // Airspeed (plane)
        "ARSPD_TYPE", "ARSPD_USE", "AIRSPEED_CRUISE", "TRIM_ARSPD_CM",
        // Flight modes (all vehicles)
        "FLTMODE_CH", "FLTMODE1", "FLTMODE2", "FLTMODE3",
        "FLTMODE4", "FLTMODE5", "FLTMODE6",

        // Copter equivalents. Listed alongside rather than instead: only the ones
        // the vehicle actually loaded are ever shown, so both sets can coexist and
        // whichever airframe is connected gets a useful summary.
        "FS_THR_ENABLE", "FS_THR_VALUE", "FS_GCS_ENABLE", "FS_EKF_ACTION",
        "MOT_PWM_TYPE", "MOT_SPIN_ARM", "MOT_SPIN_MIN", "MOT_THST_HOVER",
        "ANGLE_MAX", "PILOT_SPEED_UP", "WPNAV_SPEED", "RTL_ALT", "LAND_SPEED",
    };

    /// <summary>Matches parameter-style tokens, e.g. BATT_CAPACITY or Q_ENABLE.</summary>
    private static readonly Regex ParamToken =
        new(@"\b[A-Z][A-Z0-9]*(_[A-Z0-9]+)+\b|\bQ_[A-Z0-9_]+\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parameters the question refers to by name. Matching is case-insensitive so
    /// "what is batt_capacity" works, and unknown names are simply not returned.
    /// </summary>
    public IReadOnlyList<ParameterInfo> Mentioned(string? question, int max = 12)
    {
        if (string.IsNullOrWhiteSpace(question) || IsEmpty)
            return Array.Empty<ParameterInfo>();

        var found = new List<ParameterInfo>();

        foreach (Match m in ParamToken.Matches(question.ToUpperInvariant()))
        {
            var hit = Find(m.Value);
            if (hit != null && !found.Any(f => f.Name == hit.Name)) found.Add(hit);
            if (found.Count >= max) break;
        }

        // A bare name with no underscore ("what is ARMING") still deserves a hit
        // if it prefixes exactly one known parameter group.
        if (found.Count == 0)
        {
            var words = question.ToUpperInvariant()
                .Split(new[] { ' ', ',', '.', '?', '!', ':', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4);

            foreach (var word in words)
            {
                var matches = _byName.Values
                    .Where(p => p.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    .Take(max)
                    .ToList();

                if (matches.Count is > 0 and <= 6)
                {
                    found.AddRange(matches.Where(m => !found.Any(f => f.Name == m.Name)));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The parameter section of the grounding: what is loaded, the key settings,
    /// anything out of range, and whatever the question named.
    /// </summary>
    public string BuildSection(string? question)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== VEHICLE PARAMETERS ===");

        if (IsEmpty)
        {
            sb.Append("No parameters have been read from the vehicle yet. " +
                      "The operator can load them from the PARAMS screen.");
            return sb.ToString();
        }

        sb.AppendLine($"{Count} parameters loaded.");
        sb.AppendLine();

        sb.AppendLine("Key settings:");
        bool anyKey = false;
        foreach (var name in KeyParameters)
        {
            var p = Find(name);
            if (p == null) continue;
            anyKey = true;
            sb.AppendLine("  " + Describe(p));
        }
        if (!anyKey) sb.AppendLine("  (none of the usual key parameters are loaded)");

        var outOfRange = OutOfRange;
        if (outOfRange.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Outside their expected range:");
            foreach (var p in outOfRange.Take(15)) sb.AppendLine("  " + Describe(p));
        }

        var mentioned = Mentioned(question);
        if (mentioned.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Parameters referred to in the question:");
            foreach (var p in mentioned) sb.AppendLine("  " + Describe(p, withDescription: true));
        }

        sb.AppendLine();
        sb.AppendLine("Only the parameters listed above are available to you. If asked about " +
                      "one that is not listed, say it was not loaded rather than guessing its value.");

        return sb.ToString().TrimEnd();
    }

    private static string Describe(ParameterInfo p, bool withDescription = false)
    {
        var sb = new StringBuilder();
        sb.Append(p.Name).Append(" = ").Append(p.Value.ToString("0.####", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(p.Units)) sb.Append(' ').Append(p.Units);

        if (p.Min.HasValue && p.Max.HasValue)
            sb.Append(CultureInfo.InvariantCulture, $" (range {p.Min:0.###}..{p.Max:0.###})");

        if (p.OutOfRange) sb.Append(" [OUT OF RANGE]");

        if (withDescription && !string.IsNullOrWhiteSpace(p.Description))
            sb.Append(" — ").Append(p.Description);

        return sb.ToString();
    }
}
