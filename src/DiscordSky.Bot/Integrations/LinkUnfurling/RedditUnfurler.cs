using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Extracts Reddit post and comment content using the Arctic Shift API.
/// Reddit blocks all Azure datacenter IPs, so we use Arctic Shift
/// (<c>arctic-shift.photon-reddit.com</c>) which mirrors Reddit data
/// without authentication or API keys.
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
    /// Base URL for the Arctic Shift API.
    /// </summary>
    internal const string ArcticShiftBaseUrl = "https://arctic-shift.photon-reddit.com";

    /// <summary>
    /// Timeout for individual fetch operations.
    /// </summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Matches Reddit URLs for posts, comments, and subreddits.
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
    /// Matches a Reddit post URL pattern to extract subreddit and post ID.
    /// </summary>
    internal static readonly Regex PostUrlRegex = new(
        @"https?://(?:(?:www\.|old\.|new\.)?reddit\.com)/r/(\w+)/comments/(\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Known image hosting domains. URLs on these domains are treated as
    /// direct image links (the URL itself is the full-resolution image).
    /// </summary>
    private static readonly HashSet<string> ImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "i.redd.it",
        "i.imgur.com",
        "preview.redd.it",
        "pbs.twimg.com"
    };

    /// <summary>
    /// Common image file extensions.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };

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
            return Array.Empty<UnfurledLink>();

        var matches = RedditUrlRegex.Matches(messageContent);
        if (matches.Count == 0)
            return Array.Empty<UnfurledLink>();

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<(string url, Task<UnfurledLink?> task)>();

        foreach (Match match in matches)
        {
            var urlStr = WebContentUnfurler.CleanUrlString(match.Value);
            if (!seenUrls.Add(urlStr)) continue;
            if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri)) continue;

            tasks.Add((urlStr, FetchViaArcticShiftAsync(uri, messageTimestamp, cancellationToken)));
        }

        var results = new List<UnfurledLink>();
        foreach (var (url, task) in tasks)
        {
            try
            {
                var result = await task;
                if (result != null)
                    results.Add(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to unfurl Reddit content from {Url}", url);
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts a Reddit post ID from a URL.
    /// Handles redd.it short URLs and standard reddit.com post URLs.
    /// Returns null for subreddit-level URLs without a specific post.
    /// </summary>
    internal static string? ExtractPostId(Uri url)
    {
        var urlStr = url.AbsoluteUri;

        var shortMatch = ShortUrlRegex.Match(urlStr);
        if (shortMatch.Success)
            return shortMatch.Groups[1].Value;

        var postMatch = PostUrlRegex.Match(urlStr);
        if (postMatch.Success)
            return postMatch.Groups[2].Value;

        return null;
    }

    /// <summary>
    /// Fetches Reddit post and comment data via the Arctic Shift API.
    /// </summary>
    internal async Task<UnfurledLink?> FetchViaArcticShiftAsync(
        Uri url, DateTimeOffset messageTimestamp, CancellationToken cancellationToken)
    {
        var postId = ExtractPostId(url);
        if (postId == null)
        {
            _logger.LogDebug("Could not extract post ID from {Url}", url);
            return null;
        }

        _logger.LogDebug("Fetching Reddit content via Arctic Shift for post {PostId}", postId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FetchTimeout);

        try
        {
            // Fetch post data
            var postJson = await FetchJsonStringAsync(
                $"{ArcticShiftBaseUrl}/api/posts/ids?ids={postId}", cts.Token);
            if (postJson == null)
                return null;

            // Fetch comments (optional — failure is non-fatal)
            string? commentsJson = null;
            try
            {
                commentsJson = await FetchJsonStringAsync(
                    $"{ArcticShiftBaseUrl}/api/comments/tree?link_id=t3_{postId}&limit=5", cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Timeout fetching comments for post {PostId}", postId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to fetch comments for post {PostId}", postId);
            }

            return ParseArcticShiftResponse(postJson, commentsJson, url, messageTimestamp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timeout fetching via Arctic Shift for post {PostId}", postId);
            return null;
        }
    }

    /// <summary>
    /// Parses Arctic Shift API responses into an UnfurledLink.
    /// Post format: <c>{"data": [{ ...post fields... }]}</c>
    /// Comments format: <c>{"data": [{ ...comment fields... }]}</c>
    /// </summary>
    internal static UnfurledLink? ParseArcticShiftResponse(
        string postJson, string? commentsJson, Uri originalUrl, DateTimeOffset messageTimestamp)
    {
        try
        {
            using var postDoc = JsonDocument.Parse(postJson);
            var postRoot = postDoc.RootElement;

            if (!postRoot.TryGetProperty("data", out var dataArray)
                || dataArray.ValueKind != JsonValueKind.Array
                || dataArray.GetArrayLength() == 0)
            {
                return null;
            }

            var post = dataArray[0];

            // Extract post fields (same field names as Reddit API, flat structure)
            var title = GetStringProp(post, "title");
            var selftext = GetStringProp(post, "selftext");
            var author = GetStringProp(post, "author");
            var subreddit = GetStringProp(post, "subreddit_name_prefixed")
                         ?? (GetStringProp(post, "subreddit") is string sub ? $"r/{sub}" : null);
            var score = post.TryGetProperty("score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number
                ? scoreProp.GetInt32()
                : (int?)null;
            var numComments = post.TryGetProperty("num_comments", out var commentsProp) && commentsProp.ValueKind == JsonValueKind.Number
                ? commentsProp.GetInt32()
                : (int?)null;
            var linkUrl = GetStringProp(post, "url");
            var isSelf = post.TryGetProperty("is_self", out var isSelfProp) && isSelfProp.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(title))
                return null;

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
            if (!string.IsNullOrWhiteSpace(commentsJson))
            {
                try
                {
                    using var commentDoc = JsonDocument.Parse(commentsJson);
                    if (commentDoc.RootElement.TryGetProperty("data", out var commentsArray)
                        && commentsArray.ValueKind == JsonValueKind.Array)
                    {
                        var topComments = ExtractTopComments(commentsArray, maxComments: 3);
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
                }
                catch (JsonException)
                {
                    // Skip comments on parse failure
                }
            }

            var text = sb.ToString().Trim();
            if (text.Length > MaxContentLength)
                text = text[..MaxContentLength] + "…";

            // Extract images (thumbnail or preview)
            var images = new List<ChannelImage>();

            // For link posts, check if the URL is a direct image
            if (!isSelf && !string.IsNullOrWhiteSpace(linkUrl)
                && Uri.TryCreate(linkUrl, UriKind.Absolute, out var linkUri)
                && IsImageUrl(linkUri))
            {
                images.Add(new ChannelImage
                {
                    Url = linkUri,
                    Filename = Path.GetFileName(linkUri.LocalPath),
                    Source = "reddit-image",
                    Timestamp = messageTimestamp
                });
            }

            // Fall back to thumbnail if no full-res image was found
            if (images.Count == 0)
            {
                var thumbnail = GetStringProp(post, "thumbnail");
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
    /// Extracts the top N comments from an Arctic Shift comments data array.
    /// Each element is a flat comment object with author, body, score fields.
    /// </summary>
    internal static IReadOnlyList<(string Author, string Body)> ExtractTopComments(
        JsonElement commentsArray, int maxComments = 3)
    {
        var comments = new List<(string Author, string Body)>();

        foreach (var comment in commentsArray.EnumerateArray())
        {
            if (comments.Count >= maxComments) break;

            var author = GetStringProp(comment, "author") ?? "[deleted]";
            var body = GetStringProp(comment, "body") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(body)) continue;

            // Truncate long comments
            if (body.Length > 300)
                body = body[..300] + "…";

            comments.Add((author, body));
        }

        return comments;
    }

    private async Task<string?> FetchJsonStringAsync(string url, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Arctic Shift returned {StatusCode} for {Url}", response.StatusCode, url);
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct);
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

    /// <summary>
    /// Returns true if the URL points to a direct image, either by
    /// matching a known image host or having an image file extension.
    /// </summary>
    internal static bool IsImageUrl(Uri uri)
    {
        if (ImageHosts.Contains(uri.Host))
            return true;

        var ext = Path.GetExtension(uri.LocalPath);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }
}
