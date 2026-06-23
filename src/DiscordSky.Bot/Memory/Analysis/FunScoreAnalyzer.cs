using System.Text;
using DiscordSky.Bot.Memory.Logging;

namespace DiscordSky.Bot.Memory.Analysis;

/// <summary>
/// The deterministic half of the fun-score harness (fun_assessment_2026-06-21 §3.11): turns a pile of
/// <see cref="TranscriptEntry"/> into the concrete behavioral numbers the audit cares about, so "is he
/// funny" stops being a pure vibe. Model-agnostic and cheap (no API call), which is exactly why the
/// research (§6.8) flags it as the durable asset that survives model upgrades.
///
/// <para>
/// Deliberately NOT a single 0-100 "fun score": §6.6 (HumorRank, COMIC) argues humor is best judged
/// pairwise and calibrated to real audience reactions, so a holistic verdict belongs to a future LLM
/// judge plus reaction logging. This class measures the things a regex can measure honestly:
/// catchphrase over-use, length variance, formulaic openings, and heuristic proxies for the
/// helpful-leak and in-character rate.
/// </para>
/// </summary>
public static class FunScoreAnalyzer
{
    public static FunScoreReport Analyze(
        IReadOnlyList<TranscriptEntry> entries,
        FunScoreTargets? targets = null,
        string? personaFilter = null)
    {
        targets ??= new FunScoreTargets();

        var selected = entries
            .Where(e => personaFilter is null
                || (e.Persona?.Contains(personaFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        var replies = selected.Select(e => e.Reply ?? string.Empty).ToList();
        var total = replies.Count;

        var personaCounts = selected
            .GroupBy(e => e.Persona ?? "(none)")
            .ToDictionary(g => g.Key, g => g.Count());

        if (total == 0)
        {
            return new FunScoreReport(
                0, personaCounts, Array.Empty<TokenRate>(),
                new LengthStats(0, 0, 0, 0, 0, 0), 0, Array.Empty<OpeningCount>(), 0, 0, targets);
        }

        var tokenRates = targets.TrackedTokens
            .Select(tok =>
            {
                var count = replies.Count(r => r.Contains(tok, StringComparison.OrdinalIgnoreCase));
                return new TokenRate(tok, count, (double)count / total);
            })
            .ToList();

        var lengths = replies.Select(r => r.Length).OrderBy(n => n).ToList();
        var shortShare = (double)lengths.Count(n => n < targets.ShortLengthChars) / total;
        var longShare = (double)lengths.Count(n => n > targets.LongLengthChars) / total;
        var length = new LengthStats(
            Min: lengths[0],
            Median: Median(lengths),
            Mean: lengths.Average(),
            Max: lengths[^1],
            ShortShare: shortShare,
            LongShare: longShare);

        // Formulaic-opening rate: how many replies share their first two words with another reply.
        // This is the "six replies open with 'Ohohoho, [name]'" signal from the audit, made numeric.
        var openingKeys = replies.Select(OpeningKey).ToList();
        var keyCounts = openingKeys
            .Where(k => k.Length > 0)
            .GroupBy(k => k)
            .ToDictionary(g => g.Key, g => g.Count());
        var repeatedOpenings = openingKeys.Count(k => k.Length > 0 && keyCounts[k] >= 2);
        var openingRepetitionRate = (double)repeatedOpenings / total;
        var topRepeatedOpenings = keyCounts
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(kv => new OpeningCount(kv.Key, kv.Value))
            .ToList();

        var helpfulLeak = (double)replies.Count(r => LooksHelpful(r, targets)) / total;
        var inCharacter = (double)replies.Count(r => LooksInCharacter(r, targets)) / total;

        return new FunScoreReport(
            total, personaCounts, tokenRates, length,
            openingRepetitionRate, topRepeatedOpenings, helpfulLeak, inCharacter, targets);
    }

    private static int Median(IReadOnlyList<int> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0);
    }

    /// <summary>First two alphanumeric words, lowercased: the "shape" of how a reply opens.</summary>
    internal static string OpeningKey(string reply)
    {
        var words = new List<string>(2);
        var sb = new StringBuilder();
        foreach (var ch in reply)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
                if (words.Count == 2) break;
            }
        }
        if (sb.Length > 0 && words.Count < 2) words.Add(sb.ToString());
        return string.Join(' ', words);
    }

    private static bool LooksHelpful(string reply, FunScoreTargets targets)
    {
        var lower = reply.ToLowerInvariant();
        if (lower.Contains("```")) return true; // code block = assistant leaking
        if (targets.HelpfulLeakStrongMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return true;
        // Weak tells ("you can", "you should") are fine in short menace; only a leak when two or more
        // cluster inside a long, earnest block, which is what the audit's helpful leaks looked like.
        var weak = targets.HelpfulLeakWeakMarkers.Count(m => lower.Contains(m, StringComparison.Ordinal));
        return weak >= 2 && reply.Length > 300;
    }

    private static bool LooksInCharacter(string reply, FunScoreTargets targets) =>
        targets.PersonaMarkers.Any(m => reply.Contains(m, StringComparison.OrdinalIgnoreCase));
}
