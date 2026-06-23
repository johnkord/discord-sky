using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Memory.Scoring;

public sealed record ScoredMemory(int Index, UserMemory Memory, double Score);

public sealed record MemoryScoringResult(
    IReadOnlyList<ScoredMemory> Admitted,
    IReadOnlyList<ScoredMemory> Rejected,
    string? RejectionReason,
    double TopScore,
    double? ConfidenceRatio,
    int Considered);

public interface IMemoryScorer
{
    /// <summary>
    /// Score and admit/reject memories against the recent conversation window.
    /// <paramref name="recentMessages"/> should already be filtered to the user's own messages in chronological order.
    /// </summary>
    MemoryScoringResult Score(
        IReadOnlyList<UserMemory> memories,
        IReadOnlyList<string> recentMessages);

    /// <summary>
    /// Rank memories for the recall tool. Unlike <see cref="Score"/> (which gates ambient injection),
    /// this never excludes anything: it returns every input memory ordered best-first by a blend of
    /// relevance (BM25 against <paramref name="query"/>), recency (decay on LastReferencedAt vs
    /// <paramref name="asOf"/>), and importance. When <paramref name="query"/> is null/empty, ordering
    /// falls back to recency + importance. See docs/improvement_opportunities_2026-06-10.md F2.
    /// </summary>
    IReadOnlyList<ScoredMemory> RankForRecall(
        IReadOnlyList<UserMemory> memories,
        string? query,
        DateTimeOffset asOf);
}
