namespace DiscordSky.Bot.Models.Orchestration;

public sealed record MemoryOpportunityFeatures(
    int MessageCount,
    int ParticipantCount,
    int CharacterCount,
    TimeSpan WindowDuration,
    bool IsShutdownFlush,
    int FirstPersonAssertionCount = 0,
    int PreferenceIdentityChangeCount = 0,
    bool QuestionOnly = false,
    bool MediaOnly = false,
    double LexicalNovelty = 0.0,
    TimeSpan? PriorExtractionAge = null,
    bool IsOneMessageWindow = false);

public sealed record ExtractionWindow(
    string OperationId,
    DateTimeOffset CapturedAt,
    ulong ChannelId,
    IReadOnlyList<BufferedMessage> Messages,
    IReadOnlyList<ulong> ParticipantIds,
    MemoryOpportunityFeatures Features)
{
    public static ExtractionWindow Capture(
        ulong channelId,
        IReadOnlyList<BufferedMessage> messages,
        bool isShutdownFlush,
        DateTimeOffset? capturedAt = null,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException("An extraction window requires at least one message.", nameof(messages));
        }

        var immutableMessages = Array.AsReadOnly(messages.ToArray());
        var participants = Array.AsReadOnly(messages
            .Select(message => message.AuthorId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray());
        var first = messages.Min(message => message.Timestamp);
        var last = messages.Max(message => message.Timestamp);

        return new ExtractionWindow(
            operationId ?? Guid.NewGuid().ToString("N"),
            capturedAt ?? DateTimeOffset.UtcNow,
            channelId,
            immutableMessages,
            participants,
            new MemoryOpportunityFeatures(
                messages.Count,
                participants.Count,
                messages.Sum(message => message.Content.Length),
                last - first,
                isShutdownFlush,
                IsOneMessageWindow: messages.Count == 1));
    }
}