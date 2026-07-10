using System.Collections.Concurrent;

namespace DiscordSky.Bot.Orchestration.Impulse;

/// <summary>
/// Non-queuing per-channel lease for ambient replies. Concurrent message handlers must not each generate a reply
/// to the same burst, and stale candidates must not queue behind a slow LLM call. Explicit commands, mentions,
/// and direct replies do not use this coordinator.
/// </summary>
public sealed class AmbientChannelCoordinator
{
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _leases = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastSent = new();

    public bool TryAcquire(
        ulong channelId,
        DateTimeOffset now,
        TimeSpan quietPeriod,
        out AmbientChannelLease? lease,
        out string? veto)
    {
        if (_lastSent.TryGetValue(channelId, out var sent) && now - sent < quietPeriod)
        {
            lease = null;
            veto = "quiet";
            return false;
        }

        var semaphore = _leases.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        if (!semaphore.Wait(0))
        {
            lease = null;
            veto = "inflight";
            return false;
        }

        // Close the race where another lease sent between our first check and acquisition.
        if (_lastSent.TryGetValue(channelId, out sent) && now - sent < quietPeriod)
        {
            semaphore.Release();
            lease = null;
            veto = "quiet";
            return false;
        }

        lease = new AmbientChannelLease(semaphore, sentAt => _lastSent[channelId] = sentAt);
        veto = null;
        return true;
    }

}

public sealed class AmbientChannelLease : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly Action<DateTimeOffset> _markSent;
    private int _disposed;

    internal AmbientChannelLease(SemaphoreSlim semaphore, Action<DateTimeOffset> markSent)
    {
        _semaphore = semaphore;
        _markSent = markSent;
    }

    public void MarkSent(DateTimeOffset at) => _markSent(at);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _semaphore.Release();
    }
}
