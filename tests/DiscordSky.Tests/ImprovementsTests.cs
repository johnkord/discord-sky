using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

/// <summary>
/// Coverage for the 2026-06-10 improvement batch: adaptive ambient chance (F7), BM25 + recency +
/// importance recall ranking (F2), recall write-path touch (F4), and consolidation JSON extraction (F6).
/// See docs/improvement_opportunities_2026-06-10.md.
/// </summary>
public class ImprovementsTests
{
    // ── F7: adaptive ambient chance ───────────────────────────────────

    [Fact]
    public void AmbientChance_ZeroBase_StaysZero()
    {
        Assert.Equal(0.0, DiscordBotService.ComputeEffectiveAmbientChance(0.0, "anything", false, false));
    }

    [Fact]
    public void AmbientChance_BotSpokeRecently_LowersChance()
    {
        var baseline = DiscordBotService.ComputeEffectiveAmbientChance(0.25, "a normal message here", false, false);
        var recent = DiscordBotService.ComputeEffectiveAmbientChance(0.25, "a normal message here", botSpokeRecently: true, mentionsBot: false);
        Assert.True(recent < baseline);
    }

    [Fact]
    public void AmbientChance_MentioningBot_RaisesChance()
    {
        var baseline = DiscordBotService.ComputeEffectiveAmbientChance(0.25, "a normal message here", false, false);
        var mentioned = DiscordBotService.ComputeEffectiveAmbientChance(0.25, "a normal message here", botSpokeRecently: false, mentionsBot: true);
        Assert.True(mentioned > baseline);
    }

    [Fact]
    public void AmbientChance_ShortMessage_LowersChance()
    {
        var shortMsg = DiscordBotService.ComputeEffectiveAmbientChance(0.25, "k", false, false);
        Assert.True(shortMsg < 0.25);
    }

    [Fact]
    public void AmbientChance_NeverExceedsCap()
    {
        // Long question mentioning the bot stacks every boost; must still clamp to <= 0.9.
        var stacked = DiscordBotService.ComputeEffectiveAmbientChance(
            0.9, new string('x', 120) + " what do you think?", botSpokeRecently: false, mentionsBot: true);
        Assert.True(stacked <= 0.9);
    }

    // ── F2: BM25 + recency + importance ranking ───────────────────────

    private static LexicalMemoryScorer BuildScorer() =>
        new(new TestOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions()));

    private static UserMemory Mem(string content, DateTimeOffset? lastRef = null, int? importance = null) =>
        new(content, "ctx", DateTimeOffset.UtcNow.AddDays(-30),
            lastRef ?? DateTimeOffset.UtcNow.AddDays(-1), 0, MemoryKind.Factual, null, false, importance);

    [Fact]
    public void RankForRecall_Empty_ReturnsEmpty()
    {
        var ranked = BuildScorer().RankForRecall(Array.Empty<UserMemory>(), "cats", DateTimeOffset.UtcNow);
        Assert.Empty(ranked);
    }

    [Fact]
    public void RankForRecall_QueryMatch_RanksRelevantFirst()
    {
        var memories = new[]
        {
            Mem("loves hiking in the mountains every weekend"),
            Mem("has a pet cat named whiskers"),
            Mem("works as an accountant downtown"),
        };
        var ranked = BuildScorer().RankForRecall(memories, "my cat is being weird today", DateTimeOffset.UtcNow);
        Assert.Contains("cat", ranked[0].Memory.Content);
    }

    [Fact]
    public void RankForRecall_NoQuery_RanksMoreRecentFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var memories = new[]
        {
            Mem("older fact", lastRef: now.AddDays(-90)),
            Mem("fresher fact", lastRef: now.AddDays(-1)),
        };
        var ranked = BuildScorer().RankForRecall(memories, query: null, asOf: now);
        Assert.Equal("fresher fact", ranked[0].Memory.Content);
    }

    [Fact]
    public void RankForRecall_NoQuery_ImportanceBreaksRecencyTie()
    {
        var now = DateTimeOffset.UtcNow;
        var sameTime = now.AddDays(-5);
        var memories = new[]
        {
            Mem("trivial detail", lastRef: sameTime, importance: 2),
            Mem("identity-defining fact", lastRef: sameTime, importance: 9),
        };
        var ranked = BuildScorer().RankForRecall(memories, query: null, asOf: now);
        Assert.Equal("identity-defining fact", ranked[0].Memory.Content);
    }

    // ── F4: recall write-path touch ───────────────────────────────────

    private static InMemoryUserMemoryStore BuildStore() =>
        new(Options.Create(new BotOptions { MaxMemoriesPerUser = 20 }), NullLogger<InMemoryUserMemoryStore>.Instance);

    [Fact]
    public async Task TouchMemories_BumpsReferenceCountAndTimestamp()
    {
        var store = BuildStore();
        await store.SaveMemoryAsync(1, "has a pet cat", "ctx");
        var before = (await store.GetMemoriesAsync(1)).Single();

        await Task.Delay(5);
        await store.TouchMemoriesAsync(1, new[] { "has a pet cat" });

        var after = (await store.GetMemoriesAsync(1)).Single();
        Assert.Equal(before.ReferenceCount + 1, after.ReferenceCount);
        Assert.True(after.LastReferencedAt >= before.LastReferencedAt);
    }

    [Fact]
    public async Task TouchMemories_UnknownContent_NoChange()
    {
        var store = BuildStore();
        await store.SaveMemoryAsync(1, "has a pet cat", "ctx");
        await store.TouchMemoriesAsync(1, new[] { "totally unrelated" });

        var after = (await store.GetMemoriesAsync(1)).Single();
        Assert.Equal(0, after.ReferenceCount);
    }

    // ── F6: consolidation JSON extraction robustness ──────────────────

    [Fact]
    public void ExtractJsonObject_StripsCodeFences()
    {
        var fenced = "```json\n{\"memories\":[]}\n```";
        var result = CreativeOrchestrator.ExtractJsonObject(fenced);
        Assert.Equal("{\"memories\":[]}", result);
    }

    [Fact]
    public void ExtractJsonObject_StripsSurroundingProse()
    {
        var prose = "Sure! Here is the result:\n{\"memories\":[]}\nHope that helps.";
        var result = CreativeOrchestrator.ExtractJsonObject(prose);
        Assert.Equal("{\"memories\":[]}", result);
    }

    [Fact]
    public void ExtractJsonObject_NoObject_ReturnsNull()
    {
        Assert.Null(CreativeOrchestrator.ExtractJsonObject("not valid json {{{"));
        Assert.Null(CreativeOrchestrator.ExtractJsonObject(""));
    }
}
