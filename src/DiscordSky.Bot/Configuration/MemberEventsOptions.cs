namespace DiscordSky.Bot.Configuration;

/// <summary>
/// Configuration for member-join handling: in-house mass-join raid detection and an in-character greeting.
/// OFF by default. Requires the privileged GuildMembers gateway intent, which is only requested when
/// <see cref="Enabled"/> is true, so a default deploy needs no developer-portal change and cannot fail to
/// connect. To turn on: enable "Server Members Intent" in the Discord dev portal, then set Enabled=true.
/// </summary>
public sealed class MemberEventsOptions
{
    public const string SectionName = "MemberEvents";

    /// <summary>Master switch. When true the bot requests the GuildMembers intent and handles UserJoined.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Whether Robotnik greets each new member (suppressed during a detected join-raid).</summary>
    public bool GreetNewMembers { get; init; } = true;

    /// <summary>Channel to greet in. Empty falls back to the guild's system channel; if neither, no greeting.</summary>
    public string GreetChannelName { get; init; } = string.Empty;

    /// <summary>Channel for join-raid alerts. Empty disables raid alerting.</summary>
    public string AlertChannelName { get; init; } = string.Empty;

    /// <summary>Guild names to act in. Empty means every guild the bot is in.</summary>
    public List<string> GuildAllowList { get; init; } = new();

    /// <summary>Sliding window (seconds) over which joins are counted.</summary>
    public int JoinRaidWindowSeconds { get; init; } = 30;

    /// <summary>Joins within the window that trip a raid alert (and suppress greetings).</summary>
    public int JoinRaidThreshold { get; init; } = 5;
}
