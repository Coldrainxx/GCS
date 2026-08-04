using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Advisor;

/// <summary>
/// How a single subsystem is doing. <see cref="NoData"/> is deliberately distinct
/// from a bad score: a component the GCS cannot observe must never be scored, or
/// it drags the fleet-wide numbers down and trains the operator to ignore them.
/// </summary>
public enum ComponentStatus
{
    /// <summary>No telemetry for this component — it is not being judged at all.</summary>
    NoData = 0,
    Ok = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>A single observation backing a component's status.</summary>
public sealed record HealthEvidence(ComponentStatus Severity, string Text);

/// <summary>
/// What the advisor observed. Deliberately not a clearance to fly: the GCS sees a
/// fraction of the aircraft, so it can report problems it found and gaps it has,
/// but it cannot certify airworthiness — that stays with the operator.
/// </summary>
public enum AdvisoryVerdict
{
    /// <summary>Nothing could be measured at all.</summary>
    NoData,
    /// <summary>No problems found, but one or more flight-critical subsystems are unmonitored.</summary>
    LimitedData,
    /// <summary>No problems found, and every flight-critical subsystem was checked.</summary>
    NoIssues,
    Issues,
    CriticalIssue,
}

/// <summary>
/// One subsystem's assessment. <see cref="Score"/> is null exactly when
/// <see cref="Status"/> is <see cref="ComponentStatus.NoData"/>, so callers cannot
/// accidentally average an unmeasured component in as a zero.
/// </summary>
public sealed record ComponentHealth(
    string Name,
    ComponentStatus Status,
    int? Score,
    string Summary,
    IReadOnlyList<HealthEvidence> Evidence,
    bool IsVital = true)
{
    public bool IsMeasured => Status != ComponentStatus.NoData;

    public static ComponentHealth Unmonitored(string name, string reason, bool isVital = true) =>
        new(name, ComponentStatus.NoData, null, reason, Array.Empty<HealthEvidence>(), isVital);
}

/// <summary>
/// The whole-aircraft picture at one instant.
/// </summary>
public sealed record FlightHealthReport(
    IReadOnlyList<ComponentHealth> Components,
    DateTime EvaluatedAtUtc)
{
    /// <summary>Components the GCS could actually judge.</summary>
    public IEnumerable<ComponentHealth> Measured => Components.Where(c => c.IsMeasured);

    /// <summary>Components with no telemetry — shown so the gaps are visible.</summary>
    public IEnumerable<ComponentHealth> Unmeasured => Components.Where(c => !c.IsMeasured);

    /// <summary>
    /// Mean score across measured components only, or null when nothing could be
    /// measured. Null means "unknown" — never render it as 0%.
    /// </summary>
    public int? OverallScore
    {
        get
        {
            var scores = Measured.Select(c => c.Score!.Value).ToList();
            return scores.Count == 0 ? null : (int)Math.Round(scores.Average());
        }
    }

    /// <summary>Worst status among measured components.</summary>
    public ComponentStatus WorstStatus =>
        Measured.Any() ? Measured.Max(c => c.Status) : ComponentStatus.NoData;

    /// <summary>Share of listed components that carry telemetry, 0-100.</summary>
    public int CoveragePercent =>
        Components.Count == 0 ? 0 : (int)Math.Round(100.0 * Measured.Count() / Components.Count);

    /// <summary>Flight-critical subsystems with no telemetry behind them.</summary>
    public IReadOnlyList<ComponentHealth> UnmonitoredVital =>
        Components.Where(c => !c.IsMeasured && c.IsVital).ToList();

    /// <summary>
    /// What was observed. Never asserts the aircraft is safe — a clean result with
    /// an unmonitored battery is <see cref="AdvisoryVerdict.LimitedData"/>, not a
    /// pass, because the advisor has no way to know what it did not measure.
    /// </summary>
    public AdvisoryVerdict Verdict
    {
        get
        {
            if (!Measured.Any()) return AdvisoryVerdict.NoData;

            if (WorstStatus == ComponentStatus.Critical) return AdvisoryVerdict.CriticalIssue;
            if (WorstStatus == ComponentStatus.Warning) return AdvisoryVerdict.Issues;

            return UnmonitoredVital.Count > 0
                ? AdvisoryVerdict.LimitedData
                : AdvisoryVerdict.NoIssues;
        }
    }

    /// <summary>Every evidence line, worst first, for display.</summary>
    public IReadOnlyList<HealthEvidence> Findings =>
        Components.SelectMany(c => c.Evidence)
                  .OrderByDescending(e => e.Severity)
                  .ToList();

    public string Headline => Verdict switch
    {
        AdvisoryVerdict.NoData => "No telemetry — nothing could be checked",
        AdvisoryVerdict.CriticalIssue => "Critical issue detected",
        AdvisoryVerdict.Issues => "Issues detected — review before flying",
        AdvisoryVerdict.LimitedData =>
            $"No issues in what was checked · {Describe(UnmonitoredVital)} not monitored",
        _ => "No issues detected in all checked subsystems",
    };

    private static string Describe(IReadOnlyList<ComponentHealth> items) =>
        string.Join(", ", items.Select(c => c.Name));
}
