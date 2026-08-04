using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Advisor.Ai;

/// <summary>
/// Picks which models are worth trying, and in what order.
///
/// A provider's model list contains far more than chat models — embeddings, image,
/// audio, moderation — and on a free tier only some chat models have any quota. So
/// the list is filtered to plausible chat models and ordered cheapest-first, since
/// the small fast models are the ones free tiers actually allow.
/// </summary>
public static class ModelCandidates
{
    /// <summary>Model families that cannot answer a chat prompt at all.</summary>
    private static readonly string[] NotChat =
    {
        "embed", "embedding", "tts", "text-to-speech", "audio", "whisper", "speech",
        "image", "imagen", "dall", "vision-only", "video", "veo", "rerank",
        "moderation", "guard", "safety", "aqa", "learnlm-tuned",
    };

    /// <summary>Cheap/fast families, which is what free tiers tend to permit.</summary>
    private static readonly string[] PreferredHints =
    { "flash", "mini", "lite", "small", "8b", "haiku", "instant" };

    /// <summary>Heavy or specialised families — likely paid, or slow to answer.</summary>
    private static readonly string[] DeprioritisedHints =
    { "pro", "ultra", "opus", "large", "70b", "405b", "thinking", "reasoning", "preview", "exp" };

    public static IReadOnlyList<string> RankChatModels(IEnumerable<string>? ids)
    {
        if (ids == null) return Array.Empty<string>();

        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsPlausibleChatModel)
            .OrderBy(Rank)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsPlausibleChatModel(string id)
    {
        string lower = id.ToLowerInvariant();
        return !NotChat.Any(bad => lower.Contains(bad, StringComparison.Ordinal));
    }

    /// <summary>0 = try first. Preferred beats neutral beats deprioritised.</summary>
    private static int Rank(string id)
    {
        string lower = id.ToLowerInvariant();

        bool preferred = PreferredHints.Any(h => lower.Contains(h, StringComparison.Ordinal));
        bool heavy = DeprioritisedHints.Any(h => lower.Contains(h, StringComparison.Ordinal));

        // A small model from a heavy family (e.g. "flash-preview") still beats a
        // genuinely heavy one, so preference is checked first.
        if (preferred && !heavy) return 0;
        if (preferred) return 1;
        if (!heavy) return 2;
        return 3;
    }
}
