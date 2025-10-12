using System.Linq;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Models;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Services;

public sealed class BitStarterService
{
    private readonly IOpenAiClient _openAiClient;
    private readonly OpenAIOptions _options;
    private readonly ILogger<BitStarterService> _logger;

    public BitStarterService(
        IOpenAiClient openAiClient,
        IOptions<OpenAIOptions> options,
        ILogger<BitStarterService> logger)
    {
        _openAiClient = openAiClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BitStarterResponse> GenerateAsync(
        BitStarterRequest request,
        ChaosSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
        {
            throw new ArgumentException("Topic must be provided", nameof(request));
        }

        var participantPool = request.Participants.Count > 0
            ? request.Participants
            : new[] { "the void" };

        var maxLines = Math.Clamp((int)Math.Round(settings.MaxScriptLines * request.ChaosMultiplier), 3, 12);
        var normalizedParticipants = participantPool.Select(p => p.Trim()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var systemPrompt = "You are a mischievous writer crafting short chaotic discord scripts. Respond using playful, energetic tone while respecting server-safe language.";

        var userPrompt = $@"Topic: {request.Topic}
Participants: {(normalizedParticipants.Length == 0 ? "the void" : string.Join(", ", normalizedParticipants))}
Desired line count: {maxLines}
Annoyance level: {settings.AnnoyanceLevel:0.00}
Chaos multiplier: {request.ChaosMultiplier:0.00}
Return JSON with a short title, an array of exactly {{line_count}} script_lines, and a list of mention handles derived from participants.";

        var responseFormat = new OpenAiResponseFormat
        {
            Type = "json_schema",
            JsonSchema = new OpenAiJsonSchema
            {
                Name = "bit_starter_payload",
                Schema = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        script_lines = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            minItems = 1,
                            maxItems = maxLines
                        },
                        mentions = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        }
                    },
                    required = new[] { "title", "script_lines" }
                }
            }
        };

        var aiRequest = new OpenAiResponseRequest
        {
            Model = _options.ChatModel,
            Instructions = systemPrompt,
            Input = new[] { OpenAiResponseInputItem.FromText("user", userPrompt) },
            Temperature = Math.Clamp(_options.Temperature * (0.9 + settings.AnnoyanceLevel / 2), 0.1, 1.3),
            TopP = _options.TopP,
            MaxOutputTokens = Math.Clamp(_options.MaxTokens, 400, 2048),
            Text = new OpenAiResponseText { Format = responseFormat }
        };

        try
        {
            var response = await _openAiClient.CreateResponseAsync(aiRequest, cancellationToken);
            if (OpenAiResponseParser.TryGetJsonDocument(response, out var document) && document is not null)
            {
                return ParseResponse(document.RootElement, normalizedParticipants);
            }

            _logger.LogWarning("BitStarterService received unstructured response: {Response}", OpenAiResponseParser.ExtractPrimaryText(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to generate bit starter via OpenAI");
        }

        return FallbackScript(request.Topic, normalizedParticipants, maxLines);
    }

    private static BitStarterResponse ParseResponse(JsonElement root, IReadOnlyList<string> participants)
    {
        var title = root.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? "Chaos Interlude"
            : "Chaos Interlude";

        var lines = root.TryGetProperty("script_lines", out var scriptElement) && scriptElement.ValueKind == JsonValueKind.Array
            ? scriptElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
            : Array.Empty<string>();

        var mentions = root.TryGetProperty("mentions", out var mentionsElement) && mentionsElement.ValueKind == JsonValueKind.Array
            ? mentionsElement.EnumerateArray().Select(e => SanitizeMention(e.GetString())).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : participants.Select(SanitizeMention).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new BitStarterResponse(title, lines, mentions);
    }

    private static BitStarterResponse FallbackScript(string topic, IReadOnlyList<string> participants, int maxLines)
    {
        var mentionTags = participants
            .Select(SanitizeMention)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var scriptLines = Enumerable.Range(0, maxLines)
            .Select(i => $"[{i + 1}] {topic} escalates until the goblins cheer.")
            .ToArray();

        return new BitStarterResponse($"Operation {topic}", scriptLines, mentionTags);
    }

    private static string SanitizeMention(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim().TrimStart('@');
        var normalized = new string(trimmed.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : "@" + normalized;
    }
}
