using System.Collections.Generic;
using DiscordSky.Bot.Integrations.Reactions;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the custom-emote candidate selector: it decides which of a guild's many custom emotes are put
/// in front of the reaction judge, biasing toward author/message-relevant ones and rotating the rest so the
/// judge stops collapsing onto a couple of generic unicode faces.
/// </summary>
public class ReactionSelectionTests
{
    private static Func<double> Const(double v) => () => v;

    [Fact]
    public void Select_EmptyOrZeroMax_ReturnsEmpty()
    {
        Assert.Empty(ReactionSelection.SelectCustomEmoteNames(new List<string>(), "a", "b", null, 5, Const(0)));
        Assert.Empty(ReactionSelection.SelectCustomEmoteNames(new[] { "x", "y" }, "a", "b", null, 0, Const(0)));
    }

    [Fact]
    public void Select_FewerThanMax_ReturnsAll()
    {
        var names = new[] { "one", "two", "three" };
        var got = ReactionSelection.SelectCustomEmoteNames(names, null, null, null, 10, Const(0));
        Assert.Equal(3, got.Count);
        Assert.Equal(new HashSet<string>(names), new HashSet<string>(got));
    }

    [Fact]
    public void Select_RespectsMax()
    {
        var names = new[] { "a1", "a2", "a3", "a4", "a5" };
        var got = ReactionSelection.SelectCustomEmoteNames(names, null, null, null, 2, Const(0));
        Assert.Equal(2, got.Count);
    }

    [Fact]
    public void Select_AuthorAndMessageRelevant_SurfaceEvenWhenBudgetTight()
    {
        // author "Alascene" -> "alapat" (shared "ala" prefix = a member in-joke emote);
        // message "pog moment" -> "pogchamp" (topical word appears in the emote name).
        var names = new[] { "sadcat", "pogchamp", "zzz1", "zzz2", "alapat" };
        var got = ReactionSelection.SelectCustomEmoteNames(names, "Alascene", "pog moment", null, 3, Const(0));
        Assert.Equal(3, got.Count);
        Assert.Contains("pogchamp", got); // message echo
        Assert.Contains("alapat", got);   // author echo
    }

    [Fact]
    public void Select_RecentlyUsed_DroppedWhenNoRoom()
    {
        var names = new[] { "used", "fresh1", "fresh2" };
        var got = ReactionSelection.SelectCustomEmoteNames(names, null, null, new[] { "used" }, 2, Const(0));
        Assert.Equal(2, got.Count);
        Assert.DoesNotContain("used", got); // a just-used emote loses to fresh ones when the budget is tight
    }

    [Fact]
    public void Select_RecentlyUsed_IncludedWhenRoom()
    {
        var names = new[] { "used", "fresh1", "fresh2" };
        var got = ReactionSelection.SelectCustomEmoteNames(names, null, null, new[] { "used" }, 5, Const(0));
        Assert.Equal(3, got.Count);
        Assert.Contains("used", got);
    }

    [Fact]
    public void Select_Deterministic_SameRngSameResult()
    {
        var names = new[] { "a1", "a2", "a3", "a4", "a5", "a6" };
        var got1 = ReactionSelection.SelectCustomEmoteNames(names, "x", "y", null, 3, Const(0.3));
        var got2 = ReactionSelection.SelectCustomEmoteNames(names, "x", "y", null, 3, Const(0.3));
        Assert.Equal(got1, got2);
    }
}
