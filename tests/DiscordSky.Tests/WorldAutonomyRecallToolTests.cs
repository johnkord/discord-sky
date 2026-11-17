using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyRecallToolTests
{
    [Fact]
    public async Task Recall_IsBoundToRequesterAndDoesNotLoadUntilSelected()
    {
        var store = new Store();
        store.Notes[100] = [Memory("builds model railways")];
        store.Notes[200] = [Memory("another member's private note")];
        var telemetry = new InMemoryTelemetrySink();
        var tool = Build(store, telemetry).Bind(Opportunity(), Context());
        Assert.Equal(0, store.Loads);
        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("user_id", out _));

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "railways", ["user_id"] = "200" });

        Assert.Contains("builds model railways", result!.ToString());
        Assert.DoesNotContain("private note", result.ToString());
        Assert.Equal((ulong)100, store.LastUser);
        var metric = Assert.Single(telemetry.Events);
        Assert.Equal("run-recall", metric.OperationId);
        Assert.Equal((ulong)5001, metric.MessageId);
        Assert.Equal(TelemetryEventTypes.RecallToolOk, metric.EventType);
    }

    [Fact]
    public async Task Recall_ReusesFiltersAndEnforcesConcurrentBudget()
    {
        var store = new Store();
        store.Notes[100] = [Memory("plays guitar"), Memory("do not mention guitar", MemoryKind.Suppressed),
            Memory("old fact") with { Superseded = true }, Memory("model railways")];
        var telemetry = new InMemoryTelemetrySink();
        var tool = Build(store, telemetry, new MemoryRelevanceOptions
            { MaxRecallsPerReply = 1, RecallTopK = 1, SuppressionOverlapThreshold = 0.05 }).Bind(Opportunity(), Context());
        var results = await Task.WhenAll(tool.InvokeAsync(new AIFunctionArguments { ["query"] = "railways" }).AsTask(),
            tool.InvokeAsync(new AIFunctionArguments { ["query"] = "guitar" }).AsTask());
        var combined = string.Join(" ", results.Select(result => result?.ToString()));
        Assert.Contains("model railways", combined);
        Assert.DoesNotContain("plays guitar", combined);
        Assert.DoesNotContain("old fact", combined);
        Assert.Equal(1, store.Loads);
        Assert.Contains(telemetry.Events, record => record.Outcome == "budget_exceeded");
    }

    [Fact]
    public void Recall_EligibilityPreservesMemoryAndGuildPolicies()
    {
        var tool = Build(new Store(), new NoOpTelemetrySink());
        Assert.True(tool.CanBind(Opportunity()));
        Assert.False(tool.CanBind(Opportunity() with { IsDirectAddress = false }));
        Assert.False(tool.CanBind(Opportunity() with { SourceAuthorId = null }));
        Assert.False(Build(new Store(), new NoOpTelemetrySink(), bot: new BotOptions { EnableUserMemory = false }).CanBind(Opportunity()));
        Assert.False(Build(new Store(), new NoOpTelemetrySink(), bot: new BotOptions
            { EnableUserMemory = true, DisabledGuildIds = [4001] }).CanBind(Opportunity()));
    }

    private static WorldAutonomyRecallTool Build(Store store, IRecallTelemetrySink telemetry,
        MemoryRelevanceOptions? options = null, BotOptions? bot = null)
    {
        var monitor = new TestOptionsMonitor<MemoryRelevanceOptions>(options ?? new MemoryRelevanceOptions());
        return new WorldAutonomyRecallTool(store, new LexicalMemoryScorer(monitor), monitor,
            Options.Create(bot ?? new BotOptions { EnableUserMemory = true }), telemetry, NullLogger<WorldAutonomyRecallTool>.Instance);
    }

    private static UserMemory Memory(string text, MemoryKind kind = MemoryKind.Factual) =>
        new(text, "prior conversation", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1), 0, kind);
    private static WorldAutonomyOpportunity Opportunity() => new(4001, "discord_message", "hello", "5001",
        IsDirectAddress: true, SourceChannelId: 6001, SourceChannelName: "general", SourceAuthorId: 100);
    private static WorldAutonomyRunContext Context() => new("run-recall", 4001, "discord_message", "5001", null, null,
        "provider-neutral-model", "profile", "manifest", []);

    private sealed class Store : IUserMemoryStore
    {
        internal Dictionary<ulong, IReadOnlyList<UserMemory>> Notes { get; } = [];
        internal int Loads { get; private set; }
        internal ulong LastUser { get; private set; }
        public Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken cancellationToken = default)
        { Loads++; LastUser = userId; return Task.FromResult(Notes.GetValueOrDefault(userId) ?? (IReadOnlyList<UserMemory>)[]); }
        public Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetAllAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchMemoriesAsync(ulong userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAllMemoriesAsync(ulong userId, IReadOnlyList<UserMemory> memories, CancellationToken ct = default) => Task.CompletedTask;
    }
}