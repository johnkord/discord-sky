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
