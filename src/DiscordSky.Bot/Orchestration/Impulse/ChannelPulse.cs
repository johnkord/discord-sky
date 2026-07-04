using System.Collections.Concurrent;

namespace DiscordSky.Bot.Orchestration.Impulse;

/// <summary>A point-in-time read of a channel's activity, for the cold-open never-into-silence gate.</summary>
public sealed record ChannelPulseSnapshot(
    DateTimeOffset? LastHumanAt,
    DateTimeOffset? LastBotAt,
    DateTimeOffset? LastTypingAt,
    int DistinctHumansInWindow);

/// <summary>
/// Per-channel activity tracker: the signal the cold-open gate reads so the bot only ever speaks into a LIVE,
/// recently-active channel during a lull, never into silence. Separate from the global, user-keyed
/// <see cref="Empire.RecentParticipants"/> because cold opens need per-channel recency and a per-channel
/// distinct-human count. In-memory, bounded, clock-injectable. Updated from the message path; read by the
/// cold-open service.
/// </summary>
public sealed class ChannelPulseTracker
{
    private sealed class ChannelState
    {
        public ConcurrentDictionary<ulong, DateTimeOffset> Humans { get; } = new();
        public DateTimeOffset? LastHumanAt;
        public DateTimeOffset? LastBotAt;
        public DateTimeOffset? LastTypingAt;
    }

    private const int MaxHumansPerChannel = 64;

    private readonly ConcurrentDictionary<ulong, ChannelState> _channels = new();
    private readonly TimeSpan _humanRetention;
    private readonly Func<DateTimeOffset> _clock;

    public ChannelPulseTracker(TimeSpan? humanRetention = null, Func<DateTimeOffset>? clock = null)
    {
        _humanRetention = humanRetention is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromHours(1);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void RecordHuman(ulong channelId, ulong userId, DateTimeOffset at)
    {
        var state = _channels.GetOrAdd(channelId, _ => new ChannelState());
        state.LastHumanAt = Later(state.LastHumanAt, at);
        state.Humans[userId] = at;
        PruneHumans(state);
    }

    public void RecordBot(ulong channelId, DateTimeOffset at)
    {
        var state = _channels.GetOrAdd(channelId, _ => new ChannelState());
        state.LastBotAt = Later(state.LastBotAt, at);
    }

    public void RecordTyping(ulong channelId, DateTimeOffset at)
    {
        var state = _channels.GetOrAdd(channelId, _ => new ChannelState());
        state.LastTypingAt = Later(state.LastTypingAt, at);
    }

    /// <summary>Snapshot of the channel, counting distinct humans active within <paramref name="window"/>. Null if unseen.</summary>
    public ChannelPulseSnapshot? Snapshot(ulong channelId, TimeSpan window)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return null;
        var cutoff = _clock() - window;
        var distinct = state.Humans.Values.Count(at => at >= cutoff);
        return new ChannelPulseSnapshot(state.LastHumanAt, state.LastBotAt, state.LastTypingAt, distinct);
    }

    private void PruneHumans(ChannelState state)
    {
        var cutoff = _clock() - _humanRetention;
        foreach (var kv in state.Humans)
        {
            if (kv.Value < cutoff) state.Humans.TryRemove(kv.Key, out _);
        }
        if (state.Humans.Count > MaxHumansPerChannel)
        {
            foreach (var kv in state.Humans.OrderBy(k => k.Value).Take(state.Humans.Count - MaxHumansPerChannel))
            {
                state.Humans.TryRemove(kv.Key, out _);
            }
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? existing, DateTimeOffset candidate)
        => existing is { } x && x > candidate ? x : candidate;
}
