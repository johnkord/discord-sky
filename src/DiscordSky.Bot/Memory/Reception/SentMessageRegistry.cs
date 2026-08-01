using System.Collections.Concurrent;

namespace DiscordSky.Bot.Memory.Reception;

/// <summary>Metadata for one message sent by the bot, shared by every outbound feature.</summary>
public sealed record SentMessageInfo(
    string Persona,
    string Source,
    DateTimeOffset CreatedAt,
    ulong? TriggerMessageId = null,
    string? EpisodeId = null,
    ulong? ReplyTargetMessageId = null);

/// <summary>
/// Shared, bounded registry of messages sent by the bot. Reaction reception, reply-chain persona continuity,
/// and feature attribution all depend on knowing which Discord message IDs are ours. Keeping this registry
/// outside <c>DiscordBotService</c> lets background senders such as cold opens participate in the same feedback
/// loop instead of silently bypassing reaction logging and proven bits.
/// </summary>
public sealed class SentMessageRegistry
{
    private const int MaxEntries = 1_000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<ulong, SentMessageInfo> _messages = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _evictionLock = new();

    public SentMessageRegistry(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public void Register(
        ulong messageId,
        string persona,
        string source,
        ulong? triggerMessageId = null,
        string? episodeId = null,
        ulong? replyTargetMessageId = null)
    {
        _messages[messageId] = new SentMessageInfo(
            string.IsNullOrWhiteSpace(persona) ? "unknown" : persona,
            string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            _clock(),
            triggerMessageId,
            episodeId,
            replyTargetMessageId);
        EvictIfNeeded();
    }

    public bool TryGet(ulong messageId, out SentMessageInfo info) =>
        _messages.TryGetValue(messageId, out info!);

    public int Count => _messages.Count;

    private void EvictIfNeeded()
    {
        if (_messages.Count <= MaxEntries || !Monitor.TryEnter(_evictionLock)) return;
        try
        {
            var cutoff = _clock() - MaxAge;
            foreach (var (id, info) in _messages)
            {
                if (info.CreatedAt < cutoff) _messages.TryRemove(id, out _);
            }

            if (_messages.Count <= MaxEntries) return;
            foreach (var id in _messages
                         .OrderBy(kvp => kvp.Value.CreatedAt)
                         .Take(_messages.Count - MaxEntries)
                         .Select(kvp => kvp.Key))
            {
                _messages.TryRemove(id, out _);
            }
        }
        finally
        {
            Monitor.Exit(_evictionLock);
        }
    }
}
