using GCS.Core.Advisor;
using GCS.Core.Advisor.Ai;
using GCS.Core.Domain;
using Xunit;

namespace GCS.Core.Tests;

public class AssistantOptionsTests
{
    [Fact]
    public void DefaultsToNoProviderSoTheAppWorksWithNoConfiguration()
    {
        var options = new AssistantOptions();

        Assert.False(options.IsConfigured);
        Assert.Equal("built-in answers", options.DisplayName);
    }

    [Fact]
    public void AProviderWithoutAKeyIsNotConfigured()
    {
        var options = new AssistantOptions { Provider = "Gemini" };
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.0-flash")]
    [InlineData("Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile")]
    public void KnownProvidersResolveEndpointAndDefaultModel(string provider, string url, string model)
    {
        var options = new AssistantOptions { Provider = provider, ApiKey = "k" };

        Assert.Equal(url, options.ResolveBaseUrl());
        Assert.Equal(model, options.ResolveModel());
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void AnExplicitModelOverridesTheProviderDefault()
    {
        var options = new AssistantOptions { Provider = "Groq", ApiKey = "k", Model = "custom-model" };
        Assert.Equal("custom-model", options.ResolveModel());
    }

    [Fact]
    public void CustomProviderNeedsItsOwnUrlAndModel()
    {
        var incomplete = new AssistantOptions { Provider = "Custom", ApiKey = "k" };
        Assert.False(incomplete.IsConfigured);

        var complete = new AssistantOptions
        {
            Provider = "Custom", ApiKey = "k",
            BaseUrl = "https://example.invalid/v1", Model = "m",
        };
        Assert.True(complete.IsConfigured);
    }

    [Fact]
    public void ProviderNameIsCaseInsensitive()
    {
        var options = new AssistantOptions { Provider = "gEmInI", ApiKey = "k" };
        Assert.True(options.IsConfigured);
    }
}

public class AssistantProvidersTests
{
    [Fact]
    public void OffIsFirstSoTheDefaultChoiceNeedsNoAccount()
    {
        Assert.Equal("None", AssistantProviders.All[0].Id);
        Assert.False(AssistantProviders.All[0].RequiresKey);
    }

    [Fact]
    public void EveryKeyRequiringProviderTellsTheOperatorWhereToGetOne()
    {
        var needKeys = AssistantProviders.All
            .Where(p => p.RequiresKey && !p.RequiresBaseUrl)
            .ToList();

        Assert.NotEmpty(needKeys);
        Assert.All(needKeys, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.KeyUrl));
            Assert.StartsWith("https://", p.KeyUrl);
            Assert.NotEmpty(p.Steps);
        });
    }

    [Fact]
    public void CatalogueIdsMatchWhatTheOptionsCanResolve()
    {
        // A provider offered in the picker but unknown to the resolver would save
        // cleanly and then silently fail on first use.
        foreach (var p in AssistantProviders.All.Where(p => p.RequiresKey && !p.RequiresBaseUrl))
        {
            var options = new AssistantOptions { Provider = p.Id, ApiKey = "k" };

            Assert.False(string.IsNullOrWhiteSpace(options.ResolveBaseUrl()));
            Assert.Equal(p.DefaultModel, options.ResolveModel());
            Assert.True(options.IsConfigured);
        }
    }

    [Fact]
    public void UnknownOrMissingIdFallsBackToOffRatherThanThrowing()
    {
        Assert.Equal("None", AssistantProviders.Find(null).Id);
        Assert.Equal("None", AssistantProviders.Find("nonsense").Id);
        Assert.Equal("Gemini", AssistantProviders.Find("gemini").Id);
    }
}

public class ResponseSanitizerTests
{
    [Theory]
    [InlineData("<thought>reasoning here</thought>Real answer.", "Real answer.")]
    [InlineData("<thinking>a</thinking> Real answer.", "Real answer.")]
    [InlineData("<think>a</think>Real answer.", "Real answer.")]
    [InlineData("<reasoning>a</reasoning>Real answer.", "Real answer.")]
    public void ReasoningBlocksAreRemoved(string raw, string expected) =>
        Assert.Equal(expected, ResponseSanitizer.Strip(raw));

    [Fact]
    public void MultiLineReasoningIsRemoved()
    {
        string raw = "<thought>\n* step one\n* step two\n</thought>\nBattery is at 95%.";
        Assert.Equal("Battery is at 95%.", ResponseSanitizer.Strip(raw));
    }

    [Fact]
    public void AnUnclosedBlockDropsEverythingAfterIt()
    {
        // A truncated response leaves the tag open; what follows is all scratchpad.
        Assert.Equal("Answer.", ResponseSanitizer.Strip("Answer.<thought>still thinking"));
    }

    [Fact]
    public void AStrayClosingTagDropsThePrefixBeforeIt()
    {
        Assert.Equal("The answer.", ResponseSanitizer.Strip("hidden reasoning</thought>The answer."));
    }

