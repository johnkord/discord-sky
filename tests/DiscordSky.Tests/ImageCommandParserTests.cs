using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Tests;

public sealed class ImageCommandParserTests
{
    [Fact]
    public void Options_AreRemovedFromThePrompt()
    {
        var result = ImageCommandParser.Parse("--model flare an imperial postage stamp --size=1536x864 --quality high --format webp --compression 80 --previews 2 --transparent");
        Assert.Equal("an imperial postage stamp", result.Prompt);
        Assert.Equal("flare", result.Settings.Model);
        Assert.Equal("1536x864", result.Settings.Size);
        Assert.Equal("high", result.Settings.Quality);
        Assert.Equal("webp", result.Settings.OutputFormat);
        Assert.Equal("transparent", result.Settings.Background);
        Assert.Equal(80, result.Settings.OutputCompression);
        Assert.Equal(2, result.Settings.PartialImages);
    }

    [Theory]
    [InlineData("--size")]
    [InlineData("--previews nope")]
    [InlineData("--unknown value")]
    public void InvalidOptions_AreRejected(string request) =>
        Assert.Throws<ArgumentException>(() => ImageCommandParser.Parse(request));

    [Fact]
    public void EditCommands_PreserveExplicitAction()
    {
        Assert.Null(ImageCommandParser.Parse("make a picture of a tower").Settings.Action);
        Assert.Equal("edit", ImageCommandParser.Parse("change the background to green").Settings.Action);
        Assert.Equal("generate", ImageCommandParser.Parse("--generate change the background to green").Settings.Action);
        Assert.Equal("an image --with literal text", ImageCommandParser.Parse("an image -- --with literal text").Prompt);
    }

    [Theory]
    [InlineData("change the background to green")]
    [InlineData("make it transparent")]
    [InlineData("remove the hat")]
    [InlineData("upscale this")]
    public void FollowUpEdits_RequireImageContextForRouting(string request)
    {
        Assert.Equal(VisualRequestIntent.BitmapRequired, ImageIntentDetector.Classify(request, true));
        Assert.Equal(VisualRequestIntent.None, ImageIntentDetector.Classify(request, false));
    }
}