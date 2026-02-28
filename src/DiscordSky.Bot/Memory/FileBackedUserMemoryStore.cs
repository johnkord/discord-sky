using System.Collections.Concurrent;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory;

/// <summary>
/// File-backed implementation of <see cref="IUserMemoryStore"/>.
/// Stores one JSON file per user in a configurable data directory.
/// Mutations are write-through (debounced) so memories survive restarts.
/// Thread-safe via per-user async locking (SemaphoreSlim).
/// </summary>
public sealed class FileBackedUserMemoryStore : IUserMemoryStore, IDisposable
{
    private readonly ConcurrentDictionary<ulong, List<UserMemory>> _cache = new();
    private readonly ConcurrentDictionary<ulong, bool> _dirty = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _userLocks = new();
    private readonly ILogger<FileBackedUserMemoryStore> _logger;
    private readonly int _maxMemoriesPerUser;
    private readonly string _dataDirectory;
    private readonly Timer _flushTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public FileBackedUserMemoryStore(IOptions<BotOptions> options, ILogger<FileBackedUserMemoryStore> logger)
    {
        _logger = logger;
        _maxMemoriesPerUser = options.Value.MaxMemoriesPerUser;
        _dataDirectory = options.Value.MemoryDataPath;

        Directory.CreateDirectory(_dataDirectory);

        // Flush dirty files every 60 seconds
        _flushTimer = new Timer(_ => FlushAll(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        _logger.LogInformation("File-backed memory store initialized at \"{Path}\"", _dataDirectory);
    }

    private SemaphoreSlim GetUserLock(ulong userId) =>
        _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    public async Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default)
    {
        var userLock = GetUserLock(userId);
        await userLock.WaitAsync(ct);
        try
        {
            var memories = await GetOrLoadAsync(userId, ct);
            return memories.ToList().AsReadOnly();
        }
        finally
        {
            userLock.Release();
        }
    }

    public async Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default)
    {
        var userLock = GetUserLock(userId);
        await userLock.WaitAsync(ct);
        try
        {
            var memories = await GetOrLoadAsync(userId, ct);

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
                MarkDirty(userId);
                return;
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
            MarkDirty(userId);
            _logger.LogInformation("Saved memory for user {UserId}: \"{Content}\"", userId, content);
        }
        finally
        {
            userLock.Release();
        }
    }

    public async Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default)
    {
        var userLock = GetUserLock(userId);
        await userLock.WaitAsync(ct);
        try
        {
            var memories = await GetOrLoadAsync(userId, ct);
            if (index < 0 || index >= memories.Count)
            {
                _logger.LogWarning("Cannot update memory at index {Index} for user {UserId}: out of range", index, userId);
                return;
            }

            memories[index] = memories[index] with
            {
                Content = content,
                Context = context,
                LastReferencedAt = DateTimeOffset.UtcNow
            };
            MarkDirty(userId);
            _logger.LogInformation("Updated memory for user {UserId} at index {Index}: \"{Content}\"", userId, index, content);
        }
        finally
        {
            userLock.Release();
        }
    }

    public async Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default)
    {
        var userLock = GetUserLock(userId);
        await userLock.WaitAsync(ct);
        try
        {
            var memories = await GetOrLoadAsync(userId, ct);
            if (index < 0 || index >= memories.Count)
            {
                _logger.LogWarning("Cannot forget memory at index {Index} for user {UserId}: out of range", index, userId);
                return;
            }

            var removed = memories[index];
            memories.RemoveAt(index);
            MarkDirty(userId);
            _logger.LogInformation("Forgot memory for user {UserId} at index {Index}: \"{Content}\"", userId, index, removed.Content);
        }
        finally
        {
            userLock.Release();
        }
    }

    public async Task ForgetAllAsync(ulong userId, CancellationToken ct = default)
    {
        var userLock = GetUserLock(userId);
        await userLock.WaitAsync(ct);
        try
        {
            _cache.TryRemove(userId, out _);
            _dirty.TryRemove(userId, out _);

            var filePath = GetFilePath(userId);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogInformation("Forgot all memories for user {UserId} (deleted {File})", userId, filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete memory file for user {UserId}", userId);
            }
        }
        finally
        {
            userLock.Release();
        }
    }

    public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default)
    {
        // No-op: we no longer bulk-touch all memories on every invocation because
        // doing so defeats LRU eviction (all memories end up with the same LastReferencedAt).
        // Instead, individual memories should be touched when they are actually used in a response.
        return Task.CompletedTask;
    }

    public async Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> newMemories, CancellationToken ct = default)
    {
        var userLock = GetUserLock(userId);
        await userLock.WaitAsync(ct);
        try
        {
            var memories = await GetOrLoadAsync(userId, ct);
            memories.Clear();
            memories.AddRange(newMemories);
            MarkDirty(userId);
            _logger.LogInformation("Replaced all memories for user {UserId} with {Count} consolidated memories", userId, newMemories.Count);
        }
        finally
        {
            userLock.Release();
        }
    }

    // --- Internal helpers ---

    /// <summary>
    /// Loads a user's memories from cache or disk. Caller MUST hold the user lock.
    /// </summary>
    private async Task<List<UserMemory>> GetOrLoadAsync(ulong userId, CancellationToken ct)
    {
        if (_cache.TryGetValue(userId, out var cached))
            return cached;

        var filePath = GetFilePath(userId);
        List<UserMemory> memories;

        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, ct);
                memories = JsonSerializer.Deserialize<List<UserMemory>>(json, JsonOptions) ?? [];
                _logger.LogDebug("Loaded {Count} memories for user {UserId} from disk", memories.Count, userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load memory file for user {UserId}; starting fresh", userId);
                memories = [];
            }
        }
        else
        {
            memories = [];
        }

        // Use GetOrAdd to handle concurrent first-access races (should not happen under user lock,
        // but is a safe fallback)
        return _cache.GetOrAdd(userId, memories);
    }

    private string GetFilePath(ulong userId) =>
        Path.Combine(_dataDirectory, $"{userId}.json");

    private void MarkDirty(ulong userId) =>
        _dirty[userId] = true;

    /// <summary>
    /// Flush all dirty user memory files to disk.
    /// Called periodically by the timer and on dispose.
    /// Acquires per-user locks synchronously to get a consistent snapshot.
    /// </summary>
    internal void FlushAll()
    {
        var dirtyUsers = _dirty.Keys.ToList();

        foreach (var userId in dirtyUsers)
        {
            if (!_dirty.TryRemove(userId, out _))
                continue;

            if (!_cache.TryGetValue(userId, out var memories))
                continue;

            var userLock = GetUserLock(userId);
            userLock.Wait();
            try
            {
                var json = JsonSerializer.Serialize(memories, JsonOptions);

                var filePath = GetFilePath(userId);
                // Write to temp file first, then atomic rename for crash safety
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);

                _logger.LogDebug("Flushed {Count} memories for user {UserId} to disk", memories.Count, userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush memories for user {UserId}", userId);
                // Re-mark dirty so we retry on next flush cycle
                MarkDirty(userId);
            }
            finally
            {
                userLock.Release();
            }
        }
    }

    public void Dispose()
    {
        // Wait for any in-flight timer callback to complete before final flush
        using var waitHandle = new ManualResetEvent(false);
        _flushTimer.Dispose(waitHandle);
        waitHandle.WaitOne(TimeSpan.FromSeconds(10));

        FlushAll();

        // Dispose per-user locks
        foreach (var sem in _userLocks.Values)
        {
            sem.Dispose();
        }
        _userLocks.Clear();

        _logger.LogInformation("File-backed memory store disposed; final flush complete");
    }
}
