using System.Collections.Generic;
using DiscordSky.Bot.Integrations.Reactions;

namespace DiscordSky.Tests;

public class ReactionJudgeTests
{
    private static HashSet<string> Allowed(params string[] tokens) =>
        new(tokens, StringComparer.OrdinalIgnoreCase);

    // ── ParseVerdict ────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ValidToken_ReturnsVerdict()
    {
        var v = ReactionJudge.ParseVerdict("{\"emote\":\"clown\",\"why\":\"what a fool\"}", Allowed("egg", "clown"));
        Assert.NotNull(v);
        Assert.Equal("clown", v!.Token);
        Assert.Equal("what a fool", v.Rationale);
    }

    [Fact]
    public void ParseVerdict_None_ReturnsNull()
    {
        Assert.Null(ReactionJudge.ParseVerdict("{\"emote\":\"none\",\"why\":\"mundane\"}", Allowed("egg", "clown")));
    }

    [Fact]
    public void ParseVerdict_NoneDifferentCase_ReturnsNull()
    {
        Assert.Null(ReactionJudge.ParseVerdict("{\"emote\":\"NONE\"}", Allowed("egg")));
    }

    [Fact]
    public void ParseVerdict_UnknownToken_ReturnsNull()
    {
        // The model tried to react with something we never offered; reject it (defence in depth).
        Assert.Null(ReactionJudge.ParseVerdict("{\"emote\":\"skull\",\"why\":\"lol\"}", Allowed("egg", "clown")));
    }

    [Fact]
    public void ParseVerdict_CaseInsensitiveToken_ReturnsCanonicalCasing()
    {
        // The model returned a different casing; we react with the token as we defined it, so the emote map resolves.
        var v = ReactionJudge.ParseVerdict("{\"emote\":\"ClOwN\"}", Allowed("egg", "clown"));
        Assert.NotNull(v);
        Assert.Equal("clown", v!.Token);
    }

    [Fact]
    public void ParseVerdict_CustomEmoteToken_ReturnsCanonicalCasing()
    {
        var v = ReactionJudge.ParseVerdict("{\"emote\":\"kekw\",\"why\":\"gloating\"}", Allowed("egg", "KEKW"));
        Assert.NotNull(v);
        Assert.Equal("KEKW", v!.Token);
    }

    [Fact]
    public void ParseVerdict_MissingWhy_ReturnsEmptyRationale()
    {
        var v = ReactionJudge.ParseVerdict("{\"emote\":\"egg\"}", Allowed("egg"));
        Assert.NotNull(v);
        Assert.Equal("egg", v!.Token);
        Assert.Equal(string.Empty, v.Rationale);
    }

    [Fact]
    public void ParseVerdict_WrappedInCodeFence_StillParses()
    {
        var v = ReactionJudge.ParseVerdict("```json\n{\"emote\":\"egg\",\"why\":\"mine now\"}\n```", Allowed("egg"));
        Assert.NotNull(v);
        Assert.Equal("egg", v!.Token);
    }

    [Fact]
    public void ParseVerdict_WithSurroundingProse_StillParses()
    {
        var v = ReactionJudge.ParseVerdict("Here is my verdict: {\"emote\":\"clown\",\"why\":\"fool\"} done.", Allowed("clown"));
        Assert.NotNull(v);
        Assert.Equal("clown", v!.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"emote\":}")]
    [InlineData("{\"emote\":\"\"}")]
    [InlineData("{\"why\":\"no emote field\"}")]
    [InlineData("{\"emote\":123}")]
    public void ParseVerdict_MalformedOrEmpty_ReturnsNull(string text)
    {
        Assert.Null(ReactionJudge.ParseVerdict(text, Allowed("egg", "clown")));
    }

    [Fact]
    public void ParseVerdict_NullText_ReturnsNull()
    {
        Assert.Null(ReactionJudge.ParseVerdict(null, Allowed("egg")));
    }

    [Fact]
    public void ParseVerdict_EmptyAllowedSet_ReturnsNull()
    {
        Assert.Null(ReactionJudge.ParseVerdict("{\"emote\":\"egg\"}", Allowed()));
    }

