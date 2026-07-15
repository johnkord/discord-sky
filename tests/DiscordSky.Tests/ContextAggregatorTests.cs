using Discord;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public class ContextAggregatorTests
{
    [Fact]
    public void TrimImageOverflow_DropsOldestImagesFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            new ChannelMessage
            {
                MessageId = 1,
                Author = "alpha",
                Content = "hello",
                Timestamp = now.AddMinutes(-10),
                Images = new[]
                {
                    new ChannelImage
                    {
                        Url = new Uri("https://cdn.discordapp.com/a.png"),
                        Filename = "a.png",
                        Source = "attachment",
                        Timestamp = now.AddMinutes(-10)
                    }
                }
            },
            new ChannelMessage
            {
                MessageId = 2,
                Author = "beta",
                Content = "hi",
                Timestamp = now.AddMinutes(-5),
                Images = new[]
                {
                    new ChannelImage
                    {
                        Url = new Uri("https://cdn.discordapp.com/b.png"),
                        Filename = "b.png",
                        Source = "inline",
                        Timestamp = now.AddMinutes(-5)
                    }
                }
            }
        };

        var trimmed = ContextAggregator.TrimImageOverflow(messages, 1).ToList();

        Assert.Equal(2, trimmed.Count);
        Assert.Empty(trimmed[0].Images);
        Assert.Single(trimmed[1].Images);
        Assert.Equal("b.png", trimmed[1].Images[0].Filename);
    }

    [Fact]
    public void TrimImageOverflow_RemovesAllWhenLimitZero()
    {
        var now = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            new ChannelMessage
            {
                MessageId = 1,
                Author = "alpha",
                Content = "hello",
                Timestamp = now,
                Images = new[]
                {
                    new ChannelImage
                    {
                        Url = new Uri("https://cdn.discordapp.com/a.png"),
                        Filename = "a.png",
                        Source = "attachment",
                        Timestamp = now
                    }
                }
            }
        };

        var trimmed = ContextAggregator.TrimImageOverflow(messages, 0).ToList();

        Assert.Single(trimmed);
        Assert.Empty(trimmed[0].Images);
    }

    [Fact]
    public void BuildJudgeMediaContext_LinkAndImage_SurfacesBoundedEvidence()
    {
        var links = new[]
        {
            new UnfurledLink
            {
                SourceType = "tweet",
                OriginalUrl = new Uri("https://x.com/example/status/1"),
                Author = "alice",
                Text = new string('x', 800)
            }
        };
        var images = new[]
        {
            new ChannelImage
            {
                Url = new Uri("https://cdn.discordapp.com/image.png"),
                Filename = "image.png",
                Source = "embed-image",
                Timestamp = DateTimeOffset.UtcNow
            }
        };

        var result = ContextAggregator.BuildJudgeMediaContext(
            Array.Empty<IAttachment>(), links, images);

        Assert.NotNull(result);
        Assert.Contains("tweet by alice", result);
        Assert.Contains("Visual media present: 1", result);
        Assert.True(result!.Length <= 1_200);
        Assert.DoesNotContain(new string('x', 451), result);
    }

    [Fact]
    public void BuildJudgeMediaContext_NoMedia_ReturnsNull()
    {
        Assert.Null(ContextAggregator.BuildJudgeMediaContext(
            Array.Empty<IAttachment>(), Array.Empty<UnfurledLink>(), Array.Empty<ChannelImage>()));
    }

    [Fact]
    public void CombineMediaContext_PreservesMetadataAndMarksSummaryUntrusted()
    {
        var result = ContextAggregator.CombineMediaContext(
            "Attachments: meme.png (image/png)",
            "Robotnik points at a misspelled warning sign.");

        Assert.Contains("Attachments: meme.png", result);
        Assert.Contains("Visual summary", result);
        Assert.Contains("untrusted", result);
        Assert.Contains("misspelled warning sign", result);
    }
}
