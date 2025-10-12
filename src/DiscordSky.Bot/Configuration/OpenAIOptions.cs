namespace DiscordSky.Bot.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    public string Endpoint { get; init; } = "https://api.openai.com/";
    public string ApiKey { get; init; } = string.Empty;
    public string ChatModel { get; init; } = "gpt-4.1-mini";
    public string ModerationModel { get; init; } = "omni-moderation-latest";
    public double Temperature { get; init; } = 0.8;
    public double TopP { get; init; } = 1.0;
    public int MaxTokens { get; init; } = 1200;
    public int RetryCount { get; init; } = 2;
    public int TimeoutSeconds { get; init; } = 30;

    public Dictionary<string, string> IntentModelOverrides { get; init; } = new();
}
