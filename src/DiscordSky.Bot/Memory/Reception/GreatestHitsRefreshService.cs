using System.Text.Json;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory.Reception;

/// <summary>
/// Periodically re-ranks the bot's best-received replies from the reaction log and publishes them into the
/// <see cref="GreatestHitsCache"/> for the persona prompt to sample. Reads the same JSONL the reaction sink
/// writes. Fully fail-open: any read/parse problem keeps the previous set rather than clearing it.
/// </summary>
public sealed class GreatestHitsRefreshService : IHostedService, IDisposable
{
    private readonly ReactionOptions _options;
    private readonly GreatestHitsCache _cache;
    private readonly ILogger<GreatestHitsRefreshService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public GreatestHitsRefreshService(
        IOptions<ReactionOptions> options, GreatestHitsCache cache, ILogger<GreatestHitsRefreshService> logger)
    {
        _options = options.Value;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.ProvenBitsEnabled)
        {
            _logger.LogInformation("Proven-bits refresh disabled.");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch
            {
                // Shutdown; ignore.
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Refresh();
        var hours = Math.Max(1, _options.ProvenBitsRefreshHours);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
            while (await timer.WaitForNextTickAsync(ct))
            {
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void Refresh()
    {
        try
        {
            var events = ReadRecent();
            var hits = GreatestHits.TopHits(events, Math.Max(1, _options.ProvenBitsPoolSize));
            _cache.Set(hits);
            _logger.LogInformation(
                "proven_bits refreshed: {Count} hit(s) from {Events} reaction event(s).", hits.Count, events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proven-bits refresh failed; keeping previous set.");
        }
    }

    private IReadOnlyList<ReactionEvent> ReadRecent()
    {
        var results = new List<ReactionEvent>();
        var dir = _options.BaseDirectory;
        if (!Directory.Exists(dir))
        {
            return results;
        }

        var cutoff = DateTime.UtcNow.Date.AddDays(-Math.Max(1, _options.ProvenBitsLookbackDays));
        foreach (var path in Directory.EnumerateFiles(dir, "reactions-*.jsonl"))
        {
            var stamp = Path.GetFileNameWithoutExtension(path).Replace("reactions-", string.Empty);
            if (DateTime.TryParse(stamp, out var day) && day < cutoff)
            {
                continue;
            }

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var ev = JsonSerializer.Deserialize<ReactionEvent>(line);
                        if (ev is not null)
                        {
                            results.Add(ev);
                        }
                    }
                    catch
                    {
                        // Skip a malformed line rather than failing the whole refresh.
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed reading reaction file {Path}.", path);
            }
        }

        return results;
    }

    public void Dispose() => _cts?.Dispose();
}
