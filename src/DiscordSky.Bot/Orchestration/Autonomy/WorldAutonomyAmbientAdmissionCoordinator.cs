using System.Collections.Concurrent;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record WorldAutonomyAmbientEpisode(
    string EpisodeId,
    int MessageCount,
    ulong GuildId,
    ulong ChannelId,
    ulong TriggerMessageId);

public sealed record WorldAutonomyAmbientAdmissionRequest(
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    Func<WorldAutonomyAmbientEpisode, CancellationToken, Task> EvaluateAsync);

public sealed class WorldAutonomyAmbientAdmissionCoordinator
{
    private readonly WorldAutonomyConfiguration _configuration;
    private readonly ILogger<WorldAutonomyAmbientAdmissionCoordinator> _logger;
    private readonly ConcurrentDictionary<ulong, GuildAdmissionState> _guilds = new();

    public WorldAutonomyAmbientAdmissionCoordinator(
        WorldAutonomyConfiguration configuration,
        ILogger<WorldAutonomyAmbientAdmissionCoordinator> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task OfferAsync(
        WorldAutonomyAmbientAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.EvaluateAsync);

        var state = _guilds.GetOrAdd(request.GuildId, _ => new GuildAdmissionState());
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? priorDelay;
        CancellationTokenSource? priorEvaluation;
        string episodeId;
        int messageCount;
        long version;
        lock (state.Gate)
        {
            var continuesEpisode = (state.Pending is not null || state.EvaluationCancellation is not null) &&
                state.ChannelId == request.ChannelId && state.EpisodeId is not null;
            episodeId = continuesEpisode
                ? state.EpisodeId!
                : Guid.NewGuid().ToString("N");
            messageCount = continuesEpisode ? state.MessageCount + 1 : 1;
            priorDelay = state.DelayCancellation;
            priorEvaluation = state.EvaluationCancellation;
            state.Pending = request;
            state.ChannelId = request.ChannelId;
            state.EpisodeId = episodeId;
            state.MessageCount = messageCount;
            state.Version++;
            version = state.Version;
            state.DelayCancellation = cancellation;
            state.EvaluationCancellation = null;
        }

        Cancel(priorDelay);
        Cancel(priorEvaluation);
        return EvaluateAfterWindowAsync(
            state,
            request,
            new WorldAutonomyAmbientEpisode(
                episodeId,
                messageCount,
                request.GuildId,
                request.ChannelId,
                request.MessageId),
            version,
            cancellation);
    }

    public void Cancel(ulong guildId)
    {
        if (!_guilds.TryGetValue(guildId, out var state))
        {
            return;
        }

        CancellationTokenSource? delay;
        CancellationTokenSource? evaluation;
        lock (state.Gate)
        {
            state.Version++;
            state.Pending = null;
            state.EpisodeId = null;
            state.MessageCount = 0;
            delay = state.DelayCancellation;
            evaluation = state.EvaluationCancellation;
            state.DelayCancellation = null;
            state.EvaluationCancellation = null;
        }

        Cancel(delay);
        Cancel(evaluation);
    }

    private async Task EvaluateAfterWindowAsync(
        GuildAdmissionState state,
        WorldAutonomyAmbientAdmissionRequest request,
        WorldAutonomyAmbientEpisode episode,
        long version,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (_configuration.AmbientEpisodeCoalescingEnabled)
            {
                await Task.Delay(_configuration.AmbientEpisodeWindow, cancellation.Token).ConfigureAwait(false);
            }

            lock (state.Gate)
            {
                if (state.Version != version ||
                    !ReferenceEquals(state.DelayCancellation, cancellation) ||
                    !ReferenceEquals(state.Pending, request))
                {
                    return;
                }

                state.Pending = null;
                state.DelayCancellation = null;
                state.EvaluationCancellation = cancellation;
            }

            await request.EvaluateAsync(episode, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Ambient autonomy admission failed for guild {GuildId}, message {MessageId}.",
                request.GuildId,
                request.MessageId);
        }
        finally
        {
            lock (state.Gate)
            {
                if (ReferenceEquals(state.DelayCancellation, cancellation))
                {
                    state.DelayCancellation = null;
                }
                if (ReferenceEquals(state.EvaluationCancellation, cancellation))
                {
                    state.EvaluationCancellation = null;
                    if (state.Pending is null && state.Version == version)
                    {
                        state.EpisodeId = null;
                        state.MessageCount = 0;
                    }
                }
            }
            cancellation.Dispose();
        }
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class GuildAdmissionState
    {
        internal object Gate { get; } = new();

        internal WorldAutonomyAmbientAdmissionRequest? Pending { get; set; }

        internal ulong ChannelId { get; set; }

        internal string? EpisodeId { get; set; }

        internal int MessageCount { get; set; }

        internal long Version { get; set; }

        internal CancellationTokenSource? DelayCancellation { get; set; }

        internal CancellationTokenSource? EvaluationCancellation { get; set; }
    }
}