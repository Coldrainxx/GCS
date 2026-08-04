using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Advisor.Ai;

/// <summary>
/// A provider the operator can pick, with everything needed to actually get a key.
/// </summary>
public sealed record AssistantProviderInfo(
    string Id,
    string DisplayName,
    string Summary,
    string KeyUrl,
    IReadOnlyList<string> Steps,
    string DefaultModel)
{
    public bool RequiresKey => !string.Equals(Id, "None", StringComparison.OrdinalIgnoreCase);
    public bool RequiresBaseUrl => string.Equals(Id, "Custom", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The picker's contents. Kept in Core beside the options it configures so the
/// instructions cannot drift from the endpoints they describe.
/// </summary>
public static class AssistantProviders
{
    public static IReadOnlyList<AssistantProviderInfo> All { get; } = new[]
    {
        new AssistantProviderInfo(
            "None",
            "Off — built-in answers",
            "No account, no key, no internet. Short factual answers from the built-in rules.",
            "",
            new[] { "Nothing to set up. The advisor already works this way." },
            ""),

        new AssistantProviderInfo(
            "Gemini",
            "Google Gemini (free tier)",
            "Best quality on a free tier. Needs a Google account and internet.",
            "https://aistudio.google.com/apikey",
            new[]
            {
                "Open aistudio.google.com/apikey and sign in with a Google account.",
                "Click 'Create API key'.",
                "Copy the key and paste it below.",
            },
            "gemini-2.0-flash"),

        new AssistantProviderInfo(
            "Groq",
            "Groq (free tier)",
            "Very fast responses. Free tier with generous limits.",
            "https://console.groq.com/keys",
            new[]
            {
                "Open console.groq.com/keys and sign up (GitHub or Google works).",
                "Click 'Create API Key' and give it any name.",
                "Copy the key — it is shown only once — and paste it below.",
            },
            "llama-3.3-70b-versatile"),

        new AssistantProviderInfo(
            "OpenRouter",
            "OpenRouter (free models)",
            "Routes to several models, some marked ':free'.",
            "https://openrouter.ai/keys",
            new[]
            {
                "Open openrouter.ai/keys and sign in.",
                "Click 'Create Key'.",
                "Copy the key and paste it below.",
            },
            "meta-llama/llama-3.3-70b-instruct:free"),

        new AssistantProviderInfo(
            "GitHub",
            "GitHub Models (free for developers)",
            "Uses a GitHub personal access token.",
            "https://github.com/settings/personal-access-tokens",
            new[]
            {
                "Open github.com/settings/personal-access-tokens.",
                "Generate a fine-grained token — no extra scopes are needed.",
                "Copy the token and paste it below.",
            },
            "gpt-4o-mini"),

        new AssistantProviderInfo(
            "Custom",
            "Custom (OpenAI-compatible)",
            "Any endpoint exposing /chat/completions. Set the base URL and model yourself.",
            "",
            new[]
            {
                "Enter the base URL, e.g. https://host/v1 (without /chat/completions).",
                "Enter the model name exactly as the provider expects it.",
                "Enter the key, or any placeholder if the endpoint needs none.",
            },
            ""),
    };

    public static AssistantProviderInfo Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
