using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Orchestration;

public sealed class SafetyFilter
{
    private readonly ChaosSettings _settings;
    private readonly ILogger<SafetyFilter> _logger;
    private readonly ConcurrentQueue<DateTimeOffset> _promptHistory = new();

    private static readonly Regex MentionSanitizer = new("[^a-zA-Z0-9]", RegexOptions.Compiled);

    public SafetyFilter(ChaosSettings settings, ILogger<SafetyFilter> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool ShouldRateLimit(DateTimeOffset timestamp)
    {
        if (_settings.MaxPromptsPerHour <= 0)
        {
            return false;
        }

        _promptHistory.Enqueue(timestamp);
        while (_promptHistory.TryPeek(out var head) && timestamp - head > TimeSpan.FromHours(1))
        {
            _promptHistory.TryDequeue(out _);
        }

        if (_promptHistory.Count > _settings.MaxPromptsPerHour)
        {
            _logger.LogInformation("Creative request throttled due to MaxPromptsPerHour limit {Limit}", _settings.MaxPromptsPerHour);
            return true;
        }

        return false;
    }

    public bool IsQuietHour(DateTimeOffset timestamp) => _settings.IsQuietHour(timestamp);

    public string SanitizeMentions(string name)
    {
        var trimmed = name.Trim();
        var normalized = MentionSanitizer.Replace(trimmed, string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? "chaos" : normalized;
    }

    public string ScrubBannedContent(string text)
    {
        if (_settings.BanWords.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var banWord in _settings.BanWords)
        {
            if (string.IsNullOrWhiteSpace(banWord))
            {
                continue;
            }

            result = Regex.Replace(result, Regex.Escape(banWord), "***", RegexOptions.IgnoreCase);
        }

        return result;
    }
}
