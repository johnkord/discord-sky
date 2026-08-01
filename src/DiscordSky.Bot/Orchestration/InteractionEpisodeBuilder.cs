using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class InteractionEpisodeBuilder
{
    private readonly IEpisodeHistoryReader _history;
    private readonly IOptionsMonitor<InteractionEpisodeOptions> _options;
    private readonly ILogger<InteractionEpisodeBuilder> _logger;
    private readonly Func<DateTimeOffset> _clock;

    public InteractionEpisodeBuilder(
        IEpisodeHistoryReader history,
        IOptionsMonitor<InteractionEpisodeOptions> options,
        ILogger<InteractionEpisodeBuilder> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _history = history;
        _options = options;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<EpisodeBuildResult> BuildAsync(
        EpisodeTriggerEvidence trigger,
        string? episodeId = null,
        CancellationToken cancellationToken = default)
    {
        if (trigger.MessageId == 0 || trigger.ChannelId == 0)
        {
            return EpisodeBuildResult.Failed("invalid_trigger");
        }

        var capturedAt = _clock();
        var options = _options.CurrentValue;
        var recentLimit = Math.Clamp(options.RecentMessageLimit, 0, 20);
        var after = capturedAt.AddMinutes(-Math.Clamp(options.RecentWindowMinutes, 1, 120));

        try
        {
            var recentTask = _history.GetRecentAsync(
                trigger.ChannelId,
                trigger.MessageId,
                recentLimit,
                after,
                cancellationToken);
            Task<EpisodeMessage?> parentTask = trigger.ReferencedMessageId.HasValue
                ? _history.GetMessageAsync(trigger.ChannelId, trigger.ReferencedMessageId.Value, cancellationToken)
                : Task.FromResult<EpisodeMessage?>(null);
            await Task.WhenAll(recentTask, parentTask);

            var triggerMessage = new EpisodeMessage(
                trigger.MessageId,
                trigger.AuthorId,
                trigger.AuthorDisplayName,
                trigger.View.Text,
                trigger.View.Timestamp,
                trigger.ReferencedMessageId,
                IsBot: false,
                trigger.View.MediaContext,
                trigger.View.Images,
                trigger.View.UnfurledLinks);
            var parent = await parentTask;
            var messages = (await recentTask)
                .Append(triggerMessage)
                .Concat(parent is null ? Array.Empty<EpisodeMessage>() : new[] { parent })
                .Where(message => message.Timestamp <= capturedAt)
                .ToArray();

            var requirement = AmbientReferentDetector.Detect(
                trigger.View.Text,
                trigger.ReferencedMessageId.HasValue,
                trigger.View.HasMedia);
            var candidates = BuildCandidates(triggerMessage, parent, messages, requirement);
            var episode = InteractionEpisode.Create(
                episodeId ?? Guid.NewGuid().ToString("N"),
                capturedAt,
                trigger.ChannelId,
                trigger.MessageId,
                messages,
                parent?.MessageId,
                requirement,
                candidates);
            return EpisodeBuildResult.Success(episode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interaction episode build failed for trigger {MessageId}", trigger.MessageId);
            return EpisodeBuildResult.Failed("history_failure", ex.GetType().Name);
        }
    }

    private static IReadOnlyList<ReferentCandidate> BuildCandidates(
        EpisodeMessage trigger,
        EpisodeMessage? parent,
        IReadOnlyList<EpisodeMessage> messages,
        ReferentRequirement requirement)
    {
        if (parent is not null)
        {
            return new[] { new ReferentCandidate(parent.MessageId, 1.0, "explicit_reply_parent") };
        }
        if (!requirement.IsRequired) return Array.Empty<ReferentCandidate>();

        return messages
            .Where(message => message.MessageId != trigger.MessageId && message.Timestamp <= trigger.Timestamp)
            .Where(message => !string.IsNullOrWhiteSpace(message.Content) || message.HasMedia)
            .OrderByDescending(message => message.Timestamp)
            .Take(3)
            .Select((message, index) => new ReferentCandidate(
                message.MessageId,
                Math.Max(0.45, 0.75 - (index * 0.12)),
                message.HasMedia ? "recent_media" : "recent_message"))
            .ToArray();
    }
}