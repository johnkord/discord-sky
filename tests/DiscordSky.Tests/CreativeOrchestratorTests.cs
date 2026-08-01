using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using System.ClientModel;

namespace DiscordSky.Tests;

public class CreativeOrchestratorTests
{
    private static readonly IReadOnlyDictionary<ulong, ChannelMessage> KnownMessages =
        new Dictionary<ulong, ChannelMessage>
        {
            [100] = new() { MessageId = 100, Author = "older", Content = "older context" },
        };

    [Fact]
    public void BuildEmptyResponsePlaceholder_CommandInvocation_ReturnsPersonaNotice()
    {
        var placeholder = CreativeOrchestrator.BuildEmptyResponsePlaceholder("Robotnik from AOSTH", CreativeInvocationKind.Command);
        Assert.Equal("[Robotnik from AOSTH pauses dramatically but says nothing.]", placeholder);
    }

    [Fact]
    public void BuildEmptyResponsePlaceholder_AmbientInvocation_ReturnsEmpty()
    {
        var placeholder = CreativeOrchestrator.BuildEmptyResponsePlaceholder("Robotnik from AOSTH", CreativeInvocationKind.Ambient);
        Assert.Equal(string.Empty, placeholder);
    }

    // ── StripImageContent ─────────────────────────────────────────────

