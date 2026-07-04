using DiscordSky.Bot.Orchestration.Impulse;

namespace DiscordSky.Tests;

public class ImpulseJudgeTests
{
    // ── ParseWorth ──────────────────────────────────────────────────────

    [Fact]
    public void ParseWorth_ValidNumber_ReturnsWorthAndThought()
    {
        var v = ImpulseJudge.ParseWorth("{\"worth\":0.8,\"thought\":\"puncture that boast\"}");
        Assert.NotNull(v);
        Assert.Equal(0.8, v!.Worth, 3);
        Assert.Equal("puncture that boast", v.Thought);
    }

    [Fact]
    public void ParseWorth_StringNumber_Parses()
    {
        var v = ImpulseJudge.ParseWorth("{\"worth\":\"0.65\"}");
        Assert.NotNull(v);
        Assert.Equal(0.65, v!.Worth, 3);
        Assert.Equal(string.Empty, v.Thought);
    }

    [Fact]
    public void ParseWorth_IntegerWorth_Parses()
    {
        var v = ImpulseJudge.ParseWorth("{\"worth\":1}");
        Assert.NotNull(v);
        Assert.Equal(1.0, v!.Worth, 3);
    }

    [Fact]
    public void ParseWorth_AboveOne_ClampsToOne()
    {
        var v = ImpulseJudge.ParseWorth("{\"worth\":1.5}");
        Assert.NotNull(v);
        Assert.Equal(1.0, v!.Worth, 3);
    }

    [Fact]
    public void ParseWorth_BelowZero_ClampsToZero()
    {
        var v = ImpulseJudge.ParseWorth("{\"worth\":-0.3}");
        Assert.NotNull(v);
        Assert.Equal(0.0, v!.Worth, 3);
    }

    [Fact]
    public void ParseWorth_MissingWorth_ReturnsNull()
    {
        Assert.Null(ImpulseJudge.ParseWorth("{\"thought\":\"no score here\"}"));
    }

    [Fact]
    public void ParseWorth_NonNumericWorth_ReturnsNull()
    {
        Assert.Null(ImpulseJudge.ParseWorth("{\"worth\":true}"));
    }

    [Fact]
    public void ParseWorth_Malformed_ReturnsNull()
    {
        Assert.Null(ImpulseJudge.ParseWorth("not json at all"));
    }

    [Fact]
    public void ParseWorth_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(ImpulseJudge.ParseWorth(""));
        Assert.Null(ImpulseJudge.ParseWorth(null));
    }

    [Fact]
    public void ParseWorth_FencedJson_Parses()
    {
        var v = ImpulseJudge.ParseWorth("```json\n{\"worth\":0.3,\"thought\":\"meh\"}\n```");
        Assert.NotNull(v);
        Assert.Equal(0.3, v!.Worth, 3);
        Assert.Equal("meh", v.Thought);
    }

    [Fact]
    public void ParseWorth_ProseWrapped_ExtractsObject()
    {
        var v = ImpulseJudge.ParseWorth("Sure: {\"worth\":0.9} that is my call.");
        Assert.NotNull(v);
        Assert.Equal(0.9, v!.Worth, 3);
    }

    [Fact]
    public void ParseWorth_ThoughtOptional_DefaultsEmpty()
    {
        var v = ImpulseJudge.ParseWorth("{\"worth\":0.2}");
        Assert.NotNull(v);
        Assert.Equal(string.Empty, v!.Thought);
    }

    // ── BuildSystemPrompt ───────────────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_Robotnik_HasBioRubricAndUntrustedGuard()
    {
        var p = ImpulseJudge.BuildSystemPrompt("Robotnik from AOSTH", null);
        Assert.Contains("Robotnik", p);
        Assert.Contains("worth", p);
        Assert.Contains("spammy", p);
        Assert.Contains("untrusted", p);
    }

    [Fact]
    public void BuildSystemPrompt_OtherPersona_UsesNameNotRobotnikBio()
    {
        var p = ImpulseJudge.BuildSystemPrompt("a helpful robot", null);
        Assert.Contains("a helpful robot", p);
        Assert.DoesNotContain("Eggman", p);
    }

    [Fact]
    public void BuildSystemPrompt_Mood_IncludedWhenPresentOmittedWhenNull()
    {
        Assert.Contains("seething", ImpulseJudge.BuildSystemPrompt("Robotnik", "seething"));
        Assert.DoesNotContain("current mood is", ImpulseJudge.BuildSystemPrompt("Robotnik", null));
    }

    // ── BuildUserMessage ────────────────────────────────────────────────

    [Fact]
    public void BuildUserMessage_IncludesAuthorAndMessage_NoContextByDefault()
    {
        var m = ImpulseJudge.BuildUserMessage(new AmbientImpulseRequest("Robotnik", "curlyquote", "i love everyone here", null, null));
        Assert.Contains("curlyquote", m);
        Assert.Contains("i love everyone here", m);
        Assert.DoesNotContain("Context", m);
    }

    [Fact]
    public void BuildUserMessage_IncludesContextWhenProvided()
    {
        var m = ImpulseJudge.BuildUserMessage(new AmbientImpulseRequest("Robotnik", "bob", "same", "alice: my plan worked", null));
        Assert.Contains("Context", m);
        Assert.Contains("my plan worked", m);
    }

    [Fact]
    public void BuildUserMessage_TruncatesLongMessage()
    {
        var longMsg = new string('x', 900);
        var m = ImpulseJudge.BuildUserMessage(new AmbientImpulseRequest("Robotnik", "bob", longMsg, null, null));
        Assert.True(m.Length < 900);
    }
}
