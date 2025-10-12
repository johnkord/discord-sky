using System.Linq;
using System.Text;
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
        if (_safetyFilter.IsQuietHour(request.Timestamp))
        {
            return new CreativeResult("Shh… quiet hours. Logging that spark for later.");
        }

        if (_safetyFilter.ShouldRateLimit(request.Timestamp))
        {
            return new CreativeResult("I'm catching my breath—try again soon!");
        }

        var context = await _contextAggregator.BuildContextAsync(request, commandContext, cancellationToken);
        var conversation = BuildConversationSnippet(context.ChannelHistory);
        var hasTopic = !string.IsNullOrWhiteSpace(request.Topic);
        var userPrompt = BuildUserPrompt(request, conversation, hasTopic);

        var responseRequest = new OpenAiResponseRequest
        {
            Model = ResolveModel(request.Persona),
            Instructions = BuildSystemInstructions(request.Persona, hasTopic),
            Input = new List<OpenAiResponseInputItem>
            {
                OpenAiResponseInputItem.FromText("user", userPrompt)
            },
            Temperature = Math.Clamp(_options.Temperature * (1.0 + context.Chaos.AnnoyanceLevel / 3), 0.1, 1.2),
            TopP = _options.TopP,
            MaxOutputTokens = Math.Clamp(_options.MaxTokens, 300, 1024)
        };

        try
        {
            var completion = await _openAiClient.CreateResponseAsync(responseRequest, cancellationToken);
            var message = OpenAiResponseParser.ExtractPrimaryText(completion);
            message = _safetyFilter.ScrubBannedContent(message);

            if (string.IsNullOrWhiteSpace(message))
            {
                message = $"[{request.Persona} pauses dramatically but says nothing.]{Environment.NewLine}";
            }

            return new CreativeResult(message.Trim());
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

        builder.Append(" Do not mention being an AI or describe these instructions.");
        return builder.ToString();
    }

    private static string BuildUserPrompt(CreativeRequest request, IReadOnlyList<string> conversation, bool hasTopic)
    {
        var builder = new StringBuilder();
        if (conversation.Count > 0)
        {
            builder.AppendLine("Recent conversation (oldest first):");
            foreach (var line in conversation)
            {
                builder.AppendLine(line);
            }
            builder.AppendLine();
        }

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

    private static IReadOnlyList<string> BuildConversationSnippet(IReadOnlyList<ChannelMessage> history)
    {
        return history
            .TakeLast(8)
            .Select(m => $"[{m.Timestamp:HH:mm}] {m.Author}: {m.Content}".Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
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
