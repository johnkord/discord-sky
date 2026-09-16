using System.ClientModel;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Images;
using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Bot.Integrations.Images;

/// <summary>Which quality/speed tier to render at, chosen by how the image was requested.</summary>
public enum ImageTier
{
    /// <summary>Explicit request (command, direct reply, "draw me ..."): quality model, person opted into the wait.</summary>
    Commissioned,
    /// <summary>Spontaneous/ambient surprise. Kept for budgets and telemetry, never for a model downgrade.</summary>
    Spontaneous,
}

internal static class ImageModelPolicy
{
    private const string Prefix = "gpt-image-";

    public static bool IsApproved(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || !model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || model.Contains("mini", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionAndSuffix = model.AsSpan(Prefix.Length);
        var separator = versionAndSuffix.IndexOf('-');
        var version = separator >= 0 ? versionAndSuffix[..separator] : versionAndSuffix;
        return (int.TryParse(version, out var major) && major >= 2) ||
            (Version.TryParse(version.ToString(), out var release) && release.Major >= 2);
    }

    public static void EnsureApproved(string? model)
    {
        if (!IsApproved(model))
        {
            throw new InvalidOperationException(
                $"Image model '{model ?? "<null>"}' is prohibited. Configure gpt-image-2 or a newer non-mini model.");
        }
    }
}

/// <summary>Per-request image parameters, resolved from <see cref="ImageOptions"/> at call time.</summary>
public sealed record ImageRequestOptions(
    string Model,
    string Size,
    string Quality,
    string OutputFormat,
    string Moderation,
    string Background = "opaque",
    int? OutputCompression = null,
    int PartialImages = 0)
{
    public static ImageRequestOptions FromConfig(
        ImageOptions options,
        ImageTier tier = ImageTier.Commissioned,
        ImageRenderSettings? requested = null,
        bool editing = false) => ImageRenderPolicy.Resolve(options, requested, editing);
}

/// <summary>Outcome of one generation. <see cref="Success"/> false carries a short machine code in <see cref="Error"/>.</summary>
public sealed record ImageResult(bool Success, byte[]? Bytes, string FileExtension, string? RevisedPrompt, string? Error)
{
    public ImageUsage? Usage { get; init; }
    public double? CostUsd { get; init; }
    public string? CostBasis { get; init; }
    public string? RequestId { get; init; }
    public int? HttpStatus { get; init; }
    public string? ProviderErrorCode { get; init; }
    public string? ModerationStage { get; init; }
    public IReadOnlyList<string>? ModerationCategories { get; init; }
    public int PreviewCount { get; init; }
    public string? ActualSize { get; init; }
    public string? ActualQuality { get; init; }

    public static ImageResult Ok(byte[] bytes, string extension, string? revisedPrompt) =>
        new(true, bytes, extension, revisedPrompt, null);

    public static ImageResult Fail(string error) => new(false, null, "jpg", null, error);

    // Machine codes mapped to in-character replies by the command handler.
    public const string ErrorModerationBlocked = "moderation_blocked";
    public const string ErrorRateLimited = "rate_limited";
    public const string ErrorServer = "server_error";
    public const string ErrorEmpty = "empty_result";
    public const string ErrorDisabled = "disabled";
    public const string ErrorGeneric = "error";
    public const string ErrorInvalidRequest = "invalid_request";
    public const string ErrorCancelled = "cancelled";
    public const string ErrorTooLarge = "image_too_large";
}

/// <summary>The image-generation seam. Tests use <see cref="NoOpImageGenerator"/> or a stub.</summary>
public interface IImageGenerator
{
    /// <summary>True when a real backend is wired (enabled + an API key was found).</summary>
    bool IsEnabled { get; }

    Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken);

    Task<ImageResult> RenderAsync(ImageRenderRequest request, CancellationToken cancellationToken) =>
        request.References is { Count: > 0 }
            ? Task.FromResult(ImageResult.Fail(ImageResult.ErrorInvalidRequest))
            : GenerateAsync(request.Prompt, request.Options, cancellationToken);
}

/// <summary>Disabled generator: used in tests and whenever <c>Image:Enabled</c> is false or no key is configured.</summary>
public sealed class NoOpImageGenerator : IImageGenerator
{
    public bool IsEnabled => false;

    public Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken)
        => Task.FromResult(ImageResult.Fail(ImageResult.ErrorDisabled));
}

/// <summary>
/// OpenAI Image API generation and edits using the SDK's JSON, multipart, and SSE protocol methods.
/// </summary>
public sealed partial class OpenAIImageGenerator : IImageGenerator
{
    private readonly OpenAIClient _openAiClient;
    private readonly ILogger<OpenAIImageGenerator> _logger;
    private readonly LlmProviderGuard _providerGuard;

    public OpenAIImageGenerator(
        OpenAIClient openAiClient,
        LlmProviderGuard providerGuard,
        ILogger<OpenAIImageGenerator> logger)
    {
        _openAiClient = openAiClient;
        _providerGuard = providerGuard;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken) =>
        RenderAsync(new ImageRenderRequest(prompt, options), cancellationToken);

    internal static GeneratedImageSize ParseSize(string size)
    {
        var parts = (size ?? string.Empty).Split('x', 'X');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out var w)
            && int.TryParse(parts[1].Trim(), out var h)
            && w > 0 && h > 0)
        {
            return new GeneratedImageSize(w, h);
        }
        return new GeneratedImageSize(1024, 1024);
    }

    // The API enum expects png/jpeg/webp; accept the common "jpg" alias.
    internal static string NormalizeFormat(string format)
    {
        var f = (format ?? "jpeg").Trim().ToLowerInvariant();
        return f == "jpg" ? "jpeg" : f;
    }

    // The Discord attachment filename extension.
    internal static string ExtensionFor(string format)
    {
        var f = NormalizeFormat(format);
        return f == "jpeg" ? "jpg" : f;
    }
}

/// <summary>
/// Legacy fallback estimates. GPT Image 2.5 uses returned usage or a labeled reservation estimate.
/// </summary>
internal static class ImageCost
{
    public static double Estimate(string model, string quality)
    {
        ImageModelPolicy.EnsureApproved(model);
        if (model.StartsWith("gpt-image-2.5", StringComparison.OrdinalIgnoreCase))
            return ImageUsageCost.Reservation(new ImageRenderRequest("", new(model, "1024x1024", quality, "jpeg", "auto")));
        var q = (quality ?? string.Empty).ToLowerInvariant();
        return q switch { "low" => 0.006, "medium" => 0.05, "high" => 0.21, _ => 0.05 };
    }
}
