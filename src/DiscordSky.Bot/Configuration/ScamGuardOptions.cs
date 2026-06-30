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
}
