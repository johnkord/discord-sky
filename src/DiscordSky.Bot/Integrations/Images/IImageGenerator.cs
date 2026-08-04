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
        return int.TryParse(version, out var major) && major >= 2;
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
public sealed record ImageRequestOptions(string Model, string Size, string Quality, string OutputFormat, string Moderation)
{
    public static ImageRequestOptions FromConfig(ImageOptions o, ImageTier tier = ImageTier.Commissioned)
    {
        ImageModelPolicy.EnsureApproved(o.Model);
        var model = o.Model;
        var quality = o.Quality;

        // The high-quality tier is gated: clamp to medium unless explicitly allowed.
        if (!o.AllowHighQuality && string.Equals(quality, "high", StringComparison.OrdinalIgnoreCase))
        {
            quality = "medium";
        }
        return new ImageRequestOptions(model, o.Size, quality, o.OutputFormat, o.Moderation);
    }
}

/// <summary>Outcome of one generation. <see cref="Success"/> false carries a short machine code in <see cref="Error"/>.</summary>
public sealed record ImageResult(bool Success, byte[]? Bytes, string FileExtension, string? RevisedPrompt, string? Error)
{
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
}

/// <summary>The image-generation seam. Tests use <see cref="NoOpImageGenerator"/> or a stub.</summary>
public interface IImageGenerator
{
    /// <summary>True when a real backend is wired (enabled + an API key was found).</summary>
    bool IsEnabled { get; }

    Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken);
}

/// <summary>Disabled generator: used in tests and whenever <c>Image:Enabled</c> is false or no key is configured.</summary>
public sealed class NoOpImageGenerator : IImageGenerator
{
    public bool IsEnabled => false;

    public Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken)
        => Task.FromResult(ImageResult.Fail(ImageResult.ErrorDisabled));
}

/// <summary>
/// OpenAI-backed generator over the Image API (<c>OpenAI.Images.ImageClient</c>). Built against OpenAI SDK 2.8.0.
///
/// <para>Two SDK gotchas this code is written around, both verified against the 2.8.0 assembly:</para>
/// <list type="bullet">
/// <item><description><see cref="GeneratedImageQuality"/>.High serializes to <c>"hd"</c> (the DALL-E value), so quality
/// is constructed from the config string instead, yielding the gpt-image values low/medium/high/auto.</description></item>
/// <item><description><c>response_format</c> is not a valid parameter for gpt-image models (they always return base64),
/// so <see cref="ImageGenerationOptions.ResponseFormat"/> is intentionally left unset and we read <see cref="GeneratedImage.ImageBytes"/>.</description></item>
/// </list>
/// </summary>
public sealed class OpenAIImageGenerator : IImageGenerator
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

    public async Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken)
    {
        if (!_providerGuard.TryBeginCall(
                options.Model,
                ownsCircuitLease: true,
                out var lease,
                out var guard))
        {
            _logger.LogInformation("Image generation held by provider guard: {Reason}.", guard.Reason);
            return ImageResult.Fail(ImageResult.ErrorRateLimited);
        }

        try
        {
            var generationOptions = new ImageGenerationOptions
            {
                Quality = new GeneratedImageQuality(options.Quality.ToLowerInvariant()),
                Size = ParseSize(options.Size),
                OutputFileFormat = new GeneratedImageFileFormat(NormalizeFormat(options.OutputFormat)),
                ModerationLevel = new GeneratedImageModerationLevel(options.Moderation.ToLowerInvariant()),
                // ResponseFormat intentionally unset: gpt-image models reject response_format and return base64 by default.
            };

            // The model is bound per call so the spontaneous (fast) and commissioned (quality) tiers can use
            // different GPT Image models from one generator. GetImageClient is a cheap wrapper.
            var client = _openAiClient.GetImageClient(options.Model);
            ClientResult<GeneratedImage> result = await client.GenerateImageAsync(prompt, generationOptions, cancellationToken);
            _providerGuard.RecordFixedCostSuccess(
                lease,
                ImageCost.Estimate(options.Model, options.Quality));
            var image = result.Value;
            var bytes = image.ImageBytes?.ToArray();
            if (bytes is null || bytes.Length == 0)
            {
                return ImageResult.Fail(ImageResult.ErrorEmpty);
            }
            return ImageResult.Ok(bytes, ExtensionFor(options.OutputFormat), image.RevisedPrompt);
        }
        catch (OperationCanceledException)
        {
            _providerGuard.RecordCallFailure(lease, new OperationCanceledException());
            throw;
        }
        catch (ClientResultException ex)
        {
            _providerGuard.RecordCallFailure(lease, ex);
            var code = Classify(ex);
            // The API error message is safe to log (it describes the request problem, not user content) and
            // is the fastest way to diagnose org-verification (403) or bad-parameter failures.
            _logger.LogWarning("Image generation API error: status={Status} code={Code} message={Message}", ex.Status, code, ex.Message);
            return ImageResult.Fail(code);
        }
        catch (Exception ex)
        {
            _providerGuard.RecordCallFailure(lease, ex);
            _logger.LogWarning(ex, "Image generation failed unexpectedly.");
            return ImageResult.Fail(ImageResult.ErrorGeneric);
        }
    }

    private static string Classify(ClientResultException ex)
    {
        var message = ex.Message ?? string.Empty;
        var looksModeration = message.Contains("moderation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("safety", StringComparison.OrdinalIgnoreCase)
            || message.Contains("content policy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("content_policy", StringComparison.OrdinalIgnoreCase);

        if (ex.Status == 400 && looksModeration) return ImageResult.ErrorModerationBlocked;
        if (ex.Status == 429) return ImageResult.ErrorRateLimited;
        if (ex.Status >= 500) return ImageResult.ErrorServer;
        return ImageResult.ErrorGeneric;
    }

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
/// Rough per-image cost estimate (USD) used for telemetry and the monthly guard. Figures track the
/// pricing table in docs/image_generation_design.md section 5; they are deliberately approximate (real
/// cost is token-based) but good enough to bound spend.
/// </summary>
internal static class ImageCost
{
    public static double Estimate(string model, string quality)
    {
        ImageModelPolicy.EnsureApproved(model);
        var q = (quality ?? string.Empty).ToLowerInvariant();
        return q switch { "low" => 0.006, "medium" => 0.05, "high" => 0.21, _ => 0.05 };
    }
}
