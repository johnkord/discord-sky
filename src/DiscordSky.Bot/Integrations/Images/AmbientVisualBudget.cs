using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Images;

/// <summary>Separate per-guild budget for unsolicited images; explicit commissions do not consume it.</summary>
public sealed class AmbientVisualBudget
{
    private readonly ImageOptions _options;
    private readonly IImageGenerationLog _log;
    private readonly ILogger<AmbientVisualBudget> _logger;
    private readonly object _sync = new();
    private readonly HashSet<ulong> _inFlight = new();
    private readonly Dictionary<ulong, DateTimeOffset> _lastSucceeded = new();

    public AmbientVisualBudget(
        IOptions<ImageOptions> options,
        IImageGenerationLog log,
        ILogger<AmbientVisualBudget>? logger = null)
    {
        _options = options.Value;
        _log = log;
        _logger = logger ?? NullLogger<AmbientVisualBudget>.Instance;
    }

    public bool Enabled => _options.AmbientVisualEnabled;
    public double WorthThreshold => _options.AmbientVisualWorthThreshold;
    public double MinLead => _options.AmbientVisualMinLead;

    public bool TryAcquire(
        ulong guildId,
        DateTimeOffset now,
        out AmbientVisualLease? lease,
        out string? veto)
    {
        lock (_sync)
        {
            if (!Enabled)
            {
                lease = null;
                veto = "disabled";
                return false;
            }
            if (_inFlight.Contains(guildId))
            {
                lease = null;
                veto = "inflight";
                return false;
            }

            DateTimeOffset? durableLast;
            try
            {
                var day = DateOnly.FromDateTime(now.UtcDateTime);
                if (_options.AmbientVisualMaxPerGuildPerDay > 0
                    && _log.CountSuccessfulAmbientVisualsOnUtcDay(day, guildId)
                        >= _options.AmbientVisualMaxPerGuildPerDay)
                {
                    lease = null;
                    veto = "daily_cap";
                    return false;
                }
                durableLast = _log.LastSuccessfulAmbientVisualAt(guildId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ambient visual budget state unavailable for guild {GuildId}; vetoing.", guildId);
                lease = null;
                veto = "budget_unavailable";
                return false;
            }

            var last = _lastSucceeded.TryGetValue(guildId, out var inMemoryLast)
                && (durableLast is null || inMemoryLast > durableLast)
                    ? inMemoryLast
                    : durableLast;
            var cooldown = TimeSpan.FromHours(Math.Max(0, _options.AmbientVisualCooldownHours));
            if (last is not null && now >= last && now - last < cooldown)
            {
                lease = null;
                veto = "cooldown";
                return false;
            }

            _inFlight.Add(guildId);
            lease = new AmbientVisualLease(
                succeededAt => Complete(guildId, succeededAt),
                () => Release(guildId));
            veto = null;
            return true;
        }
    }

    private void Complete(ulong guildId, DateTimeOffset succeededAt)
    {
        lock (_sync)
        {
            _lastSucceeded[guildId] = succeededAt;
            _inFlight.Remove(guildId);
        }
    }

    private void Release(ulong guildId)
    {
        lock (_sync) _inFlight.Remove(guildId);
    }
}

public sealed class AmbientVisualLease : IDisposable
{
    private readonly Action<DateTimeOffset> _complete;
    private readonly Action _release;
    private int _finished;

    internal AmbientVisualLease(Action<DateTimeOffset> complete, Action release)
    {
        _complete = complete;
        _release = release;
    }

    public void MarkSucceeded(DateTimeOffset at)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0) _complete(at);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0) _release();
    }
}