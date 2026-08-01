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
            Mem("a", "Likes cats", createdAt: DateTimeOffset.UtcNow.AddDays(-10)),
            Mem("b", "Works as a developer", createdAt: DateTimeOffset.UtcNow.AddDays(-5)),
            Mem("c", "Lives in Canada", createdAt: DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var prompt = CreativeOrchestrator.BuildConsolidationPrompt(memories, 2);

        Assert.Contains("Likes cats", prompt);
        Assert.Contains("Works as a developer", prompt);
        Assert.Contains("Lives in Canada", prompt);
        Assert.Contains("[0]", prompt);
        Assert.Contains("[1]", prompt);
        Assert.Contains("[2]", prompt);
        Assert.Contains("id=a", prompt);
        Assert.Contains("source_memory_ids", prompt);
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
    public void BuildConsolidationPrompt_IncludesCreatedDate()
    {
        var created = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var memories = new List<UserMemory>
        {
            new("Frequently referenced", "ctx", created, DateTimeOffset.UtcNow, 42),
        };

        var prompt = CreativeOrchestrator.BuildConsolidationPrompt(memories, 1);

        Assert.Contains("created: 2025-06-15", prompt);
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

    // ── BuildConsolidationUserMessage ─────────────────────────────────
    // Regression: OpenAI Responses API rejects `text.format=json_object` if no
    // user-role message mentions "json". This caused 100% consolidation failure
    // in production for ~12 days. Don't let it regress.

    [Fact]
    public void BuildConsolidationUserMessage_ContainsJsonLiteral()
    {
        var msg = CreativeOrchestrator.BuildConsolidationUserMessage(20, 15);
        Assert.Contains("JSON", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildConsolidationUserMessage_IncludesCounts()
    {
        var msg = CreativeOrchestrator.BuildConsolidationUserMessage(20, 15);
        Assert.Contains("20", msg);
        Assert.Contains("15", msg);
    }

    // ── ParseConsolidatedMemoryProposals ──────────────────────────────

    [Fact]
    public void ParseConsolidatedMemoryProposals_ValidJson_ReturnsSources()
    {
        var json = """
        {
          "memories": [
            { "content": "Loves cats and has a cat named Whiskers", "context": "merged pet facts", "source_memory_ids": ["a", "b"] },
            { "content": "Software engineer in Vancouver", "context": "career and location", "source_memory_ids": ["c"] }
          ]
        }
        """;

        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemoryProposals(response);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("Loves cats and has a cat named Whiskers", result[0].Content);
        Assert.Equal("merged pet facts", result[0].Context);
        Assert.Equal(new[] { "a", "b" }, result[0].SourceMemoryIds);
        Assert.Equal("Software engineer in Vancouver", result[1].Content);
        Assert.Equal(new[] { "c" }, result[1].SourceMemoryIds);
    }

    [Fact]
    public void ParseConsolidatedMemoryProposals_EmptyMemoriesArray_ReturnsNull()
    {
        var json = """{ "memories": [] }""";
        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemoryProposals(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemoryProposals_MissingMemoriesKey_ReturnsNull()
    {
        var json = """{ "facts": [{ "content": "test" }] }""";
        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemoryProposals(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemoryProposals_InvalidJson_ReturnsNull()
    {
        var response = BuildTextResponse("not valid json {{{");
        var result = CreativeOrchestrator.ParseConsolidatedMemoryProposals(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemoryProposals_EmptyTextResponse_ReturnsNull()
    {
        var response = BuildTextResponse("");
        var result = CreativeOrchestrator.ParseConsolidatedMemoryProposals(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseConsolidatedMemoryProposals_MissingContentOrSources_SkipsEntry()
    {
        var json = """
        {
          "memories": [
            { "content": "Valid fact", "context": "ctx", "source_memory_ids": ["a"] },
            { "content": null, "context": "ctx", "source_memory_ids": ["b"] },
            { "content": "Missing sources", "context": "ctx" },
            { "content": "Another valid", "context": "ctx2", "source_memory_ids": ["c", "c"] }
          ]
        }
        """;

        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemoryProposals(response);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("Valid fact", result[0].Content);
        Assert.Equal("Another valid", result[1].Content);
    }

    [Fact]
    public void ParseConsolidatedMemoryProposals_MissingContext_DefaultsToEmpty()
    {
        var json = """
        {
          "memories": [
            { "content": "Fact without context", "source_memory_ids": ["a"] }
          ]
        }
        """;

        var response = BuildTextResponse(json);
        var result = CreativeOrchestrator.ParseConsolidatedMemoryProposals(response);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Fact without context", result[0].Content);
        Assert.Equal(string.Empty, result[0].Context);
    }

    // ── MemoryConsolidationPlanner ────────────────────────────────────

    [Fact]
    public void Planner_MergesMetadataAndCreatesSourceLineage()
    {
        var capturedAt = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var older = capturedAt.AddDays(-10);
        var recent = capturedAt.AddDays(-1);
        var existing = new[]
        {
            Mem("a", "Likes cats", createdAt: older, lastReferencedAt: older, referenceCount: 2,
                topics: new[] { "pets" }, importance: 5, evidence: new[] { 11UL }),
            Mem("b", "Has a cat named Luna", createdAt: recent, lastReferencedAt: recent, referenceCount: 3,
                topics: new[] { "cats" }, importance: 8, evidence: new[] { 12UL }),
        };

        var plan = MemoryConsolidationPlanner.Build(
            100,
            existing,
            new[] { new ConsolidatedMemoryProposal("Has a cat named Luna and loves cats", "merged pets", new[] { "a", "b" }) },
            targetCount: 1,
            operationId: "op-1",
            capturedAt);

        Assert.True(plan.IsValid);
        var merged = Assert.Single(plan.Memories!);
        Assert.Equal(older, merged.CreatedAt);
        Assert.Equal(recent, merged.LastReferencedAt);
        Assert.Equal(5, merged.ReferenceCount);
        Assert.Equal(MemoryKind.Factual, merged.Kind);
        Assert.Equal(new[] { "pets", "cats" }, merged.Topics);
        Assert.Equal(8, merged.Importance);
        Assert.Equal(new[] { 11UL, 12UL }, merged.Provenance?.EvidenceMessageIds);
        Assert.Equal(new[] { "a", "b" }, merged.Provenance?.SourceMemoryIds);
        Assert.Equal("consolidation", merged.Provenance?.Transition);
        Assert.Equal(MemoryIdentity.FromOperation("op-1", 100, 0), merged.MemoryId);
    }

    [Theory]
    [InlineData("unknown", "unknown_source_id")]
    [InlineData("cross-kind", "cross_kind_merge")]
    [InlineData("source-reused", "source_reused")]
    [InlineData("high-value-dropped", "high_value_source_dropped")]
    public void Planner_RejectsInvalidSourcePlans(string scenario, string expectedReason)
    {
        var existing = new[]
        {
            Mem("a", "Fact A", importance: 9),
            Mem("b", "Fact B"),
            Mem("r", "Running bit", kind: MemoryKind.Running, importance: 8),
        };
        var proposals = scenario switch
        {
            "unknown" => new[] { new ConsolidatedMemoryProposal("X", "", new[] { "missing" }) },
            "cross-kind" => new[] { new ConsolidatedMemoryProposal("X", "", new[] { "a", "r" }) },
            "source-reused" => new[]
            {
                new ConsolidatedMemoryProposal("X", "", new[] { "a", "b" }),
                new ConsolidatedMemoryProposal("Y", "", new[] { "b", "r" }),
            },
            "high-value-dropped" => new[] { new ConsolidatedMemoryProposal("B", "", new[] { "b" }) },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var plan = MemoryConsolidationPlanner.Build(
            100, existing, proposals, 2, "op", DateTimeOffset.UtcNow);

        Assert.False(plan.IsValid);
        Assert.Equal(expectedReason, plan.RejectionReason);
    }

    [Fact]
    public void Planner_PreservesMetaAndSuppressedOutsideModelRewrite()
    {
        var factual = Mem("f", "Fact");
        var meta = Mem("m", "Prefers short replies", kind: MemoryKind.Meta);
        var suppressed = Mem("s", "cats", kind: MemoryKind.Suppressed, topics: new[] { "cats" });

        var plan = MemoryConsolidationPlanner.Build(
            100,
            new[] { factual, meta, suppressed },
            new[] { new ConsolidatedMemoryProposal("Fact rewritten", "ctx", new[] { "f" }) },
            targetCount: 2,
            operationId: "op",
            DateTimeOffset.UtcNow);

        Assert.True(plan.IsValid);
        Assert.Equal(3, plan.Memories!.Count);
        Assert.Contains(meta, plan.Memories);
        Assert.Contains(suppressed, plan.Memories);
        Assert.Equal(2, plan.Memories.Count(memory => memory.Kind != MemoryKind.Suppressed));
    }

    [Fact]
    public void DeterministicFallback_PreservesProtectedAndHighValueMetadata()
    {
        var low = Mem("low", "Low", importance: 1);
        var running = Mem("run", "Running", kind: MemoryKind.Running, importance: 6, referenceCount: 4);
        var high = Mem("high", "High", importance: 9, topics: new[] { "gold" });
        var meta = Mem("meta", "Meta", kind: MemoryKind.Meta);
        var suppressed = Mem("sup", "Suppressed", kind: MemoryKind.Suppressed);

        var result = MemoryConsolidationPlanner.DeterministicFallback(
            new[] { low, running, high, meta, suppressed },
            targetCount: 3);

        Assert.DoesNotContain(low, result);
        Assert.Contains(running, result);
        Assert.Contains(high, result);
        Assert.Contains(meta, result);
        Assert.Contains(suppressed, result);
        Assert.Equal(4, result.Single(memory => memory.MemoryId == "run").ReferenceCount);
        Assert.Equal(new[] { "gold" }, result.Single(memory => memory.MemoryId == "high").Topics);
    }

    [Fact]
    public void DeterministicFallback_TiesUseOriginalOrder()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = Mem("a", "A", createdAt: timestamp, lastReferencedAt: timestamp, importance: 1);
        var second = Mem("b", "B", createdAt: timestamp, lastReferencedAt: timestamp, importance: 1);

        var result = MemoryConsolidationPlanner.DeterministicFallback(
            new[] { first, second },
            targetCount: 1);

        Assert.Equal("a", Assert.Single(result).MemoryId);
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

    private static UserMemory Mem(
        string id,
        string content,
        MemoryKind kind = MemoryKind.Factual,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastReferencedAt = null,
        int referenceCount = 0,
        IReadOnlyList<string>? topics = null,
        int? importance = null,
        IReadOnlyList<ulong>? evidence = null) => new(
            content,
            "ctx",
            createdAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            lastReferencedAt ?? new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            referenceCount,
            kind,
            topics,
            Importance: importance,
            Provenance: new MemoryProvenance("source-op", DateTimeOffset.UtcNow, evidence ?? Array.Empty<ulong>()),
            MemoryId: id);
}
