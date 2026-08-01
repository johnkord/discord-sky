using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class FileBackedUserMemoryStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileBackedUserMemoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "discord-sky-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private FileBackedUserMemoryStore CreateStore(int maxPerUser = 20)
    {
        var options = Options.Create(new BotOptions
        {
            MaxMemoriesPerUser = maxPerUser,
            MemoryDataPath = _tempDir
        });
        return new FileBackedUserMemoryStore(options, NullLogger<FileBackedUserMemoryStore>.Instance);
    }

    [Fact]
    public async Task GetMemories_Empty_ReturnsEmptyList()
    {
        using var store = CreateStore();
        var memories = await store.GetMemoriesAsync(123);
        Assert.Empty(memories);
    }

    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "Likes cats", "mentioned pets");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("Likes cats", memories[0].Content);
        Assert.Equal("mentioned pets", memories[0].Context);
    }

    [Fact]
    public async Task SaveDuplicate_UpdatesExistingMemory()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "Likes cats", "context-1");
        await store.SaveMemoryAsync(42, "Likes cats", "context-2");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("context-2", memories[0].Context);
        Assert.Equal(1, memories[0].ReferenceCount);
    }

    [Fact]
    public async Task Save_EvictsLRU_WhenAtCap()
    {
        using var store = CreateStore(maxPerUser: 2);

        await store.SaveMemoryAsync(1, "oldest", "ctx");
        await Task.Delay(10);
        await store.SaveMemoryAsync(1, "newer", "ctx");
        await Task.Delay(10);
        await store.SaveMemoryAsync(1, "newest", "ctx");

        var memories = await store.GetMemoriesAsync(1);
        Assert.Equal(2, memories.Count);
        Assert.DoesNotContain(memories, m => m.Content == "oldest");
        Assert.Contains(memories, m => m.Content == "newer");
        Assert.Contains(memories, m => m.Content == "newest");
    }

    [Fact]
    public async Task UpdateMemory_ChangesContentAndContext()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "Lives in Alaska", "initial");

        await store.UpdateMemoryAsync(42, 0, "Lives in Canada", "corrected");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("Lives in Canada", memories[0].Content);
    }

    [Fact]
    public async Task UpdateMemory_OutOfRange_NoOp()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact", "ctx");

        await store.UpdateMemoryAsync(42, 5, "new content", "new ctx");
        await store.UpdateMemoryAsync(42, -1, "new content", "new ctx");

        var memories = await store.GetMemoriesAsync(42);
        Assert.Equal("fact", memories[0].Content);
    }

    [Fact]
    public async Task ForgetMemory_RemovesAtIndex()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact-A", "ctx");
        await store.SaveMemoryAsync(42, "fact-B", "ctx");

        await store.ForgetMemoryAsync(42, 0);

        var memories = await store.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("fact-B", memories[0].Content);
    }

    [Fact]
    public async Task ForgetAll_ClearsAndDeletesFile()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact-A", "ctx");
        store.FlushAll(); // Ensure file exists on disk

        var filePath = Path.Combine(_tempDir, "42.json");
        Assert.True(File.Exists(filePath));

        await store.ForgetAllAsync(42);

        Assert.False(File.Exists(filePath));
        Assert.Empty(await store.GetMemoriesAsync(42));
    }

    [Fact]
    public async Task TouchMemories_IsNoOp_DoesNotChangeReferenceCount()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "fact", "ctx");

        var before = (await store.GetMemoriesAsync(42))[0];
        Assert.Equal(0, before.ReferenceCount);

        await store.TouchMemoriesAsync(42);

        var after = (await store.GetMemoriesAsync(42))[0];
        Assert.Equal(0, after.ReferenceCount);
    }

    [Fact]
    public async Task MultipleUsers_Isolated()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(1, "user-1-fact", "ctx");
        await store.SaveMemoryAsync(2, "user-2-fact", "ctx");

        var user1 = await store.GetMemoriesAsync(1);
        var user2 = await store.GetMemoriesAsync(2);

        Assert.Single(user1);
        Assert.Equal("user-1-fact", user1[0].Content);
        Assert.Single(user2);
        Assert.Equal("user-2-fact", user2[0].Content);
    }

    // --- Persistence-specific tests ---

    [Fact]
    public async Task FlushAll_WritesFileToDisk()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "persistent fact", "ctx");

        store.FlushAll();

        var filePath = Path.Combine(_tempDir, "42.json");
        Assert.True(File.Exists(filePath));

        var json = await File.ReadAllTextAsync(filePath);
        Assert.Contains("persistent fact", json);
    }

    [Fact]
    public async Task Persistence_SurvivesRestart()
    {
        // First store: save and flush
        using (var store1 = CreateStore())
        {
            await store1.SaveMemoryAsync(42, "Likes pizza", "mentioned food");
            await store1.SaveMemoryAsync(42, "Lives in Canada", "mentioned location");
            store1.FlushAll();
        }

        // Second store: load from same directory
        using var store2 = CreateStore();
        var memories = await store2.GetMemoriesAsync(42);

        Assert.Equal(2, memories.Count);
        Assert.Contains(memories, m => m.Content == "Likes pizza");
        Assert.Contains(memories, m => m.Content == "Lives in Canada");
    }

    [Fact]
    public async Task Persistence_MultipleUsers_SurviveRestart()
    {
        using (var store1 = CreateStore())
        {
            await store1.SaveMemoryAsync(10, "user-10-fact", "ctx");
            await store1.SaveMemoryAsync(20, "user-20-fact", "ctx");
            store1.FlushAll();
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, "10.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "20.json")));

        using var store2 = CreateStore();
        Assert.Single(await store2.GetMemoriesAsync(10));
        Assert.Single(await store2.GetMemoriesAsync(20));
    }

    [Fact]
    public async Task Persistence_UpdateSurvivesRestart()
    {
        using (var store1 = CreateStore())
        {
            await store1.SaveMemoryAsync(42, "Lives in Alaska", "initial");
            await store1.UpdateMemoryAsync(42, 0, "Lives in Canada", "corrected");
            store1.FlushAll();
        }

        using var store2 = CreateStore();
        var memories = await store2.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("Lives in Canada", memories[0].Content);
        Assert.Equal("corrected", memories[0].Context);
    }

    [Fact]
    public async Task Persistence_ForgetSurvivesRestart()
    {
        using (var store1 = CreateStore())
        {
            await store1.SaveMemoryAsync(42, "fact-A", "ctx");
            await store1.SaveMemoryAsync(42, "fact-B", "ctx");
            await store1.ForgetMemoryAsync(42, 0);
            store1.FlushAll();
        }

        using var store2 = CreateStore();
        var memories = await store2.GetMemoriesAsync(42);
        Assert.Single(memories);
        Assert.Equal("fact-B", memories[0].Content);
    }

    [Fact]
    public async Task Persistence_ForgetAllSurvivesRestart()
    {
        using (var store1 = CreateStore())
        {
            await store1.SaveMemoryAsync(42, "fact", "ctx");
            store1.FlushAll();
            await store1.ForgetAllAsync(42);
        }

        using var store2 = CreateStore();
        var memories = await store2.GetMemoriesAsync(42);
        Assert.Empty(memories);
    }

    [Fact]
    public async Task Persistence_CorruptFile_RecoversGracefully()
    {
        // Write corrupt JSON to the user's file
        Directory.CreateDirectory(_tempDir);
        var filePath = Path.Combine(_tempDir, "42.json");
        await File.WriteAllTextAsync(filePath, "NOT VALID JSON {{{");

        using var store = CreateStore();
        var memories = await store.GetMemoriesAsync(42);

        // Should recover with empty list, not throw
        Assert.Empty(memories);

        // Should be able to save new memories on top of the corrupt file
        await store.SaveMemoryAsync(42, "new fact", "ctx");
        Assert.Single(await store.GetMemoriesAsync(42));
    }

    [Fact]
    public async Task Persistence_PreservesTimestamps()
    {
        DateTimeOffset createdAt;
        using (var store1 = CreateStore())
        {
            await store1.SaveMemoryAsync(42, "fact", "ctx");
            createdAt = (await store1.GetMemoriesAsync(42))[0].CreatedAt;
            store1.FlushAll();
        }

        using var store2 = CreateStore();
        var reloaded = (await store2.GetMemoriesAsync(42))[0];

        // CreatedAt should survive serialization round-trip (within 1ms precision)
        Assert.InRange(
            (reloaded.CreatedAt - createdAt).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Persistence_ProvenanceRoundTripsAndVersionZeroRemainsReadable()
    {
        var capturedAt = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        using (var store1 = CreateStore())
        {
            await store1.ReplaceAllMemoriesAsync(42, new[]
            {
                new UserMemory(
                    "Likes meteor showers",
                    "shared preference",
                    capturedAt,
                    capturedAt,
                    0,
                    Provenance: new MemoryProvenance("op-1", capturedAt, new[] { 11UL, 12UL }))
            });
            store1.FlushAll();
        }

        using (var store2 = CreateStore())
        {
            var persisted = Assert.Single(await store2.GetMemoriesAsync(42));
            Assert.Equal("op-1", persisted.Provenance?.OperationId);
            Assert.Equal(new ulong[] { 11, 12 }, persisted.Provenance?.EvidenceMessageIds);
        }

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "43.json"),
            "[{\"content\":\"legacy\",\"context\":\"v0\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"lastReferencedAt\":\"2026-01-01T00:00:00Z\",\"referenceCount\":0}]");
        using var store3 = CreateStore();
        var legacy = Assert.Single(await store3.GetMemoriesAsync(43));
        Assert.Equal("legacy", legacy.Content);
        Assert.Null(legacy.Provenance);
    }

    [Fact]
    public async Task Persistence_LegacyMissingMemoryIdsAreAssignedAndStableAcrossRestart()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "42.json"),
            "[{\"content\":\"legacy-a\",\"context\":\"v0\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"lastReferencedAt\":\"2026-01-01T00:00:00Z\",\"referenceCount\":0},{\"content\":\"legacy-b\",\"context\":\"v0\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"lastReferencedAt\":\"2026-01-01T00:00:00Z\",\"referenceCount\":0}]");

        string[] assigned;
        using (var first = CreateStore())
        {
            assigned = (await first.GetMemoriesAsync(42)).Select(memory => memory.MemoryId!).ToArray();
            Assert.All(assigned, id => Assert.Matches("^[0-9a-f]{32}$", id));
            Assert.Equal(2, assigned.Distinct().Count());
            Assert.True(first.IsDirty(42));
            first.FlushAll();
        }

        using var second = CreateStore();
        var restored = (await second.GetMemoriesAsync(42)).Select(memory => memory.MemoryId).ToArray();
        Assert.Equal(assigned, restored);
        Assert.False(second.IsDirty(42));
    }

    [Fact]
    public async Task Persistence_DuplicateMemoryIdsAreRepaired()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "42.json"),
            "[{\"content\":\"a\",\"context\":\"ctx\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"lastReferencedAt\":\"2026-01-01T00:00:00Z\",\"referenceCount\":0,\"memoryId\":\"same\"},{\"content\":\"b\",\"context\":\"ctx\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"lastReferencedAt\":\"2026-01-01T00:00:00Z\",\"referenceCount\":0,\"memoryId\":\"same\"}]");

        using var store = CreateStore();
        var memories = await store.GetMemoriesAsync(42);

        Assert.Equal("same", memories[0].MemoryId);
        Assert.NotEqual("same", memories[1].MemoryId);
        Assert.Equal(2, memories.Select(memory => memory.MemoryId).Distinct().Count());
        Assert.True(store.IsDirty(42));
    }

    [Fact]
    public async Task SaveAndReplace_AlwaysProduceUniqueMemoryIds()
    {
        using var store = CreateStore();
        await store.SaveMemoryAsync(42, "created", "ctx");
        var created = Assert.Single(await store.GetMemoriesAsync(42));
        Assert.Matches("^[0-9a-f]{32}$", created.MemoryId);

        await store.ReplaceAllMemoriesAsync(42, new[]
        {
            created,
            new UserMemory("missing", "ctx", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
            created with { Content = "duplicate-id" },
        });

        var replaced = await store.GetMemoriesAsync(42);
        Assert.Equal(3, replaced.Select(memory => memory.MemoryId).Distinct().Count());
        Assert.All(replaced, memory => Assert.False(string.IsNullOrWhiteSpace(memory.MemoryId)));
    }

    [Fact]
    public async Task FlushFailure_PreservesLastValidSnapshotAndRetriesDirtyUser()
    {
        using (var seed = CreateStore())
        {
            await seed.SaveMemoryAsync(42, "old value", "ctx");
            seed.FlushAll();
        }
        var filePath = Path.Combine(_tempDir, "42.json");
        var validBefore = await File.ReadAllTextAsync(filePath);
        var calls = 0;
        var options = Options.Create(new BotOptions
        {
            MaxMemoriesPerUser = 20,
            MemoryDataPath = _tempDir,
        });
        using var store = new FileBackedUserMemoryStore(
            options,
            NullLogger<FileBackedUserMemoryStore>.Instance,
            (tempPath, targetPath, json) =>
            {
                calls++;
                File.WriteAllText(tempPath, json);
                if (calls == 1) throw new IOException("simulated rename failure");
                File.Move(tempPath, targetPath, overwrite: true);
            });
        await store.UpdateMemoryAsync(42, 0, "new value", "ctx");

        store.FlushAll();

        Assert.Equal(validBefore, await File.ReadAllTextAsync(filePath));
        Assert.True(store.IsDirty(42));
        using (var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(filePath)))
        {
            Assert.Equal("old value", document.RootElement[0].GetProperty("content").GetString());
        }

        store.FlushAll();

        Assert.False(store.IsDirty(42));
        using var updated = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(filePath));
        Assert.Equal("new value", updated.RootElement[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Dispose_FlushesBeforeShutdown()
    {
        var store = CreateStore();
        store.SaveMemoryAsync(42, "unflushed fact", "ctx").GetAwaiter().GetResult();

        // File should NOT exist yet (no manual flush)
        var filePath = Path.Combine(_tempDir, "42.json");
        // Timer hasn't fired yet in 60s, so file shouldn't be there
        // (this is a best-effort check — timing-dependent)

        // Dispose should trigger final flush
        store.Dispose();

        Assert.True(File.Exists(filePath));
        var json = File.ReadAllText(filePath);
        Assert.Contains("unflushed fact", json);
    }

    [Fact]
    public void Constructor_CreatesDataDirectory()
    {
        var nestedDir = Path.Combine(_tempDir, "nested", "deep");
        Assert.False(Directory.Exists(nestedDir));

        var options = Options.Create(new BotOptions
        {
            MaxMemoriesPerUser = 20,
            MemoryDataPath = nestedDir
        });

        using var store = new FileBackedUserMemoryStore(options, NullLogger<FileBackedUserMemoryStore>.Instance);
        Assert.True(Directory.Exists(nestedDir));
    }
}
