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
}
