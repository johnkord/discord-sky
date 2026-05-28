using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Recall;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the telemetry hooks added during the 2026-05-28 deep-investigation pass.
/// Verifies that the four observability gaps surfaced in the doc actually emit events.
/// </summary>
public class TelemetryHookTests
{
    private const ulong AliceId = 1000UL;
    private const ulong BobId = 2000UL;

    private static RecallToolHandler BuildHandler(InMemoryTelemetrySink sink, params UserMemory[] notes)
    {
        var store = new StubStore();
        store.Notes[AliceId] = notes;
        var opts = new MemoryRelevanceOptions();
        var monitor = new TestOptionsMonitor<MemoryRelevanceOptions>(opts);
        var scorer = new LexicalMemoryScorer(monitor);
        return new RecallToolHandler(
            store, scorer, opts,
            allowedUserIds: new HashSet<ulong> { AliceId },
            logger: NullLogger.Instance,
            telemetry: sink);
    }

    private static UserMemory Mem(string content) =>
        new(content, "chatter", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1), 0);

    [Fact]
    public async Task RecallToolHandler_EmitsOk_OnSuccessfulRecall()
    {
        var sink = new InMemoryTelemetrySink();
        var handler = BuildHandler(sink, Mem("alice has a cat"), Mem("alice likes vancouver"));

        await handler.RecallAsync(AliceId, query: null);

        var events = sink.Events.ToList();
        Assert.Single(events);
        Assert.Equal(TelemetryEventTypes.RecallToolOk, events[0].EventType);
        Assert.Equal(2, events[0].Count);
        Assert.Equal(2, events[0].Total);
        Assert.Equal(1, events[0].CallIndex);
    }

    [Fact]
    public async Task RecallToolHandler_EmitsUnknownUser_OnDisallowedUserId()
    {
        var sink = new InMemoryTelemetrySink();
        var handler = BuildHandler(sink, Mem("alice has a cat"));

        await handler.RecallAsync(BobId, query: null);

        var events = sink.Events.ToList();
        Assert.Single(events);
        Assert.Equal(TelemetryEventTypes.RecallToolUnknownUser, events[0].EventType);
    }

    [Fact]
    public async Task RecallToolHandler_EmitsNoNotes_WhenStoreIsEmpty()
    {
        var sink = new InMemoryTelemetrySink();
        var handler = BuildHandler(sink); // no notes

        await handler.RecallAsync(AliceId, query: null);

        var events = sink.Events.ToList();
        Assert.Single(events);
        Assert.Equal(TelemetryEventTypes.RecallToolNoNotes, events[0].EventType);
    }

    [Fact]
    public void TelemetryEvent_GatewayDisconnect_RoundTripsWithReason()
    {
        // Smoke test that the new Reason field serialises and the GatewayDisconnect constant
        // is what we emit for reconnect storms.
        var sink = new InMemoryTelemetrySink();
        sink.Emit(new TelemetryEvent(
            DateTimeOffset.UtcNow,
            TelemetryEventTypes.GatewayDisconnect,
            Reason: "GatewayReconnectException"));

        var captured = sink.Events.Single();
        Assert.Equal("gateway_disconnect", captured.EventType);
        Assert.Equal("GatewayReconnectException", captured.Reason);
        Assert.Null(captured.UserHash);
    }

    [Fact]
    public void TelemetryEvent_Consolidation_CarriesBeforeAfter()
    {
        var sink = new InMemoryTelemetrySink();
        sink.Emit(new TelemetryEvent(
            DateTimeOffset.UtcNow,
            TelemetryEventTypes.ConsolidationOk,
            UserHash: "abc",
            Before: 20, After: 15));

        var captured = sink.Events.Single();
        Assert.Equal("consolidation_ok", captured.EventType);
        Assert.Equal(20, captured.Before);
        Assert.Equal(15, captured.After);
    }

    private sealed class StubStore : IUserMemoryStore
    {
        public Dictionary<ulong, IReadOnlyList<UserMemory>> Notes { get; } = new();
        public Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default) =>
            Task.FromResult(Notes.TryGetValue(userId, out var n) ? n : (IReadOnlyList<UserMemory>)Array.Empty<UserMemory>());
        public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetAllAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default) => Task.CompletedTask;
    }
}
