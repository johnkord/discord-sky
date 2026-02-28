using DiscordSky.Bot.Integrations.LinkUnfurling;

namespace DiscordSky.Tests;

public class WikipediaUnfurlerTests
{
    // ── URL Matching ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Artificial_intelligence")]
    [InlineData("https://de.wikipedia.org/wiki/K%C3%BCnstliche_Intelligenz")]
    [InlineData("https://en.m.wikipedia.org/wiki/Artificial_intelligence")]
    [InlineData("https://fr.wikipedia.org/wiki/Intelligence_artificielle")]
    [InlineData("http://ja.wikipedia.org/wiki/人工知能")]
    public void CanHandle_ValidWikipediaUrls_ReturnsTrue(string url)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);

        Assert.True(unfurler.CanHandle(uri));
    }

    [Theory]
    [InlineData("https://en.wiktionary.org/wiki/hello")]
    [InlineData("https://example.com/wiki/test")]
    [InlineData("https://reddit.com/r/wikipedia")]
    [InlineData("https://news.ycombinator.com/item?id=123")]
    public void CanHandle_NonWikipediaUrls_ReturnsFalse(string url)
    {
        var unfurler = CreateUnfurler();
        var uri = new Uri(url);

        Assert.False(unfurler.CanHandle(uri));
    }

    // ── URL Regex Extraction ──────────────────────────────────────────

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Rust_(programming_language)", "en", "Rust_(programming_language)")]
    [InlineData("https://de.wikipedia.org/wiki/Berlin", "de", "Berlin")]
    [InlineData("https://en.m.wikipedia.org/wiki/Mobile_Article", "en", "Mobile_Article")]
    public void WikiUrlRegex_ExtractsLanguageAndTitle(string url, string expectedLang, string expectedTitle)
    {
        var match = WikipediaUnfurler.WikiUrlRegex.Match(url);

        Assert.True(match.Success);
        Assert.Equal(expectedLang, match.Groups[1].Value);
        Assert.Equal(expectedTitle, match.Groups[2].Value);
    }

    [Fact]
    public void WikiUrlRegex_MultipleUrls_FindsAll()
    {
        var content = "See https://en.wikipedia.org/wiki/Cat and https://en.wikipedia.org/wiki/Dog";

        var matches = WikipediaUnfurler.WikiUrlRegex.Matches(content);

        Assert.Equal(2, matches.Count);
        Assert.Equal("Cat", matches[0].Groups[2].Value);
        Assert.Equal("Dog", matches[1].Groups[2].Value);
    }

    // ── API URL Building ──────────────────────────────────────────────

    [Theory]
    [InlineData("en", "Artificial_intelligence", "https://en.wikipedia.org/api/rest_v1/page/summary/Artificial_intelligence")]
    [InlineData("de", "Berlin", "https://de.wikipedia.org/api/rest_v1/page/summary/Berlin")]
    public void BuildApiUrl_FormatsCorrectly(string lang, string title, string expected)
    {
        var result = WikipediaUnfurler.BuildApiUrl(lang, title);

        Assert.Equal(expected, result);
    }

    // ── JSON Parsing ──────────────────────────────────────────────────

    [Fact]
    public void ParseSummaryResponse_ValidArticle_ExtractsTitleAndSummary()
    {
        var json = """
        {
          "type": "standard",
          "title": "Artificial intelligence",
          "description": "Intelligence of machines",
          "extract": "Artificial intelligence (AI) is intelligence demonstrated by machines, as opposed to natural intelligence displayed by animals including humans."
        }
        """;

        var result = WikipediaUnfurler.ParseSummaryResponse(json, "https://en.wikipedia.org/wiki/Artificial_intelligence", "en", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("wikipedia", result.SourceType);
        Assert.Contains("Artificial intelligence", result.Text);
        Assert.Contains("Intelligence of machines", result.Text);
        Assert.Contains("intelligence demonstrated by machines", result.Text);
        Assert.Equal(string.Empty, result.Author);
    }

    [Fact]
    public void ParseSummaryResponse_NonEnglish_SetsLanguageSuffix()
    {
        var json = """
        {
          "type": "standard",
          "title": "Berlin",
          "extract": "Berlin ist die Hauptstadt und ein Land der Bundesrepublik Deutschland."
        }
        """;

        var result = WikipediaUnfurler.ParseSummaryResponse(json, "https://de.wikipedia.org/wiki/Berlin", "de", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("wikipedia-de", result.SourceType);
    }

    [Fact]
    public void ParseSummaryResponse_WithThumbnail_ExtractsImage()
    {
        var json = """
        {
          "type": "standard",
          "title": "Cat",
          "extract": "The cat is a domestic species of small carnivorous mammal.",
          "thumbnail": {
            "source": "https://upload.wikimedia.org/wikipedia/commons/thumb/cat.jpg",
            "width": 320,
            "height": 240
          }
        }
        """;

        var result = WikipediaUnfurler.ParseSummaryResponse(json, "https://en.wikipedia.org/wiki/Cat", "en", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Single(result.Images);
        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/thumb/cat.jpg", result.Images[0].Url.AbsoluteUri);
        Assert.Equal("wikipedia-thumbnail", result.Images[0].Source);
    }

    [Fact]
    public void ParseSummaryResponse_NoThumbnail_EmptyImages()
    {
        var json = """
        {
          "type": "standard",
          "title": "Obscure Topic",
          "extract": "This topic has no image."
        }
        """;

        var result = WikipediaUnfurler.ParseSummaryResponse(json, "https://en.wikipedia.org/wiki/Obscure_Topic", "en", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Empty(result.Images);
    }

    [Fact]
    public void ParseSummaryResponse_DisambiguationPage_StillReturnsContent()
    {
        var json = """
        {
          "type": "disambiguation",
          "title": "Mercury",
          "description": "Topics referred to by the same term",
          "extract": "Mercury may refer to: Mercury (planet), Mercury (element)."
        }
        """;

        var result = WikipediaUnfurler.ParseSummaryResponse(json, "https://en.wikipedia.org/wiki/Mercury", "en", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Contains("Mercury", result.Text);
    }

    [Fact]
    public void ParseSummaryResponse_EmptyContent_ReturnsNull()
    {
        var json = """
        {
          "type": "standard"
        }
        """;

        var result = WikipediaUnfurler.ParseSummaryResponse(json, "https://en.wikipedia.org/wiki/Empty", "en", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseSummaryResponse_InvalidJson_ReturnsNull()
    {
        var result = WikipediaUnfurler.ParseSummaryResponse("not json", "https://en.wikipedia.org/wiki/X", "en", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ParseSummaryResponse_LongExtract_Truncated()
    {
        var longExtract = new string('A', 5000);
        var json = $$"""
        {
          "type": "standard",
          "title": "Long Article",
          "extract": "{{longExtract}}"
        }
        """;

        var result = WikipediaUnfurler.ParseSummaryResponse(json, "https://en.wikipedia.org/wiki/Long_Article", "en", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.True(result.Text.Length <= WikipediaUnfurler.MaxContentLength + 10); // +10 for title and truncation marker
    }

    [Fact]
    public void ParseSummaryResponse_ValidUrl_SetsOriginalUrl()
    {
        var json = """
        {
          "type": "standard",
          "title": "Test",
          "extract": "Test content"
        }
        """;
        var originalUrl = "https://en.wikipedia.org/wiki/Test";

        var result = WikipediaUnfurler.ParseSummaryResponse(json, originalUrl, "en", DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal(new Uri(originalUrl), result.OriginalUrl);
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
    public async Task UnfurlAsync_NoWikipediaUrls_ReturnsEmpty()
    {
        var unfurler = CreateUnfurler();

        var result = await unfurler.UnfurlAsync("Just text https://google.com", DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static WikipediaUnfurler CreateUnfurler()
    {
        return new WikipediaUnfurler(
            new HttpClient(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WikipediaUnfurler>.Instance);
    }
}
