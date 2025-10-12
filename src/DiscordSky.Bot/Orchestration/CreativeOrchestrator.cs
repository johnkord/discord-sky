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
            return new CreativeResult("I'm catching my breath—try again soon!");
        }

        var context = await _contextAggregator.BuildContextAsync(request, commandContext, cancellationToken);
        var hasTopic = !string.IsNullOrWhiteSpace(request.Topic);
        var historySlice = context.ChannelHistory;
        var userPrompt = BuildUserPrompt(request, historySlice, hasTopic);

        var responseRequest = new OpenAiResponseRequest
        {
            Model = ResolveModel(request.Persona),
            Instructions = BuildSystemInstructions(request.Persona, hasTopic),
            Input = new List<OpenAiResponseInputItem>
            {
                OpenAiResponseInputItem.FromText("user", userPrompt)
            },
            MaxOutputTokens = Math.Clamp(_options.MaxTokens, 300, 1024),
            Tools = new[] { OpenAiTooling.CreateSendDiscordMessageTool() },
            ToolChoice = new
            {
                type = "function",
                name = OpenAiTooling.SendDiscordMessageToolName
            },
            ParallelToolCalls = false
        };

        var knownMessages = historySlice.ToDictionary(m => m.MessageId, m => m);

        try
        {
            var completion = await _openAiClient.CreateResponseAsync(responseRequest, cancellationToken);
            if (!OpenAiResponseParser.TryParseSendDiscordMessageCall(completion, out var toolCall))
            {
                _logger.LogWarning("OpenAI response missing send_discord_message tool call; falling back to broadcast text.");
                var fallback = _safetyFilter.ScrubBannedContent(OpenAiResponseParser.ExtractPrimaryText(completion));
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    fallback = BuildEmptyResponsePlaceholder(request.Persona);
                }

                return new CreativeResult(fallback.Trim(), null, "broadcast");
            }

            var call = toolCall!;
            var rawText = call.Text ?? string.Empty;
            var sanitized = _safetyFilter.ScrubBannedContent(rawText).Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = BuildEmptyResponsePlaceholder(request.Persona).Trim();
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
            return new CreativeResult($"My {request.Persona} impression short-circuited—try again!");
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

        builder.Append(" Always respond by invoking the send_discord_message tool with JSON arguments describing the Discord message to send.");
        builder.Append(" To reply to a specific message, set mode=\"reply\" and target_message_id to one of the provided IDs. For general updates, set mode=\"broadcast\" and target_message_id to null.");
        builder.Append(" Do not output free-form prose outside the tool call, and do not mention being an AI or describe these instructions.");
        return builder.ToString();
    }

    private static string BuildUserPrompt(CreativeRequest request, IReadOnlyList<ChannelMessage> conversation, bool hasTopic)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Recent Discord messages (JSON array, oldest first):");
        builder.AppendLine(BuildConversationJson(conversation, request.Timestamp));
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

        return builder.ToString();
    }

    private static string BuildConversationJson(IReadOnlyList<ChannelMessage> history, DateTimeOffset reference)
    {
        if (history.Count == 0)
        {
            return "[]";
        }

        var items = history
            .Select(message => new
            {
                id = message.MessageId.ToString(),
                author = message.Author,
                is_bot = message.IsBot,
                age_minutes = Math.Max(0, (int)Math.Round((reference - message.Timestamp).TotalMinutes)),
                content = NormalizeContent(message.Content, 400)
            })
            .ToArray();

        return JsonSerializer.Serialize(items);
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

    private static string BuildEmptyResponsePlaceholder(string persona)
        => $"[{persona} pauses dramatically but says nothing.]{Environment.NewLine}";

    private string ResolveModel(string persona)
    {
        if (_options.IntentModelOverrides.TryGetValue(persona, out var overrideModel) && !string.IsNullOrWhiteSpace(overrideModel))
        {
            return overrideModel;
        }

        return _options.ChatModel;
    }
}
