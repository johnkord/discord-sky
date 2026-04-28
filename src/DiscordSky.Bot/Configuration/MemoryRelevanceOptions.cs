using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Configuration;

public enum MemoryRelevanceMode
{
    /// <summary>Inject all memories. Legacy behaviour.</summary>
    Off,
    /// <summary>Score memories but still inject all; log what would have been admitted.</summary>
    ShadowOnly,
    /// <summary>Only inject memories that pass the lexical-relevance gate.</summary>
    Lexical,
}

public sealed class MemoryRelevanceOptions
{
    public const string SectionName = "MemoryRelevance";

    public MemoryRelevanceMode Mode { get; init; } = MemoryRelevanceMode.ShadowOnly;

    /// <summary>Minimum top-score to admit any memory. Below this, reject everything.</summary>
    public double HardFloor { get; init; } = 0.15;

    /// <summary>Minimum score for an individual memory to be admitted once the floor passes.</summary>
    public double AdmissionThreshold { get; init; } = 0.35;

    /// <summary>Top memory must be at least this many times the runner-up to be admitted.</summary>
    public double ConfidenceGap { get; init; } = 2.0;

    /// <summary>Hard cap on how many memories we ever inject in a single turn.</summary>
    public int MaxInjectedMemories { get; init; } = 2;

    /// <summary>Maximum recall-tool results returned to the LLM per call. See docs/recall_tool_design.md §3.2.</summary>
    public int RecallTopK { get; init; } = 10;

    /// <summary>Maximum number of recall-tool calls the LLM is allowed in a single direct/command reply.
    /// On the (n+1)-th call, the orchestrator forces ToolMode=RequireSpecific(send_discord_message).</summary>
    public int MaxRecallsPerReply { get; init; } = 3;

    /// <summary>Same budget for ambient (low-stakes) replies. Tighter because ambient replies have a 512-token output cap
    /// and don't justify multiple round-trips. See docs/recall_tool_design.md §4.2.</summary>
    public int MaxRecallsPerAmbientReply { get; init; } = 1;

    /// <summary>Legacy field — no longer read. Kept to avoid breaking config bindings during rollout.</summary>
    [Obsolete("Use RecallTopK instead.")]
    public int MaxRecallResults { get; init; } = 3;

    /// <summary>How aggressively to down-weight a candidate that overlaps with a just-admitted one. 0 = off, 1 = complete zero-out on full overlap.</summary>
    public double LateralInhibition { get; init; } = 0.5;

    /// <summary>Number of most-recent user messages whose tokens seed the relevance query.</summary>
    public int RecentMessageWindow { get; init; } = 3;

    /// <summary>Jaccard floor over tokens for a Suppressed memory to block a candidate.</summary>
    public double SuppressionOverlapThreshold { get; init; } = 0.3;

    /// <summary>
    /// Per-kind admission thresholds. String keys (rather than the <see cref="MemoryKind"/> enum) so the
    /// .NET config binder and env-var nesting play nicely: <c>MemoryRelevance__KindAmbientThreshold__Running=999999</c>.
    /// Default values render Meta and Running inadmissible ambiently.
    /// </summary>
    public Dictionary<string, double> KindAmbientThreshold { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Factual"] = 0.35,
        ["Experiential"] = 0.85,
        ["Running"] = 999999.0,
        ["Meta"] = 999999.0,
    };

    /// <summary>Append a one-line decision trace (`[memories: 2/5 admitted]`) to outgoing replies. Dev-only.</summary>
    public bool IncludeMemoryDebugFooter { get; init; } = false;

    public double GetKindThreshold(MemoryKind kind) =>
        KindAmbientThreshold.TryGetValue(kind.ToString(), out var v) ? v : AdmissionThreshold;
}
