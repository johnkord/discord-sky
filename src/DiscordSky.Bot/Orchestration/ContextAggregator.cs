using System;
using Discord;
using Discord.Commands;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class ContextAggregator
{
    private readonly ChaosSettings _chaosSettings;
    private readonly ILogger<ContextAggregator> _logger;
    private readonly string _commandPrefix;
    private readonly int _historyLimit;

    public ContextAggregator(
        ChaosSettings chaosSettings,
        IOptions<BotOptions> botOptions,
        ILogger<ContextAggregator> logger)
    {
        _chaosSettings = chaosSettings;
        _logger = logger;

        var bot = botOptions.Value;
        _commandPrefix = bot.CommandPrefix ?? string.Empty;
        _historyLimit = bot.HistoryMessageLimit > 0 ? Math.Clamp(bot.HistoryMessageLimit, 1, 100) : 20;
    }

    public async Task<CreativeContext> BuildContextAsync(CreativeRequest request, SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var history = await GatherHistoryAsync(commandContext, cancellationToken);
        return new CreativeContext(request, _chaosSettings, history);
    }

    private async Task<IReadOnlyList<ChannelMessage>> GatherHistoryAsync(SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var messages = new List<ChannelMessage>();
        var fetchLimit = Math.Max(_historyLimit * 2, _historyLimit);

        try
        {
            var history = commandContext.Channel.GetMessagesAsync(limit: fetchLimit);
            await foreach (var batch in history.WithCancellation(cancellationToken))
            {
                foreach (var message in batch)
                {
                    if (message.Id == commandContext.Message.Id)
                    {
                        continue;
                    }

                    var content = message.Content ?? string.Empty;
                    var trimmed = content.Trim();

                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }

                    if (message.Author.IsBot)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(_commandPrefix) && trimmed.StartsWith(_commandPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    messages.Add(new ChannelMessage(
                        message.Id,
                        message.Author.Username,
                        message.Content ?? string.Empty,
                        message.Timestamp,
                        message.Author.IsBot
                    ));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to gather channel history for {ChannelId}", commandContext.Channel.Id);
        }

        return messages
            .OrderByDescending(m => m.Timestamp)
            .Take(_historyLimit)
            .OrderBy(m => m.Timestamp)
            .ToArray();
    }
}
