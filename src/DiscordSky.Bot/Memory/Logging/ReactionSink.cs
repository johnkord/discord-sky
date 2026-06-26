using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory.Logging;

/// <summary>
/// Durable engagement sink: records the reactions the friend group leaves on the bot's own messages.
/// This is the first real reception signal (fun_assessment_2026-06-25 P1). Before this, quality was
/// inferred only from traffic and direct replies; now we can see what actually landed (which reply got
/// a laugh react, which got a thumbs down), and it is the calibration data the pairwise LLM judge (P3)
/// needs.
///
/// <para>
/// PRIVACY: like transcripts, this lives on the PVC and contains raw user IDs (the reactor) plus a short
/// excerpt of the bot's own reply. It carries no third-party message content. Reactions on non-bot
/// messages are never recorded.
/// </para>
/// </summary>
public interface IReactionSink
{
    /// <summary>Record one reaction add/remove on a bot message. No-op unless enabled.</summary>
    void Record(ReactionEvent reaction);
}

/// <summary>One reaction event on a bot message. Serialized as a single JSON line.</summary>
public sealed record ReactionEvent(
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("emote")] string Emote,
    [property: JsonPropertyName("reactor_id")] ulong ReactorUserId,
    [property: JsonPropertyName("channel_id")] ulong ChannelId,
    [property: JsonPropertyName("guild_id")] ulong? GuildId,
    [property: JsonPropertyName("message_id")] ulong MessageId,
    [property: JsonPropertyName("persona")] string? Persona,
    [property: JsonPropertyName("reply_excerpt")] string? ReplyExcerpt);

/// <summary>Default sink used when reaction logging is disabled or in tests. Discards entries.</summary>
public sealed class NoOpReactionSink : IReactionSink
{
    public void Record(ReactionEvent reaction) { /* drop */ }
}

/// <summary>
/// File-backed reaction sink. Writes daily JSONL to <c>{BaseDirectory}/reactions-YYYY-MM-DD.jsonl</c>,
/// fsynced per entry (durable across pod rotation, same rationale as the transcript sink). Prunes files
/// older than the retention window on startup.
/// </summary>
public sealed class FileBackedReactionSink : IReactionSink, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ReactionOptions _options;
    private readonly ILogger<FileBackedReactionSink> _logger;
    private readonly object _writeLock = new();

    public FileBackedReactionSink(IOptions<ReactionOptions> options, ILogger<FileBackedReactionSink> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void Record(ReactionEvent reaction)
    {
        if (!_options.Enabled) return;

        try
        {
            var path = PathForDate(reaction.Timestamp);
            var line = JsonSerializer.Serialize(reaction, JsonOptions) + "\n";
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
            // Reception data must never break the gateway path. Log and drop.
            _logger.LogWarning(ex, "Reaction write failed; dropping entry.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Reaction logging disabled (Reactions:Enabled=false).");
            return Task.CompletedTask;
        }

        try
        {
            Directory.CreateDirectory(_options.BaseDirectory);
            PruneOldFiles();
            _logger.LogInformation(
                "Reaction logging ENABLED at \"{Dir}\" (retention {Days}d).",
                _options.BaseDirectory, _options.RetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reaction startup: failed to prepare directory {Dir} or prune.", _options.BaseDirectory);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string PathForDate(DateTimeOffset ts)
    {
        var date = ts.UtcDateTime.ToString("yyyy-MM-dd");
        return Path.Combine(_options.BaseDirectory, $"reactions-{date}.jsonl");
    }

    private void PruneOldFiles()
    {
        if (!Directory.Exists(_options.BaseDirectory)) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
        var pruned = 0;
        foreach (var file in Directory.EnumerateFiles(_options.BaseDirectory, "reactions-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // "reactions-YYYY-MM-DD"
            if (name.Length < 20) continue; // "reactions-YYYY-MM-DD" = 20 chars
            var datePart = name[10..]; // "YYYY-MM-DD"
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var d)
                && d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < cutoff.UtcDateTime)
            {
                try { File.Delete(file); pruned++; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reaction prune: failed to delete {File}", file);
                }
            }
        }
        if (pruned > 0)
        {
            _logger.LogInformation("Reaction prune: deleted {Pruned} file(s) older than {Days} days.",
                pruned, _options.RetentionDays);
        }
    }
}

/// <summary>Configuration for <see cref="FileBackedReactionSink"/>. Bound from <c>Reactions:</c> section.</summary>
public sealed class ReactionOptions
{
    public const string SectionName = "Reactions";

    /// <summary>
    /// Master switch. On by default: reactions are low-sensitivity (emoji + IDs) and are the explicit
    /// reception signal we want. Disable to stop collecting.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory for daily JSONL files. Should sit on the PVC so it survives restarts.</summary>
    public string BaseDirectory { get; set; } = Path.Combine("data", "reactions");

    /// <summary>Days to retain reaction files. Older files are deleted on startup.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Max characters of the bot reply excerpt stored with each reaction (for join-free analysis).</summary>
    public int ReplyExcerptLength { get; set; } = 200;
}
