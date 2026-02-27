using System.Text.RegularExpressions;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class SafetyFilter
{
    private readonly ChaosSettings _settings;
    private readonly ILogger<SafetyFilter> _logger;
    private readonly Queue<DateTimeOffset> _promptHistory = new();
    private readonly object _rateLimitLock = new();
    private readonly Regex? _banWordRegex;

    public SafetyFilter(IOptions<ChaosSettings> settings, ILogger<SafetyFilter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _banWordRegex = BuildBanWordRegex(_settings.BanWords);
    }

    public bool ShouldRateLimit(DateTimeOffset timestamp)
    {
        if (_settings.MaxPromptsPerHour <= 0)
        {
            return false;
        }

        lock (_rateLimitLock)
        {
            // Purge stale entries first
            while (_promptHistory.Count > 0 && timestamp - _promptHistory.Peek() > TimeSpan.FromHours(1))
            {
                _promptHistory.Dequeue();
            }

            // Check limit before recording the current request
            if (_promptHistory.Count >= _settings.MaxPromptsPerHour)
            {
                _logger.LogInformation("Creative request throttled due to MaxPromptsPerHour limit {Limit}", _settings.MaxPromptsPerHour);
                return true;
            }

            _promptHistory.Enqueue(timestamp);
            return false;
        }
    }

    public string ScrubBannedContent(string text)
    {
        if (_banWordRegex is null)
        {
            return text;
        }

        return _banWordRegex.Replace(text, "***");
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
