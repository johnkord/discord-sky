using Discord;
using Discord.Commands;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Orchestration;

public sealed class ContextAggregator
{
    private readonly ChaosSettings _chaosSettings;
    private readonly ILogger<ContextAggregator> _logger;

    public ContextAggregator(
        ChaosSettings chaosSettings,
        ILogger<ContextAggregator> logger)
    {
        _chaosSettings = chaosSettings;
        _logger = logger;
    }

    public async Task<CreativeContext> BuildContextAsync(CreativeRequest request, SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var history = await GatherHistoryAsync(commandContext, cancellationToken);
        return new CreativeContext(request, _chaosSettings, history);
    }

    private async Task<IReadOnlyList<ChannelMessage>> GatherHistoryAsync(SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var messages = new List<ChannelMessage>();
        try
        {
            var history = commandContext.Channel.GetMessagesAsync(limit: 25);
            await foreach (var batch in history.WithCancellation(cancellationToken))
            {
                foreach (var message in batch)
                {
                    if (message.Id == commandContext.Message.Id)
                    {
                        continue;
                    }

                    messages.Add(new ChannelMessage(
                        message.Author.Username,
                        message.Content ?? string.Empty,
                        message.Timestamp
                    ));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to gather channel history for {ChannelId}", commandContext.Channel.Id);
        }

        return messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .OrderByDescending(m => m.Timestamp)
            .Take(20)
            .Reverse()
            .ToArray();
    }
}
