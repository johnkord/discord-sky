using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Memory.Logging;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public enum WorldAutonomyAudienceAction
{
    FullAutonomy,
    Reaction,
    Silence,
}

public sealed record WorldAutonomyAudienceRequest(
    string PersonaName,
    string AuthorDisplayName,
    string MessageText,
    string? SituationContext,
    string? MoodLabel,
    ulong MessageId,
    string ChannelName,
    ulong AuthorId,
    bool BotSpokeRecently,
    ulong GuildId = 0,
    ulong ChannelId = 0,
    bool HasMedia = false,
    string? MediaContext = null,
    string? EpisodeId = null,
    int EpisodeMessageCount = 1);

public sealed record WorldAutonomyAudienceDecision(
    WorldAutonomyAudienceAction Action,
    WorldAutonomyAudienceAction PredictedAction,
    WorldAutonomyAudienceVerdict? Verdict,
    string Reason,
    bool IsExplorationRun = false);

/// <summary>
/// Cheap host-side admission control for ambient world-autonomy opportunities. Direct audiences bypass this
/// component at the Discord ownership boundary; an admitted run retains the agent's complete tool authority.
/// </summary>
public sealed class WorldAutonomyAudienceGate
{
    private readonly WorldAutonomyConfiguration _configuration;
    private readonly WorldAutonomyAudienceJudge _judge;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly WorldAutonomyPostSpeechGuard _postSpeechGuard;
    private readonly WorldAutonomyProviderCircuit _providerCircuit;
    private readonly Func<double> _nextDouble;

    public WorldAutonomyAudienceGate(
        WorldAutonomyConfiguration configuration,
        WorldAutonomyAudienceJudge judge,
        IRecallTelemetrySink telemetry,
        WorldAutonomyPostSpeechGuard postSpeechGuard,
        WorldAutonomyProviderCircuit providerCircuit)
        : this(configuration, judge, telemetry, postSpeechGuard, providerCircuit, Random.Shared.NextDouble)
    {
    }

    internal WorldAutonomyAudienceGate(
        WorldAutonomyConfiguration configuration,
        WorldAutonomyAudienceJudge judge,
        IRecallTelemetrySink telemetry,
        WorldAutonomyPostSpeechGuard postSpeechGuard,
        WorldAutonomyProviderCircuit providerCircuit,
        Func<double> nextDouble)
    {
        _configuration = configuration;
        _judge = judge;
        _telemetry = telemetry;
        _postSpeechGuard = postSpeechGuard;
        _providerCircuit = providerCircuit;
        _nextDouble = nextDouble;
    }

