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

    /// <summary>Prefix for the rules we own; we maintain "{prefix}-block" and "{prefix}-alert".</summary>
    public string RuleNamePrefix { get; init; } = "sky-scamguard";

    /// <summary>
    /// Whether the block-tier rule (lookalike domains + moderator-reported hosts) actually blocks. When false it
    /// only alerts, which is a safe way to preview the block tier.
    /// </summary>
    public bool BlockLookalikes { get; init; } = true;

    /// <summary>
    /// Whether to maintain the alert-tier rule (scam phrases). Phrases cannot require a link in AutoMod and are
    /// easily mutated, so they alert rather than block; disable to drop them entirely.
    /// </summary>
    public bool AlertPhrases { get; init; } = true;

    /// <summary>Channel name AutoMod posts its native alerts to.</summary>
    public string AlertChannelName { get; init; } = string.Empty;

    /// <summary>Guild names to manage. Empty means every guild where the bot has Manage Server.</summary>
    public List<string> GuildAllowList { get; init; } = new();

    /// <summary>Channel names exempt from the rules (e.g. the mod channel, so scam discussion does not self-trigger).</summary>
    public List<string> ExemptChannelNames { get; init; } = new();

    /// <summary>Message shown to a user when their message is blocked (block tier only; max 50 chars).</summary>
    public string BlockMessageText { get; init; } = "Halted by the Eggman Empire's anti-scam net.";
}
