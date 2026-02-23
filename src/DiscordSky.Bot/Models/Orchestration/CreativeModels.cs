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
    ulong? TriggerMessageId = null,
    ChannelContext? Channel = null,
    IReadOnlyList<UserMemory>? UserMemories = null,
    IReadOnlyList<UnfurledLink>? UnfurledLinks = null
);

/// <summary>
/// Metadata about the Discord channel and server where the message originated.
/// </summary>
public sealed record ChannelContext(
    string? ChannelName,
    string? ChannelTopic,
    string? ServerName,
    bool IsNsfw,
    string? ThreadName,
    int? MemberCount,
    int RecentMessageCount,
    DateTimeOffset? BotLastSpokeAt
);

public sealed record CreativeContext(
    IReadOnlyList<ChannelMessage> ChannelHistory
);

public sealed record CreativeResult(
    string PrimaryMessage,
    ulong? ReplyToMessageId = null
);

public sealed record UserMemory(
    string Content,
    string Context,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastReferencedAt,
    int ReferenceCount
);

/// <summary>
/// A memory operation that targets a specific user, produced by conversation-window extraction.
/// </summary>
public sealed record MultiUserMemoryOperation(
    ulong UserId,
    MemoryAction Action,
    int? MemoryIndex,
    string? Content,
    string? Context
);

public enum MemoryAction { Save, Update, Forget }

/// <summary>
/// A single message buffered for conversation-window memory extraction.
/// </summary>
public sealed record BufferedMessage(
    ulong AuthorId,
    string AuthorDisplayName,
    string Content,
    DateTimeOffset Timestamp);

/// <summary>
/// Accumulates messages per channel for debounced conversation-window extraction.
/// </summary>
internal sealed class ChannelMessageBuffer
{
    public List<BufferedMessage> Messages { get; } = new();
    public Timer? DebounceTimer { get; set; }
    public DateTimeOffset FirstMessageAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public readonly object Lock = new();
}

public sealed record ChannelMessage
{
    public ulong MessageId { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public bool IsBot { get; init; }
    public IReadOnlyList<ChannelImage> Images { get; init; } = Array.Empty<ChannelImage>();
    public IReadOnlyList<UnfurledLink> UnfurledLinks { get; init; } = Array.Empty<UnfurledLink>();
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

/// <summary>
/// Content extracted from a linked resource (e.g. a tweet).
/// </summary>
public sealed record UnfurledLink
{
    public string SourceType { get; init; } = string.Empty;
    public Uri OriginalUrl { get; init; } = null!;
    public string Text { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public IReadOnlyList<ChannelImage> Images { get; init; } = Array.Empty<ChannelImage>();
}
