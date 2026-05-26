using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public class DiscordCdnExpirationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(0x67000000);

    [Fact]
    public void NonDiscordHost_ReturnsFalse()
    {
        var uri = new Uri("https://example.com/img.png?ex=00000000");
        Assert.False(ContextAggregator.IsExpiredDiscordCdnUrl(uri, Now));
    }

    [Fact]
    public void DiscordCdn_NoExParam_ReturnsFalse()
    {
        var uri = new Uri("https://cdn.discordapp.com/attachments/1/2/image.png");
        Assert.False(ContextAggregator.IsExpiredDiscordCdnUrl(uri, Now));
    }

    [Fact]
    public void DiscordCdn_ExpiredEx_ReturnsTrue()
    {
        // ex set well before Now
        var uri = new Uri("https://cdn.discordapp.com/attachments/1/2/image.png?ex=66000000&is=65fffff0&hs=abc");
        Assert.True(ContextAggregator.IsExpiredDiscordCdnUrl(uri, Now));
    }

    [Fact]
    public void DiscordCdn_FutureEx_ReturnsFalse()
    {
        // ex set well after Now
        var uri = new Uri("https://cdn.discordapp.com/attachments/1/2/image.png?ex=68000000&is=67fffff0&hs=abc");
        Assert.False(ContextAggregator.IsExpiredDiscordCdnUrl(uri, Now));
    }

    [Fact]
    public void MediaDiscordappNet_ExpiredEx_ReturnsTrue()
    {
        var uri = new Uri("https://media.discordapp.net/attachments/1/2/image.png?ex=66000000&is=65fffff0&hs=abc");
        Assert.True(ContextAggregator.IsExpiredDiscordCdnUrl(uri, Now));
    }

    [Fact]
    public void MalformedEx_ReturnsFalse()
    {
        var uri = new Uri("https://cdn.discordapp.com/attachments/1/2/image.png?ex=notahex");
        Assert.False(ContextAggregator.IsExpiredDiscordCdnUrl(uri, Now));
    }
}
