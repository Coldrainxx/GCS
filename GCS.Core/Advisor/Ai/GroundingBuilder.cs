using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GCS.Core.Domain;

namespace GCS.Core.Advisor.Ai;

/// <summary>
/// Turns the current assessment into the text the model is allowed to reason over.
///
/// This is the whole safety story for the LLM path. The model gets no tools and no
/// live access — only this snapshot — so it can only describe telemetry that
/// genuinely arrived. Subsystems with no data are listed explicitly as unmeasured
/// rather than omitted, because an absent line invites the model to fill the gap.
/// </summary>
public static class GroundingBuilder
{
    /// <summary>
    /// Instructions that constrain the model. Read-only by construction: there is
    /// no command surface exposed, so the worst case is a wrong sentence, not a
    /// wrong command to the aircraft.
    /// </summary>
    public const string SystemPrompt = """
        You are a flight advisor built into a ground control station for an
        ArduPilot QuadPlane VTOL UAV. You help the operator understand the
        aircraft's current state.

        Rules you must follow:
        - Use ONLY the telemetry snapshot provided in the user message. It is the
          complete set of facts available to you.
        - Never invent or estimate a reading. If a value is marked NOT MEASURED,
          say plainly that it is not being monitored.
        - Never state or imply that the aircraft is safe to fly, airworthy, or
          cleared for flight. You see only part of the aircraft. Report what was
          observed and what was not; the decision belongs to the operator.
        - You cannot command the aircraft, change parameters, or take any action.
          If asked to do something, say so and describe where in the GCS the
          operator can do it.
        - Be concise and factual. Two or three short sentences unless asked for
          detail. Use the units given.
        - If the snapshot does not contain what was asked about, say so directly
          instead of guessing.
        - Parameter values, when listed, are the vehicle's current settings. Never
          invent a parameter value or a range. If a parameter is not in the list,
          say it has not been loaded rather than recalling a default.
        - You may explain what a parameter does and suggest what to change, but you
          cannot change it yourself — tell the operator where in the GCS to do it.
        """;

    /// <summary>
    /// A compact, human-readable snapshot. Plain text rather than JSON: small models
    /// follow it more reliably, and it stays legible when logged.
    /// </summary>
    public static string BuildSnapshot(
        FlightHealthReport report, VehicleState state, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        sb.AppendLine("=== TELEMETRY SNAPSHOT ===");
        sb.AppendLine($"Time (UTC): {nowUtc.ToString("HH:mm:ss", ci)}");
        sb.AppendLine($"Assessment: {report.Headline}");
        sb.AppendLine($"Verdict: {report.Verdict}");
        sb.AppendLine(report.OverallScore is int score
            ? $"Overall score: {score}% (measured subsystems only)"
            : "Overall score: unknown (nothing could be measured)");
        sb.AppendLine($"Coverage: {report.Measured.Count()} of {report.Components.Count} subsystems measured");
        sb.AppendLine();

        sb.AppendLine("--- MEASURED SUBSYSTEMS ---");
        var measured = report.Measured.ToList();
        if (measured.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var c in measured)
            {
                sb.AppendLine($"{c.Name}: {c.Status} · {c.Summary} · score {c.Score}%");
                foreach (var e in c.Evidence)
                    sb.AppendLine($"    - {e.Severity}: {e.Text}");
            }
        }
        sb.AppendLine();

        // Named explicitly: a missing line reads as "nothing to say", which is
        // exactly the gap a model will paper over.
        sb.AppendLine("--- NOT MEASURED (no telemetry; do not guess these) ---");
        var unmeasured = report.Unmeasured.ToList();
        if (unmeasured.Count == 0)
            sb.AppendLine("(none)");
        else
            foreach (var c in unmeasured)
                sb.AppendLine($"{c.Name}: NOT MEASURED — {c.Summary}");
        sb.AppendLine();

        sb.AppendLine("--- VEHICLE ---");
        sb.AppendLine($"Flight mode: {state.FlightMode?.ToString() ?? "unknown"}");
        sb.AppendLine($"Armed: {(state.IsArmed ? "yes" : "no")}");

        if (state.Position is { } p)
        {
            sb.AppendLine($"Position: {p.LatitudeDeg.ToString("F6", ci)}, {p.LongitudeDeg.ToString("F6", ci)}");
            sb.AppendLine($"Altitude (relative): {p.AltitudeRelMeters.ToString("F1", ci)} m");
            sb.AppendLine($"Heading: {p.HeadingDeg.ToString("F0", ci)} deg");
        }
        else
        {
            sb.AppendLine("Position: NOT MEASURED");
        }

