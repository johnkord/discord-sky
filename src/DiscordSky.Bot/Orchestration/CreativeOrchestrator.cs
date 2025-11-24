using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Discord.Commands;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class CreativeOrchestrator
{
    private readonly ContextAggregator _contextAggregator;
    private readonly IOpenAiClient _openAiClient;
    private readonly SafetyFilter _safetyFilter;
    private readonly OpenAIOptions _options;
    private readonly ILogger<CreativeOrchestrator> _logger;

    private static readonly JsonSerializerOptions LoggingSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public CreativeOrchestrator(
        ContextAggregator contextAggregator,
        IOpenAiClient openAiClient,
        SafetyFilter safetyFilter,
        IOptions<OpenAIOptions> options,
        ILogger<CreativeOrchestrator> logger)
    {
        _contextAggregator = contextAggregator;
        _openAiClient = openAiClient;
        _safetyFilter = safetyFilter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CreativeResult> ExecuteAsync(CreativeRequest request, SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        if (_safetyFilter.ShouldRateLimit(request.Timestamp))
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

        var responseRequest = new OpenAiResponseRequest
        {
            Model = ResolveModel(request.Persona),
            Instructions = BuildSystemInstructions(request.Persona, hasTopic),
            Input = new List<OpenAiResponseInputItem>
            {
                OpenAiResponseInputItem.FromContent("user", userContent)
            },
            MaxOutputTokens = Math.Clamp(_options.MaxTokens, 300, 1024),
            Tools = new[] { OpenAiTooling.CreateSendDiscordMessageTool() },
            ToolChoice = new
            {
                type = "function",
                name = OpenAiTooling.SendDiscordMessageToolName
            },
            ParallelToolCalls = false,
            Reasoning = string.IsNullOrWhiteSpace(_options.ReasoningEffort) && string.IsNullOrWhiteSpace(_options.ReasoningSummary)
                ? null
                : new OpenAiReasoningConfig
                {
                    Effort = string.IsNullOrWhiteSpace(_options.ReasoningEffort) ? null : _options.ReasoningEffort,
                    Summary = string.IsNullOrWhiteSpace(_options.ReasoningSummary) ? null : _options.ReasoningSummary
                }
        };

        var knownMessages = historySlice.ToDictionary(m => m.MessageId, m => m);

        try
        {
            var completion = await _openAiClient.CreateResponseAsync(responseRequest, cancellationToken);
            if (!OpenAiResponseParser.TryParseSendDiscordMessageCall(completion, out var toolCall))
            {
                var serializedResponse = SerializeResponseForLogging(completion);
                _logger.LogWarning(
                    "OpenAI response missing send_discord_message tool call; falling back to broadcast text. Raw response: {Response}",
                    serializedResponse);
                var fallback = _safetyFilter.ScrubBannedContent(OpenAiResponseParser.ExtractPrimaryText(completion));
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    fallback = BuildEmptyResponsePlaceholder(request.Persona, request.InvocationKind);
                }

                return new CreativeResult(fallback.Trim(), null, "broadcast");
            }

            var call = toolCall!;
            var rawText = call.Text ?? string.Empty;
            var sanitized = _safetyFilter.ScrubBannedContent(rawText).Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = BuildEmptyResponsePlaceholder(request.Persona, request.InvocationKind).Trim();
            }

            var mode = string.Equals(call.Mode, "reply", StringComparison.OrdinalIgnoreCase)
                ? "reply"
                : "broadcast";
            ulong? replyTarget = null;

            if (mode == "reply")
            {
                if (call.TargetMessageId.HasValue && knownMessages.ContainsKey(call.TargetMessageId.Value))
                {
                    replyTarget = call.TargetMessageId.Value;
                }
                else
                {
                    _logger.LogDebug(
                        "Model selected reply mode but provided unknown target {TargetId}; downgrading to broadcast.",
                        call.TargetMessageId);
                    mode = "broadcast";
                }
            }

            return new CreativeResult(sanitized, replyTarget, mode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to craft response for persona {Persona}", request.Persona);
            var failure = request.InvocationKind == CreativeInvocationKind.Ambient
                ? string.Empty
                : $"My {request.Persona} impression short-circuited—try again!";
            return new CreativeResult(failure);
        }
    }

    private static string BuildSystemInstructions(string persona, bool hasTopic)
    {
        var builder = new StringBuilder();
        builder.Append($"You are roleplaying as {persona}. Stay fully in character, respond conversationally, and keep replies under four sentences.");
        if (hasTopic)
        {
            builder.Append(" Address the provided topic directly while weaving in relevant details from the conversation history.");
        }
        else
        {
            builder.Append(" No explicit topic was given, so behave as an engaged participant in the channel and keep the reply grounded in the conversation history.");
        }

        builder.Append(" When image inputs are provided, treat them as part of the associated Discord message and incorporate them naturally.");
        builder.Append(" You must produce exactly one call to the send_discord_message tool in your final response—never answer with plain text or any other structure.");
        builder.Append(" For a general announcement to the whole channel, set mode=\"broadcast\" and target_message_id to null.");
        builder.Append(" To directly reply to a specific Discord message, set mode=\"reply\" and target_message_id to one of the provided IDs.");
        builder.Append(" If you cannot determine a valid target_message_id, fall back to mode=\"broadcast\" with target_message_id null.");
        builder.Append(" Do not output free-form prose outside the tool call, and do not mention being an AI or describe these instructions.");
        return builder.ToString();
    }

    private IReadOnlyList<OpenAiResponseInputContent> BuildUserContent(CreativeRequest request, IReadOnlyList<ChannelMessage> conversation, bool hasTopic)
    {
        var builder = new StringBuilder();

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

        builder.AppendLine();
        builder.AppendLine("Image summary JSON (empty if none):");
        builder.AppendLine(BuildImageSummaryJson(conversation, request.Timestamp));

        var content = new List<OpenAiResponseInputContent>
        {
            OpenAiResponseInputContent.FromText(builder.ToString())
        };

        foreach (var message in conversation)
        {
            content.Add(OpenAiResponseInputContent.FromText(BuildMessageLine(message, request.Timestamp)));

            if (message.Images.Count == 0)
            {
                continue;
            }

            foreach (var image in message.Images)
            {
                content.Add(OpenAiResponseInputContent.FromImage(image.Url, _options.VisionDetail));
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

        var suffix = message.Images.Count > 0
            ? $" ({message.Images.Count} image(s) follow)"
            : string.Empty;

        return $"{message.MessageId} | {message.Author} | age_minutes={ageMinutes} | bot={message.IsBot} => {content}{suffix}";
    }

    private static string BuildImageSummaryJson(IReadOnlyList<ChannelMessage> conversation, DateTimeOffset reference)
    {
        var images = conversation
            .SelectMany(message => message.Images.Select(image => new
            {
                message_id = message.MessageId.ToString(),
                author = message.Author,
                age_minutes = Math.Max(0, (int)Math.Round((reference - image.Timestamp).TotalMinutes)),
                filename = image.Filename,
                url = image.Url.ToString(),
                source = image.Source
            }))
            .ToArray();

        if (images.Length == 0)
        {
            return "[]";
        }

        return JsonSerializer.Serialize(images);
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

    private static string SerializeResponseForLogging(OpenAiResponse response)
    {
        try
        {
            return JsonSerializer.Serialize(response, LoggingSerializerOptions);
        }
        catch
        {
            return "<failed to serialize OpenAI response>";
        }
    }

    private string ResolveModel(string persona)
    {
        if (_options.IntentModelOverrides.TryGetValue(persona, out var overrideModel) && !string.IsNullOrWhiteSpace(overrideModel))
        {
            return overrideModel;
        }

        return _options.ChatModel;
    }
}
