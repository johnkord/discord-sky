using System.Collections.Concurrent;

namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// A bounded, TTL-capped record of who has spoken recently, by display name. Two jobs: it is the activity
/// signal for the tick's gate (do not evolve a world nobody is watching), and it is the candidate list that
/// gates structured rank creation (the log may only bestow a title on someone actually present). Populated
/// from the message path; read by the tick.
/// </summary>
public sealed class RecentParticipants
{
    private readonly ConcurrentDictionary<ulong, (string Name, DateTimeOffset At)> _seen = new();
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;

    public RecentParticipants(TimeSpan ttl, Func<DateTimeOffset>? clock = null)
    {
        _ttl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromHours(6);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Record(ulong userId, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        _seen[userId] = (displayName.Trim(), _clock());
        Prune();
    }

    /// <summary>Up to <paramref name="max"/> distinct recent display names, most recent first.</summary>
    public IReadOnlyList<string> Names(int max)
    {
        if (max <= 0) return Array.Empty<string>();
        var cutoff = _clock() - _ttl;
        return _seen.Values
            .Where(v => v.At >= cutoff)
            .OrderByDescending(v => v.At)
            .Select(v => v.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    /// <summary>Was there any activity strictly after <paramref name="since"/>? Drives the tick's activity gate.</summary>
    public bool AnyActivitySince(DateTimeOffset since) => _seen.Values.Any(v => v.At > since);

    private void Prune()
    {
        var cutoff = _clock() - _ttl;
        foreach (var kv in _seen)
        {
            if (kv.Value.At < cutoff)
            {
                _seen.TryRemove(kv.Key, out _);
            }
        }
    }
}
