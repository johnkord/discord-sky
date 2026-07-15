using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class AmbientVisualBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAcquire_ConcurrentGuildAttemptIsRejected()
    {
        var budget = Build(new FakeLog());
        Assert.True(budget.TryAcquire(1, Now, out var first, out _));

        Assert.False(budget.TryAcquire(1, Now, out _, out var veto));
        Assert.Equal("inflight", veto);

        first!.Dispose();
        Assert.True(budget.TryAcquire(1, Now, out var next, out _));
        next!.Dispose();
    }

    [Fact]
    public void SuccessfulLeaseStartsCooldown()
    {
        var budget = Build(new FakeLog());
        Assert.True(budget.TryAcquire(1, Now, out var first, out _));
        first!.MarkSucceeded(Now);

        Assert.False(budget.TryAcquire(1, Now.AddHours(5), out _, out var veto));
        Assert.Equal("cooldown", veto);
        Assert.True(budget.TryAcquire(1, Now.AddHours(6), out var next, out _));
        next!.Dispose();
    }

    [Fact]
    public void DurableDailySuccessVetoesAfterRestart()
    {
        var log = new FakeLog { DailyCount = 1 };
        var budget = Build(log);

        Assert.False(budget.TryAcquire(1, Now, out _, out var veto));
        Assert.Equal("daily_cap", veto);
    }

    [Fact]
    public void DurableLogFailureVetoesUnsolicitedImage()
    {
        var budget = Build(new FakeLog { ThrowOnRead = true });

        Assert.False(budget.TryAcquire(1, Now, out _, out var veto));
        Assert.Equal("budget_unavailable", veto);
    }

    private static AmbientVisualBudget Build(FakeLog log) => new(
        Options.Create(new ImageOptions
        {
            AmbientVisualEnabled = true,
            AmbientVisualMaxPerGuildPerDay = 1,
            AmbientVisualCooldownHours = 6,
        }),
        log);

    private sealed class FakeLog : IImageGenerationLog
    {
        public int DailyCount;
        public bool ThrowOnRead;
        public void Record(ImageGenerationRecord record) { }
        public int CountSuccessesOnUtcDay(DateOnly utcDay) => 0;
        public double SumSuccessCostInUtcMonth(DateTimeOffset now) => 0;
        public int CountSuccessfulAmbientVisualsOnUtcDay(DateOnly utcDay, ulong guildId) =>
            ThrowOnRead ? throw new IOException("log unavailable") : DailyCount;
        public DateTimeOffset? LastSuccessfulAmbientVisualAt(ulong guildId) => null;
    }
}