    public async Task<WorldAutonomyAudienceDecision> EvaluateAsync(
        WorldAutonomyAudienceRequest request,
        CancellationToken cancellationToken)
    {
        if (_configuration.AmbientGateMode == WorldAutonomyAmbientGateMode.Off)
        {
            return new WorldAutonomyAudienceDecision(
                WorldAutonomyAudienceAction.FullAutonomy,
                WorldAutonomyAudienceAction.FullAutonomy,
                null,
                "gate_off");
        }

        if (_providerCircuit.Snapshot().IsOpen)
        {
            var delegated = new WorldAutonomyAudienceDecision(
                WorldAutonomyAudienceAction.FullAutonomy,
                WorldAutonomyAudienceAction.FullAutonomy,
                null,
                "provider_circuit_open");
            EmitTelemetry(request, delegated, 0);
            return delegated;
        }

        var cadence = _postSpeechGuard.ObserveAmbient(
            request.GuildId,
            request.ChannelId,
            request.MessageText,
            request.HasMedia);
        var verdict = await _judge.JudgeAsync(request, cancellationToken);
        var predicted = Decide(
            verdict,
            request.BotSpokeRecently,
            _configuration.AmbientFullThreshold,
            _configuration.AmbientReactionThreshold,
            _configuration.AmbientActionThreshold,
            _configuration.AmbientRecentSpeechPenalty,
            _configuration.AmbientJudgeConfidenceFloor);
        var cadenceHold = _configuration.AmbientPostSpeechHoldEnabled &&
            !cadence.Allowed && verdict is not null &&
            verdict.Confidence >= _configuration.AmbientJudgeConfidenceFloor &&
            verdict.ActionWorth < _configuration.AmbientActionThreshold;
        var lowValueHold = _configuration.AmbientLowValueHoldEnabled && verdict is not null &&
            verdict.Confidence >= _configuration.AmbientJudgeConfidenceFloor &&
            verdict.ConversationWorth < _configuration.AmbientLowValueFloor &&
            verdict.ReactionWorth < _configuration.AmbientLowValueFloor &&
            verdict.ActionWorth < _configuration.AmbientLowValueFloor;
        if (cadenceHold)
        {
            predicted = WorldAutonomyAudienceAction.Silence;
        }
        else if (lowValueHold)
        {
            predicted = WorldAutonomyAudienceAction.Silence;
        }

        var predictionReason = cadenceHold
            ? cadence.Reason
            : lowValueHold
            ? "all_axes_below_floor"
            : verdict is null
            ? "judge_unavailable_fail_open"
            : verdict.Confidence < _configuration.AmbientJudgeConfidenceFloor
            ? "judge_low_confidence_fail_open"
            : ActionName(predicted);
        var canaryHold = cadenceHold || lowValueHold;
        var explorationCandidate = _configuration.AmbientGateMode switch
        {
            WorldAutonomyAmbientGateMode.Canary => canaryHold,
            WorldAutonomyAmbientGateMode.Live => predicted == WorldAutonomyAudienceAction.Silence,
            _ => false,
        };
        var explorationPercent = _configuration.AmbientGateMode == WorldAutonomyAmbientGateMode.Canary
            ? _configuration.AmbientCanaryExplorationPercent
            : _configuration.AmbientLiveExplorationPercent;
        var exploration = explorationCandidate && _nextDouble() < explorationPercent / 100.0;
        var action = _configuration.AmbientGateMode switch
        {
            WorldAutonomyAmbientGateMode.Canary when canaryHold && !exploration => WorldAutonomyAudienceAction.Silence,
            WorldAutonomyAmbientGateMode.Live when !exploration => predicted,
            _ => WorldAutonomyAudienceAction.FullAutonomy,
        };
        var reason = exploration ? string.Concat("exploration_", predictionReason) : predictionReason;
        var decision = new WorldAutonomyAudienceDecision(action, predicted, verdict, reason, exploration);
        EmitTelemetry(request, decision, cadence.HumanTurns);
        return decision;
    }

    private void EmitTelemetry(
        WorldAutonomyAudienceRequest request,
        WorldAutonomyAudienceDecision decision,
        int humanTurns)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.WorldAutonomyAudience,
            UserHash: UserIdHash.Hash(request.AuthorId),
            Channel: request.ChannelName,
            Outcome: ActionName(decision.Action),
            Count: humanTurns,
            TopScore: decision.Verdict?.ConversationWorth,
            MessageId: request.MessageId,
            Note: decision.Verdict?.ConversationHook,
            Reason: decision.Reason,
            BaselineOutcome: ActionName(decision.PredictedAction),
            GateMode: _configuration.AmbientGateMode.ToString().ToLowerInvariant(),
            EpisodeId: request.EpisodeId,
            ContextMessageCount: request.EpisodeMessageCount,
            ConversationWorth: decision.Verdict?.ConversationWorth,
            ConversationHook: decision.Verdict?.ConversationHook,
            ReactionWorth: decision.Verdict?.ReactionWorth,
            ActionWorth: decision.Verdict?.ActionWorth,
            ActionHook: decision.Verdict?.ActionHook,
            Confidence: decision.Verdict?.Confidence,
            IsExplorationRun: decision.IsExplorationRun));
    }

    private static string ActionName(WorldAutonomyAudienceAction action) => action switch
    {
        WorldAutonomyAudienceAction.FullAutonomy => "full_autonomy",
        WorldAutonomyAudienceAction.Reaction => "reaction",
        _ => "silence",
    };

    internal static WorldAutonomyAudienceAction Decide(
        WorldAutonomyAudienceVerdict? verdict,
        bool botSpokeRecently,
        double fullThreshold,
        double reactionThreshold,
        double actionThreshold,
        double recentSpeechPenalty,
        double confidenceFloor)
    {
        if (verdict is null || verdict.Confidence < confidenceFloor)
        {
            return WorldAutonomyAudienceAction.FullAutonomy;
        }

        if (verdict.ActionWorth >= actionThreshold)
        {
            return WorldAutonomyAudienceAction.FullAutonomy;
        }

        var effectiveFullThreshold = botSpokeRecently
            ? Math.Min(1.0, fullThreshold + recentSpeechPenalty)
            : fullThreshold;
        if (verdict.ConversationWorth >= effectiveFullThreshold)
        {
            return WorldAutonomyAudienceAction.FullAutonomy;
        }

        return verdict.ReactionWorth >= reactionThreshold
            ? WorldAutonomyAudienceAction.Reaction
            : WorldAutonomyAudienceAction.Silence;
    }
}

