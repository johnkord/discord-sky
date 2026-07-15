namespace DiscordSky.Bot.Configuration;

/// <summary>
/// Configuration for proactive cold opens: rare, unprompted, in-character bulletins dropped into a LIVE lull in
/// an opted-in channel, never into silence. Ships off and in shadow mode. See
/// docs/proactive_ensemble_design_2026-07-04.md and the implementation plan.
/// </summary>
public sealed class ColdOpenOptions
{
    public const string SectionName = "ColdOpen";

    /// <summary>Master switch. When false the service is fully dormant (no polling, no calls).</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>When true (default), the service judges and drafts a cold open and LOGS it, but never posts. Flip to false to go live.</summary>
    public bool ShadowMode { get; init; } = true;

    /// <summary>The only channels that may ever receive a cold open (a strict, opt-in allow-list). Empty means nothing fires.</summary>
    public List<ColdOpenChannel> Channels { get; init; } = new();

    /// <summary>A channel counts as alive only if a human posted within this many minutes; older means silent, and he stays quiet.</summary>
    public int WarmWindowMinutes { get; init; } = 10;

    /// <summary>Minimum seconds since the last human message before he may speak (a lull, not talking over someone).</summary>
    public int MinLullSeconds { get; init; } = 75;

    /// <summary>If someone typed within this many seconds, yield the floor (only enforced when typing is tracked).</summary>
    public int TypingYieldSeconds { get; init; } = 8;

    /// <summary>Require at least this many distinct humans active in the warm window (2 means a real conversation, not one person).</summary>
    public int MinDistinctHumans { get; init; } = 1;

    /// <summary>Hard cap on cold opens per channel per local day.</summary>
    public int MaxPerDay { get; init; } = 3;

    /// <summary>Minimum minutes between actual cold opens in a channel.</summary>
    public int CooldownMinutes { get; init; } = 180;

    /// <summary>Minimum minutes before the same normalized hook may fire again in one channel.</summary>
    public int HookCooldownMinutes { get; init; } = 1440;

    /// <summary>Minimum minutes between composer calls in a channel (a cost bound so a long lull does not re-judge every poll).</summary>
    public int JudgeCooldownMinutes { get; init; } = 20;

    /// <summary>Local-time quiet-hours window start hour [0-23). Equal to <see cref="QuietHoursEndLocal"/> disables quiet hours.</summary>
    public int QuietHoursStartLocal { get; init; } = 1;

    /// <summary>Local-time quiet-hours window end hour [0-23).</summary>
    public int QuietHoursEndLocal { get; init; } = 9;

    /// <summary>Timezone id for the quiet-hours and daily-reset local clock. Defaults to UTC.</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>Minimum worth (0..1) from the composer to actually fire. Higher than the ambient bar; an unprompted post is higher-stakes.</summary>
    public double WorthThreshold { get; init; } = 0.6;

    /// <summary>How often (seconds) the service checks the gate.</summary>
    public int PollSeconds { get; init; } = 45;
}

/// <summary>One opted-in cold-open target: a guild name (optional; blank matches any) and a channel name.</summary>
public sealed class ColdOpenChannel
{
    public string Guild { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
}
