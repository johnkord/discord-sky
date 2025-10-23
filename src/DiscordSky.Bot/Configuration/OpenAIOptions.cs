namespace DiscordSky.Bot.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    public string Endpoint { get; init; } = "https://api.openai.com/";
    public string ApiKey { get; init; } = string.Empty;
    public string ChatModel { get; init; } = "gpt-4.1-mini";
    public string ModerationModel { get; init; } = "omni-moderation-latest";
    public int MaxTokens { get; init; } = 1200;
    public int RetryCount { get; init; } = 2;
    public int TimeoutSeconds { get; init; } = 30;
    public string VisionDetail { get; init; } = "auto";

    public Dictionary<string, string> IntentModelOverrides { get; init; } = new();

    public OpenAiReasoningOptions? Reasoning { get; init; }
}

public sealed class OpenAiReasoningOptions
{
    public string? Effort { get; init; }
    public string? Summary { get; init; }
}