public sealed record WorldAutonomyPostSpeechDecision(
    bool Allowed,
    int HumanTurns,
    bool HasNewMaterial,
    string Reason);

public sealed partial class WorldAutonomyPostSpeechGuard
{
    private static readonly HashSet<string> Acknowledgments = new(StringComparer.OrdinalIgnoreCase)
    {
        "agree", "agreed", "based", "bro", "damn", "exactly", "fr", "good", "great", "haha",
        "hahaha", "lol", "lmao", "nice", "no", "nope", "ok", "okay", "real", "rofl", "thanks",
        "thank", "true", "ty", "wow", "yeah", "yep", "yes",
    };

    private readonly WorldAutonomyConfiguration _configuration;
    private readonly ConcurrentDictionary<(ulong GuildId, ulong ChannelId), ChannelState> _channels = new();
    private readonly TimeProvider _timeProvider;

    public WorldAutonomyPostSpeechGuard(
        WorldAutonomyConfiguration configuration,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void RecordSpeech(ulong guildId, ulong channelId)
    {
        if (!_configuration.AmbientPostSpeechGuardEnabled) return;

        var state = _channels.GetOrAdd((guildId, channelId), _ => new ChannelState());
        lock (state.Gate)
        {
            state.Active = true;
            state.HumanTurns = 0;
            state.SpokeAt = _timeProvider.GetUtcNow();
        }
    }

    public WorldAutonomyPostSpeechDecision ObserveAmbient(
        ulong guildId,
        ulong channelId,
        string content,
        bool hasMedia)
    {
        if (!_configuration.AmbientPostSpeechGuardEnabled
            || !_channels.TryGetValue((guildId, channelId), out var state))
        {
            return new WorldAutonomyPostSpeechDecision(true, 0, false, "post_speech_guard_inactive");
        }

        lock (state.Gate)
        {
            if (!state.Active)
            {
                return new WorldAutonomyPostSpeechDecision(true, 0, false, "post_speech_guard_inactive");
            }
            if (_timeProvider.GetUtcNow() - state.SpokeAt >= _configuration.AmbientPostSpeechWindow)
            {
                state.Active = false;
                return new WorldAutonomyPostSpeechDecision(true, 0, false, "post_speech_guard_expired");
            }

            state.HumanTurns++;
            var hasNewMaterial = HasNewMaterial(content, hasMedia);
            var allowed = hasNewMaterial || state.HumanTurns >= _configuration.AmbientPostSpeechHumanTurns;
            if (allowed) state.Active = false;
            return new WorldAutonomyPostSpeechDecision(
                allowed,
                state.HumanTurns,
                hasNewMaterial,
                allowed ? (hasNewMaterial ? "new_material" : "human_turn_requirement_met") : "post_speech_waiting");
        }
    }

    internal static bool HasNewMaterial(string content, bool hasMedia)
    {
        if (hasMedia || content.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || content.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var meaningful = WordPattern().Matches(content)
            .Select(match => match.Value)
            .Where(word => word.Length > 1 && !Acknowledgments.Contains(word))
            .Take(2)
            .Count();
        return meaningful >= 2 || (meaningful == 1 && content.Contains('?'));
    }

    [GeneratedRegex("[A-Za-z0-9']+")]
    private static partial Regex WordPattern();

    private sealed class ChannelState
    {
        public object Gate { get; } = new();

        public bool Active { get; set; }

        public int HumanTurns { get; set; }

        public DateTimeOffset SpokeAt { get; set; }
    }
}