using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Advisor;

public enum AssistantIntent
{
    Unknown,
    Greeting,
    Help,
    HealthReport,
    Battery,
    Gps,
    Link,
    Position,
    FlightModeStatus,
    Coverage,
    Parameters,
    Setup,
}

public readonly record struct IntentMatch(AssistantIntent Intent, int Score)
{
    public bool IsConfident => Intent != AssistantIntent.Unknown;
}

/// <summary>
/// Maps a typed question to an intent.
///
/// Matching is on whole words, not substrings. Substring matching is the classic
/// trap here: <c>Contains("hi")</c> fires on "this", "which" and "high", and
/// <c>Contains("flight")</c> swallows almost every sentence an operator types.
/// Candidates are scored and the best wins, so keyword order in the source does
/// not silently decide the outcome.
/// </summary>
public static class IntentRecognizer
{
    // Azerbaijani terms appear alongside English, with unaccented spellings too
    // since operators routinely type without diacritics.
    private static readonly (AssistantIntent Intent, string[] Words)[] Keywords =
    {
        (AssistantIntent.Greeting, new[]
            { "hi", "hello", "hey", "salam", "salamlar" }),

        // Only distinctive words belong here. Filler like "what"/"can"/"you" would
        // score against every question the operator asks and hijack the match.
        (AssistantIntent.Help, new[]
            { "help", "commands", "komek", "kömək" }),

        (AssistantIntent.HealthReport, new[]
            { "health", "status", "report", "analyse", "analyze", "analysis",
              "check", "diagnose", "aircraft", "uav", "drone",
              "analiz", "veziyyet", "vəziyyət", "hesabat", "yoxla" }),

        (AssistantIntent.Battery, new[]
            { "battery", "batt", "voltage", "volts", "power", "charge", "cell",
              "batareya", "gerginlik", "gərginlik", "enerji" }),

        (AssistantIntent.Gps, new[]
            { "gps", "satellite", "satellites", "sats", "fix", "hdop", "position",
              "peyk", "mövqe", "movqe" }),

        (AssistantIntent.Link, new[]
            { "link", "connection", "connected", "telemetry", "heartbeat", "signal",
              "elaqe", "əlaqə", "baglanti", "bağlantı" }),

        (AssistantIntent.Position, new[]
            { "where", "location", "altitude", "alt", "height", "coordinates",
              "lat", "lon", "hundurluk", "hündürlük", "harada" }),

        (AssistantIntent.FlightModeStatus, new[]
            { "mode", "armed", "disarmed", "arm", "flying", "rejim", "rejimi" }),

        (AssistantIntent.Coverage, new[]
            { "monitored", "monitor", "missing", "unknown", "coverage", "measure",
              "measured", "see", "izlenir", "izlənir" }),

        (AssistantIntent.Parameters, new[]
            { "parameter", "parameters", "param", "params", "setting", "settings",
              "value", "configured", "parametr", "parametrler", "parametrlər", "tenzim" }),

        (AssistantIntent.Setup, new[]
            { "setup", "calibration", "calibrate", "calibrated", "prearm", "preflight",
              "servo", "quraşdırma", "kalibrasiya" }),
    };

    /// <summary>Phrases that pin an intent outright, checked before word scoring.</summary>
    private static readonly (AssistantIntent Intent, string Phrase)[] Phrases =
    {
        (AssistantIntent.Coverage, "not monitored"),
        (AssistantIntent.Coverage, "what can you see"),
        (AssistantIntent.Coverage, "what do you monitor"),
        (AssistantIntent.HealthReport, "health report"),
        (AssistantIntent.HealthReport, "how is the aircraft"),
        (AssistantIntent.HealthReport, "how is it"),
        (AssistantIntent.Help, "what can you do"),
    };

    public static IntentMatch Recognize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new IntentMatch(AssistantIntent.Unknown, 0);

        string normalised = Normalise(input);

        foreach (var (intent, phrase) in Phrases)
        {
            if (normalised.Contains(phrase, StringComparison.Ordinal))
                return new IntentMatch(intent, 100);
        }

        var tokens = Tokenise(normalised);
        if (tokens.Count == 0) return new IntentMatch(AssistantIntent.Unknown, 0);

        AssistantIntent best = AssistantIntent.Unknown;
        int bestScore = 0;

        foreach (var (intent, words) in Keywords)
        {
            int score = words.Count(w => tokens.Contains(w));

            // Greetings are single words; a long question that happens to contain
            // "hi" as its own word is still a question, not a greeting.
            if (intent == AssistantIntent.Greeting && tokens.Count > 3) continue;

            if (score > bestScore)
            {
                bestScore = score;
                best = intent;
            }
        }

        return new IntentMatch(best, bestScore);
    }

    private static string Normalise(string input) =>
        input.Trim().ToLowerInvariant();

    private static HashSet<string> Tokenise(string normalised)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();

        foreach (char c in normalised)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
