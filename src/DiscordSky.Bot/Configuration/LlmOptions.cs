using Microsoft.Extensions.AI;

namespace DiscordSky.Bot.Configuration;

public enum LlmWorkload
{
    Main,
    Ambient,
    Utility,
    ColdOpen,
    ColdOpenCritic,
    ImageRewrite,
    MemoryExtraction,
    MemoryConsolidation,
}

public readonly record struct LlmWorkloadProfile(
    string Model,
    string? ReasoningEffort,
    string? ReasoningSummary = null)
{
    public bool HasReasoning =>
        !string.IsNullOrWhiteSpace(ReasoningEffort) || !string.IsNullOrWhiteSpace(ReasoningSummary);

    public bool HasMaximumReasoning =>
        Enum.TryParse<ReasoningEffort>(ReasoningEffort, ignoreCase: true, out var effort)
        && effort == Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh;

    public int WithReasoningHeadroom(int normalMaxOutputTokens) =>
        HasMaximumReasoning ? Math.Max(normalMaxOutputTokens, 16_384) : normalMaxOutputTokens;

    public void ApplyReasoning(ChatOptions options)
    {
        if (!HasReasoning) return;
        options.Reasoning = new ReasoningOptions
        {
            Effort = string.IsNullOrWhiteSpace(ReasoningEffort)
                ? null
                : Enum.Parse<ReasoningEffort>(ReasoningEffort, ignoreCase: true),
            Output = string.IsNullOrWhiteSpace(ReasoningSummary)
                ? null
                : Enum.Parse<ReasoningOutput>(ReasoningSummary, ignoreCase: true),
        };
    }
}

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
    public string ChatModel { get; init; } = "gpt-5.6-sol";

    /// <summary>Lower-cost model for ambient generated replies. Falls back to <see cref="ChatModel"/>.</summary>
    public string? AmbientModel { get; init; }

    /// <summary>High-quality model for rare proactive cold-open composition. Falls back to <see cref="ChatModel"/>.</summary>
    public string? ColdOpenModel { get; init; }

    /// <summary>Balanced model for the advisory post-send cold-open audit. Falls back to <see cref="ChatModel"/>.</summary>
    public string? ColdOpenCriticModel { get; init; }

    /// <summary>Model that grounds and rewrites explicit image requests. Falls back to <see cref="ChatModel"/>.</summary>
    public string? ImageRewriteModel { get; init; }

    /// <summary>
    /// Maximum output tokens per response.
    /// </summary>
    public int MaxTokens { get; init; } = 1200;

    /// <summary>End-to-end deadline for one provider call. Clamped to 1-60 minutes by the client factory.</summary>
    public int RequestTimeoutMinutes { get; init; } = 15;

    /// <summary>
    /// Per-persona model overrides. Key = persona name, Value = model name.
    /// All models must be available on this provider.
    /// </summary>
    public Dictionary<string, string> IntentModelOverrides { get; init; } = new();

    /// <summary>
    /// Model to use for memory extraction.
    /// Defaults to <see cref="ChatModel"/> when null/empty.
    /// Should be a cheap/fast structured-output model (e.g. gpt-5.6-luna for OpenAI).
    /// </summary>
    public string? MemoryExtractionModel { get; init; }

    /// <summary>Model for rewriting a user's memory set at the cap. Falls back to MemoryExtractionModel, then ChatModel.</summary>
    public string? MemoryConsolidationModel { get; init; }

    /// <summary>
    /// Model for cheap, high-frequency utility calls (e.g. the in-character reaction judge).
    /// Defaults to <see cref="ChatModel"/> when null/empty. Should be the cheapest capable model on this
    /// provider (a mini/nano tier) so lightweight per-message decisions cost almost nothing.
    /// </summary>
    public string? UtilityModel { get; init; }

    /// <summary>
    /// Reasoning effort level ("none", "low", "medium", "high", or "ExtraHigh" for OpenAI xhigh).
    /// Leave null/empty for models that don't support it (e.g. grok-4-0709 which always reasons).
    /// </summary>
    public string? ReasoningEffort { get; init; }

    public string? AmbientReasoningEffort { get; init; }
    public string? UtilityReasoningEffort { get; init; }
    public string? ColdOpenReasoningEffort { get; init; }
    public string? ColdOpenCriticReasoningEffort { get; init; }
    public string? ImageRewriteReasoningEffort { get; init; }
    public string? MemoryExtractionReasoningEffort { get; init; }
    public string? MemoryConsolidationReasoningEffort { get; init; }

    /// <summary>
    /// Reasoning summary output mode.
    /// </summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>
    /// Whether to use OpenAI's Responses API (<c>/v1/responses</c>) instead of Chat Completions (<c>/v1/chat/completions</c>).
    /// Required for newer OpenAI models that need reasoning + tool calling together.
    /// Should be <c>false</c> for non-OpenAI providers (xAI, etc.) that don't fully support the Responses API.
    /// </summary>
    public bool UseResponsesApi { get; init; }

    public LlmWorkloadProfile GetProfile(LlmWorkload workload, string? persona = null)
    {
        var model = workload switch
        {
            LlmWorkload.Main => ResolveMainModel(persona, ChatModel),
            LlmWorkload.Ambient => ResolveMainModel(persona, AmbientModel),
            LlmWorkload.Utility => First(UtilityModel, ChatModel),
            LlmWorkload.ColdOpen => First(ColdOpenModel, ChatModel),
            LlmWorkload.ColdOpenCritic => First(ColdOpenCriticModel, ChatModel),
            LlmWorkload.ImageRewrite => First(ImageRewriteModel, ChatModel),
            LlmWorkload.MemoryExtraction => First(MemoryExtractionModel, ChatModel),
            LlmWorkload.MemoryConsolidation => First(MemoryConsolidationModel, MemoryExtractionModel, ChatModel),
            _ => ChatModel,
        };

        var effort = workload switch
        {
            LlmWorkload.Main => ReasoningEffort,
            LlmWorkload.Ambient => FirstOrNull(AmbientReasoningEffort, ReasoningEffort),
            LlmWorkload.Utility => UtilityReasoningEffort,
            LlmWorkload.ColdOpen => ColdOpenReasoningEffort,
            LlmWorkload.ColdOpenCritic => ColdOpenCriticReasoningEffort,
            LlmWorkload.ImageRewrite => ImageRewriteReasoningEffort,
            LlmWorkload.MemoryExtraction => MemoryExtractionReasoningEffort,
            LlmWorkload.MemoryConsolidation => MemoryConsolidationReasoningEffort,
            _ => null,
        };

        var summary = workload is LlmWorkload.Main or LlmWorkload.Ambient ? ReasoningSummary : null;
        return new LlmWorkloadProfile(model, effort, summary);
    }

    public IReadOnlyList<string> GetConfiguredModels() =>
        Enum.GetValues<LlmWorkload>()
            .Select(workload => GetProfile(workload).Model)
            .Concat(IntentModelOverrides.Values)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private string ResolveMainModel(string? persona, string? workloadModel)
    {
        if (!string.IsNullOrWhiteSpace(persona)
            && IntentModelOverrides.TryGetValue(persona, out var overrideModel)
            && !string.IsNullOrWhiteSpace(overrideModel))
        {
            return overrideModel;
        }
        return First(workloadModel, ChatModel);
    }

    private static string First(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? FirstOrNull(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
