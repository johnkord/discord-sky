using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordSky.Bot.Integrations.Images;

public sealed record ImageUsage(
    [property: JsonPropertyName("text_input_tokens")] long TextInputTokens,
    [property: JsonPropertyName("image_input_tokens")] long ImageInputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens,
    [property: JsonPropertyName("cached_text_input_tokens")] long CachedTextInputTokens = 0,
    [property: JsonPropertyName("cached_image_input_tokens")] long CachedImageInputTokens = 0,
    [property: JsonPropertyName("unclassified_input_tokens")] long UnclassifiedInputTokens = 0);

internal static class ImageUsageCost
{
    internal static ImageUsage? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("output_tokens", out _)) return null;
        var input = Count(usage, "input_tokens");
        var text = 0L;
        var images = 0L;
        var cachedText = 0L;
        var cachedImages = 0L;
        if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            text = Count(details, "text_tokens");
            images = Count(details, "image_tokens");
            if (details.TryGetProperty("cached_tokens_details", out var cached) && cached.ValueKind == JsonValueKind.Object)
            {
                cachedText = Math.Min(text, Count(cached, "text_tokens"));
                cachedImages = Math.Min(images, Count(cached, "image_tokens"));
            }
        }
        return new ImageUsage(text, images, Count(usage, "output_tokens"), cachedText, cachedImages,
            Math.Max(0, input - text - images));
    }

    internal static double? FromUsage(string model, ImageUsage? usage)
    {
        if (usage is null || !model.StartsWith("gpt-image-2.5", StringComparison.OrdinalIgnoreCase)) return null;
        return ((usage.TextInputTokens - usage.CachedTextInputTokens) * 5.0 +
            usage.CachedTextInputTokens * 1.25 +
            (usage.ImageInputTokens - usage.CachedImageInputTokens) * 8.0 +
            usage.CachedImageInputTokens * 2.0 + usage.UnclassifiedInputTokens * 8.0 +
            usage.OutputTokens * 30.0) / 1_000_000;
    }

    internal static double Reservation(ImageRenderRequest request)
    {
        var options = request.Options;
        var size = ImageRenderPolicy.NormalizeSize(options.Size);
        var pixels = size == "auto" ? 8294400L : size.Split('x').Select(long.Parse).Aggregate((width, height) => width * height);
        var qualityEstimate = options.Quality switch
        {
            "low" => 0.10,
            "medium" => 0.21,
            "high" => 0.42,
            "xhigh" => 0.65,
            _ => 0.90,
        };
        return qualityEstimate * Math.Max(1, pixels / 1048576.0) +
            (request.References?.Count ?? 0) * 0.05 +
            (request.Mask is null ? 0 : 0.05) + options.PartialImages * 0.003 +
            request.Prompt.Length * 0.0000025;
    }

    private static long Count(JsonElement value, string name) =>
        value.TryGetProperty(name, out var count) && count.TryGetInt64(out var number) ? Math.Max(0, number) : 0;
}

internal sealed class ImageGenerationCanceledException(ImageResult result, CancellationToken cancellationToken)
    : OperationCanceledException("Image request cancelled after dispatch; reserved spend retained.", cancellationToken)
{
    internal ImageResult Result { get; } = result;
}