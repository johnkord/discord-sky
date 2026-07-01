using System.Collections.Concurrent;

namespace DiscordSky.Bot.Integrations.Members;

/// <summary>
/// Counts member joins per guild in a sliding time window to flag mass-join raids. Thread-safe. The UserJoined
/// handler records each join and reacts when <see cref="JoinResult.JustCrossed"/> is true (the join that first
/// reaches the threshold), so a single raid produces a single alert rather than one per joiner.
/// </summary>
public sealed class JoinRaidTracker
{
    private readonly ConcurrentDictionary<ulong, List<DateTimeOffset>> _joins = new();

    public JoinResult Record(ulong guildId, DateTimeOffset now, int windowSeconds, int threshold)
    {
        var window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds));
        var cap = Math.Max(2, threshold);
        var list = _joins.GetOrAdd(guildId, _ => new List<DateTimeOffset>());

        int count;
        bool crossed;
        lock (list)
        {
            list.RemoveAll(t => now - t > window);
            var before = list.Count;
            list.Add(now);
            count = list.Count;
            crossed = before < cap && count >= cap;
        }

        return new JoinResult(count >= cap, crossed, count);
    }
}

/// <summary>Outcome of recording one join: whether the window is in raid territory, whether this join first crossed the line, and the count in the window.</summary>
public readonly record struct JoinResult(bool IsRaid, bool JustCrossed, int CountInWindow);