    [Fact]
    public void PlainAnswersAreUntouched()
    {
        const string clean = "GPS has no fix. Battery is not monitored.";
        Assert.Equal(clean, ResponseSanitizer.Strip(clean));
    }

    [Fact]
    public void AReplyThatIsEntirelyReasoningIsKeptRatherThanBlanked()
    {
        // Showing the thinking beats showing an empty bubble.
        string result = ResponseSanitizer.Strip("<thought>only thinking</thought>");
        Assert.NotEqual("", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputStaysEmpty(string? raw) =>
        Assert.Equal("", ResponseSanitizer.Strip(raw));
}

public class ModelCandidatesTests
{
    [Fact]
    public void NonChatModelsAreExcluded()
    {
        var ranked = ModelCandidates.RankChatModels(new[]
        {
            "text-embedding-004", "imagen-3.0", "tts-1", "whisper-large",
            "gemini-flash-latest", "veo-2", "text-moderation",
        });

        Assert.Single(ranked);
        Assert.Equal("gemini-flash-latest", ranked[0]);
    }

    [Fact]
    public void SmallFastModelsAreTriedFirstBecauseFreeTiersAllowThem()
    {
        var ranked = ModelCandidates.RankChatModels(new[]
        {
            "big-pro-model", "some-flash-model", "plain-model",
        });

        Assert.Equal("some-flash-model", ranked[0]);
        Assert.Equal("big-pro-model", ranked[^1]);
    }

    [Fact]
    public void DuplicatesAreCollapsed()
    {
        var ranked = ModelCandidates.RankChatModels(new[] { "a-flash", "A-FLASH", "a-flash" });
        Assert.Single(ranked);
    }

    [Fact]
    public void ARealisticGeminiListKeepsOnlyChatModels()
    {
        var ranked = ModelCandidates.RankChatModels(new[]
        {
            "gemini-flash-latest",
            "gemini-2.5-flash",
            "gemini-2.5-pro",
            "gemini-2.5-flash-image",              // image generation
            "gemini-2.5-flash-preview-tts",        // speech
            "gemini-2.5-flash-native-audio-dialog",// audio
            "text-embedding-004",                  // embeddings
            "gemini-embedding-001",
            "imagen-3.0-generate-002",
            "veo-3.0-generate",
            "aqa",
        });

        Assert.Equal(new[] { "gemini-2.5-flash", "gemini-flash-latest", "gemini-2.5-pro" }, ranked);
    }

    [Fact]
    public void ARealisticGroqListKeepsOnlyChatModels()
    {
        var ranked = ModelCandidates.RankChatModels(new[]
        {
            "llama-3.1-8b-instant",
            "llama-3.3-70b-versatile",
            "whisper-large-v3",                    // speech to text
            "distil-whisper-large-v3-en",
            "llama-guard-3-8b",                    // moderation
            "playai-tts",
        });

        Assert.Equal(new[] { "llama-3.1-8b-instant", "llama-3.3-70b-versatile" }, ranked);
    }

    [Fact]
    public void NullAndEmptyInputAreSafe()
    {
        Assert.Empty(ModelCandidates.RankChatModels(null));
        Assert.Empty(ModelCandidates.RankChatModels(Array.Empty<string>()));
        Assert.Empty(ModelCandidates.RankChatModels(new[] { "", "  " }));
    }
}

public class GroundingBuilderTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static VehicleState State(bool battery = true) =>
        new(
            Connection: new ConnectionState(true, 1, 1, Now),
            Attitude: new AttitudeState(0, 0, 0, Now),
            Position: new PositionState(40.4093, 49.8671, 120, 100, 90, 0, 0, 0, Now),
            VfrHud: null,
            Battery: battery ? new BatteryState(24.8f, 10f, 95, Now) : null,
            FlightMode: FlightMode.QHover,
            Gps: new GpsState(3, 14, 80, 100, Now),
            IsArmed: false);

    private static string Snapshot(VehicleState s) =>
        GroundingBuilder.BuildSnapshot(FlightHealthAnalyzer.Analyze(s, Now), s, Now);

    [Fact]
    public void UnmeasuredSubsystemsAreListedExplicitly()
    {
        // An omitted subsystem reads as "nothing to report" and invites the model
        // to fill the gap; naming it as NOT MEASURED does not.
        string snapshot = Snapshot(State(battery: false));

        Assert.Contains("NOT MEASURED", snapshot);
        Assert.Contains("Battery: NOT MEASURED", snapshot);
        Assert.Contains("do not guess", snapshot);
    }

    [Fact]
    public void MeasuredValuesAppearWithTheirEvidence()
    {
        string snapshot = Snapshot(State());

        Assert.Contains("Battery", snapshot);
        Assert.Contains("24.8", snapshot);
        Assert.Contains("GPS", snapshot);
    }

    [Fact]
    public void SystemPromptForbidsInventingDataAndClaimingSafety()
    {
        Assert.Contains("Never invent", GroundingBuilder.SystemPrompt);
        Assert.Contains("safe to fly", GroundingBuilder.SystemPrompt);
        Assert.Contains("cannot command", GroundingBuilder.SystemPrompt);
    }

    [Fact]
    public void TheQuestionIsCarriedAlongsideTheSnapshot()
    {
        var s = State();
        string message = GroundingBuilder.BuildUserMessage(
            "how is the battery", FlightHealthAnalyzer.Analyze(s, Now), s, Now);

        Assert.Contains("TELEMETRY SNAPSHOT", message);
        Assert.Contains("OPERATOR QUESTION", message);
        Assert.Contains("how is the battery", message);
    }

    [Fact]
    public void CoordinatesUseInvariantFormattingRegardlessOfLocale()
    {
        // A comma decimal separator would corrupt the numbers the model reads.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("az-AZ");

            Assert.Contains("40.409300", Snapshot(State()));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}

public class AssistantServiceFallbackTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClient : IAssistantChatClient
    {
        private readonly AssistantReply _reply;
        public int Calls { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string DisplayName => "Fake · test-model";

        public FakeClient(AssistantReply reply) => _reply = reply;

        public Task<AssistantReply> AskAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            Calls++;
            LastSystemPrompt = systemPrompt;
            return Task.FromResult(_reply);
        }
    }

    private static VehicleState State() =>
        new(
            Connection: new ConnectionState(true, 1, 1, Now),
            Attitude: new AttitudeState(0, 0, 0, Now),
            Position: null,
            VfrHud: null,
            Battery: new BatteryState(24.8f, 10f, 95, Now),
            FlightMode: FlightMode.Manual,
            Gps: new GpsState(3, 14, 80, 100, Now),
            IsArmed: false);

    private static Task<AssistantAnswer> Ask(AssistantService svc, string q = "how is the battery")
    {
        var s = State();
        return svc.AnswerAsync(q, FlightHealthAnalyzer.Analyze(s, Now), s, Now);
    }

    [Fact]
    public async Task WithNoProviderTheBuiltInAnswerIsUsed()
    {
        var answer = await Ask(new AssistantService());

        Assert.Equal(AnswerSource.BuiltIn, answer.Source);
        Assert.Contains("24.8", answer.Text);
    }

    [Fact]
    public async Task AConfiguredModelAnswers()
    {
        var svc = new AssistantService(new FakeClient(AssistantReply.Ok("Battery looks fine.")));

        var answer = await Ask(svc);

        Assert.Equal(AnswerSource.Model, answer.Source);
        Assert.Equal("Battery looks fine.", answer.Text);
    }

    [Fact]
    public async Task AFailingProviderFallsBackInsteadOfLosingTheAnswer()
    {
        // The point of the design: no key, no network or a provider outage must not
        // leave the operator with nothing.
        var svc = new AssistantService(new FakeClient(AssistantReply.Failed("rate limited")));

        var answer = await Ask(svc);

        Assert.Equal(AnswerSource.ModelFailedFellBack, answer.Source);
        Assert.Contains("24.8", answer.Text);       // real built-in answer, not an error
        Assert.Equal("rate limited", answer.Note);
    }

    [Fact]
    public async Task QuestionsAreAnsweredWithNoTelemetryAtAll()
    {
        // Nothing connected: the advisor must still answer rather than refuse —
        // "what can you do" has nothing to do with live telemetry.
        var empty = new VehicleState(null, null, null, null, null, null, null, false);
        var report = FlightHealthAnalyzer.Analyze(empty, Now);

        var answer = await new AssistantService()
            .AnswerAsync("what can you do", report, empty, Now);

        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        Assert.Equal(AnswerSource.BuiltIn, answer.Source);
    }

    [Fact]
    public async Task WithNoTelemetryATelemetryQuestionSaysSoRatherThanInventing()
    {
        var empty = new VehicleState(null, null, null, null, null, null, null, false);
        var report = FlightHealthAnalyzer.Analyze(empty, Now);

        var answer = await new AssistantService()
            .AnswerAsync("battery", report, empty, Now);

        Assert.Contains("not monitored", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheModelAlwaysReceivesTheConstrainingSystemPrompt()
    {
        var fake = new FakeClient(AssistantReply.Ok("ok"));

        await Ask(new AssistantService(fake));

        Assert.Equal(1, fake.Calls);
        Assert.Equal(GroundingBuilder.SystemPrompt, fake.LastSystemPrompt);
    }

    [Fact]
    public void ProviderNameReflectsWhetherAModelIsPresent()
    {
        Assert.Equal("built-in answers", new AssistantService().ProviderName);
        Assert.Equal("Fake · test-model",
            new AssistantService(new FakeClient(AssistantReply.Ok(""))).ProviderName);
    }
}
