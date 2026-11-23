using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Memory.Logging;

internal sealed class DailyEvidenceRetention(FileBackedTelemetrySink telemetry, FileBackedTranscriptSink transcripts,
    FileBackedReactionSink reactions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) Sweep(DateTimeOffset.UtcNow);
    }

    internal void Sweep(DateTimeOffset now)
    {
        telemetry.Prune(now);
        transcripts.Prune(now);
        reactions.Prune(now);
    }

    internal static void Prune(string directory, string prefix, int days, DateTimeOffset now, ILogger logger)
    {
        if (days < 1 || !Directory.Exists(directory)) return;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var deleted = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, $"{prefix}-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != prefix.Length + 11 || !name.StartsWith(prefix + "-", StringComparison.Ordinal) ||
                    !DateOnly.TryParseExact(name.AsSpan(prefix.Length + 1), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) || today.DayNumber - date.DayNumber <= days) continue;
                try { File.Delete(file); deleted++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not prune {Prefix} file {Date}.", prefix, date);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not enumerate {Prefix} files for retention.", prefix);
        }
        if (deleted > 0) logger.LogInformation("Pruned {Count} expired {Prefix} files.", deleted, prefix);
    }
}