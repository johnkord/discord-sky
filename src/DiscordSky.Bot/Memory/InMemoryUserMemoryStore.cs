using System.Collections.Concurrent;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory;

/// <summary>
/// In-memory implementation of <see cref="IUserMemoryStore"/>.
/// Memories are lost on restart — suitable for Phase 1 / MVP.
/// </summary>
public sealed class InMemoryUserMemoryStore : IUserMemoryStore
{
    private readonly ConcurrentDictionary<ulong, List<UserMemory>> _store = new();
    private readonly ILogger<InMemoryUserMemoryStore> _logger;
    private readonly int _maxMemoriesPerUser;
    private readonly object _lock = new();

    public InMemoryUserMemoryStore(IOptions<BotOptions> options, ILogger<InMemoryUserMemoryStore> logger)
    {
        _logger = logger;
        _maxMemoriesPerUser = options.Value.MaxMemoriesPerUser;
    }

    public Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default)
    {
        if (_store.TryGetValue(userId, out var memories))
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<UserMemory>>(memories.ToList().AsReadOnly());
            }
        }

        return Task.FromResult<IReadOnlyList<UserMemory>>(Array.Empty<UserMemory>());
    }

    public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var memories = _store.GetOrAdd(userId, _ => new List<UserMemory>());

            // Check for near-duplicate content before saving
            var existing = memories.FindIndex(m =>
                m.Content.Equals(content, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _logger.LogDebug("Duplicate memory for user {UserId}, updating existing at index {Index}", userId, existing);
                memories[existing] = memories[existing] with
                {
                    Content = content,
                    Context = context,
                    LastReferencedAt = DateTimeOffset.UtcNow,
                    ReferenceCount = memories[existing].ReferenceCount + 1
                };
                return Task.CompletedTask;
            }

            // Enforce cap — evict LRU memory if at limit
            if (memories.Count >= _maxMemoriesPerUser)
            {
                var lruIndex = 0;
                var lruTime = DateTimeOffset.MaxValue;
                for (int i = 0; i < memories.Count; i++)
                {
                    if (memories[i].LastReferencedAt < lruTime)
                    {
                        lruTime = memories[i].LastReferencedAt;
                        lruIndex = i;
                    }
                }

                _logger.LogDebug(
                    "User {UserId} at memory cap ({Cap}), evicting LRU memory at index {Index}: \"{Content}\"",
                    userId, _maxMemoriesPerUser, lruIndex, memories[lruIndex].Content);
                memories.RemoveAt(lruIndex);
            }

            var now = DateTimeOffset.UtcNow;
            memories.Add(new UserMemory(content, context, now, now, 0));
            _logger.LogInformation("Saved memory for user {UserId}: \"{Content}\"", userId, content);
        }

        return Task.CompletedTask;
    }

    public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(userId, out var memories) || index < 0 || index >= memories.Count)
            {
                _logger.LogWarning("Cannot update memory at index {Index} for user {UserId}: out of range", index, userId);
                return Task.CompletedTask;
            }

            memories[index] = memories[index] with
            {
                Content = content,
                Context = context,
                LastReferencedAt = DateTimeOffset.UtcNow
            };
            _logger.LogInformation("Updated memory for user {UserId} at index {Index}: \"{Content}\"", userId, index, content);
        }

        return Task.CompletedTask;
    }

    public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(userId, out var memories) || index < 0 || index >= memories.Count)
            {
                _logger.LogWarning("Cannot forget memory at index {Index} for user {UserId}: out of range", index, userId);
                return Task.CompletedTask;
            }

            var removed = memories[index];
            memories.RemoveAt(index);
            _logger.LogInformation("Forgot memory for user {UserId} at index {Index}: \"{Content}\"", userId, index, removed.Content);
        }

        return Task.CompletedTask;
    }

    public Task ForgetAllAsync(ulong userId, CancellationToken ct = default)
    {
        if (_store.TryRemove(userId, out _))
        {
            _logger.LogInformation("Forgot all memories for user {UserId}", userId);
        }

        return Task.CompletedTask;
    }

    public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default)
    {
        // No-op: we no longer bulk-touch all memories on every invocation because
        // doing so defeats LRU eviction (all memories end up with the same LastReferencedAt).
        // Instead, individual memories should be touched when they are actually used in a response.
        return Task.CompletedTask;
    }

    public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var store = _store.GetOrAdd(userId, _ => new List<UserMemory>());
            store.Clear();
            store.AddRange(memories);
            _logger.LogInformation("Replaced all memories for user {UserId} with {Count} consolidated memories", userId, memories.Count);
        }

        return Task.CompletedTask;
    }
}
