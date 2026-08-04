using System;
using System.Text.RegularExpressions;

namespace GCS.Core.Advisor.Ai;

/// <summary>
/// Strips a model's internal reasoning out of its answer.
///
/// Reasoning models emit their scratchpad inline — &lt;thought&gt;, &lt;think&gt;,
/// &lt;reasoning&gt; — and through an OpenAI-compatible endpoint it arrives as part
/// of the message content rather than a separate field. Showing it to an operator
/// buries the actual answer under the model talking to itself.
/// </summary>
public static class ResponseSanitizer
{
    private static readonly string[] ReasoningTags =
        { "thought", "thinking", "think", "reasoning", "reflection", "scratchpad" };

    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    public static string Strip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        string result = text;

        foreach (string tag in ReasoningTags)
        {
            // Closed blocks anywhere in the message.
            result = Regex.Replace(result, $@"<{tag}\b[^>]*>.*?</{tag}>", "", Options);

            // An unclosed block means the model was cut off mid-thought; everything
            // from the tag onward is scratchpad, so drop it.
            result = Regex.Replace(result, $@"<{tag}\b[^>]*>.*$", "", Options);

            // A stray closing tag with its opener already consumed leaves a prefix
            // of reasoning behind it.
            var orphan = Regex.Match(result, $@"^.*?</{tag}>", Options);
            if (orphan.Success) result = result[orphan.Length..];
        }

        result = result.Trim();

        // Never blank the reply: if a model wrapped everything in reasoning tags,
        // showing its thinking beats showing nothing.
        return result.Length == 0 ? text.Trim() : result;
    }
}
