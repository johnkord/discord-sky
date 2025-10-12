using System.Linq;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Models;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Services;

public sealed class MischiefQuestService
{
    private static readonly string[] RewardNames = Enum.GetNames(typeof(QuestRewardKind));

    private readonly IOpenAiClient _openAiClient;
    private readonly OpenAIOptions _options;
    private readonly ILogger<MischiefQuestService> _logger;

    public MischiefQuestService(
        IOpenAiClient openAiClient,
        IOptions<OpenAIOptions> options,
        ILogger<MischiefQuestService> logger)
    {
        _openAiClient = openAiClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MischiefQuest> DrawQuestAsync(
        ChaosSettings settings,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = "You are the Mischief Quest designer. Create playful, short quests for Discord crews, balancing chaos with achievable steps. Respond as JSON with title, steps, reward kind, and reward description.";
        var userPrompt = $@"Annoyance level: {settings.AnnoyanceLevel:0.00}
Max steps: {Math.Clamp(settings.MaxScriptLines, 3, 8)}
Reward options: {string.Join(", ", RewardNames)}";

        var responseFormat = new OpenAiResponseFormat
        {
            Type = "json_schema",
            JsonSchema = new OpenAiJsonSchema
            {
                Name = "mischief_quest",
                Schema = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        steps = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            minItems = 3,
                            maxItems = 8
                        },
                        reward_kind = new { type = "string" },
                        reward_description = new { type = "string" }
                    },
                    required = new[] { "title", "steps", "reward_kind" }
                }
            }
        };

        var aiRequest = new OpenAiResponseRequest
        {
            Model = _options.ChatModel,
            Instructions = systemPrompt,
            Input = new[] { OpenAiResponseInputItem.FromText("user", userPrompt) },
            Temperature = Math.Clamp(_options.Temperature * (0.8 + settings.AnnoyanceLevel / 2), 0.1, 1.2),
            TopP = _options.TopP,
            MaxOutputTokens = Math.Clamp(_options.MaxTokens, 300, 1200),
            Text = new OpenAiResponseText { Format = responseFormat }
        };

        try
        {
            var response = await _openAiClient.CreateResponseAsync(aiRequest, cancellationToken);
            if (OpenAiResponseParser.TryGetJsonDocument(response, out var document) && document is not null)
            {
                return ParseResponse(document.RootElement, settings);
            }

            _logger.LogWarning("MischiefQuestService received unstructured response: {Response}", OpenAiResponseParser.ExtractPrimaryText(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to draw quest via OpenAI");
        }

        return BuildFallback(settings);
    }

    private static MischiefQuest ParseResponse(JsonElement root, ChaosSettings settings)
    {
        var title = root.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? "Mischief Quest"
            : "Mischief Quest";

        var steps = root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array
            ? stepsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
            : Array.Empty<string>();

        if (settings.AnnoyanceLevel >= 0.8 && steps.Length < 8)
        {
            steps = steps.Concat(new[] { "Bonus chaos: recruit one more goblin to escalate." }).ToArray();
        }

        var rewardKindRaw = root.TryGetProperty("reward_kind", out var rewardElement)
            ? rewardElement.GetString()
            : null;

        if (!Enum.TryParse<QuestRewardKind>(rewardKindRaw, ignoreCase: true, out var rewardKind))
        {
            rewardKind = QuestRewardKind.CustomBadge;
        }

        var description = root.TryGetProperty("reward_description", out var descriptionElement)
            ? descriptionElement.GetString() ?? "Chaos badge unlocked!"
            : "Chaos badge unlocked!";

        return new MischiefQuest(title, steps, rewardKind, description);
    }

    private static MischiefQuest BuildFallback(ChaosSettings settings)
    {
        var steps = new List<string>
        {
            "Share a prompt that the server can riff on.",
            "React to three messages with creative emojis.",
            "Post a final recap gif in the quest channel."
        };

        if (settings.AnnoyanceLevel >= 0.8)
        {
            steps.Add("Bonus chaos: nominate a co-conspirator and pass the goblin torch.");
        }

        return new MischiefQuest(
            "Sparkstorm Quest",
            steps,
            QuestRewardKind.CustomBadge,
            "Badge unlocked: Sparkstorm Instigator");
    }
}
