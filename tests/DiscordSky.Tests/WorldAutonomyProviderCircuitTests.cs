using DiscordSky.Bot.Orchestration.Autonomy;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class LlmProviderGuardTests
{
    [Fact]
    public void QuotaFailure_OpensImmediately_AndAllowsOneHalfOpenProbe()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        var circuit = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance,
            clock,
            TimeSpan.FromMinutes(1));

        Assert.True(circuit.TryEnter(out _));
        Assert.True(circuit.RecordFailure(
            new InvalidOperationException("HTTP 429 (insufficient_quota: credit_balance_exhausted)")));
        Assert.False(circuit.TryEnter(out var open));
        Assert.Equal("credit_balance_exhausted", open.Reason);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(circuit.TryEnter(out _));
        Assert.False(circuit.TryEnter(out _));
        Assert.True(circuit.RecordSuccess());
        Assert.True(circuit.TryEnter(out var recovered));
        Assert.False(recovered.IsOpen);
    }

    [Fact]
    public void FailedHalfOpenProbe_ReleasesProbeAndSchedulesAnother()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        var circuit = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance,
            clock,
            TimeSpan.FromSeconds(30));
        circuit.RecordFailure(new InvalidOperationException("invalid_api_key"));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(circuit.TryEnter(out _));

        Assert.True(circuit.RecordFailure(new TimeoutException("probe timed out")));
        Assert.False(circuit.TryEnter(out _));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(circuit.TryEnter(out _));
    }

    [Fact]
    public void OrdinaryRequestFailure_DoesNotOpenAClosedCircuit()
    {
        var circuit = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance);

        Assert.False(circuit.RecordFailure(new InvalidOperationException("bad tool arguments")));
        Assert.True(circuit.TryEnter(out var snapshot));
        Assert.False(snapshot.IsOpen);
    }

    [Fact]
    public void CostBudget_PersistsAndBlocksBeforeTheNextExpensiveCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llm-provider-guard-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
            var options = new LlmProviderGuardOptions
            {
                StatePath = path,
                HourlyUsdLimit = 0.25,
                DailyUsdLimit = 1.0,
            };
            var guard = new LlmProviderGuard(
                NullLogger<LlmProviderGuard>.Instance,
                clock,
                options: options);

            Assert.True(guard.TryBeginCall("gpt-5.6-sol", true, out var lease, out _));
            guard.RecordCallSuccess(lease, Response(input: 10_000, output: 1_000));
            Assert.False(guard.TryBeginCall("gpt-5.6-sol", true, out _, out var blocked));
            Assert.Equal("hourly_cost_budget_exhausted", blocked.Reason);

            var restored = new LlmProviderGuard(
                NullLogger<LlmProviderGuard>.Instance,
                clock,
                options: options);
            Assert.False(restored.TryBeginCall("gpt-5.6-sol", true, out _, out _));
        }
        finally
        {
            File.Delete(path);
            File.Delete(string.Concat(path, ".tmp"));
        }
    }

    [Fact]
    public void CostBudget_CorruptStateFailsClosedAndRewritesValidState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llm-provider-guard-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            var guard = new LlmProviderGuard(
                NullLogger<LlmProviderGuard>.Instance,
                new MutableTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z")),
                options: new LlmProviderGuardOptions
                {
                    StatePath = path,
                    HourlyUsdLimit = 1.0,
                    DailyUsdLimit = 3.0,
                });

            Assert.False(guard.TryBeginCall("gpt-5.6-sol", true, out _, out var blocked));
            Assert.Equal("hourly_cost_budget_exhausted", blocked.Reason);
            Assert.Contains("hourlyCostUsd", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(string.Concat(path, ".tmp"));
        }
    }

    [Fact]
    public void EstimateUpperBoundCost_UsesModelSpecificCachedWriteAndOutputRates()
    {
        var response = Response(input: 1_000, output: 100, cached: 400, cacheWrite: 300);

        var cost = LlmProviderGuard.EstimateUpperBoundCost("gpt-5.6-sol", response);

        Assert.Equal(0.006575, cost, precision: 6);
    }

    [Fact]
    public void EstimateUpperBoundCost_TreatsUnobservedGpt56UncachedInputAsWrites()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 1_000,
                CachedInputTokenCount = 400,
                OutputTokenCount = 100,
            },
        };

        var cost = LlmProviderGuard.EstimateUpperBoundCost("gpt-5.6-sol", response);

        Assert.Equal(0.00695, cost, precision: 6);
    }

    [Fact]
    public void SolReservation_AdmitsObservedSpendWhileUnknownModelsRemainExpensive()
    {
        var guard = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance,
            options: new LlmProviderGuardOptions
            {
                HourlyUsdLimit = 1.0,
                DailyUsdLimit = 3.0,
                StatePath = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.json"),
            });

        Assert.True(guard.TryBeginCall("gpt-5.6-sol", true, out var lease, out _));
        Assert.Equal(0.20, lease.ReservedUsd);
        guard.RecordFixedCostSuccess(lease, 0.33);

        Assert.True(guard.TryBeginCall("gpt-5.6-sol", true, out var nextSol, out _));
        guard.RecordCallFailure(nextSol, new InvalidOperationException("test cleanup"));

        Assert.False(guard.TryBeginCall("unknown-expensive-model", true, out _, out var blocked));
        Assert.Equal("hourly_cost_budget_exhausted", blocked.Reason);
    }

    [Theory]
    [InlineData("hourly_cost_budget_exhausted", "hourly spending decree")]
    [InlineData("daily_cost_budget_exhausted", "daily spending decree")]
    [InlineData("credit_balance_exhausted", "until funding resumes")]
    [InlineData("authentication_failed", "failed authentication")]
    [InlineData("provider_circuit_open", "temporarily unavailable")]
    public void ProviderUnavailableDecree_DescribesTheActualHold(string reason, string expected)
    {
        var decree = WorldAutonomyOrchestrator.BuildProviderUnavailableDecree(reason);

        Assert.Contains(expected, decree, StringComparison.Ordinal);
    }

    private static ChatResponse Response(
        long input,
        long output,
        long cached = 0,
        long cacheWrite = 0) => new(new ChatMessage(ChatRole.Assistant, "done"))
        {
            ModelId = "gpt-5.6-sol",
            Usage = new UsageDetails
            {
                InputTokenCount = input,
                OutputTokenCount = output,
                CachedInputTokenCount = cached,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["cache_write_input_tokens"] = cacheWrite,
                },
            },
        };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}