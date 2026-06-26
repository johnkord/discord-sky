using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Tests;

public sealed class ImageGeneratorHelpersTests
{
    [Theory]
    [InlineData("gpt-image-1-mini", "low", 0.005)]
    [InlineData("gpt-image-1-mini", "medium", 0.011)]
    [InlineData("gpt-image-1-mini", "high", 0.036)]
    [InlineData("gpt-image-2", "low", 0.006)]
    [InlineData("gpt-image-2", "medium", 0.05)]
    [InlineData("gpt-image-2", "high", 0.21)]
    [InlineData("gpt-image-1", "low", 0.011)]
    [InlineData("gpt-image-1", "medium", 0.042)]
    [InlineData("gpt-image-1", "high", 0.167)]
    public void ImageCost_MatchesPricingTable(string model, string quality, double expected)
    {
        Assert.Equal(expected, ImageCost.Estimate(model, quality), precision: 6);
    }

    [Fact]
    public void ImageCost_UnknownQuality_FallsBackWithinModel()
    {
        // Unknown quality must not be free; it should map to a sensible non-zero estimate.
        Assert.True(ImageCost.Estimate("gpt-image-1-mini", "auto") > 0);
        Assert.True(ImageCost.Estimate("gpt-image-2", "auto") > 0);
    }

    [Fact]
    public void FromConfig_ClampsHighToMedium_WhenNotAllowed()
    {
        var options = new ImageOptions { Quality = "high", AllowHighQuality = false };

        var resolved = ImageRequestOptions.FromConfig(options);

        Assert.Equal("medium", resolved.Quality);
    }

    [Fact]
    public void FromConfig_KeepsHigh_WhenAllowed()
    {
        var options = new ImageOptions { Quality = "high", AllowHighQuality = true };

        var resolved = ImageRequestOptions.FromConfig(options);

        Assert.Equal("high", resolved.Quality);
    }

    [Fact]
    public void FromConfig_PassesThroughOtherValues()
    {
        var options = new ImageOptions
        {
            Model = "gpt-image-2",
            Size = "1536x1024",
            Quality = "low",
            OutputFormat = "png",
            Moderation = "low",
        };

        var resolved = ImageRequestOptions.FromConfig(options);

        Assert.Equal("gpt-image-2", resolved.Model);
        Assert.Equal("1536x1024", resolved.Size);
        Assert.Equal("low", resolved.Quality);
        Assert.Equal("png", resolved.OutputFormat);
        Assert.Equal("low", resolved.Moderation);
    }

    [Theory]
    [InlineData("1024x1024", "1024x1024")]
    [InlineData("1536x1024", "1536x1024")]
    [InlineData("1024x1536", "1024x1536")]
    [InlineData(" 1024 x 1024 ", "1024x1024")]
    [InlineData("garbage", "1024x1024")]
    [InlineData("", "1024x1024")]
    public void ParseSize_ParsesOrFallsBackToSquare(string input, string expected)
    {
        Assert.Equal(expected, OpenAIImageGenerator.ParseSize(input).ToString());
    }

    [Theory]
    [InlineData("jpg", "jpeg")]
    [InlineData("jpeg", "jpeg")]
    [InlineData("JPEG", "jpeg")]
    [InlineData("png", "png")]
    [InlineData("webp", "webp")]
    public void NormalizeFormat_MapsJpgAliasAndLowercases(string input, string expected)
    {
        Assert.Equal(expected, OpenAIImageGenerator.NormalizeFormat(input));
    }

    [Theory]
    [InlineData("jpeg", "jpg")]
    [InlineData("jpg", "jpg")]
    [InlineData("png", "png")]
    [InlineData("webp", "webp")]
    public void ExtensionFor_MapsToFileExtension(string input, string expected)
    {
        Assert.Equal(expected, OpenAIImageGenerator.ExtensionFor(input));
    }

    [Fact]
    public void NoOpGenerator_IsDisabledAndFails()
    {
        var gen = new NoOpImageGenerator();
        Assert.False(gen.IsEnabled);

        var result = gen.GenerateAsync("x", ImageRequestOptions.FromConfig(new ImageOptions()), CancellationToken.None).Result;
        Assert.False(result.Success);
        Assert.Equal(ImageResult.ErrorDisabled, result.Error);
    }
}
