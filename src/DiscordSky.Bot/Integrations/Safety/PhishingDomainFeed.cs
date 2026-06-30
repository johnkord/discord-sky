using System.Text.Json;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Mirrors the community-maintained Sinking Yachts phishing-domain feed (phish.sinking.yachts) into an
/// in-process set, so the scam detector can check real Discord-targeting phishing domains locally: instant,
/// private (no per-message call to a third party), and resilient (a cached copy on the PVC survives restarts
/// and outages). The network is only touched to refresh the cache.
///
/// Design choices:
/// - Polling, not the websocket feed. We learned the hard way (gateway starvation) to avoid babysitting
///   long-lived sockets; a periodic GET /v2/recent delta is simpler and robust. A full GET /v2/all runs on
///   cold start (or when the cache is empty / once a day) per the API's request not to spam /all.
/// - Fail-open. Any fetch/parse error keeps whatever domains we already have and logs a warning; the detector
///   simply leans on its heuristics until the next successful refresh.
/// </summary>
public sealed class PhishingDomainFeed : IHostedService, IPhishingDomainSource, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ScamGuardOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PhishingDomainFeed> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastSync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFullSync = DateTimeOffset.MinValue;
    private Timer? _timer;

    public PhishingDomainFeed(
        IOptions<ScamGuardOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<PhishingDomainFeed> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public int Count => _domains.Count;

    public bool Contains(string domain) =>
        !string.IsNullOrWhiteSpace(domain) && _domains.Contains(domain.Trim().ToLowerInvariant());

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadCache();

        // Don't gate startup on the network: the cached copy is already usable, and the first live refresh
        // runs in the background.
        _ = Task.Run(() => RefreshAsync(full: _domains.Count == 0, _cts.Token), _cts.Token);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.PhishingFeedRefreshMinutes));
        _timer = new Timer(_ => _ = RefreshTickAsync(), null, interval, interval);

        _logger.LogInformation(
            "Phishing-domain feed started (url={Url}, refresh={Minutes}m, cached={Count}).",
            _options.PhishingFeedUrl, _options.PhishingFeedRefreshMinutes, _domains.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task RefreshTickAsync()
    {
        try
        {
            await RefreshAsync(full: false, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Phishing-domain feed: refresh tick failed.");
        }
    }

    private async Task RefreshAsync(bool full, CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return; // a refresh is already in flight
        }

        try
        {
            var baseUrl = _options.PhishingFeedUrl.TrimEnd('/');
            using var http = _httpClientFactory.CreateClient(nameof(PhishingDomainFeed));
            http.Timeout = TimeSpan.FromSeconds(20);
            if (!http.DefaultRequestHeaders.Contains("X-Identity"))
            {
                http.DefaultRequestHeaders.Add("X-Identity", _options.PhishingFeedIdentity);
            }
            if (!http.DefaultRequestHeaders.UserAgent.Any())
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _options.PhishingFeedIdentity);
            }

            var forceFull = full
                || _lastSync == DateTimeOffset.MinValue
                || DateTimeOffset.UtcNow - _lastFullSync > TimeSpan.FromHours(24);

            if (forceFull)
            {
                var json = await http.GetStringAsync($"{baseUrl}/v2/all", cancellationToken);
                var all = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                ApplyFull(all);
                _lastFullSync = DateTimeOffset.UtcNow;
                _lastSync = DateTimeOffset.UtcNow;
                SaveCache();
                _logger.LogInformation("Phishing-domain feed: loaded {Count} domains (full sync).", _domains.Count);
            }
            else
            {
                var seconds = Math.Max(60, (int)(DateTimeOffset.UtcNow - _lastSync).TotalSeconds + 60);
                var json = await http.GetStringAsync($"{baseUrl}/v2/recent/{seconds}", cancellationToken);
                var edits = JsonSerializer.Deserialize<List<DbEdit>>(json, JsonOpts) ?? new List<DbEdit>();
                if (edits.Count > 0)
                {
                    ApplyDelta(edits);
                    SaveCache();
                }
                _lastSync = DateTimeOffset.UtcNow;
                _logger.LogDebug(
                    "Phishing-domain feed: applied {Edits} delta edit(s); {Total} domains.", edits.Count, _domains.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Phishing-domain feed refresh failed; using {Count} cached domain(s) (fail-open).", _domains.Count);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal void ApplyFull(IEnumerable<string> domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domains)
        {
            var normalized = Normalize(domain);
            if (normalized is not null)
            {
                set.Add(normalized);
            }
        }

        _domains = set;
    }

    internal void ApplyDelta(IEnumerable<DbEdit> edits)
    {
        var set = new HashSet<string>(_domains, StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            if (edit.Domains is null)
            {
                continue;
            }

            var add = string.Equals(edit.Type, "add", StringComparison.OrdinalIgnoreCase);
            foreach (var domain in edit.Domains)
            {
                var normalized = Normalize(domain);
                if (normalized is null)
                {
                    continue;
                }

                if (add)
                {
                    set.Add(normalized);
                }
                else
                {
                    set.Remove(normalized);
                }
            }
        }

        _domains = set;
    }

    internal void LoadCache()
    {
        try
        {
            if (!File.Exists(_options.PhishingFeedCachePath))
            {
                return;
            }

            var json = File.ReadAllText(_options.PhishingFeedCachePath);
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list is { Count: > 0 })
            {
                ApplyFull(list);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Phishing-domain feed: cache load failed.");
        }
    }

    internal void SaveCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(_options.PhishingFeedCachePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_options.PhishingFeedCachePath, JsonSerializer.Serialize(_domains.ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Phishing-domain feed: cache save failed.");
        }
    }

    private static string? Normalize(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var d = domain.Trim().ToLowerInvariant();
        var slash = d.IndexOf('/');
        if (slash >= 0)
        {
            d = d[..slash];
        }

        d = d.TrimEnd('.');
        return d.Contains('.') ? d : null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Dispose();
        _refreshGate.Dispose();
    }

    internal sealed record DbEdit(string? Type, string[]? Domains);
}
