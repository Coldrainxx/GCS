using System;
using System.Collections.Generic;
using System.Linq;
using GCS.Core.Domain;

namespace GCS.Core.Advisor;

/// <summary>
/// Answers operator questions from the current health report and vehicle state.
///
/// Every answer is derived from telemetry that actually arrived. When the data is
/// missing the reply says so rather than filling the gap — an assistant that
/// invents a battery reading is worse than one that admits it has none.
/// </summary>
public static class AssistantResponder
{
    public static string Respond(
        AssistantIntent intent,
        FlightHealthReport report,
        VehicleState state) => intent switch
    {
        AssistantIntent.Greeting => Greeting(report),
        AssistantIntent.Help => Help(),
        AssistantIntent.HealthReport => HealthSummary(report),
        AssistantIntent.Battery => ComponentAnswer(report, "Battery"),
        AssistantIntent.Gps => ComponentAnswer(report, "GPS"),
        AssistantIntent.Link => ComponentAnswer(report, "Link"),
        AssistantIntent.Position => PositionAnswer(state),
        AssistantIntent.FlightModeStatus => ModeAnswer(state),
        AssistantIntent.Coverage => CoverageAnswer(report),
        _ => Unknown(),
    };

    /// <summary>
    /// The no-model answers when a recorded flight is under review. Same rule as
    /// live: anything the log does not contain is reported as absent.
    /// </summary>
    public static string RespondAboutLog(AssistantIntent intent, Logging.FlightLogSummary log) =>
        intent switch
        {
            AssistantIntent.Battery => log.HasBattery
                ? $"Battery went from {log.BatteryStartVolts:F2} V to {log.BatteryEndVolts:F2} V, " +
                  $"lowest {log.BatteryMinVolts:F2} V."
                : "No battery telemetry was recorded in this log.",

            AssistantIntent.Gps => log.HasGps
                ? $"GPS: worst fix type {log.WorstGpsFix}, fewest satellites {log.MinSatellites}."
                : "No GPS telemetry was recorded in this log.",

            AssistantIntent.Position => log.HasPosition
                ? $"The aircraft covered {log.DistanceText} while armed, " +
                  $"reaching {log.MaxAltitudeRelM:F1} m above home."
                : "No position was recorded in this log.",

            AssistantIntent.FlightModeStatus => DescribeModes(log),
            AssistantIntent.Coverage => string.Join(Environment.NewLine, log.Notes),
            AssistantIntent.Help =>
                "Ask about this flight: duration, battery, GPS, altitude, distance, " +
                "what happened, or what the log does not contain.",

            _ => LogOverview(log),
        };

    private static string LogOverview(Logging.FlightLogSummary log)
    {
        var lines = new List<string>
        {
            $"{log.FileName}: {log.DurationText}, {log.PacketCount:N0} packets.",
            log.ArmCount == 0
                ? "The aircraft was never armed, so this recording contains no flight."
                : $"Armed for {log.ArmedDuration:hh\\:mm\\:ss} across {log.ArmCount} arm(s), " +
                  $"covering {log.DistanceText} and reaching {log.MaxAltitudeRelM:F1} m.",
        };

        if (log.Findings.Count == 0)
            lines.Add("No problems were detected in what was recorded.");
        else
            lines.AddRange(log.Findings.Take(5).Select(f => "• " + f));

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeModes(Logging.FlightLogSummary log)
    {
        var modes = log.Events.Where(e => e.Kind == "Mode").Take(10).ToList();
        return modes.Count == 0
            ? "No mode changes were recorded."
            : string.Join(Environment.NewLine,
                modes.Select(m => $"{m.TimestampUtc:HH:mm:ss} — {m.Text}"));
    }

    private static string Greeting(FlightHealthReport report) =>
        report.Verdict == AdvisoryVerdict.NoData
            ? "Hello. No telemetry yet — connect to the aircraft and I can report on it."
            : $"Hello. {report.Headline}.";

    private static string Help() =>
        "Ask me about: overall health, battery, GPS, link, position, flight mode, " +
        "or what is not being monitored.";

    private static string Unknown() =>
        "I did not understand that. Try \"health\", \"battery\", \"GPS\", \"link\", " +
        "\"position\", \"mode\", or \"what is not monitored\".";

    private static string HealthSummary(FlightHealthReport report)
    {
        if (report.Verdict == AdvisoryVerdict.NoData)
            return "No telemetry has arrived, so I cannot assess anything yet.";

        var lines = new List<string> { report.Headline + "." };

        if (report.OverallScore is int score)
            lines.Add($"Overall {score}% across {report.Measured.Count()} measured subsystems.");

        var problems = report.Measured
            .Where(c => c.Status >= ComponentStatus.Warning)
            .OrderByDescending(c => c.Status)
            .ToList();

        if (problems.Count == 0)
        {
            lines.Add("Nothing I can measure is out of limits.");
        }
        else
        {
            foreach (var c in problems)
            {
                string detail = c.Evidence.Count > 0
                    ? string.Join("; ", c.Evidence.Select(e => e.Text))
                    : c.Summary;
                lines.Add($"{c.Name}: {detail}.");
            }
        }

        if (report.UnmonitoredVital.Count > 0)
        {
            lines.Add("Not measured: " +
                      string.Join(", ", report.UnmonitoredVital.Select(c => c.Name)) +
                      " — so this is not a complete picture.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ComponentAnswer(FlightHealthReport report, string name)
    {
        var component = report.Components.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        if (component is null)
            return $"I have no {name} information.";

        if (!component.IsMeasured)
            return $"{name}: not monitored — {component.Summary}.";

        var lines = new List<string> { $"{name}: {component.Summary} ({component.Score}%)." };
        lines.AddRange(component.Evidence.Select(e => e.Text + "."));
        return string.Join(Environment.NewLine, lines);
    }

    private static string PositionAnswer(VehicleState state)
    {
        if (state.Position is null)
            return "No position telemetry has arrived.";

        var p = state.Position;
        return $"Position {p.LatitudeDeg:F6}, {p.LongitudeDeg:F6} · " +
               $"{p.AltitudeRelMeters:F0} m above home · heading {p.HeadingDeg:F0}°.";
    }

    private static string ModeAnswer(VehicleState state)
    {
        string mode = state.FlightMode?.ToString() ?? "unknown";
        string armed = state.IsArmed ? "ARMED" : "disarmed";
        return $"Flight mode {mode}, {armed}.";
    }

    private static string CoverageAnswer(FlightHealthReport report)
    {
        var measured = report.Measured.Select(c => c.Name).ToList();
        var unmeasured = report.Unmeasured.ToList();

        var lines = new List<string>();

        lines.Add(measured.Count == 0
            ? "I am not measuring anything right now."
            : "Measuring: " + string.Join(", ", measured) + ".");

        if (unmeasured.Count > 0)
        {
            lines.Add("Not measuring:");
            lines.AddRange(unmeasured.Select(c => $"  {c.Name} — {c.Summary}"));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
