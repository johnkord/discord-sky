using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class ImageBudgetTests
{
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now;
        public MutableClock(DateTimeOffset start) => Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeLog : IImageGenerationLog
    {
        public int DayCount;
        public double MonthCost;
        public void Record(ImageGenerationRecord record) { }
        public int CountSuccessesOnUtcDay(DateOnly utcDay) => DayCount;
        public double SumSuccessCostInUtcMonth(DateTimeOffset now) => MonthCost;
    }

    private static ImageBudget Build(ImageOptions options, IImageGenerationLog log, TimeProvider clock)
        => new(Options.Create(options), log, clock);

    private const ulong User = 4242UL;

    [Fact]
    public void TryBegin_UnderAllLimits_GrantsAndReleases()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var budget = Build(new ImageOptions { PerUserPerHour = 2, GlobalPerDay = 25, MaxConcurrent = 2, MonthlyUsdGuard = 20 }, new FakeLog(), clock);

        using var lease = budget.TryBegin(User);

        Assert.True(lease.Allowed);
        Assert.Equal(BudgetRefusalReason.None, lease.Reason);
    }

    [Fact]
    public void PerUserHourly_RefusesAfterCap()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var budget = Build(new ImageOptions { PerUserPerHour = 2, GlobalPerDay = 0, MonthlyUsdGuard = 0, MaxConcurrent = 4 }, new FakeLog(), clock);

        budget.TryBegin(User).Dispose();
        budget.TryBegin(User).Dispose();
        using var third = budget.TryBegin(User);

        Assert.False(third.Allowed);
        Assert.Equal(BudgetRefusalReason.UserHourlyLimit, third.Reason);
    }

    [Fact]
    public void PerUserHourly_ResetsAfterAnHour()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var budget = Build(new ImageOptions { PerUserPerHour = 1, GlobalPerDay = 0, MonthlyUsdGuard = 0, MaxConcurrent = 4 }, new FakeLog(), clock);

        budget.TryBegin(User).Dispose();
        Assert.False(budget.TryBegin(User).Allowed);

        clock.Now = clock.Now.AddMinutes(61);

        using var afterReset = budget.TryBegin(User);
        Assert.True(afterReset.Allowed);
    }

    [Fact]
    public void PerUserHourly_IsPerUser()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var budget = Build(new ImageOptions { PerUserPerHour = 1, GlobalPerDay = 0, MonthlyUsdGuard = 0, MaxConcurrent = 4 }, new FakeLog(), clock);

        budget.TryBegin(User).Dispose();
        Assert.False(budget.TryBegin(User).Allowed);

        using var otherUser = budget.TryBegin(9999UL);
        Assert.True(otherUser.Allowed);
    }

    [Fact]
    public void DailyCap_RefusesWhenLogAtCap()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var log = new FakeLog { DayCount = 25 };
        var budget = Build(new ImageOptions { PerUserPerHour = 0, GlobalPerDay = 25, MonthlyUsdGuard = 0, MaxConcurrent = 4 }, log, clock);

        using var lease = budget.TryBegin(User);

        Assert.False(lease.Allowed);
        Assert.Equal(BudgetRefusalReason.DailyLimit, lease.Reason);
    }

    [Fact]
    public void DailyCap_AllowsJustUnderCap()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var log = new FakeLog { DayCount = 24 };
        var budget = Build(new ImageOptions { PerUserPerHour = 0, GlobalPerDay = 25, MonthlyUsdGuard = 0, MaxConcurrent = 4 }, log, clock);

        using var lease = budget.TryBegin(User);

        Assert.True(lease.Allowed);
    }

    [Fact]
    public void MonthlyGuard_RefusesWhenSpendReached()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var log = new FakeLog { MonthCost = 20.0 };
        var budget = Build(new ImageOptions { PerUserPerHour = 0, GlobalPerDay = 0, MonthlyUsdGuard = 20.0, MaxConcurrent = 4 }, log, clock);

        using var lease = budget.TryBegin(User);

        Assert.False(lease.Allowed);
        Assert.Equal(BudgetRefusalReason.MonthlyGuard, lease.Reason);
    }

    [Fact]
    public void Concurrency_RefusesWhenBusy_ThenAllowsAfterRelease()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var budget = Build(new ImageOptions { PerUserPerHour = 0, GlobalPerDay = 0, MonthlyUsdGuard = 0, MaxConcurrent = 1 }, new FakeLog(), clock);

        var first = budget.TryBegin(User);
        Assert.True(first.Allowed);

        var second = budget.TryBegin(User);
        Assert.False(second.Allowed);
        Assert.Equal(BudgetRefusalReason.ConcurrencyBusy, second.Reason);

        first.Dispose(); // release the only slot

        using var third = budget.TryBegin(User);
        Assert.True(third.Allowed);
    }

    [Fact]
    public void DeniedLease_DisposeIsSafe()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var budget = Build(new ImageOptions { GlobalPerDay = 1, MaxConcurrent = 1 }, new FakeLog { DayCount = 5 }, clock);

        var denied = budget.TryBegin(User);
        Assert.False(denied.Allowed);

        // Disposing a denied lease must not release a slot it never held.
        denied.Dispose();
        denied.Dispose();

        using var afterwards = budget.TryBegin(User);
        // Still denied by daily cap, but concurrency slot was never leaked.
        Assert.Equal(BudgetRefusalReason.DailyLimit, afterwards.Reason);
    }
}
