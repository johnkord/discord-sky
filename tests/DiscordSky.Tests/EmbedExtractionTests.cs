using Discord;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public class EmbedExtractionTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ── ExtractEmbedsAsUnfurledLinks ─────────────────────────────────────

    [Fact]
    public void ExtractEmbeds_NullEmbeds_ReturnsEmpty()
    {
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(null!, TestTimestamp);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractEmbeds_EmptyEmbeds_ReturnsEmpty()
    {
        var embeds = Array.Empty<IEmbed>();
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractEmbeds_RedditEmbed_ExtractsContentCorrectly()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Draw Steel! Combat question")
            .WithDescription("Has anyone figured out the flanking rules?")
            .WithUrl("https://www.reddit.com/r/drawsteel/comments/1rcu0rx/draw_steel_combat_question/")
            .WithAuthor("u/testuser")
            .WithFooter("r/drawsteel · 42 points · 15 comments")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        var link = result[0];
        Assert.Equal("reddit", link.SourceType);
        Assert.Contains("Draw Steel! Combat question", link.Text);
        Assert.Contains("flanking rules", link.Text);
        Assert.Equal("u/testuser · r/drawsteel · 42 points · 15 comments", link.Author);
        Assert.Equal("https://www.reddit.com/r/drawsteel/comments/1rcu0rx/draw_steel_combat_question/", link.OriginalUrl.AbsoluteUri);
    }

    [Fact]
    public void ExtractEmbeds_TwitterEmbed_DetectsSourceType()
    {
        var embed = new EmbedBuilder()
            .WithTitle("A tweet")
            .WithDescription("Tweet content here")
            .WithUrl("https://twitter.com/user/status/123")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("tweet", result[0].SourceType);
    }

    [Fact]
    public void ExtractEmbeds_XDotComEmbed_DetectsAsTweet()
    {
        var embed = new EmbedBuilder()
            .WithTitle("A post on X")
            .WithDescription("Content from X")
            .WithUrl("https://x.com/user/status/456")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("tweet", result[0].SourceType);
    }

    [Fact]
    public void ExtractEmbeds_WikipediaEmbed_DetectsSourceType()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Unit testing")
            .WithDescription("Software testing method")
            .WithUrl("https://en.wikipedia.org/wiki/Unit_testing")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("wikipedia", result[0].SourceType);
    }

    [Fact]
    public void ExtractEmbeds_HackerNewsEmbed_DetectsSourceType()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Show HN: Something cool")
            .WithDescription("A tech article")
            .WithUrl("https://news.ycombinator.com/item?id=12345")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("hackernews", result[0].SourceType);
    }

    [Fact]
    public void ExtractEmbeds_GenericEmbed_ReturnsEmbedSourceType()
    {
        var embed = new EmbedBuilder()
            .WithTitle("An article")
            .WithDescription("Article body text")
            .WithUrl("https://example.com/article/123")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("embed", result[0].SourceType);
    }

    [Fact]
    public void ExtractEmbeds_NoUrl_Skipped()
    {
        var embed = new EmbedBuilder()
            .WithTitle("No URL embed")
            .WithDescription("Description without a URL")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractEmbeds_NoTitleOrDescription_Skipped()
    {
        var embed = new EmbedBuilder()
            .WithUrl("https://www.reddit.com/r/test/comments/abc123/test/")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractEmbeds_TitleOnly_ExtractsCorrectly()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Reddit post title only")
            .WithUrl("https://www.reddit.com/r/test/comments/abc123/test/")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("Reddit post title only", result[0].Text);
    }

    [Fact]
    public void ExtractEmbeds_DescriptionOnly_ExtractsCorrectly()
    {
        var embed = new EmbedBuilder()
            .WithDescription("Only a description, no title")
            .WithUrl("https://www.reddit.com/r/test/comments/abc123/test/")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("Only a description, no title", result[0].Text);
    }

    [Fact]
    public void ExtractEmbeds_WithImage_ExtractsImage()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Post with image")
            .WithUrl("https://www.reddit.com/r/pics/comments/abc123/cool_photo/")
            .WithImageUrl("https://i.redd.it/example.jpg")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Single(result[0].Images);
        Assert.Equal("embed-image", result[0].Images[0].Source);
        Assert.Equal("https://i.redd.it/example.jpg", result[0].Images[0].Url.AbsoluteUri);
    }

    [Fact]
    public void ExtractEmbeds_WithThumbnailOnly_ExtractsThumbnail()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Post with thumbnail")
            .WithUrl("https://www.reddit.com/r/test/comments/abc123/test/")
            .WithThumbnailUrl("https://i.redd.it/thumb.jpg")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Single(result[0].Images);
        Assert.Equal("embed-thumbnail", result[0].Images[0].Source);
    }

    [Fact]
    public void ExtractEmbeds_WithImageAndThumbnail_PrefersImage()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Post")
            .WithUrl("https://www.reddit.com/r/pics/comments/abc123/photo/")
            .WithImageUrl("https://i.redd.it/full.jpg")
            .WithThumbnailUrl("https://i.redd.it/thumb.jpg")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Single(result[0].Images);
        Assert.Equal("https://i.redd.it/full.jpg", result[0].Images[0].Url.AbsoluteUri);
    }

    [Fact]
    public void ExtractEmbeds_AuthorWithFooter_CombinesIntoAuthorString()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Post title")
            .WithUrl("https://www.reddit.com/r/programming/comments/abc123/post/")
            .WithAuthor("u/developer")
            .WithFooter("r/programming · 100 points")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("u/developer · r/programming · 100 points", result[0].Author);
    }

    [Fact]
    public void ExtractEmbeds_FooterOnly_UsesAsAuthor()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Post title")
            .WithUrl("https://www.reddit.com/r/programming/comments/abc123/post/")
            .WithFooter("r/programming")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("r/programming", result[0].Author);
    }

    [Fact]
    public void ExtractEmbeds_MultipleEmbeds_ExtractsAll()
    {
        var embed1 = new EmbedBuilder()
            .WithTitle("Reddit post")
            .WithUrl("https://www.reddit.com/r/test/comments/abc/post1/")
            .Build();
        var embed2 = new EmbedBuilder()
            .WithTitle("Wikipedia article")
            .WithUrl("https://en.wikipedia.org/wiki/Testing")
            .Build();

        var embeds = new IEmbed[] { embed1, embed2 };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Equal(2, result.Count);
        Assert.Equal("reddit", result[0].SourceType);
        Assert.Equal("wikipedia", result[1].SourceType);
    }

    [Fact]
    public void ExtractEmbeds_DuplicateUrls_DeduplicatesCorrectly()
    {
        var embed1 = new EmbedBuilder()
            .WithTitle("First embed")
            .WithUrl("https://www.reddit.com/r/test/comments/abc/post/")
            .Build();
        var embed2 = new EmbedBuilder()
            .WithTitle("Duplicate embed")
            .WithUrl("https://www.reddit.com/r/test/comments/abc/post/")
            .Build();

        var embeds = new IEmbed[] { embed1, embed2 };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Contains("First embed", result[0].Text);
    }

    [Fact]
    public void ExtractEmbeds_ReddItShortUrl_DetectedAsReddit()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Short link post")
            .WithUrl("https://redd.it/abc123")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, TestTimestamp);

        Assert.Single(result);
        Assert.Equal("reddit", result[0].SourceType);
    }

    [Fact]
    public void ExtractEmbeds_ImageTimestamp_MatchesMessageTimestamp()
    {
        var ts = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var embed = new EmbedBuilder()
            .WithTitle("Post")
            .WithUrl("https://www.reddit.com/r/pics/comments/abc/photo/")
            .WithImageUrl("https://i.redd.it/image.png")
            .Build();

        var embeds = new IEmbed[] { embed };
        var result = ContextAggregator.ExtractEmbedsAsUnfurledLinks(embeds, ts);

        Assert.Single(result);
        Assert.Equal(ts, result[0].Images[0].Timestamp);
    }

    // ── MergeUnfurledLinks ───────────────────────────────────────────────

    [Fact]
    public void Merge_BothEmpty_ReturnsEmpty()
    {
        var result = ContextAggregator.MergeUnfurledLinks(
            Array.Empty<UnfurledLink>(),
            Array.Empty<UnfurledLink>());

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_OnlyHttpLinks_ReturnsHttp()
    {
        var http = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = new Uri("https://www.reddit.com/r/test/comments/abc/post/"),
                Text = "HTTP content",
                Author = "u/author"
            }
        };

        var result = ContextAggregator.MergeUnfurledLinks(http, Array.Empty<UnfurledLink>());

        Assert.Single(result);
        Assert.Equal("HTTP content", result[0].Text);
    }

    [Fact]
    public void Merge_OnlyEmbedLinks_ReturnsEmbeds()
    {
        var embeds = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = new Uri("https://www.reddit.com/r/test/comments/abc/post/"),
                Text = "Embed content",
                Author = "u/author"
            }
        };

        var result = ContextAggregator.MergeUnfurledLinks(Array.Empty<UnfurledLink>(), embeds);

        Assert.Single(result);
        Assert.Equal("Embed content", result[0].Text);
    }

    [Fact]
    public void Merge_HttpTakesPriorityForSameUrl()
    {
        var redditUrl = new Uri("https://www.reddit.com/r/test/comments/abc/post/");
        var http = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = redditUrl,
                Text = "HTTP full content with comments",
                Author = "u/author in r/test"
            }
        };
        var embeds = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = redditUrl,
                Text = "Embed truncated content",
                Author = "u/author"
            }
        };

        var result = ContextAggregator.MergeUnfurledLinks(http, embeds);

        Assert.Single(result);
        Assert.Equal("HTTP full content with comments", result[0].Text);
    }

    [Fact]
    public void Merge_DifferentUrls_IncludesBoth()
    {
        var http = new[]
        {
            new UnfurledLink
            {
                SourceType = "hackernews",
                OriginalUrl = new Uri("https://news.ycombinator.com/item?id=123"),
                Text = "HN article"
            }
        };
        var embeds = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = new Uri("https://www.reddit.com/r/test/comments/abc/post/"),
                Text = "Reddit post from embed"
            }
        };

        var result = ContextAggregator.MergeUnfurledLinks(http, embeds);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, l => l.SourceType == "hackernews");
        Assert.Contains(result, l => l.SourceType == "reddit");
    }

    [Fact]
    public void Merge_HttpFailsForReddit_EmbedFillsGap()
    {
        // Simulates the AKS scenario: HTTP unfurler returns nothing for Reddit (403),
        // but embed data is available
        var wikiUrl = new Uri("https://en.wikipedia.org/wiki/Testing");
        var redditUrl = new Uri("https://www.reddit.com/r/drawsteel/comments/1rcu0rx/post/");

        var http = new[]
        {
            new UnfurledLink
            {
                SourceType = "wikipedia",
                OriginalUrl = wikiUrl,
                Text = "Wikipedia content"
            }
            // Note: no Reddit link from HTTP (it returned 403)
        };
        var embeds = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = redditUrl,
                Text = "Reddit content from Discord embed"
            }
        };

        var result = ContextAggregator.MergeUnfurledLinks(http, embeds);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, l => l.SourceType == "wikipedia" && l.Text == "Wikipedia content");
        Assert.Contains(result, l => l.SourceType == "reddit" && l.Text == "Reddit content from Discord embed");
    }

    [Fact]
    public void Merge_CaseInsensitiveUrlComparison()
    {
        var http = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = new Uri("https://www.reddit.com/r/Test/comments/ABC/Post/"),
                Text = "HTTP"
            }
        };
        var embeds = new[]
        {
            new UnfurledLink
            {
                SourceType = "reddit",
                OriginalUrl = new Uri("https://www.reddit.com/r/Test/comments/ABC/Post/"),
                Text = "Embed"
            }
        };

        var result = ContextAggregator.MergeUnfurledLinks(http, embeds);

        Assert.Single(result);
        Assert.Equal("HTTP", result[0].Text);
    }
}
