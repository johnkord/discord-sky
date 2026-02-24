using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Extracts Wikipedia article summaries using the Wikimedia REST API.
/// API docs: https://en.wikipedia.org/api/rest_v1/
/// Supports all language editions (en, de, fr, etc.).
/// </summary>
public sealed class WikipediaUnfurler : ILinkUnfurler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WikipediaUnfurler> _logger;

    /// <summary>
    /// Maximum characters of extracted summary to include.
    /// </summary>
    internal const int MaxContentLength = 4000;

    /// <summary>
    /// Timeout for individual fetch operations.
    /// </summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Matches Wikipedia article URLs across all language editions.
    /// Examples:
    ///   https://en.wikipedia.org/wiki/Artificial_intelligence
    ///   https://de.wikipedia.org/wiki/Künstliche_Intelligenz
    ///   https://en.m.wikipedia.org/wiki/Artificial_intelligence
    /// Captures: (1) language code, (2) article title
    /// </summary>
    internal static readonly Regex WikiUrlRegex = new(
        @"https?://([a-z]{2,3})(?:\.m)?\.wikipedia\.org/wiki/([^\s#?]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public WikipediaUnfurler(HttpClient httpClient, ILogger<WikipediaUnfurler> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(Uri url)
    {
        return WikiUrlRegex.IsMatch(url.AbsoluteUri);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnfurledLink>> UnfurlAsync(
        string messageContent,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageContent))
        {
            return Array.Empty<UnfurledLink>();
        }

        var matches = WikiUrlRegex.Matches(messageContent);
        if (matches.Count == 0)
        {
            return Array.Empty<UnfurledLink>();
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<(string key, string url, Task<UnfurledLink?> task)>();

        foreach (Match match in matches)
        {
            var lang = match.Groups[1].Value.ToLowerInvariant();
            var title = match.Groups[2].Value;
            var dedupeKey = $"{lang}:{title}";

            if (!seenKeys.Add(dedupeKey))
            {
                continue;
            }

            tasks.Add((dedupeKey, match.Value, FetchSummaryAsync(lang, title, match.Value, cancellationToken)));
        }

        var results = new List<UnfurledLink>();
        foreach (var (key, url, task) in tasks)
        {
            try
            {
                var result = await task;
                if (result != null)
                {
                    results.Add(result);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to unfurl Wikipedia article {Key} from {Url}", key, url);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches a Wikipedia article summary using the Wikimedia REST API.
    /// </summary>
    internal async Task<UnfurledLink?> FetchSummaryAsync(
        string lang,
        string title,
        string originalUrl,
        CancellationToken cancellationToken)
    {
        var apiUrl = BuildApiUrl(lang, title);

        _logger.LogDebug("Fetching Wikipedia summary for {Lang}:{Title}", lang, title);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FetchTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            // Wikimedia API requests must have a proper User-Agent
            request.Headers.Add("User-Agent", "DiscordSkyBot/1.0 (link unfurling; https://github.com/jkordish/discord-sky)");
            request.Headers.Add("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Wikipedia REST API returned {StatusCode} for {Lang}:{Title}",
                    response.StatusCode, lang, title);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            return ParseSummaryResponse(json, originalUrl, lang);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timeout fetching Wikipedia summary for {Lang}:{Title}", lang, title);
            return null;
        }
    }

    /// <summary>
    /// Builds the Wikimedia REST API URL for the page summary endpoint.
    /// </summary>
    internal static string BuildApiUrl(string lang, string title)
    {
        // The title comes URL-encoded from the URL; the API expects the same format
        return $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{title}";
    }

    /// <summary>
    /// Parses the Wikimedia REST API summary response.
    /// Response fields: title, extract (plain text summary), description,
    /// thumbnail (source, width, height), originalimage (source, width, height).
    /// </summary>
    internal static UnfurledLink? ParseSummaryResponse(string json, string originalUrl, string lang)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Check for disambiguation or missing pages
            var type = GetStringProp(root, "type");
            if (type == "disambiguation" || type == "no-extract")
            {
                // Still proceed — disambiguation pages have some content
            }

            var title = GetStringProp(root, "title");
            var extract = GetStringProp(root, "extract");
            var description = GetStringProp(root, "description");

            if (string.IsNullOrWhiteSpace(extract) && string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine(title);
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                sb.AppendLine(description);
            }

            if (!string.IsNullOrWhiteSpace(extract))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(extract);
            }

            var content = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            if (content.Length > MaxContentLength)
            {
                content = content[..MaxContentLength] + "…";
            }

            if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var originalUri))
            {
                return null;
            }

            // Extract thumbnail if available
            var images = new List<ChannelImage>();
            if (root.TryGetProperty("thumbnail", out var thumbnail) && thumbnail.ValueKind == JsonValueKind.Object)
            {
                var thumbUrl = GetStringProp(thumbnail, "source");
                if (!string.IsNullOrWhiteSpace(thumbUrl) && Uri.TryCreate(thumbUrl, UriKind.Absolute, out var thumbUri))
                {
                    images.Add(new ChannelImage
                    {
                        Url = thumbUri,
                        Filename = System.IO.Path.GetFileName(thumbUri.LocalPath),
                        Source = "wikipedia-thumbnail"
                    });
                }
            }

            return new UnfurledLink
            {
                SourceType = lang == "en" ? "wikipedia" : $"wikipedia-{lang}",
                OriginalUrl = originalUri,
                Text = content,
                Author = string.Empty, // Wikipedia doesn't attribute individual authors
                Images = images
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetStringProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }
}
