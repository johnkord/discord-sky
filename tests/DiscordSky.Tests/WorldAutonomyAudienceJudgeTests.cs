using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyAudienceJudgeTests
{
    [Fact]
    public void Parse_ClampsIndependentScoresAndBoundsHooks()
    {
        var verdict = WorldAutonomyAudienceJudge.Parse("""
            {
              "conversation_worth": 1.4,
              "conversation_hook": "one two three four five six seven eight nine ten eleven twelve thirteen",
              "reaction_worth": "0.45",
              "action_worth": -0.2,
              "action_hook": "found a reusable department from this exact room opportunity",
              "confidence": 0.8
            }
            """);

        Assert.NotNull(verdict);
        Assert.Equal(1.0, verdict.ConversationWorth);
        Assert.Equal(12, verdict.ConversationHook.Split(' ').Length);
        Assert.Equal(0.45, verdict.ReactionWorth);
        Assert.Equal(0.0, verdict.ActionWorth);
        Assert.Equal(0.8, verdict.Confidence);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"conversation_worth\":0.5,\"reaction_worth\":0.2,\"confidence\":0.9}")]
    [InlineData("not json")]
    public void Parse_IncompleteOrMalformedVerdictReturnsNull(string value)
    {
        Assert.Null(WorldAutonomyAudienceJudge.Parse(value));
    }

    [Fact]
    public void Prompt_ExposesCategoriesWithoutNativeToolCatalog()
    {
        var prompt = WorldAutonomyAudienceJudge.BuildSystemPrompt("Robotnik from AOSTH", "gloating");

        Assert.Contains("channels/topics", prompt, StringComparison.Ordinal);
        Assert.Contains("messages/pins", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("create_text_channel", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("send_message", prompt, StringComparison.Ordinal);
    }
}