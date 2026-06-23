using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory.Logging;

/// <summary>
/// Durable conversation transcript sink. Captures the full prompt the model received and the reply it
/// produced, so reply quality (persona fidelity, relevance, ambient acceptance) can actually be
/// evaluated. Before this, the bot logged nothing about its own outputs, so quality was unmeasurable.
/// See docs/improvement_opportunities_2026-06-10.md F8/H2.
///
/// PRIVACY: unlike <see cref="IRecallTelemetrySink"/> (which hashes user IDs and stores no content),
/// transcripts contain raw message content and raw user IDs by design — that is the point of the
/// feature. It is therefore <b>off by default</b> and must be explicitly enabled via
/// <c>Transcript:Enabled</c>. Files live on the PVC and are pruned after
/// <see cref="TranscriptOptions.RetentionDays"/> days.
/// </summary>
public interface ITranscriptSink
{
    /// <summary>Record one prompt/reply pair. Non-blocking-ish: a single fsync append at this bot's volume.</summary>
    void Record(TranscriptEntry entry);
}

/// <summary>One logged turn. Serialized as a single JSON line.</summary>
public sealed record TranscriptEntry(
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("user_id")] ulong UserId,
    [property: JsonPropertyName("user")] string UserDisplayName,
    [property: JsonPropertyName("channel_id")] ulong ChannelId,
    [property: JsonPropertyName("channel")] string? ChannelName,
    [property: JsonPropertyName("persona")] string Persona,
    [property: JsonPropertyName("kind")] string InvocationKind,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("reply")] string Reply);

/// <summary>Default sink used when transcript logging is disabled or in tests. Discards entries.</summary>
public sealed class NoOpTranscriptSink : ITranscriptSink
{
    public void Record(TranscriptEntry entry) { /* drop */ }
}

/// <summary>
/// File-backed transcript sink. Writes daily JSONL files to
/// <c>{BaseDirectory}/transcript-YYYY-MM-DD.jsonl</c>, synchronously fsynced per entry (durable across
/// pod rotation, the same rationale as the telemetry sink). Prunes files older than the retention
/// window on startup. When disabled, <see cref="Record"/> is a no-op and no files are created.
/// </summary>
public sealed class FileBackedTranscriptSink : ITranscriptSink, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TranscriptOptions _options;
    private readonly ILogger<FileBackedTranscriptSink> _logger;
    private readonly object _writeLock = new();

    public FileBackedTranscriptSink(IOptions<TranscriptOptions> options, ILogger<FileBackedTranscriptSink> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void Record(TranscriptEntry entry)
    {
        if (!_options.Enabled) return;

        try
        {
            var path = PathForDate(entry.Timestamp);
            var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
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
            // Logging quality data must never break the reply path. Log and drop.
            _logger.LogWarning(ex, "Transcript write failed; dropping entry.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Transcript logging disabled (Transcript:Enabled=false).");
            return Task.CompletedTask;
        }

        try
        {
            Directory.CreateDirectory(_options.BaseDirectory);
            PruneOldFiles();
            _logger.LogInformation(
                "Transcript logging ENABLED at \"{Dir}\" (retention {Days}d). Prompts and replies include raw user content.",
                _options.BaseDirectory, _options.RetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcript startup: failed to prepare directory {Dir} or prune.", _options.BaseDirectory);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string PathForDate(DateTimeOffset ts)
    {
        var date = ts.UtcDateTime.ToString("yyyy-MM-dd");
        return Path.Combine(_options.BaseDirectory, $"transcript-{date}.jsonl");
    }

    private void PruneOldFiles()
    {
        if (!Directory.Exists(_options.BaseDirectory)) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
        var pruned = 0;
        foreach (var file in Directory.EnumerateFiles(_options.BaseDirectory, "transcript-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // "transcript-YYYY-MM-DD"
            if (name.Length < 21) continue; // "transcript-YYYY-MM-DD" = 21 chars
            var datePart = name[11..]; // "YYYY-MM-DD"
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var d)
                && d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < cutoff.UtcDateTime)
            {
                try { File.Delete(file); pruned++; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Transcript prune: failed to delete {File}", file);
                }
            }
        }
        if (pruned > 0)
        {
            _logger.LogInformation("Transcript prune: deleted {Pruned} file(s) older than {Days} days.",
                pruned, _options.RetentionDays);
        }
    }
}

/// <summary>Configuration for <see cref="FileBackedTranscriptSink"/>. Bound from <c>Transcript:</c> section.</summary>
public sealed class TranscriptOptions
{
    public const string SectionName = "Transcript";

    /// <summary>
    /// Master switch. Off by default because transcripts contain raw user content and IDs.
    /// Enable deliberately (and document the privacy posture) before relying on it.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Directory for daily JSONL files. Should sit on the PVC so it survives restarts.</summary>
    public string BaseDirectory { get; set; } = Path.Combine("data", "transcripts");

    /// <summary>Days to retain transcript files. Older files are deleted on startup.</summary>
    public int RetentionDays { get; set; } = 14;
}
