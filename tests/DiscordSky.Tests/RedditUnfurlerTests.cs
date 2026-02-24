using System.Text.Json;
using DiscordSky.Bot.Integrations.LinkUnfurling;

namespace DiscordSky.Tests;

public class RedditUnfurlerTests
{
    // ── URL Matching ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.reddit.com/r/programming/comments/abc123/my_post/")]
    [InlineData("https://reddit.com/r/AskReddit/comments/xyz789/question/")]
    [InlineData("https://old.reddit.com/r/technology/comments/def456/title/")]
    [InlineData("https://new.reddit.com/r/science/comments/hhh111/post/")]
    [InlineData("https://redd.it/abc123")]
    public void CanHandle_ValidRedditUrls_ReturnsTrue(string url)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);

        Assert.True(unfurler.CanHandle(uri));
    }

    [Theory]
    [InlineData("https://example.com/r/test/comments/abc123/post/")]
    [InlineData("https://twitter.com/user/status/123")]
    [InlineData("https://google.com")]
    public void CanHandle_NonRedditUrls_ReturnsFalse(string url)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);

        Assert.False(unfurler.CanHandle(uri));
    }

    // ── ExtractPostId ─────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.reddit.com/r/programming/comments/abc123/my_post/", "abc123")]
    [InlineData("https://reddit.com/r/AskReddit/comments/xyz789/question/", "xyz789")]
    [InlineData("https://old.reddit.com/r/technology/comments/def456/title/", "def456")]
    [InlineData("https://redd.it/abc123", "abc123")]
    [InlineData("https://reddit.com/r/programming/comments/abc123/post_title/def456/", "abc123")]
    public void ExtractPostId_ValidPostUrls_ReturnsPostId(string url, string expectedId)
    {
        var result = RedditUnfurler.ExtractPostId(new Uri(url));

        Assert.Equal(expectedId, result);
    }

    [Theory]
    [InlineData("https://www.reddit.com/r/programming/")]
    [InlineData("https://reddit.com/")]
    public void ExtractPostId_NonPostUrls_ReturnsNull(string url)
    {
        var result = RedditUnfurler.ExtractPostId(new Uri(url));

        Assert.Null(result);
    }

    // ── ParseArcticShiftResponse ──────────────────────────────────────

    [Fact]
    public void ParseArcticShiftResponse_ValidPost_ExtractsTitleAndContent()
    {
        var postJson = BuildPostJson(
            title: "Test Post Title",
            selftext: "This is the body of the post",
            author: "testuser",
            subreddit: "r/testing",
            score: 42,
            numComments: 5,
            isSelf: true);

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/testing/comments/abc/test/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("reddit", result.SourceType);
        Assert.Contains("Test Post Title", result.Text);
        Assert.Contains("This is the body of the post", result.Text);
        Assert.Contains("42 points", result.Text);
        Assert.Contains("5 comments", result.Text);
        Assert.Contains("u/testuser", result.Author);
    }

    [Fact]
    public void ParseArcticShiftResponse_LinkPost_IncludesExternalUrl()
    {
        var postJson = BuildPostJson(
            title: "Check this link",
            selftext: null,
            author: "poster",
            subreddit: "r/links",
            score: 10,
            numComments: 2,
            isSelf: false,
            url: "https://example.com/article");

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/links/comments/xyz/check/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("Link: https://example.com/article", result.Text);
    }

    [Fact]
    public void ParseArcticShiftResponse_WithComments_ExtractsTopComments()
    {
        var postJson = BuildPostJson(
            title: "Discussion Post",
            author: "op",
            subreddit: "r/discuss",
            score: 100,
            numComments: 2,
            isSelf: true);

        var commentsJson = BuildCommentsJson(new[]
        {
            ("commenter1", "First insightful comment"),
            ("commenter2", "Second thoughtful response")
        });

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, commentsJson,
            new Uri("https://reddit.com/r/discuss/comments/aaa/post/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("Top comments:", result.Text);
        Assert.Contains("u/commenter1", result.Text);
        Assert.Contains("First insightful comment", result.Text);
    }

    [Fact]
    public void ParseArcticShiftResponse_InvalidJson_ReturnsNull()
    {
        var result = RedditUnfurler.ParseArcticShiftResponse(
            "not valid json", null,
            new Uri("https://reddit.com/r/test/comments/x/y/"), DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseArcticShiftResponse_EmptyDataArray_ReturnsNull()
    {
        var result = RedditUnfurler.ParseArcticShiftResponse(
            """{"data": []}""", null,
            new Uri("https://reddit.com/r/test/comments/x/y/"), DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseArcticShiftResponse_NoTitle_ReturnsNull()
    {
        var postJson = BuildPostJson(
            title: null,
            selftext: "Body without title",
            author: "user",
            subreddit: "r/test",
            score: 0,
            numComments: 0,
            isSelf: true);

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/test/comments/x/y/"), DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseArcticShiftResponse_SubredditWithoutPrefix_AddsPrefix()
    {
        // Arctic Shift may return "subreddit" without the "r/" prefix
        var postJson = """{"data": [{"title": "Test", "author": "user", "subreddit": "programming", "score": 10, "num_comments": 1, "is_self": true}]}""";

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/programming/comments/abc/test/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("r/programming", result.Text);
        Assert.Contains("r/programming", result.Author);
    }

    [Fact]
    public void ParseArcticShiftResponse_NullCommentsJson_StillReturnsPost()
    {
        var postJson = BuildPostJson(
            title: "Solo Post",
            author: "author",
            subreddit: "r/test",
            score: 5,
            numComments: 0,
            isSelf: true);

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/test/comments/x/y/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("Solo Post", result.Text);
        Assert.DoesNotContain("Top comments:", result.Text);
    }

    // ── Comment Extraction ────────────────────────────────────────────

    [Fact]
    public void ExtractTopComments_TruncatesLongComments()
    {
        var longBody = new string('A', 500);
        var json = BuildCommentsJson(new[] { ("author1", longBody) });
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var comments = RedditUnfurler.ExtractTopComments(data);

        Assert.Single(comments);
        Assert.True(comments[0].Body.Length <= 301); // 300 + "…"
    }

    [Fact]
    public void ExtractTopComments_LimitsToMaxComments()
    {
        var comments = Enumerable.Range(1, 10)
            .Select(i => ($"user{i}", $"comment {i}"))
            .ToArray();
        var json = BuildCommentsJson(comments);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var result = RedditUnfurler.ExtractTopComments(data, maxComments: 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ExtractTopComments_SkipsEmptyBodies()
    {
        var json = BuildCommentsJson(new[]
        {
            ("user1", ""),
            ("user2", "valid comment"),
            ("user3", "  ")
        });
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var result = RedditUnfurler.ExtractTopComments(data);

        Assert.Single(result);
        Assert.Equal("user2", result[0].Author);
    }

    [Fact]
    public void ExtractTopComments_DeletedAuthor_ShowsDeleted()
    {
        var json = """{"data": [{"body": "orphan comment", "score": 1}]}""";
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var result = RedditUnfurler.ExtractTopComments(data);

        Assert.Single(result);
        Assert.Equal("[deleted]", result[0].Author);
    }

    // ── Image Extraction ────────────────────────────────────────────

    [Theory]
    [InlineData("https://i.redd.it/abc123.jpg", true)]
    [InlineData("https://i.imgur.com/xyz.png", true)]
    [InlineData("https://preview.redd.it/img.webp", true)]
    [InlineData("https://pbs.twimg.com/media/img.jpg", true)]
    [InlineData("https://example.com/photo.jpg", true)]     // image extension
    [InlineData("https://example.com/photo.gif", true)]
    [InlineData("https://example.com/page", false)]          // no extension, unknown host
    [InlineData("https://reddit.com/r/pics/comments/x/y/", false)]
    public void IsImageUrl_DetectsImageUrls(string url, bool expected)
    {
        Assert.Equal(expected, RedditUnfurler.IsImageUrl(new Uri(url)));
    }

    [Fact]
    public void ParseArcticShiftResponse_ImagePost_ExtractsFullResImage()
    {
        var postJson = BuildPostJson(
            title: "Cool sunset",
            author: "photographer",
            subreddit: "r/pics",
            score: 500,
            numComments: 20,
            isSelf: false,
            url: "https://i.redd.it/abc123.jpg");

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/pics/comments/xyz/cool_sunset/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Single(result.Images);
        Assert.Equal("reddit-image", result.Images[0].Source);
        Assert.Equal(new Uri("https://i.redd.it/abc123.jpg"), result.Images[0].Url);
        Assert.Equal("abc123.jpg", result.Images[0].Filename);
    }

    [Fact]
    public void ParseArcticShiftResponse_NonImageLinkPost_FallsBackToThumbnail()
    {
        // Post links to a webpage, not a direct image — should use thumbnail
        var postJson = """{"data": [{"title": "Article", "author": "user", "subreddit_name_prefixed": "r/news", "url": "https://example.com/article", "is_self": false, "score": 10, "num_comments": 1, "thumbnail": "https://b.thumbs.redditmedia.com/thumb.jpg"}]}""";

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/news/comments/abc/article/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Single(result.Images);
        Assert.Equal("reddit-thumbnail", result.Images[0].Source);
    }

    [Fact]
    public void ParseArcticShiftResponse_SelfPost_NoImages()
    {
        var postJson = BuildPostJson(
            title: "Question",
            selftext: "Why?",
            author: "user",
            subreddit: "r/AskReddit",
            isSelf: true);

        var result = RedditUnfurler.ParseArcticShiftResponse(
            postJson, null,
            new Uri("https://reddit.com/r/AskReddit/comments/abc/question/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Empty(result.Images);
    }

    // ── UnfurlAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task UnfurlAsync_EmptyMessage_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();

        var result = await unfurler.UnfurlAsync("", DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public async Task UnfurlAsync_NoRedditUrls_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();

        var result = await unfurler.UnfurlAsync("Just a regular message https://google.com", DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static RedditUnfurler CreateUnfurler()
    {
        return new RedditUnfurler(
            new HttpClient(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditUnfurler>.Instance);
    }

    /// <summary>
    /// Builds an Arctic Shift post API response: {"data": [{...flat post fields...}]}
    /// </summary>
    private static string BuildPostJson(
        string? title, string? selftext = null, string? author = null, string? subreddit = null,
        int score = 0, int numComments = 0, bool isSelf = true, string? url = null)
    {
        var props = new List<string>();
        if (title != null) props.Add($"\"title\": \"{title}\"");
        if (selftext != null) props.Add($"\"selftext\": \"{selftext}\"");
        if (author != null) props.Add($"\"author\": \"{author}\"");
        if (subreddit != null) props.Add($"\"subreddit_name_prefixed\": \"{subreddit}\"");
        if (url != null) props.Add($"\"url\": \"{url}\"");
        props.Add($"\"score\": {score}");
        props.Add($"\"num_comments\": {numComments}");
        props.Add($"\"is_self\": {isSelf.ToString().ToLowerInvariant()}");

        return $$"""{"data": [{ {{string.Join(", ", props)}} }]}""";
    }

    /// <summary>
    /// Builds an Arctic Shift comments API response: {"data": [{...flat comment...}, ...]}
    /// </summary>
    private static string BuildCommentsJson((string author, string body)[] comments)
    {
        var entries = string.Join(", ",
            comments.Select(c => $$"""{ "author": "{{c.author}}", "body": "{{c.body}}", "score": 5 }"""));

        return $$"""{"data": [{{entries}}]}""";
    }
}
