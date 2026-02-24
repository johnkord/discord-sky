using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Extracts Reddit post/comment content using Reddit's public JSON API.
/// Appending <c>.json</c> to any Reddit URL returns structured data without
/// requiring OAuth or API keys, and bypasses IP/UA blocking that affects
/// HTML page requests from datacenter IPs.
/// </summary>
public sealed class RedditUnfurler : ILinkUnfurler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RedditUnfurler> _logger;

    /// <summary>
    /// Maximum characters of extracted text to include per post.
    /// </summary>
    internal const int MaxContentLength = 4000;

    /// <summary>
    /// Timeout for individual fetch operations.
    /// </summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Matches Reddit URLs for posts, comments, and subreddits.
    /// Captures: subreddit, post ID, (optional) comment ID.
    /// Examples:
    ///   https://www.reddit.com/r/programming/comments/abc123/post_title/
    ///   https://reddit.com/r/AskReddit/comments/abc123/post_title/def456/
    ///   https://old.reddit.com/r/technology/
    ///   https://redd.it/abc123
    /// </summary>
    internal static readonly Regex RedditUrlRegex = new(
        @"https?://(?:(?:www\.|old\.|new\.)?reddit\.com|redd\.it)/[^\s<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts the post ID from a redd.it short URL.
    /// </summary>
    internal static readonly Regex ShortUrlRegex = new(
        @"https?://redd\.it/(\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches a Reddit post URL pattern to extract components.
    /// </summary>
    internal static readonly Regex PostUrlRegex = new(
        @"https?://(?:(?:www\.|old\.|new\.)?reddit\.com)/r/(\w+)/comments/(\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public RedditUnfurler(HttpClient httpClient, ILogger<RedditUnfurler> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(Uri url)
    {
        return RedditUrlRegex.IsMatch(url.AbsoluteUri);
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

        var matches = RedditUrlRegex.Matches(messageContent);
        if (matches.Count == 0)
        {
            return Array.Empty<UnfurledLink>();
        }

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<(string url, Task<UnfurledLink?> task)>();

        foreach (Match match in matches)
        {
            var urlStr = WebContentUnfurler.CleanUrlString(match.Value);
            if (!seenUrls.Add(urlStr))
            {
                continue;
            }

            if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
            {
                continue;
            }

            tasks.Add((urlStr, FetchRedditJsonAsync(uri, messageTimestamp, cancellationToken)));
        }

        var results = new List<UnfurledLink>();
        foreach (var (url, task) in tasks)
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
                _logger.LogDebug(ex, "Failed to unfurl Reddit content from {Url}", url);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches Reddit JSON data by appending .json to the URL.
    /// </summary>
    internal async Task<UnfurledLink?> FetchRedditJsonAsync(
        Uri url,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken)
    {
        var jsonUrl = BuildJsonUrl(url);
        if (jsonUrl == null)
        {
            _logger.LogDebug("Could not build JSON URL for {Url}", url);
            return null;
        }

        _logger.LogDebug("Fetching Reddit JSON from {Url}", jsonUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FetchTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, jsonUrl);
            // Reddit's JSON API works with any UA, but we use a descriptive one
            request.Headers.Add("User-Agent", "DiscordSkyBot/1.0 (link unfurling)");

            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Reddit JSON API returned {StatusCode} for {Url}", response.StatusCode, url);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            return ParseRedditJson(json, url, messageTimestamp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timeout fetching Reddit JSON from {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Builds the .json URL for a given Reddit URL.
    /// Handles both full URLs and redd.it short links.
    /// </summary>
    internal static string? BuildJsonUrl(Uri url)
    {
        var urlStr = url.AbsoluteUri;

        // Handle redd.it short URLs by expanding them
        var shortMatch = ShortUrlRegex.Match(urlStr);
        if (shortMatch.Success)
        {
            var postId = shortMatch.Groups[1].Value;
            return $"https://www.reddit.com/comments/{postId}.json?limit=5&raw_json=1";
        }

        // Handle standard Reddit URLs
        var postMatch = PostUrlRegex.Match(urlStr);
        if (postMatch.Success)
        {
            // Get the path up to and including the post ID (may include comment path)
            var path = url.AbsolutePath.TrimEnd('/');
            return $"https://www.reddit.com{path}.json?limit=5&raw_json=1";
        }

        // Subreddit-level URLs (e.g. /r/programming/) - not a specific post
        // Skip these as they don't represent specific content worth unfurling
        return null;
    }

    /// <summary>
    /// Parses Reddit JSON response into an UnfurledLink.
    /// Reddit returns an array where [0] is the post listing and [1] is the comments listing.
    /// </summary>
    internal static UnfurledLink? ParseRedditJson(string json, Uri originalUrl, DateTimeOffset messageTimestamp)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Reddit post JSON is an array: [postListing, commentsListing]
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            var postListing = root[0];
            if (!TryGetFirstChild(postListing, out var postData))
            {
                return null;
            }

            // Extract post fields
            var title = GetStringProp(postData, "title");
            var selftext = GetStringProp(postData, "selftext");
            var author = GetStringProp(postData, "author");
            var subreddit = GetStringProp(postData, "subreddit_name_prefixed") ?? GetStringProp(postData, "subreddit");
            var score = postData.TryGetProperty("score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number
                ? scoreProp.GetInt32()
                : (int?)null;
            var numComments = postData.TryGetProperty("num_comments", out var commentsProp) && commentsProp.ValueKind == JsonValueKind.Number
                ? commentsProp.GetInt32()
                : (int?)null;
            var linkUrl = GetStringProp(postData, "url");
            var isSelf = postData.TryGetProperty("is_self", out var isSelfProp) && isSelfProp.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            // Build content text
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(title);

            if (!string.IsNullOrWhiteSpace(subreddit))
            {
                var meta = new List<string> { subreddit };
                if (score.HasValue) meta.Add($"{score.Value} points");
                if (numComments.HasValue) meta.Add($"{numComments.Value} comments");
                sb.AppendLine(string.Join(" · ", meta));
            }

            if (!string.IsNullOrWhiteSpace(selftext))
            {
                sb.AppendLine();
                sb.Append(selftext);
            }
            else if (!isSelf && !string.IsNullOrWhiteSpace(linkUrl))
            {
                sb.AppendLine();
                sb.Append($"Link: {linkUrl}");
            }

            // Extract top comments if available
            if (root.GetArrayLength() > 1)
            {
                var commentsListing = root[1];
                var topComments = ExtractTopComments(commentsListing, maxComments: 3);
                if (topComments.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.AppendLine("Top comments:");
                    foreach (var comment in topComments)
                    {
                        sb.AppendLine($"— u/{comment.Author}: {comment.Body}");
                    }
                }
            }

            var text = sb.ToString().Trim();
            if (text.Length > MaxContentLength)
            {
                text = text[..MaxContentLength] + "…";
            }

            // Extract images (thumbnail or preview)
            var images = new List<ChannelImage>();
            var thumbnail = GetStringProp(postData, "thumbnail");
            if (!string.IsNullOrWhiteSpace(thumbnail)
                && thumbnail != "self" && thumbnail != "default" && thumbnail != "nsfw" && thumbnail != "spoiler"
                && Uri.TryCreate(thumbnail, UriKind.Absolute, out var thumbUri))
            {
                images.Add(new ChannelImage
                {
                    Url = thumbUri,
                    Filename = Path.GetFileName(thumbUri.LocalPath),
                    Source = "reddit-thumbnail",
                    Timestamp = messageTimestamp
                });
            }

            var authorDisplay = !string.IsNullOrWhiteSpace(author) ? $"u/{author}" : string.Empty;
            if (!string.IsNullOrWhiteSpace(subreddit))
            {
                authorDisplay = !string.IsNullOrWhiteSpace(authorDisplay)
                    ? $"{authorDisplay} in {subreddit}"
                    : subreddit;
            }

            return new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = originalUrl,
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
    /// Extracts the top N comments from a Reddit comments listing.
    /// </summary>
    internal static IReadOnlyList<(string Author, string Body)> ExtractTopComments(
        JsonElement commentsListing, int maxComments = 3)
    {
        var comments = new List<(string Author, string Body)>();

        if (!TryGetChildren(commentsListing, out var children))
        {
            return comments;
        }

        foreach (var child in children)
        {
            if (comments.Count >= maxComments) break;

            if (!child.TryGetProperty("kind", out var kindProp) || kindProp.GetString() != "t1")
            {
                continue;
            }

            if (!child.TryGetProperty("data", out var data))
            {
                continue;
            }

            var author = GetStringProp(data, "author") ?? "[deleted]";
            var body = GetStringProp(data, "body") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(body)) continue;

            // Truncate long comments
            if (body.Length > 300)
            {
                body = body[..300] + "…";
            }

            comments.Add((author, body));
        }

        return comments;
    }

    private static bool TryGetFirstChild(JsonElement listing, out JsonElement childData)
    {
        childData = default;

        if (!listing.TryGetProperty("data", out var data)
            || !data.TryGetProperty("children", out var children)
            || children.GetArrayLength() == 0)
        {
            return false;
        }

        var firstChild = children[0];
        if (!firstChild.TryGetProperty("data", out childData))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetChildren(JsonElement listing, out JsonElement.ArrayEnumerator children)
    {
        children = default;

        if (!listing.TryGetProperty("data", out var data)
            || !data.TryGetProperty("children", out var childrenArr)
            || childrenArr.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        children = childrenArr.EnumerateArray();
        return true;
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
