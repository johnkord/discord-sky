using System;
using DiscordSky.Bot.Configuration;

namespace DiscordSky.Bot.Models.Orchestration;

public enum CreativeInvocationKind
{
    Command,
    Ambient,
    DirectReply
}

public sealed record CreativeRequest(
    string Persona,
    string? Topic,
    string UserDisplayName,
    ulong UserId,
    ulong ChannelId,
    ulong? GuildId,
    DateTimeOffset Timestamp,
    CreativeInvocationKind InvocationKind = CreativeInvocationKind.Command,
    IReadOnlyList<ChannelMessage>? ReplyChain = null,
    bool IsInThread = false,
    ulong? TriggerMessageId = null
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
    public ulong? ReferencedMessageId { get; init; }
    public bool IsFromThisBot { get; init; }
}

public sealed record ChannelImage
{
    public Uri Url { get; init; } = null!;
    public string Filename { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}
