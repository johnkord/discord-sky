using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory.Logging;

/// <summary>
/// Durable telemetry sink for the recall feature. See docs/recall_feature_review_2026-05-26.md §7.1.
///
/// Purpose: capture recall-feature events to disk so they survive pod rotation.
/// `kubectl logs` evaporates on every deploy; the PVC does not.
///
/// Data policy (owner decision, 2026-07-05): this telemetry is the bot OWNER's private record, kept on
/// the owner's own PVC and pruned after <see cref="TelemetryOptions.RetentionDays"/> days, used to review
/// and improve the bot's interactions. Event payloads MAY include real display names and raw message text
/// (for example, the room context a cold open reacted to and the line it drafted). Hashed user IDs (via
/// <see cref="UserIdHash"/>) remain available for events that do not need raw identities. This supersedes
/// the earlier hashed-only rule.
/// </summary>
public interface IRecallTelemetrySink
{
    /// <summary>
    /// Record one event. Non-blocking. If the underlying channel is full or the sink is disposed,
    /// the event is dropped silently — telemetry must never break the request path.
    /// </summary>
    void Emit(TelemetryEvent evt);
}

/// <summary>
/// One telemetry event. Discriminated by <see cref="EventType"/>. Fields are nullable because not
/// every event populates every field; the JSON writer omits nulls.
/// </summary>
public sealed record TelemetryEvent(
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("event")] string EventType,
    [property: JsonPropertyName("user")] string? UserHash = null,
    [property: JsonPropertyName("channel")] string? Channel = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("outcome")] string? Outcome = null,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("total")] int? Total = null,
    [property: JsonPropertyName("truncated")] bool? Truncated = null,
    [property: JsonPropertyName("call_index")] int? CallIndex = null,
    [property: JsonPropertyName("top_score")] double? TopScore = null,
    [property: JsonPropertyName("query_present")] bool? QueryPresent = null,
    [property: JsonPropertyName("message_id")] ulong? MessageId = null,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("before")] int? Before = null,
    [property: JsonPropertyName("after")] int? After = null,
    [property: JsonPropertyName("room")] IReadOnlyList<string>? Room = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null,
    [property: JsonPropertyName("latency_ms")] long? LatencyMs = null,
    [property: JsonPropertyName("baseline_outcome")] string? BaselineOutcome = null,
    [property: JsonPropertyName("baseline_score")] double? BaselineScore = null,
    [property: JsonPropertyName("evaluation_id")] string? EvaluationId = null,
    [property: JsonPropertyName("opportunity_at")] DateTimeOffset? OpportunityAt = null,
    [property: JsonPropertyName("workload")] string? Workload = null,
    [property: JsonPropertyName("input_tokens")] long? InputTokens = null,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens = null,
    [property: JsonPropertyName("cached_input_tokens")] long? CachedInputTokens = null,
    [property: JsonPropertyName("reasoning_tokens")] long? ReasoningTokens = null,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens = null,
    [property: JsonPropertyName("response_id")] string? ResponseId = null,
    [property: JsonPropertyName("finish_reason")] string? FinishReason = null,
    [property: JsonPropertyName("failure_class")] string? FailureClass = null,
    [property: JsonPropertyName("visual_worth")] double? VisualWorth = null,
    [property: JsonPropertyName("visual_hook")] string? VisualHook = null
);

/// <summary>Canonical event-type string constants. Use these instead of string literals at call sites.</summary>
public static class TelemetryEventTypes
{
    public const string PersonaInvoked = "persona_invoked";
    public const string RecallHintEmitted = "recall_hint_emitted";
    public const string RecallToolOk = "recall_tool_ok";
    public const string RecallToolNoNotes = "recall_tool_no_notes";
    public const string RecallToolUnknownUser = "recall_tool_unknown_user";
    public const string ConsolidationOk = "consolidation_ok";
    public const string ConsolidationFail = "consolidation_fail";
    public const string CircuitBreakerOpened = "circuit_breaker_opened";
    public const string GatewayDisconnect = "gateway_disconnect";
    public const string ScamDetected = "scam_detected";
    public const string AutoModAction = "automod_action";
    public const string NewAccountFlag = "new_account_flag";
    public const string BanObserved = "ban_observed";
    public const string ReactionJudged = "reaction_judged";
    public const string EmpireTick = "empire_tick";
    public const string ImpulseJudged = "impulse_judged";
    public const string ColdOpen = "cold_open";
    public const string ColdOpenCritique = "cold_open_critique";
    public const string ColdOpenProviderShadow = "cold_open_provider_shadow";
    public const string LlmCall = "llm_call";
}

