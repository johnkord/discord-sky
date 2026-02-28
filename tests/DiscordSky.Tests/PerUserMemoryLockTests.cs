using System.Collections.Concurrent;
using System.Reflection;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class PerUserMemoryLockTests : IAsyncDisposable
{
    // ── Stubs ───────────────────────────────────────────────────────────

    /// <summary>
    /// Chat client stub that returns a plain text response (no tool calls)
    /// after an optional delay. Since no tool calls are emitted,
    /// <see cref="CreativeOrchestrator.ParseMultiUserMemoryOperations"/>
    /// returns an empty list, making the test focus purely on lock behaviour.
    /// </summary>
    private sealed class StubChatClient : IChatClient
    {
        private readonly TimeSpan _delay;

        public StubChatClient(TimeSpan delay) => _delay = delay;

        public ChatClientMetadata Metadata => new("stub");

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Nothing notable."));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubLinkUnfurler : ILinkUnfurler
    {
        public bool CanHandle(Uri url) => false;
        public Task<IReadOnlyList<UnfurledLink>> UnfurlAsync(
            string content, DateTimeOffset ts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UnfurledLink>>(Array.Empty<UnfurledLink>());
    }

    private sealed class FixedRandomProvider : IRandomProvider
    {
        public double NextDouble() => 0.0; // Always below MemoryExtractionRate
    }

    /// <summary>
    /// Memory store that tracks per-user concurrent read counts.
    /// The 100 ms delay in <see cref="GetMemoriesAsync"/> widens the window
    /// so overlapping reads are reliably detected.
    /// </summary>
    private sealed class ConcurrencyTrackingMemoryStore : IUserMemoryStore
    {
        private readonly ConcurrentDictionary<ulong, int> _concurrentByUser = new();
        private readonly ConcurrentDictionary<ulong, int> _maxConcurrentByUser = new();

        public int GetMaxConcurrentReads(ulong userId) =>
            _maxConcurrentByUser.GetValueOrDefault(userId, 0);

        public async Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default)
        {
            var current = _concurrentByUser.AddOrUpdate(userId, 1, (_, v) => v + 1);
            _maxConcurrentByUser.AddOrUpdate(userId, current, (_, v) => Math.Max(v, current));

            await Task.Delay(100, ct); // widen the overlap window

            _concurrentByUser.AddOrUpdate(userId, 0, (_, v) => v - 1);
            return Array.Empty<UserMemory>();
        }

        public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetAllAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Memory store that throws on the first GetMemoriesAsync call.
    /// Used to verify that per-user locks are released in the finally block.
    /// </summary>
    private sealed class ThrowOnFirstCallMemoryStore : IUserMemoryStore
    {
        private int _callCount;

        public Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
                throw new InvalidOperationException("Simulated store failure");
            return Task.FromResult<IReadOnlyList<UserMemory>>(Array.Empty<UserMemory>());
        }

        public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetAllAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private DiscordBotService? _service;

    private DiscordBotService BuildService(IChatClient chatClient, IUserMemoryStore memoryStore)
    {
        var botOptions = Options.Create(new BotOptions
        {
            CommandPrefix = "!sky",
            MemoryExtractionRate = 1.0,
            MaxMemoriesPerExtraction = 15,
            EnableMemoryConsolidation = false,
            EnableUserMemory = true,
        });
        var chaosSettings = new TestOptionsMonitor<ChaosSettings>(new ChaosSettings());
        var openAiOptions = new TestOptionsMonitor<OpenAIOptions>(new OpenAIOptions { ChatModel = "test-model" });

        var linkUnfurler = new StubLinkUnfurler();
        var contextAggregator = new ContextAggregator(
            botOptions, linkUnfurler, NullLogger<ContextAggregator>.Instance);
        var safetyFilter = new SafetyFilter(chaosSettings, NullLogger<SafetyFilter>.Instance);
        var orchestrator = new CreativeOrchestrator(
            contextAggregator, chatClient, safetyFilter,
            openAiOptions, botOptions, NullLogger<CreativeOrchestrator>.Instance);

        var socketConfig = new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds };
        var client = new DiscordSocketClient(socketConfig);

        _service = new DiscordBotService(
            client, botOptions, chaosSettings,
            orchestrator, contextAggregator, memoryStore,
            linkUnfurler, NullLogger<DiscordBotService>.Instance,
            new FixedRandomProvider());

        return _service;
    }

    /// <summary>
    /// Injects buffered messages into a channel buffer via reflection
    /// so <see cref="DiscordBotService.ProcessConversationWindowAsync"/> has data to process.
    /// </summary>
    private static void PopulateChannelBuffer(
        DiscordBotService service, ulong channelId, List<BufferedMessage> messages)
    {
        var field = typeof(DiscordBotService).GetField(
            "_channelBuffers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var buffers = (ConcurrentDictionary<ulong, ChannelMessageBuffer>)field.GetValue(service)!;

        var buffer = buffers.GetOrAdd(channelId, _ => new ChannelMessageBuffer());
        lock (buffer.Lock)
        {
            buffer.Messages.AddRange(messages);
            buffer.FirstMessageAt = messages[0].Timestamp;
            buffer.LastMessageAt = messages[^1].Timestamp;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
            await _service.DisposeAsync();
    }

    // ── Tests ───────────────────────────────────────────────────────────

    /// <summary>
    /// Two conversation windows containing the same user should never read
    /// that user's memories concurrently — the per-user semaphore serialises access.
    /// </summary>
    [Fact]
    public async Task SameUser_ConcurrentWindows_AreSerialized()
    {
        var memoryStore = new ConcurrencyTrackingMemoryStore();
        var service = BuildService(new StubChatClient(TimeSpan.FromMilliseconds(50)), memoryStore);

        var now = DateTimeOffset.UtcNow;
        PopulateChannelBuffer(service, 1, [new BufferedMessage(100, "Alice", "Hello!", now)]);
        PopulateChannelBuffer(service, 2, [new BufferedMessage(100, "Alice", "Hi there!", now)]);

        await Task.WhenAll(
            service.ProcessConversationWindowAsync(1),
            service.ProcessConversationWindowAsync(2));

        // If the lock weren't held, the 100 ms delay in the store would cause both
        // reads to overlap, pushing max concurrent reads for user 100 to 2.
        Assert.Equal(1, memoryStore.GetMaxConcurrentReads(100));
    }

    /// <summary>
    /// Two windows with completely different participants should complete
    /// without blocking each other (no shared locks).
    /// </summary>
    [Fact]
    public async Task DifferentUsers_ConcurrentWindows_DoNotDeadlock()
    {
        var memoryStore = new ConcurrencyTrackingMemoryStore();
        var service = BuildService(new StubChatClient(TimeSpan.FromMilliseconds(50)), memoryStore);

        var now = DateTimeOffset.UtcNow;
        PopulateChannelBuffer(service, 1, [new BufferedMessage(100, "Alice", "Hello!", now)]);
        PopulateChannelBuffer(service, 2, [new BufferedMessage(200, "Bob", "Hi!", now)]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var both = Task.WhenAll(
            service.ProcessConversationWindowAsync(1),
            service.ProcessConversationWindowAsync(2));
        var completed = await Task.WhenAny(both, Task.Delay(Timeout.Infinite, cts.Token));
        await cts.CancelAsync();

        Assert.Equal(both, completed); // Would timeout if they blocked each other
    }

    /// <summary>
    /// When two channels share overlapping but differently-ordered participants
    /// (channel 1 → [100, 200], channel 2 → [200, 100]), sorted lock acquisition
    /// prevents the classic ABBA deadlock.
    /// </summary>
    [Fact]
    public async Task OverlappingParticipants_SortedLockOrder_PreventsDeadlock()
    {
        var memoryStore = new ConcurrencyTrackingMemoryStore();
        var service = BuildService(new StubChatClient(TimeSpan.FromMilliseconds(50)), memoryStore);

        var now = DateTimeOffset.UtcNow;
        // Channel 1: user 100 then 200
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(100, "Alice", "Hello!", now),
            new BufferedMessage(200, "Bob", "Hey!", now)
        ]);
        // Channel 2: user 200 then 100 (reversed natural order)
        PopulateChannelBuffer(service, 2,
        [
            new BufferedMessage(200, "Bob", "Yo!", now),
            new BufferedMessage(100, "Alice", "Sup!", now)
        ]);

        // Without sorted lock ordering this could deadlock:
        //   Channel 1 acquires lock(100), Channel 2 acquires lock(200)
        //   Channel 1 waits for lock(200), Channel 2 waits for lock(100) → deadlock
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var both = Task.WhenAll(
            service.ProcessConversationWindowAsync(1),
            service.ProcessConversationWindowAsync(2));
        var completed = await Task.WhenAny(both, Task.Delay(Timeout.Infinite, cts.Token));
        await cts.CancelAsync();

        Assert.Equal(both, completed);
    }

    /// <summary>
    /// If the memory store throws during the locked section, the per-user semaphore
    /// must still be released so subsequent calls don't deadlock.
    /// </summary>
    [Fact]
    public async Task Lock_IsReleased_AfterExceptionInMemoryStore()
    {
        // First GetMemoriesAsync call throws; second succeeds
        var memoryStore = new ThrowOnFirstCallMemoryStore();
        var service = BuildService(new StubChatClient(TimeSpan.Zero), memoryStore);

        var now = DateTimeOffset.UtcNow;

        // First window: store throws inside the locked section
        PopulateChannelBuffer(service, 1, [new BufferedMessage(100, "Alice", "Hello!", now)]);
        await service.ProcessConversationWindowAsync(1); // Should not throw (caught internally)

        // Second window for the same user: must not deadlock
        PopulateChannelBuffer(service, 1, [new BufferedMessage(100, "Alice", "Again!", now)]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = service.ProcessConversationWindowAsync(1);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
        await cts.CancelAsync();

        Assert.Equal(task, completed); // Would timeout if semaphore wasn't released
    }
}
