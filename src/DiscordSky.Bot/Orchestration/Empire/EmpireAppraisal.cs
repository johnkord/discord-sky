namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// Turns runtime events into small, clamped mood nudges (appraisal theory: mood arises from appraising events
/// against goals). Deltas are intentionally small so it takes several events to move the mood meaningfully, and
/// the tick's decay relaxes it back toward baseline over hours. Reuses signals the bot already produces: his
/// own reaction verdicts, reactions on his lines, and a foiled scam.
/// </summary>
public static class EmpireAppraisal
{
    /// <summary>Someone reacted positively to one of his lines: a laugh landed.</summary>
    public static MoodDelta LaughAtHim { get; } = new(0.12, 0.05);

    /// <summary>Someone reacted negatively to one of his lines: he was panned.</summary>
    public static MoodDelta Panned { get; } = new(-0.08, 0.05);

    /// <summary>His guard foiled a scam or spammer: a rare, satisfying triumph.</summary>
    public static MoodDelta ScamFoiled { get; } = new(0.15, 0.10);

    /// <summary>
    /// His OWN reaction verdict on someone's message reveals and reinforces his mood: mocking a fool pleases
    /// him, an angry react means he is irritated. Unknown or custom tokens are neutral.
    /// </summary>
    public static MoodDelta FromReaction(string? token) => (token ?? string.Empty).ToLowerInvariant() switch
    {
        "anger" => new(-0.12, 0.08),       // irritated, toward seething
        "thumbsdown" => new(-0.08, 0.02),  // dismissive
        "eyeroll" => new(-0.06, -0.04),    // contemptuous and bored
        "laughing" => new(0.12, 0.06),     // gloating laughter
        "clown" => new(0.10, 0.04),        // mocking a fool: pleased with himself
        "chartdown" => new(0.08, 0.02),    // savoring a failure
        "egg" => new(0.06, 0.03),          // his signature approval
        "robot" => new(0.05, 0.07),        // scheming approval
        "eyes" => new(0.02, 0.08),         // intrigued and energized
        "lying" => new(-0.05, 0.05),       // sniffing out a lie or scam
        _ => new(0.0, 0.0),                // custom emotes or unknown: neutral
    };
}