/// <summary>Test/CI default. Discards events.</summary>
public sealed class NoOpTelemetrySink : IRecallTelemetrySink
{
    public void Emit(TelemetryEvent evt) { /* drop */ }
}

/// <summary>Test helper: keeps events in memory for assertions.</summary>
public sealed class InMemoryTelemetrySink : IRecallTelemetrySink
{
    private readonly ConcurrentQueue<TelemetryEvent> _events = new();
    public IReadOnlyCollection<TelemetryEvent> Events => _events;
    public void Emit(TelemetryEvent evt) => _events.Enqueue(evt);
}

/// <summary>
/// File-backed implementation. Writes daily JSONL files to <c>{BaseDirectory}/recall-YYYY-MM-DD.jsonl</c>.
///
/// Writes are <b>synchronous</b> and <b>fsynced</b> per event. The earlier channel-buffered design
/// lost events when the pod was terminated mid-drain (observed on the 2026-05-26 rollover). At our
/// volume (~10 events/day) the per-write disk cost is invisible; durability matters more than throughput.
///
/// Retention: on startup, deletes any <c>recall-*.jsonl</c> older than <see cref="TelemetryOptions.RetentionDays"/>.
/// </summary>
public sealed class FileBackedTelemetrySink : IRecallTelemetrySink, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TelemetryOptions _options;
    private readonly ILogger<FileBackedTelemetrySink> _logger;
    private readonly object _writeLock = new();

    public FileBackedTelemetrySink(IOptions<TelemetryOptions> options, ILogger<FileBackedTelemetrySink> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void Emit(TelemetryEvent evt)
    {
        try
        {
            var path = PathForDate(evt.Timestamp);
            var line = JsonSerializer.Serialize(evt, JsonOptions) + "\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            lock (_writeLock)
            {
                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.Read,
                    bufferSize: 4096, FileOptions.WriteThrough);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            // Telemetry must never break the request path. Log and drop.
            _logger.LogWarning(ex, "Telemetry write failed for event {EventType}; dropping.", evt.EventType);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_options.BaseDirectory);
            PruneOldFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telemetry startup: failed to prepare directory {Dir} or prune; sink will still attempt to write.", _options.BaseDirectory);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string PathForDate(DateTimeOffset ts)
    {
        var date = ts.UtcDateTime.ToString("yyyy-MM-dd");
        return Path.Combine(_options.BaseDirectory, $"recall-{date}.jsonl");
    }

    private void PruneOldFiles()
    {
        if (!Directory.Exists(_options.BaseDirectory)) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
        var pruned = 0;
        foreach (var file in Directory.EnumerateFiles(_options.BaseDirectory, "recall-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // "recall-YYYY-MM-DD"
            if (name.Length < 17) continue; // "recall-YYYY-MM-DD" = 17 chars
            var datePart = name[7..]; // "YYYY-MM-DD"
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var d)
                && d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < cutoff.UtcDateTime)
            {
                try { File.Delete(file); pruned++; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Telemetry prune: failed to delete {File}", file);
                }
            }
        }
        if (pruned > 0)
        {
            _logger.LogInformation("Telemetry prune: deleted {Pruned} file(s) older than {Days} days from {Dir}",
                pruned, _options.RetentionDays, _options.BaseDirectory);
        }
    }
}

/// <summary>Configuration for <see cref="FileBackedTelemetrySink"/>. Bound from <c>Telemetry:</c> section.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Directory for daily JSONL files. Defaults to <c>data/telemetry</c> resolved relative to CWD.</summary>
    public string BaseDirectory { get; set; } = Path.Combine("data", "telemetry");

    /// <summary>Days to retain files. Older files are deleted on startup.</summary>
    public int RetentionDays { get; set; } = 30;
}
