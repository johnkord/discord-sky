using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration.Impulse;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class AmbientEpisodeShadowServiceTests
{
    [Fact]
    public void OffModeAndSamplingMiss_DoNotCapture()
    {
        var off = Build(new InteractionEpisodeOptions { Mode = InteractionEpisodeMode.Off });
        var miss = Build(
            new InteractionEpisodeOptions
            {
                Mode = InteractionEpisodeMode.Shadow,
                ShadowSampleRate = 0.5,
            },
            nextSample: () => 0.75);

        Assert.False(off.ShouldCapture());
        Assert.False(miss.ShouldCapture());
    }

    [Fact]
    public void PriorityCapture_BypassesSamplingButNotOffMode()
    {
        var shadow = Build(
            new InteractionEpisodeOptions
            {
                Mode = InteractionEpisodeMode.Shadow,
                ShadowSampleRate = 0.0,
            },
            nextSample: () => 1.0);
        var off = Build(
            new InteractionEpisodeOptions { Mode = InteractionEpisodeMode.Off },
            nextSample: () => 0.0);

        Assert.True(shadow.ShouldCapture(priority: true));
        Assert.False(shadow.ShouldCapture(priority: false));
        Assert.False(off.ShouldCapture(priority: true));
    }

    [Fact]
    public void FullQueue_DropsWithoutBlockingAndEmitsMetadataOnlyTelemetry()
    {
        var telemetry = new InMemoryTelemetrySink();
        var service = Build(
            new InteractionEpisodeOptions
            {
                Mode = InteractionEpisodeMode.Shadow,
                ShadowSampleRate = 1.0,
                ShadowQueueCapacity = 1,
            },
            telemetry);

        Assert.True(service.TryEnqueue(Opportunity("episode-1")));
        Assert.False(service.TryEnqueue(Opportunity("episode-2")));

        var dropped = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.AmbientEpisodeShadow, dropped.EventType);
        Assert.Equal("dropped", dropped.Outcome);
        Assert.Equal("episode-2", dropped.EpisodeId);
        Assert.Null(dropped.Room);
        Assert.Null(dropped.Note);
    }

    [Fact]
    public async Task Worker_UsesCanonicalProjectionAndEmitsValidatedComparison()
    {
        AmbientImpulseRequest? captured = null;
        var telemetry = new SignalingTelemetrySink();
        var service = Build(
            new InteractionEpisodeOptions
            {
                Mode = InteractionEpisodeMode.Shadow,
                ShadowSampleRate = 1.0,
                ReferentConfidenceThreshold = 0.7,
            },
            telemetry,
            (request, _) =>
            {
                captured = request;
                return Task.FromResult<WorthVerdict?>(new WorthVerdict(
                    0.9,
                    "meteor line",
                    VisualWorth: 0.85,
                    VisualHook: "meteor tribunal",
                    ReferentMessageId: 1,
                    ReferentConfidence: 0.95,
                    ReferentStatus: ReferentResolutionStatus.Resolved));
            });

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(service.TryEnqueue(Opportunity("episode-1")));

            var evt = await telemetry.Next.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(captured);
            Assert.Equal("ambient_episode_shadow", captured!.Workload);
            Assert.Contains("CANONICAL AMBIENT EPISODE", captured.EpisodeProjection);
            Assert.Contains("meteor incoming", captured.EpisodeProjection);
            Assert.Equal("episode-1", captured.Trace?.EpisodeId);
            Assert.Equal("text", evt.Outcome);
            Assert.Equal("silence", evt.BaselineOutcome);
            Assert.Equal(1UL, evt.ReferentMessageId);
            Assert.Equal("validated_model_selection", evt.ReasonCode);
            Assert.Equal(0.85, evt.VisualWorth);
            Assert.Equal("meteor tribunal", evt.VisualHook);
            Assert.Equal(0.2, evt.BaselineScore);
            Assert.Equal(0.0, evt.BaselineVisualWorth);
            Assert.True(evt.PrioritySample);
            Assert.NotNull(evt.EvidenceDigest);
            Assert.NotNull(evt.ProjectionDigest);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WorkerCannotMutateCapturedEpisode()
    {
        var source = new List<EpisodeMessage>
        {
            new(1, 10, "Alice", "meteor incoming", Timestamp.AddSeconds(-5)),
            new(2, 20, "Bob", "what is that?", Timestamp),
        };
        var episode = CreateEpisode("episode-1", source);
        var telemetry = new SignalingTelemetrySink();
        var service = Build(
            new InteractionEpisodeOptions { Mode = InteractionEpisodeMode.Shadow, ShadowSampleRate = 1.0 },
            telemetry);
        source.Clear();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(service.TryEnqueue(Opportunity(episode)));
            await telemetry.Next.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, episode.Messages.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private static AmbientEpisodeShadowService Build(
        InteractionEpisodeOptions options,
        IRecallTelemetrySink? telemetry = null,
        Func<AmbientImpulseRequest, CancellationToken, Task<WorthVerdict?>>? evaluate = null,
        Func<double>? nextSample = null) => new(
            options,
            evaluate ?? ((_, _) => Task.FromResult<WorthVerdict?>(new WorthVerdict(0.1, "quiet"))),
            telemetry ?? new InMemoryTelemetrySink(),
            NullLogger<AmbientEpisodeShadowService>.Instance,
            nextSample ?? (() => 0.0));

    private static AmbientEpisodeShadowOpportunity Opportunity(string episodeId) =>
        Opportunity(CreateEpisode(episodeId, new[]
        {
            new EpisodeMessage(1, 10, "Alice", "meteor incoming", Timestamp.AddSeconds(-5)),
            new EpisodeMessage(2, 20, "Bob", "what is that?", Timestamp),
        }));

    private static AmbientEpisodeShadowOpportunity Opportunity(InteractionEpisode episode) => new(
        episode,
        "chat",
        "Robotnik",
        "scheming",
        new WorthVerdict(0.2, "legacy quiet"),
        AmbientActionKind.Silence,
        TextThreshold: 0.6,
        VisualEnabled: false,
        VisualThreshold: 0.8,
        VisualMinLead: 0.05,
        PrioritySample: true);

    private static InteractionEpisode CreateEpisode(
        string episodeId,
        IEnumerable<EpisodeMessage> messages) => InteractionEpisode.Create(
            episodeId,
            Timestamp,
            99,
            2,
            messages,
            null,
            new ReferentRequirement(true, "deictic_question"),
            new[] { new ReferentCandidate(1, 0.75, "recent_message") });

    private sealed class SignalingTelemetrySink : IRecallTelemetrySink
    {
        public TaskCompletionSource<TelemetryEvent> Next { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(TelemetryEvent evt) => Next.TrySetResult(evt);
    }
}