using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class SafetyFilter : IDisposable
{
    private readonly IOptionsMonitor<ChaosSettings> _settingsMonitor;
    private readonly ILogger<SafetyFilter> _logger;
    private readonly ConcurrentDictionary<ulong, Queue<DateTimeOffset>> _channelPromptHistory = new();
    private readonly Queue<DateTimeOffset> _globalPromptHistory = new();
    private readonly ConcurrentDictionary<ulong, Queue<DateTimeOffset>> _explicitChannelReserveHistory = new();
    private readonly Queue<DateTimeOffset> _explicitGlobalReserveHistory = new();
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

    public bool ShouldRateLimit(DateTimeOffset timestamp, ulong channelId) =>
        EvaluateRateLimit(timestamp, channelId, CreativeInvocationKind.Ambient).IsRateLimited;

    /// <summary>
    /// Applies a shared per-channel/global budget. Explicit traffic may overflow into a small reserve that
    /// autonomous traffic cannot consume; both dimensions retain a hard sliding-window ceiling.
    /// </summary>
    public CreativeRateLimitDecision EvaluateRateLimit(
        DateTimeOffset timestamp,
        ulong channelId,
        CreativeInvocationKind invocationKind)
    {
        var settings = _settingsMonitor.CurrentValue;
        if (settings.MaxPromptsPerHour <= 0)
        {
            return CreativeRateLimitDecision.Allowed("disabled");
        }

        lock (_rateLimitLock)
        {
            var channelLimit = settings.MaxPromptsPerHour;
            var globalLimit = MultiplyLimit(channelLimit, 3);
            var reserveChannelLimit = Math.Max(0, settings.ExplicitReservePromptsPerHour);
            var reserveGlobalLimit = MultiplyLimit(reserveChannelLimit, 3);

            PurgeStale(_globalPromptHistory, timestamp);
            PurgeStale(_explicitGlobalReserveHistory, timestamp);
            var channelHistory = _channelPromptHistory.GetOrAdd(channelId, _ => new Queue<DateTimeOffset>());
            var explicitChannelReserve = _explicitChannelReserveHistory.GetOrAdd(
                channelId,
                _ => new Queue<DateTimeOffset>());
            PurgeStale(channelHistory, timestamp);
            PurgeStale(explicitChannelReserve, timestamp);

            var needsGlobalReserve = _globalPromptHistory.Count >= globalLimit;
            var needsChannelReserve = channelHistory.Count >= channelLimit;
            var isExplicit = invocationKind != CreativeInvocationKind.Ambient;

            if (!isExplicit && needsGlobalReserve)
            {
                _logger.LogInformation("Creative request throttled due to global rate limit ({Count}/{Limit})", _globalPromptHistory.Count, globalLimit);
                return CreativeRateLimitDecision.Limited(
                    "shared_global",
                    _globalPromptHistory.Count,
                    globalLimit,
                    "global_budget_exhausted");
            }
            if (!isExplicit && needsChannelReserve)
            {
                _logger.LogInformation("Creative request throttled for channel {ChannelId} ({Count}/{Limit})", channelId, channelHistory.Count, channelLimit);
                return CreativeRateLimitDecision.Limited(
                    "autonomous_channel",
                    channelHistory.Count,
                    channelLimit,
                    "channel_budget_exhausted");
            }

            if (isExplicit
                && needsGlobalReserve
                && _explicitGlobalReserveHistory.Count >= reserveGlobalLimit)
            {
                _logger.LogInformation(
                    "Explicit creative request throttled due to global reserve ({Count}/{Limit})",
                    _explicitGlobalReserveHistory.Count,
                    reserveGlobalLimit);
                return CreativeRateLimitDecision.Limited(
                    "explicit_global_reserve",
                    _explicitGlobalReserveHistory.Count,
                    reserveGlobalLimit,
                    "explicit_global_reserve_exhausted");
            }
            if (isExplicit
                && needsChannelReserve
                && explicitChannelReserve.Count >= reserveChannelLimit)
            {
                _logger.LogInformation(
                    "Explicit creative request throttled for channel {ChannelId} reserve ({Count}/{Limit})",
                    channelId,
                    explicitChannelReserve.Count,
                    reserveChannelLimit);
                return CreativeRateLimitDecision.Limited(
                    "explicit_channel_reserve",
                    explicitChannelReserve.Count,
                    reserveChannelLimit,
                    "explicit_channel_reserve_exhausted");
            }

            if (needsGlobalReserve) _explicitGlobalReserveHistory.Enqueue(timestamp);
            else _globalPromptHistory.Enqueue(timestamp);
            if (needsChannelReserve) explicitChannelReserve.Enqueue(timestamp);
            else channelHistory.Enqueue(timestamp);

            return CreativeRateLimitDecision.Allowed(
                needsGlobalReserve || needsChannelReserve ? "explicit_reserve" : "shared");
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

    private static int MultiplyLimit(int value, int multiplier) =>
        (int)Math.Min(int.MaxValue, (long)Math.Max(0, value) * multiplier);

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

public sealed record CreativeRateLimitDecision(
    bool IsRateLimited,
    string BudgetClass,
    int Count,
    int Limit,
    string ReasonCode)
{
    public static CreativeRateLimitDecision Allowed(string budgetClass) =>
        new(false, budgetClass, 0, 0, "allowed");

    public static CreativeRateLimitDecision Limited(
        string budgetClass,
        int count,
        int limit,
        string reasonCode) => new(true, budgetClass, count, limit, reasonCode);
}
