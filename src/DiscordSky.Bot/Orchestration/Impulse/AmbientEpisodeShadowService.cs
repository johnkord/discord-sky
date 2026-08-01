using System.Diagnostics;
using System.Threading.Channels;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Impulse;

public sealed record AmbientEpisodeShadowOpportunity(
    InteractionEpisode Episode,
    string Channel,
    string PersonaName,
    string? MoodLabel,
    WorthVerdict? BaselineVerdict,
    AmbientActionKind BaselineAction,
    double TextThreshold,
    bool VisualEnabled,
    double VisualThreshold,
    double VisualMinLead,
    bool PrioritySample = false);

public interface IAmbientEpisodeShadowSink
{
    bool ShouldCapture(bool priority = false);
    bool TryEnqueue(AmbientEpisodeShadowOpportunity opportunity);
}

public sealed class AmbientEpisodeShadowService : BackgroundService, IAmbientEpisodeShadowSink
{
    private readonly InteractionEpisodeOptions _options;
    private readonly Channel<AmbientEpisodeShadowOpportunity> _queue;
    private readonly Func<AmbientImpulseRequest, CancellationToken, Task<WorthVerdict?>> _evaluate;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<AmbientEpisodeShadowService> _logger;
    private readonly Func<double> _nextSample;

    public AmbientEpisodeShadowService(
        IOptions<InteractionEpisodeOptions> options,
        ImpulseJudge judge,
        IRecallTelemetrySink telemetry,
        ILogger<AmbientEpisodeShadowService> logger)
        : this(options.Value, judge.JudgeAmbientAsync, telemetry, logger, () => Random.Shared.NextDouble())
    {
    }

