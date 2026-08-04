using System;
using System.Threading;
using System.Threading.Tasks;
using GCS.Core.Domain;

namespace GCS.Core.Advisor.Ai;

/// <summary>Where an answer came from, so the UI can be honest about it.</summary>
public enum AnswerSource
{
    /// <summary>Deterministic rules — always available, offline, instant.</summary>
    BuiltIn,
    Model,
    /// <summary>The model was configured but failed; the built-in answer was used.</summary>
    ModelFailedFellBack,
}

public readonly record struct AssistantAnswer(string Text, AnswerSource Source, string? Note);

/// <summary>
/// Answers an operator question, preferring a configured model but never depending
/// on one.
///
/// The deterministic responder is the floor, not the fallback of last resort: with
/// no key, no network, or a provider outage the advisor still answers. That matters
/// for a GCS used in a field where connectivity is not a given — an assistant that
/// goes silent exactly when you are away from coverage is worse than one that only
/// ever gives short factual answers.
/// </summary>
public sealed class AssistantService
{
    private readonly IAssistantChatClient? _client;

    public AssistantService(IAssistantChatClient? client = null) => _client = client;

    /// <summary>True when a model is available to attempt.</summary>
    public bool HasModel => _client != null;

    public string ProviderName => _client?.DisplayName ?? "built-in answers";

    /// <param name="log">
    /// A recorded flight to answer about, when the operator is reviewing a log
    /// rather than asking about the live aircraft.
    /// </param>
    public async Task<AssistantAnswer> AnswerAsync(
        string question,
        FlightHealthReport report,
        VehicleState state,
        DateTime nowUtc,
        CancellationToken ct = default,
        Logging.FlightLogSummary? log = null)
    {
        // Computed regardless: it is both the no-model answer and the safety net
        // if the provider fails.
        var intent = IntentRecognizer.Recognize(question);
        string builtIn = log != null
            ? AssistantResponder.RespondAboutLog(intent.Intent, log)
            : AssistantResponder.Respond(intent.Intent, report, state);

        if (_client == null)
            return new AssistantAnswer(builtIn, AnswerSource.BuiltIn, null);

        string userMessage = GroundingBuilder.BuildUserMessage(question, report, state, nowUtc, log);
        var reply = await _client
            .AskAsync(GroundingBuilder.SystemPrompt, userMessage, ct)
            .ConfigureAwait(false);

        if (reply.Success)
            return new AssistantAnswer(reply.Text, AnswerSource.Model, null);

        return new AssistantAnswer(builtIn, AnswerSource.ModelFailedFellBack, reply.Error);
    }
}
