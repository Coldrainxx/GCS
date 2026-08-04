using GCS.Core.Advisor;
using GCS.Core.Domain;
using Xunit;

namespace GCS.Core.Tests;

/// <summary>
/// The subsystems that became measurable once the GCS started requesting the
/// health message streams.
/// </summary>
public class HealthSubsystemTests
{
    private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    private static VehicleState Base(bool armed = false) => new(
        Connection: new ConnectionState(true, 1, 1, Now),
        Attitude: new AttitudeState(0, 0, 0, Now),
        Position: null,
        VfrHud: null,
        Battery: new BatteryState(24.8f, 10f, 95, Now),
        FlightMode: FlightMode.QHover,
        Gps: new GpsState(3, 14, 80, 100, Now),
        IsArmed: armed);

    private static ComponentHealth Component(VehicleState s, string name) =>
        FlightHealthAnalyzer.Analyze(s, Now).Components.Single(c => c.Name == name);

    // ── Vibration ───────────────────────────────────────────────────

    [Fact]
    public void VibrationIsUnmonitoredUntilItIsStreamed()
    {
        var c = Component(Base(), "Vibration");
        Assert.Equal(ComponentStatus.NoData, c.Status);
        Assert.Null(c.Score);
    }

    [Theory]
    [InlineData(10f, ComponentStatus.Ok)]
    [InlineData(45f, ComponentStatus.Warning)]
    [InlineData(75f, ComponentStatus.Critical)]
    public void VibrationIsJudgedAgainstArduPilotThresholds(float level, ComponentStatus expected)
    {
        var state = Base() with { Vibration = new VibrationState(level, 5, 5, 0, 0, 0, Now) };
        Assert.Equal(expected, Component(state, "Vibration").Status);
    }

    [Fact]
    public void AnyAccelerometerClippingIsCritical()
    {
        // Clipping corrupts the position estimate even when levels look acceptable.
        var state = Base() with { Vibration = new VibrationState(10, 10, 10, 3, 0, 0, Now) };

        var c = Component(state, "Vibration");
        Assert.Equal(ComponentStatus.Critical, c.Status);
        Assert.Contains(c.Evidence, e => e.Text.Contains("clipping", StringComparison.OrdinalIgnoreCase));
    }

    // ── EKF ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.2f, ComponentStatus.Ok)]
    [InlineData(0.7f, ComponentStatus.Warning)]
    [InlineData(1.4f, ComponentStatus.Critical)]
    public void EkfVarianceDrivesStatus(float variance, ComponentStatus expected)
    {
        var state = Base() with
        {
            Ekf = new EkfStatusState(7, variance, 0.1f, 0.1f, 0.1f, 0, Now)
        };
        Assert.Equal(expected, Component(state, "EKF").Status);
    }

    [Fact]
    public void AnUnhealthyAttitudeFlagIsCritical()
    {
        // Flags set, but the attitude bit is clear.
        var state = Base() with { Ekf = new EkfStatusState(6, 0.1f, 0.1f, 0.1f, 0.1f, 0, Now) };
        Assert.Equal(ComponentStatus.Critical, Component(state, "EKF").Status);
    }

    // ── Motors ──────────────────────────────────────────────────────

    [Fact]
    public void MotorsAreOnlyJudgedWhileArmed()
    {
        // On the ground every output sits at minimum; a zero spread there says
        // nothing about the motors.
        var outputs = new ushort[16];
        outputs[0] = 1100; outputs[1] = 1100; outputs[2] = 1100; outputs[3] = 1800;

        var parked = Base(armed: false) with { ServoOutput = new ServoOutputState(outputs, Now) };

        Assert.Equal(ComponentStatus.NoData, Component(parked, "Motors").Status);
    }

    [Theory]
    [InlineData(1500, 1520, ComponentStatus.Ok)]
    [InlineData(1400, 1560, ComponentStatus.Warning)]   // 16% spread
    [InlineData(1300, 1600, ComponentStatus.Critical)]  // 30% spread
    public void MotorImbalanceIsDetectedFromOutputSpread(
        ushort low, ushort high, ComponentStatus expected)
    {
        var outputs = new ushort[16];
        outputs[0] = low; outputs[1] = high; outputs[2] = low; outputs[3] = high;

        var state = Base(armed: true) with { ServoOutput = new ServoOutputState(outputs, Now) };

        Assert.Equal(expected, Component(state, "Motors").Status);
    }

