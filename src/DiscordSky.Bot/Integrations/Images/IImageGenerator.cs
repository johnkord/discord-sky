using System.ClientModel;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Images;

namespace DiscordSky.Bot.Integrations.Images;

/// <summary>Per-request image parameters, resolved from <see cref="ImageOptions"/> at call time.</summary>
public sealed record ImageRequestOptions(string Model, string Size, string Quality, string OutputFormat, string Moderation)
{
    public static ImageRequestOptions FromConfig(ImageOptions o)
    {
        // The high-quality tier is gated: clamp to medium unless explicitly allowed.
        var quality = o.Quality;
        if (!o.AllowHighQuality && string.Equals(quality, "high", StringComparison.OrdinalIgnoreCase))
        {
            quality = "medium";
        }
        return new ImageRequestOptions(o.Model, o.Size, quality, o.OutputFormat, o.Moderation);
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
    private readonly ImageClient _client;
    private readonly ILogger<OpenAIImageGenerator> _logger;

    public OpenAIImageGenerator(ImageClient client, ILogger<OpenAIImageGenerator> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public async Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken)
    {
        var generationOptions = new ImageGenerationOptions
        {
            Quality = new GeneratedImageQuality(options.Quality.ToLowerInvariant()),
            Size = ParseSize(options.Size),
            OutputFileFormat = new GeneratedImageFileFormat(NormalizeFormat(options.OutputFormat)),
            ModerationLevel = new GeneratedImageModerationLevel(options.Moderation.ToLowerInvariant()),
            // ResponseFormat intentionally unset: gpt-image models reject response_format and return base64 by default.
        };

        try
        {
            ClientResult<GeneratedImage> result = await _client.GenerateImageAsync(prompt, generationOptions, cancellationToken);
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
            throw;
        }
        catch (ClientResultException ex)
        {
            var code = Classify(ex);
            // The API error message is safe to log (it describes the request problem, not user content) and
            // is the fastest way to diagnose org-verification (403) or bad-parameter failures.
            _logger.LogWarning("Image generation API error: status={Status} code={Code} message={Message}", ex.Status, code, ex.Message);
            return ImageResult.Fail(code);
        }
        catch (Exception ex)
        {
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
        var m = (model ?? string.Empty).ToLowerInvariant();
        var q = (quality ?? string.Empty).ToLowerInvariant();

        if (m.Contains("mini"))
        {
            return q switch { "low" => 0.005, "medium" => 0.011, "high" => 0.036, _ => 0.011 };
        }
        if (m.Contains("image-2") || m.Contains("image-1.5"))
        {
            return q switch { "low" => 0.006, "medium" => 0.05, "high" => 0.21, _ => 0.05 };
        }
        // gpt-image-1 and unknown models.
        return q switch { "low" => 0.011, "medium" => 0.042, "high" => 0.167, _ => 0.042 };
    }
}
