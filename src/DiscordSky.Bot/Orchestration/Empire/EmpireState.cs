namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// The canonical persisted state: a tiny structured spine (mood, version, ranks) plus one freeform
/// natural-language <see cref="Body"/> (his war-room log). The spine holds only what code must compute or
/// look up; everything else about who he is right now lives in the body. See the design doc for the why.
/// </summary>
public sealed record EmpireState(
    int Version,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastTickAt,
    Mood Mood,
    IReadOnlyList<Rank> Ranks,
    string Body);

/// <summary>Robotnik's mood on two axes (Russell circumplex) plus a derived, human-readable label.</summary>
public sealed record Mood(double Valence, double Arousal, string Label);

/// <summary>A title he has bestowed on someone. <see cref="IdleTicks"/> ages the title out when unused.</summary>
public sealed record Rank(string Name, string Title, int IdleTicks);

/// <summary>A small, clamped mood nudge produced by appraising a runtime event (phase 2).</summary>
public readonly record struct MoodDelta(double Valence, double Arousal);
