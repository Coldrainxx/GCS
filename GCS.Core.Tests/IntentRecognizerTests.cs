using GCS.Core.Advisor;
using GCS.Core.Domain;
using Xunit;

namespace GCS.Core.Tests;

public class IntentRecognizerTests
{
    private static AssistantIntent Of(string input) => IntentRecognizer.Recognize(input).Intent;

    // ── The substring trap ──────────────────────────────────────────

    [Theory]
    [InlineData("what is this")]
    [InlineData("which battery is fitted")]
    [InlineData("altitude is too high")]
    public void WordsContainingHiAreNotGreetings(string input) =>
        Assert.NotEqual(AssistantIntent.Greeting, Of(input));

    [Theory]
    [InlineData("hi")]
    [InlineData("hello")]
    [InlineData("salam")]
    public void ActualGreetingsAreRecognised(string input) =>
        Assert.Equal(AssistantIntent.Greeting, Of(input));

    [Fact]
    public void AQuestionMentioningFlightIsNotSwallowedByOneKeyword()
    {
        // "flight" previously routed almost everything to log analysis because it
        // was tested before the more specific intents.
        Assert.Equal(AssistantIntent.Battery, Of("what is the battery voltage in flight"));
    }

    [Fact]
    public void KeywordOrderInTheSourceDoesNotDecideTheOutcome()
    {
        // Battery wins on two matching words even though other intents match one.
        Assert.Equal(AssistantIntent.Battery, Of("check the battery voltage"));
    }

    // ── Intents ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("how is the aircraft", AssistantIntent.HealthReport)]
    [InlineData("health report", AssistantIntent.HealthReport)]
    [InlineData("analiz", AssistantIntent.HealthReport)]
    [InlineData("battery", AssistantIntent.Battery)]
    [InlineData("batareya", AssistantIntent.Battery)]
    [InlineData("how many satellites", AssistantIntent.Gps)]
    [InlineData("is the link connected", AssistantIntent.Link)]
    [InlineData("where are we", AssistantIntent.Position)]
    [InlineData("what mode is it in", AssistantIntent.FlightModeStatus)]
    [InlineData("what is not monitored", AssistantIntent.Coverage)]
    [InlineData("what can you do", AssistantIntent.Help)]
    public void IntentsAreRecognised(string input, AssistantIntent expected) =>
        Assert.Equal(expected, Of(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("qwertyuiop asdfgh")]
    public void NonsenseAndEmptyInputAreUnknown(string? input) =>
        Assert.Equal(AssistantIntent.Unknown, Of(input!));

    [Fact]
    public void PunctuationDoesNotBreakMatching() =>
        Assert.Equal(AssistantIntent.Battery, Of("BATTERY?!"));

    [Theory]
    [InlineData("what mode is it in", AssistantIntent.FlightModeStatus)]
    [InlineData("what is the battery", AssistantIntent.Battery)]
    [InlineData("what is the gps fix", AssistantIntent.Gps)]
    public void FillerWordsDoNotHijackTheMatch(string input, AssistantIntent expected) =>
        // "what"/"can"/"you" were once Help keywords, so every question starting
        // with "what" scored for Help and won ties by declaration order.
        Assert.Equal(expected, Of(input));
}

public class AssistantResponderTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static VehicleState State(bool battery = true, float volts = 24.8f, int remaining = 95) =>
        new(
            Connection: new ConnectionState(true, 1, 1, Now),
            Attitude: new AttitudeState(0, 0, 0, Now),
            Position: new PositionState(40.4093, 49.8671, 120, 100, 90, 0, 0, 0, Now),
            VfrHud: null,
            Battery: battery ? new BatteryState(volts, 10f, remaining, Now) : null,
            FlightMode: FlightMode.QHover,
            Gps: new GpsState(3, 14, 80, 100, Now),
            IsArmed: true);

    private static FlightHealthReport Report(VehicleState s) =>
        FlightHealthAnalyzer.Analyze(s, Now);

    [Fact]
    public void UnknownIntentOffersWhatItCanAnswerInsteadOfGuessing()
    {
        var s = State();
        string reply = AssistantResponder.Respond(AssistantIntent.Unknown, Report(s), s);

        Assert.Contains("did not understand", reply);
        Assert.Contains("battery", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBatteryIsReportedAsNotMonitoredNotInvented()
    {
        var s = State(battery: false);
        string reply = AssistantResponder.Respond(AssistantIntent.Battery, Report(s), s);

        Assert.Contains("not monitored", reply, StringComparison.OrdinalIgnoreCase);
        // Must not fabricate a figure.
        Assert.DoesNotContain("V (", reply);
    }

    [Fact]
    public void BatteryAnswerQuotesTheRealReading()
    {
        var s = State(volts: 24.8f, remaining: 95);
        string reply = AssistantResponder.Respond(AssistantIntent.Battery, Report(s), s);

        Assert.Contains("24.8", reply);
        Assert.Contains("95", reply);
    }

    [Fact]
    public void HealthSummaryFlagsWhatItCouldNotMeasure()
    {
        var s = State(battery: false);
        string reply = AssistantResponder.Respond(AssistantIntent.HealthReport, Report(s), s);

        Assert.Contains("Not measured", reply);
        Assert.Contains("Battery", reply);
        Assert.Contains("not a complete picture", reply);
    }

    [Fact]
    public void HealthSummaryNeverClaimsSafety()
    {
        var s = State();
        string reply = AssistantResponder.Respond(AssistantIntent.HealthReport, Report(s), s);

        Assert.DoesNotContain("safe", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionAnswerUsesRealCoordinates()
    {
        var s = State();
        string reply = AssistantResponder.Respond(AssistantIntent.Position, Report(s), s);

        Assert.Contains("40.409", reply);
        Assert.Contains("49.867", reply);
    }

    [Fact]
    public void PositionAnswerAdmitsWhenThereIsNoFixData()
    {
        var s = State() with { Position = null };
        string reply = AssistantResponder.Respond(AssistantIntent.Position, Report(s), s);

        Assert.Contains("No position", reply);
    }

    [Fact]
    public void CoverageAnswerListsBothSides()
    {
        var s = State();
        string reply = AssistantResponder.Respond(AssistantIntent.Coverage, Report(s), s);

        Assert.Contains("Measuring:", reply);
        Assert.Contains("Not measuring:", reply);
        Assert.Contains("ESC", reply);
    }

    [Fact]
    public void ModeAnswerReportsArmedState()
    {
        var s = State();
        string reply = AssistantResponder.Respond(AssistantIntent.FlightModeStatus, Report(s), s);

        Assert.Contains("ARMED", reply);
        Assert.Contains("QHover", reply, StringComparison.OrdinalIgnoreCase);
    }
}
