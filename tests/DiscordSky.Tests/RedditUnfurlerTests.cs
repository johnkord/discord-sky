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

    // ── BuildJsonUrl ──────────────────────────────────────────────────

    [Fact]
    public void BuildJsonUrl_StandardPostUrl_AppendsJson()
    {
        var uri = new Uri("https://www.reddit.com/r/programming/comments/abc123/my_post/");

        var result = RedditUnfurler.BuildJsonUrl(uri);

        Assert.NotNull(result);
        Assert.Contains("/r/programming/comments/abc123/my_post.json", result);
        Assert.Contains("limit=5", result);
        Assert.Contains("raw_json=1", result);
    }

    [Fact]
    public void BuildJsonUrl_ShortUrl_ExpandsToCommentsUrl()
    {
        var uri = new Uri("https://redd.it/abc123");

        var result = RedditUnfurler.BuildJsonUrl(uri);

        Assert.NotNull(result);
        Assert.Equal("https://www.reddit.com/comments/abc123.json?limit=5&raw_json=1", result);
    }

    [Fact]
    public void BuildJsonUrl_SubredditOnly_ReturnsNull()
    {
        var uri = new Uri("https://www.reddit.com/r/programming/");

        var result = RedditUnfurler.BuildJsonUrl(uri);

        Assert.Null(result);
    }

    // ── JSON Parsing ──────────────────────────────────────────────────

    [Fact]
    public void ParseRedditJson_ValidPost_ExtractsTitleAndContent()
    {
        var json = BuildRedditPostJson(
            title: "Test Post Title",
            selftext: "This is the body of the post",
            author: "testuser",
            subreddit: "r/testing",
            score: 42,
            numComments: 5,
            isSelf: true);

        var result = RedditUnfurler.ParseRedditJson(json, new Uri("https://reddit.com/r/testing/comments/abc/test/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("reddit", result.SourceType);
        Assert.Contains("Test Post Title", result.Text);
        Assert.Contains("This is the body of the post", result.Text);
        Assert.Contains("42 points", result.Text);
        Assert.Contains("5 comments", result.Text);
        Assert.Contains("u/testuser", result.Author);
    }

    [Fact]
    public void ParseRedditJson_LinkPost_IncludesExternalUrl()
    {
        var json = BuildRedditPostJson(
            title: "Check this link",
            selftext: null,
            author: "poster",
            subreddit: "r/links",
            score: 10,
            numComments: 2,
            isSelf: false,
            url: "https://example.com/article");

        var result = RedditUnfurler.ParseRedditJson(json, new Uri("https://reddit.com/r/links/comments/xyz/check/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("Link: https://example.com/article", result.Text);
    }

    [Fact]
    public void ParseRedditJson_WithComments_ExtractsTopComments()
    {
        var json = BuildRedditPostJsonWithComments(
            title: "Discussion Post",
            author: "op",
            subreddit: "r/discuss",
            comments: new[]
            {
                ("commenter1", "First insightful comment"),
                ("commenter2", "Second thoughtful response")
            });

        var result = RedditUnfurler.ParseRedditJson(json, new Uri("https://reddit.com/r/discuss/comments/aaa/post/"), DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("Top comments:", result.Text);
        Assert.Contains("u/commenter1", result.Text);
        Assert.Contains("First insightful comment", result.Text);
    }

    [Fact]
    public void ParseRedditJson_InvalidJson_ReturnsNull()
    {
        var result = RedditUnfurler.ParseRedditJson("not valid json", new Uri("https://reddit.com/r/test/comments/x/y/"), DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseRedditJson_EmptyArray_ReturnsNull()
    {
        var result = RedditUnfurler.ParseRedditJson("[]", new Uri("https://reddit.com/r/test/comments/x/y/"), DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseRedditJson_NoTitle_ReturnsNull()
    {
        var json = BuildRedditPostJson(
            title: null,
            selftext: "Body without title",
            author: "user",
            subreddit: "r/test",
            score: 0,
            numComments: 0,
            isSelf: true);

        var result = RedditUnfurler.ParseRedditJson(json, new Uri("https://reddit.com/r/test/comments/x/y/"), DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    // ── Comment Extraction ────────────────────────────────────────────

    [Fact]
    public void ExtractTopComments_TruncatesLongComments()
    {
        var longBody = new string('A', 500);
        var json = BuildCommentsListingJson(new[] { ("author1", longBody) });
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var comments = RedditUnfurler.ExtractTopComments(doc.RootElement);

        Assert.Single(comments);
        Assert.True(comments[0].Body.Length <= 301); // 300 + "…"
    }

    [Fact]
    public void ExtractTopComments_LimitsToMaxComments()
    {
        var comments = Enumerable.Range(1, 10)
            .Select(i => ($"user{i}", $"comment {i}"))
            .ToArray();
        var json = BuildCommentsListingJson(comments);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var result = RedditUnfurler.ExtractTopComments(doc.RootElement, maxComments: 3);

        Assert.Equal(3, result.Count);
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

    private static string BuildRedditPostJson(
        string? title, string? selftext, string? author, string? subreddit,
        int score, int numComments, bool isSelf, string? url = null)
    {
        var titleProp = title != null ? $"\"title\": \"{title}\"," : "";
        var selftextProp = selftext != null ? $"\"selftext\": \"{selftext}\"," : "";
        var urlProp = url != null ? $"\"url\": \"{url}\"," : "";

        return $$"""
        [
          {
            "kind": "Listing",
            "data": {
              "children": [
                {
                  "kind": "t3",
                  "data": {
                    {{titleProp}}
                    {{selftextProp}}
                    {{urlProp}}
                    "author": "{{author}}",
                    "subreddit_name_prefixed": "{{subreddit}}",
                    "score": {{score}},
                    "num_comments": {{numComments}},
                    "is_self": {{isSelf.ToString().ToLowerInvariant()}}
                  }
                }
              ]
            }
          },
          {
            "kind": "Listing",
            "data": {
              "children": []
            }
          }
        ]
        """;
    }

    private static string BuildRedditPostJsonWithComments(
        string title, string author, string subreddit,
        (string author, string body)[] comments)
    {
        var commentEntries = string.Join(",\n",
            comments.Select(c => $$"""
                {
                  "kind": "t1",
                  "data": {
                    "author": "{{c.author}}",
                    "body": "{{c.body}}",
                    "score": 10
                  }
                }
            """));

        return $$"""
        [
          {
            "kind": "Listing",
            "data": {
              "children": [
                {
                  "kind": "t3",
                  "data": {
                    "title": "{{title}}",
                    "selftext": "",
                    "author": "{{author}}",
                    "subreddit_name_prefixed": "{{subreddit}}",
                    "score": 100,
                    "num_comments": {{comments.Length}},
                    "is_self": true
                  }
                }
              ]
            }
          },
          {
            "kind": "Listing",
            "data": {
              "children": [
                {{commentEntries}}
              ]
            }
          }
        ]
        """;
    }

    private static string BuildCommentsListingJson((string author, string body)[] comments)
    {
        var entries = string.Join(",\n",
            comments.Select(c => $$"""
                {
                  "kind": "t1",
                  "data": {
                    "author": "{{c.author}}",
                    "body": "{{c.body}}",
                    "score": 5
                  }
                }
            """));

        return $$"""
        {
          "kind": "Listing",
          "data": {
            "children": [
              {{entries}}
            ]
          }
        }
        """;
    }
}
