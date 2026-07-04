namespace DiscordSky.Bot.Configuration;

/// <summary>
/// Configuration for the Empire State feature: Robotnik's persistent, evolving in-character "life"
/// (a structured mood/rank spine plus a freeform war-room-log body). Bound from the "EmpireState" section.
/// See docs/empire_state_design_2026-07-03.md and docs/empire_state_implementation_plan_2026-07-03.md.
/// </summary>
public sealed class EmpireStateOptions
{
    public const string SectionName = "EmpireState";

    /// <summary>Master switch. When false the store loads read-only, injection is skipped, and the tick never runs.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Path to the canonical state JSON. Should sit on the PVC so it survives pod rotation.</summary>
    public string Path { get; set; } = System.IO.Path.Combine("data", "empire_state.json");

    /// <summary>How often the world advances. A tick is skipped when the channel has been silent since the last one.</summary>
    public int TickIntervalHours { get; set; } = 6;

    /// <summary>Let the tick rewrite the log via the cheap LLM. False freezes the log to the seed body plus deterministic mood (kill switch).</summary>
    public bool EnableLlmBody { get; set; } = true;

    /// <summary>Hard budget that forces the log to compact and forget rather than grow.</summary>
    public int BodyMaxChars { get; set; } = 1200;

    /// <summary>Reject a rewrite that guts the log below this fraction of the prior length (guards a wipe). Skipped while the prior body is still the short seed.</summary>
    public double MinBodyRetainFraction { get; set; } = 0.5;

    /// <summary>Mood stickiness (inertia); higher is stickier. Each tick: next = baseline + (current - baseline) * retain.</summary>
    public double MoodRetain { get; set; } = 0.7;

    /// <summary>His resting valence (defeated -1 to triumphant +1); mood decays toward this.</summary>
    public double BaselineValence { get; set; } = 0.3;

    /// <summary>His resting arousal (sulking -1 to manic-scheming +1); mood decays toward this. Kept moderate (not pinned high) so both energetic and calm moods stay reachable. Tunable on live data.</summary>
    public double BaselineArousal { get; set; } = 0.2;

    /// <summary>Max remembered ranks (titles he has bestowed) before the freshest are kept and the rest dropped.</summary>
    public int RanksMax { get; set; } = 40;

    /// <summary>Drop a rank after this many ticks unused, so titles do not go stale.</summary>
    public int RankIdleTicksMax { get; set; } = 8;

    /// <summary>Max rank assignments the LLM may make per tick.</summary>
    public int MaxRankOpsPerTick { get; set; } = 2;

    /// <summary>Max characters of a rank title.</summary>
    public int MaxRankTitleLength { get; set; } = 40;

    /// <summary>How many recent participant names to offer the consolidator as razz candidates.</summary>
    public int CandidateSampleSize { get; set; } = 10;

    /// <summary>How long a participant stays a candidate after speaking.</summary>
    public int RecentParticipantTtlHours { get; set; } = 6;
}
