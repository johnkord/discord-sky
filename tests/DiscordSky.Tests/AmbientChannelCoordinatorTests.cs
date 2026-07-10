using DiscordSky.Bot.Orchestration.Impulse;

namespace DiscordSky.Tests;

public class AmbientChannelCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAcquire_ConcurrentCandidate_IsRejectedNotQueued()
    {
        var coordinator = new AmbientChannelCoordinator();
        Assert.True(coordinator.TryAcquire(1, Now, TimeSpan.FromSeconds(90), out var first, out _));

        Assert.False(coordinator.TryAcquire(1, Now, TimeSpan.FromSeconds(90), out var second, out var veto));
        Assert.Null(second);
        Assert.Equal("inflight", veto);

        first!.Dispose();
    }

    [Fact]
    public void TryAcquire_AfterUnsentLeaseReleased_AllowsNextCandidate()
    {
        var coordinator = new AmbientChannelCoordinator();
        Assert.True(coordinator.TryAcquire(1, Now, TimeSpan.FromSeconds(90), out var first, out _));
        first!.Dispose();

        Assert.True(coordinator.TryAcquire(1, Now, TimeSpan.FromSeconds(90), out var second, out var veto));
        Assert.Null(veto);
        second!.Dispose();
    }

    [Fact]
    public void TryAcquire_AfterSend_EnforcesQuietPeriod()
    {
        var coordinator = new AmbientChannelCoordinator();
        Assert.True(coordinator.TryAcquire(1, Now, TimeSpan.FromSeconds(90), out var first, out _));
        first!.MarkSent(Now);
        first.Dispose();

        Assert.False(coordinator.TryAcquire(1, Now.AddSeconds(89), TimeSpan.FromSeconds(90), out _, out var veto));
        Assert.Equal("quiet", veto);
        Assert.True(coordinator.TryAcquire(1, Now.AddSeconds(90), TimeSpan.FromSeconds(90), out var next, out _));
        next!.Dispose();
    }

    [Fact]
    public void TryAcquire_DifferentChannels_DoNotBlockEachOther()
    {
        var coordinator = new AmbientChannelCoordinator();
        Assert.True(coordinator.TryAcquire(1, Now, TimeSpan.FromSeconds(90), out var first, out _));
        Assert.True(coordinator.TryAcquire(2, Now, TimeSpan.FromSeconds(90), out var second, out _));
        first!.Dispose();
        second!.Dispose();
    }
}
