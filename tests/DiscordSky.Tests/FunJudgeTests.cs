using DiscordSky.Bot.Memory.Analysis;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the pairwise LLM-judge core (fun_assessment_2026-06-25 P3). The LLM call is injected, so the
/// prompt, verdict parsing, ranking, and reaction-join are all validated without a real model.
/// </summary>
public class FunJudgeTests
{
    [Theory]
    [InlineData("A", 'A')]
    [InlineData("B", 'B')]
    [InlineData("B, funnier and more chaotic", 'B')]
    [InlineData("  A", 'A')]
    [InlineData("a.", 'A')]
    [InlineData("", 'A')]
    public void ParseChoice_ExtractsVerdict(string input, char expected)
        => Assert.Equal(expected, FunJudge.ParseChoice(input));

    [Fact]
    public void BuildComparisonPrompt_IncludesBothRepliesAndCriterion()
    {
        var prompt = FunJudge.BuildComparisonPrompt("ALPHA reply", "BETA reply");
        Assert.Contains("ALPHA reply", prompt);
        Assert.Contains("BETA reply", prompt);
        Assert.Contains("Robotnik", prompt);
        Assert.Contains("never a polite assistant", prompt);
    }

    [Fact]
    public void Rank_OrdersByPairwiseWins()
    {
        var replies = new List<string> { "dull line", "the BEST villain line", "mediocre line" };
        // Stub judge: whichever reply contains "BEST" wins; otherwise A.
        char Judge(string a, string b) => b.Contains("BEST") ? 'B' : 'A';

        var ranked = FunJudge.Rank(replies, Judge);

        Assert.Equal("the BEST villain line", ranked[0].Reply);
        Assert.Equal(1.0, ranked[0].WinRate, 3); // wins both of its comparisons
    }

    [Fact]
    public void ReactionCountFor_MatchesByExcerptPrefix()
    {
        var reply = "Applaud at once, peasant, before I rename this channel!";
        var excerpts = new List<string> { "Applaud at once", "totally different reply", "Applaud at once" };
        Assert.Equal(2, FunJudge.ReactionCountFor(reply, excerpts));
    }
}
