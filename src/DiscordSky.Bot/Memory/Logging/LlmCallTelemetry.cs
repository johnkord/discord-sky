using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001
#pragma warning disable SCME0001

namespace DiscordSky.Bot.Memory.Logging;

public static class LlmCallTelemetry
{
    private static readonly ConditionalWeakTable<ChatOptions, LlmCallMetadata> Metadata = new();

    public static void Tag(
        ChatOptions options,
        string workload,
        LlmWorkloadProfile profile,
        ulong? messageId = null,
        string? evaluationId = null,
        InteractionTraceContext? trace = null)
    {
        Tag(options, new LlmCallMetadata(
            workload,
            profile.ReasoningEffort,
            messageId,
            evaluationId,
            trace));
    }

    internal static void Tag(ChatOptions options, LlmCallMetadata metadata)
    {
        Metadata.Remove(options);
        Metadata.Add(options, metadata);
    }

    internal static (ChatOptions? Forwarded, LlmCallMetadata? Metadata) Prepare(ChatOptions? options)
    {
        if (options is null || !Metadata.TryGetValue(options, out var metadata))
        {
            return (options, null);
        }
        return (options, metadata);
    }
}

/// <summary>Attaches one shared metadata/call-index session to options after outer agents clone them.</summary>
internal sealed class LlmCallTaggingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly LlmCallMetadata _metadata;

    public LlmCallTaggingChatClient(
        IChatClient inner,
        string workload,
        LlmWorkloadProfile profile,
        ulong? messageId = null,
        string? evaluationId = null,
        InteractionTraceContext? trace = null,
        LlmRunUsageAccumulator? usageAccumulator = null)
    {
        _inner = inner;
        _metadata = new LlmCallMetadata(
            workload,
            profile.ReasoningEffort,
            messageId,
            evaluationId,
            trace,
            usageAccumulator);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options is not null)
        {
            LlmCallTelemetry.Tag(options, _metadata);
        }
        return _inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options is not null)
        {
            LlmCallTelemetry.Tag(options, _metadata);
        }
        return _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(LlmCallTaggingChatClient)
            ? this
            : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}

internal sealed class LlmCallMetadata
{
    private int _callIndex;
    private readonly LlmRunUsageAccumulator? _usageAccumulator;

    public LlmCallMetadata(
        string workload,
        string? reasoningEffort,
        ulong? messageId,
        string? evaluationId,
        InteractionTraceContext? trace,
        LlmRunUsageAccumulator? usageAccumulator = null)
    {
        Workload = workload;
        ReasoningEffort = reasoningEffort;
        MessageId = messageId;
        EvaluationId = evaluationId;
        Trace = trace;
        _usageAccumulator = usageAccumulator;
    }

    public string Workload { get; }
    public string? ReasoningEffort { get; }
    public ulong? MessageId { get; }
    public string? EvaluationId { get; }
    public InteractionTraceContext? Trace { get; }
    public int NextCallIndex()
    {
        _usageAccumulator?.RecordCall();
        return Interlocked.Increment(ref _callIndex);
    }

    public void RecordUsage(UsageDetails? usage, long? cacheWriteInputTokens) =>
        _usageAccumulator?.RecordUsage(usage, cacheWriteInputTokens);
}

internal sealed class LlmRunUsageAccumulator
{
    private int _providerCallCount;
    private long _inputTokens;
    private long _outputTokens;
    private long _cachedInputTokens;
    private long _cacheWriteInputTokens;
    private long _reasoningTokens;
    private long _totalTokens;

    public void RecordCall() => Interlocked.Increment(ref _providerCallCount);

    public void RecordUsage(UsageDetails? usage, long? cacheWriteInputTokens = null)
    {
        Add(ref _inputTokens, usage?.InputTokenCount);
        Add(ref _outputTokens, usage?.OutputTokenCount);
        Add(ref _cachedInputTokens, usage?.CachedInputTokenCount);
        Add(ref _cacheWriteInputTokens, cacheWriteInputTokens ?? TelemetryChatClient.GetCacheWriteInputTokens(usage));
        Add(ref _reasoningTokens, usage?.ReasoningTokenCount);
        Add(ref _totalTokens, usage?.TotalTokenCount);
    }

    public LlmRunUsageSnapshot Snapshot() => new(
        Volatile.Read(ref _providerCallCount),
        Volatile.Read(ref _inputTokens),
        Volatile.Read(ref _outputTokens),
        Volatile.Read(ref _cachedInputTokens),
        Volatile.Read(ref _cacheWriteInputTokens),
        Volatile.Read(ref _reasoningTokens),
        Volatile.Read(ref _totalTokens));

    private static void Add(ref long target, long? value)
    {
        if (value.HasValue)
        {
            Interlocked.Add(ref target, value.Value);
        }
    }
}

internal sealed record LlmRunUsageSnapshot(
    int ProviderCallCount,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long ReasoningTokens,
    long TotalTokens);

