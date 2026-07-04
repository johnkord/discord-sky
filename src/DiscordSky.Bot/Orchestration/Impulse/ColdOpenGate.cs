using DiscordSky.Bot.Configuration;

namespace DiscordSky.Bot.Orchestration.Impulse;

/// <summary>Outcome of the cold-open gate: whether to proceed, and if not, which gate vetoed (for telemetry).</summary>
public sealed record GateResult(bool Pass, string? Veto)
{
    public static GateResult Ok { get; } = new(true, null);
    public static GateResult No(string veto) => new(false, veto);
}

/// <summary>
/// The never-into-silence invariant as a pure function. A cold open may proceed ONLY into a channel that is
/// demonstrably alive right now and in a natural lull, within budget, and outside quiet hours. Elapsed silence is
/// a veto, not a trigger (arXiv:2603.11409, "Speak or Stay Silent"). Fully unit-tested with an injected clock.
/// </summary>
public static class ColdOpenGate
{
    public static GateResult Evaluate(
        ChannelPulseSnapshot? pulse,
        ColdOpenOptions opts,
        DateTimeOffset now,
        int firedToday,
        DateTimeOffset? lastFiredAt)
    {
        if (pulse is null || pulse.LastHumanAt is not { } lastHuman)
        {
            return GateResult.No("cold"); // never seen a human here
        }

        var warmWindow = TimeSpan.FromMinutes(Math.Max(1, opts.WarmWindowMinutes));
        if (now - lastHuman > warmWindow)
        {
            return GateResult.No("silent"); // the channel has gone cold; never speak into silence
        }

        var minLull = TimeSpan.FromSeconds(Math.Max(0, opts.MinLullSeconds));
        if (now - lastHuman < minLull)
        {
            return GateResult.No("midflow"); // someone just spoke; do not talk over them
        }

        if (pulse.LastTypingAt is { } typing && now - typing < TimeSpan.FromSeconds(Math.Max(0, opts.TypingYieldSeconds)))
        {
            return GateResult.No("typing"); // someone is composing; yield the floor
        }

        if (pulse.LastBotAt is { } lastBot && now - lastBot < minLull)
        {
            return GateResult.No("botspoke"); // the bot itself just spoke here
        }

        if (pulse.DistinctHumansInWindow < Math.Max(1, opts.MinDistinctHumans))
        {
            return GateResult.No("quiet"); // not enough of a live conversation
        }

        if (firedToday >= Math.Max(0, opts.MaxPerDay))
        {
            return GateResult.No("dailycap");
        }

        if (lastFiredAt is { } last && now - last < TimeSpan.FromMinutes(Math.Max(0, opts.CooldownMinutes)))
        {
            return GateResult.No("cooldown");
        }

        if (IsQuietHours(now, opts))
        {
            return GateResult.No("quiethours");
        }

        return GateResult.Ok;
    }

    /// <summary>True if <paramref name="nowUtc"/> falls in the local quiet-hours window [start, end). start == end disables it.</summary>
    public static bool IsQuietHours(DateTimeOffset nowUtc, ColdOpenOptions opts)
    {
        var start = ((opts.QuietHoursStartLocal % 24) + 24) % 24;
        var end = ((opts.QuietHoursEndLocal % 24) + 24) % 24;
        if (start == end) return false; // disabled

        var hour = ToLocalHour(nowUtc, opts.TimeZone);
        return start < end
            ? hour >= start && hour < end     // same-day window, e.g. [1, 9)
            : hour >= start || hour < end;    // wraps midnight, e.g. [22, 6)
    }

    private static int ToLocalHour(DateTimeOffset nowUtc, string timeZoneId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId);
            return TimeZoneInfo.ConvertTime(nowUtc, tz).Hour;
        }
        catch (Exception) // unknown tz id: fall back to UTC rather than crash the gate
        {
            return nowUtc.UtcDateTime.Hour;
        }
    }
}
