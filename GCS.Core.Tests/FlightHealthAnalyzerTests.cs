using GCS.Core.Advisor;
using GCS.Core.Domain;
using Xunit;

namespace GCS.Core.Tests;

public class FlightHealthAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static VehicleState Healthy(
        float volts = 24.8f,          // 6S at ~4.13 V/cell
        int remaining = 95,
        byte fixType = 3,
        byte sats = 14,
        ushort eph = 80,              // HDOP 0.8
        bool armed = false,
        float rollRad = 0,
        float pitchRad = 0,
        bool battery = true,
        bool gps = true,
        bool attitude = true,
        bool connection = true,
        double heartbeatAgeSec = 0,
        double attitudeAgeSec = 0) =>
        new(
            Connection: connection
                ? new ConnectionState(true, 1, 1, Now.AddSeconds(-heartbeatAgeSec))
                : null,
            Attitude: attitude
                ? new AttitudeState(rollRad, pitchRad, 0, Now.AddSeconds(-attitudeAgeSec))
                : null,
            Position: null,
            VfrHud: null,
            Battery: battery ? new BatteryState(volts, 10f, remaining, Now) : null,
            FlightMode: GCS.Core.Domain.FlightMode.Manual,
            Gps: gps ? new GpsState(fixType, sats, eph, 100, Now) : null,
            IsArmed: armed);

    private static ComponentHealth Component(FlightHealthReport r, string name) =>
        r.Components.Single(c => c.Name == name);

    /// <summary>A trend that has seen a full 6S pack, so cell count is known.</summary>
    private static BatteryTrend Seen6S()
    {
        var trend = new BatteryTrend();
        trend.Add(Now.AddMinutes(-5), 25.2);   // 6S at 4.2 V/cell
        return trend;
    }

    // ── The defects that motivated this rewrite ─────────────────────

    [Fact]
    public void HealthyAircraftScoresFullMarksAndReportsNoIssues()
    {
        var report = FlightHealthAnalyzer.Analyze(Healthy(), Now);

        Assert.Equal(100, report.OverallScore);
        Assert.Equal(ComponentStatus.Ok, report.WorstStatus);
        Assert.Equal(AdvisoryVerdict.NoIssues, report.Verdict);
    }

    [Fact]
    public void UnmonitoredComponentsAreExcludedFromTheScoreRatherThanScoredZero()
    {
        var report = FlightHealthAnalyzer.Analyze(Healthy(), Now);

        // Motors/ESC/Vibration/EKF have no telemetry and must not drag the average.
        Assert.Contains(report.Unmeasured, c => c.Name == "Motors");
        Assert.Contains(report.Unmeasured, c => c.Name == "ESC");
        Assert.All(report.Unmeasured, c => Assert.Null(c.Score));
        Assert.Equal(100, report.OverallScore);
    }

    [Fact]
    public void NoTelemetryAtAllReportsUnknownNotZero()
    {
        var state = Healthy(battery: false, gps: false, attitude: false, connection: false);

        var report = FlightHealthAnalyzer.Analyze(state, Now);

        Assert.Null(report.OverallScore);     // unknown, not 0%
        Assert.Equal(AdvisoryVerdict.NoData, report.Verdict);   // no opinion, not "unsafe"
        Assert.Equal(0, report.CoveragePercent);
    }

    [Fact]
    public void BatteryDeductionsApplyIndependently()
    {
        // Regression: chained/nested conditions previously made every battery rule
        // unreachable unless all of them held at once.
        var lowPercentOnly = FlightHealthAnalyzer.Analyze(
            Healthy(volts: 24.8f, remaining: 20), Now);      // healthy volts, low percent

        var lowVoltsOnly = FlightHealthAnalyzer.Analyze(
            Healthy(volts: 20.4f, remaining: 95), Now, Seen6S());   // 3.4 V/cell, healthy percent

        Assert.Equal(ComponentStatus.Warning, Component(lowPercentOnly, "Battery").Status);
        Assert.Equal(ComponentStatus.Warning, Component(lowVoltsOnly, "Battery").Status);
    }

    [Fact]
    public void BatteryWarningIsActuallyReachable()
    {
        var report = FlightHealthAnalyzer.Analyze(Healthy(remaining: 22), Now);

        var battery = Component(report, "Battery");
        Assert.Equal(ComponentStatus.Warning, battery.Status);
        Assert.True(battery.Score < 100);
        Assert.Equal(AdvisoryVerdict.Issues, report.Verdict);
    }

    // ── Battery ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(0.2f)]     // what ArduPilot reports with BATT_MONITOR disabled
    [InlineData(2.5f)]
    public void ImplausibleVoltageMeansNoBatteryMonitorNotAFlatPack(float volts)
    {
        // Regression: 0.2 V was read as a real 1S pack at 0.24 V/cell and reported
        // CRITICAL forever on aircraft with no battery sensor fitted.
        var report = FlightHealthAnalyzer.Analyze(Healthy(volts: volts, remaining: -1), Now);

        var battery = Component(report, "Battery");
        Assert.Equal(ComponentStatus.NoData, battery.Status);
        Assert.Null(battery.Score);
    }

    [Fact]
    public void AnAircraftWithNoBatteryMonitorIsReportedAsLimitedDataNotAPass()
    {
        // The whole screenshot case: no battery sensor, healthy everything else.
        // Nothing is wrong, but the battery is flight-critical and unmeasured, so
        // the advisor must qualify the result instead of reporting a clean pass.
        var report = FlightHealthAnalyzer.Analyze(
            Healthy(volts: 0.2f, remaining: -1, heartbeatAgeSec: 146), Now);

        Assert.Equal(AdvisoryVerdict.LimitedData, report.Verdict);
        Assert.Equal(100, report.OverallScore);
        Assert.Contains(report.UnmonitoredVital, c => c.Name == "Battery");
    }

    [Fact]
    public void CriticallyLowCellVoltageIsCritical()
    {
        // 19.2 V on a pack we know is 6S => 3.2 V/cell.
        var report = FlightHealthAnalyzer.Analyze(
            Healthy(volts: 19.2f, remaining: 90), Now, Seen6S());

        Assert.Equal(ComponentStatus.Critical, Component(report, "Battery").Status);
        Assert.Equal(AdvisoryVerdict.CriticalIssue, report.Verdict);
    }

    [Fact]
    public void CellCountComesFromThePeakVoltageNotTheCurrentOne()
    {
        // The same 19.2 V reading is a healthy 5S or a critical 6S. Without having
        // seen the pack full, the analyzer must not invent the dangerous reading.
        var ambiguous = FlightHealthAnalyzer.Analyze(Healthy(volts: 19.2f, remaining: 90), Now);
        var known6S = FlightHealthAnalyzer.Analyze(
            Healthy(volts: 19.2f, remaining: 90), Now, Seen6S());

        Assert.Equal(ComponentStatus.Ok, Component(ambiguous, "Battery").Status);
        Assert.Equal(ComponentStatus.Critical, Component(known6S, "Battery").Status);
    }

    [Fact]
    public void ZeroRemainingPercentIsIgnoredBeforeTheAutopilotReportsIt()
    {
        // Percent of 0 on boot must not read as an empty pack.
        var report = FlightHealthAnalyzer.Analyze(Healthy(remaining: 0), Now);

        Assert.Equal(ComponentStatus.Ok, Component(report, "Battery").Status);
    }

    [Theory]
    [InlineData(11.1, 3)]
    [InlineData(14.8, 4)]
    [InlineData(22.2, 6)]
    public void CellCountIsInferredFromPackVoltage(double volts, int expected) =>
        Assert.Equal(expected, FlightHealthAnalyzer.EstimateCellCount(volts));

    // ── GPS ─────────────────────────────────────────────────────────

    [Fact]
    public void LosingGpsFixIsCriticalWhenArmedAndOnlyAWarningOnTheGround()
    {
        var airborne = FlightHealthAnalyzer.Analyze(Healthy(fixType: 1, armed: true), Now);
        var parked = FlightHealthAnalyzer.Analyze(Healthy(fixType: 1, armed: false), Now);

        Assert.Equal(ComponentStatus.Critical, Component(airborne, "GPS").Status);
        Assert.Equal(ComponentStatus.Warning, Component(parked, "GPS").Status);
    }

    [Fact]
    public void FewSatellitesAndPoorHdopBothWarn()
    {
        var fewSats = FlightHealthAnalyzer.Analyze(Healthy(sats: 4), Now);
        var poorHdop = FlightHealthAnalyzer.Analyze(Healthy(eph: 350), Now);

        Assert.Equal(ComponentStatus.Warning, Component(fewSats, "GPS").Status);
        Assert.Equal(ComponentStatus.Warning, Component(poorHdop, "GPS").Status);
    }

    // ── Link ────────────────────────────────────────────────────────

    [Fact]
    public void LinkStaysHealthyWhileTelemetryFlows()
    {
        // Regression: ConnectionState.LastHeartbeatUtc is only republished on
        // transitions, so it ages forever on a perfectly good link. Reading it as
        // a live clock reported "No heartbeat for 146s" on a connected aircraft
        // that was streaming GPS, attitude and position the whole time.
        var state = Healthy(heartbeatAgeSec: 146, attitudeAgeSec: 0);

        var report = FlightHealthAnalyzer.Analyze(state, Now);

        Assert.Equal(ComponentStatus.Ok, Component(report, "Link").Status);
        Assert.Equal(AdvisoryVerdict.NoIssues, report.Verdict);
    }

    [Fact]
    public void LinkIsCriticalWhenTheTrackerSaysDisconnected()
    {
        var state = Healthy() with { Connection = new ConnectionState(false, 1, 1, Now) };

        var report = FlightHealthAnalyzer.Analyze(state, Now);

        Assert.Equal(ComponentStatus.Critical, Component(report, "Link").Status);
        Assert.Equal(AdvisoryVerdict.CriticalIssue, report.Verdict);
    }

    [Fact]
    public void LinkIsCriticalWhenEveryTelemetryStreamGoesQuiet()
    {
        // Connection flag still up, but nothing has arrived for a while.
        var state = Healthy(attitudeAgeSec: 30) with
        {
            Position = null,
            VfrHud = null,
            Battery = null,
            Gps = null,
        };

        var report = FlightHealthAnalyzer.Analyze(state, Now);

        Assert.Equal(ComponentStatus.Critical, Component(report, "Link").Status);
        Assert.Contains(Component(report, "Link").Evidence, e => e.Text.Contains("No telemetry"));
    }

    // ── Attitude ────────────────────────────────────────────────────

    [Fact]
    public void ExtremeBankOnlyCountsWhenArmed()
    {
        const float steep = 1.4f;   // ~80Â°

        var armed = FlightHealthAnalyzer.Analyze(Healthy(rollRad: steep, armed: true), Now);
        var parked = FlightHealthAnalyzer.Analyze(Healthy(rollRad: steep, armed: false), Now);

        Assert.Equal(ComponentStatus.Warning, Component(armed, "Attitude").Status);
        Assert.Equal(ComponentStatus.Ok, Component(parked, "Attitude").Status);
    }

    [Fact]
    public void StaleAttitudeWarns()
    {
        var report = FlightHealthAnalyzer.Analyze(Healthy(attitudeAgeSec: 9), Now);

        Assert.Equal(ComponentStatus.Warning, Component(report, "Attitude").Status);
    }

    // ── Report shape ────────────────────────────────────────────────

    [Fact]
    public void CoverageReflectsHowMuchIsActuallyMeasured()
    {
        var full = FlightHealthAnalyzer.Analyze(Healthy(), Now);
        var partial = FlightHealthAnalyzer.Analyze(Healthy(gps: false, attitude: false), Now);

        Assert.True(full.CoveragePercent > partial.CoveragePercent);
        Assert.InRange(full.CoveragePercent, 1, 100);
    }

    [Fact]
    public void FindingsAreOrderedWorstFirst()
    {
        var state = Healthy(remaining: 10, sats: 4);   // critical battery + warning GPS

        var findings = FlightHealthAnalyzer.Analyze(state, Now).Findings;

        Assert.NotEmpty(findings);
        Assert.Equal(ComponentStatus.Critical, findings[0].Severity);
    }

    [Fact]
    public void PermanentlyUndecodedSubsystemsDoNotCountAsMissingVitalData()
    {
        // Motors/ESC/Vibration/EKF are never available, so treating them as vital
        // would pin every report to "limited data" and drain the qualifier of meaning.
        var report = FlightHealthAnalyzer.Analyze(Healthy(), Now);

        Assert.Empty(report.UnmonitoredVital);
        Assert.Equal(AdvisoryVerdict.NoIssues, report.Verdict);
    }

    [Fact]
    public void TheAdvisorNeverClaimsTheAircraftIsSafe()
    {
        // The GCS sees a fraction of the aircraft; "no issues found" is the
        // strongest honest claim it can make.
        var verdicts = new[]
        {
            FlightHealthAnalyzer.Analyze(Healthy(), Now).Headline,
            FlightHealthAnalyzer.Analyze(Healthy(volts: 0.2f, remaining: -1), Now).Headline,
        };

        Assert.All(verdicts, h => Assert.DoesNotContain("safe", h, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HeadlineDistinguishesUnknownFromHealthy()
    {
        var unknown = FlightHealthAnalyzer.Analyze(
            Healthy(battery: false, gps: false, attitude: false, connection: false), Now);
        var healthy = FlightHealthAnalyzer.Analyze(Healthy(), Now);

        Assert.Contains("No telemetry", unknown.Headline);
        Assert.Contains("No issues", healthy.Headline);
    }
}

public class BatteryTrendTests
{
    private static readonly DateTime Start = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoTrendUntilThereIsEnoughData()
    {
        var trend = new BatteryTrend();
        trend.Add(Start, 25.0);
        trend.Add(Start.AddSeconds(2), 24.9);

        Assert.False(trend.HasEnoughData);
        Assert.Equal(0, trend.SlopeVoltsPerMinute);   // never guess
    }

    [Fact]
    public void DetectsASteadyDischarge()
    {
        var trend = new BatteryTrend();
        for (int i = 0; i <= 12; i++)
            trend.Add(Start.AddSeconds(i * 5), 25.0 - i * 0.05);   // -0.6 V/min

        Assert.True(trend.HasEnoughData);
        Assert.InRange(trend.SlopeVoltsPerMinute, -0.75, -0.45);
    }

    [Fact]
    public void FlatVoltageHasNoSlope()
    {
        var trend = new BatteryTrend();
        for (int i = 0; i <= 12; i++)
            trend.Add(Start.AddSeconds(i * 5), 25.0);

        Assert.Equal(0, trend.SlopeVoltsPerMinute, 3);
    }

    [Fact]
    public void SamplesArrivingFasterThanTheRateLimitAreDropped()
    {
        var trend = new BatteryTrend();
        for (int i = 0; i < 50; i++)
            trend.Add(Start.AddMilliseconds(i * 100), 25.0);   // 10 Hz

        Assert.True(trend.SampleCount < 10);
    }

    [Fact]
    public void NonPositiveVoltageIsNotASample()
    {
        var trend = new BatteryTrend();
        trend.Add(Start, 0);
        trend.Add(Start.AddSeconds(2), -1);

        Assert.Equal(0, trend.SampleCount);
    }

    [Fact]
    public void PeakVoltageIsRetainedEvenAfterTheWindowMovesOn()
    {
        var trend = new BatteryTrend(TimeSpan.FromSeconds(20));
        trend.Add(Start, 25.2);                                  // full pack
        for (int i = 1; i <= 20; i++)
            trend.Add(Start.AddSeconds(i * 5), 21.0);            // later, under load

        Assert.Equal(25.2, trend.PeakVolts, 3);
    }

    [Fact]
    public void OldSamplesFallOutOfTheWindow()
    {
        var trend = new BatteryTrend(TimeSpan.FromSeconds(30));
        for (int i = 0; i <= 20; i++)
            trend.Add(Start.AddSeconds(i * 5), 25.0);

        Assert.True(trend.SpanSeconds <= 30);
    }

    [Fact]
    public void ClockGoingBackwardsResetsRatherThanFittingNoise()
    {
        var trend = new BatteryTrend();
        for (int i = 0; i <= 12; i++)
            trend.Add(Start.AddSeconds(i * 5), 25.0 - i * 0.05);

        trend.Add(Start.AddSeconds(-60), 25.0);

        Assert.Equal(1, trend.SampleCount);
    }
}

