using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Memory;

/// <summary>
/// Abstraction for per-user memory storage.
/// Memories are scoped to a Discord user ID and are used to personalize bot responses.
/// </summary>
public interface IUserMemoryStore
{
    Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default);
    Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default);

    /// <summary>
    /// Save with a <see cref="MemoryKind"/> classification, optional topic tags, and optional importance
    /// (1-10 salience used by recall ranking and consolidation). Default implementation falls back to the
    /// 3-arg overload (dropping kind/topics/importance), which keeps existing test doubles functional.
    /// Real stores override to persist the full shape.
    /// </summary>
    Task SaveMemoryAsync(
        ulong userId,
        string content,
        string context,
        MemoryKind kind,
        IReadOnlyList<string>? topics,
        int? importance = null,
        CancellationToken ct = default)
        => SaveMemoryAsync(userId, content, context, ct);

    Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default);
    Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default);
    Task ForgetAllAsync(ulong userId, CancellationToken ct = default);
    Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default);

    /// <summary>
    /// Bump LastReferencedAt and increment ReferenceCount for memories whose content matches one of
    /// <paramref name="contents"/> (case-insensitive). Recall calls this on the notes it actually
    /// surfaces, which restores the recency and frequency signals LRU eviction and recall ranking
    /// depend on. Historically these signals were dead (96/99 notes had ReferenceCount 0).
    /// Default no-op keeps test doubles functional. See docs/improvement_opportunities_2026-06-10.md F4.
    /// </summary>
    Task TouchMemoriesAsync(ulong userId, IReadOnlyList<string> contents, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Replaces all memories for a user with a consolidated set.
    /// Used by memory consolidation to atomically swap in a compressed memory list.
    /// </summary>
    Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default);
}
