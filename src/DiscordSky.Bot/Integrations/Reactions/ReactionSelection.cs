using System.Linq;

namespace DiscordSky.Bot.Integrations.Reactions;

/// <summary>
/// Chooses WHICH of a guild's many custom emotes to put in front of the reaction judge for a single message.
/// Round-6 telemetry (2026-07-06) showed the judge never once picked a custom emote across 58 reactions: the
/// server has 138 of them, but they were offered undescribed and only the arbitrary first 30, so the model
/// anchored on the richly-described unicode palette and collapsed onto a couple of generic faces (eyeroll,
/// clown). Controlling the candidate SET is the most direct lever against that mode collapse (the same
/// typicality bias Verbalized Sampling, arXiv:2510.01171, tackles for open generation; here we simply rotate
/// and bias the options rather than sample a verbalized distribution).
///
/// <para>Selection: emotes whose NAME echoes the author (the server's member inside-joke emotes) or a word in
/// the message come first (a cheap relevance + personalization boost the judge still gets to accept or
/// reject), the just-used tokens are pushed to the back so variety is favored without losing anything, and
/// the remaining budget is a rotating random sample so the long tail surfaces over time. Pure and
/// deterministic given the RNG, so it is unit-tested.</para>
/// </summary>
public static class ReactionSelection
{
    private const int MinTokenLen = 3;
    private const int MaxMessageTokens = 12;

    /// <summary>
    /// Returns up to <paramref name="max"/> custom-emote names, relevant-and-fresh first, then a rotating
    /// sample, then (only if room remains) the recently-used ones. Deterministic given <paramref name="nextDouble"/>.
    /// </summary>
    public static IReadOnlyList<string> SelectCustomEmoteNames(
        IReadOnlyList<string> allNames,
        string? authorName,
        string? messageText,
        IReadOnlyCollection<string>? recentTokens,
        int max,
        Func<double> nextDouble)
    {
        if (allNames is null || allNames.Count == 0 || max <= 0) return Array.Empty<string>();

        var recent = recentTokens is { Count: > 0 }
            ? new HashSet<string>(recentTokens, StringComparer.OrdinalIgnoreCase)
            : null;
        var authorTokens = Tokenize(authorName, int.MaxValue);
        var msgTokens = Tokenize(messageText, MaxMessageTokens);

        var relevant = new List<string>();
        var rest = new List<string>();
        var justUsed = new List<string>();
        foreach (var name in allNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (recent is not null && recent.Contains(name)) { justUsed.Add(name); continue; }
            if (IsRelevant(name, authorTokens, msgTokens)) relevant.Add(name);
            else rest.Add(name);
        }

        // Shuffle each bucket so ties (and the long tail) rotate across calls instead of always the same order.
        Shuffle(relevant, nextDouble);
        Shuffle(rest, nextDouble);
        Shuffle(justUsed, nextDouble);

        var result = new List<string>(Math.Min(max, allNames.Count));
        AddUpTo(result, relevant, max);
        AddUpTo(result, rest, max);
        AddUpTo(result, justUsed, max);
        return result;
    }

    private static void AddUpTo(List<string> dest, List<string> src, int max)
    {
        foreach (var s in src)
        {
            if (dest.Count >= max) return;
            dest.Add(s);
        }
    }

    private static bool IsRelevant(string name, IReadOnlyList<string> authorTokens, IReadOnlyList<string> msgTokens)
    {
        var lower = name.ToLowerInvariant();
        // Author echo: member inside-joke emotes share a chunk or a prefix with the author's name
        // (e.g. author "Alascene" -> "alagoinin", "alapat", "madascene").
        foreach (var t in authorTokens)
        {
            if (lower.Contains(t, StringComparison.Ordinal)) return true;
            if (lower.Length >= MinTokenLen && t.Contains(lower, StringComparison.Ordinal)) return true;
            if (SharedPrefixLen(lower, t) >= MinTokenLen) return true;
        }
        // Message echo: a topical word appears in the emote name (e.g. "pog", "sad", "cope", "clown").
        foreach (var t in msgTokens)
        {
            if (lower.Contains(t, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IReadOnlyList<string> Tokenize(string? text, int cap)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (t.Length >= MinTokenLen && seen.Add(t))
            {
                tokens.Add(t);
                if (tokens.Count >= cap) break;
            }
        }
        return tokens;
    }

    private static int SharedPrefixLen(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static void Shuffle(List<string> list, Func<double> nextDouble)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            var j = (int)(nextDouble() * (i + 1));
            if (j > i) j = i;      // guard against a nextDouble() that returns exactly 1.0
            if (j < 0) j = 0;
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
