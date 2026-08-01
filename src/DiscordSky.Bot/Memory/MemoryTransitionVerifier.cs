using System.Security.Cryptography;
using System.Text.Json;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Memory;

public sealed record MemoryTransitionPolicy(
    bool EvidenceRequired,
    int MaxMemoriesPerUser,
    IReadOnlyList<string> BanWords,
    double SuppressionOverlapThreshold = 0.3,
    double DuplicateThreshold = 0.7);

public sealed record RejectedMemoryOperation(
    MultiUserMemoryOperation Operation,
    string ReasonCode);

public sealed record MemoryPlan(
    ulong UserId,
    IReadOnlyList<MultiUserMemoryOperation> Accepted,
    IReadOnlyList<RejectedMemoryOperation> Rejected,
    IReadOnlyList<UserMemory> PredictedAfter,
    IReadOnlyDictionary<string, int> Observations)
{
    public bool IsValid => Rejected.Count == 0;
}

public sealed class MemoryTransitionVerifier
{
    public MemoryPlan BuildPlan(
        ulong userId,
        IReadOnlyList<UserMemory> before,
        IReadOnlyList<MultiUserMemoryOperation> proposals,
        ExtractionWindow window,
        MemoryTransitionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(policy);

        var accepted = new List<MultiUserMemoryOperation>();
        var rejected = new List<(int Order, RejectedMemoryOperation Rejection)>();
        var observations = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<(int Order, MultiUserMemoryOperation Operation)>();
        var windowMessages = window.Messages.ToDictionary(message => message.MessageId);

        void Observe(string reason) => observations[reason] = observations.GetValueOrDefault(reason) + 1;
        void Reject(int order, MultiUserMemoryOperation operation, string reason)
        {
            rejected.Add((order, new RejectedMemoryOperation(operation, reason)));
            Observe(reason);
        }

        for (var index = 0; index < proposals.Count; index++)
        {
            var operation = proposals[index];
            if (operation.UserId != userId)
            {
                Reject(index, operation, "unknown_target_user");
                continue;
            }
            if (!ValidateShape(operation, out var shapeReason))
            {
                Reject(index, operation, shapeReason);
                continue;
            }
            if (ContainsBanWord(operation.Content, policy.BanWords))
            {
                Reject(index, operation, "ban_word");
                continue;
            }
            if (operation.Action is MemoryAction.Save or MemoryAction.Update
                && InstructionShapePolicy.IsInstructionShaped(operation.Content))
            {
                Reject(index, operation, "instruction_shape");
                continue;
            }

            var evidence = operation.EvidenceMessageIds?.Distinct().ToArray() ?? Array.Empty<ulong>();
            var missingEvidence = RequiresEvidence(operation.Action) && evidence.Length == 0;
            var invalidEvidence = evidence.Where(id => !windowMessages.ContainsKey(id)).ToArray();
            if (missingEvidence) Observe("missing_evidence");
            if (invalidEvidence.Length > 0) Observe("invalid_evidence");
            if (evidence.Length > 0
                && evidence.Where(windowMessages.ContainsKey).All(id => windowMessages[id].AuthorId != userId))
            {
                Observe("evidence_other_participant");
            }
            if (policy.EvidenceRequired && missingEvidence)
            {
                Reject(index, operation, "evidence_required");
                continue;
            }
            if (policy.EvidenceRequired && invalidEvidence.Length > 0)
            {
                Reject(index, operation, "evidence_outside_window");
                continue;
            }

            candidates.Add((index, operation));
        }

        var working = before.ToList();
        var acceptedForgetIndices = new SortedSet<int>();
        foreach (var item in candidates
                     .Where(item => item.Operation.Action == MemoryAction.Forget)
                     .OrderByDescending(item => item.Operation.MemoryIndex)
                     .ThenBy(item => item.Order))
        {
            var operation = item.Operation;
            var index = operation.MemoryIndex!.Value;
            if (index < 0 || index >= before.Count)
            {
                Reject(item.Order, operation, "invalid_index");
                continue;
            }
            if (!acceptedForgetIndices.Add(index))
            {
                Reject(item.Order, operation, "conflicting_index");
                continue;
            }
            working.RemoveAt(index);
            accepted.Add(operation);
        }

        var updatedIndices = new HashSet<int>();
        foreach (var item in candidates
                     .Where(item => item.Operation.Action == MemoryAction.Update)
                     .OrderBy(item => item.Order))
        {
            var operation = item.Operation;
            var originalIndex = operation.MemoryIndex!.Value;
            if (originalIndex < 0 || originalIndex >= before.Count)
            {
                Reject(item.Order, operation, "invalid_index");
                continue;
            }
            if (acceptedForgetIndices.Contains(originalIndex))
            {
                Reject(item.Order, operation, "index_forgotten_in_batch");
                continue;
            }
            if (!updatedIndices.Add(originalIndex))
            {
                Reject(item.Order, operation, "conflicting_index");
                continue;
            }

            var adjustedIndex = originalIndex - acceptedForgetIndices.Count(index => index < originalIndex);
            var current = working[adjustedIndex];
            working[adjustedIndex] = current with
            {
                Content = operation.Content!,
                Context = operation.Context ?? string.Empty,
                LastReferencedAt = window.CapturedAt,
                Provenance = BuildProvenance(operation, window),
            };
            accepted.Add(operation);
        }

        foreach (var item in candidates
                     .Where(item => item.Operation.Action == MemoryAction.Save)
                     .OrderBy(item => item.Order))
        {
            var operation = item.Operation;
            if (IsDuplicate(operation.Content!, working, policy.DuplicateThreshold))
            {
                Reject(item.Order, operation, "duplicate");
                continue;
            }

            EvictAtCap(working, Math.Max(1, policy.MaxMemoriesPerUser));
            working.Add(new UserMemory(
                operation.Content!,
                operation.Context ?? string.Empty,
                window.CapturedAt,
                window.CapturedAt,
                0,
                operation.Kind ?? MemoryKind.Factual,
                operation.Topics,
                Importance: operation.Importance,
                Provenance: BuildProvenance(operation, window),
                MemoryId: MemoryIdentity.FromOperation(window.OperationId, userId, item.Order)));
            accepted.Add(operation);
        }

        foreach (var item in candidates
                     .Where(item => item.Operation.Action == MemoryAction.Suppress)
                     .OrderBy(item => item.Order))
        {
            ApplySuppression(working, item.Operation, window, policy.SuppressionOverlapThreshold);
            accepted.Add(item.Operation);
        }

        return new MemoryPlan(
            userId,
            Array.AsReadOnly(accepted.ToArray()),
            Array.AsReadOnly(rejected.OrderBy(item => item.Order).Select(item => item.Rejection).ToArray()),
            Array.AsReadOnly(working.ToArray()),
            observations);
    }

