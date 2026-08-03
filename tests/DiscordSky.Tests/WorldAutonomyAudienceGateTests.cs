using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyAudienceGateTests
{
    [Theory]
    [InlineData(0.80, 0.10, 0.10, false, WorldAutonomyAudienceAction.FullAutonomy)]
    [InlineData(0.70, 0.50, 0.10, true, WorldAutonomyAudienceAction.Reaction)]
    [InlineData(0.20, 0.10, 0.80, true, WorldAutonomyAudienceAction.FullAutonomy)]
    [InlineData(0.20, 0.50, 0.10, false, WorldAutonomyAudienceAction.Reaction)]
    [InlineData(0.20, 0.10, 0.10, false, WorldAutonomyAudienceAction.Silence)]
    public void Decide_AllocatesAttentionByIndependentWorthAxes(
        double conversationWorth,
        double reactionWorth,
        double actionWorth,
        bool botSpokeRecently,
        WorldAutonomyAudienceAction expected)
    {
        var actual = WorldAutonomyAudienceGate.Decide(
            new WorldAutonomyAudienceVerdict(
                conversationWorth,
                "conversation",
                reactionWorth,
                actionWorth,
                "action",
                Confidence: 0.9),
            botSpokeRecently,
            fullThreshold: 0.65,
            reactionThreshold: 0.35,
            actionThreshold: 0.60,
            recentSpeechPenalty: 0.15,
            confidenceFloor: 0.35);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Decide_FailsOpenWhenJudgeHasNoVerdict()
    {
        var actual = WorldAutonomyAudienceGate.Decide(
            null,
            botSpokeRecently: true,
            fullThreshold: 0.65,
            reactionThreshold: 0.35,
            actionThreshold: 0.60,
            recentSpeechPenalty: 0.15,
            confidenceFloor: 0.35);

        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, actual);
    }

    [Fact]
    public void Decide_FailsOpenWhenJudgeConfidenceIsLow()
    {
        var actual = WorldAutonomyAudienceGate.Decide(
            new WorldAutonomyAudienceVerdict(0.0, string.Empty, 0.0, 0.0, string.Empty, Confidence: 0.2),
            botSpokeRecently: true,
            fullThreshold: 0.65,
            reactionThreshold: 0.35,
            actionThreshold: 0.60,
            recentSpeechPenalty: 0.15,
            confidenceFloor: 0.35);

        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, actual);
    }

    [Fact]
    public void Configuration_RejectsReactionThresholdAboveFullThreshold()
    {
        var options = new WorldAutonomyOptions
        {
            AmbientFullThreshold = 0.5,
            AmbientReactionThreshold = 0.6,
        };

        Assert.Throws<InvalidOperationException>(() => WorldAutonomyConfiguration.FromOptions(options));
    }

    [Fact]
    public async Task Evaluate_ShadowRecordsSilencePredictionButAdmitsFullAutonomy()
    {
        var configuration = Configuration(WorldAutonomyAmbientGateMode.Shadow);
        var client = new StubChatClient(
            "{\"conversation_worth\":0.1,\"conversation_hook\":\"\",\"reaction_worth\":0.1," +
            "\"action_worth\":0.1,\"action_hook\":\"\",\"confidence\":0.9}");
        var telemetry = new RecordingTelemetrySink();
        var gate = Gate(configuration, client, telemetry, new WorldAutonomyProviderCircuit(
            NullLogger<WorldAutonomyProviderCircuit>.Instance));

        var decision = await gate.EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, decision.Action);
        Assert.Equal(WorldAutonomyAudienceAction.Silence, decision.PredictedAction);
        Assert.Equal(1, client.CallCount);
        Assert.Equal("gpt-5.4-mini", client.Options!.ModelId);
        var metric = Assert.Single(telemetry.Events);
        Assert.Equal("full_autonomy", metric.Outcome);
        Assert.Equal("silence", metric.BaselineOutcome);
        Assert.Equal("shadow", metric.GateMode);
    }

    [Fact]
    public async Task Evaluate_OpenProviderCircuitSkipsUtilityCallAndDelegatesToRouter()
    {
        var configuration = Configuration(WorldAutonomyAmbientGateMode.Live);
        var client = new StubChatClient("{}");
        var telemetry = new RecordingTelemetrySink();
        var circuit = new WorldAutonomyProviderCircuit(
            NullLogger<WorldAutonomyProviderCircuit>.Instance);
        circuit.RecordFailure(new InvalidOperationException("credit_balance_exhausted"));
        var gate = Gate(configuration, client, telemetry, circuit);

        var decision = await gate.EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, decision.Action);
        Assert.Equal("provider_circuit_open", decision.Reason);
        Assert.Equal(0, client.CallCount);
        var metric = Assert.Single(telemetry.Events);
        Assert.Equal("full_autonomy", metric.Outcome);
        Assert.Equal("provider_circuit_open", metric.Reason);
    }

    [Fact]
    public async Task Evaluate_CanaryKeepsReactionPredictionAsFullAutonomy()
    {
        var configuration = Configuration(WorldAutonomyAmbientGateMode.Canary);
        var client = new StubChatClient(VerdictJson(0.1, 0.8, 0.1));
        var gate = Gate(
            configuration,
            client,
            new RecordingTelemetrySink(),
            new WorldAutonomyProviderCircuit(NullLogger<WorldAutonomyProviderCircuit>.Instance));

        var decision = await gate.EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, decision.Action);
        Assert.Equal(WorldAutonomyAudienceAction.Reaction, decision.PredictedAction);
        Assert.False(decision.IsExplorationRun);
    }

    [Fact]
    public async Task Evaluate_CanaryEnforcesEnabledLowValueHold()
    {
        var configuration = Configuration(
            WorldAutonomyAmbientGateMode.Canary,
            lowValueHoldEnabled: true,
            canaryExplorationPercent: 0);
        var client = new StubChatClient(VerdictJson(0.05, 0.05, 0.05));
        var gate = Gate(
            configuration,
            client,
            new RecordingTelemetrySink(),
            new WorldAutonomyProviderCircuit(NullLogger<WorldAutonomyProviderCircuit>.Instance));

        var decision = await gate.EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(WorldAutonomyAudienceAction.Silence, decision.Action);
        Assert.Equal("all_axes_below_floor", decision.Reason);
    }

    [Fact]
    public async Task Evaluate_CanaryExplorationRunsWouldHoldEpisode()
    {
        var configuration = Configuration(
            WorldAutonomyAmbientGateMode.Canary,
            lowValueHoldEnabled: true,
            canaryExplorationPercent: 100);
        var client = new StubChatClient(VerdictJson(0.05, 0.05, 0.05));
        var telemetry = new RecordingTelemetrySink();
        var gate = Gate(
            configuration,
            client,
            telemetry,
            new WorldAutonomyProviderCircuit(NullLogger<WorldAutonomyProviderCircuit>.Instance),
            nextDouble: () => 0.5);

        var decision = await gate.EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, decision.Action);
        Assert.Equal(WorldAutonomyAudienceAction.Silence, decision.PredictedAction);
        Assert.True(decision.IsExplorationRun);
        Assert.True(Assert.Single(telemetry.Events).IsExplorationRun);
    }

    [Fact]
    public async Task Evaluate_HighActionWorthEscapesPostSpeechHold()
    {
        var configuration = Configuration(
            WorldAutonomyAmbientGateMode.Canary,
            postSpeechHoldEnabled: true,
            canaryExplorationPercent: 0);
        var guard = new WorldAutonomyPostSpeechGuard(configuration);
        guard.RecordSpeech(4001, 6001);
        var client = new StubChatClient(VerdictJson(0.05, 0.05, 0.9));
        var gate = Gate(
            configuration,
            client,
            new RecordingTelemetrySink(),
            new WorldAutonomyProviderCircuit(NullLogger<WorldAutonomyProviderCircuit>.Instance),
            guard);

        var decision = await gate.EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, decision.Action);
        Assert.Equal(WorldAutonomyAudienceAction.FullAutonomy, decision.PredictedAction);
    }

    [Fact]
    public void PostSpeechGuard_HoldsOneAcknowledgmentThenAllowsTheSecondHumanTurn()
    {
        var guard = Guard();
        guard.RecordSpeech(1, 2);

        var first = guard.ObserveAmbient(1, 2, "lol", hasMedia: false);
        var second = guard.ObserveAmbient(1, 2, "yeah", hasMedia: false);

        Assert.False(first.Allowed);
        Assert.Equal("post_speech_waiting", first.Reason);
        Assert.True(second.Allowed);
        Assert.Equal("human_turn_requirement_met", second.Reason);
    }

    [Fact]
    public void PostSpeechGuard_AllowsSubstantiveMaterialOnTheFirstHumanTurn()
    {
        var guard = Guard();
        guard.RecordSpeech(1, 2);

        var decision = guard.ObserveAmbient(1, 2, "What happens to the new department?", hasMedia: false);

        Assert.True(decision.Allowed);
        Assert.True(decision.HasNewMaterial);
        Assert.Equal("new_material", decision.Reason);
    }

    [Fact]
    public void PostSpeechGuard_AllowsShortMessageAfterEpisodeExpires()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            AmbientPostSpeechGuardEnabled = true,
            AmbientPostSpeechHumanTurns = 2,
            AmbientPostSpeechWindowMinutes = 10,
        });
        var guard = new WorldAutonomyPostSpeechGuard(configuration, clock);
        guard.RecordSpeech(1, 2);
        clock.Advance(TimeSpan.FromMinutes(10));

        var decision = guard.ObserveAmbient(1, 2, "lol", hasMedia: false);

        Assert.True(decision.Allowed);
        Assert.Equal("post_speech_guard_expired", decision.Reason);
    }

    [Theory]
    [InlineData("lmao", false)]
    [InlineData("wow yeah", false)]
    [InlineData("why?", true)]
    [InlineData("new department", true)]
    [InlineData("https://example.com", true)]
    public void HasNewMaterial_DistinguishesApplauseFromContent(string content, bool expected)
    {
        Assert.Equal(expected, WorldAutonomyPostSpeechGuard.HasNewMaterial(content, hasMedia: false));
    }

    private static WorldAutonomyPostSpeechGuard Guard() => new(
        WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            AmbientPostSpeechGuardEnabled = true,
            AmbientPostSpeechHumanTurns = 2,
        }));

    private static WorldAutonomyConfiguration Configuration(
        WorldAutonomyAmbientGateMode mode,
        bool postSpeechHoldEnabled = false,
        bool lowValueHoldEnabled = false,
        int canaryExplorationPercent = 10) =>
        WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            AmbientGateMode = mode,
            AmbientFullThreshold = 0.65,
            AmbientReactionThreshold = 0.35,
            AmbientRecentSpeechPenalty = 0.15,
            AmbientPostSpeechGuardEnabled = true,
            AmbientPostSpeechHoldEnabled = postSpeechHoldEnabled,
            AmbientLowValueHoldEnabled = lowValueHoldEnabled,
            AmbientCanaryExplorationPercent = canaryExplorationPercent,
        });

    private static WorldAutonomyAudienceGate Gate(
        WorldAutonomyConfiguration configuration,
        IChatClient client,
        IRecallTelemetrySink telemetry,
        WorldAutonomyProviderCircuit circuit,
        WorldAutonomyPostSpeechGuard? guard = null,
        Func<double>? nextDouble = null)
    {
        var judge = new WorldAutonomyAudienceJudge(
                client,
                new TestOptionsMonitor<LlmOptions>(new LlmOptions
                {
                    ActiveProvider = "OpenAI",
                    Providers = new Dictionary<string, LlmProviderOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["OpenAI"] = new()
                        {
                            ChatModel = "gpt-5.6-sol",
                            UtilityModel = "gpt-5.4-mini",
                            UtilityReasoningEffort = "none",
                        }
                    }
                }),
                NullLogger<WorldAutonomyAudienceJudge>.Instance);
        return nextDouble is null
            ? new WorldAutonomyAudienceGate(
                configuration,
                judge,
                telemetry,
                guard ?? new WorldAutonomyPostSpeechGuard(configuration),
                circuit)
            : new WorldAutonomyAudienceGate(
                configuration,
                judge,
                telemetry,
                guard ?? new WorldAutonomyPostSpeechGuard(configuration),
                circuit,
                nextDouble);
    }

    private static string VerdictJson(double conversation, double reaction, double action) =>
        $$"""
        {"conversation_worth":{{conversation}},"conversation_hook":"conversation","reaction_worth":{{reaction}},
        "action_worth":{{action}},"action_hook":"action","confidence":0.9}
        """;

    private static WorldAutonomyAudienceRequest Request() => new(
        "Robotnik from AOSTH",
        "member",
        "lol",
        "Robotnik spoke in the last two minutes: no.",
        "gloating",
        5001,
        "general",
        8001,
        BotSpokeRecently: false,
        GuildId: 4001,
        ChannelId: 6001);

    private sealed class StubChatClient(string responseText) : IChatClient
    {
        internal int CallCount { get; private set; }

        internal ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingTelemetrySink : IRecallTelemetrySink
    {
        internal List<TelemetryEvent> Events { get; } = [];

        public void Emit(TelemetryEvent evt) => Events.Add(evt);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}