        if (state.VfrHud is { } v)
        {
            sb.AppendLine($"Airspeed: {v.AirspeedMps.ToString("F1", ci)} m/s");
            sb.AppendLine($"Groundspeed: {v.GroundspeedMps.ToString("F1", ci)} m/s");
            sb.AppendLine($"Climb rate: {v.ClimbMps.ToString("F1", ci)} m/s");
        }

        if (state.Attitude is { } a)
        {
            sb.AppendLine($"Roll: {(a.RollRad * 180.0 / Math.PI).ToString("F0", ci)} deg");
            sb.AppendLine($"Pitch: {(a.PitchRad * 180.0 / Math.PI).ToString("F0", ci)} deg");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The full user-side message: the operator's question plus its context.</summary>
    public static string BuildUserMessage(
        string question, FlightHealthReport report, VehicleState state, DateTime nowUtc,
        Logging.FlightLogSummary? log = null,
        ParameterSnapshot? parameters = null,
        SetupSnapshot? setup = null,
        SwarmSnapshot? swarm = null)
    {
        var sb = new StringBuilder();

        // The fleet comes first when there is one: the telemetry section below
        // describes a single aircraft, and without this the model would describe
        // the whole flight as if that were all there is.
        if (swarm is { Count: > 0 })
        {
            sb.AppendLine(swarm.BuildSection());
            sb.AppendLine();
        }

        sb.AppendLine(BuildSnapshot(report, state, nowUtc));

        // Parameters and setup answer "why is it configured like this", which is a
        // different question from "what is it doing", and the two are often asked
        // together — "battery is low, what is BATT_LOW_VOLT set to?"
        if (parameters != null)
        {
            sb.AppendLine();
            sb.AppendLine(parameters.BuildSection(question));
        }

        if (setup != null)
        {
            sb.AppendLine();
            sb.AppendLine(setup.BuildSection());
        }

        if (log != null)
        {
            sb.AppendLine();
            sb.AppendLine(BuildLogSnapshot(log));
        }

        sb.AppendLine();
        sb.AppendLine("=== OPERATOR QUESTION ===");
        sb.Append(question);
        return sb.ToString();
    }

    /// <summary>
    /// A recorded flight, for questions about what happened rather than what is
    /// happening. Same discipline as the live snapshot: figures that were never
    /// recorded are named as such rather than omitted.
    /// </summary>
    public static string BuildLogSnapshot(Logging.FlightLogSummary log)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        sb.AppendLine("=== RECORDED FLIGHT LOG ===");
        sb.AppendLine($"File: {log.FileName}");
        sb.AppendLine($"Started (UTC): {log.StartUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Duration: {log.DurationText}");
        sb.AppendLine($"Packets: {log.PacketCount}");
        sb.AppendLine($"Vehicles: {string.Join(", ", log.SystemIds)}");
        sb.AppendLine(log.ArmCount == 0
            ? "Never armed — this recording contains no flight."
            : $"Armed for {log.ArmedDuration:hh\\:mm\\:ss} across {log.ArmCount} arm(s)");

        sb.AppendLine($"Distance flown: {log.DistanceText}");
        sb.AppendLine($"Max altitude (relative): {log.MaxAltitudeRelM.ToString("F1", ci)} m");
        sb.AppendLine($"Max groundspeed: {log.MaxGroundspeedMps.ToString("F1", ci)} m/s");
        sb.AppendLine($"Max airspeed: {log.MaxAirspeedMps.ToString("F1", ci)} m/s");

        sb.AppendLine(log.HasBattery
            ? $"Battery: {log.BatteryStartVolts.ToString("F2", ci)} V to " +
              $"{log.BatteryEndVolts.ToString("F2", ci)} V (lowest {log.BatteryMinVolts.ToString("F2", ci)} V)"
            : "Battery: NOT RECORDED");

        sb.AppendLine(log.HasGps
            ? $"GPS: worst fix type {log.WorstGpsFix}, fewest satellites {log.MinSatellites}"
            : "GPS: NOT RECORDED");

        sb.AppendLine();
        sb.AppendLine("Events:");
        if (log.Events.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            // Bounded: a long flight can log hundreds and they would crowd out the
            // rest of the context.
            foreach (var e in log.Events.Take(40))
                sb.AppendLine($"  {e.TimestampUtc:HH:mm:ss} [{e.Kind}] {e.Text}");

            if (log.Events.Count > 40)
                sb.AppendLine($"  ... {log.Events.Count - 40} more not shown");
        }

        sb.AppendLine();
        sb.AppendLine("Health findings during the flight:");
        if (log.Findings.Count == 0)
            sb.AppendLine("  (none in what was recorded)");
        else
            foreach (var f in log.Findings.Take(20)) sb.AppendLine($"  - {f}");

        sb.AppendLine();
        sb.AppendLine("Limitations of this log:");
        foreach (var note in log.Notes) sb.AppendLine($"  - {note}");

        return sb.ToString().TrimEnd();
    }
}
