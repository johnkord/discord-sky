using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Impulse;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class ColdOpenNoveltyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "discord-sky-cold-open-novelty-" + Guid.NewGuid().ToString("N"));

    public ColdOpenNoveltyTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(ColdOpenEpisodeNoveltyMode.Shadow, false)]
    [InlineData(ColdOpenEpisodeNoveltyMode.Exact, true)]
    [InlineData(ColdOpenEpisodeNoveltyMode.Calibrated, true)]
    public void ExactSourceOverlap_ObservesOrGatesByMode(ColdOpenEpisodeNoveltyMode mode, bool shouldSuppress)
    {
        var decision = ColdOpenNoveltyEvaluator.Evaluate(
            new[] { Evidence(10) },
            new[] { Snapshot("prior", sources: new[] { 10UL }) },
            mode);

        Assert.Equal(ColdOpenNoveltyStage.ExactSource, decision.Stage);
        Assert.True(decision.WouldSuppress);
        Assert.Equal(shouldSuppress, decision.ShouldSuppress);
    }

    [Fact]
    public void ReplyAncestryAndStableResource_AreDetected()
    {
        var ancestry = ColdOpenNoveltyEvaluator.Evaluate(
            new[] { Evidence(20, referenced: 10) },
            new[] { Snapshot("prior", sources: new[] { 10UL }) },
            ColdOpenEpisodeNoveltyMode.Exact);
        var resource = ColdOpenNoveltyEvaluator.Evaluate(
            new[] { Evidence(20, resources: new[] { "example.com/story/1" }) },
            new[] { Snapshot("prior", resources: new[] { "example.com/story/1" }) },
            ColdOpenEpisodeNoveltyMode.Exact);

        Assert.Equal(ColdOpenNoveltyStage.ReplyAncestry, ancestry.Stage);
        Assert.True(ancestry.ShouldSuppress);
        Assert.Equal(ColdOpenNoveltyStage.StableResource, resource.Stage);
        Assert.True(resource.ShouldSuppress);
    }

    [Fact]
    public void OdysseyLikeAnchorPair_IsShadowOnlyUntilCalibrated()
    {
        var candidate = new[] { Evidence(20, anchors: new[] { "odyssey", "helmet", "return" }) };
        var prior = new[] { Snapshot("prior", anchors: new[] { "odyssey", "helmet", "crew" }) };

        var shadow = ColdOpenNoveltyEvaluator.Evaluate(candidate, prior, ColdOpenEpisodeNoveltyMode.Shadow);
        var exact = ColdOpenNoveltyEvaluator.Evaluate(candidate, prior, ColdOpenEpisodeNoveltyMode.Exact);
        var calibrated = ColdOpenNoveltyEvaluator.Evaluate(candidate, prior, ColdOpenEpisodeNoveltyMode.Calibrated);

        Assert.Equal(ColdOpenNoveltyStage.MultipleTopicAnchors, shadow.Stage);
        Assert.True(shadow.WouldSuppress);
        Assert.False(shadow.ShouldSuppress);
        Assert.False(exact.ShouldSuppress);
        Assert.True(calibrated.ShouldSuppress);
    }

    [Fact]
    public void BenignCallbackWithOneAnchorIsAllowed()
    {
        var decision = ColdOpenNoveltyEvaluator.Evaluate(
            new[] { Evidence(20, anchors: new[] { "odyssey", "lunch" }) },
            new[] { Snapshot("prior", anchors: new[] { "odyssey", "helmet" }) },
            ColdOpenEpisodeNoveltyMode.Calibrated);

        Assert.Equal(ColdOpenNoveltyStage.NoOverlap, decision.Stage);
        Assert.False(decision.ShouldSuppress);
    }

    [Fact]
    public void ResourceAndAnchorExtractionIsStableAndBounded()
    {
        var resources = ColdOpenEvidenceExtractor.ExtractResourceIds(
            "read https://Example.com/story/1?utm_source=x and https://example.com/story/1#part");
        var anchors = ColdOpenEvidenceExtractor.ExtractTopicAnchors(
            "The Odyssey helmets returned, and the helmets were dented.");

        Assert.Equal(new[] { "example.com/story/1" }, resources);
        Assert.Contains("odyssey", anchors);
        Assert.Contains("helmet", anchors);
        Assert.Equal(anchors.Distinct().Count(), anchors.Count);
    }

    [Fact]
    public void OperationalMediaAnchorsAreRemoved()
    {
        var anchors = ColdOpenEvidenceExtractor.ExtractTopicAnchors(
            "Attachment image visual summary untrusted generated derivative meteor helmet");

        Assert.DoesNotContain("attachment", anchors);
        Assert.DoesNotContain("image", anchors);
        Assert.DoesNotContain("visual", anchors);
        Assert.DoesNotContain("summary", anchors);
        Assert.DoesNotContain("untrust", anchors);
        Assert.DoesNotContain("generat", anchors);
        Assert.DoesNotContain("deriv", anchors);
        Assert.Contains("meteor", anchors);
        Assert.Contains("helmet", anchors);
    }

    [Fact]
    public void SourceValidator_UsesOnlyValidCitationsWithoutGating()
    {
        var evidence = new[] { Evidence(10), Evidence(11) };

        var valid = ColdOpenSourceValidator.Validate(
            new ColdOpenDraft(0.9, "line", "hook", new ulong[] { 11 }),
            evidence);
        var partial = ColdOpenSourceValidator.Validate(
            new ColdOpenDraft(0.9, "line", "hook", new ulong[] { 10, 999 }),
            evidence);
        var missing = ColdOpenSourceValidator.Validate(
            new ColdOpenDraft(0.9, "line", "hook"),
            evidence);

        Assert.Equal("valid", valid.Status);
        Assert.Equal(11UL, Assert.Single(valid.SelectedEvidence).MessageId);
        Assert.Equal("partial", partial.Status);
        Assert.Equal(1, partial.ValidCount);
        Assert.Equal(1, partial.InvalidCount);
        Assert.Equal("missing", missing.Status);
        Assert.Empty(missing.SelectedEvidence);
    }

    [Fact]
    public async Task LedgerPersistsPrunesAndContainsNoRenderedLines()
    {
        var now = Timestamp;
        var first = Ledger(() => now);
        await first.StartAsync(CancellationToken.None);
        first.Record(Snapshot("old", firedAt: now.AddHours(-49)));
        first.Record(Snapshot("fresh", firedAt: now, sources: new[] { 10UL }, anchors: new[] { "odyssey", "helmet" }));

        var second = Ledger(() => now.AddHours(1));
        await second.StartAsync(CancellationToken.None);

        var restored = Assert.Single(second.GetRecent(99));
        Assert.Equal("fresh", restored.EpisodeId);
        var json = await File.ReadAllTextAsync(LedgerPath);
        Assert.DoesNotContain("RenderedLine", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("room text", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task LedgerWriteFailureContinuesInMemoryAndCorruptLoadStartsEmpty()
    {
        await File.WriteAllTextAsync(LedgerPath, "not-json");
        var ledger = Ledger(
            () => Timestamp,
            (_, _, _) => throw new IOException("disk unavailable"));

        await ledger.StartAsync(CancellationToken.None);
        ledger.Record(Snapshot("fresh"));

        Assert.Equal("fresh", Assert.Single(ledger.GetRecent(99)).EpisodeId);
    }

    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private string LedgerPath => Path.Combine(_tempDir, "novelty.json");

    private FileBackedProactiveEpisodeLedger Ledger(
        Func<DateTimeOffset> clock,
        Action<string, string, string>? writer = null) => new(
            new ColdOpenOptions
            {
                EpisodeNoveltyMode = ColdOpenEpisodeNoveltyMode.Shadow,
                NoveltyRetentionHours = 48,
                NoveltyLedgerPath = LedgerPath,
                NoveltyLedgerMaxEntries = 256,
            },
            NullLogger<FileBackedProactiveEpisodeLedger>.Instance,
            clock,
            writer ?? ((temp, path, json) =>
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }));

    private static ColdOpenRoomEvidence Evidence(
        ulong id,
        ulong? referenced = null,
        IReadOnlyList<string>? resources = null,
        IReadOnlyList<string>? anchors = null) => new(
            id,
            referenced,
            Timestamp,
            "Alice",
            "Alice: room text",
            anchors ?? Array.Empty<string>(),
            resources ?? Array.Empty<string>());

    private static ColdOpenEpisodeSnapshot Snapshot(
        string id,
        DateTimeOffset? firedAt = null,
        IReadOnlyList<ulong>? sources = null,
        IReadOnlyList<ulong>? references = null,
        IReadOnlyList<string>? resources = null,
        IReadOnlyList<string>? anchors = null) => new(
            id,
            99,
            firedAt ?? Timestamp,
            sources ?? Array.Empty<ulong>(),
            references ?? Array.Empty<ulong>(),
            resources ?? Array.Empty<string>(),
            anchors ?? Array.Empty<string>());
}