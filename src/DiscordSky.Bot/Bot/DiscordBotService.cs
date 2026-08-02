using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Integrations.Reactions;
using DiscordSky.Bot.Integrations.Safety;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using DiscordSky.Bot.Orchestration.Autonomy;
using DiscordSky.Bot.Orchestration.Empire;
using DiscordSky.Bot.Orchestration.Impulse;
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
    private readonly MemoryExtractionOptions _memoryExtractionOptions;
    private readonly MemoryTransitionVerifier _memoryTransitionVerifier;
    private readonly MemoryOpportunityClassifier _memoryOpportunityClassifier;
    private readonly IReactionSink _reactionSink;
    private readonly int _reactionExcerptLength;
    private readonly ReactionJudge? _reactionJudge;
    private readonly IReactionCapabilityRegistry? _reactionCapabilities;
    private readonly bool _reactionCapabilityCooldownEnabled;
    private readonly bool _reactionConstrainedToolEnabled;
    private readonly ImpulseJudge? _impulseJudge;
    private readonly AmbientChannelCoordinator _ambientCoordinator;
    private readonly TimeSpan _ambientReplyQuiet;
    private readonly ChannelPulseTracker? _channelPulse;
    private readonly RecentParticipants? _recentParticipants;
    private readonly EmpireStateStore? _empireState;
    private readonly EmpireTickService? _empireTickService;
    private readonly bool _emojiReactEnabled;
    private readonly int _maxCustomEmotes;
    private readonly TimeSpan _reactMinInterval;
    private readonly TimeSpan _reactQuiet;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastJudgeCall = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastReaction = new();
    private readonly ConcurrentDictionary<ulong, string[]> _recentEmojis = new();
    private const int RecentEmojiMemory = 5;
    private readonly ImageToolService? _imageToolService;
    private readonly ImageRewriter? _imageRewriter;
    private readonly AmbientVisualBudget? _ambientVisualBudget;
    private readonly InteractionEpisodeBuilder? _episodeBuilder;
    private readonly IAmbientEpisodeShadowSink? _ambientEpisodeShadow;
    private readonly InteractionEpisodeOptions _episodeOptions;
    private readonly ScamGuardOptions _scamGuard;
    private readonly IPhishingDomainSource _phishingDomains;
    private readonly RaidTracker _raidTracker;
    private readonly LearnedScamStore? _learnedScams;
    private readonly NewAccountFlagLog _newAccountFlags;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _scamWarnCooldown = new();
    private readonly SentMessageRegistry _sentMessages;
    private readonly ConcurrentDictionary<ulong, ChannelMessageBuffer> _channelBuffers = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _userMemoryLocks = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastSuccessfulExtraction = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly WorldAutonomyRouter? _worldAutonomyRouter;
    private readonly WorldAutonomyAudienceGate? _worldAutonomyAudienceGate;
    internal const int DiscordMaxMessageLength = 2000;

    /// <summary>How much of the room's recent conversation an autonomy run is briefed with.</summary>
    private const int AutonomyHistoryLimit = 15;

    /// <summary>Per-line cap on that briefing, so one wall of text cannot crowd out the rest of the room.</summary>
    private const int AutonomyHistoryLineLength = 300;

    private sealed record AmbientGateDecision(
        SemanticMessageView? MessageView,
        WorthVerdict? Verdict,
        InteractionEpisode? Episode = null,
        EpisodeActionDecision? EpisodeDecision = null,
        InteractionTraceContext? Trace = null,
        bool SuppressAmbient = false,
        string? SuppressReason = null);

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
        ReactionJudge? reactionJudge = null,
        ImageToolService? imageToolService = null,
        ImageRewriter? imageRewriter = null,
        IOptions<ScamGuardOptions>? scamGuardOptions = null,
        IPhishingDomainSource? phishingDomains = null,
        RaidTracker? raidTracker = null,
        LearnedScamStore? learnedScams = null,
        NewAccountFlagLog? newAccountFlags = null,
        RecentParticipants? recentParticipants = null,
        EmpireStateStore? empireState = null,
        EmpireTickService? empireTickService = null,
        ImpulseJudge? impulseJudge = null,
        ChannelPulseTracker? channelPulse = null,
        SentMessageRegistry? sentMessages = null,
        AmbientChannelCoordinator? ambientCoordinator = null,
        AmbientVisualBudget? ambientVisualBudget = null,
        IOptions<MemoryExtractionOptions>? memoryExtractionOptions = null,
        InteractionEpisodeBuilder? episodeBuilder = null,
        IAmbientEpisodeShadowSink? ambientEpisodeShadow = null,
        IOptions<InteractionEpisodeOptions>? episodeOptions = null,
        MemoryTransitionVerifier? memoryTransitionVerifier = null,
        IReactionCapabilityRegistry? reactionCapabilities = null,
        MemoryOpportunityClassifier? memoryOpportunityClassifier = null,
        WorldAutonomyRouter? worldAutonomyRouter = null,
        WorldAutonomyAudienceGate? worldAutonomyAudienceGate = null)
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
        _memoryExtractionOptions = memoryExtractionOptions?.Value ?? new MemoryExtractionOptions();
        _memoryTransitionVerifier = memoryTransitionVerifier ?? new MemoryTransitionVerifier();
        _memoryOpportunityClassifier = memoryOpportunityClassifier ?? new MemoryOpportunityClassifier();
        _reactionSink = reactionSink ?? new NoOpReactionSink();
        _reactionExcerptLength = reactionOptions?.Value.ReplyExcerptLength ?? 200;
        _reactionJudge = reactionJudge;
        _reactionCapabilities = reactionCapabilities;
        _reactionCapabilityCooldownEnabled = reactionOptions?.Value.CapabilityCooldownEnabled ?? false;
        _reactionConstrainedToolEnabled = reactionOptions?.Value.ConstrainedToolEnabled ?? false;
        _emojiReactEnabled = reactionOptions?.Value.EmojiReactEnabled ?? false;
        _maxCustomEmotes = Math.Max(0, reactionOptions?.Value.MaxCustomEmotes ?? 40);
        _reactMinInterval = TimeSpan.FromSeconds(Math.Max(0, reactionOptions?.Value.EmojiReactMinIntervalSeconds ?? 15));
        _reactQuiet = TimeSpan.FromSeconds(Math.Max(0, reactionOptions?.Value.EmojiReactQuietSeconds ?? 90));
        _imageToolService = imageToolService;
        _imageRewriter = imageRewriter;
        _ambientVisualBudget = ambientVisualBudget;
        _episodeBuilder = episodeBuilder;
        _ambientEpisodeShadow = ambientEpisodeShadow;
        _episodeOptions = episodeOptions?.Value ?? new InteractionEpisodeOptions();
        _scamGuard = scamGuardOptions?.Value ?? new ScamGuardOptions { Enabled = false };
        _phishingDomains = phishingDomains ?? NullPhishingDomainSource.Instance;
        _raidTracker = raidTracker ?? new RaidTracker();
        _learnedScams = learnedScams;
        _newAccountFlags = newAccountFlags ?? new NewAccountFlagLog();
        _recentParticipants = recentParticipants;
        _empireState = empireState;
        _empireTickService = empireTickService;
        _impulseJudge = impulseJudge;
        _channelPulse = channelPulse;
        _sentMessages = sentMessages ?? new SentMessageRegistry();
        _ambientCoordinator = ambientCoordinator ?? new AmbientChannelCoordinator();
        _ambientReplyQuiet = TimeSpan.FromSeconds(Math.Max(0, chaosSettings.CurrentValue.AmbientReplyQuietSeconds));
        _worldAutonomyRouter = worldAutonomyRouter;
        _worldAutonomyAudienceGate = worldAutonomyAudienceGate;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Coordination modes: episode={EpisodeMode} deictic_abstention={Deictic} memory_evidence_required={EvidenceRequired} memory_gate={MemoryGate} reaction_tool_constrained={ReactionTool} reaction_capability_cooldown={ReactionCooldown}.",
            _episodeOptions.Mode,
            _episodeOptions.DeicticAbstentionEnabled,
            _memoryExtractionOptions.EvidenceRequired,
            _memoryExtractionOptions.OpportunityGateMode,
            _reactionConstrainedToolEnabled,
            _reactionCapabilityCooldownEnabled);
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
            if (!_sentMessages.TryGet(reaction.MessageId, out var cached)) return; // not our message
            if (_client.CurrentUser is not null && reaction.UserId == _client.CurrentUser.Id) return; // self-react

            // Empire State appraisal: a laugh on one of his lines lifts his mood, a pan sours it. Add only.
            if (action == "add" && _empireState is not null)
            {
                var sentiment = ReactionSentiment.Score(reaction.Emote.Name);
                if (sentiment > 0) _empireState.ApplyMoodDelta(EmpireAppraisal.LaughAtHim);
                else if (sentiment < 0) _empireState.ApplyMoodDelta(EmpireAppraisal.Panned);
            }

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
                ReplyExcerpt: excerpt,
                Source: cached.Source,
                EpisodeId: cached.EpisodeId,
                TriggerMessageId: cached.TriggerMessageId,
                ReplyTargetMessageId: cached.ReplyTargetMessageId));
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
        var exType = message.Exception?.GetType().Name;
        var isExpectedReconnect = exType?.Contains("Reconnect", StringComparison.OrdinalIgnoreCase) == true
            || exType?.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) == true;
        var level = isExpectedReconnect && message.Severity == LogSeverity.Warning
            ? LogLevel.Information
            : MapLogSeverity(message.Severity);
        var text = message.Message
            ?? message.Exception?.Message
            ?? exType
            ?? "<no message>";
        _logger.Log(level, message.Exception, "Discord gateway: {Message}", text);

        // Emit telemetry for gateway disconnects so we can distinguish normal reconnects (~10/day, Discord
        // proactively rotates) from a real problem (auth revoked, network partition). Without this signal
        // a real outage looks identical to housekeeping in kubectl logs.
        if (message.Exception is not null)
        {
            exType = message.Exception.GetType().Name;
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

        // Scam guard runs above the IsBot gate on purpose: bot and webhook accounts are the primary Discord raid
        // vector, and the earlier placement (below this gate) meant automated spam was never scanned. We still
        // skip our own messages and any trusted bots, and the persona flow below still ignores bots entirely.
        var isSelfMessage = _client.CurrentUser is not null && message.Author.Id == _client.CurrentUser.Id;

        // Impulse pipeline: track per-channel activity for the cold-open never-into-silence gate. Record the bot's
        // own messages (self, seen via the gateway echo) and humans; skip other bots. Cheap, in-memory, and for
        // every channel, so an opted-in cold-open channel is tracked even if it is not on the reply allow-list.
        if (_channelPulse is not null)
        {
            var pulseNow = DateTimeOffset.UtcNow;
            if (isSelfMessage) _channelPulse.RecordBot(message.Channel.Id, pulseNow);
            else if (!message.Author.IsBot) _channelPulse.RecordHuman(message.Channel.Id, message.Author.Id, pulseNow);
        }

        if (_scamGuard.Enabled && !isSelfMessage
            && (_scamGuard.ScanBotMessages || !message.Author.IsBot)
            && !_scamGuard.TrustedBotIds.Contains(message.Author.Id)
            && await TryHandleScamLinkAsync(message))
        {
            return;
        }

        // New-account behavioral watch (link-optional). Alerts the mods out of band when a brand-new account posts
        // a payload-bearing message; the real spam threat here often has no parseable link, so the link detector
        // above misses it. Never blocks or bans, and runs before the IsBot return so new bot accounts are covered.
        if (_scamGuard.Enabled && !isSelfMessage && !_scamGuard.TrustedBotIds.Contains(message.Author.Id))
        {
            await TryHandleNewAccountAlertAsync(message);
        }

        if (message.Author.IsBot)
        {
            return;
        }

        var content = message.Content.Trim();
        var hasPrefix = !string.IsNullOrWhiteSpace(_options.CommandPrefix)
            && content.StartsWith(_options.CommandPrefix, StringComparison.OrdinalIgnoreCase);
        var payload = hasPrefix ? content[_options.CommandPrefix.Length..].TrimStart() : string.Empty;
        var mentionsBotDirectly = _client.CurrentUser is not null
            && message.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);
        var autonomyGuildChannel = message.Channel as SocketGuildChannel;
        var autonomyEnabled = _worldAutonomyRouter is not null
            && autonomyGuildChannel is not null
            && _worldAutonomyRouter.IsEnabled(autonomyGuildChannel.Guild.Id);
        var visualIntent = ImageIntentDetector.Classify(content);
        var isLocallyHandledImage = _imageToolService?.IsEnabled == true
            && visualIntent != VisualRequestIntent.None
            && (mentionsBotDirectly || MentionsBotName(content))
            && !autonomyEnabled;
        var autonomyOwnsMessage = ShouldWorldAutonomyOwnMessage(
            hasPrefix,
            payload,
            isLocallyHandledImage);
        bool? repliedToBot = null;
        var recordedForContext = false;

        // World autonomy. In a guild that has granted Robotnik real administrative control, one agent owns
        // the whole opportunity: silence, reaction, speech, and server action. The ordinary ambient/persona
        // machinery must not compete for the same message. Bot-management commands, explicit persona
        // overrides, and image generation keep their specialized local handlers.
        // Deliberately ahead of the ban-word and allow-list gates below: those scope where the bot chats,
        // not where he governs.
        if (autonomyEnabled
            && autonomyGuildChannel is { } autonomyChannel
            && autonomyOwnsMessage)
        {
            RecordMessageForContext(message);
            recordedForContext = true;
            repliedToBot = await IsReplyToBotAsync(message);
            var isDirectAddress = repliedToBot.Value
                || mentionsBotDirectly
                || hasPrefix;
            if (visualIntent != VisualRequestIntent.None)
            {
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.WorldAutonomyVisual,
                    UserHash: UserIdHash.Hash(message.Author.Id),
                    Channel: autonomyChannel.Name,
                    Kind: VisualIntentName(visualIntent),
                    Outcome: "offered",
                    MessageId: message.Id,
                    Reason: isDirectAddress ? "direct" : "ambient"));
            }
            if (!isDirectAddress && _worldAutonomyAudienceGate is not null)
            {
                var pulse = _channelPulse?.Snapshot(message.Channel.Id, TimeSpan.FromMinutes(10));
                var botSpokeRecently = pulse?.LastBotAt is { } lastBotAt
                    ? DateTimeOffset.UtcNow - lastBotAt < TimeSpan.FromMinutes(2)
                    : DidBotSpeakRecently(message.Channel, TimeSpan.FromMinutes(2));
                SemanticMessageView? audienceView = null;
                try
                {
                    audienceView = await _contextAggregator.BuildMessageViewAsync(
                        message,
                        includeHttpUnfurls: true,
                        _shutdownCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not build autonomy audience media context for message {MessageId}.", message.Id);
                }
                var gateDecision = await _worldAutonomyAudienceGate.EvaluateAsync(
                    new WorldAutonomyAudienceRequest(
                        GetDefaultPersona(),
                        GetDisplayName(message.Author),
                        audienceView?.Text ?? content,
                        BuildAutonomyAudienceContext(message, pulse, botSpokeRecently),
                        _empireState is { Enabled: true } ? _empireState.Current.Mood.Label : null,
                        message.Id,
                        autonomyChannel.Name,
                        message.Author.Id,
                        botSpokeRecently,
                        autonomyChannel.Guild.Id,
                        message.Channel.Id,
                        audienceView?.HasMedia == true || message.Attachments.Count > 0 || message.Embeds.Count > 0,
                        audienceView?.MediaContext),
                    _shutdownCts.Token);
                if (gateDecision.Action == WorldAutonomyAudienceAction.Reaction)
                {
                    await MaybeReactInCharacterAsync(message);
                    return;
                }
                if (gateDecision.Action == WorldAutonomyAudienceAction.Silence)
                {
                    return;
                }
            }
            if (await TryHandleWorldAutonomyAsync(autonomyChannel, message, content, isDirectAddress))
            {
                return;
            }
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

        if (!recordedForContext)
        {
            RecordMessageForContext(message);
        }

        var context = new SocketCommandContext(_client, message);

        if (repliedToBot ?? await IsReplyToBotAsync(message))
        {
            _logger.LogDebug("Direct reply detected: {UserId} replied to a bot message.", message.Author.Id);
            await HandleDirectReplyAsync(context, message);
            return;
        }

        if (hasPrefix)
        {
            // Handle memory management commands before normal persona flow
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

            if (payload.StartsWith("scam-report", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("scamreport", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("scam report", StringComparison.OrdinalIgnoreCase))
            {
                await HandleScamReportAsync(context, message, payload);
                return;
            }

            if (payload.Equals("empire", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("empire ", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("empire-", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEmpireCommandAsync(context, message, payload);
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

        // A direct @mention (ping) of the bot is an explicit address, so guarantee a reply instead of leaving
        // it to the ambient dice (a bare ping used to only get a 2.5x ambient nudge and often whiffed). A loose
        // name-drop is not a ping and stays on the ambient path below. Toggle via Bot:RespondToDirectMention.
        var botUserId = _client.CurrentUser?.Id;
        if (ShouldReplyToDirectMention(_options.RespondToDirectMention, botUserId, message.MentionedUsers.Select(u => u.Id)))
        {
            // Drop the bot's own mention token so the model gets a clean topic ("say hi", not "<@123> say hi").
            // A bare ping strips to an empty topic, which routes to free improvisation.
            var topic = StripBotMention(content, botUserId!.Value);
            await HandlePersonaAsync(context, _options.CommandPrefix + " " + topic, message, CreativeInvocationKind.Mention);
            return;
        }

        // Ambient reply chance — modulated by context so the bot interjects at better moments and
        // does not dominate a channel. See docs/improvement_opportunities_2026-06-10.md F7.
        var chaosSettings = _chaosSettingsMonitor.CurrentValue;
        SemanticMessageView? semanticView = null;
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
                    "Ambient roll passed (roll={Roll:F3} < effective={Eff:F3}, base={Base:F3}, botSpokeRecently={Recent}, mentionsBot={Mention}) for message {MessageId} in channel {Channel}.",
                    roll, effectiveChance, chaosSettings.AmbientReplyChance, botSpokeRecently, mentionsBot, message.Id, channelName);

                if (!_ambientCoordinator.TryAcquire(
                        message.Channel.Id, DateTimeOffset.UtcNow, _ambientReplyQuiet,
                        out var ambientLease, out var veto))
                {
                    _logger.LogInformation(
                        "ambient outcome=held reason={Reason} channel={Channel} message={MessageId}",
                        veto, channelName, message.Id);
                    _telemetry.Emit(new TelemetryEvent(
                        Timestamp: DateTimeOffset.UtcNow,
                        EventType: TelemetryEventTypes.ImpulseJudged,
                        UserHash: UserIdHash.Hash(message.Author.Id),
                        Channel: channelName,
                        Outcome: "held",
                        MessageId: message.Id,
                        Reason: veto));
                    return; // one action budget: do not pile an emoji onto an in-flight/recent ambient reply
                }

                var lease = ambientLease!;
                using (lease)
                {
                    var ambientTrace = new InteractionTraceContext(EpisodeId: Guid.NewGuid().ToString("N"));
                    var gate = await EvaluateAmbientWorthGateAsync(message, chaosSettings, ambientTrace);
                    semanticView = gate.MessageView;
                    var visualEnabled = _imageToolService?.IsEnabled == true
                        && _ambientVisualBudget?.Enabled == true;
                    var action = AmbientActionArbiter.Choose(
                        chaosSettings.UseWorthGate,
                        gate.Verdict,
                        chaosSettings.AmbientWorthThreshold,
                        visualEnabled,
                        _ambientVisualBudget?.WorthThreshold ?? 1.0,
                        _ambientVisualBudget?.MinLead ?? 0.0);
                    string? decisionReason = gate.SuppressReason;
                    if (gate.SuppressAmbient)
                    {
                        action = AmbientActionKind.Silence;
                    }
                    else if (_episodeOptions.DeicticAbstentionEnabled
                        && gate.Episode?.ReferentRequirement.IsRequired == true
                        && gate.EpisodeDecision?.ReferentDecision.SelectedMessageId is null)
                    {
                        action = AmbientActionKind.Silence;
                        decisionReason = "referent_unresolved";
                    }
                    AmbientVisualLease? visualLease = null;
                    if (action == AmbientActionKind.Image)
                    {
                        var guildId = (context.Guild as SocketGuild)?.Id;
                        var visualVeto = guildId is null ? "no_guild" : null;
                        if (guildId is null
                            || _ambientVisualBudget?.TryAcquire(
                                guildId.Value,
                                DateTimeOffset.UtcNow,
                                out visualLease,
                                out visualVeto) != true)
                        {
                            decisionReason = $"visual_{visualVeto ?? "unavailable"}";
                            action = gate.Verdict is null
                                ? AmbientActionKind.Text
                                : AmbientActionArbiter.FallbackAfterImageVeto(
                                    gate.Verdict, chaosSettings.AmbientWorthThreshold);
                        }
                    }

                    if (gate.Verdict is not null)
                    {
                        _telemetry.Emit(new TelemetryEvent(
                            Timestamp: DateTimeOffset.UtcNow,
                            EventType: TelemetryEventTypes.ImpulseJudged,
                            UserHash: UserIdHash.Hash(message.Author.Id),
                            Channel: message.Channel.Name,
                            Outcome: action.ToString().ToLowerInvariant(),
                            TopScore: gate.Verdict.Worth,
                            MessageId: message.Id,
                            Note: gate.Verdict.Thought,
                            Reason: decisionReason,
                            VisualWorth: gate.Verdict.VisualWorth,
                            VisualHook: gate.Verdict.VisualHook,
                            EpisodeId: gate.Trace?.EpisodeId,
                            EpisodeSchemaVersion: gate.Trace?.EpisodeSchemaVersion,
                            Stage: gate.Episode is null ? "legacy_judge" : "episode_judge",
                            ReasonCode: decisionReason ?? gate.EpisodeDecision?.ReferentDecision.ReasonCode,
                            ReferentMessageId: gate.EpisodeDecision?.ReferentDecision.SelectedMessageId,
                            ContextMessageCount: gate.Episode?.Messages.Count,
                            EvidenceMask: gate.Episode?.EvidenceMask.ToString(),
                            EvidenceDigest: gate.Trace?.EvidenceDigest,
                            ProjectionDigest: gate.Trace?.ProjectionDigest));
                        _logger.LogInformation(
                            "impulse_judged action={Action} text_worth={TextWorth:F2} visual_worth={VisualWorth:F2} reason={Reason} channel={Channel}",
                            action, gate.Verdict.Worth, gate.Verdict.VisualWorth, decisionReason ?? "-", message.Channel.Name);
                    }

                    var hasExplicitReply = message.Reference?.MessageId.IsSpecified == true;
                    var referentRequirement = AmbientReferentDetector.Detect(
                        gate.MessageView?.Text ?? content,
                        hasExplicitReply,
                        gate.MessageView?.HasMedia == true || message.Attachments.Count > 0);
                    var priorityShadowCapture = hasExplicitReply || referentRequirement.IsRequired;
                    if (_episodeBuilder is not null
                        && _ambientEpisodeShadow?.ShouldCapture(priorityShadowCapture) == true)
                    {
                        _ = CaptureAmbientEpisodeShadowAsync(
                            message,
                            gate.MessageView,
                            ambientTrace,
                            gate.Verdict,
                            action,
                            chaosSettings.AmbientWorthThreshold,
                            visualEnabled,
                            _ambientVisualBudget?.WorthThreshold ?? 1.0,
                            _ambientVisualBudget?.MinLead ?? 0.0,
                            priorityShadowCapture);
                    }

                    if (action != AmbientActionKind.Silence)
                    {
                        using (visualLease)
                        {
                            var actionMode = action == AmbientActionKind.Image
                                ? CreativeActionMode.ImageRequired
                                : CreativeActionMode.TextOnly;
                            var sent = await HandlePersonaAsync(
                                context, _options.CommandPrefix + " " + content, message,
                                CreativeInvocationKind.Ambient, semanticView, actionMode,
                                gate.Verdict?.VisualWorth, gate.Verdict?.VisualHook,
                                trace: gate.Trace ?? ambientTrace,
                                episode: gate.Episode,
                                episodeDecision: gate.EpisodeDecision);
                            if (sent)
                            {
                                var sentAt = DateTimeOffset.UtcNow;
                                lease.MarkSent(sentAt);
                                visualLease?.MarkSucceeded(sentAt);
                            }
                        }
                        return;
                    }
                }
                // The worth gate judged this moment not worth a full reply; fall through to a possible reaction.
            }
        }

        // He held his tongue this turn: maybe editorialize with a single in-character emoji reaction, chosen
        // by a cheap LLM. Rare, per-channel throttled, and off the reply path, so it stays presence-not-noise.
        await MaybeReactInCharacterAsync(message, semanticView);
    }

    /// <summary>
    /// The inner-thought worth gate for ambient replies. When enabled, an ambient candidate that has already
    /// passed the cheap probability roll is scored by one cheap LLM call: it becomes a reply only if the
    /// character genuinely has a good line (worth >= threshold). This replaces the blind dice with a quality
    /// judgment (Inner Thoughts, arXiv:2501.00383); the roll stays as the cost bound and MaxPromptsPerHour as
    /// the hard frequency cap downstream. Fail-open: a disabled or broken judge never suppresses a reply the
    /// roll already granted. Emits an impulse_judged telemetry row per real judgment so the threshold can be
    /// tuned on live data.
    /// </summary>
    private async Task<AmbientGateDecision> EvaluateAmbientWorthGateAsync(
        SocketUserMessage message, ChaosSettings chaos, InteractionTraceContext? trace = null)
    {
        var liveEpisode = _episodeOptions.Mode == InteractionEpisodeMode.Live;
        if (!liveEpisode && (!chaos.UseWorthGate || _impulseJudge is null))
        {
            return new AmbientGateDecision(null, null, Trace: trace);
        }

        SemanticMessageView? messageView = null;
        try
        {
            messageView = await _contextAggregator.BuildMessageViewAsync(
                message, includeHttpUnfurls: true, _shutdownCts.Token);
            var authorName = (message.Author as SocketGuildUser)?.DisplayName ?? message.Author.Username;
            var mood = _empireState is { Enabled: true } ? _empireState.Current.Mood.Label : null;

            if (liveEpisode)
            {
                if (_episodeBuilder is null)
                {
                    return await HandleLiveEpisodeFailureAsync(
                        message,
                        messageView,
                        authorName,
                        mood,
                        trace,
                        chaos,
                        "builder_unavailable",
                        null);
                }

                var evidence = new EpisodeTriggerEvidence(
                    message.Channel.Id,
                    message.Id,
                    message.Author.Id,
                    authorName,
                    message.Reference?.MessageId.IsSpecified == true ? message.Reference.MessageId.Value : null,
                    messageView);
                var build = await _episodeBuilder.BuildAsync(evidence, trace?.EpisodeId, _shutdownCts.Token);
                if (!build.IsSuccess)
                {
                    return await HandleLiveEpisodeFailureAsync(
                        message,
                        messageView,
                        authorName,
                        mood,
                        trace,
                        chaos,
                        build.Failure?.ReasonCode ?? "build_failed",
                        build.Failure?.Detail);
                }

                var episode = build.Episode!;
                var projection = EpisodeProjectionBuilder.BuildJudgeProjection(episode, mood);
                var liveTrace = (trace ?? new InteractionTraceContext(EpisodeId: episode.EpisodeId)) with
                {
                    EpisodeId = episode.EpisodeId,
                    EpisodeSchemaVersion = episode.SchemaVersion,
                    EvidenceDigest = episode.Fingerprint.EvidenceDigest,
                    ProjectionDigest = projection.ProjectionDigest,
                };
                EmitEpisodeStage(message, episode, "created", "live_capture", null, liveTrace);

                WorthVerdict? verdict = null;
                if (chaos.UseWorthGate && _impulseJudge is not null)
                {
                    verdict = await _impulseJudge.JudgeAmbientAsync(new AmbientImpulseRequest(
                        PersonaName: GetDefaultPersona(),
                        AuthorDisplayName: authorName,
                        MessageText: messageView.Text,
                        Context: null,
                        MoodLabel: mood,
                        MediaContext: messageView.MediaContext,
                        MessageId: message.Id,
                        EpisodeProjection: projection.Text,
                        ReferentCandidateIds: episode.ReferentCandidates.Select(candidate => candidate.MessageId).ToArray(),
                        Trace: liveTrace),
                        _shutdownCts.Token);
                }

                var referent = ImpulseJudge.ValidateReferentDecision(
                    verdict ?? new WorthVerdict(0.0, string.Empty),
                    episode,
                    _episodeOptions.ReferentConfidenceThreshold);
                return new AmbientGateDecision(
                    messageView,
                    verdict,
                    episode,
                    new EpisodeActionDecision(referent),
                    liveTrace);
            }

            var legacyVerdict = await JudgeLegacyAmbientAsync(
                message,
                messageView,
                authorName,
                mood,
                trace);
            return new AmbientGateDecision(messageView, legacyVerdict, Trace: trace);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (liveEpisode && _episodeOptions.OnBuildError == EpisodeFailurePolicy.SilenceAmbient)
            {
                _logger.LogWarning(ex, "Live ambient episode path failed; suppressing ambient action.");
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.InteractionEpisode,
                    UserHash: UserIdHash.Hash(message.Author.Id),
                    Channel: message.Channel.Name,
                    Outcome: "build_failed",
                    MessageId: message.Id,
                    ReasonCode: "live_exception",
                    FailureClass: ex.GetType().Name,
                    EpisodeId: trace?.EpisodeId,
                    Stage: "live_capture"));
                return new AmbientGateDecision(
                    messageView,
                    null,
                    Trace: trace,
                    SuppressAmbient: true,
                    SuppressReason: "episode_build_failed");
            }

            // Legacy behavior and the explicit compatibility fallback remain fail-open.
            _logger.LogDebug(ex, "Ambient worth gate failed; allowing the reply.");
            return new AmbientGateDecision(messageView, null, Trace: trace);
        }
    }

    private async Task<AmbientGateDecision> HandleLiveEpisodeFailureAsync(
        SocketUserMessage message,
        SemanticMessageView messageView,
        string authorName,
        string? mood,
        InteractionTraceContext? trace,
        ChaosSettings chaos,
        string reasonCode,
        string? failureClass)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.InteractionEpisode,
            UserHash: UserIdHash.Hash(message.Author.Id),
            Channel: message.Channel.Name,
            Outcome: "build_failed",
            MessageId: message.Id,
            ReasonCode: reasonCode,
            FailureClass: failureClass,
            EpisodeId: trace?.EpisodeId,
            Stage: "live_capture"));

        if (_episodeOptions.OnBuildError != EpisodeFailurePolicy.UseLegacyPath)
        {
            return new AmbientGateDecision(
                messageView,
                null,
                Trace: trace,
                SuppressAmbient: true,
                SuppressReason: "episode_build_failed");
        }

        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.InteractionEpisode,
            UserHash: UserIdHash.Hash(message.Author.Id),
            Channel: message.Channel.Name,
            Outcome: "legacy_fallback",
            MessageId: message.Id,
            ReasonCode: reasonCode,
            EpisodeId: trace?.EpisodeId,
            Stage: "episode_legacy_fallback"));
        var verdict = chaos.UseWorthGate && _impulseJudge is not null
            ? await JudgeLegacyAmbientAsync(message, messageView, authorName, mood, trace)
            : null;
        return new AmbientGateDecision(messageView, verdict, Trace: trace);
    }

    private Task<WorthVerdict?> JudgeLegacyAmbientAsync(
        SocketUserMessage message,
        SemanticMessageView messageView,
        string authorName,
        string? mood,
        InteractionTraceContext? trace)
    {
        if (_impulseJudge is null) return Task.FromResult<WorthVerdict?>(null);
        return _impulseJudge.JudgeAmbientAsync(new AmbientImpulseRequest(
            PersonaName: GetDefaultPersona(),
            AuthorDisplayName: authorName,
            MessageText: messageView.Text,
            Context: BuildReactionContext(message),
            MoodLabel: mood,
            MediaContext: messageView.MediaContext,
            MessageId: message.Id,
            Trace: trace),
            _shutdownCts.Token);
    }

    private void EmitEpisodeStage(
        SocketUserMessage message,
        InteractionEpisode episode,
        string outcome,
        string stage,
        string? reasonCode,
        InteractionTraceContext trace)
    {
        var oldestAge = episode.Messages.Count == 0
            ? 0
            : (long)Math.Max(0, (episode.CapturedAt - episode.Messages.Min(item => item.Timestamp)).TotalMilliseconds);
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.InteractionEpisode,
            UserHash: UserIdHash.Hash(message.Author.Id),
            Channel: message.Channel.Name,
            Outcome: outcome,
            Count: episode.ReferentCandidates.Count,
            MessageId: message.Id,
            EpisodeId: episode.EpisodeId,
            EpisodeSchemaVersion: episode.SchemaVersion,
            Stage: stage,
            ReasonCode: reasonCode,
            ContextMessageCount: episode.Messages.Count,
            OldestContextAgeMs: oldestAge,
            EvidenceMask: episode.EvidenceMask.ToString(),
            EvidenceDigest: trace.EvidenceDigest,
            ProjectionDigest: trace.ProjectionDigest));
    }

    private async Task CaptureAmbientEpisodeShadowAsync(
        SocketUserMessage message,
        SemanticMessageView? messageView,
        InteractionTraceContext trace,
        WorthVerdict? baselineVerdict,
        AmbientActionKind baselineAction,
        double textThreshold,
        bool visualEnabled,
        double visualThreshold,
        double visualMinLead,
        bool prioritySample)
    {
        try
        {
            messageView ??= await _contextAggregator.BuildMessageViewAsync(
                message,
                includeHttpUnfurls: true,
                _shutdownCts.Token);
            var authorName = (message.Author as SocketGuildUser)?.DisplayName ?? message.Author.Username;
            var evidence = new EpisodeTriggerEvidence(
                message.Channel.Id,
                message.Id,
                message.Author.Id,
                authorName,
                message.Reference?.MessageId.IsSpecified == true ? message.Reference.MessageId.Value : null,
                messageView);
            var result = await _episodeBuilder!.BuildAsync(
                evidence,
                trace.EpisodeId,
                _shutdownCts.Token);
            if (!result.IsSuccess)
            {
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.AmbientEpisodeShadow,
                    UserHash: UserIdHash.Hash(message.Author.Id),
                    Channel: message.Channel.Name,
                    Outcome: "build_failed",
                    MessageId: message.Id,
                    ReasonCode: result.Failure?.ReasonCode,
                    FailureClass: result.Failure?.Detail,
                    EpisodeId: trace.EpisodeId,
                    Stage: "capture"));
                return;
            }

            _ambientEpisodeShadow!.TryEnqueue(new AmbientEpisodeShadowOpportunity(
                result.Episode!,
                message.Channel.Name,
                GetDefaultPersona(),
                _empireState is { Enabled: true } ? _empireState.Current.Mood.Label : null,
                baselineVerdict,
                baselineAction,
                textThreshold,
                visualEnabled,
                visualThreshold,
                visualMinLead,
                prioritySample));
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: DateTimeOffset.UtcNow,
                EventType: TelemetryEventTypes.AmbientEpisodeShadow,
                UserHash: UserIdHash.Hash(message.Author.Id),
                Channel: message.Channel.Name,
                Outcome: "build_failed",
                MessageId: message.Id,
                ReasonCode: "capture_exception",
                FailureClass: ex.GetType().Name,
                EpisodeId: trace.EpisodeId,
                Stage: "capture"));
            _logger.LogDebug(ex, "Ambient episode shadow capture failed for message {MessageId}", message.Id);
        }
    }

    /// <summary>
    /// When the bot chose not to reply, occasionally add a single in-character emoji reaction, chosen by a
    /// cheap LLM "reaction judge" that renders the persona's verdict on the message (or declines, the common
    /// case). A per-channel cooldown bounds how often a judge call is spent (whether it reacts or declines),
    /// so an active channel triggers at most one call per window. The judge's choice is validated against the
    /// offered emoji set before reacting. Custom server emotes need a guild, so DMs get nothing. Fail-open.
    /// </summary>
    private async Task MaybeReactInCharacterAsync(
        SocketUserMessage message, SemanticMessageView? semanticView = null)
    {
        if (!_emojiReactEnabled || _reactionJudge is null)
        {
            return;
        }

        // Custom emotes (and the whole feature) need a guild; skip DMs and group channels.
        if (message.Channel is not SocketGuildChannel guildChannel)
        {
            return;
        }

        if (TryGetActiveReactionBlock(
                _reactionCapabilityCooldownEnabled,
                _reactionCapabilities,
                guildChannel.Guild.Id,
                message.Author.Id,
                out var activeBlock))
        {
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: DateTimeOffset.UtcNow,
                EventType: TelemetryEventTypes.ReactionCapabilityVeto,
                UserHash: UserIdHash.Hash(message.Author.Id),
                Channel: message.Channel.Name,
                Outcome: "blocked",
                Count: activeBlock.FailureCount,
                MessageId: message.Id,
                ReasonCode: "reaction_blocked",
                ProviderErrorCode: activeBlock.DiscordCode,
                ExpiresAt: activeBlock.ExpiresAt));
            return;
        }

        var channelId = message.Channel.Id;
        var now = DateTimeOffset.UtcNow;
        // Cost guard: spend at most one judge call per interval per channel (a react OR a decline both count),
        // so a busy channel can't hammer the LLM.
        if (_lastJudgeCall.TryGetValue(channelId, out var lastCall) && now - lastCall < _reactMinInterval)
        {
            return;
        }
        // Taste guard: after he ACTUALLY reacts, stay quiet a while so he never carpet-reacts. A decline does
        // not start this, so passing on a mundane message never blinds him to a great one right after.
        if (_lastReaction.TryGetValue(channelId, out var lastReact) && now - lastReact < _reactQuiet)
        {
            return;
        }
        _lastJudgeCall[channelId] = now;

        try
        {
            semanticView ??= await _contextAggregator.BuildMessageViewAsync(
                message, includeHttpUnfurls: false, _shutdownCts.Token);
            var authorName = (message.Author as SocketGuildUser)?.DisplayName ?? message.Author.Username;
            _recentEmojis.TryGetValue(channelId, out var recentEmojis);

            var (allowed, tokenToEmote) = BuildAllowedReactions(
                guildChannel.Guild, authorName, semanticView.Text, recentEmojis);
            if (allowed.Count == 0)
            {
                return;
            }

            IReadOnlyList<UserMemory>? authorMemories = null;
            if (_options.EnableUserMemory)
            {
                // Personalize: hand the cheap judge what Robotnik knows about this author so it can react to the
                // person, not just the words (running gags, roastable facts). Ranked to the message downstream.
                authorMemories = await _memoryStore.GetAdmissibleMemoriesAsync(
                    message.Author.Id, _memoryRelevanceMonitor, _shutdownCts.Token);
            }

            var request = new ReactionRequest(
                PersonaName: GetDefaultPersona(),
                AuthorDisplayName: authorName,
                MessageText: semanticView.Text,
                Context: BuildReactionContext(message),
                Allowed: allowed,
                AuthorMemories: authorMemories,
                RecentEmojis: recentEmojis,
                MediaContext: semanticView.MediaContext,
                MessageId: message.Id);

            var decision = await _reactionJudge.JudgeAsync(request, _shutdownCts.Token);
            var verdict = decision.Verdict;

            IEmote? emote = null;
            var reacted = decision.Kind == ReactionDecisionKind.React
                && verdict is not null
                && tokenToEmote.TryGetValue(verdict.Token, out emote);

            if (!reacted)
            {
                var outcome = decision.Kind switch
                {
                    ReactionDecisionKind.Decline => "decline",
                    ReactionDecisionKind.Invalid => "invalid_token",
                    ReactionDecisionKind.Failed => "failed",
                    _ => "invalid_token", // valid judge token missing from the runtime map
                };
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.ReactionJudged,
                    UserHash: UserIdHash.Hash(message.Author.Id),
                    Channel: message.Channel.Name,
                    Kind: verdict?.Token,
                    Outcome: outcome,
                    MessageId: message.Id,
                    Reason: decision.Kind == ReactionDecisionKind.React
                        ? "token_not_mapped"
                        : decision.Rationale));
                return; // he deigned not to react (a normal, common outcome), or the token was unknown
            }

            try
            {
                await message.AddReactionAsync(emote!);
            }
            catch (Discord.Net.HttpException ex)
            {
                var failure = ReactionDeliveryFailureClassifier.Classify(
                    (int)ex.HttpCode,
                    ex.DiscordCode.HasValue ? (int)ex.DiscordCode.Value : null);
                EmitReactionDeliveryFailure(message, verdict!, failure, ex.Reason, ex);
                if (failure.IsCapabilityBlock && failure.DiscordCode.HasValue)
                {
                    var state = _reactionCapabilities?.RecordExactBlock(
                        guildChannel.Guild.Id,
                        message.Author.Id,
                        failure.DiscordCode.Value);
                    if (state is not null)
                    {
                        EmitReactionCapabilityTransition(message, state, "blocked");
                    }
                }
                return;
            }
            catch (HttpRequestException ex)
            {
                var failure = ReactionDeliveryFailureClassifier.Classify(
                    httpStatus: null,
                    discordCode: null,
                    isTransportFailure: true);
                EmitReactionDeliveryFailure(message, verdict!, failure, ex.GetType().Name, ex);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EmitReactionDeliveryFailure(
                    message,
                    verdict!,
                    ReactionDeliveryFailureClassifier.Unexpected(),
                    ex.GetType().Name,
                    ex);
                return;
            }

            _lastReaction[channelId] = now;
            if (_reactionCapabilityCooldownEnabled
                && _reactionCapabilities?.Clear(guildChannel.Guild.Id, message.Author.Id) == true)
            {
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.ReactionCapabilityTransition,
                    UserHash: UserIdHash.Hash(message.Author.Id),
                    Channel: message.Channel.Name,
                    Outcome: "cleared",
                    MessageId: message.Id,
                    ReasonCode: "delivery_succeeded"));
            }
            RecordRecentEmoji(channelId, verdict!.Token);
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: DateTimeOffset.UtcNow,
                EventType: TelemetryEventTypes.ReactionJudged,
                UserHash: UserIdHash.Hash(message.Author.Id),
                Channel: message.Channel.Name,
                Kind: verdict.Token,
                Outcome: "react",
                MessageId: message.Id));

            // Human reception changes Empire mood. Robotnik's own editorial choice does not reward itself.
            _logger.LogInformation(
                "in_character_reaction emote={Emote} custom={Custom} channel={Channel} message={MessageId} mem={Mem} why={Why}",
                verdict!.Token, emote is Emote, message.Channel.Name, message.Id, authorMemories?.Count ?? 0, verdict!.Rationale);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "In-character reaction failed; ignoring.");
        }
    }

    private void EmitReactionDeliveryFailure(
        SocketUserMessage message,
        ReactionVerdict verdict,
        ReactionDeliveryFailure failure,
        string? detail,
        Exception exception)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.ReactionJudged,
            UserHash: UserIdHash.Hash(message.Author.Id),
            Channel: message.Channel.Name,
            Kind: verdict.Token,
            Outcome: "failed",
            MessageId: message.Id,
            Reason: string.IsNullOrWhiteSpace(detail) ? exception.GetType().Name : detail,
            ReasonCode: failure.ReasonCode,
            HttpStatus: failure.HttpStatus,
            ProviderErrorCode: failure.DiscordCode,
            FailureClass: exception.GetType().Name));
        _logger.LogWarning(
            exception,
            "In-character reaction delivery failed: emote={Emote} channel={Channel} message={MessageId} reason={ReasonCode} http={HttpStatus} discord={DiscordCode}",
            verdict.Token,
            message.Channel.Name,
            message.Id,
            failure.ReasonCode,
            failure.HttpStatus,
            failure.DiscordCode);
    }

    private void EmitReactionCapabilityTransition(
        SocketUserMessage message,
        ReactionCapabilityState state,
        string outcome)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.ReactionCapabilityTransition,
            UserHash: UserIdHash.Hash(message.Author.Id),
            Channel: message.Channel.Name,
            Outcome: outcome,
            Count: state.FailureCount,
            MessageId: message.Id,
            ReasonCode: "reaction_blocked",
            ProviderErrorCode: state.DiscordCode,
            ExpiresAt: state.ExpiresAt));
    }

    internal static bool TryGetActiveReactionBlock(
        bool enabled,
        IReactionCapabilityRegistry? registry,
        ulong guildId,
        ulong userId,
        out ReactionCapabilityState state)
    {
        state = null!;
        return enabled
            && registry is not null
            && registry.TryGetActive(guildId, userId, out state);
    }

    /// <summary>
    /// One line of context for the judge: the message this one is replying to, if any (bounded downstream,
    /// and clearly labelled as the referenced message, not the target). Null when it is not a reply or the
    /// parent has no text. Helps him read "lol same" / "that's rough" against what it answers.
    /// </summary>
    private static string? BuildReactionContext(SocketUserMessage message)
    {
        var referenced = message.ReferencedMessage;
        if (referenced is null)
        {
            return null;
        }
        var content = referenced.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }
        var author = (referenced.Author as SocketGuildUser)?.DisplayName ?? referenced.Author?.Username ?? "someone";
        return $"{author}: {content}";
    }

    /// <summary>Remembers the last few emojis he used in a channel (most recent first) so the judge can be nudged toward variety.</summary>
    private void RecordRecentEmoji(ulong channelId, string token)
    {
        _recentEmojis.AddOrUpdate(
            channelId,
            _ => new[] { token },
            (_, existing) => new[] { token }.Concat(existing).Take(RecentEmojiMemory).ToArray());
    }

    /// <summary>Owner/mod-only Empire State observability: `!sky empire` inspects the live log and mood; `!sky empire-tick` forces a tick now.</summary>
    private async Task HandleEmpireCommandAsync(SocketCommandContext context, SocketUserMessage message, string payload)
    {
        if ((message.Author as SocketGuildUser)?.GuildPermissions.ManageMessages != true)
        {
            return; // silently ignore for non-mods
        }
        if (_empireState is null || !_empireState.Enabled)
        {
            await context.Channel.SendMessageAsync("Empire state is not enabled.");
            return;
        }

        var arg = payload.Length > "empire".Length ? payload["empire".Length..].TrimStart(' ', '-') : string.Empty;
        if (arg.Equals("tick", StringComparison.OrdinalIgnoreCase))
        {
            if (_empireTickService is null)
            {
                await context.Channel.SendMessageAsync("Tick service unavailable.");
                return;
            }
            var outcome = await _empireTickService.ForceTickAsync(_shutdownCts.Token);
            await context.Channel.SendMessageAsync($"Forced a tick: **{outcome}** (mood now: {_empireState.Current.Mood.Label}).");
            return;
        }

        var s = _empireState.Current;
        var ranks = s.Ranks.Count == 0 ? "none" : string.Join(", ", s.Ranks.Select(r => $"{r.Name}={r.Title}"));
        var body = s.Body.Length > 1400 ? s.Body[..1400] + " [...]" : s.Body;
        var summary =
            $"**Empire State v{s.Version}** | mood: **{s.Mood.Label}** (v={s.Mood.Valence:F2}, a={s.Mood.Arousal:F2}) | ranks: {ranks}\n" +
            $"```\n{body}\n```";
        if (summary.Length > 1900) summary = summary[..1900];
        await context.Channel.SendMessageAsync(summary);
    }

    /// <summary>
    /// Builds the emoji set offered to the reaction judge: the unicode palette plus up to
    /// <see cref="_maxCustomEmotes"/> of the guild's custom emotes. Returns both the descriptors (for the
    /// prompt) and a case-insensitive token-to-emote map (for validating and posting the choice). A custom
    /// emote whose name collides with a unicode token is skipped so the unicode meaning stays intact.
    /// </summary>
    private (IReadOnlyList<AllowedEmote> Allowed, Dictionary<string, IEmote> Map) BuildAllowedReactions(
        SocketGuild guild, string authorName, string messageText, IReadOnlyCollection<string>? recentTokens)
    {
        var allowed = new List<AllowedEmote>(RobotnikReactions.Unicode.Count + _maxCustomEmotes);
        var map = new Dictionary<string, IEmote>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in RobotnikReactions.Unicode)
        {
            if (map.TryAdd(e.Token, new Emoji(e.Emoji)))
            {
                allowed.Add(new AllowedEmote(e.Token, e.Meaning, IsCustom: false));
            }
        }

        if (_maxCustomEmotes > 0 && guild is not null)
        {
            // Index the guild's customs by name, skipping blanks and any that collide with a unicode token.
            var byName = new Dictionary<string, IEmote>(StringComparer.OrdinalIgnoreCase);
            foreach (var emote in guild.Emotes)
            {
                if (string.IsNullOrWhiteSpace(emote.Name) || map.ContainsKey(emote.Name))
                {
                    continue;
                }
                byName.TryAdd(emote.Name, emote); // GuildEmote is an IEmote, usable directly for AddReactionAsync.
            }

            // Surface a small, varied, author/message-relevant slice instead of the arbitrary first N (round-6
            // telemetry: 138 customs offered undescribed -> the judge picked zero; now the candidates rotate).
            var selected = ReactionSelection.SelectCustomEmoteNames(
                new List<string>(byName.Keys), authorName, messageText, recentTokens, _maxCustomEmotes, _randomProvider.NextDouble);
            foreach (var name in selected)
            {
                if (byName.TryGetValue(name, out var emote) && map.TryAdd(name, emote))
                {
                    allowed.Add(new AllowedEmote(name, string.Empty, IsCustom: true));
                }
            }
        }

        return (allowed, map);
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

    /// <summary>
    /// True when a message is a direct @mention (ping) of the bot and direct-mention replies are enabled.
    /// A loose name-drop is deliberately excluded (that stays on the ambient path); only an actual ping,
    /// where the bot's user id is among the message's mentioned users, guarantees a reply. Pure for tests.
    /// </summary>
    internal static bool ShouldReplyToDirectMention(bool enabled, ulong? botUserId, IEnumerable<ulong> mentionedUserIds)
        => enabled && botUserId.HasValue && mentionedUserIds.Contains(botUserId.Value);

    /// <summary>
    /// Removes the bot's own @mention token (<c>&lt;@id&gt;</c> or the nickname form <c>&lt;@!id&gt;</c>) from a
    /// message so a direct ping yields a clean topic. Collapses the extra spaces the removal leaves behind while
    /// keeping newlines, and leaves other users' mention tokens untouched. Pure for testability.
    /// </summary>
    internal static string StripBotMention(string content, ulong botUserId)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var stripped = content
            .Replace($"<@{botUserId}>", string.Empty)
            .Replace($"<@!{botUserId}>", string.Empty);

        while (stripped.Contains("  "))
        {
            stripped = stripped.Replace("  ", " ");
        }

        return stripped.Trim();
    }

    /// <summary>
    /// Fetches the message a request is replying to (if any) and formats it as grounding context, so an image
    /// request like "draw this" can resolve "this" to the referenced content. Returns null when not a reply or
    /// the referent has no usable text. I/O; formatting is delegated to the pure <see cref="FormatReferencedContext"/>.
    /// </summary>
    private async Task<string?> TryGetReferencedContextAsync(SocketUserMessage message)
    {
        if (message.Reference?.MessageId.IsSpecified != true)
        {
            return null;
        }

        try
        {
            var referenced = message.ReferencedMessage
                ?? await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);
            return FormatReferencedContext(referenced?.Author?.Username, referenced?.Content);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch referenced message for image context.");
            return null;
        }
    }

    /// <summary>
    /// Formats a referenced message as a compact "author: content" grounding string, bounded in length so it
    /// stays a recognizable referent rather than a second essay. Returns null for empty content. Pure for tests.
    /// </summary>
    internal static string? FormatReferencedContext(string? author, string? content)
    {
        var text = content?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const int maxLength = 500;
        if (text.Length > maxLength)
        {
            text = text[..maxLength];
        }

        var who = string.IsNullOrWhiteSpace(author) ? "someone" : author.Trim();
        return $"{who}: {text}";
    }

    private async Task<bool> HandlePersonaAsync(
        SocketCommandContext context,
        string content,
        SocketUserMessage message,
        CreativeInvocationKind invocationKind,
        SemanticMessageView? semanticView = null,
        CreativeActionMode actionMode = CreativeActionMode.Auto,
        double? visualWorth = null,
        string? visualHook = null,
        InteractionTraceContext? trace = null,
        InteractionEpisode? episode = null,
        EpisodeActionDecision? episodeDecision = null)
    {
        var prefix = _options.CommandPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        trace ??= new InteractionTraceContext(EpisodeId: Guid.NewGuid().ToString("N"));

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
            MessageId: message.Id,
            EpisodeId: trace.EpisodeId));

        var payload = content[prefix.Length..].TrimStart();
        var defaultPersona = GetDefaultPersona();

        string persona;
        string remainder;

        // For ambient replies, always use the default persona — the user doesn't know
        // they triggered an ambient reply, so parsing persona syntax would be misleading.
        if (invocationKind is CreativeInvocationKind.Ambient or CreativeInvocationKind.Mention)
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
                return true;
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

        // Use the exact semantic view the worth gate judged. Explicit invocations build it here once.
        semanticView ??= await _contextAggregator.BuildMessageViewAsync(
            message, includeHttpUnfurls: true, _shutdownCts.Token);
        IReadOnlyList<ChannelImage>? triggerImagesParam = semanticView.Images.Count > 0 ? semanticView.Images : null;
        IReadOnlyList<UnfurledLink>? unfurledLinks = semanticView.UnfurledLinks.Count > 0
            ? semanticView.UnfurledLinks
            : null;

        var request = new CreativeRequest(
            persona,
            topic,
            GetDisplayName(context.User),
            context.User.Id,
            context.Channel.Id,
            (context.Guild as SocketGuild)?.Id,
            DateTimeOffset.UtcNow,
            invocationKind,
            TriggerMessageId: message.Id,
            Channel: channelContext,
            UserMemories: userMemories,
            UnfurledLinks: unfurledLinks,
            TriggerImages: triggerImagesParam,
            TriggerMediaContext: semanticView.MediaContext,
            ActionMode: actionMode,
            VisualWorth: visualWorth,
            VisualHook: visualHook,
            Trace: trace,
            Episode: episode,
            EpisodeDecision: episodeDecision);

        var result = await _orchestrator.ExecuteAsync(request, context, _shutdownCts.Token);
        var reply = string.IsNullOrWhiteSpace(result.PrimaryMessage)
            ? CreativeOrchestrator.BuildEmptyResponsePlaceholder(persona, invocationKind)
            : result.PrimaryMessage;

        if (string.IsNullOrWhiteSpace(reply))
        {
            _logger.LogDebug("Invocation {InvocationKind} produced no reply for persona {Persona}; suppressing send.", invocationKind, persona);
            return false;
        }
        MessageReference? reference = null;
        if (result.ReplyToMessageId.HasValue)
        {
            reference = new MessageReference(result.ReplyToMessageId.Value);
        }

        await SendChunkedAsync(
            context.Channel, reply, reference, persona, result.AttachmentBytes, result.AttachmentFileName,
            invocationKind.ToString(), message.Id, trace.EpisodeId, result.ReplyToMessageId);
        return true;
    }

    private async Task HandleDirectReplyAsync(SocketCommandContext context, SocketUserMessage message)
    {
        var trace = new InteractionTraceContext(EpisodeId: Guid.NewGuid().ToString("N"));

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
            && _sentMessages.TryGet(message.Reference.MessageId.Value, out var cached))
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

        var semanticView = await _contextAggregator.BuildMessageViewAsync(
            message, includeHttpUnfurls: true, _shutdownCts.Token);
        IReadOnlyList<ChannelImage>? triggerImagesParam = semanticView.Images.Count > 0
            ? semanticView.Images
            : null;
        IReadOnlyList<UnfurledLink>? unfurledLinks = semanticView.UnfurledLinks.Count > 0
            ? semanticView.UnfurledLinks
            : null;

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
            triggerImagesParam,
            semanticView.MediaContext,
            Trace: trace);

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
            MessageId: message.Id,
            EpisodeId: trace.EpisodeId));

        var result = await _orchestrator.ExecuteAsync(request, context, _shutdownCts.Token);
        var reply = string.IsNullOrWhiteSpace(result.PrimaryMessage)
            ? CreativeOrchestrator.BuildEmptyResponsePlaceholder(persona, CreativeInvocationKind.DirectReply)
            : result.PrimaryMessage;

        if (string.IsNullOrWhiteSpace(reply))
        {
            _logger.LogDebug("DirectReply produced no reply for persona {Persona}; suppressing send.", persona);
            return;
        }

        // DirectReply target ownership is deterministic in the orchestrator. Keep the fallback for
        // defensive compatibility with custom orchestrators and older serialized results.
        MessageReference? reference = result.ReplyToMessageId.HasValue
            ? new MessageReference(result.ReplyToMessageId.Value)
            : new MessageReference(message.Id);

        await SendChunkedAsync(
            context.Channel, reply, reference, persona, result.AttachmentBytes, result.AttachmentFileName,
            CreativeInvocationKind.DirectReply.ToString(), message.Id, trace.EpisodeId, result.ReplyToMessageId);
    }

    private async Task SendChunkedAsync(
        ISocketMessageChannel channel, string text, MessageReference? reference, string persona,
        byte[]? attachmentBytes = null, string? attachmentFileName = null,
        string source = "reply", ulong? triggerMessageId = null,
        string? episodeId = null, ulong? replyTargetMessageId = null)
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
            _sentMessages.Register(sentFile.Id, persona, source, triggerMessageId, episodeId, replyTargetMessageId);
            for (int i = 1; i < chunks.Count; i++)
            {
                var more = await channel.SendMessageAsync(chunks[i]);
                _sentMessages.Register(more.Id, persona, source, triggerMessageId, episodeId);
            }
            return;
        }

        // Split into chunks; first chunk gets the reply reference
        for (int i = 0; i < chunks.Count; i++)
        {
            var sent = await channel.SendMessageAsync(chunks[i], messageReference: i == 0 ? reference : null);
            // Register every chunk so replies and human reactions preserve source/persona continuity.
            _sentMessages.Register(
                sent.Id,
                persona,
                source,
                triggerMessageId,
                episodeId,
                i == 0 ? replyTargetMessageId : null);
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

    /// <summary>
    /// True when this message is a reply to one of the bot's own messages. Resolved once per message because
    /// an uncached reference costs a REST fetch.
    /// </summary>
    private async Task<bool> IsReplyToBotAsync(SocketUserMessage message)
    {
        if (message.Reference?.MessageId.IsSpecified != true || _client.CurrentUser is null)
        {
            return false;
        }

        var referenced = message.ReferencedMessage;
        if (referenced is null)
        {
            try
            {
                referenced = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch referenced message {MessageId}", message.Reference.MessageId.Value);
            }
        }

        return referenced?.Author.Id == _client.CurrentUser.Id;
    }

    /// <summary>
    /// Commands that address the bot as a program rather than the character, so they keep their local
    /// handlers even in an autonomy guild: "forget-me" must actually delete memories rather than amuse a
    /// villain, and a "(persona)" override is an explicit request for somebody who is not Robotnik.
    /// </summary>
    private static bool IsLocallyHandledCommand(string payload) =>
        payload.StartsWith('(')
        || payload.Equals("forget-me", StringComparison.OrdinalIgnoreCase)
        || payload.Equals("what-do-you-know", StringComparison.OrdinalIgnoreCase)
        || payload.Equals("forget", StringComparison.OrdinalIgnoreCase)
        || payload.StartsWith("forget ", StringComparison.OrdinalIgnoreCase)
        || payload.StartsWith("scam-report", StringComparison.OrdinalIgnoreCase)
        || payload.StartsWith("scamreport", StringComparison.OrdinalIgnoreCase)
        || payload.StartsWith("scam report", StringComparison.OrdinalIgnoreCase)
        || payload.Equals("empire", StringComparison.OrdinalIgnoreCase)
        || payload.StartsWith("empire ", StringComparison.OrdinalIgnoreCase)
        || payload.StartsWith("empire-", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldWorldAutonomyOwnMessage(
        bool hasPrefix,
        string payload,
        bool isLocallyHandledImage) =>
        !isLocallyHandledImage && (!hasPrefix || !IsLocallyHandledCommand(payload));

    private void RecordMessageForContext(SocketUserMessage message)
    {
        _recentParticipants?.Record(
            message.Author.Id,
            (message.Author as SocketGuildUser)?.DisplayName ?? message.Author.Username);

        if (_options.EnableUserMemory && !string.IsNullOrWhiteSpace(message.Content))
        {
            BufferMessageForExtraction(message);
        }
    }

    /// <summary>
    /// Offers this message to the world-autonomy agent. Returns true when the agent took ownership of the
    /// message, in which case the caller must not also reply: the whole point is that a guild which handed
    /// Robotnik real control hears one Robotnik, and it is the one who can act on what he threatens.
    /// </summary>
    private async Task<bool> TryHandleWorldAutonomyAsync(
        SocketGuildChannel guildChannel,
        SocketUserMessage message,
        string content,
        bool isDirectAddress)
    {
        var opportunity = BuildAutonomyOpportunity(guildChannel, message, content, isDirectAddress);
        if (!isDirectAddress)
        {
            _ = _worldAutonomyRouter!.TryRunAsync(opportunity, _shutdownCts.Token);
            return true;
        }

        WorldAutonomyRunResult? result;
        using (message.Channel.EnterTypingState())
        {
            result = await _worldAutonomyRouter!.TryRunDirectAsync(opportunity, _shutdownCts.Token);
        }

        if (result is null)
        {
            // Busy direct audiences stay queued in the guild mailbox. Null now means the queued run was
            // cancelled or failed before it could produce a result, so the ordinary path is a true fallback
            // rather than a second Robotnik answering the same message.
            _logger.LogWarning(
                "Autonomy could not complete direct message {MessageId} in guild {GuildId}; falling back to the persona path.",
                message.Id,
                guildChannel.Guild.Id);
            return false;
        }

        // The agent runtime never delivers his final text to Discord, so if he schemed without speaking,
        // say it for him. Routed through SendChunkedAsync so the reply keeps normal transcript, reaction,
        // and reply-chain continuity.
        if (!result.SpokeInChannel)
        {
            if (string.IsNullOrWhiteSpace(result.FinalText))
            {
                // Timed out or failed with nothing to show for it. Hand the message back rather than
                // answering a member with silence.
                _logger.LogWarning(
                    "Autonomy run {RunId} ended {Status} with nothing to say for message {MessageId}; falling back to the persona path.",
                    result.RunId,
                    result.Status,
                    message.Id);
                return false;
            }

            await SendChunkedAsync(
                message.Channel,
                result.FinalText.Trim(),
                new MessageReference(message.Id),
                _options.DefaultPersona,
                source: "world_autonomy",
                triggerMessageId: message.Id,
                replyTargetMessageId: message.Id);
            _worldAutonomyRouter.RecordDeliveredSpeech(guildChannel.Guild.Id, message.Channel.Id);
        }

        _logger.LogInformation(
            "Autonomy answered message {MessageId} in guild {GuildId} with status {Status} (spoke={Spoke}).",
            message.Id,
            guildChannel.Guild.Id,
            result.Status,
            result.SpokeInChannel);
        return true;
    }

    /// <summary>
    /// Builds the situation briefing for an autonomy run. The agent used to receive a single bare line with
    /// raw numeric IDs, which is why it discovered the server from scratch every run and could not make a
    /// callback. It now arrives knowing where it is, who it is, and what the room has been saying.
    /// </summary>
    private WorldAutonomyOpportunity BuildAutonomyOpportunity(
        SocketGuildChannel guildChannel,
        SocketUserMessage message,
        string content,
        bool isDirectAddress)
    {
        var prompt = new StringBuilder();
        prompt.Append("You are in the Discord server '").Append(guildChannel.Guild.Name)
            .Append("' (guild ID ").Append(guildChannel.Guild.Id)
            .Append("), watching #").Append(guildChannel.Name)
            .Append(" (channel ID ").Append(message.Channel.Id).Append(").\n");
        if (_client.CurrentUser is not null)
        {
            prompt.Append("Your own account there is '").Append(_client.CurrentUser.Username)
                .Append("' (user ID ").Append(_client.CurrentUser.Id)
                .Append("). Messages from that ID are your own words.\n");
        }

        var history = RenderAutonomyHistory(message);
        if (history.Length > 0)
        {
            prompt.Append("\nRecent conversation there, oldest first:\n").Append(history);
        }

        prompt.Append('\n').Append(GetDisplayName(message.Author))
            .Append(" (user ID ").Append(message.Author.Id)
            .Append(", message ID ").Append(message.Id).Append(") just said:\n")
            .Append(content.Length == 0 ? "(no text)" : content);

        prompt.Append("\n\n").Append(WorldAutonomyPrompt.BuildOpportunityDirective(isDirectAddress));
        var visualIntent = ImageIntentDetector.Classify(content);
        if (visualIntent == VisualRequestIntent.BitmapRequired)
        {
            prompt.Append("\n\nThe petition explicitly asks for an image, picture, photo, or bitmap. " +
                "Select generated_bitmap through create_visual. Text art is not a substitute. If rendering is " +
                "unavailable or refused, say so in character rather than pretending an attachment exists.");
        }
        else if (visualIntent == VisualRequestIntent.MediumChoice)
        {
            prompt.Append("\n\nThis is a visual request whose medium is deliberately yours to choose. " +
                "Select exactly one medium through create_visual: generated_bitmap for the image foundry, or " +
                "text_art for an ASCII proclamation. Choose whichever better serves your idea.");
        }

        return new WorldAutonomyOpportunity(
            guildChannel.Guild.Id,
            "discord_message",
            prompt.ToString(),
            message.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceEpisodeId: null,
            TraceId: Guid.NewGuid().ToString("N"),
            ModelOverride: null,
            IsDirectAddress: isDirectAddress,
            PersonaDirective: _empireState is { Enabled: true }
                ? _empireState.BuildDirective(GetDisplayName(message.Author))
                : null,
            SourceChannelId: message.Channel.Id,
            SourceChannelName: guildChannel.Name,
            SourceAuthorId: message.Author.Id,
            SourceAuthorDisplayName: GetDisplayName(message.Author),
            VisualIntent: visualIntent);
    }

    private string? BuildAutonomyAudienceContext(
        SocketUserMessage message,
        ChannelPulseSnapshot? pulse,
        bool botSpokeRecently)
    {
        var context = new StringBuilder();
        if (pulse is not null)
        {
            context.Append("Channel pulse: ").Append(pulse.DistinctHumansInWindow)
                .Append(" distinct human(s) active in the last ten minutes. ");
        }

        context.Append("Robotnik spoke in the last two minutes: ")
            .Append(botSpokeRecently ? "yes" : "no").Append('.');
        var botId = _client.CurrentUser?.Id;
        var recentRobotnik = botId.HasValue
            ? message.Channel.GetCachedMessages(AutonomyHistoryLimit)
                .Where(cached => cached.Author.Id == botId.Value && !string.IsNullOrWhiteSpace(cached.Content))
                .OrderByDescending(cached => cached.Timestamp)
                .Take(2)
                .Reverse()
                .Select(cached => cached.Content.Replace('\n', ' ').Trim())
            : [];
        foreach (var line in recentRobotnik)
        {
            context.Append(" Recent Robotnik turn: ")
                .Append(line.Length <= 160 ? line : string.Concat(line.AsSpan(0, 160), "..."));
        }

        return context.ToString();
    }

    private static string VisualIntentName(VisualRequestIntent intent) => intent switch
    {
        VisualRequestIntent.BitmapRequired => "bitmap_required",
        VisualRequestIntent.MediumChoice => "medium_choice",
        _ => "none",
    };

    private string RenderAutonomyHistory(SocketUserMessage message)
    {
        var builder = new StringBuilder();
        var recent = message.Channel.GetCachedMessages(AutonomyHistoryLimit)
            .Where(cached => cached.Id != message.Id && !string.IsNullOrWhiteSpace(cached.Content))
            .OrderBy(cached => cached.Timestamp);
        foreach (var cached in recent)
        {
            var speaker = cached.Author.Id == _client.CurrentUser?.Id ? "You" : GetDisplayName(cached.Author);
            var text = cached.Content.Replace('\n', ' ').Trim();
            if (text.Length > AutonomyHistoryLineLength)
            {
                text = string.Concat(text.AsSpan(0, AutonomyHistoryLineLength), "...");
            }

            builder.Append(speaker).Append(": ").Append(text).Append('\n');
        }

        return builder.ToString();
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
                message.Id,
                message.Author.Id,
                GetDisplayName(message.Author),
                message.Content,
                now,
                message.Attachments.Count > 0 || message.Embeds.Count > 0));

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
                await ProcessConversationWindowAsync(channelId, isShutdownFlush: true);
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
    internal async Task ProcessConversationWindowAsync(ulong channelId, bool isShutdownFlush = false)
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

        var window = ExtractionWindow.Capture(channelId, messages, isShutdownFlush);
        var startedAt = Stopwatch.GetTimestamp();
        var terminalOutcome = "failed";
        string? terminalReason = null;
        var failureStage = "rate_gate";
        var summary = MemoryApplySummary.Empty;
        MemoryOpportunityDecision? opportunityDecision = null;
        bool? opportunityWouldSkip = null;
        var explorationRun = false;

        try
        {
            // Probabilistic rate limiting
            if (_randomProvider.NextDouble() > _options.MemoryExtractionRate)
            {
                _logger.LogDebug("Skipping conversation extraction for channel {ChannelId} (rate limiter)", channelId);
                terminalOutcome = "sampled_out";
                terminalReason = "rate_limiter";
                return;
            }

            // Gather participant info and existing memories
            var participantIds = window.ParticipantIds;

            // Acquire per-user memory locks for all participants before reading memories.
            // This prevents cross-window races where two windows for the same user read
            // indices concurrently and then apply stale index-based operations.
            var locks = participantIds
                .Select(id => _userMemoryLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)))
                .ToList();

            foreach (var sem in locks)
            {
                failureStage = "lock";
                await sem.WaitAsync(_shutdownCts.Token);
            }

            try
            {
                var participantMemories = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>();
                foreach (var userId in participantIds)
                {
                    failureStage = "load";
                    var displayName = messages.First(m => m.AuthorId == userId).AuthorDisplayName;
                    var memories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
                    participantMemories[userId] = (displayName, memories);
                }

                if (_memoryExtractionOptions.OpportunityGateMode != MemoryOpportunityGateMode.Off)
                {
                    var priorAge = _lastSuccessfulExtraction.TryGetValue(channelId, out var lastExtraction)
                        ? window.CapturedAt - lastExtraction
                        : (TimeSpan?)null;
                    window = window with
                    {
                        Features = MemoryOpportunityFeatureExtractor.Extract(
                            window.Messages,
                            participantMemories.Values.SelectMany(value => value.Memories).ToArray(),
                            isShutdownFlush,
                            priorAge)
                    };
                    opportunityDecision = _memoryOpportunityClassifier.Classify(window.Features);
                    var forceShutdownRun = isShutdownFlush
                        && _memoryExtractionOptions.ShutdownFlushPolicy == ShutdownFlushExtractionPolicy.RunAlways;
                    var wouldSkip = !opportunityDecision.WouldRun && !forceShutdownRun;
                    opportunityWouldSkip = wouldSkip;

                    if (_memoryExtractionOptions.OpportunityGateMode == MemoryOpportunityGateMode.Live && wouldSkip)
                    {
                        explorationRun = _randomProvider.NextDouble()
                            < Math.Clamp(_memoryExtractionOptions.ExplorationRate, 0.0, 1.0);
                    }
                    var gateOutcome = _memoryExtractionOptions.OpportunityGateMode switch
                    {
                        MemoryOpportunityGateMode.Shadow when wouldSkip => "shadow_would_skip",
                        MemoryOpportunityGateMode.Shadow => "shadow_would_run",
                        MemoryOpportunityGateMode.Live when explorationRun => "exploration_run",
                        MemoryOpportunityGateMode.Live when wouldSkip => "gate_skipped",
                        _ => "gate_run",
                    };
                    EmitMemoryOpportunityTelemetry(
                        window,
                        opportunityDecision,
                        wouldSkip,
                        explorationRun,
                        gateOutcome);
                    if (_memoryExtractionOptions.OpportunityGateMode == MemoryOpportunityGateMode.Live
                        && wouldSkip
                        && !explorationRun)
                    {
                        terminalOutcome = "gate_skipped";
                        terminalReason = opportunityDecision.ReasonCodes.FirstOrDefault() ?? "classifier_skip";
                        return;
                    }
                }

                _logger.LogInformation(
                    "Processing conversation window for channel {ChannelId}: {MessageCount} messages, {ParticipantCount} participants",
                    channelId, messages.Count, participantIds.Count);

                failureStage = "provider";
                var proposedOperations = await _orchestrator.ExtractMemoriesFromConversationAsync(
                    messages,
                    participantMemories,
                    _options.MaxMemoriesPerExtraction,
                    _shutdownCts.Token,
                    new InteractionTraceContext(OperationId: window.OperationId));

                // Filter out operations targeting user IDs not in the participant list
                // (guards against LLM hallucinating user IDs)
                var knownUserIds = participantMemories.Keys.ToHashSet();
                var unknownUserOperations = proposedOperations
                    .Where(operation => !knownUserIds.Contains(operation.UserId))
                    .ToList();
                var operations = proposedOperations
                    .Where(operation => knownUserIds.Contains(operation.UserId))
                    .ToList();

                var policy = new MemoryTransitionPolicy(
                    _memoryExtractionOptions.EvidenceRequired,
                    _options.MaxMemoriesPerUser,
                    _chaosSettingsMonitor.CurrentValue.BanWords,
                    _memoryRelevanceMonitor.CurrentValue.SuppressionOverlapThreshold);
                var plans = participantIds.ToDictionary(
                    userId => userId,
                    userId => _memoryTransitionVerifier.BuildPlan(
                        userId,
                        participantMemories[userId].Memories,
                        operations.Where(operation => operation.UserId == userId).ToArray(),
                        window,
                        policy));

                failureStage = "apply";
                summary = _memoryExtractionOptions.EvidenceRequired
                    ? await ApplyVerifiedMemoryPlansAsync(plans, participantMemories)
                    : await ApplyMultiUserMemoryOperationsAsync(operations);
                summary = summary.WithRejectedOperations(unknownUserOperations, "unknown_target_user");
                foreach (var (userId, plan) in plans)
                {
                    if (plan.Accepted.Count == 0 && plan.Rejected.Count == 0) continue;
                    var actual = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
                    var expected = _memoryExtractionOptions.EvidenceRequired && !plan.IsValid
                        ? participantMemories[userId].Memories
                        : plan.PredictedAfter;
                    EmitMemoryTransitionTelemetry(window, plan, expected, actual);
                    if (_memoryExtractionOptions.EvidenceRequired
                        && plan.IsValid
                        && MemoryTransitionVerifier.ComputeBehavioralStateDigest(expected)
                            != MemoryTransitionVerifier.ComputeBehavioralStateDigest(actual))
                    {
                        throw new InvalidOperationException("verified_state_divergence");
                    }
                }
                if (_memoryExtractionOptions.EvidenceRequired && _options.EnableMemoryConsolidation)
                {
                    foreach (var (userId, plan) in plans.OrderBy(pair => pair.Key))
                    {
                        if (plan.IsValid && plan.Accepted.Count > 0)
                        {
                            await TryConsolidateUserMemoriesAsync(userId);
                        }
                    }
                }
                terminalOutcome = isShutdownFlush
                    ? "shutdown_flush"
                    : summary.Applied > 0
                        ? "ok_applied"
                        : "ok_no_operations";
                    if (explorationRun) terminalReason = "exploration_run";
                    _lastSuccessfulExtraction[channelId] = DateTimeOffset.UtcNow;
            }
            finally
            {
                foreach (var sem in locks)
                {
                    sem.Release();
                }
            }
        }
        catch (OperationCanceledException ex) when (_shutdownCts.IsCancellationRequested)
        {
            terminalOutcome = "cancelled";
            terminalReason = $"{failureStage}_cancelled";
            _logger.LogInformation(ex, "Conversation-window extraction cancelled for channel {ChannelId}", channelId);
        }
        catch (Exception ex)
        {
            terminalOutcome = "failed";
            terminalReason = $"{failureStage}_{ex.GetType().Name}";
            _logger.LogWarning(ex, "Conversation-window extraction failed for channel {ChannelId}", channelId);
        }
        finally
        {
            if (_memoryExtractionOptions.YieldTelemetryEnabled)
            {
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.MemoryExtraction,
                    Outcome: terminalOutcome,
                    Count: window.Features.MessageCount,
                    LatencyMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    OperationId: window.OperationId,
                    Stage: "terminal",
                    ReasonCode: terminalReason,
                    ContextMessageCount: window.Features.MessageCount,
                    ProposedCount: summary.Proposed,
                    AppliedCount: summary.Applied,
                    RejectedCount: summary.Rejected,
                    ParticipantCount: window.Features.ParticipantCount,
                    CharacterCount: window.Features.CharacterCount,
                    WindowDurationMs: (long)window.Features.WindowDuration.TotalMilliseconds,
                    IsShutdownFlush: window.Features.IsShutdownFlush,
                    Before: summary.UserDeltas.Sum(delta => delta.Before),
                    After: summary.UserDeltas.Sum(delta => delta.After),
                    ProposedByAction: ToTelemetryCounts(summary.ProposedByAction),
                    AppliedByAction: ToTelemetryCounts(summary.AppliedByAction),
                    RejectedByReason: summary.RejectedByReason,
                    GateMode: _memoryExtractionOptions.OpportunityGateMode.ToString(),
                    GateWouldSkip: opportunityWouldSkip,
                    IsExplorationRun: explorationRun));
            }
        }
    }

    private void EmitMemoryOpportunityTelemetry(
        ExtractionWindow window,
        MemoryOpportunityDecision decision,
        bool wouldSkip,
        bool explorationRun,
        string outcome)
    {
        var features = window.Features;
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.MemoryOpportunity,
            Outcome: outcome,
            Count: features.MessageCount,
            TopScore: decision.Score,
            ReasonCode: string.Join(',', decision.ReasonCodes),
            OperationId: window.OperationId,
            Stage: _memoryExtractionOptions.OpportunityGateMode == MemoryOpportunityGateMode.Shadow
                ? "shadow"
                : "live",
            ContextMessageCount: features.MessageCount,
            ParticipantCount: features.ParticipantCount,
            CharacterCount: features.CharacterCount,
            WindowDurationMs: (long)features.WindowDuration.TotalMilliseconds,
            IsShutdownFlush: features.IsShutdownFlush,
            GateMode: _memoryExtractionOptions.OpportunityGateMode.ToString(),
            GateWouldSkip: wouldSkip,
            IsExplorationRun: explorationRun,
            FirstPersonAssertionCount: features.FirstPersonAssertionCount,
            PreferenceIdentityChangeCount: features.PreferenceIdentityChangeCount,
            QuestionOnly: features.QuestionOnly,
            MediaOnly: features.MediaOnly,
            LexicalNovelty: features.LexicalNovelty,
            PriorExtractionAgeMs: features.PriorExtractionAge.HasValue
                ? (long)features.PriorExtractionAge.Value.TotalMilliseconds
                : null,
            IsOneMessageWindow: features.IsOneMessageWindow));
    }

    private async Task<MemoryApplySummary> ApplyMultiUserMemoryOperationsAsync(
        List<MultiUserMemoryOperation> operations)
    {
        var proposedByAction = operations
            .GroupBy(operation => operation.Action)
            .ToDictionary(group => group.Key, group => group.Count());
        var appliedByAction = new Dictionary<MemoryAction, int>();
        var rejectedByReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var deltas = new List<UserMemoryCountDelta>();
        var applied = 0;
        var rejected = 0;

        void RecordApplied(MemoryAction action)
        {
            applied++;
            appliedByAction[action] = appliedByAction.GetValueOrDefault(action) + 1;
        }

        void RecordRejected(string reason)
        {
            rejected++;
            rejectedByReason[reason] = rejectedByReason.GetValueOrDefault(reason) + 1;
        }

        // Group by user for efficient dedup
        var byUser = operations.GroupBy(o => o.UserId);

        foreach (var group in byUser)
        {
            var userId = group.Key;
            var beforeMemories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
            IReadOnlyList<UserMemory>? existingMemories = beforeMemories;

            var workingCount = beforeMemories.Count;

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
                    RecordRejected("ban_word");
                    continue;
                }
                if (op.MemoryIndex!.Value < 0 || op.MemoryIndex.Value >= workingCount)
                {
                    RecordRejected("invalid_index");
                    continue;
                }
                await _memoryStore.ForgetMemoryAsync(
                    userId, op.MemoryIndex!.Value, _shutdownCts.Token);
                workingCount--;
                RecordApplied(MemoryAction.Forget);
            }

            // Phase 2: Process updates with adjusted indices (account for removed items)
            foreach (var op in updateOps)
            {
                if (op.Content is not null && _chaosSettingsMonitor.CurrentValue.ContainsBanWord(op.Content))
                {
                    _logger.LogDebug("Memory content contains ban word; skipping");
                    RecordRejected("ban_word");
                    continue;
                }
                if (!op.MemoryIndex.HasValue || string.IsNullOrWhiteSpace(op.Content))
                {
                    RecordRejected("invalid_update");
                    continue;
                }
                if (op.MemoryIndex.HasValue)
                {
                    if (forgetIndices.Contains(op.MemoryIndex.Value))
                    {
                        _logger.LogDebug(
                            "Skipping update at index {Index} for user {UserId}: index was forgotten in the same batch",
                            op.MemoryIndex.Value, userId);
                        RecordRejected("index_forgotten_in_batch");
                        continue;
                    }
                    // Adjust index: subtract count of forgotten indices below this one
                    var adjustment = sortedForgetIndices.Count(fi => fi < op.MemoryIndex.Value);
                    var adjustedIndex = op.MemoryIndex.Value - adjustment;
                    if (adjustedIndex < 0 || adjustedIndex >= workingCount)
                    {
                        RecordRejected("invalid_index");
                        continue;
                    }
                    await _memoryStore.UpdateMemoryAsync(
                        userId, adjustedIndex, op.Content!, op.Context ?? string.Empty, _shutdownCts.Token);
                    RecordApplied(MemoryAction.Update);
                }
            }

            // Phase 3: Process saves last (dedup against current state)
            foreach (var op in saveOps)
            {
                if (op.Content is not null && _chaosSettingsMonitor.CurrentValue.ContainsBanWord(op.Content))
                {
                    _logger.LogDebug("Memory content contains ban word; skipping");
                    RecordRejected("ban_word");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(op.Content))
                {
                    RecordRejected("invalid_save");
                    continue;
                }
                if (Memory.InstructionShapePolicy.IsInstructionShaped(op.Content))
                {
                    _logger.LogWarning(
                        "memory_extract_reject instruction_shape user={UserHash}",
                        Memory.Logging.UserIdHash.Hash(userId));
                    RecordRejected("instruction_shape");
                    continue;
                }
                if (existingMemories is not null && IsDuplicateMemory(op.Content!, existingMemories))
                {
                    _logger.LogInformation("Skipping duplicate memory for user {UserId}: {Content}", userId, op.Content);
                    RecordRejected("duplicate");
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
                workingCount++;
                RecordApplied(MemoryAction.Save);
                existingMemories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
            }

            // Phase 4: Suppressions — the model asked us to stop mentioning specific topics.
            foreach (var op in suppressOps)
            {
                if (string.IsNullOrWhiteSpace(op.Content))
                {
                    RecordRejected("invalid_suppression");
                    continue;
                }
                await _memoryStore.SuppressTopicAsync(
                    userId, op.Content!, _memoryRelevanceMonitor, _shutdownCts.Token);
                RecordApplied(MemoryAction.Suppress);
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

            var afterMemories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
            deltas.Add(new UserMemoryCountDelta(userId, beforeMemories.Count, afterMemories.Count));
        }

        return new MemoryApplySummary(
            operations.Count,
            applied,
            rejected,
            proposedByAction,
            appliedByAction,
            rejectedByReason,
            deltas);
    }

    private async Task<MemoryApplySummary> ApplyVerifiedMemoryPlansAsync(
        IReadOnlyDictionary<ulong, MemoryPlan> plans,
        IReadOnlyDictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)> participantMemories)
    {
        var proposedByAction = new Dictionary<MemoryAction, int>();
        var appliedByAction = new Dictionary<MemoryAction, int>();
        var rejectedByReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var deltas = new List<UserMemoryCountDelta>();
        var proposed = 0;
        var applied = 0;
        var rejected = 0;

        foreach (var (userId, plan) in plans.OrderBy(pair => pair.Key))
        {
            var userProposed = plan.Accepted.Count + plan.Rejected.Count;
            if (userProposed == 0) continue;
            proposed += userProposed;
            foreach (var operation in plan.Accepted.Select(item => item)
                         .Concat(plan.Rejected.Select(item => item.Operation)))
            {
                proposedByAction[operation.Action] = proposedByAction.GetValueOrDefault(operation.Action) + 1;
            }

            var before = participantMemories[userId].Memories;
            if (!plan.IsValid)
            {
                rejected += userProposed;
                foreach (var rejection in plan.Rejected)
                {
                    rejectedByReason[rejection.ReasonCode] =
                        rejectedByReason.GetValueOrDefault(rejection.ReasonCode) + 1;
                }
                var rolledBack = plan.Accepted.Count;
                if (rolledBack > 0)
                {
                    rejectedByReason["atomic_user_plan_rejected"] =
                        rejectedByReason.GetValueOrDefault("atomic_user_plan_rejected") + rolledBack;
                }
                deltas.Add(new UserMemoryCountDelta(userId, before.Count, before.Count));
                continue;
            }

            await _memoryStore.ReplaceAllMemoriesAsync(userId, plan.PredictedAfter, _shutdownCts.Token);
            applied += plan.Accepted.Count;
            foreach (var operation in plan.Accepted)
            {
                appliedByAction[operation.Action] = appliedByAction.GetValueOrDefault(operation.Action) + 1;
            }
            var after = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
            deltas.Add(new UserMemoryCountDelta(userId, before.Count, after.Count));
        }

        return new MemoryApplySummary(
            proposed,
            applied,
            rejected,
            proposedByAction,
            appliedByAction,
            rejectedByReason,
            deltas);
    }

    private void EmitMemoryTransitionTelemetry(
        ExtractionWindow window,
        MemoryPlan plan,
        IReadOnlyList<UserMemory> expected,
        IReadOnlyList<UserMemory> actual)
    {
        var predictedDigest = MemoryTransitionVerifier.ComputeBehavioralStateDigest(expected);
        var actualDigest = MemoryTransitionVerifier.ComputeBehavioralStateDigest(actual);
        var operations = plan.Accepted.Select(operation => operation)
            .Concat(plan.Rejected.Select(rejection => rejection.Operation))
            .ToArray();
        var validEvidence = operations.Count(operation =>
        {
            var ids = operation.EvidenceMessageIds;
            return ids is { Count: > 0 }
                && ids.All(id => window.Messages.Any(message => message.MessageId == id));
        });
        var appliedCount = _memoryExtractionOptions.EvidenceRequired
            ? plan.IsValid ? plan.Accepted.Count : 0
            : plan.Accepted.Count;
        var rejectedCount = _memoryExtractionOptions.EvidenceRequired && !plan.IsValid
            ? operations.Length
            : plan.Rejected.Count;
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.MemoryTransition,
            UserHash: UserIdHash.Hash(plan.UserId),
            Outcome: _memoryExtractionOptions.EvidenceRequired
                ? plan.IsValid ? "verified_applied" : "verified_rejected"
                : predictedDigest == actualDigest ? "shadow_match" : "shadow_diverged",
            OperationId: window.OperationId,
            Stage: _memoryExtractionOptions.EvidenceRequired ? "verified" : "shadow",
            ProposedCount: operations.Length,
            AppliedCount: appliedCount,
            RejectedCount: rejectedCount,
            ValidEvidenceCount: validEvidence,
            MissingEvidenceCount: plan.Observations.GetValueOrDefault("missing_evidence"),
            InvalidEvidenceCount: plan.Observations.GetValueOrDefault("invalid_evidence"),
            PredictedStateDigest: predictedDigest,
            ActualStateDigest: actualDigest,
            Diverged: predictedDigest != actualDigest,
            RejectedByReason: plan.Observations));
    }

    private static IReadOnlyDictionary<string, int> ToTelemetryCounts(
        IReadOnlyDictionary<MemoryAction, int> counts) => counts.ToDictionary(
            pair => pair.Key.ToString().ToLowerInvariant(),
            pair => pair.Value,
            StringComparer.Ordinal);

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
            var countedMemories = memories.Count(memory => memory.Kind != MemoryKind.Suppressed);
            if (countedMemories < _options.MaxMemoriesPerUser)
                return; // not at cap yet, nothing to do

            var targetCount = Math.Max(1, (int)(_options.MaxMemoriesPerUser * _options.ConsolidationTargetPercent));

            _logger.LogInformation(
                "User {UserId} at memory cap ({Count}/{Max}), attempting LLM consolidation to {Target} memories",
                userId, countedMemories, _options.MaxMemoriesPerUser, targetCount);

            var consolidated = await _orchestrator.ConsolidateMemoriesAsync(
                userId, memories, targetCount, _shutdownCts.Token);

            if (consolidated is not null && consolidated.Memories.Count > 0)
            {
                var predictedDigest = MemoryTransitionVerifier.ComputeBehavioralStateDigest(consolidated.Memories);
                await _memoryStore.ReplaceAllMemoriesAsync(userId, consolidated.Memories, _shutdownCts.Token);
                var actual = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
                var actualDigest = MemoryTransitionVerifier.ComputeBehavioralStateDigest(actual);
                var diverged = predictedDigest != actualDigest;
                _logger.LogInformation(
                    "Successfully consolidated memories for user {UserId}: {OldCount} → {NewCount} mode={Mode} diverged={Diverged}",
                    userId, memories.Count, consolidated.Memories.Count, consolidated.Outcome, diverged);
                _telemetry.Emit(new TelemetryEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: TelemetryEventTypes.ConsolidationOk,
                    UserHash: UserIdHash.Hash(userId),
                    Before: memories.Count,
                    After: consolidated.Memories.Count,
                    Kind: consolidated.Outcome,
                    OperationId: consolidated.OperationId,
                    Stage: "verified_transition",
                    ReasonCode: consolidated.ReasonCode,
                    PredictedStateDigest: predictedDigest,
                    ActualStateDigest: actualDigest,
                    Diverged: diverged));
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

            // If this is a reply, the replied-to message is the referent for deictic words like "this"/"that",
            // which the rewriter otherwise never sees (it only gets the literal request). Fetch it and pass it
            // as untrusted grounding context (reference-resolution-by-description; the rewriter data-marks it).
            var replyContext = await TryGetReferencedContextAsync(message);

            var rewrite = await _imageRewriter.RewriteAsync(
                persona, request, GetDisplayName(context.User), userMemories, replyContext, _shutdownCts.Token,
                triggerMessageId: message.Id);

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
                context.User.Id,
                context.Channel.Name,
                rewrite.ImagePrompt!,
                ImageTier.Commissioned,
                _shutdownCts.Token,
                new ImageGenerationContext(
                    Source: "image_command",
                    InvocationKind: "command",
                    TriggerMessageId: message.Id,
                    OpportunityId: Guid.NewGuid().ToString("N"),
                    ToolOffered: false,
                    ToolSelected: false,
                    GuildId: (context.Guild as SocketGuild)?.Id));

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

            _sentMessages.Register(placeholder.Id, persona, "image");
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
            var now = DateTimeOffset.UtcNow;
            var senderIsBot = message.Author.IsBot;
            var senderIsNewAccount = (now - message.Author.CreatedAt).TotalDays < Math.Max(0, _scamGuard.NewAccountDays);

            var phrases = MergeLearned(_scamGuard.ExtraScamPhrases, _learnedScams?.Phrases);
            var hosts = MergeLearned(_scamGuard.ExtraPhishingHosts, _learnedScams?.Hosts);

            // Include forwarded-message content: a forwarded scam link lives in a message snapshot, not Content,
            // so scanning only Content would miss it entirely.
            var scanText = message.TextWithForwarded();

            detection = ScamLinkDetector.Detect(
                scanText, message.MentionedEveryone, phrases, hosts,
                _phishingDomains, _scamGuard.TreatShortenersAsSignal, senderIsBot, senderIsNewAccount);

            // Behavioral raid signal: even if the content looks clean, the same link sprayed across channels or
            // repeated quickly is a raid. Only link-bearing messages are tracked.
            if (!detection.IsScam)
            {
                var keys = DomainUtilities.ExtractLinkKeys(scanText);
                if (keys.Count > 0)
                {
                    var fingerprint = string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal));
                    var raid = _raidTracker.Record(
                        message.Author.Id, message.Channel.Id, fingerprint, now,
                        _scamGuard.RaidWindowSeconds, _scamGuard.RaidChannelThreshold, _scamGuard.RaidRepeatThreshold);
                    if (raid.IsRaid)
                    {
                        detection = new ScamDetection(true, raid.Reason);
                    }
                }
            }
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
        var stamp = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _scamGuard.CooldownSeconds));
        if (_scamWarnCooldown.TryGetValue(channelId, out var last) && stamp - last < cooldown)
        {
            _logger.LogInformation(
                "scam_suppressed channel={Channel} reason={Reason} (cooldown)", message.Channel.Name, detection.Reason);
            EmitScamTelemetry(message, detection, "suppressed");
            return true;
        }

        _scamWarnCooldown[channelId] = stamp;

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
                "scam_warned channel={Channel} author={Author} reason={Reason} bot={Bot}",
                message.Channel.Name, message.Author.Id, detection.Reason, message.Author.IsBot);
            _empireState?.ApplyMoodDelta(EmpireAppraisal.ScamFoiled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post scam warning in channel {Channel}", message.Channel.Name);
        }

        EmitScamTelemetry(message, detection, "warned");
        await ReportScamAsync(message, detection);
        return true;
    }

    private static IReadOnlyCollection<string> MergeLearned(
        List<string> configured, IReadOnlyCollection<string>? learned)
    {
        if (learned is null || learned.Count == 0)
        {
            return configured;
        }

        var merged = new List<string>(configured);
        merged.AddRange(learned);
        return merged;
    }

    private void EmitScamTelemetry(SocketUserMessage message, ScamDetection detection, string outcome)
    {
        try
        {
            _telemetry.Emit(new TelemetryEvent(
                DateTimeOffset.UtcNow,
                TelemetryEventTypes.ScamDetected,
                UserHash: UserIdHash.Hash(message.Author.Id),
                Channel: message.Channel.Name,
                Kind: message.Author.IsBot ? "bot" : "user",
                Outcome: outcome,
                Reason: detection.Reason,
                MessageId: message.Id));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit scam telemetry.");
        }
    }

    private async Task ReportScamAsync(SocketUserMessage message, ScamDetection detection)
    {
        if (string.IsNullOrWhiteSpace(_scamGuard.AlertChannelName))
        {
            return;
        }

        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        var alertChannel = guild?.TextChannels.FirstOrDefault(
            c => string.Equals(c.Name, _scamGuard.AlertChannelName, StringComparison.OrdinalIgnoreCase));
        if (guild is null || alertChannel is null)
        {
            return;
        }

        var jump = $"https://discord.com/channels/{guild.Id}/{message.Channel.Id}/{message.Id}";
        var botTag = message.Author.IsBot ? " [BOT]" : string.Empty;
        var report =
            $"Scam guard flagged a message. reason=`{detection.Reason}` author=**{message.Author.Username}** " +
            $"(`{message.Author.Id}`){botTag} channel=#{message.Channel.Name}\n{jump}";
        try
        {
            await alertChannel.SendMessageAsync(report, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to post scam alert to #{Channel}.", _scamGuard.AlertChannelName);
        }
    }

    /// <summary>
    /// New-account behavioral watch: when a brand-new account posts a payload-bearing message, alert the mods
    /// out of band (in character) so they can review. Link-optional and precision-first (only genuinely new
    /// accounts, only above a multi-signal score), alert-only (never blocks or bans), and per-user cooldowned.
    /// </summary>
    private async Task TryHandleNewAccountAlertAsync(SocketUserMessage message)
    {
        if (!_scamGuard.AlertNewAccounts || string.IsNullOrWhiteSpace(_scamGuard.AlertChannelName)
            || message.Channel is not SocketGuildChannel)
        {
            return;
        }

        // Cheap age gate FIRST: the overwhelming majority of messages come from established accounts, so skip the
        // regex-heavy signal extraction (ExtractHosts / ContainsInvite) entirely for them.
        var now = DateTimeOffset.UtcNow;
        var ageDays = (now - message.Author.CreatedAt).TotalDays;
        if (ageDays >= Math.Max(1, _scamGuard.NewAccountDays))
        {
            return;
        }

        try
        {
            var scanText = message.TextWithForwarded();
            var hosts = DomainUtilities.ExtractHosts(scanText);
            var signals = new NewAccountSignals(
                AccountAgeDays: ageDays,
                HasInvite: DomainUtilities.ContainsInvite(scanText),
                MentionsEveryone: message.MentionedEveryone,
                HasShortener: hosts.Any(DomainUtilities.IsShortener),
                HasLinkOrEmbed: hosts.Count > 0 || message.Embeds.Count > 0,
                HasAttachment: message.Attachments.Count > 0,
                MentionedCount: message.MentionedUsers.Count + message.MentionedRoles.Count);

            var verdict = NewAccountHeuristics.Evaluate(
                signals, _scamGuard.NewAccountDays, _scamGuard.NewAccountAlertThreshold);
            if (!verdict.ShouldAlert)
            {
                return;
            }

            // Alert once per newcomer, not once per message.
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _scamGuard.NewAccountAlertCooldownSeconds));
            if (_newAccountFlags.WasFlaggedWithin(message.Author.Id, now, cooldown))
            {
                return;
            }
            _newAccountFlags.Record(message.Author.Id, now, verdict.Reason);

            _telemetry.Emit(new TelemetryEvent(
                now,
                TelemetryEventTypes.NewAccountFlag,
                UserHash: UserIdHash.Hash(message.Author.Id),
                Channel: message.Channel.Name,
                Kind: message.Author.IsBot ? "bot" : "user",
                Outcome: "alerted",
                Count: (int)ageDays,
                Reason: verdict.Reason,
                MessageId: message.Id));

            await PostNewAccountAlertAsync(message, ageDays, verdict);
            _logger.LogInformation(
                "new_account_flag author={Author} acctAgeDays={Age} score={Score} reason={Reason} channel={Channel}",
                message.Author.Id, (int)ageDays, verdict.Score, verdict.Reason, message.Channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "New-account watch threw; ignoring.");
        }
    }

    private async Task PostNewAccountAlertAsync(SocketUserMessage message, double ageDays, NewAccountVerdict verdict)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        var alertChannel = guild?.TextChannels.FirstOrDefault(
            c => string.Equals(c.Name, _scamGuard.AlertChannelName, StringComparison.OrdinalIgnoreCase));
        if (guild is null || alertChannel is null)
        {
            return;
        }

        var jump = $"https://discord.com/channels/{guild.Id}/{message.Channel.Id}/{message.Id}";
        var report =
            $"INTRUDER SCAN: a freshly-minted account just scuttled into my domain. **{message.Author.Username}** " +
            $"(`{message.Author.Id}`), a mere **{(int)ageDays} days** old, posting in #{message.Channel.Name}. " +
            $"Reeks of a throwaway. Signals: `{verdict.Reason}`.{BuildAlertExcerpt(message)} Inspect the rabble, mods.\n{jump}";
        try
        {
            await alertChannel.SendMessageAsync(report, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to post new-account alert to #{Channel}.", _scamGuard.AlertChannelName);
        }
    }

    /// <summary>
    /// A bounded, single-line excerpt of the flagged message for the mod alert, wrapped in an inline code span so
    /// any link inside neither previews nor becomes clickable in the mod channel. Empty when there is no text.
    /// </summary>
    private static string BuildAlertExcerpt(SocketUserMessage message)
    {
        var text = (message.Content ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            return message.Attachments.Count > 0 ? " Payload: [attachment, no text]." : string.Empty;
        }

        if (text.Length > 180)
        {
            text = text[..180] + "...";
        }

        text = text.Replace('`', '\'').Replace('\n', ' ');
        return $" Payload: `{text}`.";
    }

    private async Task HandleScamReportAsync(SocketCommandContext context, SocketUserMessage message, string payload)
    {
        if (_learnedScams is null)
        {
            await context.Channel.SendMessageAsync("Scam learning is not enabled.");
            return;
        }

        // Gate to moderators so a joke report cannot teach the bot to warn on ordinary words.
        var perms = (context.User as SocketGuildUser)?.GuildPermissions;
        if (perms?.ManageMessages != true)
        {
            await context.Channel.SendMessageAsync("You need the Manage Messages permission to teach me a scam.");
            return;
        }

        var rest = payload;
        foreach (var cmd in new[] { "scam-report", "scamreport", "scam report" })
        {
            if (payload.StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
            {
                rest = payload[cmd.Length..];
                break;
            }
        }
        rest = rest.Trim();

        var learnedHosts = new List<string>();
        var learnedPhrases = new List<string>();

        foreach (var token in rest.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var hostsInToken = DomainUtilities.ExtractHosts(token);
            if (hostsInToken.Count > 0)
            {
                foreach (var host in hostsInToken)
                {
                    if (_learnedScams.AddHost(host))
                    {
                        learnedHosts.Add(host);
                    }
                }
            }
            else if (_learnedScams.AddPhrase(token))
            {
                learnedPhrases.Add(token.ToLowerInvariant());
            }
        }

        // When used as a reply to the offending message, learn its hosts automatically.
        if (message.Reference?.MessageId.IsSpecified == true)
        {
            try
            {
                var referenced = message.ReferencedMessage
                    ?? await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);
                if (referenced is not null)
                {
                    foreach (var host in DomainUtilities.ExtractHosts(referenced.Content))
                    {
                        if (_learnedScams.AddHost(host))
                        {
                            learnedHosts.Add(host);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "scam-report: failed to read referenced message.");
            }
        }

        if (learnedHosts.Count == 0 && learnedPhrases.Count == 0)
        {
            await context.Channel.SendMessageAsync(
                "Nothing new to learn. Reply to a scam with `!sky scam-report`, or `!sky scam-report <domain-or-phrase>`.");
            return;
        }

        var parts = new List<string>();
        if (learnedHosts.Count > 0)
        {
            parts.Add($"{learnedHosts.Count} host(s)");
        }
        if (learnedPhrases.Count > 0)
        {
            parts.Add($"{learnedPhrases.Count} phrase(s)");
        }

        _logger.LogInformation(
            "scam_report by={User} hosts={Hosts} phrases={Phrases}",
            context.User.Id, learnedHosts.Count, learnedPhrases.Count);
        await context.Channel.SendMessageAsync(
            $"Learned {string.Join(" and ", parts)}. I will flag those from now on.");
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
