using System;
using System.Text.Json.Serialization;

namespace DiscordSky.Bot.Models.Orchestration;

public sealed class OpenAiResponseRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    [JsonPropertyName("input")]
    public IReadOnlyList<OpenAiResponseInputItem> Input { get; init; } = Array.Empty<OpenAiResponseInputItem>();

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("text")]
    public OpenAiResponseText? Text { get; init; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<OpenAiTool>? Tools { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; init; }

    public static OpenAiResponseRequest CreateSummarizationRequest(string model, string instructions, string content)
    {
        return new OpenAiResponseRequest
        {
            Model = model,
            Instructions = instructions,
            Input = new[] { OpenAiResponseInputItem.FromText("user", content) },
            Temperature = 0.4,
            TopP = 0.9,
            MaxOutputTokens = 300
        };
    }
}

public sealed class OpenAiResponseInputItem
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public IReadOnlyList<OpenAiResponseInputContent> Content { get; init; } = Array.Empty<OpenAiResponseInputContent>();

    public static OpenAiResponseInputItem FromText(string role, string text)
    {
        return new OpenAiResponseInputItem
        {
            Role = role,
            Content = new[]
            {
                new OpenAiResponseInputContent
                {
                    Type = "input_text",
                    Text = text ?? string.Empty
                }
            }
        };
    }
}

public sealed class OpenAiResponseInputContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_text";

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

public sealed class OpenAiResponse
{
    [JsonPropertyName("output")]
    public List<OpenAiResponseOutputItem> Output { get; init; } = new();

    [JsonPropertyName("error")]
    public OpenAiResponseError? Error { get; init; }
}

public sealed class OpenAiResponseOutputItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public List<OpenAiResponseOutputContent> Content { get; init; } = new();
}

public sealed class OpenAiResponseOutputContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("annotations")]
    public object? Annotations { get; init; }
}

public sealed class OpenAiResponseError
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class OpenAiResponseText
{
    [JsonPropertyName("format")]
    public OpenAiResponseFormat? Format { get; init; }
}

public sealed class OpenAiResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("json_schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiJsonSchema? JsonSchema { get; init; }
}

public sealed class OpenAiJsonSchema
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("schema")]
    public object Schema { get; init; } = new();
}

public sealed class OpenAiTool
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public OpenAiToolFunction Function { get; init; } = new();
}

public sealed class OpenAiToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public object Parameters { get; init; } = new();
}

public sealed class OpenAiModerationRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;
}

public sealed class OpenAiModerationResponse
{
    [JsonPropertyName("results")]
    public List<OpenAiModerationResult> Results { get; init; } = new();
}

public sealed class OpenAiModerationResult
{
    [JsonPropertyName("flagged")]
    public bool Flagged { get; init; }

    [JsonPropertyName("categories")]
    public Dictionary<string, bool>? Categories { get; init; }

    [JsonPropertyName("category_scores")]
    public Dictionary<string, double>? CategoryScores { get; init; }
}