    internal AmbientEpisodeShadowService(
        InteractionEpisodeOptions options,
        Func<AmbientImpulseRequest, CancellationToken, Task<WorthVerdict?>> evaluate,
        IRecallTelemetrySink telemetry,
        ILogger<AmbientEpisodeShadowService> logger,
        Func<double> nextSample)
    {
        _options = options;
        _evaluate = evaluate;
        _telemetry = telemetry;
        _logger = logger;
        _nextSample = nextSample;
        _queue = Channel.CreateBounded<AmbientEpisodeShadowOpportunity>(new BoundedChannelOptions(
            Math.Clamp(options.ShadowQueueCapacity, 1, 1024))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public bool ShouldCapture(bool priority = false) =>
        _options.Mode == InteractionEpisodeMode.Shadow
        && (priority || _nextSample() < Math.Clamp(_options.ShadowSampleRate, 0.0, 1.0));

    public bool TryEnqueue(AmbientEpisodeShadowOpportunity opportunity)
    {
        if (_options.Mode != InteractionEpisodeMode.Shadow) return false;
        if (_queue.Writer.TryWrite(opportunity)) return true;

        Emit(opportunity, null, null, "dropped", "queue_full", null);
        _logger.LogWarning(
            "ambient_episode_shadow outcome=dropped channel={Channel} episode={EpisodeId}",
            opportunity.Channel,
            opportunity.Episode.EpisodeId);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Mode != InteractionEpisodeMode.Shadow)
        {
            _logger.LogInformation("Ambient interaction episode shadow disabled (mode={Mode}).", _options.Mode);
            return;
        }

        _logger.LogInformation(
            "Ambient interaction episode shadow enabled: sample={Sample:P0} queue={Queue}.",
            Math.Clamp(_options.ShadowSampleRate, 0.0, 1.0),
            Math.Clamp(_options.ShadowQueueCapacity, 1, 1024));
        try
        {
            await foreach (var opportunity in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await EvaluateAsync(opportunity, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    private async Task EvaluateAsync(
        AmbientEpisodeShadowOpportunity opportunity,
        CancellationToken cancellationToken)
    {
        var projection = EpisodeProjectionBuilder.BuildJudgeProjection(
            opportunity.Episode,
            opportunity.MoodLabel);
        var trigger = opportunity.Episode.Trigger;
        var trace = new InteractionTraceContext(
            EpisodeId: opportunity.Episode.EpisodeId,
            EpisodeSchemaVersion: opportunity.Episode.SchemaVersion,
            EvidenceDigest: opportunity.Episode.Fingerprint.EvidenceDigest,
            ProjectionDigest: projection.ProjectionDigest);
        var request = new AmbientImpulseRequest(
            opportunity.PersonaName,
            trigger.AuthorDisplayName,
            trigger.Content,
            Context: null,
            opportunity.MoodLabel,
            trigger.MediaContext,
            trigger.MessageId,
            projection.Text,
            opportunity.Episode.ReferentCandidates.Select(candidate => candidate.MessageId).ToArray(),
            trace,
            Workload: "ambient_episode_shadow");

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var verdict = await _evaluate(request, cancellationToken);
            var decision = verdict is null
                ? null
                : ImpulseJudge.ValidateReferentDecision(
                    verdict,
                    opportunity.Episode,
                    _options.ReferentConfidenceThreshold);
            var action = AmbientActionArbiter.Choose(
                useWorthGate: true,
                verdict,
                opportunity.TextThreshold,
                opportunity.VisualEnabled,
                opportunity.VisualThreshold,
                opportunity.VisualMinLead);
            Emit(
                opportunity,
                verdict,
                decision,
                verdict is null ? "no_verdict" : action.ToString().ToLowerInvariant(),
                decision?.ReasonCode,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Emit(
                opportunity,
                null,
                null,
                "failed",
                ex.GetType().Name,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            _logger.LogWarning(
                ex,
                "ambient_episode_shadow outcome=failed channel={Channel} episode={EpisodeId}",
                opportunity.Channel,
                opportunity.Episode.EpisodeId);
        }
    }

    private void Emit(
        AmbientEpisodeShadowOpportunity opportunity,
        WorthVerdict? verdict,
        ReferentDecision? decision,
        string outcome,
        string? reasonCode,
        long? latencyMs)
    {
        var episode = opportunity.Episode;
        var oldest = episode.Messages.Count == 0
            ? 0
            : (long)Math.Max(0, (episode.CapturedAt - episode.Messages.Min(message => message.Timestamp)).TotalMilliseconds);
        var projectionDigest = outcome == "dropped"
            ? null
            : EpisodeProjectionBuilder.BuildJudgeProjection(episode, opportunity.MoodLabel).ProjectionDigest;
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.AmbientEpisodeShadow,
            Channel: opportunity.Channel,
            Kind: decision?.Status.ToString().ToLowerInvariant(),
            Outcome: outcome,
            Count: episode.ReferentCandidates.Count,
            TopScore: verdict?.Worth,
            Note: verdict?.Thought,
            ReasonCode: reasonCode,
            LatencyMs: latencyMs,
            BaselineOutcome: opportunity.BaselineAction.ToString().ToLowerInvariant(),
            BaselineScore: opportunity.BaselineVerdict?.Worth,
            BaselineVisualWorth: opportunity.BaselineVerdict?.VisualWorth,
            BaselineVisualHook: opportunity.BaselineVerdict?.VisualHook,
            VisualWorth: verdict?.VisualWorth,
            VisualHook: verdict?.VisualHook,
            MessageId: episode.TriggerMessageId,
            EpisodeId: episode.EpisodeId,
            EpisodeSchemaVersion: episode.SchemaVersion,
            Stage: outcome == "dropped" ? "queue" : "shadow_terminal",
            ReferentMessageId: decision?.SelectedMessageId,
            ContextMessageCount: episode.Messages.Count,
            OldestContextAgeMs: oldest,
            EvidenceMask: episode.EvidenceMask.ToString(),
            EvidenceDigest: episode.Fingerprint.EvidenceDigest,
            ProjectionDigest: projectionDigest,
            PrioritySample: opportunity.PrioritySample));
    }
}