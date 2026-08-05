using GCS.Core.Advisor;
using Xunit;

namespace GCS.Core.Tests;

public class SwarmSnapshotTests
{
    private static SwarmSnapshot Fleet(int count) => new()
    {
        Vehicles = Enumerable.Range(1, count).Select(i => new SwarmVehicleInfo(
            SystemId: (byte)i,
            Name: $"UAV {i}",
            IsLeader: i == 1,
            IsActive: i == 1,
            FlightMode: "QHOVER",
            IsArmed: false,
            BatteryPercent: 90,
            Voltage: 24.6f,
            GpsFix: "3D FIX",
            Satellites: 14,
            AltitudeRelM: 0)).ToList(),
        FormationName = "V formation",
        SpacingM = 50,
    };

    [Fact]
    public void TheFleetSectionStatesTheCount()
    {
        // The bug this fixes: the advisor said "only one aircraft" with three
        // connected, because it only ever saw the active vehicle's telemetry.
        string section = Fleet(3).BuildSection();

        Assert.Contains("3 aircraft connected", section);
        Assert.Contains("UAV 1", section);
        Assert.Contains("UAV 2", section);
        Assert.Contains("UAV 3", section);
    }

    [Fact]
    public void TheActiveVehicleIsIdentifiedSoTheTelemetrySectionIsNotMistakenForTheWholeFleet()
    {
        string section = Fleet(3).BuildSection();

        Assert.Contains("shown in the main display", section);
        Assert.Contains("describes only the aircraft marked", section);
    }

    [Fact]
    public void LeaderAndFormationAreReported()
    {
        string section = Fleet(3).BuildSection();

        Assert.Contains("Leader: UAV 1", section);
        Assert.Contains("V formation", section);
        Assert.Contains("50 m spacing", section);
    }

    [Fact]
    public void ASingleVehicleIsNotDescribedAsASwarm()
    {
        var one = Fleet(1);

        Assert.False(one.IsSwarm);
        string section = one.BuildSection();

        Assert.Contains("1 aircraft connected", section);
        Assert.DoesNotContain("swarm mode", section);
        Assert.DoesNotContain("Leader:", section);
    }

    [Fact]
    public void NoVehiclesSaysSoRatherThanBeingSilent()
    {
        Assert.Contains("No vehicles are connected", new SwarmSnapshot().BuildSection());
    }

    [Fact]
    public void AVehicleWithoutABatteryMonitorIsNotReportedAsZeroVolts()
    {
        var snapshot = new SwarmSnapshot
        {
            Vehicles = new[]
            {
                new SwarmVehicleInfo(1, "UAV 1", true, true, "MANUAL", false, 0, 0f, "3D FIX", 12, 0)
            }
        };

        string section = snapshot.BuildSection();

        Assert.Contains("battery not monitored", section);
        Assert.DoesNotContain("0.0 V", section);
    }

    [Fact]
    public void AlertsAppearAgainstTheVehicleTheyBelongTo()
    {
        var snapshot = new SwarmSnapshot
        {
            Vehicles = new[]
            {
                new SwarmVehicleInfo(1, "UAV 1", true, true, "QHOVER", true, 90, 24.6f, "3D FIX", 14, 12),
                new SwarmVehicleInfo(2, "UAV 2", false, false, "QHOVER", true, 12, 21.0f, "NO FIX", 0, 11,
                    Alert: "battery 12% · no GPS fix"),
            }
        };

        string section = snapshot.BuildSection();

        Assert.Contains("UAV 2", section);
        Assert.Contains("ALERT: battery 12%", section);
    }
}

public class FleetIntentTests
{
    private static AssistantIntent Of(string input) => IntentRecognizer.Recognize(input).Intent;

    [Theory]
    [InlineData("how many aircrafts are there")]
    [InlineData("how many drones do we have")]
    [InlineData("how many vehicles are connected")]
    public void CountingQuestionsAskAboutTheFleet(string input) =>
        Assert.Equal(AssistantIntent.Fleet, Of(input));

    [Theory]
    [InlineData("show me the swarm")]
    [InlineData("who is the leader")]
    [InlineData("what formation are we in")]
    public void FleetWordsAreRecognised(string input) =>
        Assert.Equal(AssistantIntent.Fleet, Of(input));

    [Fact]
    public void AskingHowTheAircraftIsStillMeansHealthNotFleet()
    {
        // "aircraft" belongs to both readings; only counting words move it to Fleet.
        Assert.Equal(AssistantIntent.HealthReport, Of("how is the aircraft"));
    }

    [Fact]
    public void BuiltInFleetAnswerListsEveryVehicle()
    {
        var snapshot = new SwarmSnapshot
        {
            Vehicles = new[]
            {
                new SwarmVehicleInfo(1, "UAV 1", true, true, "QHOVER", false, 90, 24.6f, "3D FIX", 14, 0),
                new SwarmVehicleInfo(2, "UAV 2", false, false, "QHOVER", false, 88, 24.4f, "3D FIX", 13, 0),
            }
        };

        string reply = AssistantResponder.RespondAboutSwarm(snapshot);

        Assert.Contains("2 aircraft are connected", reply);
        Assert.Contains("UAV 1 (leader)", reply);
        Assert.Contains("UAV 2", reply);
    }

    [Fact]
    public void BuiltInFleetAnswerHandlesNothingConnected() =>
        Assert.Contains("No vehicles are connected", AssistantResponder.RespondAboutSwarm(null));
}
