namespace DiscordSky.Bot.Configuration;

/// <summary>
/// Configuration for syncing ScamGuard's lists into a native Discord AutoMod rule. AutoMod is proactive
/// (blocks before a message posts) and covers all senders including bots and webhooks, which our reactive bot
/// cannot. Requires the bot to have Manage Server. Off by default; scoped by <see cref="GuildAllowList"/>.
/// </summary>
public sealed class AutoModOptions
{
    public const string SectionName = "AutoMod";

    /// <summary>Whether to create/maintain the AutoMod rule at all.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Name of the rule we own. We only ever touch a rule with this name that we created.</summary>
    public string RuleName { get; init; } = "sky-scamguard";

    /// <summary>When true the rule blocks the message; when false it only alerts (start here, then graduate).</summary>
    public bool BlockMessages { get; init; } = false;

    /// <summary>Channel name AutoMod posts its native alerts to. Empty and not blocking means the rule is skipped.</summary>
    public string AlertChannelName { get; init; } = string.Empty;

    /// <summary>Guild names to manage. Empty means every guild where the bot has Manage Server.</summary>
    public List<string> GuildAllowList { get; init; } = new();

    /// <summary>Channel names exempt from the rule (e.g. a scam-testing channel).</summary>
    public List<string> ExemptChannelNames { get; init; } = new();

    /// <summary>Include ScamGuard's scam phrases as keywords. AutoMod cannot require a link, so these can fire on
    /// link-less chatter; disable if the alert channel gets noisy and rely on the lookalike regex.</summary>
    public bool IncludePhrases { get; init; } = true;

    /// <summary>Include the lookalike-host fragments as a regex pattern (near-zero false positives).</summary>
    public bool IncludeLookalikeRegex { get; init; } = true;

    /// <summary>Message shown to a user when their message is blocked (only used when BlockMessages is true).</summary>
    public string BlockMessageText { get; init; } = "Halted by the Eggman Empire's anti-thievery perimeter.";
}
