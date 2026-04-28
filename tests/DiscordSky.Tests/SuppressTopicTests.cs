using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

/// <summary>
/// End-to-end test of the suppression + admissible read-path.
/// Uses <see cref="InMemoryUserMemoryStore"/> to avoid disk IO.
/// </summary>
public class SuppressTopicTests
{
    private static (InMemoryUserMemoryStore store, IOptionsMonitor<MemoryRelevanceOptions> options) Build()
    {
        var botOptions = Options.Create(new BotOptions { MaxMemoriesPerUser = 20 });
        var store = new InMemoryUserMemoryStore(botOptions, NullLogger<InMemoryUserMemoryStore>.Instance);
        var options = new TestOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions { SuppressionOverlapThreshold = 0.1 });
        return (store, options);
    }

    [Fact]
    public async Task SuppressTopic_CreatesSuppressedMemory()
    {
        var (store, options) = Build();
        await store.SuppressTopicAsync(42UL, "cats", options);

        var all = await store.GetMemoriesAsync(42UL);
        Assert.Single(all);
        Assert.Equal(MemoryKind.Suppressed, all[0].Kind);
        Assert.Equal("cats", all[0].Content);
    }

    [Fact]
    public async Task SuppressTopic_Idempotent()
    {
        var (store, options) = Build();
        await store.SuppressTopicAsync(42UL, "cats", options);
        await store.SuppressTopicAsync(42UL, "CATS", options);
        await store.SuppressTopicAsync(42UL, "  cats  ", options);

        var all = await store.GetMemoriesAsync(42UL);
        Assert.Single(all);
    }

    [Fact]
    public async Task SuppressTopic_MarksMatchingMemoriesSuperseded()
    {
        var (store, options) = Build();
        await store.SaveMemoryAsync(42UL, "has two cats named whiskers and pepper", "pets talk", MemoryKind.Factual, new[] { "pets" });
        await store.SuppressTopicAsync(42UL, "cats", options);

        var admissible = await store.GetAdmissibleMemoriesAsync(42UL, options);
        Assert.Empty(admissible);

        var all = await store.GetMemoriesAsync(42UL);
        Assert.Contains(all, m => m.Kind == MemoryKind.Factual && m.Superseded);
    }

    [Fact]
    public async Task GetAdmissible_HidesSuppressed()
    {
        var (store, options) = Build();
        await store.SaveMemoryAsync(42UL, "works as a pilot", "career", MemoryKind.Factual, null);
        await store.SuppressTopicAsync(42UL, "cats", options);

        var admissible = await store.GetAdmissibleMemoriesAsync(42UL, options);
        Assert.Single(admissible);
        Assert.Equal("works as a pilot", admissible[0].Content);
    }

    [Fact]
    public async Task Suppressed_DoesNotCountTowardCap()
    {
        var botOptions = Options.Create(new BotOptions { MaxMemoriesPerUser = 2 });
        var store = new InMemoryUserMemoryStore(botOptions, NullLogger<InMemoryUserMemoryStore>.Instance);
        var options = new TestOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions());

        await store.SaveMemoryAsync(1UL, "fact one", "ctx", MemoryKind.Factual, null);
        await store.SaveMemoryAsync(1UL, "fact two", "ctx", MemoryKind.Factual, null);
        await store.SuppressTopicAsync(1UL, "ex", options);
        await store.SuppressTopicAsync(1UL, "politics", options);

        var all = await store.GetMemoriesAsync(1UL);
        // 2 factual + 2 suppressed; the two factual must still be present (cap didn't evict them).
        Assert.Equal(2, all.Count(m => m.Kind == MemoryKind.Factual));
        Assert.Equal(2, all.Count(m => m.Kind == MemoryKind.Suppressed));
    }
}
