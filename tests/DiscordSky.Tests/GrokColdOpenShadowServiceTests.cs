using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Impulse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class GrokColdOpenShadowServiceTests
{
    [Fact]
    public void Disabled_DoesNotEnqueue()
    {
        var service = BuildService(
            new GrokColdOpenShadowOptions { Enabled = false },
            (_, _) => Task.FromResult<ColdOpenDraft?>(null));

        Assert.False(service.TryEnqueue(Opportunity()));
    }

    [Fact]
    public void SamplingMiss_DoesNotEnqueue()
    {
        var service = BuildService(
            new GrokColdOpenShadowOptions { Enabled = true, SampleRate = 0.5 },
            (_, _) => Task.FromResult<ColdOpenDraft?>(null),
            nextSample: () => 0.75);

        Assert.False(service.TryEnqueue(Opportunity()));
    }

    [Fact]
    public void FullQueue_DropsWithoutBlocking_AndEmitsTelemetry()
    {
        var telemetry = new InMemoryTelemetrySink();
        var service = BuildService(
            new GrokColdOpenShadowOptions { Enabled = true, SampleRate = 1.0, QueueCapacity = 1 },
            (_, _) => Task.FromResult<ColdOpenDraft?>(null),
            telemetry);

        Assert.True(service.TryEnqueue(Opportunity("first")));
        Assert.False(service.TryEnqueue(Opportunity("second")));

        var dropped = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.ColdOpenProviderShadow, dropped.EventType);
        Assert.Equal("dropped", dropped.Outcome);
        Assert.Equal("second", dropped.Channel);
        Assert.Equal("xAI", dropped.Provider);
        Assert.Equal("grok-4.5", dropped.Model);
    }

    [Fact]
    public async Task Worker_EmitsCandidateAndBaselineTelemetry()
    {
        var telemetry = new SignalingTelemetrySink();
        var service = BuildService(
            new GrokColdOpenShadowOptions
            {
                Enabled = true,
                Model = "grok-4.5",
                ReasoningEffort = "medium",
                SampleRate = 1.0,
            },
            (_, _) => Task.FromResult<ColdOpenDraft?>(new(0.91, "You call that a backlog? I call it surrender.", "backlog")),
            telemetry);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(service.TryEnqueue(Opportunity(
                championDraft: new ColdOpenDraft(0.42, "baseline", "backlog"))));

            var evt = await telemetry.Next.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("would_post", evt.Outcome);
            Assert.Equal(0.91, evt.TopScore);
            Assert.Equal("xAI", evt.Provider);
            Assert.Equal("grok-4.5", evt.Model);
            Assert.Equal("medium", evt.ReasoningEffort);
            Assert.Equal("below_threshold", evt.BaselineOutcome);
            Assert.Equal(0.42, evt.BaselineScore);
            Assert.Equal("eval-test", evt.EvaluationId);
            Assert.NotNull(evt.OpportunityAt);
            Assert.NotNull(evt.LatencyMs);
            Assert.Contains("backlog", evt.Note);
            Assert.Equal(new[] { "mio: my backlog is judging me" }, evt.Room);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Enqueue_SnapshotsMutableContext()
    {
        var people = new List<string> { "mio" };
        var lines = new List<string> { "mio: first" };
        ColdOpenContext? captured = null;
        var telemetry = new SignalingTelemetrySink();
        var service = BuildService(
            new GrokColdOpenShadowOptions { Enabled = true, SampleRate = 1.0 },
            (context, _) =>
            {
                captured = context;
                return Task.FromResult<ColdOpenDraft?>(null);
            },
            telemetry);
        var opportunity = new ColdOpenShadowOpportunity(
            "eval-test",
            DateTimeOffset.UtcNow,
            "chat",
            new ColdOpenContext("Robotnik", "scheming", "state", people, lines),
            null,
            0.6,
            lines);

        Assert.True(service.TryEnqueue(opportunity));
        people.Add("late-arrival");
        lines.Add("late mutation");

        await service.StartAsync(CancellationToken.None);
        try
        {
            await telemetry.Next.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(captured);
            Assert.Equal(new[] { "mio" }, captured.RecentPeople);
            Assert.Equal(new[] { "mio: first" }, captured.RecentLines);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(null, 0.6, "declined")]
    [InlineData(0.59, 0.6, "below_threshold")]
    [InlineData(0.60, 0.6, "would_post")]
    public void ChampionOutcome_UsesProductionThreshold(double? worth, double threshold, string expected)
    {
        var draft = worth is null ? null : new ColdOpenDraft(worth.Value, "line", "hook");
        Assert.Equal(expected, GrokColdOpenShadowService.ChampionOutcome(draft, threshold));
    }

    [Fact]
    public async Task FixedComposerProfile_SendsGrokModelAndEffort()
    {
        var chatClient = new CapturingChatClient();
        var composer = new ColdOpenComposer(
            chatClient,
            new LlmWorkloadProfile("grok-4.5", "medium"),
            NullLogger<ColdOpenComposer>.Instance);

        var draft = await composer.ComposeAsync(
            new ColdOpenContext("Robotnik", null, string.Empty, Array.Empty<string>(), new[] { "mio: hello" }),
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("grok-4.5", chatClient.Options?.ModelId);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.Medium, chatClient.Options?.Reasoning?.Effort);
    }

    [Fact]
    public async Task FixedComposerProfile_CanSurfaceProviderFailureForEvaluation()
    {
        var composer = new ColdOpenComposer(
            new ThrowingChatClient(),
            new LlmWorkloadProfile("grok-4.5", "medium"),
            NullLogger<ColdOpenComposer>.Instance,
            surfaceFailures: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.ComposeAsync(
            new ColdOpenContext("Robotnik", null, string.Empty, Array.Empty<string>(), new[] { "mio: hello" }),
            CancellationToken.None));
    }

    private static GrokColdOpenShadowService BuildService(
        GrokColdOpenShadowOptions options,
        Func<ColdOpenContext, CancellationToken, Task<ColdOpenDraft?>> compose,
        IRecallTelemetrySink? telemetry = null,
        Func<double>? nextSample = null) => new(
            options,
            compose,
            telemetry ?? new InMemoryTelemetrySink(),
            NullLogger<GrokColdOpenShadowService>.Instance,
            nextSample ?? (() => 0.0));

    private static ColdOpenShadowOpportunity Opportunity(
        string channel = "chat",
        ColdOpenDraft? championDraft = null) => new(
            "eval-test",
            DateTimeOffset.UtcNow,
            channel,
            new ColdOpenContext(
                "Robotnik",
                "scheming",
                "state",
                new[] { "mio" },
                new[] { "mio: my backlog is judging me" }),
            championDraft,
            0.6,
            new[] { "mio: my backlog is judging me" });

    private sealed class SignalingTelemetrySink : IRecallTelemetrySink
    {
        public TaskCompletionSource<TelemetryEvent> Next { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(TelemetryEvent evt) => Next.TrySetResult(evt);
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                "{\"worth\":0.8,\"hook\":\"hello\",\"line\":\"Silence, peasant.\"}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provider failed");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}