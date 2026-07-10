namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// Turns runtime events into small, clamped mood nudges (appraisal theory: mood arises from appraising events
/// against goals). Deltas are intentionally small so it takes several events to move the mood meaningfully, and
/// the tick's decay relaxes it back toward baseline over hours. Only external outcomes move mood: human
/// reception and concrete wins such as a foiled scam. Robotnik's own generated choices do not reward themselves.
/// </summary>
public static class EmpireAppraisal
{
    /// <summary>Someone reacted positively to one of his lines: a laugh landed.</summary>
    public static MoodDelta LaughAtHim { get; } = new(0.12, 0.05);

    /// <summary>Someone reacted negatively to one of his lines: he was panned. Sours and deflates (toward sulking).</summary>
    public static MoodDelta Panned { get; } = new(-0.08, -0.06);

    /// <summary>His guard foiled a scam or spammer: a rare, satisfying triumph.</summary>
    public static MoodDelta ScamFoiled { get; } = new(0.15, 0.10);

}
