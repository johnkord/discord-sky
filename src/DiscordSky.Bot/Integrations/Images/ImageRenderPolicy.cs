using System.Globalization;
using System.Text.Json.Serialization;
using DiscordSky.Bot.Configuration;

namespace DiscordSky.Bot.Integrations.Images;

public sealed record ImageRenderSettings(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("quality")] string? Quality = null,
    [property: JsonPropertyName("size")] string? Size = null,
    [property: JsonPropertyName("output_format")] string? OutputFormat = null,
    [property: JsonPropertyName("background")] string? Background = null,
    [property: JsonPropertyName("output_compression")] int? OutputCompression = null,
    [property: JsonPropertyName("partial_images")] int? PartialImages = null,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("moderation")] string? Moderation = null);

public sealed record ImageReference(byte[] Bytes, string FileName, string MediaType, ulong? MessageId = null);

public sealed record ImagePreview(byte[] Bytes, string FileExtension, int Index);

public sealed record ImageRenderRequest(
    string Prompt,
    ImageRequestOptions Options,
    IReadOnlyList<ImageReference>? References = null,
    ImageReference? Mask = null,
    Func<ImagePreview, CancellationToken, Task>? OnPreview = null);

internal static class ImageRenderPolicy
{
    internal const string Flare = "gpt-image-2.5-flare";
    internal const string Sunburst = "gpt-image-2.5-sunburst";
    internal const int MaxOutputBytes = 8 * 1024 * 1024;
    internal const int MaxResponseBytes = MaxOutputBytes * 4 / 3 + 65536;

    internal static ImageRequestOptions Resolve(ImageOptions options, ImageRenderSettings? requested, bool editing)
    {
        var model = (requested?.Model ?? (editing ? options.EditModel : options.Model)).Trim().ToLowerInvariant();
        model = model switch { "flare" => Flare, "sunburst" => Sunburst, _ => model };
        if (requested?.Model is not null && !ImageModelPolicy.IsApproved(model))
            throw new ArgumentException("Use Flare, Sunburst, or a configured GPT Image 2 or newer non-mini model.");
        ImageModelPolicy.EnsureApproved(model);
        var quality = Normalize(requested?.Quality ?? options.Quality);
        if (quality is not ("low" or "medium" or "high" or "xhigh" or "max" or "auto"))
            throw new ArgumentException("Image quality must be low, medium, high, xhigh, max, or auto.");
        if (!options.AllowHighQuality && quality is "high" or "xhigh" or "max" or "auto")
            quality = "medium";
        if (quality is "xhigh" or "max" && !SupportsExtendedQuality(model))
            throw new ArgumentException("xhigh and max image quality require GPT Image 2.5 or newer.");

        var size = NormalizeSize(requested?.Size ?? options.Size);
        var background = Normalize(requested?.Background ?? options.Background);
        if (background is not ("opaque" or "transparent" or "auto"))
            throw new ArgumentException("Image background must be opaque, transparent, or auto.");
        var format = Normalize(requested?.OutputFormat ?? options.OutputFormat);
        if (format == "jpg") format = "jpeg";
        if (background == "transparent" && requested?.OutputFormat is null && format == "jpeg") format = "png";
        if (format is not ("png" or "jpeg" or "webp"))
            throw new ArgumentException("Image format must be png, jpeg, or webp.");
        if (background == "transparent" && format == "jpeg")
            throw new ArgumentException("Transparent images require png or webp, not jpeg.");
        var compression = requested?.OutputCompression ?? (format == "png" ? null : options.OutputCompression);
        if (compression is < 0 or > 100 || (compression.HasValue && format == "png"))
            throw new ArgumentException("Compression must be 0 to 100 and is available only for jpeg or webp.");
        var partialImages = requested?.PartialImages ?? options.PartialImages;
        if (partialImages is < 0 or > 3)
            throw new ArgumentException("Image previews must be between 0 and 3.");
        var moderation = Normalize(requested?.Moderation ?? options.Moderation);
        if (moderation is not ("auto" or "low"))
            throw new ArgumentException("Image moderation must be auto or low.");

        return new ImageRequestOptions(model, size, quality, format, moderation, background, compression, partialImages);
    }

    internal static string ResolveAction(string? action, bool hasReferences)
    {
        return Normalize(action ?? "auto") switch
        {
            "auto" => hasReferences ? "edit" : "generate",
            "generate" => "generate",
            "edit" when hasReferences => "edit",
            "edit" => throw new ArgumentException("Editing requires an attached image or a reply to an image."),
            _ => throw new ArgumentException("Image action must be auto, generate, or edit."),
        };
    }

    internal static string NormalizeSize(string value)
    {
        var size = Normalize(value);
        if (size == "auto") return size;
        var dimensions = size.Split('x', StringSplitOptions.TrimEntries);
        if (dimensions.Length != 2 ||
            !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            width <= 0 || height <= 0 || width > 3840 || height > 3840 ||
            width % 16 != 0 || height % 16 != 0 ||
            Math.Max(width, height) > 3 * Math.Min(width, height) ||
            (long)width * height is < 655360 or > 8294400)
        {
            throw new ArgumentException(
                "Image size must be auto or WIDTHxHEIGHT: multiples of 16, at most 3840 per edge, " +
                "655360 to 8294400 pixels, and no wider or taller than 3:1.");
        }
        return $"{width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool SupportsExtendedQuality(string model)
    {
        var version = model["gpt-image-".Length..].Split('-')[0];
        return int.TryParse(version, out var major) ? major >= 3 :
            Version.TryParse(version, out var release) && release >= new Version(2, 5);
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}