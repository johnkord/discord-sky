using System.Collections.Concurrent;

namespace DiscordSky.Bot.Integrations.Safety;

public readonly record struct RaidResult(bool IsRaid, string? Reason)
{
    public static readonly RaidResult None = new(false, null);
}

/// <summary>
/// Behavioral raid detector. Keeps a short, per-author sliding window of link-bearing messages and flags a
/// raid when the same link fingerprint is sprayed across several channels, or repeated many times, inside the
/// window. This catches coordinated invite/link spam whose wording the content heuristics would miss, and it
/// is the signal that finally covers bot and webhook senders. Local, cheap, and stateful.
/// </summary>
public sealed class RaidTracker
{
    private readonly record struct Hit(DateTimeOffset Ts, ulong ChannelId, string Fingerprint);

    private readonly ConcurrentDictionary<ulong, List<Hit>> _byAuthor = new();

    /// <summary>
    /// Records a link-bearing message and returns whether the author is now raiding. The fingerprint should be a
    /// stable key for the link(s) posted (see <see cref="DomainUtilities.ExtractLinkKeys"/>); an empty
    /// fingerprint is ignored.
    /// </summary>
    public RaidResult Record(
        ulong authorId, ulong channelId, string fingerprint, DateTimeOffset now,
        int windowSeconds, int channelThreshold, int repeatThreshold)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return RaidResult.None;
        }

        var window = TimeSpan.FromSeconds(Math.Max(5, windowSeconds));
        var channelsNeeded = Math.Max(2, channelThreshold);
        var repeatsNeeded = Math.Max(2, repeatThreshold);
        var hits = _byAuthor.GetOrAdd(authorId, static _ => new List<Hit>());

        lock (hits)
        {
            hits.Add(new Hit(now, channelId, fingerprint));
            hits.RemoveAll(h => now - h.Ts > window);

            var sameChannels = new HashSet<ulong>();
            var sameCount = 0;
            foreach (var h in hits)
            {
                if (h.Fingerprint == fingerprint)
                {
                    sameCount++;
                    sameChannels.Add(h.ChannelId);
                }
            }

            if (sameChannels.Count >= channelsNeeded)
            {
                return new RaidResult(true, "raid-multichannel");
            }

            if (sameCount >= repeatsNeeded)
            {
                return new RaidResult(true, "raid-repeat");
            }
        }

        return RaidResult.None;
    }
}
