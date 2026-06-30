using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Tests;

public sealed class ImageTriggersTests
{
    [Theory]
    [InlineData("draw me as a knight")]
    [InlineData("hey can you draw us riding into battle")]
    [InlineData("make a picture of my cat")]
    [InlineData("make me an image of the squad")]
    [InlineData("paint a portrait of us")]
    [InlineData("show me your latest doomsday machine")]
    [InlineData("a poster of me would be amazing")]
    [InlineData("render that as a propaganda poster")]
    public void Intent_PositiveCases(string text)
    {
        Assert.True(ImageIntentDetector.LooksLikeImageRequest(text));
    }

    [Theory]
    [InlineData("the match ended in a draw")]
    [InlineData("i need to draw money from the bank")]
    [InlineData("what's up everyone")]
    [InlineData("lol that was wild")]
    [InlineData("")]
    [InlineData(null)]
    public void Intent_NegativeCases(string? text)
    {
        Assert.False(ImageIntentDetector.LooksLikeImageRequest(text));
    }

    [Fact]
    public void FromConfig_Commissioned_UsesPrimaryModelAndQuality()
    {
        var o = new ImageOptions
        {
            Model = "gpt-image-2",
            Quality = "medium",
            SpontaneousModel = "gpt-image-1-mini",
            SpontaneousQuality = "low",
        };

        var r = ImageRequestOptions.FromConfig(o, ImageTier.Commissioned);

        Assert.Equal("gpt-image-2", r.Model);
        Assert.Equal("medium", r.Quality);
    }

    [Fact]
    public void FromConfig_Spontaneous_UsesSpontaneousModelAndQuality()
    {
        var o = new ImageOptions
        {
            Model = "gpt-image-2",
            Quality = "medium",
            SpontaneousModel = "gpt-image-1-mini",
            SpontaneousQuality = "low",
        };

        var r = ImageRequestOptions.FromConfig(o, ImageTier.Spontaneous);

        Assert.Equal("gpt-image-1-mini", r.Model);
        Assert.Equal("low", r.Quality);
    }

    [Fact]
    public void FromConfig_DefaultsToCommissioned()
    {
        var o = new ImageOptions { Model = "gpt-image-2", SpontaneousModel = "gpt-image-1-mini" };
        Assert.Equal("gpt-image-2", ImageRequestOptions.FromConfig(o).Model);
    }

    [Fact]
    public void FromConfig_ClampsHigh_OnBothTiers_WhenNotAllowed()
    {
        var o = new ImageOptions { Quality = "high", SpontaneousQuality = "high", AllowHighQuality = false };
        Assert.Equal("medium", ImageRequestOptions.FromConfig(o, ImageTier.Commissioned).Quality);
        Assert.Equal("medium", ImageRequestOptions.FromConfig(o, ImageTier.Spontaneous).Quality);
    }
}