    [Fact]
    public void StripImageContent_RemovesAllUriContent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new AIContent[]
            {
                new TextContent("Hello"),
                new UriContent(new Uri("https://example.com/img.jpg"), "image/*"),
                new TextContent("World"),
                new UriContent(new Uri("https://example.com/img2.png"), "image/*"),
            })
        };

        CreativeOrchestrator.StripImageContent(messages);

        Assert.Equal(2, messages[0].Contents.Count);
        Assert.All(messages[0].Contents, c => Assert.IsType<TextContent>(c));
    }

    [Fact]
    public void StripImageContent_NoImages_LeavesContentUnchanged()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new AIContent[]
            {
                new TextContent("Hello"),
                new TextContent("World"),
            })
        };

        CreativeOrchestrator.StripImageContent(messages);

        Assert.Equal(2, messages[0].Contents.Count);
    }

    [Fact]
    public void StripImageContent_MultipleMessages_StripsAll()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new AIContent[]
            {
                new TextContent("Msg1"),
                new UriContent(new Uri("https://a.com/1.jpg"), "image/*"),
            }),
            new(ChatRole.User, new AIContent[]
            {
                new UriContent(new Uri("https://b.com/2.jpg"), "image/*"),
                new TextContent("Msg2"),
            })
        };

        CreativeOrchestrator.StripImageContent(messages);

        Assert.Single(messages[0].Contents);
        Assert.IsType<TextContent>(messages[0].Contents[0]);
        Assert.Single(messages[1].Contents);
        Assert.IsType<TextContent>(messages[1].Contents[0]);
    }

    // ── IsImageDataError ──────────────────────────────────────────────

    [Fact]
    public void IsImageDataError_InvalidImageUrl_Matches()
    {
        var ex = new ClientResultException("HTTP 400 (invalid_request_error: invalid_image_url) Parameter: url");
        Assert.True(CreativeOrchestrator.IsImageDataError(ex));
    }

    [Fact]
    public void IsImageDataError_DownloadFailure_Matches()
    {
        // The shape we observed in production: 404 from upstream image fetch.
        var ex = new ClientResultException(
            "HTTP 400 (invalid_request_error: invalid_value) Parameter: url\nError while downloading file. Upstream status code: 404.");
        Assert.True(CreativeOrchestrator.IsImageDataError(ex));
    }

    [Fact]
    public void IsImageDataError_GenericException_DoesNotMatch()
    {
        Assert.False(CreativeOrchestrator.IsImageDataError(new HttpRequestException("boom")));
        Assert.False(CreativeOrchestrator.IsImageDataError(
            new ClientResultException("HTTP 500 (server_error) something else")));
    }

    [Theory]
    [InlineData(CreativeInvocationKind.DirectReply, "broadcast", 100UL, 999UL)]
    [InlineData(CreativeInvocationKind.Mention, "broadcast", 100UL, 999UL)]
    [InlineData(CreativeInvocationKind.Ambient, "reply", 100UL, 999UL)]
    [InlineData(CreativeInvocationKind.Ambient, "broadcast", 100UL, null)]
    [InlineData(CreativeInvocationKind.Command, "reply", 100UL, 100UL)]
    [InlineData(CreativeInvocationKind.Command, "reply", 777UL, null)]
    public void ResolveReplyTarget_EnforcesInvocationOwnership(
        CreativeInvocationKind kind,
        string mode,
        ulong? modelTarget,
        ulong? expected)
    {
        var request = new CreativeRequest(
            "Robotnik",
            "topic",
            "user",
            1,
            2,
            3,
            DateTimeOffset.UtcNow,
            kind,
            TriggerMessageId: 999);

        var actual = CreativeOrchestrator.ResolveReplyTarget(request, mode, modelTarget, KnownMessages);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveReplyTarget_TraceMetadataCannotChangeOwnership()
    {
        var baseline = new CreativeRequest(
            "Robotnik", "topic", "user", 1, 2, 3, DateTimeOffset.UtcNow,
            CreativeInvocationKind.Ambient, TriggerMessageId: 999);
        var traced = baseline with
        {
            Trace = new InteractionTraceContext(
                EpisodeId: "episode-1",
                OperationId: "operation-1",
                EvidenceDigest: "evidence-1")
        };

        var withoutTrace = CreativeOrchestrator.ResolveReplyTarget(baseline, "reply", 100, KnownMessages);
        var withTrace = CreativeOrchestrator.ResolveReplyTarget(traced, "reply", 100, KnownMessages);

        Assert.Equal(withoutTrace, withTrace);
        Assert.Equal(999UL, withTrace);
    }

    [Fact]
    public void ResolveReplyTarget_EpisodeReferentCannotChangeOwnership()
    {
        var episode = Episode();
        var request = Request(CreativeInvocationKind.Ambient, triggerMessageId: 999) with
        {
            Episode = episode,
            EpisodeDecision = new EpisodeActionDecision(
                new ReferentDecision(100, 1.0, ReferentResolutionStatus.Resolved))
        };

        var actual = CreativeOrchestrator.ResolveReplyTarget(request, "reply", 100, KnownMessages);

        Assert.Equal(999UL, actual);
    }

    [Theory]
    [InlineData(true, CreativeInvocationKind.Ambient, CreativeActionMode.ImageRequired, true)]
    [InlineData(true, CreativeInvocationKind.Ambient, CreativeActionMode.TextOnly, false)]
    [InlineData(true, CreativeInvocationKind.DirectReply, CreativeActionMode.Auto, true)]
    [InlineData(false, CreativeInvocationKind.Ambient, CreativeActionMode.ImageRequired, false)]
    public void ImageToolExposure_FollowsSelectedAction(
        bool enabled,
        CreativeInvocationKind invocationKind,
        CreativeActionMode actionMode,
        bool expected)
    {
        Assert.Equal(expected, CreativeOrchestrator.ShouldOfferImageTool(enabled, invocationKind, actionMode));
    }

    [Fact]
    public void RequiredImage_CannotCompleteAsProseOnly()
    {
        Assert.False(CreativeOrchestrator.CanCompleteRequiredImage(CreativeActionMode.ImageRequired, null));
        Assert.False(CreativeOrchestrator.CanCompleteRequiredImage(CreativeActionMode.ImageRequired, []));
        Assert.True(CreativeOrchestrator.CanCompleteRequiredImage(CreativeActionMode.ImageRequired, [1]));
        Assert.True(CreativeOrchestrator.CanCompleteRequiredImage(CreativeActionMode.TextOnly, null));
    }

    [Theory]
    [InlineData(CreativeInvocationKind.DirectReply, 123UL)]
    [InlineData(CreativeInvocationKind.Mention, 123UL)]
    [InlineData(CreativeInvocationKind.Command, null)]
    [InlineData(CreativeInvocationKind.Ambient, null)]
    public void ProviderFallback_PreservesDeterministicExplicitTarget(
        CreativeInvocationKind invocationKind,
        ulong? expectedTarget)
    {
        var request = Request(invocationKind, triggerMessageId: 123);

        var result = CreativeOrchestrator.BuildProviderFallback(request, "fallback");

        Assert.Equal(expectedTarget, result.ReplyToMessageId);
        Assert.Equal("fallback", result.PrimaryMessage);
    }

    [Fact]
    public void ProviderFallback_DeliversCompletedRequiredImageButSuppressesMissingImage()
    {
        var request = Request(
            CreativeInvocationKind.Ambient,
            triggerMessageId: 123,
            actionMode: CreativeActionMode.ImageRequired);

        var completed = CreativeOrchestrator.BuildProviderFallback(request, "provider prose", [1], "image.jpg");
        var missing = CreativeOrchestrator.BuildProviderFallback(request, "provider prose");

        Assert.Equal("Behold.", completed.PrimaryMessage);
        Assert.Equal((ulong)123, completed.ReplyToMessageId);
        Assert.NotNull(completed.AttachmentBytes);
        Assert.Equal(string.Empty, missing.PrimaryMessage);
        Assert.Null(missing.AttachmentBytes);
    }

    [Fact]
    public void RateLimitOutcome_CarriesBudgetEpisodeAndDeliveryMetadataWithoutModelClaim()
    {
        var request = Request(CreativeInvocationKind.DirectReply, triggerMessageId: 123) with
        {
            Channel = new ChannelContext("general", null, "server", false, null, 10, 2, null),
            Trace = new InteractionTraceContext(EpisodeId: "episode-1", EvidenceDigest: "evidence-1"),
        };
        var decision = CreativeRateLimitDecision.Limited(
            "explicit_channel_reserve",
            4,
            4,
            "explicit_channel_reserve_exhausted");
        var result = CreativeOrchestrator.BuildProviderFallback(request, "try again");

        var telemetry = CreativeOrchestrator.BuildRateLimitTelemetry(request, decision);
        var transcript = CreativeOrchestrator.BuildRateLimitTranscript(request, result);

        Assert.Equal(TelemetryEventTypes.CreativeRateLimited, telemetry.EventType);
        Assert.Equal("episode-1", telemetry.EpisodeId);
        Assert.Equal("DirectReply", telemetry.Kind);
        Assert.Equal("explicit_channel_reserve", telemetry.BudgetClass);
        Assert.Equal(4, telemetry.Count);
        Assert.Equal(4, telemetry.Limit);
        Assert.False(string.IsNullOrWhiteSpace(telemetry.ChannelHash));
        Assert.Equal("try again", transcript.Reply);
        Assert.Equal(123UL, transcript.ReplyTargetMessageId);
        Assert.Equal("rate_limited_fallback", transcript.Outcome);
        Assert.False(transcript.ModelInvoked);
        Assert.Equal("[rate limited before model invocation]", transcript.Prompt);
    }

    [Theory]
    [InlineData(CreativeInvocationKind.DirectReply, CreativeActionMode.ImageRequired, 123UL)]
    [InlineData(CreativeInvocationKind.Command, CreativeActionMode.ImageRequired, null)]
    [InlineData(CreativeInvocationKind.Ambient, CreativeActionMode.ImageRequired, null)]
    public void RateLimitFallback_IsClearForExplicitImagesAndSilentForAmbient(
        CreativeInvocationKind invocationKind,
        CreativeActionMode actionMode,
        ulong? expectedTarget)
    {
        var request = Request(invocationKind, triggerMessageId: 123, actionMode);

        var result = CreativeOrchestrator.BuildRateLimitFallback(request);

        Assert.Equal(expectedTarget, result.ReplyToMessageId);
        Assert.Equal(
            invocationKind == CreativeInvocationKind.Ambient ? string.Empty : "I'm catching my breath, try again soon!",
            result.PrimaryMessage);
        Assert.Null(result.AttachmentBytes);
    }

    private static CreativeRequest Request(
        CreativeInvocationKind invocationKind,
        ulong? triggerMessageId,
        CreativeActionMode actionMode = CreativeActionMode.Auto) =>
        new(
            "Robotnik",
            "topic",
            "user",
            1,
            2,
            3,
            DateTimeOffset.UtcNow,
            invocationKind,
            TriggerMessageId: triggerMessageId,
            ActionMode: actionMode);

    private static InteractionEpisode Episode()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        return InteractionEpisode.Create(
            "episode-1",
            now,
            2,
            999,
            new[]
            {
                new EpisodeMessage(100, 10, "Alice", "meteor incoming", now.AddSeconds(-5)),
                new EpisodeMessage(999, 20, "Bob", "what is that?", now),
            },
            null,
            new ReferentRequirement(true, "deictic_question"),
            new[] { new ReferentCandidate(100, 0.75, "recent_message") });
    }
}
