namespace DiscordSky.Bot.Configuration;

/// <summary>
/// Configuration for the proactive scam-link guard. When an obvious phishing or crypto-scam link lands in an
/// allow-listed channel, the bot replies in character to warn everyone off it. Requested by a server admin
/// after the bot organically called out a "MrBeast crypto casino" scam (see repo memory / session notes).
/// </summary>
public sealed class ScamGuardOptions
{
    public const string SectionName = "ScamGuard";

    /// <summary>Whether proactive scam-link warnings are enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Minimum seconds between warnings in the same channel. Stops a flood of scam links from turning the bot
    /// into the spammer; during a raid only the first link is called out, the rest are silently dropped.
    /// </summary>
    public int CooldownSeconds { get; init; } = 45;

    /// <summary>
    /// Extra scam phrases to flag, merged with the built-in list. Case-insensitive substring match against the
    /// message text. Only applied when the message also contains a link.
    /// </summary>
    public List<string> ExtraScamPhrases { get; init; } = new();

    /// <summary>
    /// Extra phishing host fragments to flag, merged with the built-in list. Case-insensitive substring match
    /// (e.g. "dlscord", "steamcommunlty"). Only applied when the message also contains a link.
    /// </summary>
    public List<string> ExtraPhishingHosts { get; init; } = new();

    /// <summary>
    /// Whether known URL shorteners (bit.ly, tinyurl, ...) count as a corroborating scam signal when paired
    /// with a strong scam token or a mass-mention. We never follow the shortener (that would be an SSRF risk).
    /// </summary>
    public bool TreatShortenersAsSignal { get; init; } = true;

    /// <summary>
    /// Whether to consult the Sinking Yachts phishing-domain feed (mirrored locally on the PVC). Opt-in because
    /// it adds an outbound dependency; detection is always fail-open, falling back to the built-in heuristics.
    /// </summary>
    public bool UsePhishingFeed { get; init; } = false;

    /// <summary>Base URL of the phishing-domain feed (Sinking Yachts API).</summary>
    public string PhishingFeedUrl { get; init; } = "https://phish.sinking.yachts";

    /// <summary>How often to pull recent feed changes.</summary>
    public int PhishingFeedRefreshMinutes { get; init; } = 15;

    /// <summary>Value sent in the X-Identity header so the feed maintainers know who is calling.</summary>
    public string PhishingFeedIdentity { get; init; } = "discord-sky-bot";

    /// <summary>Where the mirrored domain list is cached so restarts and outages stay covered.</summary>
    public string PhishingFeedCachePath { get; init; } = "data/phishing_domains.json";

    /// <summary>Whether to scan messages from other bots and webhooks (the primary raid vector).</summary>
    public bool ScanBotMessages { get; init; } = true;

    /// <summary>Bot/webhook user IDs that are trusted and never scanned (music bots, GitHub, etc.).</summary>
    public List<ulong> TrustedBotIds { get; init; } = new();

    /// <summary>Accounts younger than this many days count as a "new account" corroborating signal for invites.</summary>
    public int NewAccountDays { get; init; } = 7;

    /// <summary>Sliding-window length, in seconds, for behavioral raid detection.</summary>
    public int RaidWindowSeconds { get; init; } = 60;

    /// <summary>Distinct channels the same link must hit within the window to count as a raid.</summary>
    public int RaidChannelThreshold { get; init; } = 3;

    /// <summary>Repeat count of the same link within the window to count as a raid.</summary>
    public int RaidRepeatThreshold { get; init; } = 4;

    /// <summary>Channel name to post scam/raid alerts to (with a jump link). Empty disables reporting.</summary>
    public string AlertChannelName { get; init; } = string.Empty;

    /// <summary>Where moderator-taught scam phrases/hosts are persisted.</summary>
    public string LearnedListPath { get; init; } = "data/learned_scams.json";
}
