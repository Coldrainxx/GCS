using System;

namespace GCS.Core.Advisor.Ai;

/// <summary>
/// Which LLM the assistant talks to, if any.
///
/// Every supported provider speaks the OpenAI chat-completions shape, so the
/// provider is three settings rather than three integrations. Keys live in the
/// gitignored appsettings.json next to the executable — never in source, and never
/// shipped with the app: a key embedded in a shared build is both extractable and
/// a shared rate limit.
/// </summary>
public sealed class AssistantOptions
{
    /// <summary>Free-tier friendly presets. Custom uses <see cref="BaseUrl"/> verbatim.</summary>
    public string Provider { get; set; } = "None";

    /// <summary>Only needed when <see cref="Provider"/> is Custom.</summary>
    public string BaseUrl { get; set; } = "";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>Give up rather than leave the operator staring at a spinner.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// True when there is enough configuration to attempt a request. Anything less
    /// falls back to the built-in answers instead of failing.
    /// </summary>
    public bool IsConfigured =>
        !string.Equals(Provider, "None", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ResolveBaseUrl()) &&
        !string.IsNullOrWhiteSpace(ResolveModel());

    /// <summary>Endpoint for the provider, or the explicit BaseUrl for Custom.</summary>
    public string ResolveBaseUrl() => Provider.Trim().ToLowerInvariant() switch
    {
        // Gemini exposes an OpenAI-compatible surface, so it needs no special client.
        "gemini" => "https://generativelanguage.googleapis.com/v1beta/openai",
        "groq" => "https://api.groq.com/openai/v1",
        "openrouter" => "https://openrouter.ai/api/v1",
        "github" => "https://models.inference.ai.azure.com",
        _ => BaseUrl.Trim(),
    };

    /// <summary>Sensible free-tier default per provider when Model is blank.</summary>
    public string ResolveModel()
    {
        if (!string.IsNullOrWhiteSpace(Model)) return Model.Trim();

        return Provider.Trim().ToLowerInvariant() switch
        {
            "gemini" => "gemini-2.0-flash",
            "groq" => "llama-3.3-70b-versatile",
            "openrouter" => "meta-llama/llama-3.3-70b-instruct:free",
            "github" => "gpt-4o-mini",
            _ => "",
        };
    }

    /// <summary>Short label for the UI, e.g. "Gemini · gemini-2.0-flash".</summary>
    public string DisplayName =>
        IsConfigured ? $"{Provider.Trim()} · {ResolveModel()}" : "built-in answers";
}
