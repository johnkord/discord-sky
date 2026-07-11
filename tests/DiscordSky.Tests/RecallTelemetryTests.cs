using System.Text.Json;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class RecallTelemetryTests : IDisposable
{
    private readonly string _tempDir;

    public RecallTelemetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "discord-sky-telemetry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private FileBackedTelemetrySink BuildSink(int retentionDays = 30)
    {
        var opts = new TelemetryOptions { BaseDirectory = _tempDir, RetentionDays = retentionDays };
        return new FileBackedTelemetrySink(Options.Create(opts), NullLogger<FileBackedTelemetrySink>.Instance);
    }

    [Fact]
    public async Task Emit_WritesJsonLineToDailyFile()
    {
        var sink = BuildSink();
        await sink.StartAsync(CancellationToken.None);

        var ts = new DateTimeOffset(2026, 5, 26, 7, 0, 0, TimeSpan.Zero);
        sink.Emit(new TelemetryEvent(
            Timestamp: ts,
            EventType: TelemetryEventTypes.RecallToolOk,
            UserHash: "deadbeef00",
            Count: 5,
            Total: 5,
            Truncated: false,
            QueryPresent: false,
            TopScore: 0.42,
            CallIndex: 1));

        await sink.StopAsync(CancellationToken.None);

        var path = Path.Combine(_tempDir, "recall-2026-05-26.jsonl");
        Assert.True(File.Exists(path), $"expected {path}");
        var content = await File.ReadAllTextAsync(path);
        var line = content.TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(TelemetryEventTypes.RecallToolOk, doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("deadbeef00", doc.RootElement.GetProperty("user").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Emit_OmitsNullFieldsFromJson()
    {
        var sink = BuildSink();
        await sink.StartAsync(CancellationToken.None);

        sink.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.PersonaInvoked,
            UserHash: "abc"));

        await sink.StopAsync(CancellationToken.None);

        var line = (await ReadOnlyLineAsync())!;
        // Channel, Kind, Count, Total etc. are all null → must not appear.
        Assert.DoesNotContain("\"channel\"", line);
        Assert.DoesNotContain("\"count\"", line);
        Assert.DoesNotContain("\"top_score\"", line);
        Assert.Contains("\"user\":\"abc\"", line);
    }

    [Fact]
    public async Task Emit_ColdOpen_PersistsRoomTextLineAndCritic()
    {
        var sink = BuildSink();
        await sink.StartAsync(CancellationToken.None);

        var ts = new DateTimeOffset(2026, 7, 5, 6, 0, 0, TimeSpan.Zero);
        sink.Emit(new TelemetryEvent(
            Timestamp: ts,
            EventType: TelemetryEventTypes.ColdOpen,
            Channel: "secret-chat",
            Kind: "bot cunty",
            Outcome: "shadow",
            TopScore: 0.86,
            Note: "alascene, my bot being cunty is precision engineering.",
            Reason: "critic 0.84 clean",
            Room: new[] { "alascene: Why is your bot so cunty to me yano", "curlyquote: who isn't he cunty to" },
            Provider: "xAI",
            Model: "grok-4.5",
            ReasoningEffort: "medium",
            LatencyMs: 1234,
            BaselineOutcome: "would_post",
            BaselineScore: 0.88,
            EvaluationId: "eval-123",
            OpportunityAt: ts.AddSeconds(-2)));

        await sink.StopAsync(CancellationToken.None);

        var path = Path.Combine(_tempDir, "recall-2026-07-05.jsonl");
        Assert.True(File.Exists(path), $"expected {path}");
        var line = (await File.ReadAllTextAsync(path)).TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // The raw room context (real display names + message text) is durably stored for owner review.
        var room = root.GetProperty("room").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("alascene: Why is your bot so cunty to me yano", room);
        Assert.Contains("curlyquote: who isn't he cunty to", room);
        // The bot's own drafted line and the advisory critic verdict are stored alongside it.
        Assert.Contains("precision engineering", root.GetProperty("note").GetString());
        Assert.Equal("critic 0.84 clean", root.GetProperty("reason").GetString());
        Assert.Equal("xAI", root.GetProperty("provider").GetString());
        Assert.Equal("grok-4.5", root.GetProperty("model").GetString());
        Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(1234, root.GetProperty("latency_ms").GetInt64());
        Assert.Equal("would_post", root.GetProperty("baseline_outcome").GetString());
        Assert.Equal(0.88, root.GetProperty("baseline_score").GetDouble());
        Assert.Equal("eval-123", root.GetProperty("evaluation_id").GetString());
        Assert.Equal(ts.AddSeconds(-2), root.GetProperty("opportunity_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task StartAsync_PrunesFilesOlderThanRetention()
    {
        // Pre-seed: one old (>30d) and one fresh.
        var oldDate = DateTimeOffset.UtcNow.AddDays(-45).UtcDateTime.ToString("yyyy-MM-dd");
        var freshDate = DateTimeOffset.UtcNow.AddDays(-1).UtcDateTime.ToString("yyyy-MM-dd");
        var oldPath = Path.Combine(_tempDir, $"recall-{oldDate}.jsonl");
        var freshPath = Path.Combine(_tempDir, $"recall-{freshDate}.jsonl");
        await File.WriteAllTextAsync(oldPath, "stale\n");
        await File.WriteAllTextAsync(freshPath, "fresh\n");

        var sink = BuildSink(retentionDays: 30);
        await sink.StartAsync(CancellationToken.None);
        await sink.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(oldPath), "old file should have been pruned");
        Assert.True(File.Exists(freshPath), "fresh file must survive");
    }

    [Fact]
    public async Task Emit_IsThreadSafe_UnderConcurrentWrites()
    {
        // Replaces the prior "non-blocking under buffer pressure" test. Synchronous fsync'd writes
        // are serialized via lock; many threads should produce a clean, line-delimited file with no
        // interleaved or truncated lines.
        var sink = BuildSink();
        await sink.StartAsync(CancellationToken.None);

        const int writers = 8;
        const int perWriter = 50;
        var ts = new DateTimeOffset(2026, 5, 26, 7, 0, 0, TimeSpan.Zero);
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                sink.Emit(new TelemetryEvent(ts, TelemetryEventTypes.RecallToolOk, UserHash: $"w{w}-{i}"));
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        await sink.StopAsync(CancellationToken.None);

        var path = Path.Combine(_tempDir, "recall-2026-05-26.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(writers * perWriter, lines.Length);
        // Each line must parse as valid JSON with a 'user' field — confirms no interleaving.
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("user", out _));
        }
    }

    [Fact]
    public void InMemorySink_CapturesAllEvents()
    {
        var sink = new InMemoryTelemetrySink();
        sink.Emit(new TelemetryEvent(DateTimeOffset.UtcNow, TelemetryEventTypes.RecallToolOk, UserHash: "u1"));
        sink.Emit(new TelemetryEvent(DateTimeOffset.UtcNow, TelemetryEventTypes.RecallHintEmitted, UserHash: "u2"));
        Assert.Equal(2, sink.Events.Count);
    }

    private async Task<string?> ReadOnlyLineAsync()
    {
        var file = Directory.EnumerateFiles(_tempDir, "recall-*.jsonl").FirstOrDefault();
        if (file is null) return null;
        var content = await File.ReadAllTextAsync(file);
        return content.TrimEnd('\n');
    }
}
