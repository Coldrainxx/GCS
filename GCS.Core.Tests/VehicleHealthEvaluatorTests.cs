using GCS.Core.Swarm;
using Xunit;

namespace GCS.Core.Tests;

public class VehicleHealthEvaluatorTests
{
    // A healthy vehicle: fresh telemetry, good battery, GPS fix.
    private static VehicleHealthResult Healthy(
        double age = 0.5, int battery = 80, bool hasBattery = true,
        bool hasGps = true, bool fix = true, bool armed = false)
        => VehicleHealthEvaluator.Evaluate(age, battery, hasBattery, hasGps, fix, armed);

    [Fact]
    public void HealthyVehicleRaisesNothing()
    {
        var r = Healthy();
        Assert.Equal(VehicleAlertLevel.None, r.Level);
        Assert.False(r.HasAlert);
        Assert.Equal("", r.Text);
    }

    [Theory]
    [InlineData(26, VehicleAlertLevel.None)]
    [InlineData(25, VehicleAlertLevel.Warning)]
    [InlineData(16, VehicleAlertLevel.Warning)]
    [InlineData(15, VehicleAlertLevel.Critical)]
    [InlineData(5, VehicleAlertLevel.Critical)]
    public void BatteryThresholdsEscalate(int percent, VehicleAlertLevel expected)
    {
        Assert.Equal(expected, Healthy(battery: percent).Level);
    }

    [Fact]
    public void UnreportedBatteryIsNotTreatedAsEmpty()
    {
        // No battery telemetry yet — must not read as 0% and cry wolf.
        Assert.Equal(VehicleAlertLevel.None, Healthy(battery: 0, hasBattery: false).Level);
        Assert.Equal(VehicleAlertLevel.None, Healthy(battery: 0, hasBattery: true).Level);
    }

    [Fact]
    public void LostTelemetryIsCriticalAndSuppressesOtherReasons()
    {
        // Once a vehicle goes silent, its last battery/GPS values are stale, so
        // reporting them alongside would be misleading.
        var r = VehicleHealthEvaluator.Evaluate(
            secondsSinceUpdate: 9, batteryPercent: 5, hasBattery: true,
            hasGps: true, hasGpsFix: false, isArmed: true);

        Assert.Equal(VehicleAlertLevel.Critical, r.Level);
        Assert.Contains("no telemetry", r.Text);
        Assert.DoesNotContain("battery", r.Text);
    }

    [Fact]
    public void FreshTelemetryJustUnderTheStaleLimitIsStillTrusted()
    {
        var r = Healthy(age: VehicleHealthEvaluator.TelemetryStaleSeconds - 0.1);
        Assert.Equal(VehicleAlertLevel.None, r.Level);
    }

    [Fact]
    public void LosingGpsMattersMoreWhenArmed()
    {
        Assert.Equal(VehicleAlertLevel.Warning, Healthy(fix: false, armed: false).Level);
        Assert.Equal(VehicleAlertLevel.Critical, Healthy(fix: false, armed: true).Level);
    }

    [Fact]
    public void VehicleWithoutGpsTelemetryIsNotPenalised()
    {
        Assert.Equal(VehicleAlertLevel.None, Healthy(hasGps: false, fix: false).Level);
    }

    [Fact]
    public void MultipleProblemsAreAllReportedAtTheWorstLevel()
    {
        var r = Healthy(battery: 20, fix: false, armed: true);

        Assert.Equal(VehicleAlertLevel.Critical, r.Level);   // GPS while armed
        Assert.Contains("battery 20%", r.Text);              // warning still listed
        Assert.Contains("no GPS fix", r.Text);
    }
}
