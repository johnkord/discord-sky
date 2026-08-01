using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public interface IEpisodeHistoryReader
{
    Task<IReadOnlyList<EpisodeMessage>> GetRecentAsync(
        ulong channelId,
        ulong triggerMessageId,
        int limit,
        DateTimeOffset after,
        CancellationToken cancellationToken);

    Task<EpisodeMessage?> GetMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken);
}

public sealed class DiscordEpisodeHistoryReader : IEpisodeHistoryReader
{
    private readonly DiscordSocketClient _client;
    private readonly ContextAggregator _contextAggregator;
    private readonly BotOptions _options;

    public DiscordEpisodeHistoryReader(
        DiscordSocketClient client,
        ContextAggregator contextAggregator,
        IOptions<BotOptions> options)
    {
        _client = client;
        _contextAggregator = contextAggregator;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<EpisodeMessage>> GetRecentAsync(
        ulong channelId,
        ulong triggerMessageId,
        int limit,
        DateTimeOffset after,
        CancellationToken cancellationToken)
    {
        if (_client.GetChannel(channelId) is not IMessageChannel channel || limit <= 0)
        {
            return Array.Empty<EpisodeMessage>();
        }

        var messages = new List<IMessage>();
        await foreach (var batch in channel.GetMessagesAsync(limit: Math.Clamp(limit * 2, 1, 100))
                           .WithCancellation(cancellationToken))
        {
            messages.AddRange(batch);
        }

        var normalized = new List<EpisodeMessage>();
        foreach (var message in messages
                     .Where(message => message.Id != triggerMessageId && message.Timestamp >= after)
                     .OrderByDescending(message => message.Timestamp))
        {
            if (message.Author.IsBot
                && (message.Author.Id != _client.CurrentUser?.Id || !_options.IncludeOwnMessagesInHistory))
            {
                continue;
            }
            var trimmed = message.TextWithForwarded().Trim();
            if (!string.IsNullOrWhiteSpace(_options.CommandPrefix)
                && trimmed.StartsWith(_options.CommandPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var episodeMessage = await NormalizeAsync(message, cancellationToken);
            if (!string.IsNullOrWhiteSpace(episodeMessage.Content) || episodeMessage.HasMedia)
            {
                normalized.Add(episodeMessage);
            }
            if (normalized.Count >= limit) break;
        }
        return normalized.OrderBy(message => message.Timestamp).ThenBy(message => message.MessageId).ToArray();
    }

    public async Task<EpisodeMessage?> GetMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        if (_client.GetChannel(channelId) is not IMessageChannel channel) return null;
        var message = await channel.GetMessageAsync(messageId);
        return message is null ? null : await NormalizeAsync(message, cancellationToken);
    }

    private async Task<EpisodeMessage> NormalizeAsync(IMessage message, CancellationToken cancellationToken)
    {
        var view = await _contextAggregator.BuildMessageViewAsync(
            message,
            includeHttpUnfurls: true,
            cancellationToken);
        var displayName = (message.Author as SocketGuildUser)?.DisplayName ?? message.Author.Username;
        return new EpisodeMessage(
            message.Id,
            message.Author.Id,
            displayName,
            view.Text,
            view.Timestamp,
            message.Reference?.MessageId.IsSpecified == true ? message.Reference.MessageId.Value : null,
            message.Author.IsBot,
            view.MediaContext,
            view.Images,
            view.UnfurledLinks);
    }
}