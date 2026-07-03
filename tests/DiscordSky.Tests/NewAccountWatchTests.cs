using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public class NewAccountHeuristicsTests
{
    private static NewAccountSignals Signals(
        double age, bool invite = false, bool everyone = false, bool shortener = false,
        bool linkOrEmbed = false, bool attach = false, int mentions = 0)
        => new(age, invite, everyone, shortener, linkOrEmbed, attach, mentions);

    [Fact]
    public void OldAccount_NeverAlerts_EvenWithPayload()
    {
        var v = NewAccountHeuristics.Evaluate(
            Signals(400, invite: true, everyone: true, linkOrEmbed: true), newAccountDays: 21, threshold: 3);
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
    public void NewAccount_WithLink_AlertsAtDefaultThreshold()
    {
        // On this rare-new-member server the default (3) is high-recall: a new account + any link alerts.
        var v = NewAccountHeuristics.Evaluate(Signals(3, linkOrEmbed: true), 21, 3);
        Assert.True(v.ShouldAlert);
        Assert.Equal(3, v.Score);
        Assert.Contains("link", v.Reason);
        Assert.Contains("new_account", v.Reason);
    }

    [Fact]
    public void LinkAndEmbed_AreOneSignal_NotTwo()
    {
        // A link and the embed Discord auto-generates for it must not double-count: score stays 3, not 4.
        var v = NewAccountHeuristics.Evaluate(Signals(3, linkOrEmbed: true), 21, 4);
        Assert.Equal(3, v.Score);
        Assert.False(v.ShouldAlert); // a lone link is weak; below a strict threshold of 4
    }

    [Fact]
    public void NewAccount_WithInvite_IsStrong()
    {
        var v = NewAccountHeuristics.Evaluate(Signals(6, invite: true), 21, 4);
        Assert.True(v.ShouldAlert); // a strong signal reaches the strict threshold alone
        Assert.True(v.Score >= 4);
    }

    [Fact]
    public void NewAccount_WithShortener_IsStrong()
    {
        var v = NewAccountHeuristics.Evaluate(Signals(6, shortener: true), 21, 4);
        Assert.True(v.ShouldAlert);
        Assert.Contains("shortener", v.Reason);
    }

    [Fact]
    public void NewAccount_EveryoneMention_IsStrong()
    {
        Assert.True(NewAccountHeuristics.Evaluate(Signals(2, everyone: true), 21, 4).ShouldAlert);
    }

    [Fact]
    public void NewAccount_TwoWeakSignals_ReachStrictThreshold()
    {
        // link + attachment: two weak signals corroborate to 4.
        var v = NewAccountHeuristics.Evaluate(Signals(3, linkOrEmbed: true, attach: true), 21, 4);
        Assert.Equal(4, v.Score);
        Assert.True(v.ShouldAlert);
    }

    [Theory]
    [InlineData(6.2)]
    [InlineData(7.0)]
    [InlineData(7.5)]
    public void MatchesTheMissedSpammers_YoungAccountWithPayload(double age)
    {
        // The three real 2026-07-02 misses were 6.2/7.0/7.5-day accounts posting a payload. Under the 21-day gate
        // (raised from 7, which two of them exceeded), a young account with a link alerts at the default threshold.
        Assert.True(NewAccountHeuristics.Evaluate(Signals(age, linkOrEmbed: true), 21, 3).ShouldAlert);
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
