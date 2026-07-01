using System.Text;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Memory.Logging;

namespace DiscordSky.Bot.Memory.Reception;

/// <summary>One ranked bot reply: the excerpt, its net reaction score, and how many positive reactions it drew.</summary>
public readonly record struct RankedBit(string Excerpt, int Score, int PositiveReactions);

/// <summary>
/// Turns the reaction log into a ranked "greatest hits" list of the bot's own replies. This closes the fun
/// feedback loop: we already record which reply each reaction landed on (via the stored excerpt), so we can
/// rank the bot's lines by how much laughter they earned and feed the winners back into the persona prompt.
/// Pure functions over reaction events, so the ranking is unit-testable without any I/O.
/// </summary>
public static class GreatestHits
{
    /// <summary>
    /// Ranks bot replies by net reaction sentiment (positive minus negative, honoring removes). One reply is
    /// identified by its message id; the richest excerpt seen for that message is kept. Returns best-first.
    /// </summary>
    public static IReadOnlyList<RankedBit> Rank(IEnumerable<ReactionEvent> events, int minExcerptLength = 16)
    {
        var byMessage = new Dictionary<ulong, Aggregate>();
        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.ReplyExcerpt))
            {
                continue;
            }

            var sentiment = ReactionSentiment.Score(e.Emote);
            if (sentiment == 0)
            {
                continue;
            }

            var isRemove = string.Equals(e.Action, "remove", StringComparison.OrdinalIgnoreCase);
            var delta = isRemove ? -sentiment : sentiment;

            byMessage.TryGetValue(e.MessageId, out var agg);
            agg.Score += delta;
            if (!isRemove && sentiment > 0)
            {
                agg.PositiveReactions += 1;
            }

            var excerpt = e.ReplyExcerpt!.Trim();
            if (excerpt.Length > (agg.Excerpt?.Length ?? 0))
            {
                agg.Excerpt = excerpt;
            }

            byMessage[e.MessageId] = agg;
        }

        return byMessage.Values
            .Where(a => a.Excerpt is { Length: > 0 } && a.Excerpt.Length >= minExcerptLength)
            .Select(a => new RankedBit(a.Excerpt!, a.Score, a.PositiveReactions))
            .OrderByDescending(b => b.Score)
            .ThenByDescending(b => b.PositiveReactions)
            .ToList();
    }

    /// <summary>The excerpts of the top <paramref name="count"/> positively-received replies.</summary>
    public static IReadOnlyList<string> TopHits(IEnumerable<ReactionEvent> events, int count)
        => Rank(events)
            .Where(b => b.Score > 0)
            .Take(Math.Max(0, count))
            .Select(b => b.Excerpt)
            .ToList();

    /// <summary>
    /// Formats a persona-prompt directive from a sample of proven bits, or null when there are none. Frames them
    /// as reception intel to imitate the STYLE of, not repeat verbatim.
    /// </summary>
    public static string? BuildDirective(IReadOnlyList<string> hits)
    {
        if (hits is null || hits.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("\nReception intel (THIS server): these past lines of yours drew the biggest laughs. Do NOT reuse them, but bring the same energy that landed (the specificity, the cruelty, the timing):\n");
        foreach (var h in hits)
        {
            var line = h.Length > 180 ? h[..180] : h;
            sb.Append("- \"").Append(line).Append("\"\n");
        }

        return sb.ToString();
    }

    private struct Aggregate
    {
        public int Score;
        public int PositiveReactions;
        public string? Excerpt;
    }
}

/// <summary>
/// Thread-safe holder for the current proven-bits pool, refreshed periodically by
/// <see cref="GreatestHitsRefreshService"/> and sampled per-turn by the orchestrator.
/// </summary>
public sealed class GreatestHitsCache
{
    private volatile IReadOnlyList<string> _hits = Array.Empty<string>();

    public void Set(IReadOnlyList<string>? hits) => _hits = hits ?? Array.Empty<string>();

    public IReadOnlyList<string> Hits => _hits;

    /// <summary>
    /// A rotating sample of up to <paramref name="n"/> distinct hits, so each turn sees variety without
    /// bloating the prompt or fixating on a single line.
    /// </summary>
    public IReadOnlyList<string> Sample(IRandomProvider rng, int n)
    {
        var hits = _hits;
        if (n <= 0 || hits.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (hits.Count <= n)
        {
            return hits;
        }

        var chosen = new List<string>(n);
        var used = new HashSet<int>();
        var guard = 0;
        while (chosen.Count < n && guard++ < n * 12)
        {
            var i = (int)(Math.Clamp(rng.NextDouble(), 0.0, 0.999999) * hits.Count);
            if (used.Add(i))
            {
                chosen.Add(hits[i]);
            }
        }

        return chosen;
    }
}
