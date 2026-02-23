using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Extracts tweet content (text + images) from X/Twitter links using the fxtwitter API.
/// </summary>
public sealed class TweetUnfurler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TweetUnfurler> _logger;

    // Matches https://x.com/user/status/123 or https://twitter.com/user/status/123
    // Also handles mobile.twitter.com, vxtwitter.com, fxtwitter.com variants
    internal static readonly Regex TweetUrlRegex = new(
        @"https?://(?:(?:www\.|mobile\.)?(?:twitter|x)\.com|(?:vx|fx)twitter\.com)/[^/]+/status/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TweetUnfurler(HttpClient httpClient, ILogger<TweetUnfurler> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Scans message content for tweet URLs and returns unfurled content for each.
    /// </summary>
    public async Task<IReadOnlyList<UnfurledLink>> UnfurlTweetsAsync(
        string messageContent,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageContent))
        {
            return Array.Empty<UnfurledLink>();
        }

        var matches = TweetUrlRegex.Matches(messageContent);
        if (matches.Count == 0)
        {
            return Array.Empty<UnfurledLink>();
        }

        // Deduplicate by tweet ID
        var seenIds = new HashSet<string>();
        var tasks = new List<(string tweetId, string originalUrl, Task<UnfurledLink?> task)>();

        foreach (Match match in matches)
        {
            var tweetId = match.Groups[1].Value;
            if (!seenIds.Add(tweetId))
            {
                continue;
            }

            tasks.Add((tweetId, match.Value, FetchTweetAsync(tweetId, match.Value, messageTimestamp, cancellationToken)));
        }

        var results = new List<UnfurledLink>();
        foreach (var (tweetId, originalUrl, task) in tasks)
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
                _logger.LogDebug(ex, "Failed to unfurl tweet {TweetId} from {Url}", tweetId, originalUrl);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches tweet data from the fxtwitter API and converts it to an UnfurledLink.
    /// </summary>
    internal async Task<UnfurledLink?> FetchTweetAsync(
        string tweetId,
        string originalUrl,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken)
    {
        var apiUrl = $"https://api.fxtwitter.com/status/{tweetId}";

        _logger.LogDebug("Fetching tweet {TweetId} from fxtwitter API", tweetId);

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("User-Agent", "DiscordSkyBot/1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("fxtwitter API returned {StatusCode} for tweet {TweetId}", response.StatusCode, tweetId);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseFxTwitterResponse(json, originalUrl, messageTimestamp);
    }

    /// <summary>
    /// Parses the fxtwitter API JSON response into an UnfurledLink.
    /// </summary>
    internal static UnfurledLink? ParseFxTwitterResponse(string json, string originalUrl, DateTimeOffset messageTimestamp)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tweet", out var tweet))
            {
                return null;
            }

            var text = tweet.TryGetProperty("text", out var textProp)
                ? textProp.GetString() ?? string.Empty
                : string.Empty;

            var authorName = string.Empty;
            var authorHandle = string.Empty;
            if (tweet.TryGetProperty("author", out var author))
            {
                authorName = author.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? string.Empty
                    : string.Empty;
                authorHandle = author.TryGetProperty("screen_name", out var handleProp)
                    ? handleProp.GetString() ?? string.Empty
                    : string.Empty;
            }

            var authorDisplay = !string.IsNullOrWhiteSpace(authorName) && !string.IsNullOrWhiteSpace(authorHandle)
                ? $"{authorName} (@{authorHandle})"
                : !string.IsNullOrWhiteSpace(authorHandle)
                    ? $"@{authorHandle}"
                    : authorName;

            var images = new List<ChannelImage>();
            if (tweet.TryGetProperty("media", out var media)
                && media.TryGetProperty("photos", out var photos))
            {
                foreach (var photo in photos.EnumerateArray())
                {
                    var url = photo.TryGetProperty("url", out var urlProp)
                        ? urlProp.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        continue;
                    }

                    images.Add(new ChannelImage
                    {
                        Url = uri,
                        Filename = Path.GetFileName(uri.LocalPath),
                        Source = "tweet",
                        Timestamp = messageTimestamp
                    });
                }
            }

            // Skip if the tweet has no meaningful content
            if (string.IsNullOrWhiteSpace(text) && images.Count == 0)
            {
                return null;
            }

            if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var originalUri))
            {
                return null;
            }

            return new UnfurledLink
            {
                SourceType = "tweet",
                OriginalUrl = originalUri,
                Text = text,
                Author = authorDisplay,
                Images = images
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts tweet IDs from a message. Useful for testing URL detection.
    /// </summary>
    internal static IReadOnlyList<string> ExtractTweetIds(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var matches = TweetUrlRegex.Matches(content);
        var ids = new HashSet<string>();
        foreach (Match match in matches)
        {
            ids.Add(match.Groups[1].Value);
        }

        return ids.ToList();
    }
}
