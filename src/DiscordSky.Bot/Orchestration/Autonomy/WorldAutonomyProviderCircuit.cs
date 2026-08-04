using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record LlmProviderGuardSnapshot(
    bool IsOpen,
    DateTimeOffset? NextProbeAt,
    string? Reason,
    double HourlyCostUsd = 0,
    double DailyCostUsd = 0);

public sealed record LlmProviderCallLease(
    string Model,
    double ReservedUsd,
    bool OwnsCircuitLease,
    bool GuardEnabled);

public sealed class LlmProviderBlockedException(string reason)
    : InvalidOperationException($"LLM provider guard blocked the call: {reason}")
{
    public string Reason { get; } = reason;
}

public sealed class LlmProviderGuard
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeInterval;
    private readonly ILogger<LlmProviderGuard> _logger;
    private readonly LlmProviderGuardOptions _options;
    private readonly IRecallTelemetrySink? _telemetry;
    private SpendState _spend;
    private double _hourlyReservations;
    private double _dailyReservations;
    private bool _isOpen;
    private bool _probeInFlight;
    private DateTimeOffset? _nextProbeAt;
    private string? _reason;

    internal LlmProviderGuard(
        ILogger<LlmProviderGuard> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? probeInterval = null,
        LlmProviderGuardOptions? options = null,
        IRecallTelemetrySink? telemetry = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new LlmProviderGuardOptions();
        ValidateOptions(_options);
        _telemetry = telemetry;
        _probeInterval = probeInterval ?? TimeSpan.FromMinutes(
            Math.Clamp(_options.ProbeIntervalMinutes, 1, 60));
        _spend = LoadState(_options.StatePath) ?? SpendState.Create(_timeProvider.GetUtcNow());
    }

    public LlmProviderGuard(
        IOptions<LlmOptions> options,
        IRecallTelemetrySink telemetry,
        ILogger<LlmProviderGuard> logger)
        : this(logger, options: options.Value.Guard, telemetry: telemetry)
    {
    }

    public bool TryEnter(out LlmProviderGuardSnapshot snapshot)
    {
        lock (_gate)
        {
            var allowed = TryEnterCircuitUnsafe();
            snapshot = SnapshotUnsafe();
            return allowed;
        }
    }

    public bool TryBeginCall(
        string model,
        bool ownsCircuitLease,
        out LlmProviderCallLease lease,
        out LlmProviderGuardSnapshot snapshot)
    {
        lock (_gate)
        {
            ResetSpendWindows(_timeProvider.GetUtcNow());
            if (ownsCircuitLease && !TryEnterCircuitUnsafe())
            {
                lease = new LlmProviderCallLease(model, 0, ownsCircuitLease, _options.Enabled);
                snapshot = SnapshotUnsafe();
                return false;
            }

            if (!_options.Enabled)
            {
                lease = new LlmProviderCallLease(model, 0, ownsCircuitLease, GuardEnabled: false);
                snapshot = SnapshotUnsafe();
                return true;
            }

            var reservation = ReservationFor(model);
            if (_spend.HourlyCostUsd + _hourlyReservations + reservation > _options.HourlyUsdLimit)
            {
                return BlockBudgetUnsafe(
                    model,
                    ownsCircuitLease,
                    "hourly_cost_budget_exhausted",
                    out lease,
                    out snapshot);
            }
            if (_spend.DailyCostUsd + _dailyReservations + reservation > _options.DailyUsdLimit)
            {
                return BlockBudgetUnsafe(
                    model,
                    ownsCircuitLease,
                    "daily_cost_budget_exhausted",
                    out lease,
                    out snapshot);
            }

            if (_reason is "hourly_cost_budget_exhausted" or "daily_cost_budget_exhausted")
            {
                _reason = null;
            }
            _hourlyReservations += reservation;
            _dailyReservations += reservation;
            lease = new LlmProviderCallLease(model, reservation, ownsCircuitLease, GuardEnabled: true);
            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    public void RecordCallSuccess(LlmProviderCallLease lease, ChatResponse? response)
    {
        lock (_gate)
        {
            ReleaseReservationUnsafe(lease);
            ResetSpendWindows(_timeProvider.GetUtcNow());
            var cost = response is null ? lease.ReservedUsd : EstimateUpperBoundCost(lease.Model, response);
            _spend.HourlyCostUsd += cost;
            _spend.DailyCostUsd += cost;
            PersistSpendUnsafe();
            if (lease.OwnsCircuitLease)
            {
                RecordSuccessUnsafe();
            }
            Emit("recorded", null, cost);
        }
    }

    public void RecordFixedCostSuccess(LlmProviderCallLease lease, double estimatedCostUsd)
    {
        lock (_gate)
        {
            ReleaseReservationUnsafe(lease);
            ResetSpendWindows(_timeProvider.GetUtcNow());
            var cost = Math.Max(0, estimatedCostUsd);
            _spend.HourlyCostUsd += cost;
            _spend.DailyCostUsd += cost;
            PersistSpendUnsafe();
            if (lease.OwnsCircuitLease)
            {
                RecordSuccessUnsafe();
            }
            Emit("recorded", null, cost);
        }
    }

    public void RecordCallFailure(LlmProviderCallLease lease, Exception exception)
    {
        lock (_gate)
        {
            ReleaseReservationUnsafe(lease);
            if (lease.OwnsCircuitLease)
            {
                RecordFailureUnsafe(exception);
            }
        }
    }

    public bool RecordFailure(Exception exception)
    {
        lock (_gate)
        {
            return RecordFailureUnsafe(exception);
        }
    }

    public bool RecordSuccess()
    {
        lock (_gate)
        {
            return RecordSuccessUnsafe();
        }
    }

    public LlmProviderGuardSnapshot Snapshot()
    {
        lock (_gate)
        {
            ResetSpendWindows(_timeProvider.GetUtcNow());
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

    internal static double EstimateUpperBoundCost(string model, ChatResponse response)
    {
        var usage = response.Usage;
        if (usage is null)
        {
            return ReservationFor(model);
        }

        var input = Math.Max(0, usage.InputTokenCount ?? 0);
        var cached = Math.Clamp(usage.CachedInputTokenCount ?? 0, 0, input);
        var observedWrite = TelemetryChatClient.GetCacheWriteInputTokens(response);
        var write = Math.Clamp(
            observedWrite ?? (model.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase)
                ? input - cached
                : 0),
            0,
            input - cached);
        var ordinary = Math.Max(0, input - cached - write);
        var output = Math.Max(0, usage.OutputTokenCount ?? 0);
        var rates = model.StartsWith("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase)
            ? (Input: 5.0, Cached: 0.5, Write: 6.25, Output: 30.0)
            : model.StartsWith("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase)
                ? (Input: 0.2, Cached: 0.02, Write: 0.25, Output: 1.2)
                : model.StartsWith("gpt-5.4-mini", StringComparison.OrdinalIgnoreCase)
                    ? (Input: 0.75, Cached: 0.075, Write: 0.75, Output: 4.5)
                    : (Input: 5.0, Cached: 0.5, Write: 6.25, Output: 30.0);
        return (ordinary * rates.Input + cached * rates.Cached + write * rates.Write + output * rates.Output)
            / 1_000_000.0;
    }

    private bool BlockBudgetUnsafe(
        string model,
        bool ownsCircuitLease,
        string reason,
        out LlmProviderCallLease lease,
        out LlmProviderGuardSnapshot snapshot)
    {
        if (ownsCircuitLease && _isOpen)
        {
            _probeInFlight = false;
        }
        if (!_isOpen)
        {
            _reason = reason;
        }
        lease = new LlmProviderCallLease(model, 0, ownsCircuitLease, GuardEnabled: true);
        snapshot = SnapshotUnsafe();
        Emit("blocked", reason, 0);
        return false;
    }

    private bool TryEnterCircuitUnsafe()
    {
        if (!_isOpen)
        {
            return true;
        }
        var now = _timeProvider.GetUtcNow();
        if (!_probeInFlight && (!_nextProbeAt.HasValue || now >= _nextProbeAt.Value))
        {
            _probeInFlight = true;
            return true;
        }
        return false;
    }

    private bool RecordFailureUnsafe(Exception exception)
    {
        var deterministic = IsDeterministicProviderBlock(exception, out var reason);
        if (!deterministic && !_isOpen)
        {
            return false;
        }

        _isOpen = true;
        _probeInFlight = false;
        _reason = deterministic ? reason : exception.GetType().Name;
        _nextProbeAt = _timeProvider.GetUtcNow() + _probeInterval;
        _logger.LogWarning(
            "LLM provider guard opened reason={Reason} next_probe={NextProbeAt:O}.",
            _reason,
            _nextProbeAt);
        Emit("opened", _reason, 0);
        return true;
    }

    private bool RecordSuccessUnsafe()
    {
        var recovered = _isOpen;
        _isOpen = false;
        _probeInFlight = false;
        _nextProbeAt = null;
        if (recovered)
        {
            _reason = null;
        }
        if (recovered)
        {
            _logger.LogInformation("LLM provider guard recovered.");
            Emit("recovered", null, 0);
        }
        return recovered;
    }

    private void ReleaseReservationUnsafe(LlmProviderCallLease lease)
    {
        if (!lease.GuardEnabled || lease.ReservedUsd <= 0)
        {
            return;
        }
        _hourlyReservations = Math.Max(0, _hourlyReservations - lease.ReservedUsd);
        _dailyReservations = Math.Max(0, _dailyReservations - lease.ReservedUsd);
    }

    private void ResetSpendWindows(DateTimeOffset now)
    {
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        if (_spend.HourStartUtc != hour)
        {
            _spend.HourStartUtc = hour;
            _spend.HourlyCostUsd = 0;
            _hourlyReservations = 0;
        }
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (_spend.DayUtc != day)
        {
            _spend.DayUtc = day;
            _spend.DailyCostUsd = 0;
            _dailyReservations = 0;
        }
    }

    private static double ReservationFor(string model) =>
        model.StartsWith("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gpt-5.4-mini", StringComparison.OrdinalIgnoreCase)
            ? 0.02
            : model.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase)
                ? 0.21
                : 0.75;

    private static void ValidateOptions(LlmProviderGuardOptions options)
    {
        if (options.HourlyUsdLimit <= 0 || !double.IsFinite(options.HourlyUsdLimit))
        {
            throw new InvalidOperationException("LLM:Guard:HourlyUsdLimit must be a positive finite value.");
        }
        if (options.DailyUsdLimit < options.HourlyUsdLimit || !double.IsFinite(options.DailyUsdLimit))
        {
            throw new InvalidOperationException(
                "LLM:Guard:DailyUsdLimit must be finite and at least HourlyUsdLimit.");
        }
        if (options.ProbeIntervalMinutes is < 1 or > 60)
        {
            throw new InvalidOperationException("LLM:Guard:ProbeIntervalMinutes must be between 1 and 60.");
        }
        if (string.IsNullOrWhiteSpace(options.StatePath))
        {
            throw new InvalidOperationException("LLM:Guard:StatePath must be non-empty.");
        }
    }

    private void PersistSpendUnsafe()
    {
        try
        {
            var fullPath = Path.GetFullPath(_options.StatePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporary = string.Concat(fullPath, ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(_spend, JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to persist LLM provider guard spend state.");
        }
    }

    private SpendState? LoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<SpendState>(File.ReadAllText(path), JsonOptions);
            var now = _timeProvider.GetUtcNow();
            var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var currentDay = DateOnly.FromDateTime(now.UtcDateTime);
            if (state is null || state.HourStartUtc == default || state.HourStartUtc > currentHour ||
                state.DayUtc == default || state.DayUtc > currentDay ||
                !double.IsFinite(state.HourlyCostUsd) || state.HourlyCostUsd < 0 ||
                !double.IsFinite(state.DailyCostUsd) || state.DailyCostUsd < 0)
            {
                throw new InvalidDataException("LLM provider guard spend state is invalid.");
            }
            return state;
        }
        catch (Exception exception)
        {
            var state = SpendState.Create(_timeProvider.GetUtcNow());
            state.HourlyCostUsd = _options.HourlyUsdLimit;
            state.DailyCostUsd = _options.DailyUsdLimit;
            _logger.LogError(
                exception,
                "Unable to load LLM provider guard spend state; holding guarded calls through the current UTC day.");
            _telemetry?.Emit(new TelemetryEvent(
                Timestamp: _timeProvider.GetUtcNow(),
                EventType: TelemetryEventTypes.LlmProviderGuard,
                Outcome: "state_fail_closed",
                Reason: exception.GetType().Name,
                HourlyCostUsd: state.HourlyCostUsd,
                DailyCostUsd: state.DailyCostUsd));
            try
            {
                var fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var temporary = string.Concat(fullPath, ".tmp");
                File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temporary, fullPath, overwrite: true);
            }
            catch (Exception persistException)
            {
                _logger.LogError(persistException, "Unable to replace corrupt LLM provider guard spend state.");
            }
            return state;
        }
    }

    private void Emit(string outcome, string? reason, double cost) => _telemetry?.Emit(new TelemetryEvent(
        Timestamp: _timeProvider.GetUtcNow(),
        EventType: TelemetryEventTypes.LlmProviderGuard,
        Outcome: outcome,
        Reason: reason,
        EstimatedCostUsd: cost,
        HourlyCostUsd: _spend.HourlyCostUsd,
        DailyCostUsd: _spend.DailyCostUsd));

    private LlmProviderGuardSnapshot SnapshotUnsafe() => new(
        _isOpen,
        _nextProbeAt,
        _reason,
        _spend.HourlyCostUsd,
        _spend.DailyCostUsd);

    private sealed class SpendState
    {
        public DateTimeOffset HourStartUtc { get; set; }
        public DateOnly DayUtc { get; set; }
        public double HourlyCostUsd { get; set; }
        public double DailyCostUsd { get; set; }

        public static SpendState Create(DateTimeOffset now) => new()
        {
            HourStartUtc = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero),
            DayUtc = DateOnly.FromDateTime(now.UtcDateTime),
        };
    }
}
