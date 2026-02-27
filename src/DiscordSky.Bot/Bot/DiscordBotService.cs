using System.Collections.Concurrent;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Memory;
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
    private readonly ContextAggregator _contextAggregator;
    private readonly IUserMemoryStore _memoryStore;
    private readonly ILinkUnfurler _linkUnfurler;
    private readonly IRandomProvider _randomProvider;
    private readonly ConcurrentDictionary<ulong, (string Persona, DateTimeOffset CreatedAt)> _personaCache = new();
    private readonly ConcurrentDictionary<ulong, ChannelMessageBuffer> _channelBuffers = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private const int MaxPersonaCacheSize = 500;
    internal const int DiscordMaxMessageLength = 2000;

    public DiscordBotService(
        DiscordSocketClient client,
        IOptions<BotOptions> options,
        IOptions<ChaosSettings> chaosSettings,
        CreativeOrchestrator orchestrator,
        ContextAggregator contextAggregator,
        IUserMemoryStore memoryStore,
        ILinkUnfurler linkUnfurler,
        ILogger<DiscordBotService> logger,
        IRandomProvider? randomProvider = null)
    {
        _client = client;
        _options = options.Value;
        _chaosSettings = chaosSettings.Value;
        _orchestrator = orchestrator;
        _contextAggregator = contextAggregator;
        _memoryStore = memoryStore;
        _linkUnfurler = linkUnfurler;
        _logger = logger;
        _randomProvider = randomProvider ?? DefaultRandomProvider.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += OnLogAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Flush any pending conversation buffers before shutdown
        await FlushAllBuffersAsync();

        await _shutdownCts.CancelAsync();

        _client.Log -= OnLogAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
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
        try
        {
            await ProcessMessageAsync(rawMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing message {MessageId}", rawMessage.Id);
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

            if (referencedMessage?.Author.Id == _client.CurrentUser.Id)
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

        var channelContext = BuildChannelContext(context);

        // Load per-user memories if enabled
        IReadOnlyList<UserMemory>? userMemories = null;
        if (_options.EnableUserMemory)
        {
            userMemories = await _memoryStore.GetMemoriesAsync(context.User.Id, _shutdownCts.Token);
            if (userMemories.Count > 0)
            {
                _ = _memoryStore.TouchMemoriesAsync(context.User.Id, _shutdownCts.Token);
            }
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

        await SendChunkedAsync(context.Channel, reply, reference, persona);
    }

    private async Task HandleDirectReplyAsync(SocketCommandContext context, SocketUserMessage message)
    {
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
            userMemories = await _memoryStore.GetMemoriesAsync(context.User.Id, _shutdownCts.Token);
            if (userMemories.Count > 0)
            {
                _ = _memoryStore.TouchMemoriesAsync(context.User.Id, _shutdownCts.Token);
            }
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

        await SendChunkedAsync(context.Channel, reply, reference, persona);
    }

    private async Task SendChunkedAsync(ISocketMessageChannel channel, string text, MessageReference? reference, string persona)
    {
        if (text.Length <= DiscordMaxMessageLength)
        {
            var sent = await channel.SendMessageAsync(text, messageReference: reference);
            CachePersona(sent.Id, persona);
            return;
        }

        // Split into chunks; first chunk gets the reply reference
        var chunks = ChunkMessage(text, DiscordMaxMessageLength);
        for (int i = 0; i < chunks.Count; i++)
        {
            var sent = await channel.SendMessageAsync(chunks[i], messageReference: i == 0 ? reference : null);
            if (i == 0)
            {
                CachePersona(sent.Id, persona);
            }
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

        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var key in _personaCache.Keys)
        {
            if (_personaCache.TryGetValue(key, out var entry) && entry.CreatedAt < cutoff)
            {
                _personaCache.TryRemove(key, out _);
            }
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
        if (memories.Count == 0)
        {
            await context.Channel.SendMessageAsync("I don't have any memories about you yet.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**What I remember about you** ({memories.Count} memories):");
        for (int i = 0; i < memories.Count; i++)
        {
            sb.AppendLine($"`[{i}]` {memories[i].Content}");
        }
        sb.AppendLine();
        sb.AppendLine($"_Use `{_options.CommandPrefix} forget-me` to clear all memories._");

        await SendChunkedAsync(context.Channel, sb.ToString(), null, GetDefaultPersona());
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
                .ToList();

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

            foreach (var op in userOps)
            {
                if (op.Content is not null && _chaosSettings.ContainsBanWord(op.Content))
                {
                    _logger.LogDebug("Memory content contains ban word; skipping");
                    continue;
                }

                switch (op.Action)
                {
                    case MemoryAction.Save:
                        if (existingMemories is not null && IsDuplicateMemory(op.Content!, existingMemories))
                        {
                            _logger.LogInformation("Skipping duplicate memory for user {UserId}: {Content}", userId, op.Content);
                            continue;
                        }
                        await _memoryStore.SaveMemoryAsync(
                            userId, op.Content!, op.Context ?? string.Empty, _shutdownCts.Token);
                        existingMemories = await _memoryStore.GetMemoriesAsync(userId, _shutdownCts.Token);
                        break;
                    case MemoryAction.Update when op.MemoryIndex.HasValue:
                        await _memoryStore.UpdateMemoryAsync(
                            userId, op.MemoryIndex.Value, op.Content!, op.Context ?? string.Empty, _shutdownCts.Token);
                        break;
                    case MemoryAction.Forget when op.MemoryIndex.HasValue:
                        await _memoryStore.ForgetMemoryAsync(
                            userId, op.MemoryIndex.Value, _shutdownCts.Token);
                        break;
                }
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
            }
            else
            {
                _logger.LogDebug(
                    "Consolidation returned no results for user {UserId}; LRU eviction will handle overflow",
                    userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory consolidation failed for user {UserId}; LRU eviction will handle overflow", userId);
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

        _shutdownCts.Dispose();
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
    public double NextDouble() => Random.Shared.NextDouble();
}
