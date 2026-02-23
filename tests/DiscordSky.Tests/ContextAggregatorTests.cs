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
}
