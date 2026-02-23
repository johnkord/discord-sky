using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class SafetyFilterTests
{
    private static SafetyFilter CreateFilter(ChaosSettings? settings = null)
    {
        var opts = Options.Create(settings ?? new ChaosSettings());
        return new SafetyFilter(opts, NullLogger<SafetyFilter>.Instance);
    }

    // ── Rate limiting ──────────────────────────────────────────────

    [Fact]
    public void ShouldRateLimit_AllowsUpToConfiguredCap()
    {
        var filter = CreateFilter(new ChaosSettings { MaxPromptsPerHour = 3 });
        var now = DateTimeOffset.UtcNow;

        Assert.False(filter.ShouldRateLimit(now));
        Assert.False(filter.ShouldRateLimit(now.AddSeconds(1)));
        Assert.False(filter.ShouldRateLimit(now.AddSeconds(2)));
        Assert.True(filter.ShouldRateLimit(now.AddSeconds(3)));
    }

    [Fact]
    public void ShouldRateLimit_SlidingWindow_EvictsOldEntries()
    {
        var filter = CreateFilter(new ChaosSettings { MaxPromptsPerHour = 2 });
        var baseTime = DateTimeOffset.UtcNow;

        Assert.False(filter.ShouldRateLimit(baseTime));
        Assert.False(filter.ShouldRateLimit(baseTime.AddSeconds(1)));
        // 3rd call trips the limit (count > 2)
        Assert.True(filter.ShouldRateLimit(baseTime.AddSeconds(2)));

        // After 1+ hour, old entries are evicted; the window now has only the new call
        Assert.False(filter.ShouldRateLimit(baseTime.AddHours(1).AddSeconds(10)));
        Assert.False(filter.ShouldRateLimit(baseTime.AddHours(1).AddSeconds(11)));
        // 3rd call within the new window trips it again
        Assert.True(filter.ShouldRateLimit(baseTime.AddHours(1).AddSeconds(12)));
    }

    [Fact]
    public void ShouldRateLimit_DisabledWhenZero()
    {
        var filter = CreateFilter(new ChaosSettings { MaxPromptsPerHour = 0 });
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 100; i++)
        {
            Assert.False(filter.ShouldRateLimit(now.AddSeconds(i)));
        }
    }

    [Fact]
    public void ShouldRateLimit_ThreadSafe_NoCrash()
    {
        var filter = CreateFilter(new ChaosSettings { MaxPromptsPerHour = 50 });
        var now = DateTimeOffset.UtcNow;

        // Hammer from multiple threads to verify no exceptions
        Parallel.For(0, 200, i =>
        {
            filter.ShouldRateLimit(now.AddMilliseconds(i));
        });
    }

    // ── Ban word scrubbing ─────────────────────────────────────────

    [Fact]
    public void ScrubBannedContent_ReplacesMatchesCaseInsensitively()
    {
        var filter = CreateFilter(new ChaosSettings { BanWords = ["badword", "SECRET"] });

        Assert.Equal("hello ***", filter.ScrubBannedContent("hello badword"));
        Assert.Equal("hello ***", filter.ScrubBannedContent("hello BADWORD"));
        Assert.Equal("my *** value", filter.ScrubBannedContent("my Secret value"));
    }

    [Fact]
    public void ScrubBannedContent_ReplacesMultipleOccurrences()
    {
        var filter = CreateFilter(new ChaosSettings { BanWords = ["x"] });

        Assert.Equal("a *** b *** c", filter.ScrubBannedContent("a x b x c"));
    }

    [Fact]
    public void ScrubBannedContent_NoBanWords_ReturnsOriginal()
    {
        var filter = CreateFilter(new ChaosSettings { BanWords = [] });

        Assert.Equal("hello world", filter.ScrubBannedContent("hello world"));
    }

    [Fact]
    public void ScrubBannedContent_WhitespaceOnlyBanWords_ReturnsOriginal()
    {
        var filter = CreateFilter(new ChaosSettings { BanWords = ["", " ", "  "] });

        Assert.Equal("hello world", filter.ScrubBannedContent("hello world"));
    }

    [Fact]
    public void ScrubBannedContent_RegexSpecialChars_AreEscaped()
    {
        var filter = CreateFilter(new ChaosSettings { BanWords = ["foo.bar", "a+b"] });

        // Should not match "fooXbar" — the dot is escaped
        Assert.Equal("fooXbar", filter.ScrubBannedContent("fooXbar"));
        Assert.Equal("***", filter.ScrubBannedContent("foo.bar"));
        Assert.Equal("***", filter.ScrubBannedContent("a+b"));
    }
}
