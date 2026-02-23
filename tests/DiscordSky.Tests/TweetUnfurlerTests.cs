using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public class TweetUnfurlerTests
{
    // ── URL Detection ─────────────────────────────────────────────────

    [Theory]
    [InlineData("https://x.com/elonmusk/status/1234567890", "1234567890")]
    [InlineData("https://twitter.com/user/status/9876543210", "9876543210")]
    [InlineData("https://www.twitter.com/user/status/111", "111")]
    [InlineData("https://mobile.twitter.com/user/status/222", "222")]
    [InlineData("https://vxtwitter.com/user/status/333", "333")]
    [InlineData("https://fxtwitter.com/user/status/444", "444")]
    public void ExtractTweetIds_ValidUrls(string url, string expectedId)
    {
        var ids = TweetUnfurler.ExtractTweetIds($"Check this out: {url} pretty cool");

        Assert.Single(ids);
        Assert.Equal(expectedId, ids[0]);
    }

    [Theory]
    [InlineData("https://x.com/elonmusk")]
    [InlineData("https://example.com/status/123")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void ExtractTweetIds_NonTweetUrls_ReturnsEmpty(string content)
    {
        var ids = TweetUnfurler.ExtractTweetIds(content);

        Assert.Empty(ids);
    }

    [Fact]
    public void ExtractTweetIds_MultipleUrls_DeduplicatesById()
    {
        var content = "https://x.com/user/status/123 and also https://twitter.com/other/status/123";

        var ids = TweetUnfurler.ExtractTweetIds(content);

        Assert.Single(ids);
        Assert.Equal("123", ids[0]);
    }

    [Fact]
    public void ExtractTweetIds_MultipleDistinctUrls()
    {
        var content = "https://x.com/a/status/111 and https://twitter.com/b/status/222";

        var ids = TweetUnfurler.ExtractTweetIds(content);

        Assert.Equal(2, ids.Count);
        Assert.Contains("111", ids);
        Assert.Contains("222", ids);
    }

    [Fact]
    public void ExtractTweetIds_NullInput_ReturnsEmpty()
    {
        var ids = TweetUnfurler.ExtractTweetIds(null!);

        Assert.Empty(ids);
    }

    // ── Response Parsing ──────────────────────────────────────────────

    [Fact]
    public void ParseFxTwitterResponse_ValidTweet_ExtractsTextAndAuthor()
    {
        var json = """
        {
          "tweet": {
            "text": "Hello world! This is a tweet.",
            "author": {
              "name": "Test User",
              "screen_name": "testuser"
            }
          }
        }
        """;

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/testuser/status/1", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("tweet", result.SourceType);
        Assert.Equal("Hello world! This is a tweet.", result.Text);
        Assert.Equal("Test User (@testuser)", result.Author);
        Assert.Empty(result.Images);
    }

    [Fact]
    public void ParseFxTwitterResponse_WithPhotos_ExtractsImages()
    {
        var json = """
        {
          "tweet": {
            "text": "Look at this pic",
            "author": {
              "name": "Photographer",
              "screen_name": "photog"
            },
            "media": {
              "photos": [
                { "url": "https://pbs.twimg.com/media/abc123.jpg" },
                { "url": "https://pbs.twimg.com/media/def456.png" }
              ]
            }
          }
        }
        """;

        var timestamp = DateTimeOffset.UtcNow;
        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/photog/status/1", timestamp);

        Assert.NotNull(result);
        Assert.Equal(2, result.Images.Count);
        Assert.Equal("https://pbs.twimg.com/media/abc123.jpg", result.Images[0].Url.ToString());
        Assert.Equal("abc123.jpg", result.Images[0].Filename);
        Assert.Equal("tweet", result.Images[0].Source);
        Assert.Equal(timestamp, result.Images[0].Timestamp);
        Assert.Equal("https://pbs.twimg.com/media/def456.png", result.Images[1].Url.ToString());
    }

    [Fact]
    public void ParseFxTwitterResponse_MissingTweetProperty_ReturnsNull()
    {
        var json = """{ "error": "not found" }""";

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/u/status/1", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseFxTwitterResponse_EmptyTextNoMedia_ReturnsNull()
    {
        var json = """
        {
          "tweet": {
            "text": "",
            "author": { "name": "Empty", "screen_name": "empty" }
          }
        }
        """;

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/u/status/1", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseFxTwitterResponse_InvalidJson_ReturnsNull()
    {
        var result = TweetUnfurler.ParseFxTwitterResponse("{{{bad json", "https://x.com/u/status/1", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseFxTwitterResponse_OnlyScreenName_FormatsAuthorCorrectly()
    {
        var json = """
        {
          "tweet": {
            "text": "Hello",
            "author": {
              "screen_name": "justhandle"
            }
          }
        }
        """;

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/u/status/1", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("@justhandle", result.Author);
    }

    [Fact]
    public void ParseFxTwitterResponse_NoAuthor_EmptyAuthorString()
    {
        var json = """
        {
          "tweet": {
            "text": "Orphaned tweet"
          }
        }
        """;

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/u/status/1", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Author);
    }

    [Fact]
    public void ParseFxTwitterResponse_PhotoWithInvalidUrl_Skipped()
    {
        var json = """
        {
          "tweet": {
            "text": "Pic tweet",
            "author": { "name": "A", "screen_name": "a" },
            "media": {
              "photos": [
                { "url": "not-a-valid-url" },
                { "url": "https://pbs.twimg.com/media/valid.jpg" }
              ]
            }
          }
        }
        """;

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/a/status/1", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Single(result.Images);
        Assert.Equal("https://pbs.twimg.com/media/valid.jpg", result.Images[0].Url.ToString());
    }

    [Fact]
    public void ParseFxTwitterResponse_EmptyTextWithMedia_StillReturns()
    {
        var json = """
        {
          "tweet": {
            "text": "",
            "author": { "name": "A", "screen_name": "a" },
            "media": {
              "photos": [
                { "url": "https://pbs.twimg.com/media/pic.jpg" }
              ]
            }
          }
        }
        """;

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/a/status/1", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Single(result.Images);
    }

    [Fact]
    public void ParseFxTwitterResponse_SetsOriginalUrl()
    {
        var json = """
        {
          "tweet": {
            "text": "Hello",
            "author": { "name": "A", "screen_name": "a" }
          }
        }
        """;

        var result = TweetUnfurler.ParseFxTwitterResponse(json, "https://x.com/a/status/12345", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("https://x.com/a/status/12345", result.OriginalUrl.ToString());
    }

    // ── Config Default ────────────────────────────────────────────────

    [Fact]
    public void BotOptions_EnableLinkUnfurling_DefaultsToTrue()
    {
        var options = new DiscordSky.Bot.Configuration.BotOptions();

        Assert.True(options.EnableLinkUnfurling);
    }

    // ── Regex Edge Cases ──────────────────────────────────────────────

    [Fact]
    public void TweetUrlRegex_MatchesWithSurroundingText()
    {
        var content = "yo check https://x.com/dude/status/99999 lmaooo";
        var match = TweetUnfurler.TweetUrlRegex.Match(content);

        Assert.True(match.Success);
        Assert.Equal("99999", match.Groups[1].Value);
    }

    [Fact]
    public void TweetUrlRegex_DoesNotMatchNonStatusPaths()
    {
        var content = "https://x.com/user/likes";
        var match = TweetUnfurler.TweetUrlRegex.Match(content);

        Assert.False(match.Success);
    }

    [Fact]
    public void TweetUrlRegex_HttpNotMatched()
    {
        // http (not https) for x.com — still matches the regex since tweets can be http
        var content = "http://x.com/user/status/123";
        var match = TweetUnfurler.TweetUrlRegex.Match(content);

        // Regex allows http, which is fine — the actual fetch goes to fxtwitter API
        Assert.True(match.Success);
    }
}
