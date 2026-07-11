using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using DiscordSky.Bot.Orchestration.Empire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Impulse;

/// <summary>
/// The proactive heartbeat for cold opens. Every PollSeconds it checks each opted-in channel and, only when the
/// never-into-silence gate passes (a live channel in a natural lull, within budget, outside quiet hours), asks
/// the composer whether Robotnik has a great unprompted line right now. In shadow mode it logs the would-be cold
/// open and never posts; live, it posts it (AllowedMentions.None). Self-gates on Enabled, serialized, fully
/// fail-open. Mirrors EmpireTickService in shape. All budget state is in-memory (a restart just resets the daily
/// counters, bounded by the cooldown).
/// </summary>
public sealed class ColdOpenService : IHostedService, IDisposable
{
    private sealed class ChannelBudget
    {
        public DateOnly Day;
        public int FiredToday;
        public DateTimeOffset? LastFiredAt;
        public DateTimeOffset? LastJudgedAt;
    }

    private readonly DiscordSocketClient _client;
    private readonly IOptionsMonitor<ColdOpenOptions> _options;
    private readonly ChannelPulseTracker _pulse;
    private readonly ColdOpenComposer _composer;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<ColdOpenService> _logger;
    private readonly EmpireStateStore? _empireState;
    private readonly RecentParticipants? _recentParticipants;
    private readonly ColdOpenCritic? _critic;
    private readonly SentMessageRegistry? _sentMessages;
    private readonly IColdOpenShadowSink? _providerShadow;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<ulong, ChannelBudget> _budgets = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ColdOpenService(
        DiscordSocketClient client,
        IOptionsMonitor<ColdOpenOptions> options,
        ChannelPulseTracker pulse,
        ColdOpenComposer composer,
        IRecallTelemetrySink telemetry,
        ILogger<ColdOpenService> logger,
        EmpireStateStore? empireState = null,
        RecentParticipants? recentParticipants = null,
        ColdOpenCritic? critic = null,
        SentMessageRegistry? sentMessages = null,
        IColdOpenShadowSink? providerShadow = null)
    {
        _client = client;
        _options = options;
        _pulse = pulse;
        _composer = composer;
        _telemetry = telemetry;
        _logger = logger;
        _empireState = empireState;
        _recentParticipants = recentParticipants;
        _critic = critic;
        _sentMessages = sentMessages;
        _providerShadow = providerShadow;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            _logger.LogInformation("Cold opens disabled.");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Cold opens enabled: {Mode}, {Channels} channel(s), poll {Poll}s, worth>={Worth:F2}, cap {Cap}/day, cooldown {Cooldown}m.",
            opts.ShadowMode ? "SHADOW (no posting)" : "LIVE", opts.Channels.Count, opts.PollSeconds, opts.WorthThreshold, opts.MaxPerDay, opts.CooldownMinutes);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop; }
            catch { /* shutting down */ }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(Math.Clamp(_options.CurrentValue.PollSeconds, 15, 300));
        try
        {
            using var timer = new PeriodicTimer(poll);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!await _lock.WaitAsync(0, ct)) continue;
                try
                {
                    await CheckOnceAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cold-open check failed; skipping this cycle.");
                }
                finally
                {
                    _lock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || opts.Channels.Count == 0) return;

        foreach (var target in opts.Channels)
        {
            var channel = ResolveChannel(target);
            if (channel is null) continue;

            var now = DateTimeOffset.UtcNow;
            var budget = GetBudget(channel.Id, now, opts);
            var window = TimeSpan.FromMinutes(Math.Max(1, opts.WarmWindowMinutes));
            var pulse = _pulse.Snapshot(channel.Id, window);

            var gate = ColdOpenGate.Evaluate(pulse, opts, now, budget.FiredToday, budget.LastFiredAt);
            if (!gate.Pass)
            {
                _logger.LogDebug("cold_open gate=veto reason={Reason} channel={Channel}", gate.Veto, channel.Name);
                continue;
            }

            // Cost bound: do not re-run the composer more often than the judge cooldown, even while a long lull
            // keeps the structural gate passing.
            var judgeCooldown = TimeSpan.FromMinutes(Math.Max(1, opts.JudgeCooldownMinutes));
            if (budget.LastJudgedAt is { } judged && now - judged < judgeCooldown) continue;
            budget.LastJudgedAt = now;

            var recentLines = await GatherRecentLinesAsync(channel);
            var context = BuildContext(recentLines);
            var evaluationId = Guid.NewGuid().ToString("N");
            var draft = await _composer.ComposeAsync(context, ct);
            _providerShadow?.TryEnqueue(new ColdOpenShadowOpportunity(
                EvaluationId: evaluationId,
                CapturedAt: now,
                Channel: channel.Name,
                Context: context,
                ChampionDraft: draft,
                WorthThreshold: opts.WorthThreshold,
                RoomLines: recentLines));
            if (draft is null || draft.Worth < opts.WorthThreshold)
            {
                Emit("declined", channel.Name, draft?.Hook, draft?.Worth, draft?.Line, recentLines, null, evaluationId, now);
                _logger.LogInformation("cold_open outcome=declined worth={Worth:F2} channel={Channel}", draft?.Worth ?? 0.0, channel.Name);
                continue; // a decline does NOT consume the fire cooldown or the daily cap, only the judge cooldown
            }

            // The critic is advisory and cannot block the line. Audit in the background so a second main-model
            // call does not delay a time-sensitive interruption; its result gets a separate durable event.
            if (_critic is not null) _ = ReviewCritiqueAsync(context, draft, channel.Name, recentLines, evaluationId, now, ct);

            if (opts.ShadowMode)
            {
                Emit("shadow", channel.Name, draft.Hook, draft.Worth, draft.Line, recentLines, null, evaluationId, now);
                _logger.LogInformation("cold_open outcome=shadow worth={Worth:F2} hook={Hook} channel={Channel} line={Line}",
                    draft.Worth, draft.Hook, channel.Name, draft.Line);
            }
            else
            {
                var sent = await channel.SendMessageAsync(draft.Line, allowedMentions: AllowedMentions.None);
                _sentMessages?.Register(sent.Id, GetPersona(), "cold_open");
                _pulse.RecordBot(channel.Id, DateTimeOffset.UtcNow);
                Emit("fired", channel.Name, draft.Hook, draft.Worth, draft.Line, recentLines, null, evaluationId, now);
                _logger.LogInformation("cold_open outcome=fired worth={Worth:F2} hook={Hook} channel={Channel}",
                    draft.Worth, draft.Hook, channel.Name);
            }

            // A fire (shadow or live) consumes the daily cap and the fire cooldown, so the shadow cadence mirrors live.
            budget.FiredToday++;
            budget.LastFiredAt = now;
        }
    }

    private ColdOpenContext BuildContext(IReadOnlyList<string> recentLines)
    {
        var state = _empireState?.Current;
        var mood = _empireState is { Enabled: true } ? state?.Mood.Label : null;
        var situation = state?.Body ?? string.Empty;
        var people = _recentParticipants?.Names(6) ?? Array.Empty<string>();
        return new ColdOpenContext(GetPersona(), mood, situation, people, recentLines);
    }

    private async Task ReviewCritiqueAsync(
        ColdOpenContext context,
        ColdOpenDraft draft,
        string channel,
        IReadOnlyList<string> roomLines,
        string evaluationId,
        DateTimeOffset opportunityAt,
        CancellationToken ct)
    {
        try
        {
            var critique = await _critic!.ReviewAsync(context, draft, ct);
            if (critique is null) return;

            var flaw = string.IsNullOrWhiteSpace(critique.Flaw) ? "-" : critique.Flaw;
            _logger.LogInformation(
                "cold_open_critic composer={Composer:F2} critic={Critic:F2} flaw={Flaw}",
                draft.Worth, critique.Worth, flaw);
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: DateTimeOffset.UtcNow,
                EventType: TelemetryEventTypes.ColdOpenCritique,
                Channel: channel,
                Kind: string.IsNullOrWhiteSpace(draft.Hook) ? null : draft.Hook,
                Outcome: flaw.Equals("clean", StringComparison.OrdinalIgnoreCase) ? "clean" : "flagged",
                TopScore: critique.Worth,
                Note: draft.Line,
                Reason: flaw,
                Room: roomLines,
                EvaluationId: evaluationId,
                OpportunityAt: opportunityAt));
        }
        catch (OperationCanceledException)
        {
            // Service shutdown; the line was already sent/logged and critique is advisory.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cold-open advisory critique failed after send.");
        }
    }

    /// <summary>
    /// Reads the recent human lines in the channel: this is the cold open's PRIMARY material, since a cold open
    /// must hook onto what the room actually cares about (round 1 eval: pure-internal-scheme cold opens read as
    /// detached noise). A slightly wider window than the first cut, to give a real topical hook or a genuine
    /// callback to catch. Bounded; the bot's own and other bots' messages are skipped; marked untrusted
    /// downstream. Fail-open to empty, in which case the composer has no hook and should stay silent.
    /// </summary>
    private async Task<IReadOnlyList<string>> GatherRecentLinesAsync(SocketTextChannel channel)
    {
        const int Fetch = 16;
        const int MaxLines = 8;
        const int MaxLineChars = 200;
        try
        {
            var recent = await channel.GetMessagesAsync(Fetch).FlattenAsync();
            return recent
                .Where(m => !m.Author.IsBot && !string.IsNullOrWhiteSpace(m.Content))
                .OrderBy(m => m.Timestamp)
                .TakeLast(MaxLines)
                .Select(m =>
                {
                    var name = (m.Author as SocketGuildUser)?.DisplayName ?? m.Author.Username;
                    var text = m.Content.Replace('\n', ' ').Replace('\r', ' ').Trim();
                    return text.Length > MaxLineChars ? $"{name}: {text[..MaxLineChars]}" : $"{name}: {text}";
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cold-open recent-line gather failed; proceeding with Empire State only.");
            return Array.Empty<string>();
        }
    }

    private static string GetPersona() => "Robotnik from Adventures of Sonic the Hedgehog";

    private SocketTextChannel? ResolveChannel(ColdOpenChannel target)
    {
        if (string.IsNullOrWhiteSpace(target.Channel)) return null;

        var guild = _client.Guilds.FirstOrDefault(g =>
            string.IsNullOrWhiteSpace(target.Guild) || string.Equals(g.Name, target.Guild, StringComparison.OrdinalIgnoreCase));
        var channel = guild?.TextChannels.FirstOrDefault(c => string.Equals(c.Name, target.Channel, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            _logger.LogDebug("cold_open channel not resolvable yet: guild={Guild} channel={Channel}", target.Guild, target.Channel);
        }
        return channel;
    }

    private ChannelBudget GetBudget(ulong channelId, DateTimeOffset now, ColdOpenOptions opts)
    {
        var today = LocalToday(now, opts.TimeZone);
        if (!_budgets.TryGetValue(channelId, out var budget))
        {
            budget = new ChannelBudget { Day = today };
            _budgets[channelId] = budget;
        }
        if (budget.Day != today)
        {
            budget.Day = today;
            budget.FiredToday = 0;
        }
        return budget;
    }

    private static DateOnly LocalToday(DateTimeOffset nowUtc, string tzId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(tzId) ? "UTC" : tzId);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime);
        }
        catch (Exception)
        {
            return DateOnly.FromDateTime(nowUtc.UtcDateTime);
        }
    }

    private void Emit(string outcome, string channel, string? hook, double? worth, string? line,
        IReadOnlyList<string>? roomLines, ColdOpenCritique? critique, string evaluationId, DateTimeOffset opportunityAt)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.ColdOpen,
            Channel: channel,
            Kind: string.IsNullOrWhiteSpace(hook) ? null : hook,
            Outcome: outcome,
            TopScore: worth,
            Note: string.IsNullOrWhiteSpace(line) ? null : line,
            Reason: critique is null ? null : $"critic {critique.Worth:F2} {(string.IsNullOrWhiteSpace(critique.Flaw) ? "-" : critique.Flaw)}",
            Room: roomLines is { Count: > 0 } ? roomLines : null,
            EvaluationId: evaluationId,
            OpportunityAt: opportunityAt));
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _lock.Dispose();
    }
}
