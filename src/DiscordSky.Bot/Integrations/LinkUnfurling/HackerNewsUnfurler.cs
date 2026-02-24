using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Extracts Hacker News story/comment content using the official Firebase API.
/// The HN API is free, requires no auth, and returns structured JSON.
/// API docs: https://github.com/HackerNews/API
/// </summary>
public sealed class HackerNewsUnfurler : ILinkUnfurler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HackerNewsUnfurler> _logger;

    /// <summary>
    /// Maximum characters of extracted text to include per item.
    /// </summary>
    internal const int MaxContentLength = 4000;

    /// <summary>
    /// Timeout for individual fetch operations.
    /// </summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Matches Hacker News item URLs.
    /// Examples:
    ///   https://news.ycombinator.com/item?id=12345678
    ///   https://news.ycombinator.com/item?id=12345678#comment
    /// </summary>
    internal static readonly Regex HnUrlRegex = new(
        @"https?://news\.ycombinator\.com/item\?id=(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public HackerNewsUnfurler(HttpClient httpClient, ILogger<HackerNewsUnfurler> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(Uri url)
    {
        return HnUrlRegex.IsMatch(url.AbsoluteUri);
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

        var matches = HnUrlRegex.Matches(messageContent);
        if (matches.Count == 0)
        {
            return Array.Empty<UnfurledLink>();
        }

        var seenIds = new HashSet<string>();
        var tasks = new List<(string id, string url, Task<UnfurledLink?> task)>();

        foreach (Match match in matches)
        {
            var itemId = match.Groups[1].Value;
            if (!seenIds.Add(itemId))
            {
                continue;
            }

            tasks.Add((itemId, match.Value, FetchItemAsync(itemId, match.Value, messageTimestamp, cancellationToken)));
        }

        var results = new List<UnfurledLink>();
        foreach (var (id, url, task) in tasks)
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
                _logger.LogDebug(ex, "Failed to unfurl HN item {Id} from {Url}", id, url);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches a Hacker News item using the Firebase API.
    /// </summary>
    internal async Task<UnfurledLink?> FetchItemAsync(
        string itemId,
        string originalUrl,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken)
    {
        var apiUrl = $"https://hacker-news.firebaseio.com/v0/item/{itemId}.json";

        _logger.LogDebug("Fetching HN item {Id} from Firebase API", itemId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FetchTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("User-Agent", "DiscordSkyBot/1.0 (link unfurling)");

            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HN Firebase API returned {StatusCode} for item {Id}", response.StatusCode, itemId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            return ParseHnItem(json, originalUrl, messageTimestamp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timeout fetching HN item {Id}", itemId);
            return null;
        }
    }

    /// <summary>
    /// Parses a Hacker News Firebase API JSON response into an UnfurledLink.
    /// HN items have a "type" field: "story", "comment", "job", "poll", "pollopt".
    /// </summary>
    internal static UnfurledLink? ParseHnItem(string json, string originalUrl, DateTimeOffset messageTimestamp)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Check if item is dead or deleted
            if (root.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
            {
                return null;
            }
            if (root.TryGetProperty("dead", out var dead) && dead.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            var type = GetStringProp(root, "type") ?? "story";
            var title = GetStringProp(root, "title");
            var text = GetStringProp(root, "text"); // HTML content for self-posts/comments
            var url = GetStringProp(root, "url");   // External link URL
            var author = GetStringProp(root, "by");
            var score = root.TryGetProperty("score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number
                ? scoreProp.GetInt32()
                : (int?)null;
            var descendants = root.TryGetProperty("descendants", out var descProp) && descProp.ValueKind == JsonValueKind.Number
                ? descProp.GetInt32()
                : (int?)null;

            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine(title);
            }

            // Metadata line
            var meta = new List<string>();
            if (score.HasValue) meta.Add($"{score.Value} points");
            if (!string.IsNullOrWhiteSpace(author)) meta.Add($"by {author}");
            if (descendants.HasValue) meta.Add($"{descendants.Value} comments");
            if (meta.Count > 0) sb.AppendLine(string.Join(" · ", meta));

            if (!string.IsNullOrWhiteSpace(url))
            {
                sb.AppendLine($"Link: {url}");
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine();
                // Strip basic HTML tags from HN text content
                sb.Append(StripHtmlTags(text));
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

            var sourceType = type == "comment" ? "hn-comment" : "hackernews";

            return new UnfurledLink
            {
                SourceType = sourceType,
                OriginalUrl = originalUri,
                Text = content,
                Author = !string.IsNullOrWhiteSpace(author) ? author : string.Empty,
                Images = Array.Empty<ChannelImage>()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Simple HTML tag stripping for HN text content.
    /// HN uses basic formatting: &lt;p&gt;, &lt;a&gt;, &lt;i&gt;, &lt;code&gt;, &lt;pre&gt;.
    /// </summary>
    internal static string StripHtmlTags(string html)
    {
        // Replace <p> and <br> with newlines
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</?p>", "\n", RegexOptions.IgnoreCase);

        // Strip remaining tags
        html = Regex.Replace(html, @"<[^>]+>", string.Empty);

        // Decode common HTML entities
        html = html
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#x27;", "'")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'");

        // Collapse multiple blank lines
        html = Regex.Replace(html, @"\n\s*\n", "\n\n");

        return html.Trim();
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
