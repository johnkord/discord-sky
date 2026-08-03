using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyAmbientAdmissionCoordinatorTests
{
    [Fact]
    public async Task Offer_CoalescesRapidFragmentsBeforeOneEvaluation()
    {
        var coordinator = Coordinator(windowMilliseconds: 40);
        var evaluations = new List<(ulong MessageId, WorldAutonomyAmbientEpisode Episode)>();

        var first = coordinator.OfferAsync(
            Request(1001, (episode, _) => RecordAsync(1001, episode)),
            CancellationToken.None);
        var second = coordinator.OfferAsync(
            Request(1002, (episode, _) => RecordAsync(1002, episode)),
            CancellationToken.None);
        await Task.WhenAll(first, second);

        var evaluation = Assert.Single(evaluations);
        Assert.Equal(1002UL, evaluation.MessageId);
        Assert.Equal(2, evaluation.Episode.MessageCount);
        Assert.Equal(1002UL, evaluation.Episode.TriggerMessageId);

        Task RecordAsync(ulong messageId, WorldAutonomyAmbientEpisode episode)
        {
            lock (evaluations)
            {
                evaluations.Add((messageId, episode));
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Cancel_DirectPetitionCancelsPendingAmbientEvaluation()
    {
        var coordinator = Coordinator(windowMilliseconds: 5000);
        var evaluated = false;
        var pending = coordinator.OfferAsync(
            Request(1001, (_, _) =>
            {
                evaluated = true;
                return Task.CompletedTask;
            }),
            CancellationToken.None);

        coordinator.Cancel(GuildId);
        await pending;

        Assert.False(evaluated);
    }

    [Fact]
    public async Task Offer_NewFragmentCancelsStaleInFlightEvaluation()
    {
        var coordinator = Coordinator(windowMilliseconds: 0);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEvaluated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstEpisodeId = null;
        WorldAutonomyAmbientEpisode? secondEpisode = null;
        var first = coordinator.OfferAsync(
            Request(1001, async (episode, cancellationToken) =>
            {
                firstEpisodeId = episode.EpisodeId;
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    firstCancelled.TrySetResult();
                    throw;
                }
            }),
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = coordinator.OfferAsync(
            Request(1002, (episode, _) =>
            {
                secondEpisode = episode;
                secondEvaluated.TrySetResult();
                return Task.CompletedTask;
            }),
            CancellationToken.None);
        await Task.WhenAll(first, second);

        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondEvaluated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstEpisodeId, secondEpisode?.EpisodeId);
        Assert.Equal(2, secondEpisode?.MessageCount);
    }

    private const ulong GuildId = 4001;
    private const ulong ChannelId = 6001;

    private static WorldAutonomyAmbientAdmissionRequest Request(
        ulong messageId,
        Func<WorldAutonomyAmbientEpisode, CancellationToken, Task> evaluate) => new(
            GuildId,
            ChannelId,
            messageId,
            evaluate);

    private static WorldAutonomyAmbientAdmissionCoordinator Coordinator(int windowMilliseconds) => new(
        WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            AmbientEpisodeCoalescingEnabled = true,
            AmbientEpisodeWindowMilliseconds = windowMilliseconds,
        }),
        NullLogger<WorldAutonomyAmbientAdmissionCoordinator>.Instance);
}