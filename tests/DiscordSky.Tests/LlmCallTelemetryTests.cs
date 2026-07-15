using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.AI;

namespace DiscordSky.Tests;

public sealed class LlmCallTelemetryTests
{
    [Fact]
    public async Task Call_EmitsUsageAndCorrelationWithoutForwardingMarker()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
        {
            ModelId = "returned-model",
            ResponseId = "resp-1",
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 40,
                CachedInputTokenCount = 25,
                ReasoningTokenCount = 10,
                TotalTokenCount = 140,
            },
        };
        var inner = new StubChatClient(response);
        var telemetry = new InMemoryTelemetrySink();
        using var client = new TelemetryChatClient(inner, "OpenAI", telemetry);
        var options = new ChatOptions { ModelId = "requested-model" };
        LlmCallTelemetry.Tag(
            options,
            "ambient_reply",
            new LlmWorkloadProfile("requested-model", "low"),
            messageId: 123,
            evaluationId: "eval-1");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "secret prompt")], options);

        Assert.False(inner.Options?.AdditionalProperties?.Any() ?? false);
        var evt = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.LlmCall, evt.EventType);
        Assert.Equal("ok", evt.Outcome);
        Assert.Equal("OpenAI", evt.Provider);
        Assert.Equal("returned-model", evt.Model);
        Assert.Equal("ambient_reply", evt.Workload);
        Assert.Equal("low", evt.ReasoningEffort);
        Assert.Equal((ulong)123, evt.MessageId);
        Assert.Equal("eval-1", evt.EvaluationId);
        Assert.Equal(1, evt.CallIndex);
        Assert.Equal(100, evt.InputTokens);
        Assert.Equal(40, evt.OutputTokens);
        Assert.Equal(25, evt.CachedInputTokens);
        Assert.Equal(10, evt.ReasoningTokens);
        Assert.Equal(140, evt.TotalTokens);
        Assert.Equal("resp-1", evt.ResponseId);
        Assert.Null(evt.FailureClass);
    }

    [Fact]
    public async Task Call_FailureIsRecordedAndRethrown()
    {
        var telemetry = new InMemoryTelemetrySink();
        using var client = new TelemetryChatClient(new ThrowingChatClient(), "xAI", telemetry);
        var options = new ChatOptions { ModelId = "grok" };
        LlmCallTelemetry.Tag(options, "cold_open", new LlmWorkloadProfile("grok", "medium"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "prompt")], options));

        var evt = Assert.Single(telemetry.Events);
        Assert.Equal("error", evt.Outcome);
        Assert.Equal("InvalidOperationException", evt.FailureClass);
        Assert.Equal("cold_open", evt.Workload);
        Assert.Equal("grok", evt.Model);
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly ChatResponse _response;

        public StubChatClient(ChatResponse response) => _response = response;

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(_response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("provider failed");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}