using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Memory;

internal sealed record ConsolidatedMemoryProposal(
    string Content,
    string Context,
    IReadOnlyList<string> SourceMemoryIds);

internal sealed record MemoryConsolidationPlan(
    IReadOnlyList<UserMemory>? Memories,
    string? RejectionReason)
{
    public bool IsValid => Memories is not null && RejectionReason is null;

    public static MemoryConsolidationPlan Accepted(IReadOnlyList<UserMemory> memories) =>
        new(memories, null);

    public static MemoryConsolidationPlan Rejected(string reason) =>
        new(null, reason);
}

internal sealed record MemoryConsolidationResult(
    IReadOnlyList<UserMemory> Memories,
    string OperationId,
    string Outcome,
    string? ReasonCode = null);

internal static class MemoryConsolidationPlanner
{
    private const int MaxTopics = 8;
    private const int MaxEvidenceIds = 32;

    public static IReadOnlyList<UserMemory> ModelCandidates(IReadOnlyList<UserMemory> memories) =>
        memories.Where(memory => memory.Kind is not MemoryKind.Meta and not MemoryKind.Suppressed).ToArray();

    public static int ModelTarget(IReadOnlyList<UserMemory> memories, int targetCount)
    {
        var protectedCounted = memories.Count(memory => memory.Kind == MemoryKind.Meta);
        return Math.Max(0, targetCount - protectedCounted);
    }

    public static MemoryConsolidationPlan Build(
        ulong userId,
        IReadOnlyList<UserMemory> existing,
        IReadOnlyList<ConsolidatedMemoryProposal> proposals,
        int targetCount,
        string operationId,
        DateTimeOffset capturedAt)
    {
        if (proposals.Count == 0) return MemoryConsolidationPlan.Rejected("empty_proposals");

        var byId = new Dictionary<string, (UserMemory Memory, int Index)>(StringComparer.Ordinal);
        for (var index = 0; index < existing.Count; index++)
        {
            var memory = existing[index];
            if (string.IsNullOrWhiteSpace(memory.MemoryId))
            {
                return MemoryConsolidationPlan.Rejected("source_missing_memory_id");
            }
            if (!byId.TryAdd(memory.MemoryId, (memory, index)))
            {
                return MemoryConsolidationPlan.Rejected("duplicate_memory_id");
            }
        }

        var modelTarget = ModelTarget(existing, targetCount);
        if (proposals.Count > modelTarget)
        {
            return MemoryConsolidationPlan.Rejected("output_over_target");
        }

        var usedSources = new HashSet<string>(StringComparer.Ordinal);
        var rewritten = new List<(int Order, UserMemory Memory)>();
        for (var proposalIndex = 0; proposalIndex < proposals.Count; proposalIndex++)
        {
            var proposal = proposals[proposalIndex];
            if (string.IsNullOrWhiteSpace(proposal.Content))
            {
                return MemoryConsolidationPlan.Rejected("missing_content");
            }

            var sourceIds = proposal.SourceMemoryIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sourceIds.Length == 0)
            {
                return MemoryConsolidationPlan.Rejected("missing_source_ids");
            }
            if (sourceIds.Any(id => !byId.ContainsKey(id)))
            {
                return MemoryConsolidationPlan.Rejected("unknown_source_id");
            }
            if (sourceIds.Any(id => byId[id].Memory.Kind is MemoryKind.Meta or MemoryKind.Suppressed))
            {
                return MemoryConsolidationPlan.Rejected("protected_source_claimed");
            }
            if (sourceIds.Any(id => !usedSources.Add(id)))
            {
                return MemoryConsolidationPlan.Rejected("source_reused");
            }

            var sources = sourceIds.Select(id => byId[id]).ToArray();
            var kinds = sources.Select(source => source.Memory.Kind).Distinct().ToArray();
            if (kinds.Length != 1)
            {
                return MemoryConsolidationPlan.Rejected("cross_kind_merge");
            }
            var supersededValues = sources.Select(source => source.Memory.Superseded).Distinct().ToArray();
            if (supersededValues.Length != 1)
            {
                return MemoryConsolidationPlan.Rejected("cross_status_merge");
            }

            var evidenceIds = sources
                .SelectMany(source => source.Memory.Provenance?.EvidenceMessageIds ?? Array.Empty<ulong>())
                .Distinct()
                .Take(MaxEvidenceIds)
                .ToArray();
            var referenceCount = sources.Aggregate(
                0,
                (total, source) => total > int.MaxValue - source.Memory.ReferenceCount
                    ? int.MaxValue
                    : total + source.Memory.ReferenceCount);
            var memoryId = sources.Length == 1
                ? sources[0].Memory.MemoryId!
                : MemoryIdentity.FromOperation(operationId, userId, proposalIndex);
            var merged = new UserMemory(
                Content: proposal.Content.Trim(),
                Context: proposal.Context?.Trim() ?? string.Empty,
                CreatedAt: sources.Min(source => source.Memory.CreatedAt),
                LastReferencedAt: sources.Max(source => source.Memory.LastReferencedAt),
                ReferenceCount: referenceCount,
                Kind: kinds[0],
                Topics: sources
                    .SelectMany(source => source.Memory.Topics ?? Array.Empty<string>())
                    .Where(topic => !string.IsNullOrWhiteSpace(topic))
                    .Select(topic => topic.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxTopics)
                    .ToArray(),
                Superseded: supersededValues[0],
                Importance: sources.Max(source => source.Memory.Importance),
                Provenance: new MemoryProvenance(
                    operationId,
                    capturedAt,
                    evidenceIds,
                    sourceIds,
                    "consolidation"),
                MemoryId: memoryId);
            rewritten.Add((sources.Min(source => source.Index), merged));
        }

