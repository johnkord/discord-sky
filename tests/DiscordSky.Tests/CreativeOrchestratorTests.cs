using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using System.ClientModel;

namespace DiscordSky.Tests;

public class CreativeOrchestratorTests
{
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
}
