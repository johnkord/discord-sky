using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public sealed class DomainUtilitiesTests
{
    [Fact]
    public void ExtractHosts_FindsAllAndDedupes()
    {
        var hosts = DomainUtilities.ExtractHosts(
            "see https://youtube.com/watch and http://bit.ly/x and bare Example.org! plus youtube.com again");

        Assert.Contains("youtube.com", hosts);
        Assert.Contains("bit.ly", hosts);
        Assert.Contains("example.org", hosts);
        Assert.Equal(3, hosts.Count);
    }

    [Theory]
    [InlineData("just some words")]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractHosts_EmptyWhenNoLink(string? text)
    {
        Assert.Empty(DomainUtilities.ExtractHosts(text));
    }

    [Fact]
    public void SuffixCandidates_WalksUpToRegistrable()
    {
        var candidates = DomainUtilities.SuffixCandidates("login.evil.co").ToList();

        Assert.Equal(new[] { "login.evil.co", "evil.co" }, candidates);
    }

    [Theory]
    [InlineData("d1s\u0441ord", "dlscord")]   // digit '1' + Cyrillic 'c'
    [InlineData("disc\u043Ered", "discored")] // Cyrillic 'o'
    public void Skeleton_FoldsConfusables(string input, string expected)
    {
        Assert.Equal(expected, DomainUtilities.Skeleton(input));
    }

    [Theory]
    [InlineData("bit.ly", true)]
    [InlineData("tinyurl.com", true)]
    [InlineData("youtube.com", false)]
    public void IsShortener_Knows(string host, bool expected)
    {
        Assert.Equal(expected, DomainUtilities.IsShortener(host));
    }

    [Theory]
    [InlineData("join here discord.gg/mrbeast", true)]
    [InlineData("come to https://discord.com/invite/abc123", true)]
    [InlineData("check youtube.com/watch", false)]
    [InlineData("", false)]
    public void ContainsInvite_Detects(string text, bool expected)
    {
        Assert.Equal(expected, DomainUtilities.ContainsInvite(text));
    }

    [Fact]
    public void ExtractLinkKeys_KeepsFirstPathSegment()
    {
        var keys = DomainUtilities.ExtractLinkKeys("a discord.gg/abc and https://scam.top/win?x=1 and bare foo.com");

        Assert.Contains("discord.gg/abc", keys);
        Assert.Contains("scam.top/win", keys);
        Assert.Contains("foo.com", keys);
    }
}
