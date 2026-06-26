using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class ImageGenerationLogTests : IDisposable
{
    private readonly string _tempDir;

    public ImageGenerationLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "discord-sky-imagelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private FileBackedImageGenerationLog Build(int retentionDays = 60)
        => new(Options.Create(new ImageOptions { BaseDirectory = _tempDir, RetentionDays = retentionDays }),
            NullLogger<FileBackedImageGenerationLog>.Instance);

    private static ImageGenerationRecord Record(DateTimeOffset ts, string outcome, double cost) =>
        new(ts, "general", "userhash", "gpt-image-1-mini", "1024x1024", "low", cost, 1234, outcome);

    [Fact]
    public void CountSuccessesOnUtcDay_CountsOnlyOkOutcomes()
    {
        var log = Build();
        var day = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

        log.Record(Record(day, ImageGenerationRecord.OutcomeOk, 0.005));
        log.Record(Record(day, ImageGenerationRecord.OutcomeOk, 0.005));
        log.Record(Record(day, ImageGenerationRecord.OutcomeError, 0.0));
        log.Record(Record(day, ImageGenerationRecord.OutcomeRefused, 0.0));
        log.Record(Record(day, ImageGenerationRecord.OutcomeModerationBlocked, 0.0));

        Assert.Equal(2, log.CountSuccessesOnUtcDay(DateOnly.FromDateTime(day.UtcDateTime)));
    }

    [Fact]
    public void CountSuccessesOnUtcDay_IsScopedToTheDay()
    {
        var log = Build();
        var day1 = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

        log.Record(Record(day1, ImageGenerationRecord.OutcomeOk, 0.005));
        log.Record(Record(day2, ImageGenerationRecord.OutcomeOk, 0.005));
        log.Record(Record(day2, ImageGenerationRecord.OutcomeOk, 0.005));

        Assert.Equal(1, log.CountSuccessesOnUtcDay(DateOnly.FromDateTime(day1.UtcDateTime)));
        Assert.Equal(2, log.CountSuccessesOnUtcDay(DateOnly.FromDateTime(day2.UtcDateTime)));
    }

    [Fact]
    public void CountSuccessesOnUtcDay_MissingFile_ReturnsZero()
    {
        var log = Build();
        Assert.Equal(0, log.CountSuccessesOnUtcDay(new DateOnly(2020, 1, 1)));
    }

    [Fact]
    public void SumSuccessCostInUtcMonth_SumsOkAcrossDaysInMonth()
    {
        var log = Build();
        var early = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2026, 6, 28, 9, 0, 0, TimeSpan.Zero);
        var otherMonth = new DateTimeOffset(2026, 5, 28, 9, 0, 0, TimeSpan.Zero);

        log.Record(Record(early, ImageGenerationRecord.OutcomeOk, 0.01));
        log.Record(Record(late, ImageGenerationRecord.OutcomeOk, 0.02));
        log.Record(Record(late, ImageGenerationRecord.OutcomeError, 5.0));   // not ok, ignored
        log.Record(Record(otherMonth, ImageGenerationRecord.OutcomeOk, 9.0)); // different month, ignored

        var sum = log.SumSuccessCostInUtcMonth(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(0.03, sum, precision: 6);
    }

    [Fact]
    public void ReadBack_ToleratesTornOrGarbageLines()
    {
        var log = Build();
        var day = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
        log.Record(Record(day, ImageGenerationRecord.OutcomeOk, 0.005));

        // Simulate a crash mid-write / a corrupt line appended to today's file.
        var path = Path.Combine(_tempDir, "image-gen-2026-06-25.jsonl");
        File.AppendAllText(path, "{ this is not valid json\n");
        log.Record(Record(day, ImageGenerationRecord.OutcomeOk, 0.005));

        Assert.Equal(2, log.CountSuccessesOnUtcDay(DateOnly.FromDateTime(day.UtcDateTime)));
    }

    [Fact]
    public async Task StartAsync_PrunesFilesOlderThanRetention()
    {
        var log = Build(retentionDays: 30);
        var old = DateTimeOffset.UtcNow.AddDays(-45);
        var fresh = DateTimeOffset.UtcNow.AddDays(-1);
        log.Record(Record(old, ImageGenerationRecord.OutcomeOk, 0.005));
        log.Record(Record(fresh, ImageGenerationRecord.OutcomeOk, 0.005));

        var oldPath = Path.Combine(_tempDir, $"image-gen-{old.UtcDateTime:yyyy-MM-dd}.jsonl");
        var freshPath = Path.Combine(_tempDir, $"image-gen-{fresh.UtcDateTime:yyyy-MM-dd}.jsonl");
        Assert.True(File.Exists(oldPath));

        await log.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(oldPath), "old file should be pruned");
        Assert.True(File.Exists(freshPath), "fresh file should remain");
    }

    [Fact]
    public void NoOpLog_RecordsNothingAndCountsZero()
    {
        var log = new NoOpImageGenerationLog();
        log.Record(Record(DateTimeOffset.UtcNow, ImageGenerationRecord.OutcomeOk, 1.0));
        Assert.Equal(0, log.CountSuccessesOnUtcDay(DateOnly.FromDateTime(DateTime.UtcNow)));
        Assert.Equal(0.0, log.SumSuccessCostInUtcMonth(DateTimeOffset.UtcNow));
    }
}
