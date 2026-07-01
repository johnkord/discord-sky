using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public sealed class AutoModRuleBuilderTests
{
    [Fact]
    public void BuildBlockPlan_RegexPlusLearnedHosts()
    {
        var plan = AutoModRuleBuilder.BuildBlockPlan("dlscord|discord-nitro", new[] { "evil.example" });

        Assert.Single(plan.RegexPatterns);
        Assert.StartsWith("(?i)", plan.RegexPatterns[0]);
        Assert.Contains("dlscord", plan.RegexPatterns[0]);
        Assert.Contains("*evil.example*", plan.Keywords);
    }

    [Fact]
    public void BuildBlockPlan_DropsOverlongRegex()
    {
        var tooLong = new string('a', 300);
        var plan = AutoModRuleBuilder.BuildBlockPlan(tooLong, Array.Empty<string>());

        Assert.Empty(plan.RegexPatterns);
        Assert.Empty(plan.Keywords);
    }

    [Fact]
    public void BuildAlertPlan_WildcardsPhrases_IncludesLearned_Dedupes()
    {
        var plan = AutoModRuleBuilder.BuildAlertPlan(
            new[] { "free nitro", "free nitro", "crypto casino" }, new[] { "drain your wallet" });

        Assert.Contains("*free nitro*", plan.Keywords);
        Assert.Contains("*crypto casino*", plan.Keywords);
        Assert.Contains("*drain your wallet*", plan.Keywords);
        Assert.Equal(1, plan.Keywords.Count(k => k == "*free nitro*"));
        Assert.Empty(plan.RegexPatterns);
    }

    [Fact]
    public void BuildAlertPlan_CapsKeywordLength()
    {
        var longPhrase = new string('x', 70);
        var plan = AutoModRuleBuilder.BuildAlertPlan(new[] { longPhrase }, Array.Empty<string>());

        Assert.DoesNotContain(plan.Keywords, k => k.Length > 60);
    }

    [Fact]
    public void BuiltInLists_AreExposedAndNonEmpty()
    {
        Assert.NotEmpty(ScamLinkDetector.BuiltInScamPhrases);
        Assert.Contains("dlscord", ScamLinkDetector.BuiltInLookalikePattern);
    }
}
