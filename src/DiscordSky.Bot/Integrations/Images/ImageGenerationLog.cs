using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Images;

/// <summary>
/// Durable record of one image-generation attempt. Serialized as a single JSON line. Carries metadata
/// and estimated cost only; the image bytes are never persisted by us (Discord hosts the image).
/// </summary>
public sealed record ImageGenerationRecord(
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("user")] string? UserHash,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("quality")] string Quality,
    [property: JsonPropertyName("est_cost_usd")] double EstCostUsd,
    [property: JsonPropertyName("latency_ms")] long LatencyMs,
    [property: JsonPropertyName("outcome")] string Outcome)
{
    public const string OutcomeOk = "ok";
    public const string OutcomeRefused = "refused";
    public const string OutcomeModerationBlocked = "moderation_blocked";
    public const string OutcomeError = "error";
}

/// <summary>
/// Durable, restart-surviving log of image generations. It is both the observability trail and the
/// data source for the budget's daily cap and monthly spend guard (docs/image_generation_design.md
/// section 5): a crash loop must not be able to blow the budget, so the cap is derived from disk, not
/// an in-memory counter.
/// </summary>
public interface IImageGenerationLog
{
    /// <summary>Append one record. No-op when disabled; never throws into the request path.</summary>
    void Record(ImageGenerationRecord record);

    /// <summary>Number of successful generations on the given UTC day (read back from disk).</summary>
    int CountSuccessesOnUtcDay(DateOnly utcDay);

    /// <summary>Sum of estimated cost of successful generations in the UTC month containing <paramref name="now"/>.</summary>
    double SumSuccessCostInUtcMonth(DateTimeOffset now);
}

/// <summary>Default used in tests and when image generation is disabled. Records nothing, counts nothing.</summary>
public sealed class NoOpImageGenerationLog : IImageGenerationLog
{
    public void Record(ImageGenerationRecord record) { /* drop */ }
    public int CountSuccessesOnUtcDay(DateOnly utcDay) => 0;
    public double SumSuccessCostInUtcMonth(DateTimeOffset now) => 0.0;
}

/// <summary>
/// File-backed log. Writes daily JSONL to <c>{BaseDirectory}/image-gen-YYYY-MM-DD.jsonl</c>, fsynced per
/// entry (durable across pod rotation, same rationale as the telemetry and reaction sinks). Prunes files
/// older than the retention window on startup. The read-back methods parse the relevant day/month files.
/// </summary>
public sealed class FileBackedImageGenerationLog : IImageGenerationLog, IHostedService
{
    private const string FilePrefix = "image-gen-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ImageOptions _options;
    private readonly ILogger<FileBackedImageGenerationLog> _logger;
    private readonly object _writeLock = new();

    public FileBackedImageGenerationLog(IOptions<ImageOptions> options, ILogger<FileBackedImageGenerationLog> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void Record(ImageGenerationRecord record)
    {
        try
        {
            var path = PathForDate(record.Timestamp);
            var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
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
            // The spend log must never break the gateway path. Log and drop.
            _logger.LogWarning(ex, "Image-log write failed; dropping entry.");
        }
    }

    public int CountSuccessesOnUtcDay(DateOnly utcDay)
    {
        var path = Path.Combine(_options.BaseDirectory, $"{FilePrefix}{utcDay:yyyy-MM-dd}.jsonl");
        var count = 0;
        foreach (var record in ReadRecords(path))
        {
            if (record.Outcome == ImageGenerationRecord.OutcomeOk) count++;
        }
        return count;
    }

    public double SumSuccessCostInUtcMonth(DateTimeOffset now)
    {
        if (!Directory.Exists(_options.BaseDirectory)) return 0.0;
        var monthPrefix = $"{FilePrefix}{now.UtcDateTime:yyyy-MM}";
        var sum = 0.0;
        foreach (var file in Directory.EnumerateFiles(_options.BaseDirectory, $"{monthPrefix}*.jsonl"))
        {
            foreach (var record in ReadRecords(file))
            {
                if (record.Outcome == ImageGenerationRecord.OutcomeOk) sum += record.EstCostUsd;
            }
        }
        return sum;
    }

    private IEnumerable<ImageGenerationRecord> ReadRecords(string path)
    {
        if (!File.Exists(path)) yield break;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image-log read failed for {Path}; treating as empty.", path);
            yield break;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ImageGenerationRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize<ImageGenerationRecord>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // A torn final line (crash mid-write) or a future schema change must not break the cap.
            }
            if (record is not null) yield return record;
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
            _logger.LogWarning(ex, "Image-log startup: failed to prepare {Dir} or prune; will still attempt to write.", _options.BaseDirectory);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string PathForDate(DateTimeOffset ts)
    {
        var date = ts.UtcDateTime.ToString("yyyy-MM-dd");
        return Path.Combine(_options.BaseDirectory, $"{FilePrefix}{date}.jsonl");
    }

    private void PruneOldFiles()
    {
        if (!Directory.Exists(_options.BaseDirectory)) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
        var pruned = 0;
        foreach (var file in Directory.EnumerateFiles(_options.BaseDirectory, $"{FilePrefix}*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // "image-gen-YYYY-MM-DD"
            if (name.Length < FilePrefix.Length + 10) continue;
            var datePart = name[FilePrefix.Length..];
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var d)
                && d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < cutoff.UtcDateTime)
            {
                try { File.Delete(file); pruned++; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Image-log prune: failed to delete {File}", file);
                }
            }
        }
        if (pruned > 0)
        {
            _logger.LogInformation("Image-log prune: deleted {Pruned} file(s) older than {Days} days.",
                pruned, _options.RetentionDays);
        }
    }
}
