using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
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
        private int _callCount;

        public StubChatClient(TimeSpan delay) => _delay = delay;

        public ChatClientMetadata Metadata => new("stub");
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
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
        private readonly double _value;

        public FixedRandomProvider(double value = 0.0) => _value = value;

        public double NextDouble() => _value;
    }

    private sealed class SequenceRandomProvider : IRandomProvider
    {
        private readonly Queue<double> _values;

        public SequenceRandomProvider(params double[] values) => _values = new Queue<double>(values);

        public double NextDouble() => _values.Count > 0 ? _values.Dequeue() : 0.0;
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provider failed");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MemoryOperationChatClient : IChatClient
    {
        private readonly bool _includeEvidence;

        public MemoryOperationChatClient(bool includeEvidence) => _includeEvidence = includeEvidence;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "save",
                ["content"] = "Loves meteor showers",
                ["context"] = "shared a preference",
            };
            if (_includeEvidence)
            {
                using var document = JsonDocument.Parse("[11]");
                arguments["evidence_message_ids"] = document.RootElement.Clone();
            }
            var call = new FunctionCallContent(
                "call-memory",
                CreativeOrchestrator.UpdateUserMemoryConversationToolName,
                arguments);
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                new List<AIContent> { call })));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ReplaceTrackingMemoryStore : IUserMemoryStore
    {
        private readonly InMemoryUserMemoryStore _inner = new(
            Options.Create(new BotOptions { MaxMemoriesPerUser = 20 }),
            NullLogger<InMemoryUserMemoryStore>.Instance);

        public int ReplaceCount { get; private set; }

        public Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default) =>
            _inner.GetMemoriesAsync(userId, ct);
        public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default) =>
            _inner.SaveMemoryAsync(userId, content, context, ct);
        public Task SaveMemoryAsync(ulong userId, string content, string context, MemoryKind kind, IReadOnlyList<string>? topics, int? importance = null, CancellationToken ct = default) =>
            _inner.SaveMemoryAsync(userId, content, context, kind, topics, importance, ct);
        public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default) =>
            _inner.UpdateMemoryAsync(userId, index, content, context, ct);
        public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default) =>
            _inner.ForgetMemoryAsync(userId, index, ct);
        public Task ForgetAllAsync(ulong userId, CancellationToken ct = default) => _inner.ForgetAllAsync(userId, ct);
        public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default) => _inner.TouchMemoriesAsync(userId, ct);
        public Task TouchMemoriesAsync(ulong userId, IReadOnlyList<string> contents, CancellationToken ct = default) =>
            _inner.TouchMemoriesAsync(userId, contents, ct);

        public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default)
        {
            ReplaceCount++;
            return _inner.ReplaceAllMemoriesAsync(userId, memories, ct);
        }
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

    private DiscordBotService BuildService(
        IChatClient chatClient,
        IUserMemoryStore memoryStore,
        IRecallTelemetrySink? telemetry = null,
        IRandomProvider? randomProvider = null,
        double memoryExtractionRate = 1.0,
        MemoryExtractionOptions? memoryExtractionOptions = null)
    {
        telemetry ??= new NoOpTelemetrySink();
        var botOptions = Options.Create(new BotOptions
        {
            CommandPrefix = "!sky",
            MemoryExtractionRate = memoryExtractionRate,
            MaxMemoriesPerExtraction = 15,
            EnableMemoryConsolidation = false,
            EnableUserMemory = true,
        });
        var chaosSettings = new TestOptionsMonitor<ChaosSettings>(new ChaosSettings());
        var openAiOptions = new TestOptionsMonitor<LlmOptions>(new LlmOptions
        {
            ActiveProvider = "OpenAI",
            Providers = new Dictionary<string, LlmProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenAI"] = new LlmProviderOptions { ChatModel = "test-model" }
            }
        });

        var linkUnfurler = new StubLinkUnfurler();
        var contextAggregator = new ContextAggregator(
            botOptions, linkUnfurler, NullLogger<ContextAggregator>.Instance);
        var safetyFilter = new SafetyFilter(chaosSettings, NullLogger<SafetyFilter>.Instance);
        var memoryRelevanceMonitor = new TestOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions());
        var memoryScorer = new DiscordSky.Bot.Memory.Scoring.LexicalMemoryScorer(memoryRelevanceMonitor);
        var telemetryClient = new TelemetryChatClient(chatClient, "test", telemetry);
        var orchestrator = new CreativeOrchestrator(
            contextAggregator, telemetryClient, safetyFilter,
            openAiOptions, botOptions, memoryScorer, memoryRelevanceMonitor,
            memoryStore,
            NullLogger<CreativeOrchestrator>.Instance,
            telemetry);

        var socketConfig = new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds };
        var client = new DiscordSocketClient(socketConfig);

        _service = new DiscordBotService(
            client, botOptions, chaosSettings,
            orchestrator, contextAggregator, memoryStore,
            memoryRelevanceMonitor,
            linkUnfurler, NullLogger<DiscordBotService>.Instance,
            telemetry,
            randomProvider ?? new FixedRandomProvider(),
            memoryExtractionOptions: Options.Create(memoryExtractionOptions ?? new MemoryExtractionOptions()));

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
        PopulateChannelBuffer(service, 1, [new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: now)]);
        PopulateChannelBuffer(service, 2, [new BufferedMessage(MessageId: 2, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hi there!", Timestamp: now)]);

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
        PopulateChannelBuffer(service, 1, [new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: now)]);
        PopulateChannelBuffer(service, 2, [new BufferedMessage(MessageId: 2, AuthorId: 200, AuthorDisplayName: "Bob", Content: "Hi!", Timestamp: now)]);

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
            new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: now),
            new BufferedMessage(MessageId: 2, AuthorId: 200, AuthorDisplayName: "Bob", Content: "Hey!", Timestamp: now)
        ]);
        // Channel 2: user 200 then 100 (reversed natural order)
        PopulateChannelBuffer(service, 2,
        [
            new BufferedMessage(MessageId: 3, AuthorId: 200, AuthorDisplayName: "Bob", Content: "Yo!", Timestamp: now),
            new BufferedMessage(MessageId: 4, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Sup!", Timestamp: now)
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
        PopulateChannelBuffer(service, 1, [new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: now)]);
        await service.ProcessConversationWindowAsync(1); // Should not throw (caught internally)

        // Second window for the same user: must not deadlock
        PopulateChannelBuffer(service, 1, [new BufferedMessage(MessageId: 2, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Again!", Timestamp: now)]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = service.ProcessConversationWindowAsync(1);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
        await cts.CancelAsync();

        Assert.Equal(task, completed); // Would timeout if semaphore wasn't released
    }

    [Fact]
    public async Task SampledOutWindow_EmitsTerminalWithoutProviderCall()
    {
        var telemetry = new InMemoryTelemetrySink();
        var service = BuildService(
            new StubChatClient(TimeSpan.Zero),
            new ConcurrencyTrackingMemoryStore(),
            telemetry,
            new FixedRandomProvider(1.0),
            memoryExtractionRate: 0.0);
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: DateTimeOffset.UtcNow)
        ]);

        await service.ProcessConversationWindowAsync(1);

        var terminal = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryExtraction));
        Assert.Equal("sampled_out", terminal.Outcome);
        Assert.Equal("rate_limiter", terminal.ReasonCode);
        Assert.NotNull(terminal.OperationId);
        Assert.DoesNotContain(telemetry.Events, evt => evt.EventType == TelemetryEventTypes.LlmCall);
    }

    [Fact]
    public async Task CalledWindow_JoinsProviderAndTerminalByOperationId()
    {
        var telemetry = new InMemoryTelemetrySink();
        var service = BuildService(
            new StubChatClient(TimeSpan.Zero),
            new ConcurrencyTrackingMemoryStore(),
            telemetry);
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: DateTimeOffset.UtcNow)
        ]);

        await service.ProcessConversationWindowAsync(1);

        var provider = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.LlmCall));
        var terminal = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryExtraction));
        Assert.Equal("ok_no_operations", terminal.Outcome);
        Assert.Equal(provider.OperationId, terminal.OperationId);
        Assert.Equal(1, terminal.ContextMessageCount);
        Assert.Equal(1, terminal.ParticipantCount);
        Assert.Equal(0, terminal.ProposedCount);
    }

    [Fact]
    public async Task ProviderFailure_EmitsFailedTerminalWithSameOperationId()
    {
        var telemetry = new InMemoryTelemetrySink();
        var service = BuildService(
            new ThrowingChatClient(),
            new ConcurrencyTrackingMemoryStore(),
            telemetry);
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: DateTimeOffset.UtcNow)
        ]);

        await service.ProcessConversationWindowAsync(1);

        var provider = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.LlmCall));
        var terminal = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryExtraction));
        Assert.Equal("error", provider.Outcome);
        Assert.Equal("failed", terminal.Outcome);
        Assert.Equal("provider_InvalidOperationException", terminal.ReasonCode);
        Assert.Equal(provider.OperationId, terminal.OperationId);
    }

    [Fact]
    public async Task ShutdownAndConcurrentWindows_HaveDistinctLabeledOperationIds()
    {
        var telemetry = new InMemoryTelemetrySink();
        var service = BuildService(
            new StubChatClient(TimeSpan.Zero),
            new ConcurrencyTrackingMemoryStore(),
            telemetry);
        var now = DateTimeOffset.UtcNow;
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(MessageId: 1, AuthorId: 100, AuthorDisplayName: "Alice", Content: "Hello!", Timestamp: now)
        ]);
        PopulateChannelBuffer(service, 2,
        [
            new BufferedMessage(MessageId: 2, AuthorId: 200, AuthorDisplayName: "Bob", Content: "Hi!", Timestamp: now)
        ]);

        await Task.WhenAll(
            service.ProcessConversationWindowAsync(1, isShutdownFlush: true),
            service.ProcessConversationWindowAsync(2));

        var terminals = telemetry.Events
            .Where(evt => evt.EventType == TelemetryEventTypes.MemoryExtraction)
            .ToList();
        Assert.Equal(2, terminals.Count);
        Assert.Equal(2, terminals.Select(evt => evt.OperationId).Distinct().Count());
        Assert.Single(terminals, evt => evt.Outcome == "shutdown_flush" && evt.IsShutdownFlush == true);
        Assert.Single(terminals, evt => evt.Outcome == "ok_no_operations" && evt.IsShutdownFlush == false);
    }

    [Fact]
    public async Task OptionalEvidence_ShadowAppliesLegacyWriteAndReportsMatch()
    {
        var telemetry = new InMemoryTelemetrySink();
        var store = new ReplaceTrackingMemoryStore();
        var service = BuildService(
            new MemoryOperationChatClient(includeEvidence: false),
            store,
            telemetry,
            memoryExtractionOptions: new MemoryExtractionOptions { EvidenceRequired = false });
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(11, 100, "Alice", "I love meteor showers", DateTimeOffset.UtcNow)
        ]);

        await service.ProcessConversationWindowAsync(1);

        var memory = Assert.Single(await store.GetMemoriesAsync(100));
        Assert.Equal("Loves meteor showers", memory.Content);
        Assert.Null(memory.Provenance);
        Assert.Equal(0, store.ReplaceCount);
        var transition = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryTransition));
        Assert.Equal("shadow_match", transition.Outcome);
        Assert.Equal(1, transition.MissingEvidenceCount);
        Assert.False(transition.Diverged);
    }

    [Fact]
    public async Task RequiredEvidence_MissingEvidenceRejectsWholeUserPlan()
    {
        var telemetry = new InMemoryTelemetrySink();
        var store = new ReplaceTrackingMemoryStore();
        var service = BuildService(
            new MemoryOperationChatClient(includeEvidence: false),
            store,
            telemetry,
            memoryExtractionOptions: new MemoryExtractionOptions { EvidenceRequired = true });
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(11, 100, "Alice", "I love meteor showers", DateTimeOffset.UtcNow)
        ]);

        await service.ProcessConversationWindowAsync(1);

        Assert.Empty(await store.GetMemoriesAsync(100));
        Assert.Equal(0, store.ReplaceCount);
        var transition = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryTransition));
        Assert.Equal("verified_rejected", transition.Outcome);
        Assert.Equal(0, transition.AppliedCount);
        Assert.Equal(1, transition.RejectedCount);
        var terminal = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryExtraction));
        Assert.Equal(0, terminal.AppliedCount);
        Assert.Equal(1, terminal.RejectedCount);
    }

    [Fact]
    public async Task RequiredEvidence_ValidPlanUsesOneReplacementAndPersistsProvenance()
    {
        var telemetry = new InMemoryTelemetrySink();
        var store = new ReplaceTrackingMemoryStore();
        var service = BuildService(
            new MemoryOperationChatClient(includeEvidence: true),
            store,
            telemetry,
            memoryExtractionOptions: new MemoryExtractionOptions { EvidenceRequired = true });
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(11, 100, "Alice", "I love meteor showers", DateTimeOffset.UtcNow)
        ]);

        await service.ProcessConversationWindowAsync(1);

        var memory = Assert.Single(await store.GetMemoriesAsync(100));
        Assert.Equal(1, store.ReplaceCount);
        Assert.NotNull(memory.Provenance);
        Assert.Equal(new[] { 11UL }, memory.Provenance!.EvidenceMessageIds);
        var transition = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryTransition));
        Assert.Equal("verified_applied", transition.Outcome);
        Assert.Equal(1, transition.ValidEvidenceCount);
        Assert.Equal(transition.PredictedStateDigest, transition.ActualStateDigest);
        Assert.False(transition.Diverged);
    }

    [Fact]
    public async Task OpportunityGate_OffAlwaysCallsProviderWithoutGateTelemetry()
    {
        var telemetry = new InMemoryTelemetrySink();
        var client = new StubChatClient(TimeSpan.Zero);
        var service = BuildService(
            client,
            new ReplaceTrackingMemoryStore(),
            telemetry,
            memoryExtractionOptions: new MemoryExtractionOptions
            {
                OpportunityGateMode = MemoryOpportunityGateMode.Off,
            });
        PopulateTinyWindow(service);

        await service.ProcessConversationWindowAsync(1);

        Assert.Equal(1, client.CallCount);
        Assert.DoesNotContain(telemetry.Events, evt => evt.EventType == TelemetryEventTypes.MemoryOpportunity);
    }

    [Fact]
    public async Task OpportunityGate_ShadowWouldSkipButStillCallsProvider()
    {
        var telemetry = new InMemoryTelemetrySink();
        var client = new StubChatClient(TimeSpan.Zero);
        var service = BuildService(
            client,
            new ReplaceTrackingMemoryStore(),
            telemetry,
            memoryExtractionOptions: new MemoryExtractionOptions
            {
                OpportunityGateMode = MemoryOpportunityGateMode.Shadow,
            });
        PopulateTinyWindow(service);

        await service.ProcessConversationWindowAsync(1);

        Assert.Equal(1, client.CallCount);
        var gate = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryOpportunity));
        Assert.Equal("shadow_would_skip", gate.Outcome);
        Assert.True(gate.GateWouldSkip);
        Assert.False(gate.IsExplorationRun);
        Assert.Equal("tiny_single_message", gate.ReasonCode);
    }

    [Fact]
    public async Task OpportunityGate_LiveSkipEmitsTerminalWithoutProviderCall()
    {
        var telemetry = new InMemoryTelemetrySink();
        var client = new StubChatClient(TimeSpan.Zero);
        var service = BuildService(
            client,
            new ReplaceTrackingMemoryStore(),
            telemetry,
            new SequenceRandomProvider(0.0, 0.9),
            memoryExtractionOptions: new MemoryExtractionOptions
            {
                OpportunityGateMode = MemoryOpportunityGateMode.Live,
                ExplorationRate = 0.05,
            });
        PopulateTinyWindow(service);

        await service.ProcessConversationWindowAsync(1);

        Assert.Equal(0, client.CallCount);
        Assert.DoesNotContain(telemetry.Events, evt => evt.EventType == TelemetryEventTypes.LlmCall);
        var terminal = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryExtraction));
        Assert.Equal("gate_skipped", terminal.Outcome);
        Assert.Equal("tiny_single_message", terminal.ReasonCode);
    }

    [Fact]
    public async Task OpportunityGate_ExplorationSamplesOnlyWouldSkipWindowsAndCallsProvider()
    {
        var telemetry = new InMemoryTelemetrySink();
        var client = new StubChatClient(TimeSpan.Zero);
        var service = BuildService(
            client,
            new ReplaceTrackingMemoryStore(),
            telemetry,
            new SequenceRandomProvider(0.0, 0.01),
            memoryExtractionOptions: new MemoryExtractionOptions
            {
                OpportunityGateMode = MemoryOpportunityGateMode.Live,
                ExplorationRate = 0.05,
            });
        PopulateTinyWindow(service);

        await service.ProcessConversationWindowAsync(1);

        Assert.Equal(1, client.CallCount);
        var gate = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryOpportunity));
        Assert.Equal("exploration_run", gate.Outcome);
        Assert.True(gate.IsExplorationRun);
        var terminal = Assert.Single(telemetry.Events.Where(evt => evt.EventType == TelemetryEventTypes.MemoryExtraction));
        Assert.Equal("exploration_run", terminal.ReasonCode);
    }

    [Fact]
    public async Task OpportunityGate_ProductiveCueRunsAndShutdownDefaultsToRunAlways()
    {
        var telemetry = new InMemoryTelemetrySink();
        var client = new StubChatClient(TimeSpan.Zero);
        var service = BuildService(
            client,
            new ReplaceTrackingMemoryStore(),
            telemetry,
            new SequenceRandomProvider(0.0, 0.0),
            memoryExtractionOptions: new MemoryExtractionOptions
            {
                OpportunityGateMode = MemoryOpportunityGateMode.Live,
                ExplorationRate = 0.0,
                ShutdownFlushPolicy = ShutdownFlushExtractionPolicy.RunAlways,
            });
        PopulateChannelBuffer(service, 1,
        [
            new BufferedMessage(11, 100, "Alice", "I prefer tea to coffee", DateTimeOffset.UtcNow)
        ]);
        PopulateChannelBuffer(service, 2,
        [
            new BufferedMessage(12, 200, "Bob", "gg", DateTimeOffset.UtcNow)
        ]);

        await service.ProcessConversationWindowAsync(1);
        await service.ProcessConversationWindowAsync(2, isShutdownFlush: true);

        Assert.Equal(2, client.CallCount);
        var outcomes = telemetry.Events
            .Where(evt => evt.EventType == TelemetryEventTypes.MemoryOpportunity)
            .Select(evt => evt.Outcome)
            .ToArray();
        Assert.Equal(new[] { "gate_run", "gate_run" }, outcomes);
        Assert.Contains(telemetry.Events, evt =>
            evt.EventType == TelemetryEventTypes.MemoryExtraction
            && evt.Outcome == "shutdown_flush"
            && evt.GateWouldSkip == false);
    }

    private static void PopulateTinyWindow(DiscordBotService service) => PopulateChannelBuffer(service, 1,
    [
        new BufferedMessage(11, 100, "Alice", "gg", DateTimeOffset.UtcNow)
    ]);
}
