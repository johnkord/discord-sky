using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Integrations;
using DiscordSky.Bot.Integrations.Members;

namespace DiscordSky.Tests;

public class MessageForwardTests
{
    [Fact]
    public void Combine_NoForwarded_ReturnsTrimmedContent()
    {
        Assert.Equal("hello", MessageForwardExtensions.Combine("  hello  ", null));
    }

    [Fact]
    public void Combine_EmptyContentWithForwarded_ReturnsForwarded()
    {
        // The real-world case: a forwarded scam link arrives with empty Content and the payload in a snapshot.
        var result = MessageForwardExtensions.Combine("", new[] { "free nitro at scam.link/claim" });
        Assert.Equal("free nitro at scam.link/claim", result);
    }

    [Fact]
    public void Combine_ContentAndForwarded_NewlineJoined()
    {
        var result = MessageForwardExtensions.Combine("look at this:", new[] { "forwarded body" });
        Assert.Equal("look at this:\nforwarded body", result);
    }

    [Fact]
    public void Combine_SkipsNullAndWhitespaceForwarded()
    {
        var result = MessageForwardExtensions.Combine("base", new[] { null, "  ", "real" });
        Assert.Equal("base\nreal", result);
    }

    [Fact]
    public void Combine_MultipleForwarded_AllIncluded()
    {
        var result = MessageForwardExtensions.Combine(null, new[] { "one", "two" });
        Assert.Equal("one\ntwo", result);
    }
}

public class JoinRaidTrackerTests
{
    [Fact]
    public void Record_BelowThreshold_NotRaid()
    {
        var tracker = new JoinRaidTracker();
        var r = tracker.Record(1, DateTimeOffset.UtcNow, 30, 5);
        Assert.False(r.IsRaid);
        Assert.False(r.JustCrossed);
        Assert.Equal(1, r.CountInWindow);
    }

    [Fact]
    public void Record_CrossingThreshold_FlagsJustCrossedExactlyOnce()
    {
        var tracker = new JoinRaidTracker();
        var now = DateTimeOffset.UtcNow;
        JoinResult last = default;
        for (var i = 0; i < 5; i++)
        {
            last = tracker.Record(7, now.AddSeconds(i * 0.1), 30, 5);
        }

        Assert.True(last.IsRaid);
        Assert.True(last.JustCrossed);
        Assert.Equal(5, last.CountInWindow);

        // Still a raid on the next join, but no longer "just crossed" -> the caller alerts only once.
        var next = tracker.Record(7, now.AddSeconds(0.6), 30, 5);
        Assert.True(next.IsRaid);
        Assert.False(next.JustCrossed);
    }

    [Fact]
    public void Record_OutsideWindow_Expires()
    {
        var tracker = new JoinRaidTracker();
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            tracker.Record(3, start.AddSeconds(i), 30, 5);
        }

        // 40s later the earlier joins have aged out of the 30s window, so the count resets.
        var r = tracker.Record(3, start.AddSeconds(40), 30, 5);
        Assert.False(r.IsRaid);
        Assert.Equal(1, r.CountInWindow);
    }

    [Fact]
    public void Record_SeparateGuilds_TrackedIndependently()
    {
        var tracker = new JoinRaidTracker();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            tracker.Record(100, now.AddSeconds(i * 0.1), 30, 5);
        }

        var other = tracker.Record(200, now, 30, 5);
        Assert.False(other.IsRaid);
        Assert.Equal(1, other.CountInWindow);
    }
}

public class MemberGreetingsTests
{
    private sealed class FixedRng : IRandomProvider
    {
        private readonly double _value;
        public FixedRng(double value) => _value = value;
        public double NextDouble() => _value;
    }

    [Fact]
    public void Random_SubstitutesDisplayName()
    {
        var line = MemberGreetings.Random(new FixedRng(0.0), "Coconuts");
        Assert.Contains("Coconuts", line);
        Assert.DoesNotContain("{0}", line);
    }

    [Fact]
    public void Random_BlankName_UsesFallback()
    {
        var line = MemberGreetings.Random(new FixedRng(0.0), "   ");
        Assert.Contains("newcomer", line);
    }

    [Fact]
    public void Random_TopOfRange_StaysInBounds()
    {
        // NextDouble() near 1.0 must not index past the end of the line array.
        var line = MemberGreetings.Random(new FixedRng(0.999999), "Scratch");
        Assert.Contains("Scratch", line);
    }
}
