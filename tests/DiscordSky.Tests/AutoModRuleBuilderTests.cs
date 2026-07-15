using Discord;
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

    [Fact]
    public void RuleSnapshot_SameSemanticStateDifferentOrder_IsEqual()
    {
        var first = AutoModRuleSnapshot.CreateDesired(
            "sky-scamguard-block",
            AutoModTriggerType.Keyword,
            new AutoModRuleActionProperties[]
            {
                new() { Type = AutoModActionType.SendAlertMessage, ChannelId = 42 },
                new() { Type = AutoModActionType.BlockMessage, CustomMessage = "No." },
            },
            new ulong[] { 3, 2 },
            new[] { "*evil.example*", "*bad.example*" },
            new[] { "(?i)dlscord", "(?i)nitro" });
        var reordered = AutoModRuleSnapshot.CreateDesired(
            "sky-scamguard-block",
            AutoModTriggerType.Keyword,
            new AutoModRuleActionProperties[]
            {
                new() { Type = AutoModActionType.BlockMessage, CustomMessage = "No." },
                new() { Type = AutoModActionType.SendAlertMessage, ChannelId = 42 },
            },
            new ulong[] { 2, 3 },
            new[] { "*bad.example*", "*evil.example*" },
            new[] { "(?i)nitro", "(?i)dlscord" });

        Assert.True(first.SemanticallyEquals(reordered));
    }

    [Theory]
    [InlineData(5, true, 42, "No.", true)]
    [InlineData(6, true, 42, "No.", false)]
    [InlineData(5, false, 42, "No.", false)]
    [InlineData(5, true, 43, "No.", false)]
    [InlineData(5, true, 42, "Changed", true)] // Discord does not return write-only custom_message
    public void RuleSnapshot_DetectsManagedDrift(
        int mentionLimit,
        bool raidProtection,
        ulong alertChannel,
        string customMessage,
        bool expected)
    {
        var desired = AutoModRuleSnapshot.CreateDesired(
            "sky-scamguard-mentions",
            AutoModTriggerType.MentionSpam,
            new AutoModRuleActionProperties[]
            {
                new() { Type = AutoModActionType.SendAlertMessage, ChannelId = 42 },
                new() { Type = AutoModActionType.BlockMessage, CustomMessage = "No." },
            },
            new ulong[] { 2 },
            mentionLimit: 5,
            mentionRaidProtectionEnabled: true);
        var current = AutoModRuleSnapshot.CreateDesired(
            "sky-scamguard-mentions",
            AutoModTriggerType.MentionSpam,
            new AutoModRuleActionProperties[]
            {
                new() { Type = AutoModActionType.SendAlertMessage, ChannelId = alertChannel },
                new() { Type = AutoModActionType.BlockMessage, CustomMessage = customMessage },
            },
            new ulong[] { 2 },
            mentionLimit: mentionLimit,
            mentionRaidProtectionEnabled: raidProtection);

        Assert.Equal(expected, desired.SemanticallyEquals(current));
    }
}
