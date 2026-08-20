using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyContinuityObserverTests
{
    [Fact]
    public async Task Observe_ShadowCandidateEmitsIdsAndDigestWithoutRawMemory()
    {
        var memoryStore = new InMemoryUserMemoryStore(
            Options.Create(new BotOptions { MaxMemoriesPerUser = 30 }),
            NullLogger<InMemoryUserMemoryStore>.Instance);
        await memoryStore.SaveMemoryAsync(
            42,
            "Kirin knocked the router offline during a stream.",
            "shared callback",
            MemoryKind.Running,
            ["kirin", "router"],
            importance: 9);
        await memoryStore.SaveMemoryAsync(
            42,
            "The user enjoys network troubleshooting.",
            "stable preference",
            MemoryKind.Factual,
            ["network"],
            importance: 6);
        var memoryOptions = new TestOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions());
        var telemetry = new InMemoryTelemetrySink();
        var observer = new WorldAutonomyContinuityObserver(
            memoryStore,
            memoryOptions,
            new LexicalMemoryScorer(memoryOptions),
            Options.Create(new WorldAutonomyOptions { ContinuityBriefShadowEnabled = true }),
            telemetry,
            NullLogger<WorldAutonomyContinuityObserver>.Instance);

        var brief = await observer.ObserveAsync(
            "ambient_conversation",
            42,
            "member",
            "Kirin broke the router again",
            100,
            "operation-1",
            CancellationToken.None);

        Assert.NotNull(brief);
        Assert.Equal(2, brief.MemoryIds.Count);
        Assert.InRange(brief.Text.Length, 1, WorldAutonomyContinuityObserver.MaxBriefChars);
        var evt = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.WorldAutonomyContinuity, evt.EventType);
        Assert.Equal("shadow_candidate", evt.Outcome);
        Assert.Equal(brief.MemoryIds, evt.MemoryIds);
        Assert.Equal(brief.Digest, evt.ProjectionDigest);
        Assert.Equal(2, evt.Count);
        Assert.Equal(2, evt.Total);
        Assert.Null(evt.Note);
        Assert.Null(evt.Room);
    }

    [Fact]
    public async Task Observe_DisabledDoesNotReadOrEmit()
    {
        var memoryStore = new ThrowingMemoryStore();
        var memoryOptions = new TestOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions());
        var telemetry = new InMemoryTelemetrySink();
        var observer = new WorldAutonomyContinuityObserver(
            memoryStore,
            memoryOptions,
            new LexicalMemoryScorer(memoryOptions),
            Options.Create(new WorldAutonomyOptions { ContinuityBriefShadowEnabled = false }),
            telemetry,
            NullLogger<WorldAutonomyContinuityObserver>.Instance);

        var brief = await observer.ObserveAsync(
            "ambient_full",
            42,
            "member",
            "message",
            100,
            "operation-1",
            CancellationToken.None);

        Assert.Null(brief);
        Assert.Empty(telemetry.Events);
    }

    private sealed class ThrowingMemoryStore : IUserMemoryStore
    {
        public Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default) =>
            throw new InvalidOperationException("disabled observer must not read memory");

        public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ForgetAllAsync(ulong userId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}