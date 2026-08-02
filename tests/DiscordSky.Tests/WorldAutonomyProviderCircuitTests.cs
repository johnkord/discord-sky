using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyProviderCircuitTests
{
    [Fact]
    public void QuotaFailure_OpensImmediately_AndAllowsOneHalfOpenProbe()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        var circuit = new WorldAutonomyProviderCircuit(
            NullLogger<WorldAutonomyProviderCircuit>.Instance,
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
        var circuit = new WorldAutonomyProviderCircuit(
            NullLogger<WorldAutonomyProviderCircuit>.Instance,
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
        var circuit = new WorldAutonomyProviderCircuit(
            NullLogger<WorldAutonomyProviderCircuit>.Instance);

        Assert.False(circuit.RecordFailure(new InvalidOperationException("bad tool arguments")));
        Assert.True(circuit.TryEnter(out var snapshot));
        Assert.False(snapshot.IsOpen);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}