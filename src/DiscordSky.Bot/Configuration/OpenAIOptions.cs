namespace DiscordSky.Bot.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    public string ApiKey { get; init; } = string.Empty;
    public string ChatModel { get; init; } = "gpt-4.1-mini";
    public int MaxTokens { get; init; } = 1200;

    public Dictionary<string, string> IntentModelOverrides { get; init; } = new();

    public string? ReasoningEffort { get; init; }
    public string? ReasoningSummary { get; init; }
}
