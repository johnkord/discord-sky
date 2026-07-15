using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class MediaSemanticCacheTests
{
    [Fact]
    public async Task DescribeAsync_AnalyzesOnceAndCachesByMessageId()
    {
        var client = new StubChatClient(new string('x', 700));
        var cache = new MediaSemanticCache(
            client,
            new TestOptionsMonitor<LlmOptions>(Options()),
            NullLogger<MediaSemanticCache>.Instance);
        var images = new[]
        {
            new ChannelImage
            {
                Url = new Uri("https://cdn.discordapp.com/meme.png"),
                Filename = "meme.png",
                Source = "attachment",
                Timestamp = DateTimeOffset.UtcNow,
            },
        };

        var first = await cache.DescribeAsync(123, DateTimeOffset.UtcNow, "Attachments: meme.png", images, CancellationToken.None);
        var second = await cache.DescribeAsync(123, DateTimeOffset.UtcNow, "changed metadata", images, CancellationToken.None);

        Assert.True(first.Analyzed);
        Assert.Equal(500, first.Summary!.Length);
        Assert.Equal(first, second);
        Assert.Equal(1, client.CallCount);
        Assert.Single(client.CapturedContents!.OfType<UriContent>());
    }

    [Fact]
    public async Task DescribeAsync_NoImagesDoesNotCallModel()
    {
        var client = new StubChatClient("unused");
        var cache = new MediaSemanticCache(
            client,
            new TestOptionsMonitor<LlmOptions>(Options()),
            NullLogger<MediaSemanticCache>.Instance);

        var result = await cache.DescribeAsync(
            123, DateTimeOffset.UtcNow, "Attachments: clip.mp4", [], CancellationToken.None);

        Assert.Equal(MediaSemanticResult.None, result);
        Assert.Equal(0, client.CallCount);
    }

    private static LlmOptions Options() => new()
    {
        ActiveProvider = "OpenAI",
        Providers = new Dictionary<string, LlmProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = new() { ChatModel = "main", UtilityModel = "mini" },
        },
    };

    private sealed class StubChatClient : IChatClient
    {
        private readonly string _response;

        public StubChatClient(string response) => _response = response;

        public int CallCount { get; private set; }
        public IList<AIContent>? CapturedContents { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CapturedContents = messages.Single().Contents;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}