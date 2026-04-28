using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory;

/// <summary>
/// Shared read-path helpers for memory stores. Centralising the suppression/admissibility logic here
/// keeps it out of the Discord plumbing (<see cref="IUserMemoryStore"/> consumers) and out of the individual store
/// implementations.
/// </summary>
public static class MemoryFilter
{
    /// <summary>
    /// Filter a raw memory list to the subset admissible for ambient injection:
    /// not <see cref="MemoryKind.Suppressed"/>, not <see cref="MemoryKind.Meta"/>, not superseded,
    /// not matching an active suppression (by topic or token Jaccard), and not instruction-shaped.
    /// </summary>
    public static IReadOnlyList<UserMemory> Admissible(
        IReadOnlyList<UserMemory> all,
        double suppressionOverlapThreshold)
    {
        if (all.Count == 0) return all;

        var suppressions = all.Where(m => m.Kind == MemoryKind.Suppressed).ToList();
        var suppressedTopics = suppressions
            .SelectMany(m => m.Topics ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppressedTokenSets = suppressions
            .Select(m => TokenUtilities.ExtractContentTokens(m.Content))
            .Where(s => s.Count > 0)
            .ToList();

        var result = new List<UserMemory>(all.Count);
        foreach (var m in all)
        {
            if (m.Kind is MemoryKind.Suppressed or MemoryKind.Meta) continue;
            if (m.Superseded) continue;
            if (InstructionShapePolicy.IsInstructionShaped(m.Content)) continue;
            if (IsBlockedBySuppression(m, suppressedTopics, suppressedTokenSets, suppressionOverlapThreshold)) continue;
            result.Add(m);
        }
        return result;
    }

    public static bool IsBlockedBySuppression(
        UserMemory memory,
        HashSet<string> suppressedTopics,
        IReadOnlyList<HashSet<string>> suppressedTokenSets,
        double overlapThreshold)
    {
        if (memory.Topics is { Count: > 0 })
        {
            foreach (var t in memory.Topics)
            {
                if (suppressedTopics.Contains(t)) return true;
            }
        }
        if (suppressedTokenSets.Count == 0) return false;
        var memTokens = TokenUtilities.ExtractContentTokens(memory.Content);
        foreach (var s in suppressedTokenSets)
        {
            if (TokenUtilities.Jaccard(memTokens, s) >= overlapThreshold) return true;
        }
        return false;
    }
}

/// <summary>
/// Extension-method surface on <see cref="IUserMemoryStore"/> to avoid enlarging the interface and
/// breaking mocks/tests. Delegates back to <see cref="IUserMemoryStore.GetMemoriesAsync"/>.
/// </summary>
public static class UserMemoryStoreExtensions
{
    public static async Task<IReadOnlyList<UserMemory>> GetAdmissibleMemoriesAsync(
        this IUserMemoryStore store,
        ulong userId,
        IOptionsMonitor<MemoryRelevanceOptions> optionsMonitor,
        CancellationToken ct = default)
    {
        var all = await store.GetMemoriesAsync(userId, ct).ConfigureAwait(false);
        return MemoryFilter.Admissible(all, optionsMonitor.CurrentValue.SuppressionOverlapThreshold);
    }

    /// <summary>
    /// Record a new <see cref="MemoryKind.Suppressed"/> entry for a topic the user has asked the bot to stop bringing up.
    /// Idempotent: existing Suppressed records with the same normalised topic are not duplicated.
    /// Also marks any existing non-suppressed memory that the new suppression would block as <c>Superseded=true</c>.
    /// Performs a single <see cref="IUserMemoryStore.ReplaceAllMemoriesAsync"/> write for both operations.
    /// </summary>
    public static async Task SuppressTopicAsync(
        this IUserMemoryStore store,
        ulong userId,
        string topic,
        IOptionsMonitor<MemoryRelevanceOptions> optionsMonitor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        var normalized = topic.Trim().ToLowerInvariant();

        var existing = await store.GetMemoriesAsync(userId, ct).ConfigureAwait(false);

        // Idempotency: skip the write entirely if an equivalent suppression already exists AND
        // no existing factual memory needs to be newly superseded by it.
        var alreadyHave = existing.Any(m => m.Kind == MemoryKind.Suppressed
            && (m.Topics?.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)) ?? false));

        // Build the post-state in memory, then write it in one call.
        var next = existing.ToList();
        if (!alreadyHave)
        {
            var now = DateTimeOffset.UtcNow;
            next.Add(new UserMemory(
                Content: normalized,
                Context: "user asked to suppress this topic",
                CreatedAt: now,
                LastReferencedAt: now,
                ReferenceCount: 0,
                Kind: MemoryKind.Suppressed,
                Topics: new[] { normalized }));
        }

        // Compute suppression index over the new state (includes the suppression we just appended).
        var suppressedTopics = next
            .Where(m => m.Kind == MemoryKind.Suppressed)
            .SelectMany(m => m.Topics ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppressedTokenSets = next
            .Where(m => m.Kind == MemoryKind.Suppressed)
            .Select(m => TokenUtilities.ExtractContentTokens(m.Content))
            .Where(s => s.Count > 0)
            .ToList();
        var overlap = optionsMonitor.CurrentValue.SuppressionOverlapThreshold;

        var rewritten = next.Select(m =>
        {
            if (m.Kind == MemoryKind.Suppressed || m.Superseded) return m;
            if (MemoryFilter.IsBlockedBySuppression(m, suppressedTopics, suppressedTokenSets, overlap))
            {
                return m with { Superseded = true };
            }
            return m;
        }).ToList();

        // Only write if something actually changed — avoids spurious flushes on no-op repeated calls.
        if (!rewritten.SequenceEqual(existing))
        {
            await store.ReplaceAllMemoriesAsync(userId, rewritten, ct).ConfigureAwait(false);
        }
    }
}
