using System.ClientModel;
using System.Text;
using System.Text.Json;
using Discord.Commands;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class CreativeOrchestrator
{
    private readonly ContextAggregator _contextAggregator;
    private readonly IChatClient _chatClient;
    private readonly SafetyFilter _safetyFilter;
    private readonly IOptionsMonitor<LlmOptions> _llmOptionsMonitor;
    private readonly BotOptions _botOptions;
    private readonly ILogger<CreativeOrchestrator> _logger;

    // Circuit breaker: fast-fail when the API is persistently down
    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;
    private readonly object _circuitLock = new();
    private const int CircuitBreakerThreshold = 5;
    private static readonly TimeSpan CircuitBreakerCooldown = TimeSpan.FromMinutes(1);

    // Throttle concurrent LLM calls to avoid rate limits and runaway cost
    private static readonly SemaphoreSlim _llmThrottle = new(3);

    internal const string SendDiscordMessageToolName = "send_discord_message";

    private static readonly AIFunctionDeclaration SendDiscordMessageTool = AIFunctionFactory.CreateDeclaration(
        name: SendDiscordMessageToolName,
        description: "Send a Discord message, optionally replying to one of the provided messages.",
        jsonSchema: JsonDocument.Parse("""
        {
            "type": "object",
            "additionalProperties": false,
            "properties": {
                "mode": {
                    "type": "string",
                    "enum": ["reply", "broadcast"]
                },
                "target_message_id": {
                    "anyOf": [
                        { "type": "string", "pattern": "^[0-9]{1,20}$" },
                        { "type": "null" }
                    ]
                },
                "text": {
                    "type": "string",
                    "minLength": 1
                },
                "embeds": {
                    "type": "array",
                    "items": { "type": "object" },
                    "default": []
                },
                "components": {
                    "type": "array",
                    "items": { "type": "object" },
                    "default": []
                }
            },
            "required": ["mode", "text"]
        }
        """).RootElement);

    /// <summary>
    /// Tool schema for conversation-window extraction with multi-user support.
    /// Adds a required user_id field so the model can attribute memories to specific participants.
    /// </summary>
    internal const string UpdateUserMemoryConversationToolName = "update_user_memory";

    private static readonly AIFunctionDeclaration UpdateUserMemoryConversationTool = AIFunctionFactory.CreateDeclaration(
        name: UpdateUserMemoryConversationToolName,
        description: "Save, update, or forget a fact about a user who participated in the conversation.",
        jsonSchema: JsonDocument.Parse("""
        {
            "type": "object",
            "additionalProperties": false,
            "properties": {
                "user_id": {
                    "type": "string",
                    "description": "The numeric Discord user ID of the user this memory belongs to."
                },
                "action": {
                    "type": "string",
                    "enum": ["save", "update", "forget"]
                },
                "memory_index": {
                    "anyOf": [
                        { "type": "integer", "minimum": 0 },
                        { "type": "null" }
                    ],
                    "description": "Index of the memory to update or forget (relative to that user's existing memories). Required for update and forget. Null for save."
                },
                "content": {
                    "type": "string",
                    "description": "The fact to remember. Required for save and update."
                },
                "context": {
                    "type": "string",
                    "description": "Brief context for why this is being remembered."
                }
            },
            "required": ["user_id", "action"]
        }
        """).RootElement);

    public CreativeOrchestrator(
        ContextAggregator contextAggregator,
        IChatClient chatClient,
        SafetyFilter safetyFilter,
        IOptionsMonitor<LlmOptions> llmOptions,
        IOptions<BotOptions> botOptions,
        ILogger<CreativeOrchestrator> logger)
    {
        _contextAggregator = contextAggregator;
        _chatClient = chatClient;
        _safetyFilter = safetyFilter;
        _llmOptionsMonitor = llmOptions;
        _botOptions = botOptions.Value;
        _logger = logger;
    }

    public async Task<CreativeResult> ExecuteAsync(CreativeRequest request, SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        if (_safetyFilter.ShouldRateLimit(request.Timestamp, request.ChannelId))
        {
            var rateLimited = request.InvocationKind == CreativeInvocationKind.Ambient
                ? string.Empty
                : "I'm catching my breath—try again soon!";
            return new CreativeResult(rateLimited);
        }

        var context = await _contextAggregator.BuildContextAsync(request, commandContext, cancellationToken);
        var hasTopic = !string.IsNullOrWhiteSpace(request.Topic);
        var historySlice = context.ChannelHistory;
        var userContent = BuildUserContent(request, historySlice, hasTopic);

        // When reasoning is enabled, we need more output tokens to accommodate both reasoning AND the tool call
        var llmProvider = _llmOptionsMonitor.CurrentValue.GetActiveProvider();
        var hasReasoning = !string.IsNullOrWhiteSpace(llmProvider.ReasoningEffort) || !string.IsNullOrWhiteSpace(llmProvider.ReasoningSummary);
        var maxOutputTokens = hasReasoning
            ? Math.Clamp(llmProvider.MaxTokens * 3, 1500, 4096)  // Triple tokens for reasoning models
            : Math.Clamp(llmProvider.MaxTokens, 300, 1024);

        // Use a smaller token budget for ambient replies to reduce cost and response length
        if (request.InvocationKind == CreativeInvocationKind.Ambient)
        {
            maxOutputTokens = Math.Min(maxOutputTokens, 512);
        }

        var chatOptions = new ChatOptions
        {
            ModelId = ResolveModel(request.Persona, llmProvider),
            Instructions = BuildSystemInstructions(request.Persona, hasTopic, request.InvocationKind, request.ReplyChain, request.IsInThread),
            MaxOutputTokens = maxOutputTokens,
            Tools = [SendDiscordMessageTool],
            ToolMode = ChatToolMode.RequireSpecific(SendDiscordMessageToolName),
        };

        if (hasReasoning)
        {
            chatOptions.Reasoning = new ReasoningOptions
            {
                Effort = string.IsNullOrWhiteSpace(llmProvider.ReasoningEffort) ? null : Enum.Parse<ReasoningEffort>(llmProvider.ReasoningEffort, ignoreCase: true),
                Output = string.IsNullOrWhiteSpace(llmProvider.ReasoningSummary) ? null : Enum.Parse<ReasoningOutput>(llmProvider.ReasoningSummary, ignoreCase: true),
            };
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, userContent)
        };

        var knownMessages = historySlice.ToDictionary(m => m.MessageId, m => m);

        await _llmThrottle.WaitAsync(cancellationToken);
        try
        {
            var response = await GetResponseWithRetryAsync(messages, chatOptions, cancellationToken);

            var functionCall = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .FirstOrDefault(fc => string.Equals(fc.Name, SendDiscordMessageToolName, StringComparison.OrdinalIgnoreCase));

            if (functionCall is null)
            {
                _logger.LogWarning(
                    "LLM response missing send_discord_message tool call; falling back to broadcast text. Response text: {Text}",
                    response.Text ?? "(empty)");
                var fallback = _safetyFilter.ScrubBannedContent(response.Text ?? string.Empty);
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    fallback = BuildEmptyResponsePlaceholder(request.Persona, request.InvocationKind);
                }

                return new CreativeResult(fallback.Trim());
            }

            if (!TryParseToolCallArguments(functionCall, out var mode, out var text, out var targetMessageId))
            {
                _logger.LogWarning("Failed to parse send_discord_message arguments from tool call.");
                var fallback = BuildEmptyResponsePlaceholder(request.Persona, request.InvocationKind);
                return new CreativeResult(fallback.Trim());
            }

            var sanitized = _safetyFilter.ScrubBannedContent(text).Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = BuildEmptyResponsePlaceholder(request.Persona, request.InvocationKind).Trim();
            }

            mode = string.Equals(mode, "reply", StringComparison.OrdinalIgnoreCase)
                ? "reply"
                : "broadcast";
            ulong? replyTarget = null;

            if (mode == "reply")
            {
                if (targetMessageId.HasValue && knownMessages.ContainsKey(targetMessageId.Value))
                {
                    replyTarget = targetMessageId.Value;
                }
                else
                {
                    _logger.LogDebug(
                        "Model selected reply mode but provided unknown target {TargetId}; downgrading to broadcast.",
                        targetMessageId);
                    mode = "broadcast";
                }
            }

            return new CreativeResult(sanitized, replyTarget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to craft response for persona {Persona}", request.Persona);
            var failure = request.InvocationKind == CreativeInvocationKind.Ambient
                ? string.Empty
                : $"My {request.Persona} impression short-circuited—try again!";
            return new CreativeResult(failure);
        }
        finally
        {
            _llmThrottle.Release();
        }
    }

    internal static bool TryParseToolCallArguments(FunctionCallContent functionCall, out string mode, out string text, out ulong? targetMessageId)
    {
        mode = "broadcast";
        text = string.Empty;
        targetMessageId = null;

        var args = functionCall.Arguments;
        if (args is null || args.Count == 0)
            return false;

        // Extract mode
        if (!args.TryGetValue("mode", out var modeVal) || modeVal is null)
            return false;
        var candidateMode = modeVal.ToString()?.Trim().ToLowerInvariant();
        if (candidateMode is not ("reply" or "broadcast"))
            return false;

        // Extract text
        if (!args.TryGetValue("text", out var textVal) || textVal is null)
            return false;
        var candidateText = ExtractStringValue(textVal);
        if (string.IsNullOrWhiteSpace(candidateText))
            return false;

        // Extract target_message_id
        ulong? parsedTarget = null;
        if (args.TryGetValue("target_message_id", out var tidVal) && tidVal is not null)
        {
            var tidStr = ExtractStringValue(tidVal);
            if (!string.IsNullOrWhiteSpace(tidStr) && ulong.TryParse(tidStr, out var parsed))
                parsedTarget = parsed;
        }

        mode = candidateMode;
        text = candidateText;
        targetMessageId = parsedTarget;
        return true;
    }

    private static string ExtractStringValue(object? value)
    {
        if (value is null) return string.Empty;
        if (value is string s) return s;
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString() ?? string.Empty,
                JsonValueKind.Number => je.GetRawText(),
                _ => je.ToString()
            };
        }
        return value.ToString() ?? string.Empty;
    }

    private static string BuildSystemInstructions(string persona, bool hasTopic, CreativeInvocationKind invocationKind, IReadOnlyList<ChannelMessage>? replyChain, bool isInThread)
    {
        var builder = new StringBuilder();
        builder.Append($"You are roleplaying as {persona}. Stay fully in character, respond conversationally, and keep replies under four sentences.");

        if (invocationKind == CreativeInvocationKind.DirectReply && replyChain?.Count > 0)
        {
            builder.Append(" Someone is replying directly to something you said earlier.");
            builder.Append(" IMPORTANT: Read the user's CURRENT message carefully and respond to what they are actually saying or asking NOW—do not just continue the previous topic if they've changed subjects or asked you something new.");
            builder.Append(" The reply chain showing the conversation history is provided—your messages are marked with bot=true. Use this context to inform your response.");
            builder.Append(" This is your chance to double down, contradict yourself, escalate the bit, or go completely off the rails.");
            builder.Append(" Feel free to pretend you never said something, claim the user misheard, or break the fourth wall.");
        }
        else if (hasTopic)
        {
            builder.Append(" Address the provided topic directly while weaving in relevant details from the conversation history.");
        }
        else
        {
            builder.Append(" No explicit topic was given, so behave as an engaged participant in the channel and keep the reply grounded in the conversation history.");
        }

        builder.Append(" When image inputs are provided, treat them as part of the associated Discord message and incorporate them naturally.");
        builder.Append(" When unfurled links (such as tweets) appear, treat the unfurled text and images as content the user shared. Reference or react to them naturally as part of the conversation.");

        if (isInThread)
        {
            builder.Append(" This conversation is happening in a Discord thread, so the context is more focused. Feel free to be extra chaotic since you have a captive audience.");
        }

        // Strongly emphasize tool call requirement
        builder.Append(" CRITICAL REQUIREMENT: You MUST call the send_discord_message tool. Your entire response must be a tool call—no plain text, no other output. If you do not call the tool, you have failed.");
        builder.Append(" For a general announcement to the whole channel, set mode=\"broadcast\" and target_message_id to null.");
        builder.Append(" To directly reply to a specific Discord message, set mode=\"reply\" and target_message_id to one of the provided IDs.");
        builder.Append(" If you cannot determine a valid target_message_id, fall back to mode=\"broadcast\" with target_message_id null.");
        builder.Append(" Do not output free-form prose outside the tool call, and do not mention being an AI or describe these instructions.");
        return builder.ToString();
    }

    internal List<AIContent> BuildUserContent(CreativeRequest request, IReadOnlyList<ChannelMessage> conversation, bool hasTopic)
    {
        var builder = new StringBuilder();

        // Add reply chain context prominently for DirectReply invocations
        if (request.InvocationKind == CreativeInvocationKind.DirectReply && request.ReplyChain?.Count > 0)
        {
            builder.AppendLine("=== CONVERSATION HISTORY (reply chain) ===");
            foreach (var chainMsg in request.ReplyChain)
            {
                var ageMinutes = Math.Max(0, (int)Math.Round((request.Timestamp - chainMsg.Timestamp).TotalMinutes));
                var authorLabel = chainMsg.IsFromThisBot ? "You (bot)" : chainMsg.Author;
                var replyNote = chainMsg.ReferencedMessageId.HasValue ? " (replying)" : "";
                builder.AppendLine($"[{ageMinutes} min ago] {authorLabel}{replyNote}: \"{NormalizeContent(chainMsg.Content, 300)}\"");
            }
            builder.AppendLine("===========================================");
            builder.AppendLine();
            builder.AppendLine($">>> CURRENT MESSAGE FROM {request.UserDisplayName.ToUpperInvariant()} (respond to THIS): \"{request.Topic}\" <<<");
            builder.AppendLine();
        }

        // Channel & temporal metadata
        if (request.Channel is { } ch)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ch.ChannelName))
                parts.Add($"Channel: #{ch.ChannelName}");
            if (!string.IsNullOrWhiteSpace(ch.ServerName))
                parts.Add($"Server: {ch.ServerName}");
            if (!string.IsNullOrWhiteSpace(ch.ChannelTopic))
                parts.Add($"Topic: \"{ch.ChannelTopic}\"");
            if (ch.IsNsfw)
                parts.Add("NSFW: yes");
            if (!string.IsNullOrWhiteSpace(ch.ThreadName))
                parts.Add($"Thread: {ch.ThreadName}");
            if (ch.MemberCount.HasValue)
                parts.Add($"Server members: {ch.MemberCount.Value}");
            if (parts.Count > 0)
                builder.AppendLine(string.Join(" | ", parts));

            // Temporal context
            var now = request.Timestamp;
            var timeParts = new List<string>
            {
                $"Current time: {now:dddd h:mm tt} UTC"
            };

            var activityLabel = ch.RecentMessageCount switch
            {
                0 => "silent",
                <= 5 => "quiet",
                <= 20 => "moderate",
                _ => "busy"
            };
            timeParts.Add($"Channel activity (last hour): {ch.RecentMessageCount} messages ({activityLabel})");

            if (ch.BotLastSpokeAt.HasValue)
            {
                var ago = now - ch.BotLastSpokeAt.Value;
                var agoLabel = ago.TotalMinutes < 1 ? "just now"
                    : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
                    : $"{(int)ago.TotalHours}h ago";
                timeParts.Add($"Bot last spoke: {agoLabel}");
            }
            else
            {
                timeParts.Add("Bot last spoke: not recently");
            }

            builder.AppendLine(string.Join(" | ", timeParts));
            builder.AppendLine();
        }

        // Per-user memory injection
        if (request.UserMemories is { Count: > 0 })
        {
            builder.AppendLine($"=== WHAT YOU REMEMBER ABOUT {request.UserDisplayName.ToUpperInvariant()} ===");
            for (int i = 0; i < request.UserMemories.Count; i++)
            {
                builder.AppendLine($"[{i}] {request.UserMemories[i].Content}");
            }
            builder.AppendLine("=======================================================");
            builder.AppendLine("Use these memories to personalize your response. Stay in character.");
            builder.AppendLine();
        }

        builder.AppendLine("Recent Discord messages follow as individual entries (oldest first).");
        builder.AppendLine("Format: MessageId | Author | age_minutes | bot_flag => content.");
        builder.AppendLine();

        builder.AppendLine($"Invoker: {request.UserDisplayName} (user_id={request.UserId}).");
        builder.AppendLine("Decide whether to reply directly to one of the messages above or broadcast a general update.");
        builder.AppendLine("Use send_discord_message to provide the final text and metadata.");
        builder.AppendLine();

        if (hasTopic)
        {
            builder.AppendLine($"Topic from {request.UserDisplayName}: {request.Topic!}");
            builder.AppendLine($"Reply as {request.Persona}, addressing the topic while staying true to the conversation.");
        }
        else
        {
            builder.AppendLine($"No explicit topic was provided. Reply as {request.Persona}, continuing the conversation naturally as though you are a member of this chat.");
            builder.AppendLine($"Command invoked by: {request.UserDisplayName}.");
        }

        var content = new List<AIContent>
        {
            new TextContent(builder.ToString())
        };

        // Include images from the triggering message itself (excluded from channel history)
        if (request.TriggerImages is { Count: > 0 })
        {
            foreach (var image in request.TriggerImages)
            {
                content.Add(new UriContent(image.Url, "image/*"));
            }
        }

        // Include unfurled links from the triggering message (e.g. tweets)
        if (request.UnfurledLinks is { Count: > 0 })
        {
            foreach (var link in request.UnfurledLinks)
            {
                var linkHeader = $"[Unfurled {link.SourceType} from {link.Author}]";
                content.Add(new TextContent($"{linkHeader}: {link.Text}"));

                foreach (var image in link.Images)
                {
                    content.Add(new UriContent(image.Url, "image/*"));
                }
            }
        }

        foreach (var message in conversation)
        {
            content.Add(new TextContent(BuildMessageLine(message, request.Timestamp)));

            foreach (var image in message.Images)
            {
                content.Add(new UriContent(image.Url, "image/*"));
            }

            foreach (var link in message.UnfurledLinks)
            {
                var linkHeader = $"[Unfurled {link.SourceType} from {link.Author}]";
                content.Add(new TextContent($"{linkHeader}: {link.Text}"));

                foreach (var image in link.Images)
                {
                    content.Add(new UriContent(image.Url, "image/*"));
                }
            }
        }

        return content;
    }

    private static string BuildMessageLine(ChannelMessage message, DateTimeOffset reference)
    {
        var ageMinutes = Math.Max(0, (int)Math.Round((reference - message.Timestamp).TotalMinutes));
        var content = NormalizeContent(message.Content, 400);
        if (string.IsNullOrWhiteSpace(content))
        {
            content = message.Images.Count > 0 ? "[image attached]" : "[no text content]";
        }

        var suffixParts = new List<string>();
        if (message.Images.Count > 0)
            suffixParts.Add($"{message.Images.Count} image(s) follow");
        if (message.UnfurledLinks.Count > 0)
            suffixParts.Add($"{message.UnfurledLinks.Count} unfurled link(s) follow");
        var suffix = suffixParts.Count > 0 ? $" ({string.Join(", ", suffixParts)})" : string.Empty;

        return $"{message.MessageId} | {message.Author} | age_minutes={ageMinutes} | bot={message.IsBot} => {content}{suffix}";
    }

    private static string NormalizeContent(string content, int maxLength)
    {
        var flattened = (content ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (flattened.Length <= maxLength)
        {
            return flattened;
        }

        return flattened[..maxLength];
    }

    internal static string BuildEmptyResponsePlaceholder(string persona, CreativeInvocationKind invocationKind)
    {
        if (invocationKind == CreativeInvocationKind.Ambient)
        {
            return string.Empty;
        }

        return $"[{persona} pauses dramatically but says nothing.]";
    }

    private async Task<ChatResponse> GetResponseWithRetryAsync(
        IList<ChatMessage> messages, ChatOptions chatOptions, CancellationToken cancellationToken)
    {
        // Circuit breaker: fail fast if the API is known to be down
        lock (_circuitLock)
        {
            if (_consecutiveFailures >= CircuitBreakerThreshold && DateTimeOffset.UtcNow < _circuitOpenUntil)
            {
                _logger.LogWarning("Circuit breaker open; failing fast until {Until:HH:mm:ss}", _circuitOpenUntil);
                throw new InvalidOperationException("Circuit breaker is open — API calls are temporarily disabled");
            }
        }

        const int maxAttempts = 3;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
                    // Success: reset circuit breaker
                    lock (_circuitLock) { _consecutiveFailures = 0; }
                    return result;
                }
                catch (ClientResultException crEx) when (
                    attempt < maxAttempts &&
                    !cancellationToken.IsCancellationRequested &&
                    crEx.Message.Contains("invalid_image_url", StringComparison.OrdinalIgnoreCase))
                {
                    // An image URL is inaccessible to OpenAI's servers.
                    // Strip all UriContent from the messages and retry once.
                    _logger.LogWarning(crEx,
                        "Image URL rejected by API on attempt {Attempt}/{MaxAttempts}; stripping images and retrying",
                        attempt, maxAttempts);
                    StripImageContent(messages);
                    // Fall through to retry immediately without the images
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger.LogWarning(ex, "Transient error on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}s",
                        attempt, maxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // All retry attempts exhausted — record failure for circuit breaker
            lock (_circuitLock)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= CircuitBreakerThreshold)
                {
                    _circuitOpenUntil = DateTimeOffset.UtcNow + CircuitBreakerCooldown;
                    _logger.LogWarning(
                        "Circuit breaker opened after {Count} consecutive failures; cooling down for {Duration}s",
                        _consecutiveFailures, CircuitBreakerCooldown.TotalSeconds);
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Removes all <see cref="UriContent"/> items from every message so the request
    /// can be retried without images (e.g. after an <c>invalid_image_url</c> error).
    /// </summary>
    internal static void StripImageContent(IList<ChatMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Contents.Any(c => c is UriContent))
            {
                // Rebuild the message without UriContent.
                // We replace the entire ChatMessage because Contents may be backed
                // by a fixed-size array that doesn't support RemoveAt.
                var filtered = msg.Contents.Where(c => c is not UriContent).ToList();
                messages[i] = new ChatMessage(msg.Role, filtered);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;

    private static string ResolveModel(string persona, LlmProviderOptions provider)
    {
        if (provider.IntentModelOverrides.TryGetValue(persona, out var overrideModel) && !string.IsNullOrWhiteSpace(overrideModel))
        {
            return overrideModel;
        }

        return provider.ChatModel;
    }

    /// <summary>
    /// Returns the model to use for memory extraction/consolidation.
    /// Prefers <see cref="LlmProviderOptions.MemoryExtractionModel"/> if set,
    /// otherwise falls back to the provider's default <see cref="LlmProviderOptions.ChatModel"/>.
    /// </summary>
    private string ResolveMemoryExtractionModel()
    {
        var provider = _llmOptionsMonitor.CurrentValue.GetActiveProvider();
        return !string.IsNullOrWhiteSpace(provider.MemoryExtractionModel)
            ? provider.MemoryExtractionModel
            : provider.ChatModel;
    }

    // ── Conversation-Window Memory Extraction ───────────────────────────

    /// <summary>
    /// Extracts memories from a conversation window containing messages from multiple users.
    /// Uses a single LLM call to process the entire conversation context.
    /// </summary>
    internal async Task<List<MultiUserMemoryOperation>> ExtractMemoriesFromConversationAsync(
        IReadOnlyList<BufferedMessage> conversation,
        Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)> participantMemories,
        int maxMemories,
        CancellationToken cancellationToken)
    {
        try
        {
            var systemPrompt = BuildConversationExtractionPrompt(conversation, participantMemories);

            // Format the conversation as the user message
            var conversationText = new StringBuilder();
            conversationText.AppendLine("=== CONVERSATION ===");
            foreach (var msg in conversation)
            {
                var timestamp = msg.Timestamp.ToString("HH:mm:ss");
                var content = NormalizeContent(msg.Content, 500);
                conversationText.AppendLine($"[{timestamp}] {msg.AuthorDisplayName} (ID:{msg.AuthorId}): {content}");
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, conversationText.ToString())
            };

            var options = new ChatOptions
            {
                ModelId = ResolveMemoryExtractionModel(),
                Instructions = systemPrompt,
                MaxOutputTokens = 800,
                Tools = [UpdateUserMemoryConversationTool],
                ToolMode = ChatToolMode.Auto,
            };

            await _llmThrottle.WaitAsync(cancellationToken);
            ChatResponse response;
            try
            {
                response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            }
            finally
            {
                _llmThrottle.Release();
            }
            var operations = ParseMultiUserMemoryOperations(response);

            // Enforce max memories per extraction
            if (operations.Count > maxMemories)
            {
                _logger.LogInformation(
                    "Conversation extraction produced {Count} operations, capping to {Max}",
                    operations.Count, maxMemories);
                operations = operations.Take(maxMemories).ToList();
            }

            return operations;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversation-window memory extraction failed; skipping");
            return [];
        }
    }

    /// <summary>
    /// Uses the LLM to consolidate a user's memories when they approach the cap.
    /// Merges related facts, drops redundancies, and compresses the memory set
    /// down to at most <paramref name="targetCount"/> entries.
    /// Returns null on failure so the caller can fall back to LRU eviction.
    /// </summary>
    internal async Task<List<UserMemory>?> ConsolidateMemoriesAsync(
        ulong userId,
        IReadOnlyList<UserMemory> existingMemories,
        int targetCount,
        CancellationToken cancellationToken)
    {
        try
        {
            if (existingMemories.Count <= targetCount)
                return null; // nothing to consolidate

            var systemPrompt = BuildConsolidationPrompt(existingMemories, targetCount);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, $"Consolidate the {existingMemories.Count} memories above into at most {targetCount} memories.")
            };

            var options = new ChatOptions
            {
                ModelId = ResolveMemoryExtractionModel(),
                Instructions = systemPrompt,
                MaxOutputTokens = 1200,
                ResponseFormat = ChatResponseFormat.Json,
            };

            await _llmThrottle.WaitAsync(cancellationToken);
            ChatResponse response;
            try
            {
                response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            }
            finally
            {
                _llmThrottle.Release();
            }
            var consolidated = ParseConsolidatedMemories(response);

            if (consolidated is null || consolidated.Count == 0)
            {
                _logger.LogWarning("Memory consolidation returned empty result for user {UserId}; skipping", userId);
                return null;
            }

            if (consolidated.Count > targetCount)
            {
                _logger.LogInformation(
                    "Consolidation for user {UserId} returned {Count} memories (target {Target}), trimming",
                    userId, consolidated.Count, targetCount);
                consolidated = consolidated.Take(targetCount).ToList();
            }

            _logger.LogInformation(
                "Consolidated {OldCount} memories down to {NewCount} for user {UserId}",
                existingMemories.Count, consolidated.Count, userId);

            return consolidated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory consolidation failed for user {UserId}; falling back to LRU eviction", userId);
            return null;
        }
    }

    internal static string BuildConsolidationPrompt(
        IReadOnlyList<UserMemory> existingMemories,
        int targetCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a memory consolidation system for a Discord bot.");
        sb.AppendLine("A user's memory store has grown large and needs to be compressed.");
        sb.AppendLine();
        sb.AppendLine("Your task: take the existing memories below and CONSOLIDATE them into a smaller set.");
        sb.AppendLine($"You MUST produce at most {targetCount} memories.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- MERGE related facts into single, richer memories (e.g., \"Likes cats\" + \"Has a cat named Whiskers\" → \"Has a cat named Whiskers and loves cats\")");
        sb.AppendLine("- KEEP the most important, distinctive, and personality-defining facts");
        sb.AppendLine("- DROP truly redundant, trivial, or outdated information");
        sb.AppendLine("- PRESERVE specific details like names, places, and preferences — don't over-generalize");
        sb.AppendLine("- PRESERVE corrections (if a memory says \"actually from Canada, not Australia\", keep the corrected version)");
        sb.AppendLine("- More recently created or referenced memories are generally more important");
        sb.AppendLine("- Each consolidated memory should be a concise, standalone fact");
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object containing a single key \"memories\" with an array of objects.");
        sb.AppendLine("Each object must have:");
        sb.AppendLine("  - \"content\": the consolidated fact (string)");
        sb.AppendLine("  - \"context\": brief context for why this is remembered (string)");
        sb.AppendLine();
        sb.AppendLine("Example response:");
        sb.AppendLine("""
        {
          "memories": [
            { "content": "Has a cat named Whiskers and loves cats in general", "context": "mentioned pets multiple times" },
            { "content": "Works as a software engineer in Vancouver, Canada", "context": "shared career and location details" }
          ]
        }
        """);
        sb.AppendLine();
        sb.AppendLine("=== EXISTING MEMORIES ===");
        for (int i = 0; i < existingMemories.Count; i++)
        {
            var m = existingMemories[i];
            sb.AppendLine($"[{i}] \"{m.Content}\" (context: {m.Context}, created: {m.CreatedAt:yyyy-MM-dd}, last referenced: {m.LastReferencedAt:yyyy-MM-dd})");
        }

        return sb.ToString();
    }

    internal static List<UserMemory>? ParseConsolidatedMemories(ChatResponse response)
    {
        var textContent = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Select(tc => tc.Text)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(textContent))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(textContent);
            if (!doc.RootElement.TryGetProperty("memories", out var memoriesArray))
                return null;

            var now = DateTimeOffset.UtcNow;
            var result = new List<UserMemory>();

            foreach (var item in memoriesArray.EnumerateArray())
            {
                var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
                var context = item.TryGetProperty("context", out var ctx) ? ctx.GetString() : null;

                if (!string.IsNullOrWhiteSpace(content))
                {
                    result.Add(new UserMemory(content, context ?? string.Empty, now, now, 0));
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string BuildConversationExtractionPrompt(
        IReadOnlyList<BufferedMessage> conversation,
        Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)> participantMemories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a memory manager for a Discord bot. You have observed a conversation in a channel the bot participates in.");
        sb.AppendLine("Your job is to extract noteworthy facts about the participants that are worth remembering for future interactions.");
        sb.AppendLine("The conversation may involve multiple users — attribute each memory to the correct user by their user_id.");
        sb.AppendLine();
        sb.AppendLine("SAVE things like:");
        sb.AppendLine("- User preferences, interests, or opinions they have expressed");
        sb.AppendLine("- Personal facts they have shared (name, location, hobbies, pets, job)");
        sb.AppendLine("- Relationships between users (\"Alice and Bob are siblings\")");
        sb.AppendLine("- Running jokes, catchphrases, or recurring themes");
        sb.AppendLine("- How the user prefers to interact (serious, playful, confrontational)");
        sb.AppendLine("- Corrections users have made (\"actually, I'm from Canada, not Australia\")");
        sb.AppendLine("- Facts mentioned about OTHER users (\"everyone congratulated Alice on her promotion\" → save to Alice)");
        sb.AppendLine();
        sb.AppendLine("DO NOT SAVE:");
        sb.AppendLine("- Generic conversation filler (\"user said hello\", \"user sent a meme\")");
        sb.AppendLine("- Anything only relevant to this specific moment");
        sb.AppendLine("- Sensitive information (health conditions, financial details, passwords) unless the user explicitly asks to remember it");
        sb.AppendLine("- Redundant facts already in the existing memories below");
        sb.AppendLine("- Facts about bots");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT:");
        sb.AppendLine("- Use pronouns, context, and the full conversation to resolve who said what");
        sb.AppendLine("- A user might reveal facts about ANOTHER user — attribute the memory to the person the fact is ABOUT");
        sb.AppendLine("- If updating or correcting an existing memory, use action=\"update\" with the memory_index for that user");
        sb.AppendLine("- If nothing is worth saving, do NOT call the tool at all — just respond with a short acknowledgment");
        sb.AppendLine("- Extract at most 5 facts per user. Focus on the most significant and durable information.");
        sb.AppendLine("- If multiple messages reveal related information, combine them into a single consolidated fact rather than saving each one separately.");
        sb.AppendLine("- Only use user_id values from the PARTICIPANTS list below. Do not invent user IDs.");
        sb.AppendLine();

        // List participants and their existing memories
        sb.AppendLine("=== PARTICIPANTS ===");
        foreach (var (userId, (displayName, memories)) in participantMemories)
        {
            sb.AppendLine($"User: {displayName} (ID:{userId})");
            if (memories.Count > 0)
            {
                sb.AppendLine("  Existing memories:");
                for (int i = 0; i < memories.Count; i++)
                {
                    sb.AppendLine($"    [{i}] {memories[i].Content}");
                }
            }
            else
            {
                sb.AppendLine("  No existing memories.");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static List<MultiUserMemoryOperation> ParseMultiUserMemoryOperations(ChatResponse response)
    {
        var operations = new List<MultiUserMemoryOperation>();

        var toolCalls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(fc => string.Equals(fc.Name, UpdateUserMemoryConversationToolName, StringComparison.OrdinalIgnoreCase));

        foreach (var call in toolCalls)
        {
            if (call.Arguments is null || call.Arguments.Count == 0)
                continue;

            // user_id is required for conversation-level extraction
            if (!call.Arguments.TryGetValue("user_id", out var userIdVal) || userIdVal is null)
                continue;
            var userIdStr = ExtractStringValue(userIdVal);
            if (!ulong.TryParse(userIdStr, out var userId))
                continue;

            if (!call.Arguments.TryGetValue("action", out var actionVal) || actionVal is null)
                continue;
            var actionStr = ExtractStringValue(actionVal).ToLowerInvariant();
            if (!Enum.TryParse<MemoryAction>(actionStr, ignoreCase: true, out var action))
                continue;

            string? content = null;
            string? context = null;
            int? memoryIndex = null;

            if (call.Arguments.TryGetValue("content", out var contentVal) && contentVal is not null)
                content = ExtractStringValue(contentVal);
            if (call.Arguments.TryGetValue("context", out var contextVal) && contextVal is not null)
                context = ExtractStringValue(contextVal);
            if (call.Arguments.TryGetValue("memory_index", out var indexVal) && indexVal is not null)
            {
                var indexStr = ExtractStringValue(indexVal);
                if (int.TryParse(indexStr, out var parsed))
                    memoryIndex = parsed;
            }

            // Validate: save/update require content, update/forget require index
            if (action is MemoryAction.Save or MemoryAction.Update && string.IsNullOrWhiteSpace(content))
                continue;
            if (action is MemoryAction.Update or MemoryAction.Forget && !memoryIndex.HasValue)
                continue;

            operations.Add(new MultiUserMemoryOperation(userId, action, memoryIndex, content, context ?? string.Empty));
        }

        return operations;
    }
}
