using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
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
/// PII policy (see review §10 #6): event payloads include hashed user IDs (via <see cref="UserIdHash"/>)
/// and counts. They MUST NOT include raw note content. Content hashes and 40-char prefixes are acceptable.
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
    [property: JsonPropertyName("note")] string? Note = null
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
/// Buffered via an in-memory channel; a single background loop drains to disk. Non-blocking emit.
///
/// Retention: on startup, deletes any <c>recall-*.jsonl</c> older than <see cref="TelemetryOptions.RetentionDays"/>.
/// </summary>
public sealed class FileBackedTelemetrySink : IRecallTelemetrySink, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TelemetryOptions _options;
    private readonly ILogger<FileBackedTelemetrySink> _logger;
    private readonly Channel<TelemetryEvent> _channel;
    private Task? _drainTask;
    private CancellationTokenSource? _stopCts;

    public FileBackedTelemetrySink(IOptions<TelemetryOptions> options, ILogger<FileBackedTelemetrySink> logger)
    {
        _options = options.Value;
        _logger = logger;
        _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(_options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Emit(TelemetryEvent evt)
    {
        // Non-blocking; if the channel is full DropOldest fires.
        _channel.Writer.TryWrite(evt);
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

        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _drainTask = Task.Run(() => DrainLoopAsync(_stopCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Complete the writer; drain loop exits naturally when WaitToReadAsync returns false.
        // Only cancel the inner CTS if shutdown is taking too long — otherwise we'd abandon
        // events still in the channel.
        _channel.Writer.TryComplete();
        if (_drainTask is not null)
        {
            try
            {
                await _drainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller-provided cancellation fired: force the drain loop down.
                _stopCts?.Cancel();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopCts?.Dispose();
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var evt))
            {
                try
                {
                    var path = PathForDate(evt.Timestamp);
                    var line = JsonSerializer.Serialize(evt, JsonOptions);
                    await File.AppendAllTextAsync(path, line + "\n", ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Never let a write failure crash the drain loop. Log once per error and continue.
                    _logger.LogWarning(ex, "Telemetry write failed for event {EventType}; dropping.", evt.EventType);
                }
            }
        }
    }

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

    /// <summary>In-memory buffer size. Events beyond this are dropped (oldest first) under load.</summary>
    public int BufferCapacity { get; set; } = 1024;
}
