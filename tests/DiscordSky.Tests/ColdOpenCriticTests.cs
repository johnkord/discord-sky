using DiscordSky.Bot.Orchestration.Impulse;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the cold-open critic: the skeptical second pass that audits a drafted cold open for CHECKABLE flaws
/// (inaccuracy, detachment, generic framing) and scores its own postability, so the service can MIN-combine it
/// with the composer's self-score and drag an over-scored miss under the bar. Parser and prompt-builder are pure
/// and public, so they are unit-testable without an LLM.
/// </summary>
public class ColdOpenCriticTests
{
    // ── ParseCritique ───────────────────────────────────────────────────

    [Fact]
    public void ParseCritique_Valid_ReturnsWorthAndFlaw()
    {
        var c = ColdOpenCritic.ParseCritique("{\"worth\":0.2,\"flaw\":\"invented a scam archive\"}");
        Assert.NotNull(c);
        Assert.Equal(0.2, c!.Worth, 3);
        Assert.Equal("invented a scam archive", c.Flaw);
    }

    [Fact]
    public void ParseCritique_MissingWorth_ReturnsNull()
    {
        Assert.Null(ColdOpenCritic.ParseCritique("{\"flaw\":\"generic frame\"}"));
    }

    [Fact]
    public void ParseCritique_ClampsAndParsesStringWorth()
    {
        Assert.Equal(1.0, ColdOpenCritic.ParseCritique("{\"worth\":2}")!.Worth, 3);
        Assert.Equal(0.5, ColdOpenCritic.ParseCritique("{\"worth\":\"0.5\"}")!.Worth, 3);
    }

    [Fact]
    public void ParseCritique_FlawOptional_DefaultsEmpty()
    {
        Assert.Equal(string.Empty, ColdOpenCritic.ParseCritique("{\"worth\":0.9}")!.Flaw);
    }

    [Fact]
    public void ParseCritique_FencedJson_Parses()
    {
        var c = ColdOpenCritic.ParseCritique("```json\n{\"worth\":0.85,\"flaw\":\"clean\"}\n```");
        Assert.NotNull(c);
        Assert.Equal("clean", c!.Flaw);
    }

    [Fact]
    public void ParseCritique_Malformed_ReturnsNull()
    {
        Assert.Null(ColdOpenCritic.ParseCritique("not json"));
        Assert.Null(ColdOpenCritic.ParseCritique(null));
    }

    // ── BuildSystemPrompt ───────────────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_Robotnik_IsAnAuditorOfCheckableFlaws()
    {
        var p = ColdOpenCritic.BuildSystemPrompt("Robotnik from AOSTH");
        Assert.Contains("Robotnik", p);
        Assert.Contains("auditing", p);
        Assert.Contains("INACCURACY", p);
        Assert.Contains("untrusted", p);
        // It must NOT be scoring raw humor; that is the whole point of the second pass.
        Assert.Contains("NOT scoring raw humor", p);
    }

    [Fact]
    public void BuildSystemPrompt_OtherPersona_NoRobotnikLore()
    {
        var p = ColdOpenCritic.BuildSystemPrompt("a stern wizard");
        Assert.Contains("a stern wizard", p);
        Assert.DoesNotContain("Mobius", p);
    }

    // ── BuildUserMessage ────────────────────────────────────────────────

    [Fact]
    public void BuildUserMessage_IncludesRoomLinesAndProposedLine_MarkedUntrusted()
    {
        var ctx = new ColdOpenContext("Robotnik", "scheming", "situation", new[] { "curlyquote" },
            new[] { "curlyquote: mr beast scam images lol" });
        var draft = new ColdOpenDraft(0.86, "curlyquote, your scam archive is a blur", "scam");
        var m = ColdOpenCritic.BuildUserMessage(ctx, draft);

        Assert.Contains("untrusted", m);
        Assert.Contains("mr beast scam images", m);
        Assert.Contains("PROPOSED COLD OPEN", m);
        Assert.Contains("your scam archive is a blur", m);
        Assert.Contains("scam", m); // the claimed hook is surfaced for the deliver-on-hook check
    }

    [Fact]
    public void BuildUserMessage_NoRecentLines_CallsItDetached()
    {
        var ctx = new ColdOpenContext("Robotnik", null, "situation", Array.Empty<string>(), null);
        var draft = new ColdOpenDraft(0.5, "behold my egg empire", string.Empty);
        var m = ColdOpenCritic.BuildUserMessage(ctx, draft);

        Assert.Contains("detached", m);
        Assert.Contains("behold my egg empire", m);
    }
}
