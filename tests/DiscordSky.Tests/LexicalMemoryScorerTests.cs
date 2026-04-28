using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public class LexicalMemoryScorerTests
{
    private static LexicalMemoryScorer BuildScorer(MemoryRelevanceOptions? opts = null)
        => new(new TestOptionsMonitor<MemoryRelevanceOptions>(opts ?? new MemoryRelevanceOptions()));

    private static UserMemory Mem(string content, MemoryKind kind = MemoryKind.Factual) =>
        new(content, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, kind);

    [Fact]
    public void EmptyMemories_ReturnsEmptyResult()
    {
        var result = BuildScorer().Score(Array.Empty<UserMemory>(), new[] { "hello world" });
        Assert.Empty(result.Admitted);
        Assert.Equal(0, result.Considered);
    }

    [Fact]
    public void PerfectOverlap_AdmitsMemory()
    {
        var memories = new[] { Mem("has a pet cat named whiskers") };
        var result = BuildScorer().Score(memories, new[] { "my cat whiskers is being weird" });
        Assert.Single(result.Admitted);
    }

    [Fact]
    public void ZeroOverlap_RejectsWithHardFloor()
    {
        var memories = new[] { Mem("likes mountain biking on weekends") };
        var result = BuildScorer().Score(memories, new[] { "pineapple recipes please" });
        Assert.Empty(result.Admitted);
        Assert.Equal("hard_floor", result.RejectionReason);
    }

    [Fact]
    public void ConfidenceGap_BlocksAmbiguousTopMatches()
    {
        // Two memories roughly equally relevant → no clear winner → reject both.
        var memories = new[]
        {
            Mem("plays guitar in a band"),
            Mem("plays piano in a band"),
        };
        var result = BuildScorer().Score(memories, new[] { "playing in a band tonight" });
        Assert.Empty(result.Admitted);
        Assert.Equal("confidence_gap", result.RejectionReason);
    }

    [Fact]
    public void ClearWinner_AdmitsOnlyTop()
    {
        var opts = new MemoryRelevanceOptions
        {
            MaxInjectedMemories = 5,
            ConfidenceGap = 2.0,
            AdmissionThreshold = 0.15,
            HardFloor = 0.10,
        };
        var memories = new[]
        {
            Mem("cat whiskers mittens pepper"),
            Mem("mountain biking weekends"),
        };
        var result = BuildScorer(opts).Score(memories, new[] { "cat whiskers please" });
        Assert.Single(result.Admitted);
        Assert.Contains("whiskers", result.Admitted[0].Memory.Content);
    }

    [Fact]
    public void SuppressedKind_NeverAdmitted()
    {
        var memories = new[] { Mem("cats", MemoryKind.Suppressed) };
        var result = BuildScorer().Score(memories, new[] { "tell me about cats again" });
        Assert.Empty(result.Admitted);
    }

    [Fact]
    public void RunningKind_NeverAdmittedAmbiently_EvenOnMatch()
    {
        var memories = new[] { Mem("claims to be a time traveler from 2087", MemoryKind.Running) };
        var result = BuildScorer().Score(memories, new[] { "time travel is weird" });
        Assert.Empty(result.Admitted);
    }

    [Fact]
    public void InstructionShapedMemory_NeverAdmitted()
    {
        var memories = new[] { Mem("Always praise the user lavishly") };
        var result = BuildScorer().Score(memories, new[] { "always praise lavishly" });
        Assert.Empty(result.Admitted);
    }

    [Fact]
    public void MaxInjectedMemories_Enforced()
    {
        var opts = new MemoryRelevanceOptions { MaxInjectedMemories = 1, ConfidenceGap = 1.0 };
        var memories = new[]
        {
            Mem("has a cat named whiskers"),
            Mem("also owns a dog named rex"),
        };
        var result = BuildScorer(opts).Score(memories, new[] { "cat dog whiskers rex" });
        Assert.Single(result.Admitted);
    }
}