        var requiredSourceIds = existing
            .Where(memory => memory.Kind is not MemoryKind.Meta and not MemoryKind.Suppressed)
            .Where(memory => memory.Kind == MemoryKind.Running || (memory.Importance ?? 0) >= 8)
            .Select(memory => memory.MemoryId!)
            .ToArray();
        if (requiredSourceIds.Any(id => !usedSources.Contains(id)))
        {
            return MemoryConsolidationPlan.Rejected("high_value_source_dropped");
        }

        var protectedMemories = existing
            .Select((memory, index) => (Memory: memory, Index: index))
            .Where(item => item.Memory.Kind is MemoryKind.Meta or MemoryKind.Suppressed)
            .Select(item => (Order: item.Index, Memory: item.Memory));
        var result = rewritten
            .Concat(protectedMemories)
            .OrderBy(item => item.Order)
            .Select(item => item.Memory)
            .ToArray();
        var counted = result.Count(memory => memory.Kind != MemoryKind.Suppressed);
        if (counted > targetCount)
        {
            return MemoryConsolidationPlan.Rejected("result_over_target");
        }

        return MemoryConsolidationPlan.Accepted(result);
    }

    public static List<UserMemory> DeterministicFallback(
        IReadOnlyList<UserMemory> memories,
        int targetCount)
    {
        targetCount = Math.Max(1, targetCount);
        var meta = memories.Where(memory => memory.Kind == MemoryKind.Meta).ToArray();
        var suppressed = memories.Where(memory => memory.Kind == MemoryKind.Suppressed).ToArray();
        var keepCount = Math.Max(0, targetCount - meta.Length);
        var selected = memories
            .Select((memory, index) => (Memory: memory, Index: index))
            .Where(item => item.Memory.Kind is not MemoryKind.Meta and not MemoryKind.Suppressed)
            .OrderByDescending(item => item.Memory.Kind == MemoryKind.Running)
            .ThenByDescending(item => item.Memory.Importance ?? 0)
            .ThenByDescending(item => item.Memory.LastReferencedAt)
            .ThenByDescending(item => item.Memory.CreatedAt)
            .ThenBy(item => item.Index)
            .Take(keepCount)
            .Select(item => item.Memory.MemoryId)
            .ToHashSet(StringComparer.Ordinal);

        return memories
            .Where(memory => memory.Kind is MemoryKind.Meta or MemoryKind.Suppressed
                || (memory.MemoryId is not null && selected.Contains(memory.MemoryId)))
            .ToList();
    }
}