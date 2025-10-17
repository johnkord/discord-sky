using System;
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

public sealed record CreativeResult(
    string PrimaryMessage,
    ulong? ReplyToMessageId = null,
    string Mode = "broadcast"
);

public sealed record ChannelMessage
{
    public ulong MessageId { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public bool IsBot { get; init; }
    public IReadOnlyList<ChannelImage> Images { get; init; } = Array.Empty<ChannelImage>();
}

public sealed record ChannelImage
{
    public Uri Url { get; init; } = null!;
    public string Filename { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}