/// <summary>Records metadata and usage for every call without retaining prompts or response text.</summary>
internal sealed class TelemetryChatClient : IChatClient
{
    private static readonly string[] CacheWriteCountKeys =
    [
        "cache_write_input_tokens",
        "cache_creation_input_tokens",
    ];

    private readonly IChatClient _inner;
    private readonly string _provider;
    private readonly IRecallTelemetrySink _telemetry;

    public TelemetryChatClient(IChatClient inner, string provider, IRecallTelemetrySink telemetry)
    {
        _inner = inner;
        _provider = provider;
        _telemetry = telemetry;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (forwarded, metadata) = LlmCallTelemetry.Prepare(options);
        var callIndex = metadata?.NextCallIndex();
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var response = await _inner.GetResponseAsync(messages, forwarded, cancellationToken);
            var cacheWriteInputTokens = GetCacheWriteInputTokens(response);
            Emit(
                metadata,
                callIndex,
                startedAt,
                "ok",
                response.ModelId ?? forwarded?.ModelId,
                response.ResponseId,
                response.FinishReason?.ToString(),
                response.Usage,
                cacheWriteInputTokens,
                null);
            return response;
        }
        catch (OperationCanceledException)
        {
            Emit(metadata, callIndex, startedAt, "cancelled", forwarded?.ModelId, null, null, null, null, "OperationCanceledException");
            throw;
        }
        catch (Exception ex)
        {
            Emit(metadata, callIndex, startedAt, "error", forwarded?.ModelId, null, null, null, null, ex.GetType().Name);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (forwarded, metadata) = LlmCallTelemetry.Prepare(options);
        var callIndex = metadata?.NextCallIndex();
        var startedAt = Stopwatch.GetTimestamp();
        await using var enumerator = _inner
            .GetStreamingResponseAsync(messages, forwarded, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
            {
                Emit(metadata, callIndex, startedAt, "cancelled", forwarded?.ModelId, null, null, null, null, "OperationCanceledException");
                throw;
            }
            catch (Exception ex)
            {
                Emit(metadata, callIndex, startedAt, "error", forwarded?.ModelId, null, null, null, null, ex.GetType().Name);
                throw;
            }

            if (!hasNext) break;
            yield return enumerator.Current;
        }

        Emit(metadata, callIndex, startedAt, "ok", forwarded?.ModelId, null, null, null, null, null);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(TelemetryChatClient)
            ? this
            : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private void Emit(
        LlmCallMetadata? metadata,
        int? callIndex,
        long startedAt,
        string outcome,
        string? model,
        string? responseId,
        string? finishReason,
        UsageDetails? usage,
        long? cacheWriteInputTokens,
        string? failureClass)
    {
        metadata?.RecordUsage(usage, cacheWriteInputTokens);
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.LlmCall,
            Kind: metadata?.Workload ?? "unclassified",
            Outcome: outcome,
            CallIndex: callIndex,
            MessageId: metadata?.MessageId,
            Provider: _provider,
            Model: model,
            ReasoningEffort: metadata?.ReasoningEffort,
            LatencyMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            EvaluationId: metadata?.EvaluationId,
            Workload: metadata?.Workload ?? "unclassified",
            InputTokens: usage?.InputTokenCount,
            OutputTokens: usage?.OutputTokenCount,
            CachedInputTokens: usage?.CachedInputTokenCount,
            CacheWriteInputTokens: cacheWriteInputTokens ?? GetCacheWriteInputTokens(usage),
            ReasoningTokens: usage?.ReasoningTokenCount,
            TotalTokens: usage?.TotalTokenCount,
            ResponseId: responseId,
            FinishReason: finishReason,
            FailureClass: failureClass,
            OperationId: metadata?.Trace?.OperationId,
            EpisodeId: metadata?.Trace?.EpisodeId,
            EpisodeSchemaVersion: metadata?.Trace?.EpisodeSchemaVersion,
            EvidenceDigest: metadata?.Trace?.EvidenceDigest,
            ProjectionDigest: metadata?.Trace?.ProjectionDigest));
    }

    internal static long? GetCacheWriteInputTokens(UsageDetails? usage)
    {
        if (usage?.AdditionalCounts is not { } counts)
        {
            return null;
        }

        foreach (var key in CacheWriteCountKeys)
        {
            if (counts.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    internal static long? GetCacheWriteInputTokens(ChatResponse response)
    {
        var usageValue = GetCacheWriteInputTokens(response.Usage);
        if (usageValue.HasValue)
        {
            return usageValue;
        }

        return response.RawRepresentation is ResponseResult nativeResponse &&
            nativeResponse.Patch.TryGetValue(
                "$.usage.input_tokens_details.cache_write_tokens"u8,
                out int cacheWriteTokens)
            ? cacheWriteTokens
            : null;
    }
}