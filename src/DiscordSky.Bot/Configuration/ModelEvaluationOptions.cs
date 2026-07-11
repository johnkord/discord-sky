namespace DiscordSky.Bot.Configuration;

/// <summary>Configuration for side-effect-free challenger evaluation alongside the production champion.</summary>
public sealed class ModelEvaluationOptions
{
    public const string SectionName = "ModelEvaluation";

    public GrokColdOpenShadowOptions GrokColdOpen { get; init; } = new();
}

/// <summary>Runs Grok on eligible cold-open opportunities without allowing it to post or mutate bot state.</summary>
public sealed class GrokColdOpenShadowOptions
{
    public bool Enabled { get; init; } = false;
    public string ProviderName { get; init; } = "xAI";
    public string Model { get; init; } = "grok-4.5";
    public string ReasoningEffort { get; init; } = "medium";
    public double SampleRate { get; init; } = 1.0;
    public int QueueCapacity { get; init; } = 32;
}