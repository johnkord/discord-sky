using DiscordSky.Bot.Configuration;

namespace DiscordSky.Bot.Models.Orchestration;

public sealed record CreativeRequest(
    string Persona,
    string? Topic,
    string UserDisplayName,
    ulong UserId,
    ulong ChannelId,
    ulong? GuildId,
    DateTimeOffset Timestamp
);

public sealed record CreativeContext(
    CreativeRequest Request,
    ChaosSettings Chaos,
    IReadOnlyList<ChannelMessage> ChannelHistory
);

public sealed record CreativeResult(string PrimaryMessage);

public sealed record ChannelMessage(string Author, string Content, DateTimeOffset Timestamp);
