using System.Linq;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Models;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Services;

public sealed class GremlinStudioService
{
    private readonly IOpenAiClient _openAiClient;
    private readonly OpenAIOptions _options;
    private readonly ILogger<GremlinStudioService> _logger;

    public GremlinStudioService(
        IOpenAiClient openAiClient,
        IOptions<OpenAIOptions> options,
        ILogger<GremlinStudioService> logger)
    {
        _openAiClient = openAiClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GremlinArtifact> RemixAsync(
        GremlinPrompt prompt,
        ChaosSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (prompt.Attachments.Count == 0)
        {
            return new GremlinArtifact(
                prompt.PreferredKind,
                $"{prompt.Seed} Remix",
                new[] { "Need at least one attachment – toss the Gremlin a bone!" }
            );
        }

        var systemPrompt = "You are the Gremlin Studio director. Generate outrageous remix concepts tailored to the requested artifact kind. Use JSON to describe the remix plan, including prompts for assets.";
        var userPrompt = BuildUserPrompt(prompt, settings);

        var responseFormat = new OpenAiResponseFormat
        {
            Type = "json_schema",
            JsonSchema = new OpenAiJsonSchema
            {
                Name = "gremlin_artifact",
                Schema = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        payloads = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            minItems = 1,
                            maxItems = 6
                        }
                    },
                    required = new[] { "title", "payloads" }
                }
            }
        };

        var aiRequest = new OpenAiResponseRequest
        {
            Model = _options.ChatModel,
            Instructions = systemPrompt,
            Input = new[] { OpenAiResponseInputItem.FromText("user", userPrompt) },
            Temperature = Math.Clamp(_options.Temperature * (1.0 + settings.AnnoyanceLevel / 3), 0.1, 1.4),
            TopP = _options.TopP,
            MaxOutputTokens = Math.Clamp(_options.MaxTokens, 400, 2048),
            Text = new OpenAiResponseText { Format = responseFormat }
        };

        try
        {
            var response = await _openAiClient.CreateResponseAsync(aiRequest, cancellationToken);
            if (OpenAiResponseParser.TryGetJsonDocument(response, out var document) && document is not null)
            {
                var artifact = ParseResponse(document.RootElement, prompt.PreferredKind);
                return artifact;
            }

            _logger.LogWarning("GremlinStudioService received unstructured response: {Response}", OpenAiResponseParser.ExtractPrimaryText(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to remix prompt via OpenAI");
        }

        return new GremlinArtifact(prompt.PreferredKind, $"Gremlin Remix: {prompt.Seed}", BuildFallbackPayloads(prompt));
    }

    private static string BuildUserPrompt(GremlinPrompt prompt, ChaosSettings settings)
    {
        var attachments = string.Join(", ", prompt.Attachments.Take(5));
        return $@"Seed: {prompt.Seed}
Attachments: {attachments}
Artifact kind: {prompt.PreferredKind}
Annoyance level: {settings.AnnoyanceLevel:0.00}
Return JSON with title and a payloads array of remix outputs.";
    }

    private static GremlinArtifact ParseResponse(JsonElement root, GremlinArtifactKind kind)
    {
        var title = root.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? $"Gremlin Remix: {Guid.NewGuid():N}".Substring(0, 8)
            : $"Gremlin Remix: {Guid.NewGuid():N}".Substring(0, 8);

        var payloads = root.TryGetProperty("payloads", out var payloadsElement) && payloadsElement.ValueKind == JsonValueKind.Array
            ? payloadsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
            : Array.Empty<string>();

        return new GremlinArtifact(kind, title, payloads);
    }

    private static IReadOnlyList<string> BuildFallbackPayloads(GremlinPrompt prompt)
    {
        var attachmentSummary = string.Join(", ", prompt.Attachments.Take(3));
        return new[]
        {
            $"Compose a remix featuring {prompt.Seed} colliding with {attachmentSummary}.",
            "Layer glitchy textures and sprinkle goblin energy throughout."
        };
    }

}
