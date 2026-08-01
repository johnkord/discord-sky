using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.AI;

namespace DiscordSky.Tests;

public sealed class TimeoutChatClientTests
{
    [Fact]
    public async Task ProviderDeadline_ThrowsTimeoutException()
    {
        using var client = new TimeoutChatClient(
            new BlockingChatClient(),
            TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("deadline", error.Message);
    }

    [Fact]
    public async Task CallerCancellation_RemainsOperationCanceledException()
    {
        using var client = new TimeoutChatClient(
            new BlockingChatClient(),
            TimeSpan.FromMinutes(15));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: cts.Token));
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after an infinite cancellable delay.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}