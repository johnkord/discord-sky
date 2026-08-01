namespace DiscordSky.Bot.Models.Orchestration;

[Flags]
public enum EpisodeEvidenceMask
{
    None = 0,
    Trigger = 1,
    ReplyParent = 2,
    RecentMessages = 4,
    Media = 8,
    DeicticRisk = 16,
}

public enum ReferentResolutionStatus
{
    None,
    ExplicitReply,
    Resolved,
    Ambiguous,
    Unresolved,
    Invalid,
}

public enum EpisodeReplyTargetPolicy
{
    TriggerOrBroadcast,
}

public sealed record ReferentRequirement(bool IsRequired, string ReasonCode);

public sealed record ReferentCandidate(
    ulong MessageId,
    double CandidateScore,
    string CandidateReason);

public sealed record ReferentDecision(
    ulong? SelectedMessageId,
    double Confidence,
    ReferentResolutionStatus Status,
    string ReasonCode = "none");

public sealed record EpisodeActionDecision(ReferentDecision ReferentDecision);

public sealed record EpisodeTargetPolicy(
    EpisodeReplyTargetPolicy Policy,
    ulong TriggerMessageId);

public sealed record EpisodeFingerprint(
    string EvidenceDigest,
    IReadOnlyList<ulong> MessageIds,
    EpisodeEvidenceMask EvidenceMask);

public sealed record EpisodeMessage(
    ulong MessageId,
    ulong AuthorId,
    string AuthorDisplayName,
    string Content,
    DateTimeOffset Timestamp,
    ulong? ReferencedMessageId = null,
    bool IsBot = false,
    string? MediaContext = null,
    IReadOnlyList<ChannelImage>? Images = null,
    IReadOnlyList<UnfurledLink>? UnfurledLinks = null)
{
    public bool HasMedia => !string.IsNullOrWhiteSpace(MediaContext)
        || Images is { Count: > 0 }
        || UnfurledLinks is { Count: > 0 };

    internal EpisodeMessage Freeze() => this with
    {
        Images = Array.AsReadOnly((Images ?? Array.Empty<ChannelImage>()).ToArray()),
        UnfurledLinks = Array.AsReadOnly((UnfurledLinks ?? Array.Empty<UnfurledLink>()).ToArray()),
    };
}

public sealed record InteractionEpisode
{
    public const int CurrentSchemaVersion = 1;

    private InteractionEpisode(
        string episodeId,
        DateTimeOffset capturedAt,
        ulong channelId,
        ulong triggerMessageId,
        IReadOnlyList<EpisodeMessage> messages,
        ulong? replyParentMessageId,
        ReferentRequirement referentRequirement,
        IReadOnlyList<ReferentCandidate> referentCandidates,
        EpisodeEvidenceMask evidenceMask,
        EpisodeFingerprint fingerprint,
        EpisodeTargetPolicy targetPolicy)
    {
        EpisodeId = episodeId;
        CapturedAt = capturedAt;
        ChannelId = channelId;
        TriggerMessageId = triggerMessageId;
        Messages = messages;
        ReplyParentMessageId = replyParentMessageId;
        ReferentRequirement = referentRequirement;
        ReferentCandidates = referentCandidates;
        EvidenceMask = evidenceMask;
        Fingerprint = fingerprint;
        TargetPolicy = targetPolicy;
    }

    public string EpisodeId { get; }
    public int SchemaVersion => CurrentSchemaVersion;
    public DateTimeOffset CapturedAt { get; }
    public ulong ChannelId { get; }
    public ulong TriggerMessageId { get; }
    public IReadOnlyList<EpisodeMessage> Messages { get; }
    public ulong? ReplyParentMessageId { get; }
    public ReferentRequirement ReferentRequirement { get; }
    public IReadOnlyList<ReferentCandidate> ReferentCandidates { get; }
    public EpisodeEvidenceMask EvidenceMask { get; }
    public EpisodeFingerprint Fingerprint { get; }
    public EpisodeTargetPolicy TargetPolicy { get; }

    public EpisodeMessage Trigger => Messages.Single(message => message.MessageId == TriggerMessageId);

    public static InteractionEpisode Create(
        string episodeId,
        DateTimeOffset capturedAt,
        ulong channelId,
        ulong triggerMessageId,
        IEnumerable<EpisodeMessage> messages,
        ulong? replyParentMessageId,
        ReferentRequirement referentRequirement,
        IEnumerable<ReferentCandidate> referentCandidates)
    {
        if (string.IsNullOrWhiteSpace(episodeId)) throw new ArgumentException("Episode ID is required.", nameof(episodeId));

        var orderedMessages = messages
            .GroupBy(message => message.MessageId)
            .Select(group => group.Last())
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId)
            .Select(message => message.Freeze())
            .ToArray();
        if (!orderedMessages.Any(message => message.MessageId == triggerMessageId))
        {
            throw new ArgumentException("The trigger message must be present in the episode.", nameof(messages));
        }

        var messageIds = orderedMessages.Select(message => message.MessageId).ToHashSet();
        var candidates = referentCandidates
            .Where(candidate => messageIds.Contains(candidate.MessageId) && candidate.MessageId != triggerMessageId)
            .GroupBy(candidate => candidate.MessageId)
            .Select(group => group.OrderByDescending(candidate => candidate.CandidateScore).First())
            .OrderByDescending(candidate => candidate.CandidateScore)
            .ThenByDescending(candidate => candidate.MessageId)
            .ToArray();

        var mask = EpisodeEvidenceMask.Trigger;
        if (replyParentMessageId.HasValue && messageIds.Contains(replyParentMessageId.Value)) mask |= EpisodeEvidenceMask.ReplyParent;
        if (orderedMessages.Any(message => message.MessageId != triggerMessageId && message.MessageId != replyParentMessageId)) mask |= EpisodeEvidenceMask.RecentMessages;
        if (orderedMessages.Any(message => message.HasMedia)) mask |= EpisodeEvidenceMask.Media;
        if (referentRequirement.IsRequired) mask |= EpisodeEvidenceMask.DeicticRisk;

        var evidenceDigest = EpisodeDigest.ComputeEvidenceDigest(
            capturedAt,
            channelId,
            triggerMessageId,
            orderedMessages,
            replyParentMessageId,
            referentRequirement,
            candidates,
            mask);
        var fingerprint = new EpisodeFingerprint(
            evidenceDigest,
            Array.AsReadOnly(orderedMessages.Select(message => message.MessageId).ToArray()),
            mask);

        return new InteractionEpisode(
            episodeId,
            capturedAt,
            channelId,
            triggerMessageId,
            Array.AsReadOnly(orderedMessages),
            replyParentMessageId,
            referentRequirement,
            Array.AsReadOnly(candidates),
            mask,
            fingerprint,
            new EpisodeTargetPolicy(EpisodeReplyTargetPolicy.TriggerOrBroadcast, triggerMessageId));
    }
}

public sealed record EpisodeTriggerEvidence(
    ulong ChannelId,
    ulong MessageId,
    ulong AuthorId,
    string AuthorDisplayName,
    ulong? ReferencedMessageId,
    SemanticMessageView View);

public sealed record EpisodeBuildFailure(string ReasonCode, string? Detail = null);

public sealed record EpisodeBuildResult(
    InteractionEpisode? Episode,
    EpisodeBuildFailure? Failure)
{
    public bool IsSuccess => Episode is not null && Failure is null;

    public static EpisodeBuildResult Success(InteractionEpisode episode) => new(episode, null);
    public static EpisodeBuildResult Failed(string reasonCode, string? detail = null) =>
        new(null, new EpisodeBuildFailure(reasonCode, detail));
}