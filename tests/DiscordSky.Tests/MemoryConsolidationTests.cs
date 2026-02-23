using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class MemoryConsolidationTests
{
    // ── BuildConsolidationPrompt ──────────────────────────────────────

    [Fact]
    public void BuildConsolidationPrompt_IncludesAllMemories()
    {
        var memories = new List<UserMemory>
        {
            new("Likes cats", "pets", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow, 3),
            new("Works as a developer", "career", DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow, 1),
            new("Lives in Canada", "location", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 2),
        };

        var prompt = CreativeOrchestrator.BuildConsolidationPrompt(memories, 2);

        Assert.Contains("Likes cats", prompt);
        Assert.Contains("Works as a developer", prompt);
        Assert.Contains("Lives in Canada", prompt);
        Assert.Contains("[0]", prompt);
        Assert.Contains("[1]", prompt);
        Assert.Contains("[2]", prompt);
    }

    [Fact]
    public void BuildConsolidationPrompt_IncludesTargetCount()
    {
        var memories = new List<UserMemory>
        {
            new("Fact A", "ctx", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
            new("Fact B", "ctx", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
        };

        var prompt = CreativeOrchestrator.BuildConsolidationPrompt(memories, 5);

        Assert.Contains("at most 5", prompt);
    }

    [Fact]
    public void BuildConsolidationPrompt_IncludesReferenceCount()
    {
        var memories = new List<UserMemory>
        {
            new("Frequently referenced", "ctx", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 42),
        };

        var prompt = CreativeOrchestrator.BuildConsolidationPrompt(memories, 1);

        Assert.Contains("referenced: 42 times", prompt);
    }

    [Fact]
    public void BuildConsolidationPrompt_IncludesMergeGuidance()
    {
        var memories = new List<UserMemory>
        {
            new("Fact A", "ctx", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
        };

        var prompt = CreativeOrchestrator.BuildConsolidationPrompt(memories, 1);

        Assert.Contains("MERGE", prompt);
        Assert.Contains("KEEP", prompt);
        Assert.Contains("DROP", prompt);
        Assert.Contains("PRESERVE", prompt);
    }

    // ── ParseConsolidatedMemories ─────────────────────────────────────

    [Fact]
    public void ParseConsolidatedMemories_ValidJson_ReturnsMemories()
    {
        var json = """
        {
          "memories": [
            { "content": "Loves cats and has a cat named Whiskers", "context": "merged pet facts" },
            { "content": "Software engineer in Vancouver", "context": "career and location" }
          ]
        }
        """;

        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("Loves cats and has a cat named Whiskers", result[0].Content);
        Assert.Equal("merged pet facts", result[0].Context);
        Assert.Equal("Software engineer in Vancouver", result[1].Content);
        Assert.Equal("career and location", result[1].Context);
    }

    [Fact]
    public void ParseConsolidatedMemories_EmptyMemoriesArray_ReturnsNull()
    {
        var json = """{ "memories": [] }""";
        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemories_MissingMemoriesKey_ReturnsNull()
    {
        var json = """{ "facts": [{ "content": "test" }] }""";
        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemories_InvalidJson_ReturnsNull()
    {
        var response = BuildTextResponse("not valid json {{{");
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemories_EmptyTextResponse_ReturnsNull()
    {
        var response = BuildTextResponse("");
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemories_NullContentEntries_SkipsNull()
    {
        var json = """
        {
          "memories": [
            { "content": "Valid fact", "context": "ctx" },
            { "content": null, "context": "ctx" },
            { "content": "", "context": "ctx" },
            { "content": "Another valid", "context": "ctx2" }
          ]
        }
        """;

        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("Valid fact", result[0].Content);
        Assert.Equal("Another valid", result[1].Content);
    }

    [Fact]
    public void ParseConsolidatedMemories_MissingContext_DefaultsToEmpty()
    {
        var json = """
        {
          "memories": [
            { "content": "Fact without context" }
          ]
        }
        """;

        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Fact without context", result[0].Content);
        Assert.Equal(string.Empty, result[0].Context);
    }

    [Fact]
    public void ParseConsolidatedMemories_SetsTimestampsAndZeroReferenceCount()
    {
        var json = """
        {
          "memories": [
            { "content": "Some fact", "context": "ctx" }
          ]
        }
        """;

        var before = DateTimeOffset.UtcNow;
        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemories(response);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.InRange(result[0].CreatedAt, before, after);
        Assert.InRange(result[0].LastReferencedAt, before, after);
        Assert.Equal(0, result[0].ReferenceCount);
    }

    // ── ReplaceAllMemoriesAsync (InMemoryUserMemoryStore) ─────────────

    [Fact]
    public async Task InMemoryStore_ReplaceAllMemories_ReplacesExisting()
    {
        var options = Options.Create(new BotOptions { MaxMemoriesPerUser = 20 });
        var store = new InMemoryUserMemoryStore(options, NullLogger<InMemoryUserMemoryStore>.Instance);

        await store.SaveMemoryAsync(1, "old-fact-A", "ctx");
        await store.SaveMemoryAsync(1, "old-fact-B", "ctx");
        await store.SaveMemoryAsync(1, "old-fact-C", "ctx");

        var consolidated = new List<UserMemory>
        {
            new("merged-AB", "consolidated", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
            new("kept-C", "consolidated", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
        };

        await store.ReplaceAllMemoriesAsync(1, consolidated);

        var memories = await store.GetMemoriesAsync(1);
        Assert.Equal(2, memories.Count);
        Assert.Equal("merged-AB", memories[0].Content);
        Assert.Equal("kept-C", memories[1].Content);
    }

    [Fact]
    public async Task InMemoryStore_ReplaceAllMemories_WorksWithEmptyExisting()
    {
        var options = Options.Create(new BotOptions { MaxMemoriesPerUser = 20 });
        var store = new InMemoryUserMemoryStore(options, NullLogger<InMemoryUserMemoryStore>.Instance);

        var consolidated = new List<UserMemory>
        {
            new("new-fact", "ctx", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
        };

        await store.ReplaceAllMemoriesAsync(1, consolidated);

        var memories = await store.GetMemoriesAsync(1);
        Assert.Single(memories);
        Assert.Equal("new-fact", memories[0].Content);
    }

    // ── ReplaceAllMemoriesAsync (FileBackedUserMemoryStore) ───────────

    [Fact]
    public async Task FileBackedStore_ReplaceAllMemories_ReplacesExisting()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"discordsky_test_{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new BotOptions
            {
                MaxMemoriesPerUser = 20,
                MemoryDataPath = tempDir
            });
            using var store = new FileBackedUserMemoryStore(options, NullLogger<FileBackedUserMemoryStore>.Instance);

            await store.SaveMemoryAsync(1, "old-A", "ctx");
            await store.SaveMemoryAsync(1, "old-B", "ctx");

            var consolidated = new List<UserMemory>
            {
                new("merged", "consolidated", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
            };

            await store.ReplaceAllMemoriesAsync(1, consolidated);

            var memories = await store.GetMemoriesAsync(1);
            Assert.Single(memories);
            Assert.Equal("merged", memories[0].Content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ── Config option defaults ────────────────────────────────────────

    [Fact]
    public void BotOptions_ConsolidationDefaults()
    {
        var options = new BotOptions();

        Assert.True(options.EnableMemoryConsolidation);
        Assert.Equal(0.75, options.ConsolidationTargetPercent);
    }

    [Fact]
    public void BotOptions_ConsolidationTargetCount_CalculatesCorrectly()
    {
        var options = new BotOptions
        {
            MaxMemoriesPerUser = 20,
            ConsolidationTargetPercent = 0.75
        };

        var target = Math.Max(1, (int)(options.MaxMemoriesPerUser * options.ConsolidationTargetPercent));

        Assert.Equal(15, target);
    }

    [Fact]
    public void BotOptions_ConsolidationTargetCount_NeverZero()
    {
        var options = new BotOptions
        {
            MaxMemoriesPerUser = 1,
            ConsolidationTargetPercent = 0.1
        };

        var target = Math.Max(1, (int)(options.MaxMemoriesPerUser * options.ConsolidationTargetPercent));

        Assert.Equal(1, target);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static ChatResponse BuildTextResponse(string text)
    {
        var message = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse(message);
    }
}
