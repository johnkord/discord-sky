using DiscordSky.Bot.Memory.Scoring;

namespace DiscordSky.Tests;

public class TokenUtilitiesTests
{
    [Fact]
    public void ExtractContentTokens_LowercasesAndDropsStopwords()
    {
        var tokens = TokenUtilities.ExtractContentTokens("The QUICK brown fox jumped over the lazy dog.");
        Assert.Contains("quick", tokens);
        Assert.Contains("brown", tokens);
        Assert.Contains("fox", tokens);
        Assert.Contains("lazy", tokens);
        Assert.Contains("dog", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("over", tokens);
    }

    [Fact]
    public void ExtractContentTokens_StripsSimpleSuffixes()
    {
        var tokens = TokenUtilities.ExtractContentTokens("cats cats cat running runs");
        Assert.Contains("cat", tokens);
        Assert.Contains("run", tokens);
    }

    [Fact]
    public void ExtractContentTokens_DropsShortTokens()
    {
        var tokens = TokenUtilities.ExtractContentTokens("a bb ccc dddd");
        Assert.DoesNotContain("a", tokens);
        Assert.DoesNotContain("bb", tokens);
        Assert.Contains("ccc", tokens);
        Assert.Contains("dddd", tokens);
    }

    [Fact]
    public void Jaccard_IdenticalSetsIsOne()
    {
        var a = new HashSet<string> { "cat", "dog" };
        var b = new HashSet<string> { "cat", "dog" };
        Assert.Equal(1.0, TokenUtilities.Jaccard(a, b));
    }

    [Fact]
    public void Jaccard_DisjointSetsIsZero()
    {
        var a = new HashSet<string> { "cat" };
        var b = new HashSet<string> { "dog" };
        Assert.Equal(0.0, TokenUtilities.Jaccard(a, b));
    }

    [Fact]
    public void Jaccard_EmptyEitherSideIsZero()
    {
        Assert.Equal(0.0, TokenUtilities.Jaccard(new HashSet<string>(), new HashSet<string> { "x" }));
    }
}
