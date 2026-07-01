using Discord;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public class AutoModActionResponderTests
{
    [Theory]
    [InlineData(AutoModActionType.BlockMessage, "blocked")]
    [InlineData(AutoModActionType.SendAlertMessage, "alerted")]
    [InlineData(AutoModActionType.Timeout, "timeout")]
    [InlineData(AutoModActionType.BlockMemberInteraction, "blocked_interaction")]
    public void MapOutcome_maps_known_action_types(AutoModActionType type, string expected)
    {
        Assert.Equal(expected, AutoModActionResponder.MapOutcome(type));
    }

    [Theory]
    [InlineData("sky-scamguard-block", "sky-scamguard", true)]
    [InlineData("SKY-SCAMGUARD-ALERT", "sky-scamguard", true)]
    [InlineData("some-other-rule", "sky-scamguard", false)]
    [InlineData(null, "sky-scamguard", false)]
    [InlineData("", "sky-scamguard", false)]
    [InlineData("sky-scamguard-block", "", false)]
    public void IsOurRule_matches_prefix_case_insensitively(string? ruleName, string prefix, bool expected)
    {
        Assert.Equal(expected, AutoModActionResponder.IsOurRule(ruleName, prefix));
    }

    [Fact]
    public void BuildReason_includes_rule_trigger_and_keyword()
    {
        var reason = AutoModActionResponder.BuildReason(
            "sky-scamguard-block", 123UL, AutoModTriggerType.Keyword, "dlscord");
        Assert.Equal("rule=sky-scamguard-block;trigger=Keyword;kw=dlscord", reason);
    }

    [Fact]
    public void BuildReason_omits_keyword_when_empty()
    {
        var reason = AutoModActionResponder.BuildReason(
            "sky-scamguard-alert", 123UL, AutoModTriggerType.Keyword, "");
        Assert.Equal("rule=sky-scamguard-alert;trigger=Keyword", reason);
    }

    [Fact]
    public void BuildReason_falls_back_to_rule_id_when_name_unknown()
    {
        var reason = AutoModActionResponder.BuildReason(
            null, 999UL, AutoModTriggerType.MentionSpam, null);
        Assert.Equal("rule=999;trigger=MentionSpam", reason);
    }

    [Fact]
    public void BuildReason_truncates_long_matched_keyword()
    {
        var longKeyword = new string('x', 80);
        var reason = AutoModActionResponder.BuildReason(
            "r", 1UL, AutoModTriggerType.Keyword, longKeyword);
        Assert.Equal($"rule=r;trigger=Keyword;kw={new string('x', 40)}", reason);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999999)]
    public void AutoModBlockTaunts_Random_returns_a_nonempty_line(double roll)
    {
        var line = AutoModBlockTaunts.Random(new FixedRng(roll));
        Assert.False(string.IsNullOrWhiteSpace(line));
    }

    [Fact]
    public void AutoModBlockTaunts_Random_selects_distinct_lines_across_the_range()
    {
        var first = AutoModBlockTaunts.Random(new FixedRng(0.0));
        var last = AutoModBlockTaunts.Random(new FixedRng(0.999999));
        Assert.NotEqual(first, last);
    }

    private sealed class FixedRng : IRandomProvider
    {
        private readonly double _value;
        public FixedRng(double value) => _value = value;
        public double NextDouble() => _value;
    }
}
