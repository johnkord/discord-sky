using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Extracts readable text content from general web pages using AngleSharp HTML parsing.
/// Strips boilerplate (navigation, headers, footers, scripts, styles) and returns
/// clean text suitable for LLM context windows.
/// </summary>
public sealed class WebContentUnfurler : ILinkUnfurler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebContentUnfurler> _logger;

    /// <summary>
    /// Maximum characters of extracted text to include per page.
    /// Keeps LLM context usage reasonable.
    /// </summary>
    internal const int MaxContentLength = 4000;

    /// <summary>
    /// Maximum response body size to download (2 MB).
    /// Prevents memory issues from huge pages.
    /// </summary>
    private const int MaxDownloadBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Timeout for individual page fetches.
    /// </summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Realistic browser User-Agent string. Many sites (e.g. Reddit) return 403
    /// for bot-like User-Agents. Using a standard browser UA allows content access
    /// equivalent to what a user would see when clicking the link.
    /// </summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Matches common URLs in message text.
    /// Allows ')' so that URLs with balanced parentheses (e.g. Wikipedia) are captured intact.
    /// Unbalanced trailing ')' is handled by <see cref="CleanUrlString"/>.
    /// </summary>
    internal static readonly Regex UrlRegex = new(
        @"https?://[^\s<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Content types we can parse.
    /// </summary>
    private static readonly HashSet<string> AcceptableContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "application/xhtml+xml"
    };

    /// <summary>
    /// File extensions that are clearly non-HTML and should be skipped without making an HTTP request.
    /// </summary>
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico",
        ".mp4", ".webm", ".mov", ".avi", ".mkv",
        ".mp3", ".wav", ".ogg", ".flac",
        ".pdf", ".zip", ".tar", ".gz", ".rar", ".7z",
        ".exe", ".dmg", ".msi", ".apk",
        ".css", ".js", ".json", ".xml", ".woff", ".woff2", ".ttf"
    };

    /// <summary>
    /// Domains that should be skipped (handled by specialized unfurlers or non-scrapable).
    /// </summary>
    private static readonly HashSet<string> SkippedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        // Twitter/X — handled by TweetUnfurler
        "twitter.com",
        "www.twitter.com",
        "mobile.twitter.com",
        "x.com",
        "www.x.com",
        "vxtwitter.com",
        "fxtwitter.com",
        // Reddit — handled by RedditUnfurler
        "reddit.com",
        "www.reddit.com",
        "old.reddit.com",
        "new.reddit.com",
        "np.reddit.com",
        "redd.it",
        // Hacker News — handled by HackerNewsUnfurler
        "news.ycombinator.com",
        // Wikipedia — handled by WikipediaUnfurler
        // Note: CanHandle() also has a dynamic *.wikipedia.org check for all language editions
        // Media/binary-heavy sites that won't yield useful text
        "youtube.com",
        "www.youtube.com",
        "youtu.be",
        "spotify.com",
        "open.spotify.com",
        // Image hosts
        "i.imgur.com",
        "cdn.discordapp.com",
        "media.discordapp.net",
        "tenor.com",
        "giphy.com"
    };

    /// <summary>
    /// HTML elements to remove before text extraction (boilerplate/noise).
    /// </summary>
    private static readonly string[] BoilerplateSelectors =
    {
        "script", "style", "noscript", "svg", "iframe",
        "nav", "header", "footer",
        "[role='navigation']", "[role='banner']", "[role='contentinfo']",
        ".sidebar", ".menu", ".nav", ".navbar", ".navigation",
        ".ad", ".ads", ".advertisement", ".cookie-banner", ".cookie-notice",
        ".popup", ".modal", ".overlay",
        ".social-share", ".share-buttons", ".related-posts",
        ".comments", "#comments"
    };

    /// <summary>
    /// CSS selectors for main content areas, tried in order.
    /// </summary>
    private static readonly string[] ContentSelectors =
    {
        "article",
        "[role='main']",
        "main",
        ".post-content",
        ".article-content",
        ".entry-content",
        ".content",
        "#content",
        ".post",
        ".article"
    };

    public WebContentUnfurler(HttpClient httpClient, ILogger<WebContentUnfurler> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(Uri url)
    {
        // Handle any http/https URL that isn't in the skip list
        if (url.Scheme != "http" && url.Scheme != "https")
        {
            return false;
        }

        if (SkippedDomains.Contains(url.Host))
        {
            return false;
        }

        // Dynamic check for all Wikipedia language editions (300+ exist)
        if (url.Host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Skip URLs with obvious non-HTML file extensions
        var ext = Path.GetExtension(url.AbsolutePath);
        if (!string.IsNullOrEmpty(ext) && SkippedExtensions.Contains(ext))
        {
            return false;
        }

        return true;
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

        var matches = UrlRegex.Matches(messageContent);
        if (matches.Count == 0)
        {
            return Array.Empty<UnfurledLink>();
        }

        // Deduplicate by URL and filter to ones we can handle
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<(string url, Task<UnfurledLink?> task)>();

        foreach (Match match in matches)
        {
            var urlStr = CleanUrlString(match.Value);
            if (!seenUrls.Add(urlStr))
            {
                continue;
            }

            if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri) || !CanHandle(uri))
            {
                continue;
            }

            tasks.Add((urlStr, FetchAndParseAsync(uri, messageTimestamp, cancellationToken)));
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
                _logger.LogDebug(ex, "Failed to unfurl web content from {Url}", url);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches the page HTML and extracts clean text content.
    /// Handles per-URL timeouts gracefully without cancelling sibling operations.
    /// </summary>
    internal async Task<UnfurledLink?> FetchAndParseAsync(
        Uri url,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching web content from {Url}", url);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FetchTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", BrowserUserAgent);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Web fetch returned {StatusCode} for {Url}", response.StatusCode, url);
                return null;
            }

            // Check content type
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!AcceptableContentTypes.Contains(contentType))
            {
                _logger.LogDebug("Skipping non-HTML content type {ContentType} for {Url}", contentType, url);
                return null;
            }

            // Check content length if available
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                _logger.LogDebug("Skipping oversized page ({Size} bytes) for {Url}", response.Content.Headers.ContentLength, url);
                return null;
            }

            // Read with size limit
            var html = await ReadLimitedAsync(response.Content, MaxDownloadBytes, cts.Token);
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            return await ParseHtmlAsync(html, url, messageTimestamp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Per-URL timeout — not a global cancellation. Return null gracefully.
            _logger.LogDebug("Timeout fetching web content from {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Parses HTML content and extracts clean text + metadata.
    /// </summary>
    internal static async Task<UnfurledLink?> ParseHtmlAsync(string html, Uri sourceUrl, DateTimeOffset messageTimestamp)
    {
        var config = AngleSharp.Configuration.Default;
        using var context = BrowsingContext.New(config);
        using var document = await context.OpenAsync(req => req.Content(html).Address(sourceUrl.AbsoluteUri));

        // Extract title
        var title = document.Title?.Trim();

        // Extract description from meta tags
        var description = GetMetaContent(document, "description")
                       ?? GetMetaContent(document, "og:description")
                       ?? GetMetaContent(document, "twitter:description");

        // Extract author
        var author = GetMetaContent(document, "author")
                  ?? GetMetaContent(document, "og:site_name")
                  ?? sourceUrl.Host;

        // Extract OG image
        var images = new List<ChannelImage>();
        var ogImage = GetMetaContent(document, "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage) && Uri.TryCreate(ogImage, UriKind.Absolute, out var imageUri))
        {
            images.Add(new ChannelImage
            {
                Url = imageUri,
                Filename = Path.GetFileName(imageUri.LocalPath),
                Source = "web-og",
                Timestamp = messageTimestamp
            });
        }

        // Remove boilerplate elements
        foreach (var selector in BoilerplateSelectors)
        {
            try
            {
                var elements = document.QuerySelectorAll(selector);
                foreach (var element in elements)
                {
                    element.Remove();
                }
            }
            catch
            {
                // Ignore invalid selectors
            }
        }

        // Try to find main content area
        string extractedText = string.Empty;
        foreach (var selector in ContentSelectors)
        {
            var contentElement = document.QuerySelector(selector);
            if (contentElement != null)
            {
                extractedText = ExtractCleanText(contentElement);
                if (extractedText.Length > 100) // Meaningful content threshold
                {
                    break;
                }
            }
        }

        // Fall back to body if no content area found
        if (extractedText.Length <= 100 && document.Body != null)
        {
            extractedText = ExtractCleanText(document.Body);
        }

        // If we still have nothing, try description
        if (string.IsNullOrWhiteSpace(extractedText) && !string.IsNullOrWhiteSpace(description))
        {
            extractedText = description;
        }

        // No content at all
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            return null;
        }

        // Prepend title if available and not already in content
        if (!string.IsNullOrWhiteSpace(title) && !extractedText.StartsWith(title, StringComparison.OrdinalIgnoreCase))
        {
            extractedText = $"{title}\n\n{extractedText}";
        }

        // Truncate AFTER prepending title so total output is bounded
        if (extractedText.Length > MaxContentLength)
        {
            extractedText = extractedText[..MaxContentLength] + "…";
        }

        // Determine source type based on domain
        var sourceType = ClassifySource(sourceUrl);

        return new UnfurledLink
        {
            SourceType = sourceType,
            OriginalUrl = sourceUrl,
            Text = extractedText,
            Author = author ?? string.Empty,
            Images = images
        };
    }

    /// <summary>
    /// Precompiled patterns for whitespace collapsing in <see cref="ExtractCleanText"/>.
    /// </summary>
    private static readonly Regex HorizontalWhitespace = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n\s*\n", RegexOptions.Compiled);

    /// <summary>
    /// Extracts clean, readable text from an HTML element.
    /// Collapses whitespace and removes empty lines.
    /// </summary>
    internal static string ExtractCleanText(IElement element)
    {
        var text = element.TextContent ?? string.Empty;

        // Collapse multiple whitespace into single spaces
        text = HorizontalWhitespace.Replace(text, " ");

        // Collapse multiple newlines into double-newline paragraph breaks
        text = BlankLines.Replace(text, "\n\n");

        // Trim each line
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Gets meta tag content by name or property attribute.
    /// </summary>
    internal static string? GetMetaContent(IDocument document, string nameOrProperty)
    {
        var meta = document.QuerySelector($"meta[name='{nameOrProperty}']")
                ?? document.QuerySelector($"meta[property='{nameOrProperty}']");

        var content = meta?.GetAttribute("content");
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    /// <summary>
    /// Classifies the source URL into a human-readable source type.
    /// </summary>
    internal static string ClassifySource(Uri url)
    {
        var host = url.Host.ToLowerInvariant();

        if (host.Contains("reddit.com") || host.Contains("redd.it"))
            return "reddit";
        if (host.Contains("github.com"))
            return "github";
        if (host.Contains("stackoverflow.com") || host.Contains("stackexchange.com"))
            return "stackoverflow";
        if (host.Contains("wikipedia.org"))
            return "wikipedia";
        if (host.Contains("medium.com"))
            return "article";
        if (host.Contains("substack.com"))
            return "article";
        if (host.Contains("bsky.app"))
            return "bluesky";

        return "webpage";
    }

    /// <summary>
    /// Reads response content up to a maximum byte limit.
    /// Uses a MemoryStream with a small initial capacity to avoid over-allocation
    /// (most pages are well under the 2MB limit).
    /// </summary>
    private static async Task<string> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream(capacity: 64 * 1024); // 64KB initial
        var buffer = new byte[8192]; // 8KB read chunks

        while (memoryStream.Length < maxBytes)
        {
            var remaining = maxBytes - (int)memoryStream.Length;
            var toRead = Math.Min(buffer.Length, remaining);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }
            memoryStream.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
    }

    /// <summary>
    /// Cleans trailing punctuation from URL strings that were extracted from message text.
    /// Handles cases like "check out https://example.com." or "(https://example.com)".
    /// Strips one unbalanced trailing ')' at a time so balanced parens (e.g. Wikipedia) are preserved.
    /// </summary>
    internal static string CleanUrlString(string url)
    {
        // Remove trailing punctuation that's likely sentence-ending rather than part of the URL
        url = url.TrimEnd('.', ',', ';', '!', '?');

        // Remove exactly one trailing ')' at a time while parens are unbalanced.
        // This preserves balanced parens like https://en.wikipedia.org/wiki/C_(programming)
        // while stripping sentence-wrapping parens like (https://example.com)
        while (url.EndsWith(')') && url.Count(c => c == '(') < url.Count(c => c == ')'))
        {
            url = url[..^1];
        }

        return url;
    }
}
