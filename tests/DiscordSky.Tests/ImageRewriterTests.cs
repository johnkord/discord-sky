using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Tests;

public sealed class ImageRewriterTests
{
    [Fact]
    public void Parse_ValidDrawing_ReturnsBarePromptAndCaption()
    {
        var json = """
        { "refuse": false, "refusal_text": "", "image_prompt": "a colossal golden statue of my own glorious face", "caption": "Behold my magnificence, peasant!" }
        """;

        var result = ImageRewriter.Parse(json);

        Assert.False(result.Refuse);
        Assert.Equal("Behold my magnificence, peasant!", result.Caption);
        // Parse returns the bare, persona-vetted subject; the style suffix is applied later in ImageToolService.
        Assert.Equal("a colossal golden statue of my own glorious face", result.ImagePrompt);
    }

    [Fact]
    public void Parse_DoesNotAppendStyleSuffix()
    {
        var json = """{ "refuse": false, "image_prompt": "an egg-shaped war machine", "caption": "Tremble." }""";

        var result = ImageRewriter.Parse(json);

        Assert.Equal("an egg-shaped war machine", result.ImagePrompt);
        Assert.DoesNotContain(ImageToolService.StyleSuffix, result.ImagePrompt!);
    }

    [Fact]
    public void Parse_Refusal_ReturnsRefusalText()
    {
        var json = """{ "refuse": true, "refusal_text": "I shall NEVER draw that drivel!", "image_prompt": "", "caption": "" }""";

        var result = ImageRewriter.Parse(json);

        Assert.True(result.Refuse);
        Assert.Equal("I shall NEVER draw that drivel!", result.RefusalText);
        Assert.Null(result.ImagePrompt);
    }

    [Fact]
    public void Parse_RefuseFalseButEmptyPrompt_IsTreatedAsRefusal()
    {
        var json = """{ "refuse": false, "image_prompt": "", "caption": "nothing" }""";

        var result = ImageRewriter.Parse(json);

        Assert.True(result.Refuse);
        Assert.Null(result.ImagePrompt);
    }

    [Fact]
    public void Parse_Malformed_FailsSafeToRefusal()
    {
        var result = ImageRewriter.Parse("this is not json at all");

        Assert.True(result.Refuse);
        Assert.Null(result.RefusalText);
        Assert.Null(result.ImagePrompt);
    }

    [Fact]
    public void Parse_Null_FailsSafeToRefusal()
    {
        var result = ImageRewriter.Parse(null);

        Assert.True(result.Refuse);
    }

    [Fact]
    public void Parse_CodeFenceWrapped_StillParses()
    {
        var json = "```json\n{ \"refuse\": false, \"image_prompt\": \"my face on Mount Mobius\", \"caption\": \"A fitting tribute.\" }\n```";

        var result = ImageRewriter.Parse(json);

        Assert.False(result.Refuse);
        Assert.StartsWith("my face on Mount Mobius", result.ImagePrompt);
        Assert.Equal("A fitting tribute.", result.Caption);
    }

    [Fact]
    public void Parse_RefuseAsString_IsHonored()
    {
        var json = """{ "refuse": "true", "refusal_text": "No.", "image_prompt": "x", "caption": "y" }""";

        var result = ImageRewriter.Parse(json);

        Assert.True(result.Refuse);
    }
}
