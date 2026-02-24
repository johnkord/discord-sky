using DiscordSky.Bot.Integrations.LinkUnfurling;

namespace DiscordSky.Tests;

public class HackerNewsUnfurlerTests
{
    // ── URL Matching ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://news.ycombinator.com/item?id=12345678")]
    [InlineData("https://news.ycombinator.com/item?id=1")]
    [InlineData("http://news.ycombinator.com/item?id=99999999")]
    public void CanHandle_ValidHnUrls_ReturnsTrue(string url)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);

        Assert.True(unfurler.CanHandle(uri));
    }

    [Theory]
    [InlineData("https://news.ycombinator.com/")]
    [InlineData("https://news.ycombinator.com/newest")]
    [InlineData("https://example.com/item?id=123")]
    [InlineData("https://reddit.com/r/test")]
    public void CanHandle_NonHnUrls_ReturnsFalse(string url)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);

        Assert.False(unfurler.CanHandle(uri));
    }

    // ── URL Regex Extraction ──────────────────────────────────────────

    [Fact]
    public void HnUrlRegex_ExtractsItemId()
    {
        var match = HackerNewsUnfurler.HnUrlRegex.Match("https://news.ycombinator.com/item?id=42567890");

        Assert.True(match.Success);
        Assert.Equal("42567890", match.Groups[1].Value);
    }

    [Fact]
    public void HnUrlRegex_MultipleUrls_FindsAll()
    {
        var content = "Check https://news.ycombinator.com/item?id=111 and https://news.ycombinator.com/item?id=222";

        var matches = HackerNewsUnfurler.HnUrlRegex.Matches(content);

        Assert.Equal(2, matches.Count);
        Assert.Equal("111", matches[0].Groups[1].Value);
        Assert.Equal("222", matches[1].Groups[1].Value);
    }

    // ── JSON Parsing ──────────────────────────────────────────────────

    [Fact]
    public void ParseHnItem_ValidStory_ExtractsTitleAndMetadata()
    {
        var json = """
        {
          "by": "dhouston",
          "descendants": 71,
          "id": 8863,
          "score": 111,
          "time": 1175714200,
          "title": "My YC app: Dropbox - Throw away your USB drive",
          "type": "story",
          "url": "http://www.getdropbox.com/u/2/screencast.html"
        }
        """;

        var result = HackerNewsUnfurler.ParseHnItem(json, "https://news.ycombinator.com/item?id=8863", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("hackernews", result.SourceType);
        Assert.Contains("My YC app: Dropbox", result.Text);
        Assert.Contains("111 points", result.Text);
        Assert.Contains("by dhouston", result.Text);
        Assert.Contains("71 comments", result.Text);
        Assert.Contains("Link: http://www.getdropbox.com/u/2/screencast.html", result.Text);
        Assert.Equal("dhouston", result.Author);
        Assert.Empty(result.Images);
    }

    [Fact]
    public void ParseHnItem_AskHnPost_IncludesTextContent()
    {
        var json = """
        {
          "by": "askuser",
          "descendants": 15,
          "id": 99999,
          "score": 50,
          "text": "<p>What are your favorite programming books?</p><p>Looking for recommendations.</p>",
          "title": "Ask HN: Best programming books?",
          "type": "story"
        }
        """;

        var result = HackerNewsUnfurler.ParseHnItem(json, "https://news.ycombinator.com/item?id=99999", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("Ask HN: Best programming books?", result.Text);
        Assert.Contains("What are your favorite programming books?", result.Text);
        Assert.Contains("Looking for recommendations.", result.Text);
    }

    [Fact]
    public void ParseHnItem_Comment_SetsSourceTypeToHnComment()
    {
        var json = """
        {
          "by": "commenter",
          "id": 55555,
          "text": "This is a really good point.",
          "type": "comment"
        }
        """;

        var result = HackerNewsUnfurler.ParseHnItem(json, "https://news.ycombinator.com/item?id=55555", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("hn-comment", result.SourceType);
        Assert.Contains("This is a really good point.", result.Text);
    }

    [Fact]
    public void ParseHnItem_DeletedItem_ReturnsNull()
    {
        var json = """
        {
          "deleted": true,
          "id": 11111,
          "type": "comment"
        }
        """;

        var result = HackerNewsUnfurler.ParseHnItem(json, "https://news.ycombinator.com/item?id=11111", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseHnItem_DeadItem_ReturnsNull()
    {
        var json = """
        {
          "dead": true,
          "id": 22222,
          "type": "story",
          "title": "Spam link"
        }
        """;

        var result = HackerNewsUnfurler.ParseHnItem(json, "https://news.ycombinator.com/item?id=22222", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseHnItem_InvalidJson_ReturnsNull()
    {
        var result = HackerNewsUnfurler.ParseHnItem("not json", "https://news.ycombinator.com/item?id=1", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseHnItem_EmptyContent_ReturnsNull()
    {
        var json = """
        {
          "id": 33333,
          "type": "story"
        }
        """;

        var result = HackerNewsUnfurler.ParseHnItem(json, "https://news.ycombinator.com/item?id=33333", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    // ── HTML Stripping ────────────────────────────────────────────────

    [Fact]
    public void StripHtmlTags_RemovesTags()
    {
        var html = "<p>Hello <b>world</b></p><p>Second paragraph</p>";

        var result = HackerNewsUnfurler.StripHtmlTags(html);

        Assert.DoesNotContain("<", result);
        Assert.Contains("Hello world", result);
        Assert.Contains("Second paragraph", result);
    }

    [Fact]
    public void StripHtmlTags_DecodesEntities()
    {
        var html = "Tom &amp; Jerry &lt;3 &gt; &quot;fun&quot;";

        var result = HackerNewsUnfurler.StripHtmlTags(html);

        Assert.Equal("Tom & Jerry <3 > \"fun\"", result);
    }

    [Fact]
    public void StripHtmlTags_ConvertsBrToNewlines()
    {
        var html = "Line one<br>Line two<br/>Line three<br />";

        var result = HackerNewsUnfurler.StripHtmlTags(html);

        Assert.Contains("Line one\nLine two\nLine three", result);
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
    public async Task UnfurlAsync_NoHnUrls_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();

        var result = await unfurler.UnfurlAsync("Just text with https://google.com", DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static HackerNewsUnfurler CreateUnfurler()
    {
        return new HackerNewsUnfurler(
            new HttpClient(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HackerNewsUnfurler>.Instance);
    }
}
