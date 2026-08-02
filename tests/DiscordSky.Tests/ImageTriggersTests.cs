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
    [InlineData("draw something about today's discussion")]
    [InlineData("generate an image of the new department")]
    [InlineData("make me a photo of the throne")]
    [InlineData("give me an image of that")]
    [InlineData("photograph of the new department")]
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

    [Theory]
    [InlineData("make an image of my cat", VisualRequestIntent.BitmapRequired)]
    [InlineData("generate a photo of the department", VisualRequestIntent.BitmapRequired)]
    [InlineData("give me an image of the department", VisualRequestIntent.BitmapRequired)]
    [InlineData("photograph of this disaster", VisualRequestIntent.BitmapRequired)]
    [InlineData("draw something about today", VisualRequestIntent.MediumChoice)]
    [InlineData("paint a portrait of us", VisualRequestIntent.MediumChoice)]
    [InlineData("the match was a draw", VisualRequestIntent.None)]
    public void Classify_DistinguishesRequiredBitmapFromMediumChoice(
        string text,
        VisualRequestIntent expected)
    {
        Assert.Equal(expected, ImageIntentDetector.Classify(text));
    }

    [Fact]
    public void FromConfig_Commissioned_UsesPrimaryModelAndQuality()
    {
        var o = new ImageOptions
        {
            Model = "gpt-image-2",
            Quality = "medium",
        };

        var r = ImageRequestOptions.FromConfig(o, ImageTier.Commissioned);

        Assert.Equal("gpt-image-2", r.Model);
        Assert.Equal("medium", r.Quality);
    }

    [Fact]
    public void FromConfig_Spontaneous_NeverDowngradesModelOrQuality()
    {
        var o = new ImageOptions
        {
            Model = "gpt-image-2",
            Quality = "medium",
        };

        var r = ImageRequestOptions.FromConfig(o, ImageTier.Spontaneous);

        Assert.Equal("gpt-image-2", r.Model);
        Assert.Equal("medium", r.Quality);
    }

    [Fact]
    public void FromConfig_DefaultsToCommissioned()
    {
        var o = new ImageOptions { Model = "gpt-image-2" };
        Assert.Equal("gpt-image-2", ImageRequestOptions.FromConfig(o).Model);
    }

    [Fact]
    public void FromConfig_ClampsHigh_OnBothTiers_WhenNotAllowed()
    {
        var o = new ImageOptions { Quality = "high", AllowHighQuality = false };
        Assert.Equal("medium", ImageRequestOptions.FromConfig(o, ImageTier.Commissioned).Quality);
        Assert.Equal("medium", ImageRequestOptions.FromConfig(o, ImageTier.Spontaneous).Quality);
    }
}
