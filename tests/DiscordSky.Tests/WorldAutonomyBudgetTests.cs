using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyBudgetTests
{
    [Fact]
    public void Budget_PersistsCountsAndKeepsRouteClassesIndependent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"world-autonomy-budget-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
            var configuration = Configuration(path, ambientFullPerHour: 1, ambientConversationPerHour: 2);
            var first = new WorldAutonomyBudget(
                configuration,
                new RecordingTelemetrySink(),
                NullLogger<WorldAutonomyBudget>.Instance,
                clock);

            Assert.True(first.TryConsume(WorldAutonomyBudgetKind.AmbientFull).Allowed);
            Assert.False(first.TryConsume(WorldAutonomyBudgetKind.AmbientFull).Allowed);
            Assert.True(first.TryConsume(WorldAutonomyBudgetKind.AmbientConversation).Allowed);

            var restored = new WorldAutonomyBudget(
                configuration,
                new RecordingTelemetrySink(),
                NullLogger<WorldAutonomyBudget>.Instance,
                clock);
            Assert.False(restored.TryConsume(WorldAutonomyBudgetKind.AmbientFull).Allowed);
            Assert.True(restored.TryConsume(WorldAutonomyBudgetKind.AmbientConversation).Allowed);
            Assert.False(restored.TryConsume(WorldAutonomyBudgetKind.AmbientConversation).Allowed);
        }
        finally
        {
            File.Delete(path);
            File.Delete(string.Concat(path, ".tmp"));
        }
    }

    [Fact]
    public void Budget_ResetsHourlyCountsButRetainsDailyCounts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"world-autonomy-budget-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 59, 0, TimeSpan.Zero));
            var budget = new WorldAutonomyBudget(
                Configuration(path, ambientFullPerHour: 1, ambientFullPerDay: 2),
                new RecordingTelemetrySink(),
                NullLogger<WorldAutonomyBudget>.Instance,
                clock);

            Assert.True(budget.TryConsume(WorldAutonomyBudgetKind.AmbientFull).Allowed);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.True(budget.TryConsume(WorldAutonomyBudgetKind.AmbientFull).Allowed);
            Assert.False(budget.TryConsume(WorldAutonomyBudgetKind.AmbientFull).Allowed);
        }
        finally
        {
            File.Delete(path);
            File.Delete(string.Concat(path, ".tmp"));
        }
    }

    [Fact]
    public void Budget_PersistenceFailureHoldsRouteWithoutConsumingCount()
    {
        var budget = new WorldAutonomyBudget(
            Configuration("/proc/discord-sky-budget.json", ambientFullPerHour: 1),
            new RecordingTelemetrySink(),
            NullLogger<WorldAutonomyBudget>.Instance,
            new MutableTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)));

        var decision = budget.TryConsume(WorldAutonomyBudgetKind.AmbientFull);

        Assert.False(decision.Allowed);
        Assert.Equal("budget_state_unavailable", decision.Reason);
        Assert.Equal(0, decision.HourCount);
        Assert.Equal(0, decision.DayCount);
    }

    [Fact]
    public void Budget_CorruptStateFailsClosedAndRewritesValidState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"world-autonomy-budget-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            var telemetry = new RecordingTelemetrySink();
            var budget = new WorldAutonomyBudget(
                Configuration(path, ambientFullPerHour: 1),
                telemetry,
                NullLogger<WorldAutonomyBudget>.Instance,
                new MutableTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)));

            var decision = budget.TryConsume(WorldAutonomyBudgetKind.AmbientFull);

            Assert.False(decision.Allowed);
            Assert.Contains(telemetry.Events, evt => evt.Outcome == "state_fail_closed");
            Assert.Contains("hourCounts", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(string.Concat(path, ".tmp"));
        }
    }

    private static WorldAutonomyConfiguration Configuration(
        string path,
        int ambientFullPerHour,
        int ambientConversationPerHour = 2,
        int ambientFullPerDay = 10) => WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            Budget = new WorldAutonomyBudgetOptions
            {
                StatePath = path,
                AmbientFullPerHour = ambientFullPerHour,
                AmbientFullPerDay = ambientFullPerDay,
                AmbientConversationPerHour = ambientConversationPerHour,
                AmbientConversationPerDay = 10,
                DirectFullPerHour = 2,
                DirectFullPerDay = 10,
                DirectConversationPerHour = 2,
                DirectConversationPerDay = 10,
            },
        });

    private sealed class RecordingTelemetrySink : IRecallTelemetrySink
    {
        public List<TelemetryEvent> Events { get; } = [];

        public void Emit(TelemetryEvent evt) => Events.Add(evt);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