    public static string ComputeBehavioralStateDigest(IReadOnlyList<UserMemory> memories)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var memory in memories)
            {
                writer.WriteStartObject();
                writer.WriteString("content", memory.Content);
                writer.WriteString("context", memory.Context);
                writer.WriteString("kind", memory.Kind.ToString());
                writer.WriteBoolean("superseded", memory.Superseded);
                writer.WriteNumber("reference_count", memory.ReferenceCount);
                if (memory.Importance.HasValue) writer.WriteNumber("importance", memory.Importance.Value);
                writer.WriteStartArray("topics");
                foreach (var topic in memory.Topics ?? Array.Empty<string>()) writer.WriteStringValue(topic);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static bool ValidateShape(MultiUserMemoryOperation operation, out string reason)
    {
        if (operation.Action is MemoryAction.Save or MemoryAction.Update
            && string.IsNullOrWhiteSpace(operation.Content))
        {
            reason = operation.Action == MemoryAction.Save ? "invalid_save" : "invalid_update";
            return false;
        }
        if (operation.Action is MemoryAction.Update or MemoryAction.Forget && !operation.MemoryIndex.HasValue)
        {
            reason = "missing_index";
            return false;
        }
        if (operation.Action == MemoryAction.Suppress && string.IsNullOrWhiteSpace(operation.Content))
        {
            reason = "invalid_suppression";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool RequiresEvidence(MemoryAction action) =>
        action is MemoryAction.Save or MemoryAction.Update or MemoryAction.Suppress;

    private static bool ContainsBanWord(string? content, IReadOnlyList<string> banWords) =>
        !string.IsNullOrWhiteSpace(content)
        && banWords.Any(word => content.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static MemoryProvenance BuildProvenance(
        MultiUserMemoryOperation operation,
        ExtractionWindow window)
    {
        var validIds = (operation.EvidenceMessageIds ?? Array.Empty<ulong>())
            .Where(id => window.Messages.Any(message => message.MessageId == id))
            .Distinct()
            .Take(8)
            .ToArray();
        return new MemoryProvenance(
            window.OperationId,
            window.CapturedAt,
            Array.AsReadOnly(validIds));
    }

    private static bool IsDuplicate(
        string candidate,
        IReadOnlyList<UserMemory> memories,
        double threshold)
    {
        if (memories.Any(memory => memory.Content.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return true;
        var candidateTokens = TokenUtilities.ExtractContentTokens(candidate);
        return candidateTokens.Count > 0 && memories.Any(memory =>
            TokenUtilities.Jaccard(candidateTokens, TokenUtilities.ExtractContentTokens(memory.Content)) >= threshold);
    }

    private static void EvictAtCap(List<UserMemory> memories, int cap)
    {
        var nonSuppressed = memories
            .Select((memory, index) => (Memory: memory, Index: index))
            .Where(item => item.Memory.Kind != MemoryKind.Suppressed)
            .ToList();
        if (nonSuppressed.Count < cap) return;
        var lru = nonSuppressed
            .OrderBy(item => item.Memory.LastReferencedAt)
            .ThenBy(item => item.Index)
            .First();
        memories.RemoveAt(lru.Index);
    }

    private static void ApplySuppression(
        List<UserMemory> memories,
        MultiUserMemoryOperation operation,
        ExtractionWindow window,
        double overlapThreshold)
    {
        var normalized = operation.Content!.Trim().ToLowerInvariant();
        var alreadyExists = memories.Any(memory => memory.Kind == MemoryKind.Suppressed
            && (memory.Topics?.Any(topic => topic.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? false));
        if (!alreadyExists)
        {
            memories.Add(new UserMemory(
                normalized,
                "user asked to suppress this topic",
                window.CapturedAt,
                window.CapturedAt,
                0,
                MemoryKind.Suppressed,
                new[] { normalized },
                Provenance: BuildProvenance(operation, window),
                MemoryId: MemoryIdentity.FromOperation(
                    window.OperationId,
                    operation.UserId,
                    operation.MemoryIndex ?? int.MaxValue)));
        }

        var suppressedTopics = memories
            .Where(memory => memory.Kind == MemoryKind.Suppressed)
            .SelectMany(memory => memory.Topics ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppressedTokens = memories
            .Where(memory => memory.Kind == MemoryKind.Suppressed)
            .Select(memory => TokenUtilities.ExtractContentTokens(memory.Content))
            .Where(tokens => tokens.Count > 0)
            .ToList();
        for (var index = 0; index < memories.Count; index++)
        {
            var memory = memories[index];
            if (memory.Kind == MemoryKind.Suppressed || memory.Superseded) continue;
            if (MemoryFilter.IsBlockedBySuppression(
                    memory,
                    suppressedTopics,
                    suppressedTokens,
                    overlapThreshold))
            {
                memories[index] = memory with { Superseded = true };
            }
        }
    }
}