using DiscordSky.Bot.Configuration;

namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// The pure deterministic half of the tick: decay mood toward baseline (with inertia) and age and cap the
/// ranks. No LLM, so the world always moves and mood stays reliable and testable even when the model call is
/// disabled or fails. Live appraisal nudges the mood immediately elsewhere (see EmpireStateStore.ApplyMoodDelta);
/// the LLM body rewrite is layered on top by the tick service through <see cref="MergeRankOps"/>.
/// </summary>
public static class EmpireTick
{
    public static (Mood Mood, IReadOnlyList<Rank> Ranks) Advance(EmpireState state, EmpireStateOptions options)
    {
        var mood = EmpireMood.Decay(state.Mood, options.BaselineValence, options.BaselineArousal, options.MoodRetain);

        var ranks = new List<Rank>(state.Ranks.Count);
        foreach (var r in state.Ranks)
        {
            var aged = r with { IdleTicks = r.IdleTicks + 1 };
            if (aged.IdleTicks <= options.RankIdleTicksMax)
            {
                ranks.Add(aged);
            }
        }

        return (mood, CapRanks(ranks, options));
    }

    /// <summary>Merges the LLM's rank ops into the aged ranks (upsert by name, reset idle), then re-caps.</summary>
    public static IReadOnlyList<Rank> MergeRankOps(
        IReadOnlyList<Rank> ranks, IReadOnlyList<Rank>? ops, EmpireStateOptions options)
    {
        if (ops is null || ops.Count == 0) return ranks;

        var merged = ranks.ToList();
        foreach (var op in ops)
        {
            var idx = merged.FindIndex(r => string.Equals(r.Name, op.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                merged[idx] = merged[idx] with { Title = op.Title, IdleTicks = 0 };
            }
            else
            {
                merged.Add(op with { IdleTicks = 0 });
            }
        }

        return CapRanks(merged, options);
    }

    private static IReadOnlyList<Rank> CapRanks(List<Rank> ranks, EmpireStateOptions options)
    {
        if (ranks.Count <= options.RanksMax) return ranks;
        // Keep the freshest (lowest idle) when over the cap.
        return ranks.OrderBy(r => r.IdleTicks).Take(options.RanksMax).ToList();
    }
}
