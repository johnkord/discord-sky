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
    public async Task RejectedWrappedCallCannotPostponeRecoveryOrConsumeProbe()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        var guard = new LlmProviderGuard(NullLogger<LlmProviderGuard>.Instance, clock,
            options: new LlmProviderGuardOptions { StatePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json") });
        var inner = new NoDispatchClient();
        using var wrapped = new DiscordSky.Bot.Memory.Logging.TelemetryChatClient(inner, "test", new DiscordSky.Bot.Memory.Logging.NoOpTelemetrySink(), guard);
        guard.RecordFailure(new InvalidOperationException("insufficient_quota"));
        var deadline = guard.Snapshot().NextProbeAt;
        clock.Advance(TimeSpan.FromMinutes(4));
        await Assert.ThrowsAsync<LlmProviderBlockedException>(() => wrapped.GetResponseAsync("fixture"));
        Assert.Equal(deadline, guard.Snapshot().NextProbeAt);
        Assert.Equal("credit_balance_exhausted", guard.Snapshot().Reason);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(guard.CanAttempt(out _));
        Assert.True(guard.CanAttempt(out _));
        Assert.True(guard.TryBeginCall("gpt-6.2-astra", true, out var probe, out _));
        Assert.False(guard.TryBeginCall("gpt-6.2-astra", true, out var refused, out _));
        Assert.Null(refused);
        guard.RecordCallFailure(probe!, new TimeoutException());
        Assert.False(guard.CanAttempt(out _));
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

            Assert.True(guard.TryBeginCall("gpt-6-sol", true, out var lease, out _));
            guard.RecordCallSuccess(lease, Response(input: 20_000, output: 2_000));
            Assert.False(guard.TryBeginCall("gpt-6-sol", true, out _, out var blocked));
            Assert.Equal("hourly_cost_budget_exhausted", blocked.Reason);

            var restored = new LlmProviderGuard(
                NullLogger<LlmProviderGuard>.Instance,
                clock,
                options: options);
            Assert.False(restored.TryBeginCall("gpt-6-sol", true, out _, out _));
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

            Assert.False(guard.TryBeginCall("gpt-6-sol", true, out _, out var blocked));
            Assert.Equal("hourly_cost_budget_exhausted", blocked.Reason);
            Assert.Contains("hourlyCostUsd", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(string.Concat(path, ".tmp"));
        }
    }

    [Theory]
    [InlineData("gpt-5.6-sol", 0.006575)]
    [InlineData("gpt-6-sol", 0.00243)]
    [InlineData("gpt-6-astra", 0.01215)]
    [InlineData("gpt-6.2-astra", 0.00956)]
    [InlineData("grok-4.7", 0.002)]
    [InlineData("grok-4.7-2026-11-01", 0.002)]
    public void EstimateUpperBoundCost_UsesModelSpecificCachedWriteAndOutputRates(string model, double expected)
    {
        var response = Response(input: 1_000, output: 100, cached: 400, cacheWrite: 300);

        var cost = LlmProviderGuard.EstimateUpperBoundCost(model, response);

        Assert.Equal(expected, cost, precision: 6);
    }

    [Theory]
    [InlineData("gpt-5.6-sol", 0.00695)]
    [InlineData("gpt-6-sol", 0.00258)]
    [InlineData("gpt-6-astra", 0.0129)]
    [InlineData("gpt-6.2-astra", 0.01016)]
    [InlineData("grok-4.7", 0.002)]
    public void EstimateUpperBoundCost_TreatsUnobservedUncachedInputAsWrites(string model, double expected)
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

        var cost = LlmProviderGuard.EstimateUpperBoundCost(model, response);

        Assert.Equal(expected, cost, precision: 6);
    }

    [Fact]
    public void EstimateUpperBoundCost_Gpt6SolAppliesLongContextRatesAbove272KInput()
    {
        var atBoundary = LlmProviderGuard.EstimateUpperBoundCost(
            "gpt-6-sol", Response(input: 272_000, output: 100));
        var aboveBoundary = LlmProviderGuard.EstimateUpperBoundCost(
            "gpt-6-sol", Response(input: 272_001, output: 100));

        Assert.Equal(0.545, atBoundary, precision: 6);
        Assert.Equal(1.089504, aboveBoundary, precision: 6);
    }

    [Theory]
    [InlineData("gpt-6-astra", 2.725, 5.44752)]
    [InlineData("gpt-6.2-astra", 2.18, 4.358016)]
    public void EstimateUpperBoundCost_AstraAppliesLongContextRatesAbove272KInput(
        string model, double expectedAtBoundary, double expectedAboveBoundary)
    {
        var atBoundary = LlmProviderGuard.EstimateUpperBoundCost(
            model, Response(input: 272_000, output: 100));
        var aboveBoundary = LlmProviderGuard.EstimateUpperBoundCost(
            model, Response(input: 272_001, output: 100));

        Assert.Equal(expectedAtBoundary, atBoundary, precision: 6);
        Assert.Equal(expectedAboveBoundary, aboveBoundary, precision: 6);
    }

    [Theory]
    [InlineData(199_999, 0.400598)]
    [InlineData(200_000, 0.8012)]
    [InlineData(200_001, 0.801204)]
    public void Grok47Pricing_DoublesAllRatesAt200KInput(long input, double expected)
    {
        Assert.Equal(expected, LlmProviderGuard.EstimateUpperBoundCost("grok-4.7", Response(input, 100)), 6);
    }

    [Fact]
    public void Grok47Pricing_DoublesCachedInputRateAtLongContext()
    {
        Assert.Equal(0.8000, LlmProviderGuard.EstimateUpperBoundCost("grok-4.7", Response(200_000, 100, cached: 400)), 6);
    }

    [Theory]
    [InlineData("grok-4.7", 0.20)]
    [InlineData("grok-4.7-2026-11-01", 0.20)]
    [InlineData("grok-4.70", 0.75)]
    public void Grok47Reservation_DoesNotMispriceOtherModelVersions(string model, double expected)
    {
        var guard = new LlmProviderGuard(NullLogger<LlmProviderGuard>.Instance,
            options: new LlmProviderGuardOptions { StatePath = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.json") });
        Assert.True(guard.TryBeginCall(model, true, out var lease, out _));
        Assert.Equal(expected, lease.ReservedUsd);
        guard.RecordCallFailure(lease, new InvalidOperationException("test cleanup"));
    }

    [Theory]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-6.2-astra")]
    public void AstraReservation_AccountsForHigherCostWithinExistingBudget(string model)
    {
        var guard = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance,
            options: new LlmProviderGuardOptions
            {
                HourlyUsdLimit = 1.0,
                DailyUsdLimit = 3.0,
                StatePath = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.json"),
            });

        Assert.True(guard.TryBeginCall(model, true, out var lease, out _));
        Assert.Equal(0.50, lease.ReservedUsd);
        Assert.True(guard.TryBeginCall(model, false, out var secondLease, out _));
        Assert.False(guard.TryBeginCall(model, false, out _, out var blocked));
        Assert.Equal("hourly_cost_budget_exhausted", blocked.Reason);
        guard.RecordCallFailure(lease, new InvalidOperationException("test cleanup"));
        guard.RecordCallFailure(secondLease, new InvalidOperationException("test cleanup"));
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

        Assert.True(guard.TryBeginCall("gpt-6-sol", true, out var lease, out _));
        Assert.Equal(0.20, lease.ReservedUsd);
        guard.RecordFixedCostSuccess(lease, 0.33);

        Assert.True(guard.TryBeginCall("gpt-6-sol", true, out var nextSol, out _));
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

    [Theory]
    [InlineData("failed", "ClientResultException", "failed during this audience")]
    [InlineData("timed_out", "session_timeout", "ran out of time")]
    public void InterruptedDirectRun_ReportsExecutionFailureWithoutASecondModel(
        string status, string reason, string expected)
    {
        var opportunity = new WorldAutonomyOpportunity(4001, "message", "Change my nickname.", IsDirectAddress: true);

        var result = WorldAutonomyOrchestrator.BuildInterruptedRunResult(opportunity, "run", status, reason, activity: null);

        Assert.Equal("run", result.RunId);
        Assert.Equal((ulong)4001, result.GuildId);
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.FailureReason);
        Assert.False(result.SpokeInChannel);
        Assert.Contains(expected, result.FinalText, StringComparison.Ordinal);
        Assert.Contains("No server changes were attempted.", result.FinalText, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot", result.FinalText!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "ClientResultException")]
    [InlineData(true, "hourly_cost_budget_exhausted")]
    public void InterruptedRun_WithWritesDoesNotClaimNoChanges(bool providerUnavailable, string reason)
    {
        var opportunity = new WorldAutonomyOpportunity(4001, "message", "Change a role.", IsDirectAddress: true);
        var activity = new WorldAutonomyRunActivitySnapshot(1, 1, 0, 0, 0, 0, 1, false, false);

        var result = WorldAutonomyOrchestrator.BuildInterruptedRunResult(
            opportunity, "run", WorldAutonomyRunStatuses.Failed, reason, activity, providerUnavailable);

        Assert.Contains("Some server changes may already have occurred", result.FinalText, StringComparison.Ordinal);
        Assert.DoesNotContain("No server changes", result.FinalText!, StringComparison.Ordinal);
        if (providerUnavailable)
            Assert.Contains("hourly spending decree", result.FinalText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InterruptedRun_AfterDeliveryPreservesHandledState(bool speechDelivered, bool visualDelivered)
    {
        var opportunity = new WorldAutonomyOpportunity(4001, "message", "A petition.", IsDirectAddress: true);
        var activity = new WorldAutonomyRunActivitySnapshot(0, 0, 0, 0, 0, 0, 0, speechDelivered, visualDelivered);

        var result = WorldAutonomyOrchestrator.BuildInterruptedRunResult(
            opportunity, "run", WorldAutonomyRunStatuses.Failed, "ClientResultException", activity);

        Assert.True(result.SpokeInChannel);
        Assert.Null(result.FinalText);
    }

    [Fact]
    public void InterruptedAmbientRun_DoesNotProduceAnUnsolicitedFailureReply()
    {
        var opportunity = new WorldAutonomyOpportunity(4001, "message", "An ambient opportunity.");

        var result = WorldAutonomyOrchestrator.BuildInterruptedRunResult(
            opportunity, "run", WorldAutonomyRunStatuses.Failed, "ClientResultException", activity: null);

        Assert.Null(result.FinalText);
        Assert.False(result.SpokeInChannel);
    }

    private static ChatResponse Response(
        long input,
        long output,
        long cached = 0,
        long cacheWrite = 0) => new(new ChatMessage(ChatRole.Assistant, "done"))
        {
            ModelId = "gpt-6-sol",
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

    private sealed class NoDispatchClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected provider dispatch");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}