    // ── BuildSystemPrompt ───────────────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_Robotnik_MentionsCharacterAndJsonContract()
    {
        var prompt = ReactionJudge.BuildSystemPrompt("Dr. Robotnik");
        Assert.Contains("Robotnik", prompt);
        Assert.Contains("none", prompt);
        Assert.Contains("emote", prompt);
        Assert.Contains("untrusted", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_OtherPersona_UsesPersonaNameAndDeclineRule()
    {
        var prompt = ReactionJudge.BuildSystemPrompt("Weird Al");
        Assert.Contains("Weird Al", prompt);
        Assert.DoesNotContain("Robotnik", prompt);
        Assert.Contains("none", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_Robotnik_IsBroadenedNotRare()
    {
        // Guidance was widened away from "React RARELY / decline the vast majority" so he uses his full range.
        var prompt = ReactionJudge.BuildSystemPrompt("Robotnik");
        Assert.DoesNotContain("RARELY", prompt);
        Assert.Contains("verdict", prompt);
    }

    // ── BuildUserMessage ────────────────────────────────────────────────

    [Fact]
    public void BuildUserMessage_ListsEveryAllowedToken()
    {
        var request = new ReactionRequest(
            PersonaName: "Robotnik",
            AuthorDisplayName: "Sonic",
            MessageText: "gotta go fast",
            Context: null,
            Allowed: new List<AllowedEmote>
            {
                new("egg", "his signature", IsCustom: false),
                new("kekw", string.Empty, IsCustom: true),
            });

        var msg = ReactionJudge.BuildUserMessage(request, Array.Empty<string>());
        Assert.Contains("Sonic", msg);
        Assert.Contains("gotta go fast", msg);
        Assert.Contains("- egg:", msg);
        Assert.Contains("his signature", msg);
        Assert.Contains("- kekw", msg);          // the custom emote is listed (by name, under a culture header)
        Assert.Contains("custom emote", msg);    // the server-emotes section header
    }

    [Fact]
    public void BuildUserMessage_LongMessage_IsTruncated()
    {
        var request = new ReactionRequest(
            PersonaName: "Robotnik",
            AuthorDisplayName: "Spammer",
            MessageText: new string('x', 5000),
            Context: null,
            Allowed: new List<AllowedEmote> { new("egg", "sig", IsCustom: false) });

        var msg = ReactionJudge.BuildUserMessage(request, Array.Empty<string>());
        Assert.True(msg.Length < 2000, $"expected truncation, got length {msg.Length}");
    }

    [Fact]
    public void BuildUserMessage_IncludesContextWhenPresent()
    {
        var request = new ReactionRequest(
            PersonaName: "Robotnik",
            AuthorDisplayName: "Tails",
            MessageText: "look at this",
            Context: "earlier: a plan was mentioned",
            Allowed: new List<AllowedEmote> { new("eyes", "scheming", IsCustom: false) });

        var msg = ReactionJudge.BuildUserMessage(request, Array.Empty<string>());
        Assert.Contains("Context", msg);
        Assert.Contains("a plan was mentioned", msg);
    }

    [Fact]
    public void BuildUserMessage_WithMemories_IncludesMemoryBlock()
    {
        var request = new ReactionRequest(
            PersonaName: "Robotnik",
            AuthorDisplayName: "Curlyquote",
            MessageText: "lost again",
            Context: null,
            Allowed: new List<AllowedEmote> { new("chartdown", "mocking failure", IsCustom: false) });

        var msg = ReactionJudge.BuildUserMessage(request, new[] { "always loses at chess", "obsessed with eggs" });
        Assert.Contains("What you know about Curlyquote", msg);
        Assert.Contains("always loses at chess", msg);
        Assert.Contains("obsessed with eggs", msg);
    }

    [Fact]
    public void BuildUserMessage_NoMemories_OmitsMemoryBlock()
    {
        var request = new ReactionRequest(
            PersonaName: "Robotnik",
            AuthorDisplayName: "Curlyquote",
            MessageText: "hi",
            Context: null,
            Allowed: new List<AllowedEmote> { new("egg", "sig", IsCustom: false) });

        var msg = ReactionJudge.BuildUserMessage(request, Array.Empty<string>());
        Assert.DoesNotContain("What you know about", msg);
    }

    [Fact]
    public void BuildUserMessage_WithRecentEmojis_IncludesVarietyNudge()
    {
        var request = new ReactionRequest(
            PersonaName: "Robotnik",
            AuthorDisplayName: "Sonic",
            MessageText: "gotta go fast",
            Context: null,
            Allowed: new List<AllowedEmote> { new("anger", "rage", IsCustom: false), new("clown", "fool", IsCustom: false) },
            RecentEmojis: new[] { "anger", "clown" });

        var msg = ReactionJudge.BuildUserMessage(request, Array.Empty<string>());
        Assert.Contains("recently reacted with", msg);
        Assert.Contains("anger", msg);
    }

    [Fact]
    public void BuildSystemPrompt_Robotnik_EncouragesVariety()
    {
        var prompt = ReactionJudge.BuildSystemPrompt("Robotnik");
        Assert.Contains("vary your reactions", prompt);
    }
}
