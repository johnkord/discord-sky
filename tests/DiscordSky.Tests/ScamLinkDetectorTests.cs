using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public sealed class ScamLinkDetectorTests
{
    [Theory]
    [InlineData("yo check this crypto casino https://spin.win withdrawal success $5,600", false)]
    [InlineData("WITHDRAWAL SUCCESS, claim it here https://cash.top", false)]
    [InlineData("free nitro for everyone https://discord-nitro.click/claim", false)]
    [InlineData("get free robux now https://robux.gen.xyz", false)]
    [InlineData("click here to claim your reward https://reward.live/x", false)]
    [InlineData("congratulations you won a prize https://prize.vip", false)]
    [InlineData("double your bitcoin guaranteed profit https://btc2x.io", false)]
    // Phishing lookalike host alone is enough, no scam phrase required.
    [InlineData("hey is this real https://dlscord.gg/nitro", false)]
    [InlineData("https://steamcommunlty.com/gift/activate", false)]
    public void Detect_FlagsObviousScams(string text, bool mentionsEveryone)
    {
        Assert.True(ScamLinkDetector.Detect(text, mentionsEveryone).IsScam);
    }

    [Fact]
    public void Detect_FlagsMassMentionRaid()
    {
        // No phrase or host match: the signal is @everyone + a link + a money/gift token.
        var result = ScamLinkDetector.Detect(
            "@everyone come get your reward over at https://foo.top", mentionsEveryone: true);

        Assert.True(result.IsScam);
        Assert.Equal("mass-mention", result.Reason);
    }

    [Theory]
    // A link with no scam signal is just normal chat.
    [InlineData("check out this song https://youtube.com/watch?v=abc", false)]
    // Sharing a MrBeast video must NOT trip the guard (deliberately excluded to avoid this false positive).
    [InlineData("this mrbeast video is wild https://youtube.com/watch?v=x", false)]
    // "casino" only counts as part of "crypto casino"; a movie mention is fine.
    [InlineData("the casino heist movie was great https://imdb.com/title/x", false)]
    // Sharing an Elon interview is not a scam.
    [InlineData("anyone want this elon musk interview https://news.site/x", false)]
    // Scam phrase but no link: nothing to click, so do not warn.
    [InlineData("free nitro lol", false)]
    // Mass mention without any money/gift token is just an announcement.
    [InlineData("@everyone movie night at https://twitch.tv/foo", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detect_IgnoresCleanMessages(string? text, bool mentionsEveryone)
    {
        Assert.False(ScamLinkDetector.Detect(text, mentionsEveryone).IsScam);
    }

    [Fact]
    public void Detect_HonorsExtraPhrases()
    {
        Assert.False(ScamLinkDetector.Detect("grab some spinzz at https://x.io", false).IsScam);
        Assert.True(ScamLinkDetector.Detect(
            "grab some spinzz at https://x.io", false, extraPhrases: new[] { "spinzz" }).IsScam);
    }

    [Fact]
    public void Detect_HonorsExtraHosts()
    {
        Assert.True(ScamLinkDetector.Detect(
            "see https://totally-legit.example", false, extraHosts: new[] { "totally-legit" }).IsScam);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999999)]
    public void Warning_IsInCharacterAndNeverMassPings(double roll)
    {
        var line = ScamWarnings.Random(new FixedRng(roll));

        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.DoesNotContain("@everyone", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@here", line, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedRng : IRandomProvider
    {
        private readonly double _value;
        public FixedRng(double value) => _value = value;
        public double NextDouble() => _value;
    }
}
