namespace DiscordSky.Bot.Models.Orchestration;

public sealed record UserMemoryCountDelta(ulong UserId, int Before, int After);

public sealed record MemoryApplySummary(
    int Proposed,
    int Applied,
    int Rejected,
    IReadOnlyDictionary<MemoryAction, int> ProposedByAction,
    IReadOnlyDictionary<MemoryAction, int> AppliedByAction,
    IReadOnlyDictionary<string, int> RejectedByReason,
    IReadOnlyList<UserMemoryCountDelta> UserDeltas)
{
    public static MemoryApplySummary Empty { get; } = new(
        0,
        0,
        0,
        new Dictionary<MemoryAction, int>(),
        new Dictionary<MemoryAction, int>(),
        new Dictionary<string, int>(StringComparer.Ordinal),
        Array.Empty<UserMemoryCountDelta>());

    public MemoryApplySummary WithRejectedOperations(
        IReadOnlyList<MultiUserMemoryOperation> operations,
        string reason)
    {
        if (operations.Count == 0) return this;

        var proposedByAction = ProposedByAction.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var operation in operations)
        {
            proposedByAction[operation.Action] = proposedByAction.GetValueOrDefault(operation.Action) + 1;
        }

        var rejectedByReason = RejectedByReason.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        rejectedByReason[reason] = rejectedByReason.GetValueOrDefault(reason) + operations.Count;

        return this with
        {
            Proposed = Proposed + operations.Count,
            Rejected = Rejected + operations.Count,
            ProposedByAction = proposedByAction,
            RejectedByReason = rejectedByReason,
        };
    }
}