using System.Collections.Concurrent;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Images;

/// <summary>Why a generation was refused by the budget, mapped to an in-character line by <see cref="ImageRefusals"/>.</summary>
public enum BudgetRefusalReason
{
    None,
    UserHourlyLimit,
    DailyLimit,
    MonthlyGuard,
    ConcurrencyBusy,
}

/// <summary>
/// A granted generation slot. Dispose releases the concurrency slot it holds. A denied lease holds nothing.
/// </summary>
public sealed class BudgetLease : IDisposable
{
    private SemaphoreSlim? _toRelease;

    private BudgetLease(bool allowed, BudgetRefusalReason reason, SemaphoreSlim? toRelease)
    {
        Allowed = allowed;
        Reason = reason;
        _toRelease = toRelease;
    }

    public bool Allowed { get; }
    public BudgetRefusalReason Reason { get; }

    internal static BudgetLease Granted(SemaphoreSlim concurrencySlot) => new(true, BudgetRefusalReason.None, concurrencySlot);
    internal static BudgetLease Denied(BudgetRefusalReason reason) => new(false, reason, null);

    public void Dispose()
    {
        var sem = Interlocked.Exchange(ref _toRelease, null);
        sem?.Release();
    }
}

/// <summary>
/// The spend and abuse gate for image generation (docs/image_generation_design.md section 5). Two
/// load-bearing limits, a durable daily cap and a per-user hourly throttle, plus a concurrency gate and
/// a monthly USD guard. The daily cap and monthly guard are read from the durable on-disk log so they
/// survive pod restarts (a crash loop must not be able to blow the budget). Cheap in-memory checks run
/// before the concurrency slot is taken, so a refused request never holds a slot.
/// </summary>
public sealed class ImageBudget
{
    private readonly ImageOptions _options;
    private readonly IImageGenerationLog _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<ulong, List<DateTimeOffset>> _userHits = new();

    public ImageBudget(IOptions<ImageOptions> options, IImageGenerationLog log, TimeProvider? clock = null)
    {
        _options = options.Value;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrent));
    }

    /// <summary>
    /// Attempts to begin one generation. On success the returned lease holds a concurrency slot that the
    /// caller MUST dispose when the generation finishes (success or failure).
    /// </summary>
    public BudgetLease TryBegin(ulong userId)
    {
        var now = _clock.GetUtcNow();

        // 1. Per-user hourly throttle (cheap, in-memory).
        if (_options.PerUserPerHour > 0 && CountRecentUserHits(userId, now) >= _options.PerUserPerHour)
        {
            return BudgetLease.Denied(BudgetRefusalReason.UserHourlyLimit);
        }

        // 2. Durable daily cap (read from disk so it survives restarts).
        if (_options.GlobalPerDay > 0)
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (_log.CountSuccessesOnUtcDay(today) >= _options.GlobalPerDay)
            {
                return BudgetLease.Denied(BudgetRefusalReason.DailyLimit);
            }
        }

        // 3. Monthly USD guard (read from disk).
        if (_options.MonthlyUsdGuard > 0 && _log.SumSuccessCostInUtcMonth(now) >= _options.MonthlyUsdGuard)
        {
            return BudgetLease.Denied(BudgetRefusalReason.MonthlyGuard);
        }

        // 4. Concurrency gate, taken last so a refused request never holds a slot.
        if (!_concurrency.Wait(0))
        {
            return BudgetLease.Denied(BudgetRefusalReason.ConcurrencyBusy);
        }

        // 5. Commit: record the per-user attempt now that we are proceeding.
        RecordUserHit(userId, now);
        return BudgetLease.Granted(_concurrency);
    }

    private int CountRecentUserHits(ulong userId, DateTimeOffset now)
    {
        if (!_userHits.TryGetValue(userId, out var hits)) return 0;
        var cutoff = now - TimeSpan.FromHours(1);
        lock (hits)
        {
            hits.RemoveAll(t => t < cutoff);
            return hits.Count;
        }
    }

    private void RecordUserHit(ulong userId, DateTimeOffset now)
    {
        var hits = _userHits.GetOrAdd(userId, _ => new List<DateTimeOffset>());
        var cutoff = now - TimeSpan.FromHours(1);
        lock (hits)
        {
            hits.RemoveAll(t => t < cutoff);
            hits.Add(now);
        }
    }
}
