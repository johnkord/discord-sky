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
}
