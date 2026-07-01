using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public sealed class AutoModRuleBuilderTests
{
    [Fact]
    public void Build_WildcardsPhrases_AndIncludesLearned()
    {
        var plan = AutoModRuleBuilder.Build(
            new[] { "free nitro", "crypto casino" }, "dlscord|discord-nitro",
            new[] { "drain your wallet" }, new[] { "evil.example" },
            includePhrases: true, includeLookalikeRegex: true);

        Assert.Contains("*free nitro*", plan.Keywords);
        Assert.Contains("*crypto casino*", plan.Keywords);
        Assert.Contains("*drain your wallet*", plan.Keywords);
        Assert.Contains("*evil.example*", plan.Keywords);
        Assert.Single(plan.RegexPatterns);
        Assert.StartsWith("(?i)", plan.RegexPatterns[0]);
        Assert.Contains("dlscord", plan.RegexPatterns[0]);
    }

    [Fact]
    public void Build_DedupesAndCapsKeywordLength()
    {
        var longPhrase = new string('x', 70);
        var plan = AutoModRuleBuilder.Build(
            new[] { "free nitro", "free nitro", longPhrase }, "",
            Array.Empty<string>(), Array.Empty<string>(), true, true);

        Assert.Equal(1, plan.Keywords.Count(k => k == "*free nitro*"));
        Assert.DoesNotContain(plan.Keywords, k => k.Length > 60);
    }

    [Fact]
    public void Build_PhrasesDisabled_KeepsLearnedHostsAndRegex()
    {
        var plan = AutoModRuleBuilder.Build(
            new[] { "free nitro" }, "dlscord",
            new[] { "some phrase" }, new[] { "evil.example" },
            includePhrases: false, includeLookalikeRegex: true);

        Assert.DoesNotContain("*free nitro*", plan.Keywords);
        Assert.DoesNotContain("*some phrase*", plan.Keywords);
        Assert.Contains("*evil.example*", plan.Keywords); // learned hosts are always included
        Assert.Single(plan.RegexPatterns);
    }

    [Fact]
    public void Build_RegexDisabledOrTooLong_EmitsNoPattern()
    {
        Assert.Empty(AutoModRuleBuilder.Build(
            Array.Empty<string>(), "dlscord", Array.Empty<string>(), new[] { "x.example" },
            includePhrases: true, includeLookalikeRegex: false).RegexPatterns);

        var tooLong = new string('a', 300);
        Assert.Empty(AutoModRuleBuilder.Build(
            Array.Empty<string>(), tooLong, Array.Empty<string>(), new[] { "x.example" },
            includePhrases: true, includeLookalikeRegex: true).RegexPatterns);
    }

    [Fact]
    public void BuiltInLists_AreExposedAndNonEmpty()
    {
        Assert.NotEmpty(ScamLinkDetector.BuiltInScamPhrases);
        Assert.Contains("dlscord", ScamLinkDetector.BuiltInLookalikePattern);
    }
}
