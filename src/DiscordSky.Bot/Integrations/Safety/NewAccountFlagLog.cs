using System.Collections.Concurrent;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Shared, in-memory record of the accounts the new-account watch has recently flagged to the mods. Written by
/// the message path when it alerts, read by <see cref="BanWatchService"/> when a ban lands, so a ban can be
/// labeled "predicted" (we flagged first) or "missed" (we did not). This is the learn-from-bans measurement
/// substrate: mod bans are the cleanest spam labels on the server, and this turns them into a recall metric.
/// In-memory with a short TTL is enough because mods act within minutes; durable correlation also survives via
/// the two telemetry streams (new_account_flag and ban_observed) on the PVC.
/// </summary>
public sealed class NewAccountFlagLog
{
    private const int MaxEntries = 512;
    private readonly ConcurrentDictionary<ulong, FlagRecord> _flags = new();

    public void Record(ulong userId, DateTimeOffset at, string reason)
    {
        _flags[userId] = new FlagRecord(at, reason);
        if (_flags.Count > MaxEntries)
        {
            Prune(at, TimeSpan.FromHours(24));
        }
    }

    public bool TryGet(ulong userId, out FlagRecord record) => _flags.TryGetValue(userId, out record);

    public bool WasFlaggedWithin(ulong userId, DateTimeOffset now, TimeSpan window)
        => _flags.TryGetValue(userId, out var r) && now - r.At <= window;

    public void Prune(DateTimeOffset now, TimeSpan ttl)
    {
        foreach (var kvp in _flags)
        {
            if (now - kvp.Value.At > ttl)
            {
                _flags.TryRemove(kvp.Key, out _);
            }
        }
    }

    public readonly record struct FlagRecord(DateTimeOffset At, string Reason);
}
