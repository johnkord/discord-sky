using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public class WebContentUnfurlerTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ── CanHandle ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.reddit.com/r/csharp/comments/abc123/", true)]
    [InlineData("https://old.reddit.com/r/programming/comments/xyz/", true)]
    [InlineData("https://en.wikipedia.org/wiki/C_Sharp", true)]
    [InlineData("https://stackoverflow.com/questions/12345/", true)]
    [InlineData("https://medium.com/@user/article-title-abc123", true)]
    [InlineData("https://example.com/page", true)]
    [InlineData("https://news.ycombinator.com/item?id=12345", true)]
    [InlineData("https://github.com/dotnet/runtime/issues/1", true)]
    public void CanHandle_GeneralUrls_ReturnsTrue(string url, bool expected)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);
        Assert.Equal(expected, unfurler.CanHandle(uri));
    }

    [Theory]
    [InlineData("https://twitter.com/user/status/123")]
    [InlineData("https://x.com/user/status/123")]
    [InlineData("https://www.twitter.com/user/status/456")]
    [InlineData("https://mobile.twitter.com/user/status/789")]
    [InlineData("https://vxtwitter.com/user/status/111")]
    [InlineData("https://fxtwitter.com/user/status/222")]
    [InlineData("https://youtube.com/watch?v=abc")]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("https://open.spotify.com/track/abc")]
    [InlineData("https://i.imgur.com/abc.jpg")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/img.png")]
    [InlineData("https://tenor.com/view/gif-123")]
    public void CanHandle_SkippedDomains_ReturnsFalse(string url)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);
        Assert.False(unfurler.CanHandle(uri));
    }

    [Fact]
    public void CanHandle_NonHttpScheme_ReturnsFalse()
    {
        var unfurler = CreateUnfurler();
        Assert.False(unfurler.CanHandle(new Uri("ftp://example.com/file")));
        Assert.False(unfurler.CanHandle(new Uri("mailto:user@example.com")));
    }

    [Theory]
    [InlineData("https://example.com/photo.jpg")]
    [InlineData("https://example.com/photo.png")]
    [InlineData("https://example.com/photo.gif")]
    [InlineData("https://example.com/photo.webp")]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("https://example.com/doc.pdf")]
    [InlineData("https://example.com/archive.zip")]
    [InlineData("https://example.com/style.css")]
    [InlineData("https://example.com/script.js")]
    [InlineData("https://example.com/data.json")]
    public void CanHandle_SkippedFileExtensions_ReturnsFalse(string url)
    {
        var unfurler = CreateUnfurler();
        Assert.False(unfurler.CanHandle(new Uri(url)));
    }

    [Theory]
    [InlineData("https://example.com/article.html")]
    [InlineData("https://example.com/page.php")]
    [InlineData("https://example.com/post.aspx")]
    [InlineData("https://example.com/page")]
    [InlineData("https://example.com/")]
    public void CanHandle_HtmlOrNoExtension_ReturnsTrue(string url)
    {
        var unfurler = CreateUnfurler();
        Assert.True(unfurler.CanHandle(new Uri(url)));
    }

    // ── ClassifySource ────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.reddit.com/r/csharp/", "reddit")]
    [InlineData("https://old.reddit.com/r/dotnet/", "reddit")]
    [InlineData("https://redd.it/abc123", "reddit")]
    [InlineData("https://github.com/dotnet/runtime", "github")]
    [InlineData("https://stackoverflow.com/questions/123", "stackoverflow")]
    [InlineData("https://cs.stackexchange.com/questions/123", "stackoverflow")]
    [InlineData("https://en.wikipedia.org/wiki/Test", "wikipedia")]
    [InlineData("https://medium.com/@user/article", "article")]
    [InlineData("https://example.substack.com/p/post", "article")]
    [InlineData("https://bsky.app/profile/user/post/123", "bluesky")]
    [InlineData("https://example.com/page", "webpage")]
    [InlineData("https://news.ycombinator.com/item?id=123", "webpage")]
    public void ClassifySource_IdentifiesDomainCorrectly(string url, string expectedType)
    {
        var result = WebContentUnfurler.ClassifySource(new Uri(url));
        Assert.Equal(expectedType, result);
    }

    // ── CleanUrlString ────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com.", "https://example.com")]
    [InlineData("https://example.com,", "https://example.com")]
    [InlineData("https://example.com;", "https://example.com")]
    [InlineData("https://example.com!", "https://example.com")]
    [InlineData("https://example.com?", "https://example.com")]
    [InlineData("https://example.com/page)", "https://example.com/page")]
    [InlineData("https://example.com/page", "https://example.com/page")]
    [InlineData("https://en.wikipedia.org/wiki/C_(programming)", "https://en.wikipedia.org/wiki/C_(programming)")]
    [InlineData("https://en.wikipedia.org/wiki/C_(programming))", "https://en.wikipedia.org/wiki/C_(programming)")]
    [InlineData("https://example.com/path))", "https://example.com/path")]
    public void CleanUrlString_RemovesTrailingPunctuation(string input, string expected)
    {
        Assert.Equal(expected, WebContentUnfurler.CleanUrlString(input));
    }

    // ── ParseHtmlAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ParseHtmlAsync_SimpleArticle_ExtractsContent()
    {
        var html = @"
<!DOCTYPE html>
<html>
<head>
    <title>Test Article Title</title>
    <meta name=""description"" content=""A short description of the article."">
    <meta name=""author"" content=""John Doe"">
</head>
<body>
    <nav>Navigation links here</nav>
    <article>
        <h1>Test Article Title</h1>
        <p>This is the main content of the article. It contains several paragraphs of meaningful text that should be extracted by the unfurler.</p>
        <p>Second paragraph with more information about the topic being discussed.</p>
    </article>
    <footer>Footer content</footer>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/article"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Equal("webpage", result.SourceType);
        Assert.Contains("main content of the article", result.Text);
        Assert.Contains("Second paragraph", result.Text);
        Assert.DoesNotContain("Navigation links here", result.Text);
        Assert.DoesNotContain("Footer content", result.Text);
        Assert.Equal("John Doe", result.Author);
    }

    [Fact]
    public async Task ParseHtmlAsync_ExtractsTitle()
    {
        var html = @"
<html>
<head><title>My Page Title</title></head>
<body>
    <article>
        <p>Some substantial content here that is long enough to pass the threshold for being considered real content.</p>
        <p>Additional paragraph to ensure we have enough text for the parser to consider this meaningful content.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Contains("My Page Title", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_ExtractsOgImage()
    {
        var html = @"
<html>
<head>
    <meta property=""og:image"" content=""https://example.com/image.jpg"">
</head>
<body>
    <article>
        <p>Article content here with enough text to pass the minimum threshold of characters needed.</p>
        <p>More content to ensure adequate length for the unfurler to consider this valid.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Single(result.Images);
        Assert.Equal("https://example.com/image.jpg", result.Images[0].Url.AbsoluteUri);
        Assert.Equal("web-og", result.Images[0].Source);
    }

    [Fact]
    public async Task ParseHtmlAsync_RemovesScriptsAndStyles()
    {
        var html = @"
<html>
<body>
    <style>body { color: red; } .hidden { display: none; }</style>
    <script>alert('should not appear in output'); var x = 42;</script>
    <article>
        <p>Visible content that should be extracted from the page and included in the unfurled link text output.</p>
    </article>
    <script>console.log('also removed');</script>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.DoesNotContain("alert", result.Text);
        Assert.DoesNotContain("console.log", result.Text);
        Assert.DoesNotContain("color: red", result.Text);
        Assert.Contains("Visible content", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_EmptyBody_ReturnsNull()
    {
        var html = @"
<html>
<head><title>Empty Page</title></head>
<body>
    <script>var x = 1;</script>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.Null(result);
    }

    [Fact]
    public async Task ParseHtmlAsync_FallsBackToBody_WhenNoArticleElement()
    {
        var html = @"
<html>
<body>
    <div>
        <p>This is body content without any article, main, or content-specific elements. It should still be extracted correctly.</p>
        <p>Another paragraph of content to ensure we have enough text to pass the minimum meaningful content threshold.</p>
    </div>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Contains("body content", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_TruncatesLongContent()
    {
        var longParagraph = string.Join(" ", Enumerable.Repeat("word", 2000));
        var html = $@"
<html>
<body>
    <article>
        <p>{longParagraph}</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        // Title is prepended BEFORE truncation, so total length is bounded
        Assert.True(result.Text.Length <= WebContentUnfurler.MaxContentLength + 1, // +1 for the '…' char
            $"Text length {result.Text.Length} exceeds expected maximum of {WebContentUnfurler.MaxContentLength + 1}");
        Assert.Contains("…", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_UsesDescriptionFallback()
    {
        var html = @"
<html>
<head>
    <meta name=""description"" content=""A detailed meta description of the page."">
</head>
<body>
    <nav>Just navigation links</nav>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Contains("detailed meta description", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_RedditUrl_ClassifiedAsReddit()
    {
        var html = @"
<html>
<head><title>r/csharp - Cool Post</title></head>
<body>
    <article>
        <p>This is a reddit post with substantial content that discusses something interesting about C# programming language.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://www.reddit.com/r/csharp/comments/abc/cool_post/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Equal("reddit", result.SourceType);
    }

    [Fact]
    public async Task ParseHtmlAsync_OgSiteName_UsedAsAuthor()
    {
        var html = @"
<html>
<head>
    <meta property=""og:site_name"" content=""The New York Times"">
</head>
<body>
    <article>
        <p>Some article content from The New York Times that has enough text to be considered meaningful by the parser.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://nytimes.com/article/test"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Equal("The New York Times", result.Author);
    }

    [Fact]
    public async Task ParseHtmlAsync_MultipleParagraphs_PreservesStructure()
    {
        var html = @"
<html>
<body>
    <article>
        <h1>Article Heading</h1>
        <p>First paragraph of the article with introductory content.</p>
        <p>Second paragraph continues the discussion with more details.</p>
        <p>Third paragraph wraps up the main points of the article.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Contains("First paragraph", result.Text);
        Assert.Contains("Second paragraph", result.Text);
        Assert.Contains("Third paragraph", result.Text);
    }

    // ── ExtractCleanText (tested through ParseHtmlAsync) ────────────

    [Fact]
    public async Task ParseHtmlAsync_CollapsesWhitespace()
    {
        var html = @"
<html>
<body>
    <article>
        <p>Text   with    lots     of      spaces    and      more    text to make it long enough for extraction.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        // Multiple spaces should be collapsed
        Assert.DoesNotContain("  ", result.Text);
        Assert.Contains("Text with lots of spaces", result.Text);
    }

    // ── GetMetaContent (tested through ParseHtmlAsync) ────────────────

    [Fact]
    public async Task ParseHtmlAsync_ExtractsMetaDescription()
    {
        var html = @"<html><head><meta name=""description"" content=""Test desc""></head><body><article><p>Just enough content to satisfy the minimum text threshold for the parser.</p></article></body></html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        // Description is used when body text is short
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ParseHtmlAsync_ExtractsOgDescription()
    {
        // When body has no content, og:description is the fallback
        var html = @"<html><head><meta property=""og:description"" content=""OG desc for the page""></head><body><nav>nav only</nav></body></html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Contains("OG desc for the page", result.Text);
    }

    // ── URL regex ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Check https://example.com/page out", 1)]
    [InlineData("Two links: https://a.com and https://b.com", 2)]
    [InlineData("No links here", 0)]
    [InlineData("http://old-school.com/path?q=1&r=2#section", 1)]
    [InlineData("https://example.com/path/to/page.html", 1)]
    [InlineData("(see https://en.wikipedia.org/wiki/C_(programming))", 1)]
    public void UrlRegex_MatchesCorrectCount(string content, int expectedCount)
    {
        var matches = WebContentUnfurler.UrlRegex.Matches(content);
        Assert.Equal(expectedCount, matches.Count);
    }

    [Fact]
    public void UrlRegex_WikipediaUrl_CapturesBalancedParens()
    {
        var content = "See https://en.wikipedia.org/wiki/C_(programming) for details";
        var match = WebContentUnfurler.UrlRegex.Match(content);
        var cleaned = WebContentUnfurler.CleanUrlString(match.Value);
        Assert.Equal("https://en.wikipedia.org/wiki/C_(programming)", cleaned);
    }

    [Fact]
    public void UrlRegex_WikipediaUrl_InParens_CleansCorrectly()
    {
        var content = "(see https://en.wikipedia.org/wiki/C_(programming))";
        var match = WebContentUnfurler.UrlRegex.Match(content);
        var cleaned = WebContentUnfurler.CleanUrlString(match.Value);
        Assert.Equal("https://en.wikipedia.org/wiki/C_(programming)", cleaned);
    }

    // ── UnfurlAsync with message containing no URLs ───────────────────

    [Fact]
    public async Task UnfurlAsync_NoUrls_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();
        var result = await unfurler.UnfurlAsync("just some text without urls", TestTimestamp);

        Assert.Empty(result);
    }

    [Fact]
    public async Task UnfurlAsync_EmptyString_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();
        var result = await unfurler.UnfurlAsync("", TestTimestamp);

        Assert.Empty(result);
    }

    [Fact]
    public async Task UnfurlAsync_NullString_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();
        var result = await unfurler.UnfurlAsync(null!, TestTimestamp);

        Assert.Empty(result);
    }

    [Fact]
    public async Task UnfurlAsync_SkippedDomain_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();
        var result = await unfurler.UnfurlAsync("check https://twitter.com/user/status/123", TestTimestamp);

        Assert.Empty(result);
    }

    // ── ParseHtmlAsync: edge cases ────────────────────────────────────

    [Fact]
    public async Task ParseHtmlAsync_RemovesSidebar()
    {
        var html = @"
<html>
<body>
    <div class=""sidebar"">Sidebar content should be removed from the output</div>
    <article>
        <p>Main article content that should remain in the extracted text output.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.DoesNotContain("Sidebar content", result.Text);
        Assert.Contains("Main article content", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_RemovesCookieBanner()
    {
        var html = @"
<html>
<body>
    <div class=""cookie-banner"">We use cookies. Accept?</div>
    <article>
        <p>The actual page content is here and should be extracted without cookie banners.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.DoesNotContain("We use cookies", result.Text);
        Assert.Contains("actual page content", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_PrefersArticleOverBody()
    {
        var html = @"
<html>
<body>
    <div>
        <p>This large block of text is outside the article element and should be ignored when article content is available since article is preferred.</p>
    </div>
    <article>
        <p>This is the article content that should be extracted because article element takes priority over general body content.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://example.com/"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Contains("article content that should be extracted", result.Text);
    }

    [Fact]
    public async Task ParseHtmlAsync_WikipediaUrl_ClassifiedCorrectly()
    {
        var html = @"
<html>
<body>
    <article>
        <p>Wikipedia article content about some topic that is interesting and educational.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://en.wikipedia.org/wiki/Test"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Equal("wikipedia", result.SourceType);
    }

    [Fact]
    public async Task ParseHtmlAsync_GitHubUrl_ClassifiedCorrectly()
    {
        var html = @"
<html>
<body>
    <article>
        <p>GitHub repository page with README content that describes the project and its purpose.</p>
    </article>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://github.com/dotnet/runtime"), TestTimestamp);

        Assert.NotNull(result);
        Assert.Equal("github", result.SourceType);
    }

    // ── Integration-style: parse real-world-like HTML ─────────────────

    [Fact]
    public async Task ParseHtmlAsync_BlogPost_ExtractsCleanly()
    {
        var html = @"
<!DOCTYPE html>
<html lang=""en"">
<head>
    <title>10 Tips for Better C# Code</title>
    <meta name=""description"" content=""Improve your C# with these tips"">
    <meta property=""og:site_name"" content=""DevBlog"">
    <meta property=""og:image"" content=""https://devblog.com/img/csharp-tips.png"">
</head>
<body>
    <header>
        <nav class=""navbar"">
            <a href=""/"">Home</a>
            <a href=""/blog"">Blog</a>
            <a href=""/about"">About</a>
        </nav>
    </header>

    <article class=""post-content"">
        <h1>10 Tips for Better C# Code</h1>
        <p>Published by DevBlog Team on January 15, 2025</p>

        <h2>Tip 1: Use Pattern Matching</h2>
        <p>Pattern matching in C# has evolved significantly. Modern switch expressions provide concise and readable code.</p>

        <h2>Tip 2: Embrace Records</h2>
        <p>Records provide value-based equality and immutability by default, making them ideal for DTOs and value objects.</p>

        <h2>Tip 3: Async All The Way</h2>
        <p>Always use async/await consistently throughout your call chain to avoid deadlocks and improve scalability.</p>
    </article>

    <div class=""sidebar"">
        <h3>Popular Posts</h3>
        <ul><li>Post 1</li><li>Post 2</li></ul>
    </div>

    <div class=""comments"">
        <h3>Comments</h3>
        <p>Great article! - User123</p>
    </div>

    <div class=""social-share"">
        <button>Share on Twitter</button>
        <button>Share on Facebook</button>
    </div>

    <footer>
        <p>© 2025 DevBlog. All rights reserved.</p>
    </footer>
</body>
</html>";

        var result = await WebContentUnfurler.ParseHtmlAsync(html, new Uri("https://devblog.com/10-tips"), TestTimestamp);

        Assert.NotNull(result);

        // Content should include article body
        Assert.Contains("Pattern Matching", result.Text);
        Assert.Contains("Embrace Records", result.Text);
        Assert.Contains("Async All The Way", result.Text);

        // Should remove boilerplate
        Assert.DoesNotContain("Popular Posts", result.Text);
        Assert.DoesNotContain("Great article!", result.Text);
        Assert.DoesNotContain("Share on Twitter", result.Text);
        Assert.DoesNotContain("© 2025 DevBlog", result.Text);

        // Metadata
        Assert.Equal("DevBlog", result.Author);
        Assert.Single(result.Images);
        Assert.Equal("https://devblog.com/img/csharp-tips.png", result.Images[0].Url.AbsoluteUri);
    }

    // ── Helper ────────────────────────────────────────────────────────

    private static WebContentUnfurler CreateUnfurler()
    {
        var handler = new HttpClientHandler();
        var httpClient = new HttpClient(handler);
        var logger = new TestLogger<WebContentUnfurler>();
        return new WebContentUnfurler(httpClient, logger);
    }

    /// <summary>
    /// Minimal ILogger implementation for testing.
    /// </summary>
    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
