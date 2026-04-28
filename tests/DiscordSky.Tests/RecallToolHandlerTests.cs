using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Recall;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class RecallToolHandlerTests
{
    private const ulong AliceId = 100UL;
    private const ulong BobId = 200UL;

    private static UserMemory Mem(string content, MemoryKind kind = MemoryKind.Factual, DateTimeOffset? at = null) =>
        new(content, "chatter", at ?? DateTimeOffset.UtcNow.AddDays(-1), at ?? DateTimeOffset.UtcNow.AddDays(-1), 0, kind);

    private static RecallToolHandler Build(StubMemoryStore store, MemoryRelevanceOptions? opts = null)
    {
        var options = opts ?? new MemoryRelevanceOptions();
        var monitor = new TestOptionsMonitor<MemoryRelevanceOptions>(options);
        var scorer = new LexicalMemoryScorer(monitor);
        return new RecallToolHandler(
            store, scorer, options,
            allowedUserIds: new HashSet<ulong> { AliceId },
            logger: NullLogger.Instance);
    }

    [Fact]
    public async Task UnknownUser_ReturnsUnknownUserSentinel()
    {
        var store = new StubMemoryStore();
        var handler = Build(store);
        var result = await handler.RecallAsync(BobId, query: null);
        Assert.Same(RecallToolResult.UnknownUser, result);
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task UserWithNoNotes_ReturnsNoNotesSentinel()
    {
        var store = new StubMemoryStore();
        store.Notes[AliceId] = Array.Empty<UserMemory>();
        var handler = Build(store);
        var result = await handler.RecallAsync(AliceId, null);
        Assert.Same(RecallToolResult.NoNotes, result);
    }

    [Fact]
    public async Task UserWithNotes_ReturnsAll()
    {
        var store = new StubMemoryStore();
        store.Notes[AliceId] = new[] { Mem("has a cat"), Mem("likes vancouver") };
        var handler = Build(store);
        var result = await handler.RecallAsync(AliceId, null);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Notes.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task NotesBeyondTopK_AreTruncated()
    {
        var store = new StubMemoryStore();
        store.Notes[AliceId] = Enumerable.Range(0, 15).Select(i => Mem($"note {i}")).ToArray();
        var handler = Build(store, new MemoryRelevanceOptions { RecallTopK = 5 });
        var result = await handler.RecallAsync(AliceId, null);
        Assert.Equal(15, result.Total);
        Assert.Equal(5, result.Notes.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task QueryReorders_ButNeverFiltersOut()
    {
        // The scorer returning 0 for every memory must NOT cause an empty result.
        // This is the central design property: query is advisory ranking only.
        var store = new StubMemoryStore();
        store.Notes[AliceId] = new[]
        {
            Mem("has a cat named whiskers"),
            Mem("likes vancouver"),
            Mem("plays guitar"),
        };
        var handler = Build(store);
        var result = await handler.RecallAsync(AliceId, query: "completely unrelated alien topology");
        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Notes.Count);
    }

    [Fact]
    public async Task SuppressedMemoryBlocksMatchingNote_NotReturnedByRecall()
    {
        // Defence-in-depth: MemoryFilter.Admissible should hide suppressed/superseded items.
        var store = new StubMemoryStore();
        store.Notes[AliceId] = new[]
        {
            Mem("has a cat named whiskers"),
            new UserMemory("don't bring up cats", "user request",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0,
                MemoryKind.Suppressed, new[] { "pets" }),
        };
        // Suppressed memory's content has high overlap with the cat memory ("cat" token shared).
        var handler = Build(store, new MemoryRelevanceOptions { SuppressionOverlapThreshold = 0.05 });
        var result = await handler.RecallAsync(AliceId, null);
        // Suppressed itself is filtered, and the cat note is blocked by suppression.
        Assert.Equal(0, result.Total);
        Assert.Same(RecallToolResult.NoNotes, result);
    }

    [Fact]
    public async Task RepeatedRecall_HitsStoreOnce_PerUser()
    {
        var store = new StubMemoryStore();
        store.Notes[AliceId] = new[] { Mem("has a cat") };
        var handler = Build(store);
        await handler.RecallAsync(AliceId, null);
        await handler.RecallAsync(AliceId, "cats");
        await handler.RecallAsync(AliceId, "pets");
        Assert.Equal(1, store.GetCalls);
        Assert.Equal(3, handler.RecallsPerformed);
    }

    [Fact]
    public async Task PrefetchedNotes_AvoidStoreCall()
    {
        var store = new StubMemoryStore();
        var prefetched = new Dictionary<ulong, IReadOnlyList<UserMemory>>
        {
            [AliceId] = new[] { Mem("has a cat") }
        };
        var monitor = new TestOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions());
        var scorer = new LexicalMemoryScorer(monitor);
        var handler = new RecallToolHandler(
            store, scorer, new MemoryRelevanceOptions(),
            new HashSet<ulong> { AliceId },
            NullLogger.Instance,
            prefetched);
        var result = await handler.RecallAsync(AliceId, null);
        Assert.Equal(1, result.Total);
        Assert.Equal(0, store.GetCalls);
    }

    private sealed class StubMemoryStore : IUserMemoryStore
    {
        public Dictionary<ulong, IReadOnlyList<UserMemory>> Notes { get; } = new();
        public int GetCalls { get; private set; }
        public Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(Notes.TryGetValue(userId, out var list) ? list : Array.Empty<UserMemory>());
        }
        public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetAllAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default) => Task.CompletedTask;
    }
}
