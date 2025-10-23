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

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiReasoningConfig? Reasoning { get; init; }
}

public sealed class OpenAiResponseInputItem
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public IReadOnlyList<OpenAiResponseInputContent> Content { get; init; } = Array.Empty<OpenAiResponseInputContent>();

    public static OpenAiResponseInputItem FromText(string role, string text)
    {
        return FromContent(role, new[]
        {
            OpenAiResponseInputContent.FromText(text)
        });
    }

    public static OpenAiResponseInputItem FromContent(string role, IReadOnlyList<OpenAiResponseInputContent> content)
    {
        return new OpenAiResponseInputItem
        {
            Role = role,
            Content = content
        };
    }
}

public sealed class OpenAiResponseInputContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    public static OpenAiResponseInputContent FromText(string text)
    {
        return new OpenAiResponseInputContent
        {
            Type = "input_text",
            Text = text ?? string.Empty
        };
    }

    public static OpenAiResponseInputContent FromImage(Uri url, string? detail)
    {
        return new OpenAiResponseInputContent
        {
            Type = "input_image",
            ImageUrl = url.ToString(),
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail
        };
    }
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

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiResponseOutputContent>? Content { get; init; }
}

public sealed class OpenAiResponseOutputContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

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

public sealed class OpenAiReasoningConfig
{
    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }
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

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Parameters { get; init; }

    [JsonPropertyName("function")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiToolFunction? Function { get; init; }
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
