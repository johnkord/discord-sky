using DiscordSky.Bot.Orchestration.Impulse;
using DiscordSky.Bot.Models.Orchestration;

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

    [Fact]
    public void ParseWorth_VisualFieldsParseAndClamp()
    {
        var v = ImpulseJudge.ParseWorth(
            "{\"worth\":0.4,\"thought\":\"a line\",\"visual_worth\":1.4,\"visual_hook\":\"lava board meeting\"}");

        Assert.NotNull(v);
        Assert.Equal(1.0, v!.VisualWorth);
        Assert.Equal("lava board meeting", v.VisualHook);
    }

    [Fact]
    public void ParseWorth_OptionalReferentFieldsRemainBackwardCompatible()
    {
        var legacy = ImpulseJudge.ParseWorth("{\"worth\":0.4}");
        var enriched = ImpulseJudge.ParseWorth(
            "{\"worth\":0.8,\"referent_message_id\":\"42\",\"referent_confidence\":1.4,\"referent_status\":\"resolved\"}");

        Assert.NotNull(legacy);
        Assert.Null(legacy!.ReferentMessageId);
        Assert.Equal(ReferentResolutionStatus.None, legacy.ReferentStatus);
        Assert.NotNull(enriched);
        Assert.Equal(42UL, enriched!.ReferentMessageId);
        Assert.Equal(1.0, enriched.ReferentConfidence);
        Assert.Equal(ReferentResolutionStatus.Resolved, enriched.ReferentStatus);
    }

    [Fact]
    public void ValidateReferentDecision_AcceptsOnlyOfferedHighConfidenceCandidate()
    {
        var episode = Episode();

        var accepted = ImpulseJudge.ValidateReferentDecision(
            new WorthVerdict(0.8, "", ReferentMessageId: 1, ReferentConfidence: 0.9),
            episode,
            0.7);
        var invalid = ImpulseJudge.ValidateReferentDecision(
            new WorthVerdict(0.8, "", ReferentMessageId: 999, ReferentConfidence: 1.0),
            episode,
            0.7);
        var weak = ImpulseJudge.ValidateReferentDecision(
            new WorthVerdict(0.8, "", ReferentMessageId: 1, ReferentConfidence: 0.2),
            episode,
            0.7);

        Assert.Equal(1UL, accepted.SelectedMessageId);
        Assert.Equal(ReferentResolutionStatus.Resolved, accepted.Status);
        Assert.Null(invalid.SelectedMessageId);
        Assert.Equal(ReferentResolutionStatus.Invalid, invalid.Status);
        Assert.Null(weak.SelectedMessageId);
        Assert.Equal(ReferentResolutionStatus.Ambiguous, weak.Status);
    }

    [Theory]
    [InlineData(0.80, 0.90, true, AmbientActionKind.Image)]
    [InlineData(0.80, 0.83, true, AmbientActionKind.Text)]
    [InlineData(0.40, 0.80, true, AmbientActionKind.Image)]
    [InlineData(0.40, 0.80, false, AmbientActionKind.Silence)]
    [InlineData(0.40, 0.50, true, AmbientActionKind.Silence)]
    public void ActionArbiter_SelectsSingleBestEligibleAction(
        double textWorth,
        double visualWorth,
        bool visualEnabled,
        AmbientActionKind expected)
    {
        var actual = AmbientActionArbiter.Choose(
            useWorthGate: true,
            new WorthVerdict(textWorth, "text", visualWorth, "visual"),
            textThreshold: 0.5,
            visualEnabled,
            visualThreshold: 0.72,
            visualMinLead: 0.05);

        Assert.Equal(expected, actual);
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
    public void BuildUserMessage_LabelsRoomStateSeparatelyFromReplyContext()
    {
        var message = ImpulseJudge.BuildUserMessage(new AmbientImpulseRequest(
            "Robotnik",
            "bob",
            "lol",
            Context: null,
            MoodLabel: null,
            SituationContext: "Robotnik spoke in the last two minutes: yes."));

        Assert.Contains("Current room state", message);
        Assert.Contains("Robotnik spoke", message);
        Assert.DoesNotContain("message it replies to", message);
    }

    [Fact]
    public void BuildUserMessage_MediaOnly_IncludesUntrustedMediaContext()
    {
        var m = ImpulseJudge.BuildUserMessage(new AmbientImpulseRequest(
            "Robotnik", "bob", string.Empty, null, null, "tweet by alice: a foolish boast"));

        Assert.Contains("no text", m);
        Assert.Contains("Media/link context", m);
        Assert.Contains("a foolish boast", m);
        Assert.Contains("untrusted", m);
    }

    [Fact]
    public void BuildUserMessage_TruncatesLongMessage()
    {
        var longMsg = new string('x', 900);
        var m = ImpulseJudge.BuildUserMessage(new AmbientImpulseRequest("Robotnik", "bob", longMsg, null, null));
        Assert.True(m.Length < 900);
    }

    [Fact]
    public void BuildUserMessage_EpisodeProjectionReplacesDivergentLegacyContext()
    {
        var request = new AmbientImpulseRequest(
            "Robotnik",
            "bob",
            "legacy trigger",
            "legacy parent",
            null,
            EpisodeProjection: "canonical frozen episode");

        var message = ImpulseJudge.BuildUserMessage(request);

        Assert.Equal("canonical frozen episode", message);
        Assert.DoesNotContain("legacy", message);
    }

    private static InteractionEpisode Episode()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        return InteractionEpisode.Create(
            "episode-1",
            now,
            99,
            2,
            new[]
            {
                new EpisodeMessage(1, 10, "Alice", "meteor", now.AddSeconds(-5)),
                new EpisodeMessage(2, 20, "Bob", "what is that?", now),
            },
            null,
            new ReferentRequirement(true, "deictic_question"),
            new[] { new ReferentCandidate(1, 0.75, "recent_message") });
    }
}
