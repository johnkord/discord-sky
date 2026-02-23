using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class InMemoryUserMemoryStoreTests
{
    private static InMemoryUserMemoryStore CreateStore(int maxPerUser = 20)
    {
        var options = Options.Create(new BotOptions { MaxMemoriesPerUser = maxPerUser });
        return new InMemoryUserMemoryStore(options, NullLogger<InMemoryUserMemoryStore>.Instance);
    }

    [Fact]
    public async Task GetMemories_Empty_ReturnsEmptyList()
    {
        var store = CreateStore();
        var memories = await store.GetMemoriesAsync(123);
        Assert.Empty(memories);
    }

    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "Likes cats", "mentioned pets");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("Likes cats", memories[0].Content);
        Assert.Equal("mentioned pets", memories[0].Context);
        Assert.Equal(0, memories[0].ReferenceCount);
    }

    [Fact]
    public async Task SaveDuplicate_UpdatesExistingMemory()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "Likes cats", "context-1");
        await store.SaveMemoryAsync(42, "Likes cats", "context-2");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("context-2", memories[0].Context);
        Assert.Equal(1, memories[0].ReferenceCount);
    }

    [Fact]
    public async Task SaveDuplicate_CaseInsensitive()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "Likes Cats", "context-1");
        await store.SaveMemoryAsync(42, "likes cats", "context-2");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        // Updated content uses the new casing
        Assert.Equal("likes cats", memories[0].Content);
    }

    [Fact]
    public async Task Save_EvictsLRU_WhenAtCap()
    {
        var store = CreateStore(maxPerUser: 2);

        await store.SaveMemoryAsync(1, "fact-A", "ctx-A");
        await store.SaveMemoryAsync(1, "fact-B", "ctx-B");

        // Touch fact-B so fact-A is the LRU
        await store.TouchMemoriesAsync(1);

        // Saving a third should evict fact-A (oldest LastReferencedAt)
        // But TouchMemoriesAsync touches all, so we need a different approach.
        // Instead, save sequentially — fact-A was saved first, so its LastReferencedAt is oldest.
        var store2 = CreateStore(maxPerUser: 2);
        await store2.SaveMemoryAsync(1, "oldest", "ctx");
        await Task.Delay(10); // Ensure different timestamps
        await store2.SaveMemoryAsync(1, "newer", "ctx");
        await Task.Delay(10);
        await store2.SaveMemoryAsync(1, "newest", "ctx");

        var memories = await store2.GetMemoriesAsync(1);
        Assert.Equal(2, memories.Count);
        Assert.DoesNotContain(memories, m => m.Content == "oldest");
        Assert.Contains(memories, m => m.Content == "newer");
        Assert.Contains(memories, m => m.Content == "newest");
    }

    [Fact]
    public async Task UpdateMemory_ChangesContentAndContext()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "Lives in Alaska", "initial");

        await store.UpdateMemoryAsync(42, 0, "Lives in Canada", "corrected");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("Lives in Canada", memories[0].Content);
        Assert.Equal("corrected", memories[0].Context);
    }

    [Fact]
    public async Task UpdateMemory_OutOfRange_NoOp()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact", "ctx");

        // Should not throw
        await store.UpdateMemoryAsync(42, 5, "new content", "new ctx");
        await store.UpdateMemoryAsync(42, -1, "new content", "new ctx");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("fact", memories[0].Content);
    }

    [Fact]
    public async Task UpdateMemory_UnknownUser_NoOp()
    {
        var store = CreateStore();
        await store.UpdateMemoryAsync(999, 0, "content", "ctx");
        // No exception, no crash
    }

    [Fact]
    public async Task ForgetMemory_RemovesAtIndex()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact-A", "ctx");
        await store.SaveMemoryAsync(42, "fact-B", "ctx");
        await store.SaveMemoryAsync(42, "fact-C", "ctx");

        await store.ForgetMemoryAsync(42, 1); // Remove fact-B

        var memories = await store.GetMemoriesAsync(42);
        Assert.Equal(2, memories.Count);
        Assert.Equal("fact-A", memories[0].Content);
        Assert.Equal("fact-C", memories[1].Content);
    }

    [Fact]
    public async Task ForgetMemory_OutOfRange_NoOp()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact", "ctx");

        await store.ForgetMemoryAsync(42, 5);
        await store.ForgetMemoryAsync(42, -1);

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
    }

    [Fact]
    public async Task ForgetAll_ClearsUserMemories()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact-A", "ctx");
        await store.SaveMemoryAsync(42, "fact-B", "ctx");

        await store.ForgetAllAsync(42);

        var memories = await store.GetMemoriesAsync(42);
        Assert.Empty(memories);
    }

    [Fact]
    public async Task ForgetAll_DoesNotAffectOtherUsers()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "user-42-fact", "ctx");
        await store.SaveMemoryAsync(99, "user-99-fact", "ctx");

        await store.ForgetAllAsync(42);

        Assert.Empty(await store.GetMemoriesAsync(42));
        Assert.Single(await store.GetMemoriesAsync(99));
    }

    [Fact]
    public async Task ForgetAll_UnknownUser_NoOp()
    {
        var store = CreateStore();
        await store.ForgetAllAsync(999);
        // No exception
    }

    [Fact]
    public async Task TouchMemories_IncreasesReferenceCount()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact", "ctx");

        var before = (await store.GetMemoriesAsync(42))[0];
        Assert.Equal(0, before.ReferenceCount);

        await store.TouchMemoriesAsync(42);

        var after = (await store.GetMemoriesAsync(42))[0];
        Assert.Equal(1, after.ReferenceCount);
    }

    [Fact]
    public async Task TouchMemories_UpdatesLastReferencedAt()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact", "ctx");

        var before = (await store.GetMemoriesAsync(42))[0].LastReferencedAt;
        await Task.Delay(10);
        await store.TouchMemoriesAsync(42);

        var after = (await store.GetMemoriesAsync(42))[0].LastReferencedAt;
        Assert.True(after > before);
    }

    [Fact]
    public async Task GetMemories_ReturnsSnapshot_NotLiveReference()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact-A", "ctx");

        var snapshot = await store.GetMemoriesAsync(42);
        await store.SaveMemoryAsync(42, "fact-B", "ctx");

        // Snapshot should still have only 1 item
        Assert.Single(snapshot);
    }

    [Fact]
    public async Task MultipleUsers_Isolated()
    {
        var store = CreateStore();
        await store.SaveMemoryAsync(1, "user-1-fact", "ctx");
        await store.SaveMemoryAsync(2, "user-2-fact", "ctx");

        var user1 = await store.GetMemoriesAsync(1);
        var user2 = await store.GetMemoriesAsync(2);

        Assert.Single(user1);
        Assert.Equal("user-1-fact", user1[0].Content);
        Assert.Single(user2);
        Assert.Equal("user-2-fact", user2[0].Content);
    }
}
