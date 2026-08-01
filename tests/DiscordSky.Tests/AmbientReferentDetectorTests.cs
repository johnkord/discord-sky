using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public sealed class AmbientReferentDetectorTests
{
    [Theory]
    [InlineData("what is that?")]
    [InlineData("look at that")]
    [InlineData("this")]
    [InlineData("same here")]
    public void Detect_AmbiguousShortReference_RequiresReferent(string text)
    {
        Assert.True(AmbientReferentDetector.Detect(text, false, false).IsRequired);
    }

    [Theory]
    [InlineData("that rules", false)]
    [InlineData("it is raining", false)]
    [InlineData("she literally said 'look at that' yesterday", false)]
    [InlineData("The meteor landed beside the tower.", false)]
    [InlineData("look at this", true)]
    public void Detect_SelfContainedCases_DoNotRequireReferent(string text, bool hasMedia)
    {
        Assert.False(AmbientReferentDetector.Detect(text, false, hasMedia).IsRequired);
    }

    [Fact]
    public void Detect_ExplicitReplyNeverRequiresInference()
    {
        var result = AmbientReferentDetector.Detect("what is that?", true, false);

        Assert.False(result.IsRequired);
        Assert.Equal("explicit_reply", result.ReasonCode);
    }
}