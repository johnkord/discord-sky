using Discord;
using Discord.Commands;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models;
using DiscordSky.Bot.Services;
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
    private readonly BitStarterService _bitStarter;
    private readonly GremlinStudioService _gremlinStudio;
    private readonly HeckleCycleService _heckleCycle;
    private readonly MischiefQuestService _questService;

    public DiscordBotService(
        DiscordSocketClient client,
        IOptions<BotOptions> options,
        ChaosSettings chaosSettings,
        BitStarterService bitStarter,
        GremlinStudioService gremlinStudio,
        HeckleCycleService heckleCycle,
        MischiefQuestService questService,
        ILogger<DiscordBotService> logger)
    {
        _client = client;
        _options = options.Value;
        _chaosSettings = chaosSettings;
        _bitStarter = bitStarter;
        _gremlinStudio = gremlinStudio;
        _heckleCycle = heckleCycle;
        _questService = questService;
        _logger = logger;
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

        if (content.StartsWith("!chaos", StringComparison.OrdinalIgnoreCase))
        {
            await HandleChaosAsync(context, content);
        }
        else if (content.StartsWith("!quest", StringComparison.OrdinalIgnoreCase))
        {
            await HandleQuestAsync(context);
        }
        else if (content.StartsWith("!heckle-done", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHeckleDoneAsync(context);
        }
        else if (content.StartsWith("!heckle", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHeckleAsync(context, content);
        }
        else if (content.StartsWith("!remix", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRemixAsync(context, message);
        }
    }

    private async Task HandleChaosAsync(SocketCommandContext context, string content)
    {
        var topic = content.Length > 6 ? content[6..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(topic))
        {
            await context.Channel.SendMessageAsync("Throw me a topic! Usage: !chaos <topic>");
            return;
        }

        var participants = context.Channel is SocketTextChannel textChannel
            ? textChannel.Users.Where(u => !u.IsBot).Select(u => u.DisplayName).Take(6).ToArray()
            : new[] { context.User.Username };

        var response = _bitStarter.Generate(new BitStarterRequest(topic, participants), _chaosSettings);
        var payload = string.Join('\n', response.ScriptLines);

        await context.Channel.SendMessageAsync($"**{response.Title}**\n{payload}\nTags: {string.Join(", ", response.MentionTags)}");
    }

    private async Task HandleQuestAsync(SocketCommandContext context)
    {
        var quest = _questService.DrawQuest(_chaosSettings);
        var steps = string.Join("\n", quest.Steps.Select((step, index) => $"{index + 1}. {step}"));

        await context.Channel.SendMessageAsync($"**{quest.Title}**\n{steps}\nReward: {quest.RewardKind} – {quest.RewardDescription}");
    }

    private async Task HandleHeckleAsync(SocketCommandContext context, string content)
    {
        var declaration = content.Length > 7 ? content[7..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(declaration))
        {
            await context.Channel.SendMessageAsync("What are we nudging you about? Usage: !heckle <promise>");
            return;
        }

        var trigger = new HeckleTrigger(context.User.Username, declaration, DateTimeOffset.UtcNow, Delivered: false);
        var response = _heckleCycle.BuildResponse(trigger, _chaosSettings);

        await context.Channel.SendMessageAsync($"{response.Reminder}\nNext poke at {response.NextNudgeAt:HH:mm} UTC.");
        await context.Channel.SendMessageAsync($"When you're done, type `!heckle-done` so I can drop the celebration.");
    }

    private async Task HandleRemixAsync(SocketCommandContext context, SocketUserMessage message)
    {
        var seed = message.Content.Length > 6 ? message.Content[6..].Trim() : "server chaos";
        var attachments = message.Attachments.Select(a => a.Filename).ToArray();

        var prompt = new GremlinPrompt(seed, GremlinArtifactKind.ImageMashup, attachments);
        var artifact = _gremlinStudio.Remix(prompt, _chaosSettings);
        var payload = string.Join("\n", artifact.Payloads.Select(line => $"• {line}"));

        await context.Channel.SendMessageAsync($"**{artifact.Title}**\n{payload}");
    }

    private async Task HandleHeckleDoneAsync(SocketCommandContext context)
    {
        var trigger = new HeckleTrigger(context.User.Username, "completed chaos", DateTimeOffset.UtcNow, Delivered: true);
        var response = _heckleCycle.BuildResponse(trigger, _chaosSettings);

        var celebration = string.IsNullOrWhiteSpace(response.FollowUpCelebration)
            ? $"🎊 {context.User.Username} actually shipped something!"
            : response.FollowUpCelebration;

        await context.Channel.SendMessageAsync(celebration);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}
