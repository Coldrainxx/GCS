using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Advisor.Ai;

/// <summary>
/// Outcome of one model call. Failures are values rather than exceptions: a
/// provider being down must degrade the assistant to its built-in answers, not
/// surface a stack trace mid-flight.
/// </summary>
public readonly record struct AssistantReply(bool Success, string Text, string? Error)
{
    public static AssistantReply Ok(string text) => new(true, text, null);
    public static AssistantReply Failed(string error) => new(false, "", error);
}

/// <summary>
/// Minimal chat surface. Deliberately not tool-capable — the assistant is
/// read-only, and the model is given a snapshot rather than access to the vehicle.
/// </summary>
public interface IAssistantChatClient
{
    /// <summary>Human-readable provider label for the UI.</summary>
    string DisplayName { get; }

    Task<AssistantReply> AskAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default);
}
