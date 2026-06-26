using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class ImageToolServiceTests
{
    private sealed class StubGenerator : IImageGenerator
    {
        public bool Enabled = true;
        public string? CapturedPrompt;
        public ImageResult Next = ImageResult.Ok(new byte[] { 1, 2, 3 }, "jpg", null);

        public bool IsEnabled => Enabled;

        public Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken)
        {
            CapturedPrompt = prompt;
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeLog : IImageGenerationLog
    {
        public int DayCount;
        public readonly List<ImageGenerationRecord> Records = new();
        public void Record(ImageGenerationRecord record) => Records.Add(record);
        public int CountSuccessesOnUtcDay(DateOnly utcDay) => DayCount;
        public double SumSuccessCostInUtcMonth(DateTimeOffset now) => 0.0;
    }

    private static ImageToolService Build(StubGenerator gen, IImageGenerationLog log, ImageOptions? options = null)
    {
        var opts = options ?? new ImageOptions { PerUserPerHour = 0, GlobalPerDay = 0, MonthlyUsdGuard = 0, MaxConcurrent = 4 };
        var budget = new ImageBudget(Options.Create(opts), log);
        return new ImageToolService(budget, gen, log, Options.Create(opts), NullLogger<ImageToolService>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_AppendsStyleSuffixToPrompt()
    {
        var gen = new StubGenerator();
        var service = Build(gen, new FakeLog());

        await service.GenerateAsync(123, "general", "a statue of my own glorious face", CancellationToken.None);

        Assert.NotNull(gen.CapturedPrompt);
        Assert.StartsWith("a statue of my own glorious face", gen.CapturedPrompt);
        Assert.EndsWith(ImageToolService.StyleSuffix, gen.CapturedPrompt);
    }

    [Fact]
    public async Task GenerateAsync_Success_ReturnsBytesAndFileNameAndLogsOk()
    {
        var gen = new StubGenerator { Next = ImageResult.Ok(new byte[] { 1, 2, 3, 4 }, "jpg", "revised") };
        var log = new FakeLog();
        var service = Build(gen, log);

        var outcome = await service.GenerateAsync(1, "chan", "draw me a throne", CancellationToken.None);

        Assert.True(outcome.Generated);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, outcome.Bytes);
        Assert.Equal("robotnik.jpg", outcome.FileName);
        Assert.Null(outcome.RefusalText);
        Assert.Contains(log.Records, r => r.Outcome == ImageGenerationRecord.OutcomeOk);
    }

    [Fact]
    public async Task GenerateAsync_ModerationBlocked_RefusesAndLogs()
    {
        var gen = new StubGenerator { Next = ImageResult.Fail(ImageResult.ErrorModerationBlocked) };
        var log = new FakeLog();
        var service = Build(gen, log);

        var outcome = await service.GenerateAsync(1, "chan", "draw something", CancellationToken.None);

        Assert.False(outcome.Generated);
        Assert.Null(outcome.Bytes);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RefusalText));
        Assert.Contains(log.Records, r => r.Outcome == ImageGenerationRecord.OutcomeModerationBlocked);
    }

    [Fact]
    public async Task GenerateAsync_GenericError_RefusesAndLogsError()
    {
        var gen = new StubGenerator { Next = ImageResult.Fail(ImageResult.ErrorServer) };
        var log = new FakeLog();
        var service = Build(gen, log);

        var outcome = await service.GenerateAsync(1, "chan", "draw", CancellationToken.None);

        Assert.False(outcome.Generated);
        Assert.Contains(log.Records, r => r.Outcome == ImageGenerationRecord.OutcomeError);
    }

    [Fact]
    public async Task GenerateAsync_BudgetDenied_RefusesWithoutCallingGenerator()
    {
        var gen = new StubGenerator();
        var log = new FakeLog { DayCount = 5 };
        var opts = new ImageOptions { GlobalPerDay = 5, PerUserPerHour = 0, MonthlyUsdGuard = 0, MaxConcurrent = 4 };
        var service = Build(gen, log, opts);

        var outcome = await service.GenerateAsync(1, "chan", "draw", CancellationToken.None);

        Assert.False(outcome.Generated);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RefusalText));
        Assert.Null(gen.CapturedPrompt); // generator must not be called when the budget denies
    }

    [Fact]
    public async Task GenerateAsync_EmptyPrompt_Refuses()
    {
        var gen = new StubGenerator();
        var service = Build(gen, new FakeLog());

        var outcome = await service.GenerateAsync(1, "chan", "   ", CancellationToken.None);

        Assert.False(outcome.Generated);
        Assert.Null(gen.CapturedPrompt);
    }

    [Fact]
    public void IsEnabled_ReflectsTheGenerator()
    {
        Assert.True(Build(new StubGenerator { Enabled = true }, new FakeLog()).IsEnabled);
        Assert.False(Build(new StubGenerator { Enabled = false }, new FakeLog()).IsEnabled);
    }
}
