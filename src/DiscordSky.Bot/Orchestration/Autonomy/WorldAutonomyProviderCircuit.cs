using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record WorldAutonomyCircuitSnapshot(
    bool IsOpen,
    DateTimeOffset? NextProbeAt,
    string? Reason);

public sealed class WorldAutonomyProviderCircuit
{
    private static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeInterval;
    private readonly ILogger<WorldAutonomyProviderCircuit> _logger;
    private bool _isOpen;
    private bool _probeInFlight;
    private DateTimeOffset? _nextProbeAt;
    private string? _reason;

    public WorldAutonomyProviderCircuit(
        ILogger<WorldAutonomyProviderCircuit> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? probeInterval = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _probeInterval = probeInterval ?? DefaultProbeInterval;
    }

    public bool TryEnter(out WorldAutonomyCircuitSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_isOpen)
            {
                snapshot = SnapshotUnsafe();
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            if (!_probeInFlight && (!_nextProbeAt.HasValue || now >= _nextProbeAt.Value))
            {
                _probeInFlight = true;
                snapshot = SnapshotUnsafe();
                return true;
            }

            snapshot = SnapshotUnsafe();
            return false;
        }
    }

    public bool RecordFailure(Exception exception)
    {
        var deterministic = IsDeterministicProviderBlock(exception, out var reason);
        lock (_gate)
        {
            if (!deterministic && !_isOpen)
            {
                return false;
            }

            _isOpen = true;
            _probeInFlight = false;
            _reason = deterministic ? reason : exception.GetType().Name;
            _nextProbeAt = _timeProvider.GetUtcNow() + _probeInterval;
            _logger.LogWarning(
                "World autonomy provider circuit opened reason={Reason} next_probe={NextProbeAt:O}.",
                _reason,
                _nextProbeAt);
            return true;
        }
    }

    public bool RecordSuccess()
    {
        lock (_gate)
        {
            var recovered = _isOpen;
            _isOpen = false;
            _probeInFlight = false;
            _nextProbeAt = null;
            _reason = null;
            if (recovered)
            {
                _logger.LogInformation("World autonomy provider circuit recovered.");
            }

            return recovered;
        }
    }

    public WorldAutonomyCircuitSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    internal static bool IsDeterministicProviderBlock(Exception exception, out string reason)
    {
        var message = exception.ToString();
        if (message.Contains("credit_balance_exhausted", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase))
        {
            reason = "credit_balance_exhausted";
            return true;
        }

        if (message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase))
        {
            reason = "authentication_failed";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private WorldAutonomyCircuitSnapshot SnapshotUnsafe() => new(_isOpen, _nextProbeAt, _reason);
}