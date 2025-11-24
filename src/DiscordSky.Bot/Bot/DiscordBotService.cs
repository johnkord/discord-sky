using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Linq;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Bot;

public sealed class DiscordBotService : IHostedService, IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly ChaosSettings _chaosSettings;
    private readonly BotOptions _options;
    private readonly CreativeOrchestrator _orchestrator;
    private readonly IRandomProvider _randomProvider;

    public DiscordBotService(
        DiscordSocketClient client,
        IOptions<BotOptions> options,
        ChaosSettings chaosSettings,
        CreativeOrchestrator orchestrator,
        ILogger<DiscordBotService> logger,
        IRandomProvider? randomProvider = null)
    {
        _client = client;
        _options = options.Value;
        _chaosSettings = chaosSettings;
        _orchestrator = orchestrator;
        _logger = logger;
        _randomProvider = randomProvider ?? DefaultRandomProvider.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += OnLogAsync;
        _client.MessageReceived += OnMessageReceivedAsync;

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            _logger.LogWarning("Bot token not set. Discord connection skipped – running in dry mode.");
            return;
        }

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync();

        if (!string.IsNullOrWhiteSpace(_options.Status))
        {
            await _client.SetGameAsync(_options.Status);
        }

        _logger.LogInformation("Discord Sky bot started and listening for chaos triggers.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Log -= OnLogAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            return;
        }

        await _client.LogoutAsync();
        await _client.StopAsync();
    }

    private Task OnLogAsync(LogMessage message)
    {
        _logger.Log(MapLogSeverity(message.Severity), message.Exception, message.Message ?? "<no message>");
        return Task.CompletedTask;
    }

    private static LogLevel MapLogSeverity(LogSeverity severity) => severity switch
    {
        LogSeverity.Critical => LogLevel.Critical,
        LogSeverity.Error => LogLevel.Error,
        LogSeverity.Warning => LogLevel.Warning,
        LogSeverity.Info => LogLevel.Information,
        LogSeverity.Verbose => LogLevel.Debug,
        _ => LogLevel.Trace
    };

    private async Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message)
        {
            return;
        }

        if (message.Author.IsBot)
        {
            return;
        }

        if (_chaosSettings.ContainsBanWord(message.Content))
        {
            _logger.LogDebug("Skipping message containing ban words.");
            return;
        }

        var channelName = (message.Channel as SocketGuildChannel)?.Name ?? message.Channel.Name;
        if (!_options.IsChannelAllowed(channelName))
        {
            _logger.LogDebug("Channel '{ChannelName}' is not allow-listed; ignoring message.", channelName ?? "<unknown>");
            return;
        }

        var context = new SocketCommandContext(_client, message);
        var content = message.Content.Trim();

        var hasPrefix = !string.IsNullOrWhiteSpace(_options.CommandPrefix) && content.StartsWith(_options.CommandPrefix, StringComparison.OrdinalIgnoreCase);

        if (hasPrefix)
        {
            await HandlePersonaAsync(context, content, message, CreativeInvocationKind.Command);
            return;
        }

        // Ambient reply chance
        if (_chaosSettings.AmbientReplyChance > 0)
        {
            var roll = _randomProvider.NextDouble();
            if (roll < _chaosSettings.AmbientReplyChance)
            {
                _logger.LogDebug("Ambient reply triggered (roll={Roll:F3} < chance={Chance:F3}) for message {MessageId} in channel {Channel}.", roll, _chaosSettings.AmbientReplyChance, message.Id, channelName);
                await HandlePersonaAsync(context, _options.CommandPrefix, message, CreativeInvocationKind.Ambient);
            }
        }
    }

    private async Task HandlePersonaAsync(SocketCommandContext context, string content, SocketUserMessage message, CreativeInvocationKind invocationKind)
    {
        var prefix = _options.CommandPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        var payload = content[prefix.Length..].TrimStart();
        var defaultPersona = GetDefaultPersona();

        string persona;
        string remainder;

        if (string.IsNullOrWhiteSpace(payload))
        {
            persona = defaultPersona;
            remainder = string.Empty;
        }
        else if (payload.StartsWith('('))
        {
            var closingParenthesisIndex = payload.IndexOf(')');
            if (closingParenthesisIndex < 0)
            {
                await context.Channel.SendMessageAsync($"Usage: {prefix}(persona) [topic]");
                return;
            }

            var extractedPersona = payload[1..closingParenthesisIndex].Trim();
            persona = string.IsNullOrWhiteSpace(extractedPersona) ? defaultPersona : extractedPersona;

            remainder = payload[(closingParenthesisIndex + 1)..].Trim();
        }
        else
        {
            persona = defaultPersona;
            remainder = payload;
        }

        string? topic = string.IsNullOrWhiteSpace(remainder) ? null : remainder;

        if (message.Attachments.Count > 0)
        {
            var attachmentSummary = string.Join(", ", message.Attachments.Select(a => a.Filename));
            var attachmentLine = $"Attachments shared: {attachmentSummary}";
            topic = string.IsNullOrWhiteSpace(topic)
                ? attachmentLine
                : $"{topic}\n\n{attachmentLine}";
        }

        if (invocationKind == CreativeInvocationKind.Command)
        {
            await context.Channel.TriggerTypingAsync();
        }

        var request = new CreativeRequest(
            persona,
            topic,
            GetDisplayName(context.User),
            context.User.Id,
            context.Channel.Id,
            (context.Guild as SocketGuild)?.Id,
            DateTimeOffset.UtcNow,
            invocationKind);

        var result = await _orchestrator.ExecuteAsync(request, context, CancellationToken.None);
        var reply = string.IsNullOrWhiteSpace(result.PrimaryMessage)
            ? CreativeOrchestrator.BuildEmptyResponsePlaceholder(persona, invocationKind)
            : result.PrimaryMessage;

        if (string.IsNullOrWhiteSpace(reply))
        {
            _logger.LogDebug("Invocation {InvocationKind} produced no reply for persona {Persona}; suppressing send.", invocationKind, persona);
            return;
        }
        MessageReference? reference = null;
        if (result.ReplyToMessageId.HasValue)
        {
            reference = new MessageReference(result.ReplyToMessageId.Value);
        }

        await context.Channel.SendMessageAsync(reply, messageReference: reference);
    }

    private static string GetDisplayName(SocketUser user)
    {
        if (user is SocketGuildUser guildUser)
        {
            return guildUser.DisplayName;
        }

        return user.GlobalName ?? user.Username;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    private string GetDefaultPersona()
    {
        if (!string.IsNullOrWhiteSpace(_options.DefaultPersona))
        {
            return _options.DefaultPersona.Trim();
        }

        return "Weird Al";
    }
}

public interface IRandomProvider
{
    double NextDouble();
}

public sealed class DefaultRandomProvider : IRandomProvider
{
    public static DefaultRandomProvider Instance { get; } = new();
    private readonly Random _random = new();
    public double NextDouble() => _random.NextDouble();
}
