using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class SafetyFilter : IDisposable
{
    private readonly IOptionsMonitor<ChaosSettings> _settingsMonitor;
    private readonly ILogger<SafetyFilter> _logger;
    private readonly ConcurrentDictionary<ulong, Queue<DateTimeOffset>> _channelPromptHistory = new();
    private readonly Queue<DateTimeOffset> _globalPromptHistory = new();
    private readonly object _rateLimitLock = new();
    private volatile Regex? _banWordRegex;
    private IReadOnlyList<string> _lastBanWords;
    private readonly IDisposable? _optionsChangeToken;

    public SafetyFilter(IOptionsMonitor<ChaosSettings> settingsMonitor, ILogger<SafetyFilter> logger)
    {
        _settingsMonitor = settingsMonitor;
        _logger = logger;
        var initial = settingsMonitor.CurrentValue;
        _banWordRegex = BuildBanWordRegex(initial.BanWords);
        _lastBanWords = initial.BanWords.ToList();
        _optionsChangeToken = settingsMonitor.OnChange(settings =>
        {
            if (!settings.BanWords.SequenceEqual(_lastBanWords))
            {
                _banWordRegex = BuildBanWordRegex(settings.BanWords);
                _lastBanWords = settings.BanWords.ToList();
                _logger.LogInformation("Ban word regex rebuilt due to configuration change ({Count} words)", settings.BanWords.Count);
            }
        });
    }

    /// <summary>
    /// Per-channel rate limiting with a global ceiling.
    /// The configured MaxPromptsPerHour applies per channel; the global limit is 3x that value.
    /// </summary>
    public bool ShouldRateLimit(DateTimeOffset timestamp, ulong channelId)
    {
        var settings = _settingsMonitor.CurrentValue;
        if (settings.MaxPromptsPerHour <= 0)
        {
            return false;
        }

        lock (_rateLimitLock)
        {
            // Global rate limit (3x per-channel limit)
            var globalLimit = settings.MaxPromptsPerHour * 3;
            PurgeStale(_globalPromptHistory, timestamp);
            if (_globalPromptHistory.Count >= globalLimit)
            {
                _logger.LogInformation("Creative request throttled due to global rate limit ({Count}/{Limit})", _globalPromptHistory.Count, globalLimit);
                return true;
            }

            // Per-channel rate limit
            var channelHistory = _channelPromptHistory.GetOrAdd(channelId, _ => new Queue<DateTimeOffset>());
            PurgeStale(channelHistory, timestamp);
            if (channelHistory.Count >= settings.MaxPromptsPerHour)
            {
                _logger.LogInformation("Creative request throttled for channel {ChannelId} ({Count}/{Limit})", channelId, channelHistory.Count, settings.MaxPromptsPerHour);
                return true;
            }

            channelHistory.Enqueue(timestamp);
            _globalPromptHistory.Enqueue(timestamp);
            return false;
        }
    }

    public string ScrubBannedContent(string text)
    {
        var regex = _banWordRegex;
        if (regex is null)
        {
            return text;
        }

        return regex.Replace(text, "***");
    }

    public void Dispose()
    {
        _optionsChangeToken?.Dispose();
    }

    private static void PurgeStale(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1))
        {
            queue.Dequeue();
        }
    }

    private static Regex? BuildBanWordRegex(IReadOnlyList<string> banWords)
    {
        if (banWords.Count == 0)
        {
            return null;
        }

        var patterns = banWords
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(Regex.Escape)
            .ToList();

        if (patterns.Count == 0)
        {
            return null;
        }

        return new Regex(string.Join("|", patterns), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
