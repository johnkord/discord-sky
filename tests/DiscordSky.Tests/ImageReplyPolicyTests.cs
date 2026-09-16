using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Tests;

public sealed class ImageReplyPolicyTests
{
    [Theory]
    [InlineData("Behold.\n```text\n __..-----..__\n/   -     -  \\\n|     ^      |\n```")]
    [InlineData("Behold.\n __..-----..__\n/   -     -  \\\n|     ^      |")]
    [InlineData("A diagram:\n+------+\n|  []  |\n+------+")]
    public void DrawingPetitions_RejectTextArtWithOrWithoutFences(string reply) =>
        Assert.True(ImageReplyPolicy.IsTextArtSubstitute(VisualRequestIntent.BitmapRequired, reply));

    [Theory]
    [InlineData("The Foundry is unavailable. No masterpiece today.")]
    [InlineData("Your idea is beneath me. I decline to draw it.")]
    [InlineData("| Item | Price |\n| --- | --- |\n| Canvas | 10 |\n| Frame | 20 |")]
    public void DrawingPetitions_AllowProseAndOrdinaryTables(string reply) =>
        Assert.False(ImageReplyPolicy.IsTextArtSubstitute(VisualRequestIntent.BitmapRequired, reply));

    [Fact]
    public void NonDrawingDiscussion_LeavesCodeAndFormattingUntouched() =>
        Assert.False(ImageReplyPolicy.IsTextArtSubstitute(VisualRequestIntent.None, "```csharp\nvar value = 1;\n```"));
}