using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace DiscordSky.Tests;

public sealed class ImageGeneratorHelpersTests
{
    [Theory]
    [InlineData("gpt-image-2", "low", 0.006)]
    [InlineData("gpt-image-2", "medium", 0.05)]
    [InlineData("gpt-image-2", "high", 0.21)]
    public void ImageCost_MatchesPricingTable(string model, string quality, double expected)
    {
        Assert.Equal(expected, ImageCost.Estimate(model, quality), precision: 6);
    }

    [Fact]
    public void ImageCost_UnknownQuality_FallsBackWithinModel()
    {
        // Unknown quality must not be free; it should map to a sensible non-zero estimate.
        Assert.True(ImageCost.Estimate("gpt-image-2", "auto") > 0);
    }

    [Theory]
    [InlineData("gpt-image-1")]
    [InlineData("gpt-image-1-mini")]
    [InlineData("gpt-image-2-mini")]
    [InlineData("dall-e-3")]
    [InlineData("")]
    public void FromConfig_RejectsModelsBelowQualityFloor(string model)
    {
        var options = new ImageOptions { Model = model };

        var error = Assert.Throws<InvalidOperationException>(() => ImageRequestOptions.FromConfig(options));

        Assert.Contains("prohibited", error.Message);
    }

    [Theory]
    [InlineData("gpt-image-2")]
    [InlineData("gpt-image-2-2026-01-01")]
    [InlineData("gpt-image-3")]
    public void FromConfig_AcceptsV2OrNewerNonMiniModels(string model)
    {
        var resolved = ImageRequestOptions.FromConfig(new ImageOptions { Model = model });
        Assert.Equal(model, resolved.Model);
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

    [Fact]
    public async Task OpenAiGenerator_ProviderGuardBlocksBeforeNetworkCall()
    {
        var guard = new LlmProviderGuard(
            NullLogger<LlmProviderGuard>.Instance,
            options: new LlmProviderGuardOptions
            {
                HourlyUsdLimit = 0.10,
                DailyUsdLimit = 1.0,
                StatePath = Path.Combine(Path.GetTempPath(), $"image-guard-{Guid.NewGuid():N}.json"),
            });
        var generator = new OpenAIImageGenerator(
            new OpenAIClient("not-a-real-key"),
            guard,
            NullLogger<OpenAIImageGenerator>.Instance);

        var result = await generator.GenerateAsync(
            "Never reaches the network.",
            ImageRequestOptions.FromConfig(new ImageOptions()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ImageResult.ErrorRateLimited, result.Error);
    }
}