    [Fact]
    public void ASaturatedMotorOutputIsCritical()
    {
        // At full output there is nothing left to stabilise with.
        var outputs = new ushort[16];
        outputs[0] = 1960; outputs[1] = 1960; outputs[2] = 1955; outputs[3] = 1958;

        var state = Base(armed: true) with { ServoOutput = new ServoOutputState(outputs, Now) };

        var c = Component(state, "Motors");
        Assert.Equal(ComponentStatus.Critical, c.Status);
        Assert.Contains(c.Evidence, e => e.Text.Contains("saturated"));
    }

    // ── ESC ─────────────────────────────────────────────────────────

    [Fact]
    public void MissingEscHardwareIsReportedAsUnmonitoredNotHealthy()
    {
        var state = Base() with { Esc = new EscTelemetryState(new EscReading[12], Now) };
        Assert.Equal(ComponentStatus.NoData, Component(state, "ESC").Status);
    }

    [Fact]
    public void HotEscIsFlagged()
    {
        var escs = new EscReading[12];
        escs[0] = new EscReading(105, 8000, 2400, 500);

        var state = Base() with { Esc = new EscTelemetryState(escs, Now) };

        Assert.Equal(ComponentStatus.Critical, Component(state, "ESC").Status);
    }

    // ── Power ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(5.1f, ComponentStatus.Ok)]
    [InlineData(4.5f, ComponentStatus.Warning)]
    [InlineData(4.1f, ComponentStatus.Critical)]
    public void RailVoltageIsJudged(float volts, ComponentStatus expected)
    {
        var state = Base() with { Power = new PowerStatusState(volts, 5.0f, 3, Now) };
        Assert.Equal(expected, Component(state, "Power").Status);
    }

    // ── Battery detail ──────────────────────────────────────────────

    [Fact]
    public void CellImbalanceIsCaughtEvenWhenThePackTotalLooksFine()
    {
        // 6S totalling a healthy 24.6 V, but one cell is badly down.
        var cells = new ushort[] { 4200, 4200, 4200, 4200, 4200, 3600, 65535, 65535, 65535, 65535 };

        var state = Base() with
        {
            BatteryStatus = new BatteryStatusState(cells, 1200, 25f, 80, Now)
        };

        var battery = Component(state, "Battery");
        Assert.Equal(ComponentStatus.Critical, battery.Status);
        Assert.Contains(battery.Evidence, e => e.Text.Contains("imbalance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnusedCellSlotsAreNotCountedAsCells()
    {
        // Unreported slots are UINT16_MAX and would otherwise read as 65 V cells.
        var cells = new ushort[] { 4100, 4100, 4100, 65535, 65535, 65535, 65535, 65535, 65535, 65535 };
        var status = new BatteryStatusState(cells, 0, 20f, 90, Now);

        Assert.Equal(3, status.CellCount);
        Assert.True(status.CellImbalanceVolts < 0.01f);
    }

    // ── Report shape ────────────────────────────────────────────────

    [Fact]
    public void UnstreamedSubsystemsStillDoNotDragTheScore()
    {
        // The whole point of the original rewrite, re-checked now that there are
        // five more subsystems that are usually absent.
        var report = FlightHealthAnalyzer.Analyze(Base(), Now);

        Assert.Equal(100, report.OverallScore);
        Assert.Equal(AdvisoryVerdict.NoIssues, report.Verdict);
        Assert.All(report.Unmeasured, c => Assert.Null(c.Score));
    }

    [Fact]
    public void AFullyInstrumentedHealthyAircraftScoresFullMarks()
    {
        var outputs = new ushort[16];
        outputs[0] = 1500; outputs[1] = 1505; outputs[2] = 1498; outputs[3] = 1502;

        var state = Base(armed: true) with
        {
            Vibration = new VibrationState(8, 9, 12, 0, 0, 0, Now),
            Ekf = new EkfStatusState(831, 0.15f, 0.12f, 0.10f, 0.08f, 0, Now),
            ServoOutput = new ServoOutputState(outputs, Now),
            Power = new PowerStatusState(5.15f, 5.0f, 3, Now),
        };

        var report = FlightHealthAnalyzer.Analyze(state, Now);

        Assert.Equal(100, report.OverallScore);
        Assert.Equal(AdvisoryVerdict.NoIssues, report.Verdict);
        // Link, Battery, GPS, Attitude, Vibration, EKF, Motors, Power.
        Assert.Equal(8, report.Measured.Count());
    }
}
