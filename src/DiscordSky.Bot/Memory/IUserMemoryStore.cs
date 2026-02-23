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
    Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default);
    Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default);
    Task ForgetAllAsync(ulong userId, CancellationToken ct = default);
    Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default);

    /// <summary>
    /// Replaces all memories for a user with a consolidated set.
    /// Used by memory consolidation to atomically swap in a compressed memory list.
    /// </summary>
    Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default);
}
