using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.AI;

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
        Metadata.Remove(options);
        Metadata.Add(options, new LlmCallMetadata(
            workload,
            profile.ReasoningEffort,
            messageId,
            evaluationId,
            trace));
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

internal sealed class LlmCallMetadata
{
    private int _callIndex;

    public LlmCallMetadata(
        string workload,
        string? reasoningEffort,
        ulong? messageId,
        string? evaluationId,
        InteractionTraceContext? trace)
    {
        Workload = workload;
        ReasoningEffort = reasoningEffort;
        MessageId = messageId;
        EvaluationId = evaluationId;
        Trace = trace;
    }

    public string Workload { get; }
    public string? ReasoningEffort { get; }
    public ulong? MessageId { get; }
    public string? EvaluationId { get; }
    public InteractionTraceContext? Trace { get; }
    public int NextCallIndex() => Interlocked.Increment(ref _callIndex);
}

/// <summary>Records metadata and usage for every call without retaining prompts or response text.</summary>
internal sealed class TelemetryChatClient : IChatClient
{
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
            Emit(
                metadata,
                callIndex,
                startedAt,
                "ok",
                response.ModelId ?? forwarded?.ModelId,
                response.ResponseId,
                response.FinishReason?.ToString(),
                response.Usage,
                null);
            return response;
        }
        catch (OperationCanceledException)
        {
            Emit(metadata, callIndex, startedAt, "cancelled", forwarded?.ModelId, null, null, null, "OperationCanceledException");
            throw;
        }
        catch (Exception ex)
        {
            Emit(metadata, callIndex, startedAt, "error", forwarded?.ModelId, null, null, null, ex.GetType().Name);
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
                Emit(metadata, callIndex, startedAt, "cancelled", forwarded?.ModelId, null, null, null, "OperationCanceledException");
                throw;
            }
            catch (Exception ex)
            {
                Emit(metadata, callIndex, startedAt, "error", forwarded?.ModelId, null, null, null, ex.GetType().Name);
                throw;
            }

            if (!hasNext) break;
            yield return enumerator.Current;
        }

        Emit(metadata, callIndex, startedAt, "ok", forwarded?.ModelId, null, null, null, null);
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
        string? failureClass)
    {
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
}