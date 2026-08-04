using System.Text.Json;
using DiscordSky.Bot.Memory.Logging;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public enum WorldAutonomyBudgetKind
{
    AmbientFull,
    AmbientConversation,
    DirectFull,
    DirectConversation,
}

public sealed record WorldAutonomyBudgetDecision(
    bool Allowed,
    string Reason,
    int HourCount,
    int DayCount);

public sealed class WorldAutonomyBudget
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly WorldAutonomyBudgetOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorldAutonomyBudget> _logger;
    private readonly IRecallTelemetrySink _telemetry;
    private BudgetState _state;

    public WorldAutonomyBudget(
        WorldAutonomyConfiguration configuration,
        IRecallTelemetrySink telemetry,
        ILogger<WorldAutonomyBudget> logger,
        TimeProvider? timeProvider = null)
    {
        _options = configuration.Budget;
        _telemetry = telemetry;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = LoadState(_options.StatePath) ?? BudgetState.Create(_timeProvider.GetUtcNow());
    }

    public WorldAutonomyBudgetDecision TryConsume(WorldAutonomyBudgetKind kind)
    {
        if (!_options.Enabled)
        {
            return Emit(kind, new WorldAutonomyBudgetDecision(true, "budget_disabled", 0, 0));
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            ResetWindows(now);
            var key = kind.ToString();
            var hourCount = _state.HourCounts.GetValueOrDefault(key);
            var dayCount = _state.DayCounts.GetValueOrDefault(key);
            var (hourLimit, dayLimit) = Limits(kind);
            if (hourCount >= hourLimit)
            {
                return Emit(kind, new WorldAutonomyBudgetDecision(
                    false,
                    "hourly_route_budget_exhausted",
                    hourCount,
                    dayCount));
            }
            if (dayCount >= dayLimit)
            {
                return Emit(kind, new WorldAutonomyBudgetDecision(
                    false,
                    "daily_route_budget_exhausted",
                    hourCount,
                    dayCount));
            }

            hourCount++;
            dayCount++;
            _state.HourCounts[key] = hourCount;
            _state.DayCounts[key] = dayCount;
            try
            {
                Persist();
            }
            catch (Exception)
            {
                if (hourCount == 1)
                {
                    _state.HourCounts.Remove(key);
                }
                else
                {
                    _state.HourCounts[key] = hourCount - 1;
                }
                if (dayCount == 1)
                {
                    _state.DayCounts.Remove(key);
                }
                else
                {
                    _state.DayCounts[key] = dayCount - 1;
                }
                return Emit(kind, new WorldAutonomyBudgetDecision(
                    false,
                    "budget_state_unavailable",
                    hourCount - 1,
                    dayCount - 1));
            }
            return Emit(kind, new WorldAutonomyBudgetDecision(true, "admitted", hourCount, dayCount));
        }
    }

    private WorldAutonomyBudgetDecision Emit(
        WorldAutonomyBudgetKind kind,
        WorldAutonomyBudgetDecision decision)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: _timeProvider.GetUtcNow(),
            EventType: TelemetryEventTypes.WorldAutonomyBudget,
            Kind: kind.ToString().ToLowerInvariant(),
            Outcome: decision.Allowed ? "admitted" : "held",
            Count: decision.HourCount,
            Total: decision.DayCount,
            Reason: decision.Reason));
        return decision;
    }

    private (int Hour, int Day) Limits(WorldAutonomyBudgetKind kind) => kind switch
    {
        WorldAutonomyBudgetKind.AmbientFull => (_options.AmbientFullPerHour, _options.AmbientFullPerDay),
        WorldAutonomyBudgetKind.AmbientConversation =>
            (_options.AmbientConversationPerHour, _options.AmbientConversationPerDay),
        WorldAutonomyBudgetKind.DirectFull => (_options.DirectFullPerHour, _options.DirectFullPerDay),
        _ => (_options.DirectConversationPerHour, _options.DirectConversationPerDay),
    };

    private void ResetWindows(DateTimeOffset now)
    {
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        if (_state.HourStartUtc != hour)
        {
            _state.HourStartUtc = hour;
            _state.HourCounts.Clear();
        }

        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (_state.DayUtc != day)
        {
            _state.DayUtc = day;
            _state.DayCounts.Clear();
        }
    }

    private void Persist()
    {
        try
        {
            var fullPath = Path.GetFullPath(_options.StatePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporary = string.Concat(fullPath, ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to persist world-autonomy route budget state.");
            throw;
        }
    }

    private BudgetState? LoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<BudgetState>(File.ReadAllText(path), JsonOptions);
            var now = _timeProvider.GetUtcNow();
            var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var currentDay = DateOnly.FromDateTime(now.UtcDateTime);
            if (state is null || state.HourStartUtc == default || state.HourStartUtc > currentHour ||
                state.DayUtc == default || state.DayUtc > currentDay ||
                state.HourCounts is null || state.DayCounts is null ||
                state.HourCounts.Values.Any(count => count < 0) ||
                state.DayCounts.Values.Any(count => count < 0))
            {
                throw new InvalidDataException("World-autonomy route budget state is invalid.");
            }
            return state;
        }
        catch (Exception exception)
        {
            var state = BudgetState.CreateExhausted(_timeProvider.GetUtcNow(), _options);
            _logger.LogError(
                exception,
                "Unable to load world-autonomy route budget state; holding all routes through the current UTC day.");
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: _timeProvider.GetUtcNow(),
                EventType: TelemetryEventTypes.WorldAutonomyBudget,
                Outcome: "state_fail_closed",
                Reason: exception.GetType().Name));
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
                _logger.LogError(
                    persistException,
                    "Unable to replace corrupt world-autonomy route budget state.");
            }
            return state;
        }
    }

    private sealed class BudgetState
    {
        public DateTimeOffset HourStartUtc { get; set; }

        public DateOnly DayUtc { get; set; }

        public Dictionary<string, int> HourCounts { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> DayCounts { get; set; } = new(StringComparer.Ordinal);

        public static BudgetState Create(DateTimeOffset now) => new()
        {
            HourStartUtc = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero),
            DayUtc = DateOnly.FromDateTime(now.UtcDateTime),
        };

        public static BudgetState CreateExhausted(
            DateTimeOffset now,
            WorldAutonomyBudgetOptions options) => new()
        {
            HourStartUtc = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero),
            DayUtc = DateOnly.FromDateTime(now.UtcDateTime),
            HourCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [nameof(WorldAutonomyBudgetKind.AmbientFull)] = options.AmbientFullPerHour,
                [nameof(WorldAutonomyBudgetKind.AmbientConversation)] = options.AmbientConversationPerHour,
                [nameof(WorldAutonomyBudgetKind.DirectFull)] = options.DirectFullPerHour,
                [nameof(WorldAutonomyBudgetKind.DirectConversation)] = options.DirectConversationPerHour,
            },
            DayCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [nameof(WorldAutonomyBudgetKind.AmbientFull)] = options.AmbientFullPerDay,
                [nameof(WorldAutonomyBudgetKind.AmbientConversation)] = options.AmbientConversationPerDay,
                [nameof(WorldAutonomyBudgetKind.DirectFull)] = options.DirectFullPerDay,
                [nameof(WorldAutonomyBudgetKind.DirectConversation)] = options.DirectConversationPerDay,
            },
        };
    }
}