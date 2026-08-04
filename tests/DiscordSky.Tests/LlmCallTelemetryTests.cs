using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using System.ClientModel.Primitives;

#pragma warning disable OPENAI001

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
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["cache_creation_input_tokens"] = 15,
                },
            },
        };
        var inner = new StubChatClient(response);
        var telemetry = new InMemoryTelemetrySink();
        using var client = new TelemetryChatClient(inner, "OpenAI", telemetry);
        var usage = new LlmRunUsageAccumulator();
        var options = new ChatOptions { ModelId = "requested-model" };
        using var taggingClient = new LlmCallTaggingChatClient(
            client,
            "ambient_reply",
            new LlmWorkloadProfile("requested-model", "low"),
            messageId: 123,
            evaluationId: "eval-1",
            trace: new InteractionTraceContext(
                EpisodeId: "episode-1",
                OperationId: "operation-1",
                EpisodeSchemaVersion: 1,
                EvidenceDigest: "evidence-1",
                ProjectionDigest: "projection-1"),
            usageAccumulator: usage);

        await taggingClient.GetResponseAsync([new ChatMessage(ChatRole.User, "secret prompt")], options);

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
        Assert.Equal("episode-1", evt.EpisodeId);
        Assert.Equal("operation-1", evt.OperationId);
        Assert.Equal(1, evt.EpisodeSchemaVersion);
        Assert.Equal("evidence-1", evt.EvidenceDigest);
        Assert.Equal("projection-1", evt.ProjectionDigest);
        Assert.Equal(1, evt.CallIndex);
        Assert.Equal(100, evt.InputTokens);
        Assert.Equal(40, evt.OutputTokens);
        Assert.Equal(25, evt.CachedInputTokens);
        Assert.Equal(15, evt.CacheWriteInputTokens);
        Assert.Equal(10, evt.ReasoningTokens);
        Assert.Equal(140, evt.TotalTokens);
        Assert.Equal("resp-1", evt.ResponseId);
        Assert.Null(evt.FailureClass);
        Assert.Equal(
            new LlmRunUsageSnapshot(1, 100, 40, 25, 15, 10, 140),
            usage.Snapshot());
    }

    [Fact]
    public void CacheWriteTokens_UnknownOrAbsentKeysRemainUnknown()
    {
        Assert.Null(TelemetryChatClient.GetCacheWriteInputTokens((UsageDetails?)null));
        Assert.Null(TelemetryChatClient.GetCacheWriteInputTokens(new UsageDetails()));
        Assert.Null(TelemetryChatClient.GetCacheWriteInputTokens(new UsageDetails
        {
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["provider_specific_count"] = 42,
            },
        }));
    }

    [Fact]
    public void CacheWriteTokens_ReadsExactNativeResponsesUsagePath()
    {
        var native = ModelReaderWriter.Read<ResponseResult>(
            BinaryData.FromString("""
                {
                  "id": "resp_test",
                  "object": "response",
                  "created_at": 0,
                  "status": "completed",
                  "model": "gpt-5.6",
                  "output": [],
                  "parallel_tool_calls": false,
                  "tool_choice": "auto",
                  "tools": [],
                  "usage": {
                    "input_tokens": 1200,
                    "input_tokens_details": { "cached_tokens": 0, "cache_write_tokens": 1024 },
                    "output_tokens": 5,
                    "output_tokens_details": { "reasoning_tokens": 0 },
                    "total_tokens": 1205
                  }
                }
                """),
            ModelReaderWriterOptions.Json);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
        {
            RawRepresentation = native,
            Usage = new UsageDetails { InputTokenCount = 1200 },
        };

        Assert.Equal(1024, TelemetryChatClient.GetCacheWriteInputTokens(response));
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

    [Fact]
    public async Task Call_ProviderGuardBlocksBeforeInnerProviderInvocation()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "should not run"));
        var inner = new StubChatClient(response);
        var telemetry = new InMemoryTelemetrySink();
        var guard = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance,
            options: new LlmProviderGuardOptions
            {
                HourlyUsdLimit = 0.10,
                DailyUsdLimit = 1.0,
                StatePath = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.json"),
            });
        using var client = new TelemetryChatClient(inner, "OpenAI", telemetry, guard);
        var options = new ChatOptions { ModelId = "gpt-5.6-sol" };
        LlmCallTelemetry.Tag(options, "main_reply", new LlmWorkloadProfile("gpt-5.6-sol", "ExtraHigh"));

        var exception = await Assert.ThrowsAsync<LlmProviderBlockedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "prompt")], options));

        Assert.Equal("hourly_cost_budget_exhausted", exception.Reason);
        Assert.Equal(0, inner.CallCount);
        var evt = Assert.Single(telemetry.Events);
        Assert.Equal("error", evt.Outcome);
        Assert.Equal(nameof(LlmProviderBlockedException), evt.FailureClass);
    }

    [Fact]
    public async Task StreamingCall_EarlyDisposalReleasesProviderReservation()
    {
        var guard = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance,
            options: new LlmProviderGuardOptions
            {
                HourlyUsdLimit = 1.0,
                DailyUsdLimit = 3.0,
                StatePath = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.json"),
            });
        using var client = new TelemetryChatClient(
            new StreamingChatClient(),
            "OpenAI",
            new InMemoryTelemetrySink(),
            guard);
        var options = new ChatOptions { ModelId = "gpt-5.6-sol" };

        await using (var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "prompt")], options)
            .GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        Assert.True(guard.TryBeginCall("gpt-5.6-sol", true, out var lease, out _));
        guard.RecordCallFailure(lease, new InvalidOperationException("test cleanup"));
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly ChatResponse _response;

        public StubChatClient(ChatResponse response) => _response = response;

        public ChatOptions? Options { get; private set; }

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
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

    private sealed class StreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "second");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}