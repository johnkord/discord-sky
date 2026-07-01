using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Memory.Reception;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public class ReactionSentimentTests
{
    [Theory]
    [InlineData("\U0001F923", 1)]  // rolling laugh
    [InlineData("\U0001F602", 1)]  // tears of joy
    [InlineData("\U0001F525", 1)]  // fire
    [InlineData("\U0001F480", 1)]  // skull ("I'm dead", laughter here)
    [InlineData("hy_sobs", 1)]     // custom name contains "sob"
    [InlineData("Roblox_MADwithJOY", 1)] // contains "joy"
    [InlineData("\U0001F44E", -1)] // thumbs down
    [InlineData("cringe_pepe", -1)]
    [InlineData("waitwhat", 0)]
    [InlineData("", 0)]
    public void Score_ClassifiesEmote(string emote, int expected)
    {
        Assert.Equal(expected, ReactionSentiment.Score(emote));
    }

    [Fact]
    public void Score_Null_IsNeutral()
    {
        Assert.Equal(0, ReactionSentiment.Score(null));
    }
}

public class GreatestHitsTests
{
    private static ReactionEvent React(ulong messageId, string emote, string excerpt, string action = "add")
        => new(DateTimeOffset.UtcNow, action, emote, ReactorUserId: 1, ChannelId: 2, GuildId: 3, messageId, "Robotnik from AOSTH", excerpt);

    [Fact]
    public void Rank_OrdersByNetSentiment_AndKeepsExcerpt()
    {
        var events = new[]
        {
            React(10, "\U0001F923", "the two-laugh line about your posture"),
            React(10, "\U0001F602", "the two-laugh line about your posture"),
            React(20, "\U0001F923", "the single-laugh line about your cat"),
            React(30, "\U0001F44E", "the flop line nobody enjoyed at all"),
        };

        var ranked = GreatestHits.Rank(events);

        Assert.Equal(3, ranked.Count);
        Assert.Equal("the two-laugh line about your posture", ranked[0].Excerpt);
        Assert.Equal(2, ranked[0].Score);
        Assert.Equal("the single-laugh line about your cat", ranked[1].Excerpt);
        Assert.Equal(-1, ranked[^1].Score);
    }

    [Fact]
    public void Rank_RemoveCancelsAdd()
    {
        var events = new[]
        {
            React(10, "\U0001F923", "a line that got a laugh then a takeback"),
            React(10, "\U0001F923", "a line that got a laugh then a takeback", action: "remove"),
        };

        var top = GreatestHits.TopHits(events, 5);
        Assert.Empty(top); // net score 0, not a positive hit
    }

    [Fact]
    public void Rank_IgnoresNeutralAndShortExcerpts()
    {
        var events = new[]
        {
            React(10, "waitwhat", "a perfectly long excerpt that is neutral"), // neutral emote
            React(20, "\U0001F923", "short"),                                  // below min length
        };

        Assert.Empty(GreatestHits.Rank(events));
    }

    [Fact]
    public void TopHits_ReturnsRequestedCount()
    {
        var events = new[]
        {
            React(1, "\U0001F923", "first winning line about the hedgehog"),
            React(2, "\U0001F923", "second winning line about the badniks"),
            React(3, "\U0001F923", "third winning line about the peasants"),
        };

        Assert.Equal(2, GreatestHits.TopHits(events, 2).Count);
    }

    [Fact]
    public void BuildDirective_EmptyIsNull()
    {
        Assert.Null(GreatestHits.BuildDirective(Array.Empty<string>()));
    }

    [Fact]
    public void BuildDirective_IncludesHitsAndFraming()
    {
        var directive = GreatestHits.BuildDirective(new[] { "you have the charisma of a damp sock" });
        Assert.NotNull(directive);
        Assert.Contains("damp sock", directive);
        Assert.Contains("laughs", directive);
    }
}

public class GreatestHitsCacheTests
{
    private sealed class SeqRng : IRandomProvider
    {
        private readonly double[] _values;
        private int _i;
        public SeqRng(params double[] values) => _values = values;
        public double NextDouble() => _values[_i++ % _values.Length];
    }

    [Fact]
    public void Sample_ReturnsAll_WhenPoolSmallerThanN()
    {
        var cache = new GreatestHitsCache();
        cache.Set(new[] { "a", "b" });
        Assert.Equal(2, cache.Sample(new SeqRng(0.0), 5).Count);
    }

    [Fact]
    public void Sample_ReturnsDistinctSubset()
    {
        var cache = new GreatestHitsCache();
        cache.Set(new[] { "a", "b", "c", "d", "e" });
        var sample = cache.Sample(new SeqRng(0.0, 0.5, 0.9), 3);
        Assert.Equal(3, sample.Count);
        Assert.Equal(sample.Distinct().Count(), sample.Count);
    }

    [Fact]
    public void Sample_EmptyPool_IsEmpty()
    {
        Assert.Empty(new GreatestHitsCache().Sample(new SeqRng(0.0), 3));
    }
}

public class RobotnikReactionsTests
{
    private sealed class FixedRng : IRandomProvider
    {
        private readonly double _value;
        public FixedRng(double value) => _value = value;
        public double NextDouble() => _value;
    }

    [Fact]
    public void Pick_FirstOfPalette_IsEgg()
    {
        Assert.Equal("\U0001F95A", RobotnikReactions.Pick(new FixedRng(0.0)));
    }

    [Fact]
    public void Pick_TopOfRange_StaysInBounds()
    {
        Assert.False(string.IsNullOrEmpty(RobotnikReactions.Pick(new FixedRng(0.999999))));
    }
}

public class DeterministicConsolidationTests
{
    private static UserMemory Mem(string content, int importance, DateTimeOffset? lastRef = null)
        => new(content, "ctx", DateTimeOffset.UtcNow.AddDays(-10), lastRef ?? DateTimeOffset.UtcNow, 0, Importance: importance);

    [Fact]
    public void DeterministicConsolidate_KeepsHighestAmmoFirst()
    {
        var memories = new[]
        {
            Mem("dull", 1),
            Mem("gold", 9),
            Mem("meh", 3),
            Mem("great", 7),
            Mem("trivial", 2),
        };

        var kept = CreativeOrchestrator.DeterministicConsolidate(memories, 2);

        Assert.Equal(2, kept.Count);
        Assert.Equal("gold", kept[0].Content);
        Assert.Equal("great", kept[1].Content);
    }

    [Fact]
    public void DeterministicConsolidate_TieBreaksOnRecency()
    {
        var older = Mem("older", 5, DateTimeOffset.UtcNow.AddDays(-5));
        var newer = Mem("newer", 5, DateTimeOffset.UtcNow);

        var kept = CreativeOrchestrator.DeterministicConsolidate(new[] { older, newer }, 1);

        Assert.Single(kept);
        Assert.Equal("newer", kept[0].Content);
    }

    [Fact]
    public void DeterministicConsolidate_RespectsTargetFloorOfOne()
    {
        var kept = CreativeOrchestrator.DeterministicConsolidate(new[] { Mem("a", 1), Mem("b", 2) }, 0);
        Assert.Single(kept);
    }
}
