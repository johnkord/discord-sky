namespace DiscordSky.Bot.Configuration;

/// <summary>
/// Provider-agnostic LLM configuration.
/// Replaces the old <c>OpenAIOptions</c> class to support multiple providers
/// (OpenAI, xAI/Grok, or any OpenAI-compatible endpoint) selected at config time.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "LLM";

    /// <summary>
    /// Which provider block to activate. Must be a key under <c>LLM:Providers</c>
    /// (e.g. "OpenAI", "xAI"). Case-insensitive.
    /// </summary>
    public string ActiveProvider { get; init; } = "OpenAI";

    /// <summary>
    /// Named provider configurations. The key chosen by <see cref="ActiveProvider"/>
    /// supplies all runtime values.
    /// </summary>
    public Dictionary<string, LlmProviderOptions> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Convenience accessors for the active provider ────────────────

    /// <summary>Returns the currently active provider config, or throws if not found.</summary>
    public LlmProviderOptions GetActiveProvider()
    {
        if (Providers.TryGetValue(ActiveProvider, out var provider))
            return provider;

        throw new InvalidOperationException(
            $"LLM provider '{ActiveProvider}' is not configured. " +
            $"Available providers: [{string.Join(", ", Providers.Keys)}]");
    }
}

/// <summary>
/// Configuration for a single LLM provider. Identical schema regardless of
/// whether the backend is OpenAI, xAI, or another OpenAI-compatible service.
/// </summary>
public sealed class LlmProviderOptions
{
    /// <summary>
    /// API key for this provider.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Base endpoint URI. Defaults to OpenAI's endpoint when null/empty.
    /// For xAI, set to <c>https://api.x.ai/v1</c>.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Default chat model name (e.g. "gpt-5.2", "grok-4-1-fast-reasoning").
    /// </summary>
    public string ChatModel { get; init; } = "gpt-4.1-mini";

    /// <summary>
    /// Maximum output tokens per response.
    /// </summary>
    public int MaxTokens { get; init; } = 1200;

    /// <summary>
    /// Per-persona model overrides. Key = persona name, Value = model name.
    /// All models must be available on this provider.
    /// </summary>
    public Dictionary<string, string> IntentModelOverrides { get; init; } = new();

    /// <summary>
    /// Model to use for memory extraction and consolidation.
    /// Defaults to <see cref="ChatModel"/> when null/empty.
    /// Should be a cheap/fast model on this provider (e.g. "gpt-5.2" for OpenAI, "grok-4-1-fast-non-reasoning" for xAI).
    /// </summary>
    public string? MemoryExtractionModel { get; init; }

    /// <summary>
    /// Reasoning effort level (e.g. "low", "medium", "high").
    /// Leave null/empty for models that don't support it (e.g. grok-4-0709 which always reasons).
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Reasoning summary output mode.
    /// </summary>
    public string? ReasoningSummary { get; init; }
}
