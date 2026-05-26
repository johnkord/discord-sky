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
        var opts = new TelemetryOptions { BaseDirectory = _tempDir, RetentionDays = retentionDays, BufferCapacity = 64 };
        return new FileBackedTelemetrySink(Options.Create(opts), NullLogger<FileBackedTelemetrySink>.Instance);
    }

    [Fact]
    public async Task Emit_WritesJsonLineToDailyFile()
    {
        await using var sink = BuildSink();
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
        await using var sink = BuildSink();
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

        await using var sink = BuildSink(retentionDays: 30);
        await sink.StartAsync(CancellationToken.None);
        await sink.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(oldPath), "old file should have been pruned");
        Assert.True(File.Exists(freshPath), "fresh file must survive");
    }

    [Fact]
    public async Task Emit_IsNonBlocking_WhenBufferFull()
    {
        // Tiny buffer; flood it before the drain loop starts to catch up. Just asserts that emit
        // doesn't throw and doesn't block; we don't care which events survive.
        var opts = new TelemetryOptions { BaseDirectory = _tempDir, RetentionDays = 30, BufferCapacity = 4 };
        await using var sink = new FileBackedTelemetrySink(Options.Create(opts), NullLogger<FileBackedTelemetrySink>.Instance);
        await sink.StartAsync(CancellationToken.None);

        for (int i = 0; i < 1000; i++)
        {
            sink.Emit(new TelemetryEvent(DateTimeOffset.UtcNow, TelemetryEventTypes.RecallHintEmitted, UserHash: $"u{i}"));
        }

        await sink.StopAsync(CancellationToken.None);
        // Test passes if we got here without hanging or throwing.
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
