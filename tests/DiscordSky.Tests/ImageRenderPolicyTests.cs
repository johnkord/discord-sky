using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Tests;

public sealed class ImageRenderPolicyTests
{
    [Theory]
    [InlineData("1024x1024")]
    [InlineData("1536x864")]
    [InlineData("3840x2160")]
    [InlineData("2160x3840")]
    [InlineData("auto")]
    public void CustomSizes_AcceptDocumentedDimensions(string size) =>
        Assert.Equal(size, ImageRenderPolicy.NormalizeSize(size));

    [Theory]
    [InlineData("1025x1024")]
    [InlineData("512x512")]
    [InlineData("3840x3840")]
    [InlineData("4096x1024")]
    [InlineData("3072x768")]
    [InlineData("banana")]
    public void CustomSizes_RejectInvalidDimensions(string size) =>
        Assert.Throws<ArgumentException>(() => ImageRenderPolicy.NormalizeSize(size));

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void ExtendedQuality_RequiresNewModelAndPermission(string quality)
    {
        Assert.Throws<ArgumentException>(() => ImageRequestOptions.FromConfig(
            new ImageOptions { AllowHighQuality = true }, requested: new(Quality: quality)));
        var permitted = ImageRequestOptions.FromConfig(new ImageOptions { AllowHighQuality = true },
            requested: new(Model: "flare", Quality: quality));
        Assert.Equal(quality, permitted.Quality);
        var capped = ImageRequestOptions.FromConfig(new ImageOptions(), requested: new(Model: "flare", Quality: quality));
        Assert.Equal("medium", capped.Quality);
    }

    [Fact]
    public void Transparency_SelectsLosslessFormatUnlessExplicitlyIncompatible()
    {
        var resolved = ImageRequestOptions.FromConfig(new ImageOptions(), requested: new(Background: "transparent"));
        Assert.Equal("png", resolved.OutputFormat);
        Assert.Throws<ArgumentException>(() => ImageRequestOptions.FromConfig(new ImageOptions(),
            requested: new(OutputFormat: "jpeg", Background: "transparent")));
    }

    [Fact]
    public void Edit_SelectsSunburstAndRequiresReference()
    {
        Assert.Equal(ImageRenderPolicy.Sunburst, ImageRequestOptions.FromConfig(new ImageOptions(), editing: true).Model);
        Assert.Equal("edit", ImageRenderPolicy.ResolveAction("auto", true));
        Assert.Equal("generate", ImageRenderPolicy.ResolveAction("generate", true));
        Assert.Throws<ArgumentException>(() => ImageRenderPolicy.ResolveAction("edit", false));
    }

    [Fact]
    public void CompressionAndPreviews_AreValidated()
    {
        var resolved = ImageRequestOptions.FromConfig(new ImageOptions(),
            requested: new(OutputFormat: "webp", OutputCompression: 70, PartialImages: 3));
        Assert.Equal(70, resolved.OutputCompression);
        Assert.Equal(3, resolved.PartialImages);
        Assert.Throws<ArgumentException>(() => ImageRequestOptions.FromConfig(new ImageOptions(), requested: new(PartialImages: 4)));
        Assert.Throws<ArgumentException>(() => ImageRequestOptions.FromConfig(new ImageOptions(),
            requested: new(OutputFormat: "png", OutputCompression: 50)));
    }
}