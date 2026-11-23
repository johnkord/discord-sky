using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Discord;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyVisionTests
{
    [Fact]
    public async Task MixedAttachments_UseActualBytesAndProvenanceWithoutLeakingUrls()
    {
        using var cdn = new VisionTestCdn();
        var current = cdn.Attachment;
        var replied = current with { MessageId = 20, Origin = AutonomyImageOrigin.Reply,
            Url = "https://media.discordapp.net/attachments/6001/200/reply.png" };
        var elsewhere = current with { Url = "https://cdn.discordapp.com/attachments/99/300/private.png" };
        var unavailable = current with { Url = "https://cdn.discordapp.com/attachments/6001/400/missing.png" };
        var message = await cdn.Reader(new BotOptions { HistoryImageLimit = 4 }).BuildUserMessageAsync(
            "What do the images say?", new([current, current, elsewhere, unavailable, replied]), 6001, 10, "run", "full", default);

        Assert.Equal(ChatRole.User, message.Role);
        var images = message.Contents.OfType<DataContent>().ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, image => { Assert.Equal("image/png", image.MediaType); Assert.Equal(cdn.Png, image.Data.ToArray()); });
        Assert.Contains("current message", message.Text);
        Assert.Contains("replied-to message 20", message.Text);
        Assert.Contains("2 image attachment(s) could not be included", message.Text);
        Assert.DoesNotContain("https://", message.Text);
        Assert.Equal(3, cdn.Downloads);
        var metric = Assert.Single(cdn.Telemetry.Events);
        Assert.Equal(("run", "full", "partial", 2, 4), (metric.OperationId, metric.Kind, metric.Outcome, metric.Count, metric.Total));
        Assert.DoesNotContain("sensitive-signature", JsonSerializer.Serialize(metric));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public async Task DisabledVision_NeverFetchesOrAddsContent(bool allow, int limit)
    {
        using var cdn = new VisionTestCdn();
        var message = await cdn.Reader(new BotOptions { AllowImageContext = allow, HistoryImageLimit = limit })
            .BuildUserMessageAsync("text only", new([cdn.Attachment]), 6001, 10, "run", "full", default);
        Assert.IsType<TextContent>(Assert.Single(message.Contents));
        Assert.Equal("text only", message.Text);
        Assert.Equal(0, cdn.Downloads);
        Assert.Empty(cdn.Telemetry.Events);
    }

    [Fact]
    public async Task LimitedOrInvalidImages_DoNotLoseTheTextOrPretendToSeePixels()
    {
        using var cdn = new VisionTestCdn();
        var source = cdn.Attachment;
        var message = await cdn.Reader(new BotOptions { HistoryImageLimit = 2 },
            new ImageOptions { MaxReferenceBytes = 4 }).BuildUserMessageAsync("read", new([
                source with { Size = 500 }, source with { Size = 1, Url = source.Url + "1" },
                source with { Url = source.Url + "2" }]), 6001, 10, "run", "full", default);
        Assert.Empty(message.Contents.OfType<DataContent>());
        Assert.StartsWith("read", message.Text);
        Assert.Contains("3 image attachment(s) could not be included", message.Text);
        Assert.Equal(1, cdn.Downloads);
        var metric = Assert.Single(cdn.Telemetry.Events);
        Assert.True(metric.Truncated);
        Assert.Equal("unavailable", metric.Outcome);
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToMissingImage()
    {
        using var cdn = new VisionTestCdn();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cdn.Reader().BuildUserMessageAsync("read",
            new([cdn.Attachment]), 6001, 10, "run", "full", new CancellationToken(true)));
        Assert.Equal(0, cdn.Downloads);
        Assert.Empty(cdn.Telemetry.Events);
    }

    [Theory]
    [InlineData("what do you make of this AI slop?", true)]
    [InlineData("what does this say?", true)]
    [InlineData("read the screenshot", true)]
    [InlineData("let me check", false)]
    [InlineData("let's discuss image compression", false)]
    [InlineData("make an image of a red balloon", false)]
    public void UnthreadedQuestion_MustPointAtAnImage(string text, bool refers) =>
        Assert.Equal(refers, WorldAutonomyVision.RefersToRecentImage(text));

    [Fact]
    public void SelectedOrigin_PrefersCurrentUploadAndNeverWidensAnExplicitReference()
    {
        var parent = new MessageReference(20, 6001);
        const string question = "What does this say?";
        Assert.Equal(AutonomyImageOrigin.Trigger, WorldAutonomyVision.SelectOrigin(true, true, MessageType.Reply, parent, 6001, question));
        Assert.Equal(AutonomyImageOrigin.Reply, WorldAutonomyVision.SelectOrigin(false, false, MessageType.Reply, parent, 6001, question));
        Assert.Null(WorldAutonomyVision.SelectOrigin(false, true, MessageType.Reply, parent, 6002, question));
        Assert.Null(WorldAutonomyVision.SelectOrigin(false, true, MessageType.Reply,
            new MessageReference(20, 6001, null, null, MessageReferenceType.Forward), 6001, question));
        Assert.Null(WorldAutonomyVision.SelectOrigin(false, true, MessageType.Default, parent, 6001, question));
        Assert.Equal(AutonomyImageOrigin.Recent, WorldAutonomyVision.SelectOrigin(false, true, MessageType.Default, null, 6001, question));
        Assert.Null(WorldAutonomyVision.SelectOrigin(false, false, MessageType.Default, null, 6001, question));
    }

    [Fact]
    public async Task MissingReply_IsUnknownRatherThanAClaimThereWasAnImage()
    {
        using var cdn = new VisionTestCdn();
        var message = await cdn.Reader().BuildUserMessageAsync("read", new([], MissingReply: true), 6001, 10, "run", "full", default);
        Assert.Empty(message.Contents.OfType<DataContent>());
        Assert.Contains("attachments are unknown", message.Text);
        Assert.Equal(0, cdn.Downloads);
        Assert.Equal("missing_reply", Assert.Single(cdn.Telemetry.Events).ReasonCode);
    }

    [Fact]
    public void RecentImages_StopAtConversationBreakAgeOrDifferentAuthor()
    {
        using var cdn = new VisionTestCdn();
        var now = DateTimeOffset.UtcNow;
        var prior = cdn.Attachment with { Origin = AutonomyImageOrigin.Recent };
        var last = (1UL, now.AddSeconds(-17), (IReadOnlyList<AutonomyImageAttachment>)[prior]);
        var earlier = (1UL, now.AddSeconds(-47), (IReadOnlyList<AutonomyImageAttachment>)[prior with { MessageId = 8 }]);
        Assert.Equal(new ulong[] { 10, 8 }, WorldAutonomyVision.RecentImages([last, earlier, earlier], now).Select(item => item.MessageId));
        Assert.Single(WorldAutonomyVision.RecentImages([last, (2UL, earlier.Item2, earlier.Item3)], now));
        Assert.Empty(WorldAutonomyVision.RecentImages([(1UL, now.AddSeconds(-121), last.Item3)], now));
        Assert.Empty(WorldAutonomyVision.RecentImages([(1UL, now, []), earlier], now));
    }
}

internal sealed class VisionTestCdn : HttpMessageHandler, IHttpClientFactory
{
    public byte[] Png { get; } = ImageReferenceValidationTests.Png(235, 22);
    public AutonomyImageAttachment Attachment => new(10,
        "https://cdn.discordapp.com/attachments/6001/100/image.png?test=sensitive-signature", Png.Length,
        AutonomyImageOrigin.Trigger);
    public InMemoryTelemetrySink Telemetry { get; } = new();
    public int Downloads { get; private set; }
    public WorldAutonomyVision Reader(BotOptions? bot = null, ImageOptions? images = null) =>
        new(this, Options.Create(bot ?? new BotOptions()), Options.Create(images ?? new ImageOptions()), Telemetry);
    public HttpClient CreateClient(string name)
    {
        Assert.Equal(DiscordImageReferenceResolver.HttpClientName, name);
        return new HttpClient(this, disposeHandler: false);
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Downloads++;
        var status = request.RequestUri!.AbsolutePath.EndsWith("missing.png", StringComparison.Ordinal)
            ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Png) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        return Task.FromResult(response);
    }
}