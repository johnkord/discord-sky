using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Models;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Services;

public sealed class HeckleCycleService
{
    private readonly IOpenAiClient _openAiClient;
    private readonly OpenAIOptions _options;
    private readonly ILogger<HeckleCycleService> _logger;

    public HeckleCycleService(
        IOpenAiClient openAiClient,
        IOptions<OpenAIOptions> options,
        ILogger<HeckleCycleService> logger)
    {
        _openAiClient = openAiClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<HeckleResponse> BuildResponseAsync(
        HeckleTrigger trigger,
        ChaosSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.IsQuietHour(trigger.Timestamp))
        {
            return new HeckleResponse(
                Reminder: $"Shh… quiet hours. Logging '{trigger.Declaration}' for later mischief.",
                FollowUpCelebration: string.Empty,
                NextNudgeAt: trigger.Timestamp.AddHours(1)
            );
        }

        var delayMinutes = CalculateDelayMinutes(settings);

        var systemPrompt = "You orchestrate playful heckles and hype messages to keep projects moving. Respond with JSON containing reminder, celebration message, and the minutes until the next follow-up.";
        var userPrompt = $@"Username: {trigger.Username}
Declaration: {trigger.Declaration}
Annoyance level: {settings.AnnoyanceLevel:0.00}
Preferred next nudge minutes: {delayMinutes}
Trigger delivered already? {trigger.Delivered}";

        var responseFormat = new OpenAiResponseFormat
        {
            Type = "json_schema",
            JsonSchema = new OpenAiJsonSchema
            {
                Name = "heckle_cycle",
                Schema = new
                {
                    type = "object",
                    properties = new
                    {
                        reminder = new { type = "string" },
                        celebration = new { type = "string" },
                        nudge_minutes = new { type = "integer", minimum = 5, maximum = 120 }
                    },
                    required = new[] { "reminder", "celebration" }
                }
            }
        };

        var aiRequest = new OpenAiResponseRequest
        {
            Model = _options.ChatModel,
            Instructions = systemPrompt,
            Input = new[] { OpenAiResponseInputItem.FromText("user", userPrompt) },
            Temperature = Math.Clamp(_options.Temperature * (0.8 + settings.AnnoyanceLevel / 4), 0.1, 1.2),
            TopP = _options.TopP,
            MaxOutputTokens = Math.Clamp(_options.MaxTokens, 300, 1024),
            Text = new OpenAiResponseText { Format = responseFormat }
        };

        try
        {
            var response = await _openAiClient.CreateResponseAsync(aiRequest, cancellationToken);
            if (OpenAiResponseParser.TryGetJsonDocument(response, out var document) && document is not null)
            {
                return ParseResponse(document.RootElement, trigger, delayMinutes);
            }

            _logger.LogWarning("HeckleCycleService received unstructured response: {Response}", OpenAiResponseParser.ExtractPrimaryText(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to build heckle response via OpenAI");
        }

        return BuildFallback(trigger, delayMinutes);
    }

    private static int CalculateDelayMinutes(ChaosSettings settings) => settings.AnnoyanceLevel switch
    {
        >= 0.8 => 15,
        >= 0.5 => 30,
        _ => 60
    };

    private static HeckleResponse ParseResponse(JsonElement root, HeckleTrigger trigger, int defaultDelay)
    {
        var reminder = root.TryGetProperty("reminder", out var reminderElement)
            ? reminderElement.GetString() ?? $"Friendly chaos ping! {trigger.Username}, how's '{trigger.Declaration}' doing?"
            : $"Friendly chaos ping! {trigger.Username}, how's '{trigger.Declaration}' doing?";

        var celebration = root.TryGetProperty("celebration", out var celebrationElement)
            ? celebrationElement.GetString() ?? $"🎉 {trigger.Username} actually finished '{trigger.Declaration}'!"
            : $"🎉 {trigger.Username} actually finished '{trigger.Declaration}'!";

        var minutes = defaultDelay;
        if (root.TryGetProperty("nudge_minutes", out var minutesElement) && minutesElement.ValueKind == JsonValueKind.Number)
        {
            minutes = minutesElement.GetInt32();
        }

        return new HeckleResponse(reminder, celebration, trigger.Timestamp.AddMinutes(Math.Clamp(minutes, 5, 240)));
    }

    private static HeckleResponse BuildFallback(HeckleTrigger trigger, int delayMinutes)
    {
        var reminder = $"👀 {trigger.Username}, future-you just called and asked where that '{trigger.Declaration}' update is.";
        var celebration = $"🎉 {trigger.Username} actually finished '{trigger.Declaration}'!";
        return new HeckleResponse(reminder, celebration, trigger.Timestamp.AddMinutes(delayMinutes));
    }
}
