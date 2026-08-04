using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GCS.Core.Advisor.Ai;

/// <summary>
/// Talks to any provider exposing the OpenAI chat-completions shape — Gemini's
/// compatibility endpoint, Groq, OpenRouter, GitHub Models — so switching provider
/// is configuration rather than code.
///
/// Written against HttpClient directly instead of an SDK: the request is a dozen
/// lines, and it keeps the app free of a dependency that would have to track four
/// providers' quirks.
/// </summary>
public sealed class OpenAiCompatibleChatClient : IAssistantChatClient, IDisposable
{
    private readonly AssistantOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public string DisplayName => _options.DisplayName;

    public OpenAiCompatibleChatClient(AssistantOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsClient = http is null;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120));
    }

    public Task<AssistantReply> AskAsync(
        string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            return Task.FromResult(AssistantReply.Failed("No assistant provider configured."));

        return SendAsync(_options.ResolveModel(), systemPrompt, userMessage, ct);
    }

    /// <summary>
    /// Smallest possible real request against one model, used to find out whether
    /// this key may actually call it. Listing a model does not mean it is usable.
    /// </summary>
    public Task<AssistantReply> ProbeModelAsync(string model, CancellationToken ct = default) =>
        SendAsync(model, "Reply with the single word: OK.", "Reply with OK.", ct);

    private async Task<AssistantReply> SendAsync(
        string model, string systemPrompt, string userMessage, CancellationToken ct)
    {
        string url = _options.ResolveBaseUrl().TrimEnd('/') + "/chat/completions";

        var payload = new JObject
        {
            ["model"] = model,
            // Low temperature: this is a factual readout, not creative writing.
            ["temperature"] = 0.2,
            // Generous, because reasoning models spend this budget thinking before
            // they write anything. At 500 the thoughts consumed the whole allowance
            // and the visible answer was cut off mid-sentence — or never started.
            ["max_tokens"] = 4000,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = systemPrompt },
                new JObject { ["role"] = "user", ["content"] = userMessage },
            },
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return AssistantReply.Failed(DescribeFailure((int)response.StatusCode, body));

            var choice = JObject.Parse(body)["choices"]?[0];
            string? text = choice?["message"]?["content"]?.ToString();
            string? finish = choice?["finish_reason"]?.ToString();

            // Reasoning models inline their scratchpad in the content; the operator
            // wants the answer, not the deliberation.
            string answer = ResponseSanitizer.Strip(text);

            if (string.IsNullOrWhiteSpace(answer))
            {
                return AssistantReply.Failed(
                    string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase)
                        ? "The model used its whole output budget thinking and never answered. "
                        + "Try a non-reasoning model — 'Find one that works' prefers those."
                        : "The model returned an empty response.");
            }

            // Say so rather than presenting half a sentence as a complete answer.
            if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase))
                answer += "\n\n[Answer was cut off — the model hit its output limit.]";

            return AssistantReply.Ok(answer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AssistantReply.Failed("Cancelled.");
        }
        catch (TaskCanceledException)
        {
            return AssistantReply.Failed($"The provider did not respond within {_options.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            return AssistantReply.Failed($"Could not reach the provider: {ex.Message}");
        }
        catch (Exception ex)
        {
            return AssistantReply.Failed($"Assistant error: {ex.Message}");
        }
    }

    /// <summary>
    /// Ask the provider which models this key may use.
    ///
    /// Model availability moves — names get retired and free-tier eligibility
    /// changes — so a hardcoded default eventually points at something the key
    /// cannot call. Asking beats guessing.
    /// </summary>
    public async Task<(bool Success, IReadOnlyList<string> Models, string? Error)>
        ListModelsAsync(CancellationToken ct = default)
    {
        string baseUrl = _options.ResolveBaseUrl().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey))
            return (false, Array.Empty<string>(), "Set a provider and API key first.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (false, Array.Empty<string>(), DescribeFailure((int)response.StatusCode, body));

            var ids = JObject.Parse(body)["data"]?
                .Select(m => m["id"]?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Replace("models/", ""))   // Gemini prefixes ids
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            return ids.Count == 0
                ? (false, Array.Empty<string>(), "The provider returned no models.")
                : (true, ids, null);
        }
        catch (Exception ex)
        {
            return (false, Array.Empty<string>(), $"Could not list models: {ex.Message}");
        }
    }

    /// <summary>
    /// Map the common failures to something the operator can act on, rather than
    /// echoing a raw JSON error blob into the chat panel.
    /// </summary>
    private static string DescribeFailure(int status, string body) => status switch
    {
        401 or 403 => "The API key was rejected. Check the key and try again.",
        404 => "That model does not exist for this provider. Use 'Load models' to pick one.",
        // A 429 on the first call is normally zero free-tier quota for the chosen
        // model rather than an exhausted limit, so point at the model first.
        429 => "Rejected with 'too many requests'. If this was your first attempt, "
             + "the model likely has no free-tier quota on your account — use "
             + "'Load models' and pick a different one. Otherwise wait and retry.",
        >= 500 => "The provider is having problems. Try again shortly.",
        _ => $"Provider returned {status}: {Truncate(body, 200)}",
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
