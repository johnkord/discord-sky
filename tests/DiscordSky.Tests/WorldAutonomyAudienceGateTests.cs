using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Orchestration.Autonomy;
using DiscordSky.Bot.Orchestration.Impulse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyAudienceGateTests
{
    [Theory]
    [InlineData(0.80, false, WorldAutonomyAudienceAction.FullAutonomy)]
    [InlineData(0.70, true, WorldAutonomyAudienceAction.Reaction)]
    [InlineData(0.50, false, WorldAutonomyAudienceAction.Reaction)]
    [InlineData(0.20, false, WorldAutonomyAudienceAction.Silence)]
    public void Decide_AllocatesAttentionByWorthAndRecentSpeech(
        double worth,
        bool botSpokeRecently,
        WorldAutonomyAudienceAction expected)
    {
        var actual = WorldAutonomyAudienceGate.Decide(
            new WorthVerdict(worth, "test"),
            botSpokeRecently,
            fullThreshold: 0.65,
            reactionThreshold: 0.35,
            recentSpeechPenalty: 0.15);

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
            recentSpeechPenalty: 0.15);

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
        var client = new StubChatClient("{\"worth\":0.1,\"thought\":\"nothing worth seizing\"}");
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
        var client = new StubChatClient("{\"worth\":0.1}");
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

    private static WorldAutonomyConfiguration Configuration(WorldAutonomyAmbientGateMode mode) =>
        WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            AmbientGateMode = mode,
            AmbientFullThreshold = 0.65,
            AmbientReactionThreshold = 0.35,
            AmbientRecentSpeechPenalty = 0.15,
            AmbientPostSpeechGuardEnabled = true,
        });

    private static WorldAutonomyAudienceGate Gate(
        WorldAutonomyConfiguration configuration,
        IChatClient client,
        IRecallTelemetrySink telemetry,
        WorldAutonomyProviderCircuit circuit) => new(
            configuration,
            new ImpulseJudge(
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
                NullLogger<ImpulseJudge>.Instance),
            telemetry,
            new WorldAutonomyPostSpeechGuard(configuration),
            circuit);

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