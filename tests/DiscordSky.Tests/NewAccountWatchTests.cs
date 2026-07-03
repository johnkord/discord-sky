using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public class NewAccountHeuristicsTests
{
    private static NewAccountSignals Signals(
        double age, bool link = false, bool invite = false, bool attach = false,
        bool embed = false, bool everyone = false, int mentions = 0)
        => new(age, link, invite, attach, embed, everyone, mentions);

    [Fact]
    public void OldAccount_NeverAlerts_EvenWithPayload()
    {
        var v = NewAccountHeuristics.Evaluate(Signals(400, link: true, invite: true, everyone: true), newAccountDays: 21, threshold: 3);
        Assert.False(v.ShouldAlert);
        Assert.Equal(0, v.Score);
    }

    [Fact]
    public void NewAccount_NoPayload_DoesNotAlert()
    {
        var v = NewAccountHeuristics.Evaluate(Signals(3), 21, 3);
        Assert.False(v.ShouldAlert);
        Assert.Equal(2, v.Score); // being new alone is not enough
    }

    [Fact]
    public void NewAccount_WithLink_Alerts()
    {
        var v = NewAccountHeuristics.Evaluate(Signals(3, link: true), 21, 3);
        Assert.True(v.ShouldAlert);
        Assert.Equal(3, v.Score);
        Assert.Contains("link", v.Reason);
        Assert.Contains("new_account", v.Reason);
    }

    [Fact]
    public void NewAccount_WithInvite_Alerts()
    {
        var v = NewAccountHeuristics.Evaluate(Signals(6, invite: true), 21, 3);
        Assert.True(v.ShouldAlert);
        Assert.True(v.Score >= 4);
    }

    [Fact]
    public void NewAccount_AttachmentOnly_Alerts()
    {
        Assert.True(NewAccountHeuristics.Evaluate(Signals(7, attach: true), 21, 3).ShouldAlert);
    }

    [Fact]
    public void NewAccount_EmbedOnly_Alerts()
    {
        Assert.True(NewAccountHeuristics.Evaluate(Signals(2, embed: true), 21, 3).ShouldAlert);
    }

    [Fact]
    public void NewAccount_EveryoneMention_Alerts()
    {
        Assert.True(NewAccountHeuristics.Evaluate(Signals(2, everyone: true), 21, 3).ShouldAlert);
    }

    [Fact]
    public void Threshold_Respected()
    {
        // With a stricter threshold, a lone link is not enough.
        Assert.False(NewAccountHeuristics.Evaluate(Signals(3, link: true), 21, 5).ShouldAlert);
    }

    [Theory]
    [InlineData(6.2)]
    [InlineData(7.0)]
    [InlineData(7.5)]
    public void MatchesTheMissedSpammers_YoungAccountWithPayload(double age)
    {
        // The three real 2026-07-02 misses were 6.2/7.0/7.5-day accounts posting a payload. Under the 21-day gate
        // (raised from 7, which two of them exceeded), all three now alert.
        Assert.True(NewAccountHeuristics.Evaluate(Signals(age, link: true), 21, 3).ShouldAlert);
    }
}

public class NewAccountFlagLogTests
{
    [Fact]
    public void Record_And_WasFlaggedWithin()
    {
        var log = new NewAccountFlagLog();
        var now = DateTimeOffset.UtcNow;
        log.Record(42, now, "new_account(3d)+link");
        Assert.True(log.WasFlaggedWithin(42, now.AddMinutes(5), TimeSpan.FromMinutes(10)));
        Assert.False(log.WasFlaggedWithin(42, now.AddMinutes(20), TimeSpan.FromMinutes(10)));
        Assert.False(log.WasFlaggedWithin(99, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void TryGet_ReturnsReason()
    {
        var log = new NewAccountFlagLog();
        log.Record(7, DateTimeOffset.UtcNow, "reason-x");
        Assert.True(log.TryGet(7, out var rec));
        Assert.Equal("reason-x", rec.Reason);
    }

    [Fact]
    public void Prune_RemovesExpired()
    {
        var log = new NewAccountFlagLog();
        var start = DateTimeOffset.UtcNow;
        log.Record(1, start, "old");
        log.Prune(start.AddHours(2), TimeSpan.FromHours(1));
        Assert.False(log.TryGet(1, out _));
    }
}
