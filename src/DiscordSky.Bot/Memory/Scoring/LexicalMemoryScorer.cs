using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory.Scoring;

/// <summary>
/// Cheap lexical relevance scorer: Jaccard overlap between the user's recent messages and each memory's content.
/// Applies a hard floor, a confidence-gap rule, per-kind thresholds, and lateral inhibition.
/// See docs/memory_relevance_design.md §6.0, §6.2.
/// </summary>
public sealed class LexicalMemoryScorer : IMemoryScorer
{
    private readonly IOptionsMonitor<MemoryRelevanceOptions> _optionsMonitor;

    public LexicalMemoryScorer(IOptionsMonitor<MemoryRelevanceOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    public MemoryScoringResult Score(IReadOnlyList<UserMemory> memories, IReadOnlyList<string> recentMessages)
    {
        var options = _optionsMonitor.CurrentValue;

        if (memories.Count == 0)
        {
            return new MemoryScoringResult(
                Admitted: Array.Empty<ScoredMemory>(),
                Rejected: Array.Empty<ScoredMemory>(),
                RejectionReason: null,
                TopScore: 0.0,
                ConfidenceRatio: null,
                Considered: 0);
        }

        // Take only the last N user messages.
        var windowSize = Math.Max(1, options.RecentMessageWindow);
        var window = recentMessages
            .Skip(Math.Max(0, recentMessages.Count - windowSize));
        var queryTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in window)
        {
            foreach (var tok in TokenUtilities.ExtractContentTokens(msg))
            {
                queryTokens.Add(tok);
            }
        }

        // Exclude instruction-shaped and Suppressed memories from scoring up front —
        // they must never be admitted ambiently regardless of score.
        var scored = new List<ScoredMemory>(memories.Count);
        for (int i = 0; i < memories.Count; i++)
        {
            var m = memories[i];
            if (m.Kind == MemoryKind.Suppressed || m.Superseded || InstructionShapePolicy.IsInstructionShaped(m.Content))
            {
                scored.Add(new ScoredMemory(i, m, 0.0));
                continue;
            }
            var memTokens = TokenUtilities.ExtractContentTokens(m.Content);
            var score = TokenUtilities.Jaccard(queryTokens, memTokens);
            scored.Add(new ScoredMemory(i, m, score));
        }

        var ranked = scored.OrderByDescending(s => s.Score).ToList();
        var top = ranked[0];
        var runner = ranked.Count > 1 ? ranked[1].Score : 0.0;
        double? confidenceRatio = runner > 0 ? top.Score / runner : null;

        // Hard floor: nothing is even close to relevant.
        if (top.Score < options.HardFloor)
        {
            return new MemoryScoringResult(
                Admitted: Array.Empty<ScoredMemory>(),
                Rejected: ranked,
                RejectionReason: "hard_floor",
                TopScore: top.Score,
                ConfidenceRatio: confidenceRatio,
                Considered: memories.Count);
        }

        // Confidence gap: top memory must dominate runner-up. When both are weak-to-mid, trust neither.
        if (runner > 0 && top.Score < options.ConfidenceGap * runner)
        {
            return new MemoryScoringResult(
                Admitted: Array.Empty<ScoredMemory>(),
                Rejected: ranked,
                RejectionReason: "confidence_gap",
                TopScore: top.Score,
                ConfidenceRatio: confidenceRatio,
                Considered: memories.Count);
        }

        // Greedy admission with lateral inhibition: after picking a memory, down-weight remaining candidates that overlap with it.
        var admitted = new List<ScoredMemory>();
        var remaining = ranked
            .Where(s => s.Score > 0)
            .Select(s => (Scored: s, WorkingScore: s.Score))
            .ToList();

        while (admitted.Count < options.MaxInjectedMemories && remaining.Count > 0)
        {
            var (best, workingScore) = remaining[0];
            var perKindFloor = options.GetKindThreshold(best.Memory.Kind);
            var threshold = Math.Max(options.AdmissionThreshold, perKindFloor);
            if (workingScore < threshold) break;

            admitted.Add(best);
            remaining.RemoveAt(0);

            if (options.LateralInhibition > 0)
            {
                var bestTokens = TokenUtilities.ExtractContentTokens(best.Memory.Content);
                for (int i = 0; i < remaining.Count; i++)
                {
                    var (cand, score) = remaining[i];
                    var candTokens = TokenUtilities.ExtractContentTokens(cand.Memory.Content);
                    var overlap = TokenUtilities.Jaccard(bestTokens, candTokens);
                    var reduction = 1.0 - (options.LateralInhibition * overlap);
                    remaining[i] = (cand, score * reduction);
                }
                remaining.Sort((a, b) => b.WorkingScore.CompareTo(a.WorkingScore));
            }
        }

        var rejected = ranked.Where(s => !admitted.Contains(s)).ToList();

        return new MemoryScoringResult(
            Admitted: admitted,
            Rejected: rejected,
            RejectionReason: admitted.Count == 0 ? "below_threshold" : null,
            TopScore: top.Score,
            ConfidenceRatio: confidenceRatio,
            Considered: memories.Count);
    }
}
