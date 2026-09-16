using System.Globalization;
using System.Text.Json;

namespace DiscordSky.Bot.Integrations.Images;

internal sealed record ParsedImageCommand(string Prompt, ImageRenderSettings Settings);

internal static class ImageCommandParser
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal static ParsedImageCommand Parse(string request)
    {
        var tokens = request.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var prompt = new List<string>();
        var settings = new Dictionary<string, object>();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal)) { prompt.Add(token); continue; }
            if (token == "--") { prompt.AddRange(tokens[(index + 1)..]); break; }
            var parts = token[2..].Split('=', 2);
            var name = parts[0].ToLowerInvariant();
            if (name == "transparent" && parts.Length == 1) { settings["background"] = "transparent"; continue; }
            if (name is "edit" or "generate" && parts.Length == 1) { settings["action"] = name; continue; }
            var key = name switch
            {
                "model" or "quality" or "size" or "background" or "action" or "moderation" => name,
                "format" => "output_format",
                "compression" => "output_compression",
                "previews" => "partial_images",
                _ => throw new ArgumentException("Unknown image option. Use --model, --quality, --size, --format, --background, --compression, --previews, --action, or --moderation."),
            };
            var value = parts.Length == 2 ? parts[1] : ++index < tokens.Length ? tokens[index] : string.Empty;
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"The --{name} option needs a value.");
            if (key is "partial_images" or "output_compression")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    throw new ArgumentException($"The --{name} option needs a whole number.");
                settings[key] = number;
            }
            else settings[key] = value;
        }
        var resolved = JsonSerializer.Deserialize<ImageRenderSettings>(JsonSerializer.Serialize(settings), JsonOptions)!;
        if (resolved.Action is null && ImageIntentDetector.LooksLikeEditRequest(string.Join(' ', prompt)) &&
            !ImageIntentDetector.LooksLikeImageRequest(string.Join(' ', prompt)))
            resolved = resolved with { Action = "edit" };
        return new ParsedImageCommand(string.Join(' ', prompt), resolved);
    }

    internal static ImageRenderSettings Merge(ImageRenderSettings primary, ImageRenderSettings? fallback) => new(
        primary.Model ?? fallback?.Model, primary.Quality ?? fallback?.Quality, primary.Size ?? fallback?.Size,
        primary.OutputFormat ?? fallback?.OutputFormat, primary.Background ?? fallback?.Background,
        primary.OutputCompression ?? fallback?.OutputCompression, primary.PartialImages ?? fallback?.PartialImages,
        primary.Action ?? fallback?.Action, primary.Moderation ?? fallback?.Moderation);
}