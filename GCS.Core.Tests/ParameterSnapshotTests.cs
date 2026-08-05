using GCS.Core.Advisor;
using Xunit;

namespace GCS.Core.Tests;

public class ParameterSnapshotTests
{
    private static ParameterSnapshot Sample() => new(new[]
    {
        new ParameterInfo("BATT_CAPACITY", 5000, "mAh", "Battery capacity", 0, 100000),
        new ParameterInfo("BATT_MONITOR", 4, "", "Battery monitor type", 0, 20),
        new ParameterInfo("BATT_LOW_VOLT", 21.0f, "V", "Low battery voltage", 0, 60),
        new ParameterInfo("Q_ENABLE", 1, "", "QuadPlane enable", 0, 1),
        new ParameterInfo("FLTMODE1", 10, "", "Flight mode 1", 0, 30),
        new ParameterInfo("ARSPD_TYPE", 99, "", "Airspeed sensor type", 0, 10, OutOfRange: true),
    });

    [Fact]
    public void AnEmptySnapshotSaysNothingIsLoadedRatherThanLookingHealthy()
    {
        var snapshot = new ParameterSnapshot();

        Assert.True(snapshot.IsEmpty);
        Assert.Contains("No parameters have been read", snapshot.BuildSection("battery"));
    }

    [Fact]
    public void KeySettingsAreIncludedWithoutBeingAsked()
    {
        string section = Sample().BuildSection("how is the aircraft");

        Assert.Contains("BATT_MONITOR", section);
        Assert.Contains("Q_ENABLE", section);
    }

    [Fact]
    public void OutOfRangeParametersAreCalledOut()
    {
        string section = Sample().BuildSection("anything");

        Assert.Contains("Outside their expected range", section);
        Assert.Contains("ARSPD_TYPE", section);
        Assert.Contains("OUT OF RANGE", section);
    }

    [Theory]
    [InlineData("what is BATT_CAPACITY", "BATT_CAPACITY")]
    [InlineData("what is batt_capacity set to", "BATT_CAPACITY")]
    [InlineData("check Q_ENABLE please", "Q_ENABLE")]
    public void NamedParametersAreFound(string question, string expected)
    {
        var found = Sample().Mentioned(question);

        Assert.Contains(found, p => p.Name == expected);
    }

    [Fact]
    public void AParameterThatWasNotLoadedIsSimplyNotReturned()
    {
        // The model must be told it is absent, never handed a guessed value.
        var found = Sample().Mentioned("what is SERVO9_FUNCTION");

        Assert.DoesNotContain(found, p => p.Name == "SERVO9_FUNCTION");
    }

    [Fact]
    public void TheSectionForbidsInventingUnlistedParameters()
    {
        string section = Sample().BuildSection("what is INS_GYRO_FILTER");

        Assert.Contains("not listed", section);
        Assert.Contains("rather than guessing", section);
    }

    [Fact]
    public void MentionsAreBounded()
    {
        // A question naming dozens of parameters must not blow up the prompt.
        var many = Enumerable.Range(0, 60)
            .Select(i => new ParameterInfo($"TEST_PARAM{i}", i))
            .ToList();

        var snapshot = new ParameterSnapshot(many);
        string question = string.Join(" ", many.Select(p => p.Name));

        Assert.True(snapshot.Mentioned(question).Count <= 12);
    }

    [Fact]
    public void DuplicateNamesDoNotThrow()
    {
        var snapshot = new ParameterSnapshot(new[]
        {
            new ParameterInfo("BATT_CAPACITY", 5000),
            new ParameterInfo("BATT_CAPACITY", 6000),
        });

        Assert.Equal(1, snapshot.Count);
    }

    [Fact]
    public void BuiltInAnswerQuotesTheRealValue()
    {
        var named = Sample().Mentioned("what is BATT_CAPACITY");
        string reply = AssistantResponder.RespondAboutParameters(named);

        Assert.Contains("5000", reply);
        Assert.Contains("mAh", reply);
    }

    [Fact]
    public void BuiltInAnswerAdmitsWhenNothingIsLoaded()
    {
        string reply = AssistantResponder.RespondAboutParameters(new ParameterSnapshot());

        Assert.Contains("No parameters have been read", reply);
        Assert.Contains("PARAMS", reply);
    }
}

public class SetupSnapshotTests
{
    [Fact]
    public void AnEmptySetupSaysSoRatherThanImplyingItIsConfigured()
    {
        var setup = new SetupSnapshot();

        Assert.True(setup.IsEmpty);
        Assert.Contains("No setup information", setup.BuildSection());
    }

    [Fact]
    public void ChecksModesAndFrameAreReported()
    {
        var setup = new SetupSnapshot
        {
            FrameDescription = "QuadPlane (VTOL)",
            PreflightChecks = new[] { ("GPS", "PASSED", (string?)null), ("Compass", "FAILED", "not calibrated") },
            FlightModes = new[] { (1, "Manual (0)"), (2, "QHover (18)") },
        };

        string section = setup.BuildSection();

        Assert.Contains("QuadPlane", section);
        Assert.Contains("Compass: FAILED — not calibrated", section);
        Assert.Contains("Position 2: QHover (18)", section);
    }
}
