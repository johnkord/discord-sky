using DiscordSky.Bot.Integrations.Safety;

namespace DiscordSky.Tests;

public sealed class RaidTrackerTests
{
    [Fact]
    public void MultiChannel_SameLink_IsRaid()
    {
        var t = new RaidTracker();
        var now = DateTimeOffset.UtcNow;

        Assert.False(t.Record(1, 100, "discord.gg/abc", now, 60, 3, 4).IsRaid);
        Assert.False(t.Record(1, 200, "discord.gg/abc", now.AddSeconds(1), 60, 3, 4).IsRaid);

        var third = t.Record(1, 300, "discord.gg/abc", now.AddSeconds(2), 60, 3, 4);
        Assert.True(third.IsRaid);
        Assert.Equal("raid-multichannel", third.Reason);
    }

    [Fact]
    public void Repeat_SameChannel_IsRaid()
    {
        var t = new RaidTracker();
        var now = DateTimeOffset.UtcNow;

        var last = RaidResult.None;
        for (var i = 0; i < 4; i++)
        {
            last = t.Record(1, 100, "scam.top/x", now.AddSeconds(i), 60, 3, 4);
        }

        Assert.True(last.IsRaid);
        Assert.Equal("raid-repeat", last.Reason);
    }

    [Fact]
    public void DifferentLinks_NotRaid()
    {
        var t = new RaidTracker();
        var now = DateTimeOffset.UtcNow;

        Assert.False(t.Record(1, 100, "discord.gg/aaa", now, 60, 3, 4).IsRaid);
        Assert.False(t.Record(1, 200, "discord.gg/bbb", now, 60, 3, 4).IsRaid);
        Assert.False(t.Record(1, 300, "discord.gg/ccc", now, 60, 3, 4).IsRaid);
    }

    [Fact]
    public void OutsideWindow_NotRaid()
    {
        var t = new RaidTracker();
        var now = DateTimeOffset.UtcNow;

        Assert.False(t.Record(1, 100, "x.top/a", now, 30, 3, 4).IsRaid);
        Assert.False(t.Record(1, 200, "x.top/a", now.AddSeconds(40), 30, 3, 4).IsRaid);
        Assert.False(t.Record(1, 300, "x.top/a", now.AddSeconds(80), 30, 3, 4).IsRaid);
    }

    [Fact]
    public void EmptyFingerprint_Ignored()
    {
        var t = new RaidTracker();
        Assert.False(t.Record(1, 100, "", DateTimeOffset.UtcNow, 60, 3, 4).IsRaid);
    }
}
