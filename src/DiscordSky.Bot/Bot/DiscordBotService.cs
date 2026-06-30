using System.Collections.Concurrent;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Integrations.Safety;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
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
    private readonly IRecallTelemetrySink _telemetry;
    private readonly IOptionsMonitor<ChaosSettings> _chaosSettingsMonitor;
    private readonly BotOptions _options;
    private readonly CreativeOrchestrator _orchestrator;
    private readonly ContextAggregator _contextAggregator;
    private readonly IUserMemoryStore _memoryStore;
    private readonly IOptionsMonitor<MemoryRelevanceOptions> _memoryRelevanceMonitor;
    private readonly ILinkUnfurler _linkUnfurler;
    private readonly IRandomProvider _randomProvider;
    private readonly IReactionSink _reactionSink;
    private readonly int _reactionExcerptLength;
    private readonly ImageToolService? _imageToolService;
    private readonly ImageRewriter? _imageRewriter;
    private readonly ScamGuardOptions _scamGuard;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _scamWarnCooldown = new();
    private readonly ConcurrentDictionary<ulong, (string Persona, DateTimeOffset CreatedAt)> _personaCache = new();
    private readonly ConcurrentDictionary<ulong, ChannelMessageBuffer> _channelBuffers = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _userMemoryLocks = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _evictionLock = new();
    private const int MaxPersonaCacheSize = 500;
    internal const int DiscordMaxMessageLength = 2000;

    public DiscordBotService(
        DiscordSocketClient client,
        IOptions<BotOptions> options,
        IOptionsMonitor<ChaosSettings> chaosSettings,
        CreativeOrchestrator orchestrator,
        ContextAggregator contextAggregator,
        IUserMemoryStore memoryStore,
        IOptionsMonitor<MemoryRelevanceOptions> memoryRelevanceMonitor,
        ILinkUnfurler linkUnfurler,
        ILogger<DiscordBotService> logger,
        IRecallTelemetrySink telemetry,
        IRandomProvider? randomProvider = null,
        IReactionSink? reactionSink = null,
        IOptions<ReactionOptions>? reactionOptions = null,
        ImageToolService? imageToolService = null,
        ImageRewriter? imageRewriter = null,
        IOptions<ScamGuardOptions>? scamGuardOptions = null)
    {
        _client = client;
        _options = options.Value;
        _chaosSettingsMonitor = chaosSettings;
        _orchestrator = orchestrator;
        _contextAggregator = contextAggregator;
        _memoryStore = memoryStore;
        _memoryRelevanceMonitor = memoryRelevanceMonitor;
        _linkUnfurler = linkUnfurler;
        _logger = logger;
        _telemetry = telemetry;
        _randomProvider = randomProvider ?? DefaultRandomProvider.Instance;
        _reactionSink = reactionSink ?? new NoOpReactionSink();
        _reactionExcerptLength = reactionOptions?.Value.ReplyExcerptLength ?? 200;
        _imageToolService = imageToolService;
        _imageRewriter = imageRewriter;
        _scamGuard = scamGuardOptions?.Value ?? new ScamGuardOptions { Enabled = false };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += OnLogAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ReactionAdded += OnReactionAddedAsync;
        _client.ReactionRemoved += OnReactionRemovedAsync;
        _client.Ready += OnReadyAsync;

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

    private Task OnReadyAsync()
    {
        _contextAggregator.SetBotUserId(_client.CurrentUser.Id);
        _logger.LogInformation("Bot ready. User ID: {BotUserId}", _client.CurrentUser.Id);
        return Task.CompletedTask;
    }

    private Task OnReactionAddedAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction)
    {
        RecordReaction(message, channel, reaction, "add");
        return Task.CompletedTask;
    }

    private Task OnReactionRemovedAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction)
    {
        RecordReaction(message, channel, reaction, "remove");
        return Task.CompletedTask;
    }

    // Reception signal (fun_assessment_2026-06-25 P1): record reactions on the bot's OWN messages only.
    // Bot-message detection is O(1) via the persona cache, which already indexes every message we send.
    private void RecordReaction(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction,
        string action)
    {
        try
        {
            if (!_personaCache.TryGetValue(reaction.MessageId, out var cached)) return; // not our message
            if (_client.CurrentUser is not null && reaction.UserId == _client.CurrentUser.Id) return; // self-react

            string? excerpt = null;
            if (message.HasValue && !string.IsNullOrWhiteSpace(message.Value.Content))
            {
                var content = message.Value.Content;
                excerpt = content.Length > _reactionExcerptLength ? content[.._reactionExcerptLength] : content;
            }

            var guildId = (channel.HasValue ? channel.Value as SocketGuildChannel : null)?.Guild.Id;

            _reactionSink.Record(new ReactionEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Action: action,
                Emote: reaction.Emote.Name,
                ReactorUserId: reaction.UserId,
                ChannelId: channel.Id,
                GuildId: guildId,
                MessageId: reaction.MessageId,
                Persona: cached.Persona,
                ReplyExcerpt: excerpt));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record reaction on message {MessageId}", reaction.MessageId);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Flush any pending conversation buffers before shutdown
        await FlushAllBuffersAsync();

        await _shutdownCts.CancelAsync();

        _client.Log -= OnLogAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.ReactionAdded -= OnReactionAddedAsync;
        _client.ReactionRemoved -= OnReactionRemovedAsync;
        _client.Ready -= OnReadyAsync;

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

        // Emit telemetry for gateway disconnects so we can distinguish normal reconnects (~10/day, Discord
        // proactively rotates) from a real problem (auth revoked, network partition). Without this signal
        // a real outage looks identical to housekeeping in kubectl logs.
        if (message.Exception is not null)
        {
            var exType = message.Exception.GetType().Name;
            if (exType.Contains("Reconnect", StringComparison.OrdinalIgnoreCase)
                || exType.Contains("WebSocket", StringComparison.OrdinalIgnoreCase)
                || message.Exception.Message?.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) == true)
            {
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.GatewayDisconnect,
                    Reason: exType));
            }
        }
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

    private Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        // Discord.Net executes event handlers synchronously on the gateway task. Orchestrating a reply
        // (LLM calls with retries/backoff + HTTP link unfurls) routinely exceeds Discord's heartbeat
        // window, which starves the gateway and triggers reconnects/disconnects. Production telemetry
        // showed ~13 gateway disconnects/day plus repeated "A MessageReceived handler is blocking the
        // gateway task" warnings. Offload all processing to a background task so the gateway thread stays
        // responsive; concurrent LLM cost is already bounded by the orchestrator's _llmThrottle.
        // See docs/improvement_opportunities_2026-06-10.md F1 and the Discord.Net events guide.
        _ = Task.Run(() => ProcessMessageSafelyAsync(rawMessage));
        return Task.CompletedTask;
    }

    private async Task ProcessMessageSafelyAsync(SocketMessage rawMessage)
    {
        try
        {
            await ProcessMessageAsync(rawMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing message {MessageId}", rawMessage.Id);
            // Only send error feedback for command-prefixed messages.
            // Ambient/unsolicited failures should be silent to avoid confusing users.
            var content = (rawMessage as SocketUserMessage)?.Content?.Trim() ?? string.Empty;
            var isCommand = !string.IsNullOrWhiteSpace(_options.CommandPrefix)
                && content.StartsWith(_options.CommandPrefix, StringComparison.OrdinalIgnoreCase);
            var isReplyToBot = rawMessage is SocketUserMessage um
                && um.Reference?.MessageId.IsSpecified == true
                && um.ReferencedMessage?.Author.Id == _client.CurrentUser?.Id;

            if (isCommand || isReplyToBot)
            {
                try
                {
                    if (rawMessage.Channel is not null)
                    {
                        await rawMessage.Channel.SendMessageAsync("Something went wrong on my end—try again!");
                    }
                }
                catch (Exception innerEx)
                {
                    _logger.LogDebug(innerEx, "Failed to send error notification to channel");
                }
            }
        }
    }

    private async Task ProcessMessageAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message)
        {
            return;
        }

        if (message.Author.IsBot)
        {
            return;
        }

        if (_chaosSettingsMonitor.CurrentValue.ContainsBanWord(message.Content))
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

        // Proactive scam-link guard: if an obvious phishing/crypto-scam link lands here, bellow an in-character
        // warning and stop. Runs regardless of whether the bot was addressed, and before memory extraction so a
        // scam never gets remembered as something a user "said". Requested by a server admin (sleeper-mod duty).
        if (_scamGuard.Enabled && await TryHandleScamLinkAsync(message))
        {
            return;
        }

        // Conversation-window memory extraction: buffer messages and process in batches
        if (_options.EnableUserMemory && !string.IsNullOrWhiteSpace(message.Content))
        {
            BufferMessageForExtraction(message);
        }

        var context = new SocketCommandContext(_client, message);
        var content = message.Content.Trim();

        // Check if this is a reply to the bot
        if (message.Reference?.MessageId.IsSpecified == true)
        {
            // Try to get the referenced message - it might be cached or we need to fetch it
            IMessage? referencedMessage = message.ReferencedMessage;
            if (referencedMessage == null)
            {
                try
                {
                    referencedMessage = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to fetch referenced message {MessageId}", message.Reference.MessageId.Value);
                }
            }

            if (_client.CurrentUser is not null && referencedMessage?.Author.Id == _client.CurrentUser.Id)
            {
                _logger.LogDebug(
                    "Direct reply detected: {UserId} replied to bot message {BotMessageId}",
                    message.Author.Id,
                    referencedMessage.Id);

                await HandleDirectReplyAsync(context, message);
                return;
            }
        }

        var hasPrefix = !string.IsNullOrWhiteSpace(_options.CommandPrefix) && content.StartsWith(_options.CommandPrefix, StringComparison.OrdinalIgnoreCase);

        if (hasPrefix)
        {
            // Handle memory management commands before normal persona flow
            var payload = content[_options.CommandPrefix.Length..].TrimStart();
            if (payload.Equals("forget-me", StringComparison.OrdinalIgnoreCase))
            {
                await HandleForgetMeAsync(context);
                return;
            }
            if (payload.Equals("what-do-you-know", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWhatDoYouKnowAsync(context);
                return;
            }
            if (payload.StartsWith("forget ", StringComparison.OrdinalIgnoreCase)
                || payload.Equals("forget", StringComparison.OrdinalIgnoreCase))
            {
                var topic = payload.Length > "forget".Length
                    ? payload["forget".Length..].Trim()
                    : string.Empty;
                await HandleForgetTopicAsync(context, topic);
                return;
            }

            // Image command (docs/image_generation_design.md). Intercept before persona parsing so the
            // "(image)" prefix is not mistaken for a "(persona)" selector.
            if (payload.StartsWith("(image)", StringComparison.OrdinalIgnoreCase))
            {
                var imageRequest = payload["(image)".Length..].Trim();
                await HandleImageAsync(context, message, imageRequest);
                return;
            }

            await HandlePersonaAsync(context, content, message, CreativeInvocationKind.Command);
            return;
        }

        // Natural-language image request when the bot is addressed by mention or name (not a reply): route
        // to the image pipeline so images are not stranded behind a command nobody types. See ops_analysis P2.
        if (_imageToolService?.IsEnabled == true && ImageIntentDetector.LooksLikeImageRequest(content))
        {
            var addressedBotId = _client.CurrentUser?.Id;
            var addressed = (addressedBotId.HasValue && message.MentionedUsers.Any(u => u.Id == addressedBotId.Value))
                || MentionsBotName(content);
            if (addressed)
            {
                await HandleImageAsync(context, message, content);
                return;
            }
        }

        // Ambient reply chance — modulated by context so the bot interjects at better moments and
        // does not dominate a channel. See docs/improvement_opportunities_2026-06-10.md F7.
        var chaosSettings = _chaosSettingsMonitor.CurrentValue;
        if (chaosSettings.AmbientReplyChance > 0)
        {
            var botSpokeRecently = DidBotSpeakRecently(context.Channel, TimeSpan.FromMinutes(2));
            var botId = _client.CurrentUser?.Id;
            var mentionsBot = (botId.HasValue && message.MentionedUsers.Any(u => u.Id == botId.Value))
                || MentionsBotName(content);
            var effectiveChance = ComputeEffectiveAmbientChance(
                chaosSettings.AmbientReplyChance, content, botSpokeRecently, mentionsBot);

            var roll = _randomProvider.NextDouble();
            if (roll < effectiveChance)
            {
                _logger.LogInformation(
                    "Ambient reply triggered (roll={Roll:F3} < effective={Eff:F3}, base={Base:F3}, botSpokeRecently={Recent}, mentionsBot={Mention}) for message {MessageId} in channel {Channel}.",
                    roll, effectiveChance, chaosSettings.AmbientReplyChance, botSpokeRecently, mentionsBot, message.Id, channelName);
                // Pass prefix + message content so HandlePersonaAsync can extract the user's text as the topic
                await HandlePersonaAsync(context, _options.CommandPrefix + " " + content, message, CreativeInvocationKind.Ambient);
            }
        }
    }

    /// <summary>
    /// Adjusts the base ambient-reply probability using cheap conversational signals so the bot
    /// chimes in at better moments and does not dominate a channel. Pure function for testability.
    /// </summary>
    internal static double ComputeEffectiveAmbientChance(
        double baseChance,
        string? messageContent,
        bool botSpokeRecently,
        bool mentionsBot)
    {
        if (baseChance <= 0) return 0.0;

        var content = (messageContent ?? string.Empty).Trim();
        var factor = 1.0;

        // Don't dominate: if the bot just spoke here, back off hard.
        if (botSpokeRecently) factor *= 0.35;

        // Someone naming the bot (without a formal reply) is a strong cue to engage.
        if (mentionsBot) factor *= 2.5;

        // A question in the air is a better moment to chime in.
        if (content.EndsWith("?", StringComparison.Ordinal)) factor *= 1.6;

        // Throwaway messages ("lol", "k", "nice") are poor interjection material; substantive ones are better.
        if (content.Length < 4) factor *= 0.3;
        else if (content.Length < 12) factor *= 0.7;
        else if (content.Length > 80) factor *= 1.3;

        return Math.Clamp(baseChance * factor, 0.0, 0.9);
    }

    private bool DidBotSpeakRecently(ISocketMessageChannel channel, TimeSpan window)
    {
        var botId = _client.CurrentUser?.Id;
        if (botId is null) return false;
        var cutoff = DateTimeOffset.UtcNow - window;
        try
        {
            foreach (var msg in channel.GetCachedMessages(20))
            {
                if (msg.Author.Id == botId.Value && msg.Timestamp >= cutoff) return true;
            }
        }
        catch
        {
            // Cache may be unavailable (e.g. just reconnected); treat as "not recently".
        }
        return false;
    }

    private bool MentionsBotName(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var name = _client.CurrentUser?.Username;
        return !string.IsNullOrWhiteSpace(name)
            && content.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandlePersonaAsync(SocketCommandContext context, string content, SocketUserMessage message, CreativeInvocationKind invocationKind)
    {
        var prefix = _options.CommandPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        // Traffic visibility: invocation_kind + author + channel. One log line per orchestrated reply,
        // makes "is the bot getting any traffic at all" answerable from logs alone.
        _logger.LogInformation(
            "persona_invoked kind={Kind} author={Author} channel={Channel} message_id={MessageId}",
            invocationKind, message.Author.Id, context.Channel.Name, message.Id);
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.PersonaInvoked,
            UserHash: UserIdHash.Hash(message.Author.Id),
            Channel: context.Channel.Name,
            Kind: invocationKind.ToString(),
            MessageId: message.Id));

        var payload = content[prefix.Length..].TrimStart();
        var defaultPersona = GetDefaultPersona();

        string persona;
        string remainder;

        // For ambient replies, always use the default persona — the user doesn't know
        // they triggered an ambient reply, so parsing persona syntax would be misleading.
        if (invocationKind == CreativeInvocationKind.Ambient)
        {
            persona = defaultPersona;
            remainder = payload;
        }
        else if (string.IsNullOrWhiteSpace(payload))
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

        var channelContext = BuildChannelContext(context);

        // Load per-user memories if enabled
        IReadOnlyList<UserMemory>? userMemories = null;
        if (_options.EnableUserMemory)
        {
            userMemories = await _memoryStore.GetAdmissibleMemoriesAsync(
                context.User.Id, _memoryRelevanceMonitor, _shutdownCts.Token);
        }

        // Collect images from the triggering message
        var triggerImages = _contextAggregator.CollectImages(message);
        IReadOnlyList<ChannelImage>? triggerImagesParam = triggerImages.Count > 0 ? triggerImages : null;

        // Unfurl links (e.g. tweets) from the triggering message
        IReadOnlyList<UnfurledLink>? unfurledLinks = null;
        if (_options.EnableLinkUnfurling && !string.IsNullOrWhiteSpace(topic))
        {
            unfurledLinks = await _linkUnfurler.UnfurlAsync(topic, DateTimeOffset.UtcNow, _shutdownCts.Token);
            if (unfurledLinks.Count == 0) unfurledLinks = null;
        }

        var request = new CreativeRequest(
            persona,
            topic,
            GetDisplayName(context.User),
            context.User.Id,
            context.Channel.Id,
            (context.Guild as SocketGuild)?.Id,
            DateTimeOffset.UtcNow,
            invocationKind,
            Channel: channelContext,
            UserMemories: userMemories,
            UnfurledLinks: unfurledLinks,
            TriggerImages: triggerImagesParam);

        var result = await _orchestrator.ExecuteAsync(request, context, _shutdownCts.Token);
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

        await SendChunkedAsync(context.Channel, reply, reference, persona, result.AttachmentBytes, result.AttachmentFileName);
    }

    private async Task HandleDirectReplyAsync(SocketCommandContext context, SocketUserMessage message)
    {
        // Natural-language image request in a reply ("draw me as a knight") routes straight to the image
        // pipeline, so it does not depend on the model choosing the tool. See docs/ops_analysis_2026-06-29.md P2.
        if (_imageToolService?.IsEnabled == true && ImageIntentDetector.LooksLikeImageRequest(message.Content))
        {
            await HandleImageAsync(context, message, message.Content.Trim());
            return;
        }

        // Show typing indicator for direct replies (same as Command)
        await context.Channel.TriggerTypingAsync();

        // Gather the reply chain
        var replyChain = await _contextAggregator.GatherReplyChainAsync(
            message,
            context.Channel,
            _shutdownCts.Token);

        // Look up the persona from the original bot message, falling back to default
        var persona = GetDefaultPersona();
        if (message.Reference?.MessageId.IsSpecified == true
            && _personaCache.TryGetValue(message.Reference.MessageId.Value, out var cached))
        {
            persona = cached.Persona;
        }

        // The user's reply content becomes the topic
        var topic = message.Content.Trim();
        if (message.Attachments.Count > 0)
        {
            var attachmentSummary = string.Join(", ", message.Attachments.Select(a => a.Filename));
            var attachmentLine = $"Attachments shared: {attachmentSummary}";
            topic = string.IsNullOrWhiteSpace(topic)
                ? attachmentLine
                : $"{topic}\n\n{attachmentLine}";
        }

        // Detect if we're in a thread
        var isInThread = context.Channel is Discord.IThreadChannel;
        var channelContext = BuildChannelContext(context);

        // Load per-user memories if enabled
        IReadOnlyList<UserMemory>? userMemories = null;
        if (_options.EnableUserMemory)
        {
            userMemories = await _memoryStore.GetAdmissibleMemoriesAsync(
                context.User.Id, _memoryRelevanceMonitor, _shutdownCts.Token);
        }

        // Collect images from the reply message
        var triggerImages = _contextAggregator.CollectImages(message);
        IReadOnlyList<ChannelImage>? triggerImagesParam = triggerImages.Count > 0 ? triggerImages : null;

        // Unfurl links (e.g. tweets) from the reply message
        IReadOnlyList<UnfurledLink>? unfurledLinks = null;
        if (_options.EnableLinkUnfurling && !string.IsNullOrWhiteSpace(topic))
        {
            unfurledLinks = await _linkUnfurler.UnfurlAsync(topic, DateTimeOffset.UtcNow, _shutdownCts.Token);
            if (unfurledLinks.Count == 0) unfurledLinks = null;
        }

        var request = new CreativeRequest(
            persona,
            string.IsNullOrWhiteSpace(topic) ? null : topic,
            GetDisplayName(context.User),
            context.User.Id,
            context.Channel.Id,
            (context.Guild as SocketGuild)?.Id,
            DateTimeOffset.UtcNow,
            CreativeInvocationKind.DirectReply,
            replyChain,
            isInThread,
            message.Id,
            channelContext,
            userMemories,
            unfurledLinks,
            triggerImagesParam);

        // Traffic visibility — see HandlePersonaAsync. DirectReply was previously silent in telemetry,
        // which underclamped the adoption-rate denominator (recall_feature_review §6.7 footnote).
        _logger.LogInformation(
            "persona_invoked kind={Kind} author={Author} channel={Channel} message_id={MessageId}",
            CreativeInvocationKind.DirectReply, message.Author.Id, context.Channel.Name, message.Id);
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.PersonaInvoked,
            UserHash: UserIdHash.Hash(message.Author.Id),
            Channel: context.Channel.Name,
            Kind: CreativeInvocationKind.DirectReply.ToString(),
            MessageId: message.Id));

        var result = await _orchestrator.ExecuteAsync(request, context, _shutdownCts.Token);
        var reply = string.IsNullOrWhiteSpace(result.PrimaryMessage)
            ? CreativeOrchestrator.BuildEmptyResponsePlaceholder(persona, CreativeInvocationKind.DirectReply)
            : result.PrimaryMessage;

        if (string.IsNullOrWhiteSpace(reply))
        {
            _logger.LogDebug("DirectReply produced no reply for persona {Persona}; suppressing send.", persona);
            return;
        }

        // For DirectReply, default to replying to the user's message (the trigger)
        // unless the orchestrator chose a different target
        MessageReference? reference = result.ReplyToMessageId.HasValue
            ? new MessageReference(result.ReplyToMessageId.Value)
            : new MessageReference(message.Id);

        await SendChunkedAsync(context.Channel, reply, reference, persona, result.AttachmentBytes, result.AttachmentFileName);
    }

    private async Task SendChunkedAsync(
        ISocketMessageChannel channel, string text, MessageReference? reference, string persona,
        byte[]? attachmentBytes = null, string? attachmentFileName = null)
    {
        var hasAttachment = attachmentBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(attachmentFileName);

        // The reply text becomes the image caption (first chunk); any overflow follows as plain messages.
        var chunks = text.Length <= DiscordMaxMessageLength
            ? new List<string> { text }
            : ChunkMessage(text, DiscordMaxMessageLength);

        if (hasAttachment)
        {
            using var stream = new MemoryStream(attachmentBytes!);
            var sentFile = await channel.SendFileAsync(stream, attachmentFileName, text: chunks[0], messageReference: reference);
            CachePersona(sentFile.Id, persona);
            for (int i = 1; i < chunks.Count; i++)
            {
                var more = await channel.SendMessageAsync(chunks[i]);
                CachePersona(more.Id, persona);
            }
            return;
        }

        // Split into chunks; first chunk gets the reply reference
        for (int i = 0; i < chunks.Count; i++)
        {
            var sent = await channel.SendMessageAsync(chunks[i], messageReference: i == 0 ? reference : null);
            // Cache persona for every chunk so replies to any part preserve character continuity
            CachePersona(sent.Id, persona);
        }
    }

    private void CachePersona(ulong messageId, string persona)
    {
        _personaCache[messageId] = (persona, DateTimeOffset.UtcNow);
        EvictStalePersonas();
    }

    private void EvictStalePersonas()
    {
        if (_personaCache.Count <= MaxPersonaCacheSize)
        {
            return;
        }

        // Use TryEnter so concurrent callers skip eviction instead of blocking
        if (!Monitor.TryEnter(_evictionLock))
        {
            return;
        }

        try
        {
            // Double-check after acquiring lock
            if (_personaCache.Count <= MaxPersonaCacheSize)
            {
                return;
            }

            // First pass: remove entries older than 24 hours
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            foreach (var key in _personaCache.Keys)
            {
                if (_personaCache.TryGetValue(key, out var entry) && entry.CreatedAt < cutoff)
                {
                    _personaCache.TryRemove(key, out _);
                }
            }

            // Second pass: if still over cap, evict oldest entries until at the limit
            if (_personaCache.Count > MaxPersonaCacheSize)
            {
                var excess = _personaCache
                    .OrderBy(kvp => kvp.Value.CreatedAt)
                    .Take(_personaCache.Count - MaxPersonaCacheSize)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in excess)
                {
                    _personaCache.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            Monitor.Exit(_evictionLock);
        }
    }

    internal static IReadOnlyList<string> ChunkMessage(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return [text];
        }

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining.ToString());
                break;
            }

            // Try to split at a newline or space near the limit
            var slice = remaining[..maxLength];
            var splitAt = slice.LastIndexOf('\n');
            if (splitAt < maxLength / 2)
            {
                splitAt = slice.LastIndexOf(' ');
            }
            if (splitAt < maxLength / 2)
            {
                splitAt = maxLength; // Hard split as last resort
            }

            chunks.Add(remaining[..splitAt].ToString());
            remaining = remaining[splitAt..].TrimStart();
        }

        return chunks;
    }

    private ChannelContext BuildChannelContext(SocketCommandContext context)
    {
        var channel = context.Channel;
        var guild = context.Guild as SocketGuild;

        string? channelName = (channel as SocketGuildChannel)?.Name ?? channel.Name;
        string? channelTopic = (channel as SocketTextChannel)?.Topic;
        string? serverName = guild?.Name;
        bool isNsfw = channel is SocketTextChannel textCh && textCh.IsNsfw;
        string? threadName = channel is IThreadChannel thread ? thread.Name : null;
        int? memberCount = guild?.MemberCount;

        // Count recent messages from the bot in this channel to determine when it last spoke
        DateTimeOffset? botLastSpokeAt = null;
        if (_client.CurrentUser is not null)
        {
            var cached = channel.GetCachedMessages(50);
            var lastBotMsg = cached
                .Where(m => m.Author.Id == _client.CurrentUser.Id)
                .MaxBy(m => m.Timestamp);
            botLastSpokeAt = lastBotMsg?.Timestamp;
        }

        // Estimate recent channel activity from cached messages
        var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var recentCount = channel.GetCachedMessages(100)
            .Count(m => m.Timestamp > oneHourAgo);

        return new ChannelContext(
            ChannelName: channelName,
            ChannelTopic: channelTopic,
            ServerName: serverName,
            IsNsfw: isNsfw,
            ThreadName: threadName,
            MemberCount: memberCount,
            RecentMessageCount: recentCount,
            BotLastSpokeAt: botLastSpokeAt
        );
    }

    private static string GetDisplayName(SocketUser user)
    {
        if (user is SocketGuildUser guildUser)
        {
            return guildUser.DisplayName;
        }

        return user.GlobalName ?? user.Username;
    }

    // ── Memory management commands ──────────────────────────────────────

    private async Task HandleForgetMeAsync(SocketCommandContext context)
    {
        await _memoryStore.ForgetAllAsync(context.User.Id, _shutdownCts.Token);
        await context.Channel.SendMessageAsync("Done — I've forgotten everything about you. Fresh start! 🧹");
    }

    private async Task HandleWhatDoYouKnowAsync(SocketCommandContext context)
    {
        var memories = await _memoryStore.GetMemoriesAsync(context.User.Id, _shutdownCts.Token);
        var visible = memories.Where(m => m.Kind != MemoryKind.Suppressed && !m.Superseded).ToList();

        if (visible.Count == 0)
        {
            await context.Channel.SendMessageAsync("I don't have any memories about you yet.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**What I remember about you** ({visible.Count} entries):");
        sb.AppendLine();

        foreach (var kind in new[] { MemoryKind.Factual, MemoryKind.Experiential, MemoryKind.Running, MemoryKind.Meta })
        {
            var group = visible.Where(m => m.Kind == kind).ToList();
            if (group.Count == 0) continue;
            var heading = kind switch
            {
                MemoryKind.Factual => "Facts",
                MemoryKind.Experiential => "Shared moments",
                MemoryKind.Running => "Running bits",
                MemoryKind.Meta => "Preferences",
                _ => kind.ToString()
            };
            sb.AppendLine($"**{heading}**");
            foreach (var m in group)
            {
                var age = Memory.HumanizedAge.Format(DateTimeOffset.UtcNow - m.LastReferencedAt);
                var ctx = string.IsNullOrWhiteSpace(m.Context) ? "" : $" (from {m.Context}, {age})";
                sb.AppendLine($"\u2022 {m.Content}{ctx}");
            }
            sb.AppendLine();
        }

        // Note suppressions separately so the user knows what they've asked the bot to drop.
        var suppressions = memories.Where(m => m.Kind == MemoryKind.Suppressed).ToList();
        if (suppressions.Count > 0)
        {
            sb.AppendLine($"**Topics I'm keeping quiet about**: {string.Join(", ", suppressions.Select(m => m.Content))}");
            sb.AppendLine();
        }

        sb.AppendLine($"_`{_options.CommandPrefix} forget <topic>` to suppress a topic \u00b7 `{_options.CommandPrefix} forget-me` to wipe everything._");

        await SendChunkedAsync(context.Channel, sb.ToString(), null, GetDefaultPersona());
    }

    private async Task HandleForgetTopicAsync(SocketCommandContext context, string topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Length < 2)
        {
            await context.Channel.SendMessageAsync(
                $"Usage: `{_options.CommandPrefix} forget <topic>` \u2014 give me a short topic to stop bringing up (e.g. `cats`, `my ex`).");
            return;
        }

        await _memoryStore.SuppressTopicAsync(context.User.Id, topic, _memoryRelevanceMonitor, _shutdownCts.Token);
        _logger.LogInformation(
            "memory_command action=suppress user={UserHash} topic_len={Len}",
            Memory.Logging.UserIdHash.Hash(context.User.Id), topic.Length);
        await context.Channel.SendMessageAsync(
            $"Got it \u2014 I'll stop bringing up **{topic}**. Use `{_options.CommandPrefix} what-do-you-know` to see what else I've got.");
    }

    // ── Conversation-window memory extraction ──────────────────────────

    /// <summary>
    /// Adds a message to the per-channel buffer and resets (or starts) the debounce timer.
    /// When the timer fires or a cap is hit, the accumulated window is processed in one LLM call.
    /// </summary>
    internal void BufferMessageForExtraction(SocketUserMessage message)
    {
        var channelId = message.Channel.Id;

        var buffer = _channelBuffers.GetOrAdd(channelId, _ => new ChannelMessageBuffer());

        bool shouldFlush = false;

        lock (buffer.Lock)
        {
            var now = DateTimeOffset.UtcNow;

            if (buffer.Messages.Count == 0)
                buffer.FirstMessageAt = now;

            buffer.LastMessageAt = now;
            buffer.Messages.Add(new BufferedMessage(
                message.Author.Id,
                GetDisplayName(message.Author),
                message.Content,
                now));

            // Check hard caps
            if (buffer.Messages.Count >= _options.MaxWindowMessages ||
                (now - buffer.FirstMessageAt) >= _options.MaxWindowDuration)
            {
                shouldFlush = true;
                buffer.DebounceTimer?.Dispose();
                buffer.DebounceTimer = null;
            }
            else
            {
                // Reset debounce timer
                buffer.DebounceTimer?.Dispose();
                buffer.DebounceTimer = new Timer(
                    OnDebounceTimerFired,
                    channelId,
                    _options.ConversationWindowTimeout,
                    Timeout.InfiniteTimeSpan);
            }
        }

        if (shouldFlush)
        {
            _ = ProcessConversationWindowAsync(channelId);
        }
    }

    private void OnDebounceTimerFired(object? state)
    {
        var channelId = (ulong)state!;
        _ = ProcessConversationWindowAsync(channelId);
    }

    /// <summary>
    /// Flushes all pending channel buffers — called during graceful shutdown.
    /// </summary>
    private async Task FlushAllBuffersAsync()
    {
        var channelIds = _channelBuffers.Keys.ToList();
        foreach (var channelId in channelIds)
        {
            try
            {
                await ProcessConversationWindowAsync(channelId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush buffer for channel {ChannelId} during shutdown", channelId);
            }
        }
    }

    /// <summary>
    /// Drains the buffer for a channel and runs a single multi-user extraction pass.
    /// </summary>
    internal async Task ProcessConversationWindowAsync(ulong channelId)
    {
        List<BufferedMessage> messages;

        if (!_channelBuffers.TryGetValue(channelId, out var buffer))
            return;

        lock (buffer.Lock)
        {
            if (buffer.Messages.Count == 0)
                return;

            messages = new List<BufferedMessage>(buffer.Messages);
            buffer.Messages.Clear();
            buffer.DebounceTimer?.Dispose();
            buffer.DebounceTimer = null;
        }

        try
        {
            // Probabilistic rate limiting
            if (_randomProvider.NextDouble() > _options.MemoryExtractionRate)
            {
                _logger.LogDebug("Skipping conversation extraction for channel {ChannelId} (rate limiter)", channelId);
                return;
            }

            // Gather participant info and existing memories
            var participantIds = messages
                .Select(m => m.AuthorId)
                .Distinct()
                .OrderBy(id => id) // Consistent lock ordering to prevent deadlocks
                .ToList();

            // Acquire per-user memory locks for all participants before reading memories.
            // This prevents cross-window races where two windows for the same user read
            // indices concurrently and then apply stale index-based operations.
            var locks = participantIds
                .Select(id => _userMemoryLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)))
                .ToList();

            foreach (var sem in locks)
            {
                await sem.WaitAsync(_shutdownCts.Token);
            }

            try
            {
                var participantMemories = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>();
                foreach (var userId in participantIds)
                {
                    var displayName = messages.First(m => m.AuthorId == userId).AuthorDisplayName;
                    var memories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
                    participantMemories[userId] = (displayName, memories);
                }

                _logger.LogInformation(
                    "Processing conversation window for channel {ChannelId}: {MessageCount} messages, {ParticipantCount} participants",
                    channelId, messages.Count, participantIds.Count);

                var operations = await _orchestrator.ExtractMemoriesFromConversationAsync(
                    messages,
                    participantMemories,
                    _options.MaxMemoriesPerExtraction,
                    _shutdownCts.Token);

                // Filter out operations targeting user IDs not in the participant list
                // (guards against LLM hallucinating user IDs)
                var knownUserIds = participantMemories.Keys.ToHashSet();
                operations = operations.Where(o => knownUserIds.Contains(o.UserId)).ToList();

                await ApplyMultiUserMemoryOperationsAsync(operations);
            }
            finally
            {
                foreach (var sem in locks)
                {
                    sem.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversation-window extraction failed for channel {ChannelId}", channelId);
        }
    }

    private async Task ApplyMultiUserMemoryOperationsAsync(List<MultiUserMemoryOperation> operations)
    {
        // Group by user for efficient dedup
        var byUser = operations.GroupBy(o => o.UserId);

        foreach (var group in byUser)
        {
            var userId = group.Key;
            IReadOnlyList<UserMemory>? existingMemories = null;

            if (group.Any(o => o.Action == MemoryAction.Save))
            {
                existingMemories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
            }

            var userOps = group.ToList();

            // Separate operations by type for correct index adjustment
            var forgetOps = userOps
                .Where(o => o.Action == MemoryAction.Forget && o.MemoryIndex.HasValue)
                .OrderByDescending(o => o.MemoryIndex!.Value)
                .ToList();
            var updateOps = userOps.Where(o => o.Action == MemoryAction.Update).ToList();
            var saveOps = userOps.Where(o => o.Action == MemoryAction.Save).ToList();
            var suppressOps = userOps.Where(o => o.Action == MemoryAction.Suppress).ToList();

            // Collect forget indices for re-indexing updates after forgets
            var forgetIndices = new HashSet<int>(forgetOps.Select(o => o.MemoryIndex!.Value));
            var sortedForgetIndices = forgetOps.Select(o => o.MemoryIndex!.Value).OrderBy(i => i).ToList();

            // Phase 1: Process forgets in descending order (preserves stable indices)
            foreach (var op in forgetOps)
            {
                if (op.Content is not null && _chaosSettingsMonitor.CurrentValue.ContainsBanWord(op.Content))
                {
                    _logger.LogDebug("Memory content contains ban word; skipping");
                    continue;
                }
                await _memoryStore.ForgetMemoryAsync(
                    userId, op.MemoryIndex!.Value, _shutdownCts.Token);
            }

            // Phase 2: Process updates with adjusted indices (account for removed items)
            foreach (var op in updateOps)
            {
                if (op.Content is not null && _chaosSettingsMonitor.CurrentValue.ContainsBanWord(op.Content))
                {
                    _logger.LogDebug("Memory content contains ban word; skipping");
                    continue;
                }
                if (op.MemoryIndex.HasValue)
                {
                    if (forgetIndices.Contains(op.MemoryIndex.Value))
                    {
                        _logger.LogDebug(
                            "Skipping update at index {Index} for user {UserId}: index was forgotten in the same batch",
                            op.MemoryIndex.Value, userId);
                        continue;
                    }
                    // Adjust index: subtract count of forgotten indices below this one
                    var adjustment = sortedForgetIndices.Count(fi => fi < op.MemoryIndex.Value);
                    var adjustedIndex = op.MemoryIndex.Value - adjustment;
                    await _memoryStore.UpdateMemoryAsync(
                        userId, adjustedIndex, op.Content!, op.Context ?? string.Empty, _shutdownCts.Token);
                }
            }

            // Phase 3: Process saves last (dedup against current state)
            foreach (var op in saveOps)
            {
                if (op.Content is not null && _chaosSettingsMonitor.CurrentValue.ContainsBanWord(op.Content))
                {
                    _logger.LogDebug("Memory content contains ban word; skipping");
                    continue;
                }
                if (Memory.InstructionShapePolicy.IsInstructionShaped(op.Content))
                {
                    _logger.LogWarning(
                        "memory_extract_reject instruction_shape user={UserHash}",
                        Memory.Logging.UserIdHash.Hash(userId));
                    continue;
                }
                if (existingMemories is not null && IsDuplicateMemory(op.Content!, existingMemories))
                {
                    _logger.LogInformation("Skipping duplicate memory for user {UserId}: {Content}", userId, op.Content);
                    continue;
                }
                await _memoryStore.SaveMemoryAsync(
                    userId,
                    op.Content!,
                    op.Context ?? string.Empty,
                    op.Kind ?? MemoryKind.Factual,
                    op.Topics,
                    op.Importance,
                    _shutdownCts.Token);
                existingMemories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
            }

            // Phase 4: Suppressions — the model asked us to stop mentioning specific topics.
            foreach (var op in suppressOps)
            {
                if (string.IsNullOrWhiteSpace(op.Content)) continue;
                await _memoryStore.SuppressTopicAsync(
                    userId, op.Content!, _memoryRelevanceMonitor, _shutdownCts.Token);
                _logger.LogInformation(
                    "memory_extract action=suppress user={UserHash}",
                    Memory.Logging.UserIdHash.Hash(userId));
            }

            if (userOps.Count > 0)
            {
                _logger.LogInformation("Processed {Count} memory operation(s) for user {UserId}", userOps.Count, userId);
            }

            // After all operations, check if the user is at or near the memory cap
            // and trigger LLM-based consolidation to compress memories instead of relying on LRU eviction
            if (_options.EnableMemoryConsolidation)
            {
                await TryConsolidateUserMemoriesAsync(userId);
            }
        }
    }

    /// <summary>
    /// Checks whether a user's memory count has reached the cap and, if so,
    /// uses the LLM to consolidate memories down to the target count.
    /// Falls back silently on failure (LRU eviction in the store remains as a safety net).
    /// </summary>
    private async Task TryConsolidateUserMemoriesAsync(ulong userId)
    {
        try
        {
            var memories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
            if (memories.Count < _options.MaxMemoriesPerUser)
                return; // not at cap yet, nothing to do

            var targetCount = Math.Max(1, (int)(_options.MaxMemoriesPerUser * _options.ConsolidationTargetPercent));

            _logger.LogInformation(
                "User {UserId} at memory cap ({Count}/{Max}), attempting LLM consolidation to {Target} memories",
                userId, memories.Count, _options.MaxMemoriesPerUser, targetCount);

            var consolidated = await _orchestrator.ConsolidateMemoriesAsync(
                userId, memories, targetCount, _shutdownCts.Token);

            if (consolidated is not null && consolidated.Count > 0)
            {
                await _memoryStore.ReplaceAllMemoriesAsync(userId, consolidated, _shutdownCts.Token);
                _logger.LogInformation(
                    "Successfully consolidated memories for user {UserId}: {OldCount} → {NewCount}",
                    userId, memories.Count, consolidated.Count);
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.ConsolidationOk,
                    UserHash: UserIdHash.Hash(userId),
                    Before: memories.Count,
                    After: consolidated.Count));
            }
            else
            {
                _logger.LogDebug(
                    "Consolidation returned no results for user {UserId}; LRU eviction will handle overflow",
                    userId);
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.ConsolidationFail,
                    UserHash: UserIdHash.Hash(userId),
                    Before: memories.Count,
                    Reason: "empty_result"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory consolidation failed for user {UserId}; LRU eviction will handle overflow", userId);
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: DateTimeOffset.UtcNow,
                EventType: TelemetryEventTypes.ConsolidationFail,
                UserHash: UserIdHash.Hash(userId),
                Reason: ex.GetType().Name));
        }
    }

    /// <summary>
    /// Checks if a candidate memory is semantically duplicated by any existing memory
    /// using Jaccard similarity on lowercased word sets.
    /// </summary>
    internal static bool IsDuplicateMemory(string candidate, IReadOnlyList<UserMemory> existingMemories, double threshold = 0.7)
    {
        var candidateWords = NormalizeToWordSet(candidate);
        if (candidateWords.Count == 0)
            return false;

        foreach (var existing in existingMemories)
        {
            var existingWords = NormalizeToWordSet(existing.Content);
            if (existingWords.Count == 0)
                continue;

            int intersection = candidateWords.Count(w => existingWords.Contains(w));
            int union = candidateWords.Union(existingWords).Count();

            if (union > 0 && (double)intersection / union >= threshold)
                return true;
        }

        return false;
    }

    private static readonly char[] WordSeparators =
        [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '\u2014', '\u2013', '-'];

    private static HashSet<string> NormalizeToWordSet(string text)
    {
        return text
            .ToLowerInvariant()
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1) // skip single-char noise
            .ToHashSet();
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose all debounce timers
        foreach (var buffer in _channelBuffers.Values)
        {
            lock (buffer.Lock)
            {
                buffer.DebounceTimer?.Dispose();
                buffer.DebounceTimer = null;
            }
        }
        _channelBuffers.Clear();

        // Dispose per-user memory lock semaphores
        // (_userMemoryLocks entries accumulate over time but each is ~80 bytes;
        //  safe eviction during operation is impractical, so we clean up here)
        foreach (var kvp in _userMemoryLocks)
        {
            kvp.Value.Dispose();
        }
        _userMemoryLocks.Clear();

        _shutdownCts.Dispose();
        await _client.DisposeAsync();
    }

    // Image command handler (docs/image_generation_design.md). Rewrite-in-character, then hand the vetted
    // prompt to the shared ImageToolService (budget + style suffix + generate + log), then send the file.
    // Refuses in character on every non-drawing outcome.
    private async Task HandleImageAsync(SocketCommandContext context, SocketUserMessage message, string request)
    {
        var persona = GetDefaultPersona();
        var reference = new MessageReference(message.Id);

        if (string.IsNullOrWhiteSpace(request))
        {
            await context.Channel.SendMessageAsync(
                $"Usage: {_options.CommandPrefix}(image) <what you want me to draw>",
                messageReference: reference);
            return;
        }

        // Feature off or not wired (disabled, no API key, or constructed without image deps in tests):
        // refuse in character rather than falling through to a generic persona reply.
        if (_imageToolService is null || !_imageToolService.IsEnabled || _imageRewriter is null)
        {
            await SendChunkedAsync(context.Channel, ImageRefusals.Disabled, reference, persona);
            return;
        }

        _logger.LogInformation(
            "image_requested author={Author} channel={Channel} message_id={MessageId}",
            message.Author.Id, context.Channel.Name, message.Id);

        // EnterTypingState keeps the typing indicator alive for the whole rewrite + generation, which can
        // run many seconds. Disposed when the using block exits.
        using (context.Channel.EnterTypingState())
        {
            // Load what we know about the requester so "draw me" can personalize, exactly as the
            // model-decided path already does via the orchestrator's inline recall.
            IReadOnlyList<UserMemory>? userMemories = null;
            if (_options.EnableUserMemory)
            {
                userMemories = await _memoryStore.GetAdmissibleMemoriesAsync(
                    context.User.Id, _memoryRelevanceMonitor, _shutdownCts.Token);
            }

            var rewrite = await _imageRewriter.RewriteAsync(
                persona, request, GetDisplayName(context.User), userMemories, _shutdownCts.Token);

            if (rewrite.Refuse || string.IsNullOrWhiteSpace(rewrite.ImagePrompt))
            {
                var refusal = string.IsNullOrWhiteSpace(rewrite.RefusalText)
                    ? ImageRefusals.GenericRefusal
                    : rewrite.RefusalText!;
                await SendChunkedAsync(context.Channel, refusal, reference, persona);
                return;
            }

            // The commissioned tier (gpt-image-2/medium) can take ~70s. Post a single in-character placeholder
            // the instant we commit to drawing, then edit THAT SAME message in place once the render lands, so a
            // commission is one message that transforms from "firing up" into the finished piece rather than a
            // placeholder followed by a separate reply. See ops_analysis P1.
            IUserMessage? placeholder = null;
            try
            {
                placeholder = await context.Channel.SendMessageAsync(
                    ImagePlaceholders.Random(_randomProvider), messageReference: reference);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to post image placeholder; continuing.");
            }

            var outcome = await _imageToolService.GenerateAsync(
                context.User.Id, context.Channel.Name, rewrite.ImagePrompt!, ImageTier.Commissioned, _shutdownCts.Token);

            if (!outcome.Generated || outcome.Bytes is null || outcome.FileName is null)
            {
                // Turn the placeholder into the refusal so we never leave a dangling "firing up" line behind.
                var refusal = outcome.RefusalText ?? ImageRefusals.GenericRefusal;
                if (!await TryEditPlaceholderAsync(placeholder, refusal, persona))
                {
                    await SendChunkedAsync(context.Channel, refusal, reference, persona);
                }
                return;
            }

            var caption = string.IsNullOrWhiteSpace(rewrite.Caption) ? "Behold." : rewrite.Caption;
            if (!await TryEditPlaceholderAsync(placeholder, caption, persona, outcome.Bytes, outcome.FileName))
            {
                // No placeholder to edit (its send failed) or Discord rejected the edit -> post a fresh message.
                await SendChunkedAsync(context.Channel, caption, reference, persona, outcome.Bytes, outcome.FileName);
            }
        }
    }

    /// <summary>
    /// Edits an already-posted placeholder message in place. With image bytes it becomes the finished render
    /// (caption plus attachment); without, it becomes plain text such as a refusal. Returns false when there is
    /// no placeholder, the content is too long, or Discord rejects the edit, so the caller can fall back to a
    /// fresh message. Persona is cached against the message so replies keep character continuity, mirroring the
    /// SendFileAsync path.
    /// </summary>
    private async Task<bool> TryEditPlaceholderAsync(
        IUserMessage? placeholder, string content, string persona,
        byte[]? imageBytes = null, string? fileName = null)
    {
        if (placeholder is null || content.Length > DiscordMaxMessageLength)
        {
            return false;
        }

        try
        {
            if (imageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(fileName))
            {
                using var stream = new MemoryStream(imageBytes);
                await placeholder.ModifyAsync(m =>
                {
                    m.Content = content;
                    m.Attachments = new[] { new FileAttachment(stream, fileName!) };
                });
            }
            else
            {
                await placeholder.ModifyAsync(m => m.Content = content);
            }

            CachePersona(placeholder.Id, persona);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to edit image placeholder; falling back to a fresh message.");
            return false;
        }
    }

    /// <summary>
    /// Detects an obvious scam/phishing link and, if found, replies once (per-channel cooldown) with an
    /// in-character warning. Returns true when the message was handled as a scam so the caller stops normal
    /// processing. Returning true even while on cooldown means raid spam is silently dropped, not echoed.
    /// </summary>
    private async Task<bool> TryHandleScamLinkAsync(SocketUserMessage message)
    {
        ScamDetection detection;
        try
        {
            detection = ScamLinkDetector.Detect(
                message.Content, message.MentionedEveryone,
                _scamGuard.ExtraScamPhrases, _scamGuard.ExtraPhishingHosts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scam detection threw; treating message as clean.");
            return false;
        }

        if (!detection.IsScam)
        {
            return false;
        }

        var channelId = message.Channel.Id;
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _scamGuard.CooldownSeconds));
        if (_scamWarnCooldown.TryGetValue(channelId, out var last) && now - last < cooldown)
        {
            _logger.LogInformation(
                "scam_suppressed channel={Channel} reason={Reason} (cooldown)", message.Channel.Name, detection.Reason);
            return true;
        }

        _scamWarnCooldown[channelId] = now;

        try
        {
            // Reply anchors the warning to the offending message but pings nobody, so the bot never amplifies a
            // mass-mention raid or pesters a possibly-compromised friend.
            var noPing = new AllowedMentions(AllowedMentionTypes.None) { MentionRepliedUser = false };
            await message.Channel.SendMessageAsync(
                ScamWarnings.Random(_randomProvider),
                messageReference: new MessageReference(message.Id),
                allowedMentions: noPing);
            _logger.LogInformation(
                "scam_warned channel={Channel} author={Author} reason={Reason}",
                message.Channel.Name, message.Author.Id, detection.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post scam warning in channel {Channel}", message.Channel.Name);
        }

        return true;
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
    public double NextDouble() => Random.Shared.NextDouble();
}
