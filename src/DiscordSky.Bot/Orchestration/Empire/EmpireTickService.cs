using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// The background heartbeat that acts like sleep: every TickIntervalHours it advances Robotnik's world a beat
/// and consolidates his log. Mirrors GreatestHitsRefreshService (IHostedService plus PeriodicTimer, self-gating,
/// fully fail-safe). It waits one interval before the first tick so a deploy does not tick immediately.
/// </summary>
public sealed class EmpireTickService : IHostedService, IDisposable
{
    private readonly EmpireStateStore _store;
    private readonly RecentParticipants _participants;
    private readonly EmpireBodyConsolidator _consolidator;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<EmpireTickService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public EmpireTickService(
        EmpireStateStore store,
        RecentParticipants participants,
        EmpireBodyConsolidator consolidator,
        IRecallTelemetrySink telemetry,
        ILogger<EmpireTickService> logger)
    {
        _store = store;
        _participants = participants;
        _consolidator = consolidator;
        _telemetry = telemetry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_store.Enabled)
        {
            _logger.LogInformation("Empire state disabled.");
            return Task.CompletedTask;
        }
        _logger.LogInformation("Empire state enabled: tick every {Hours}h, LLM body {Llm}.",
            _store.Options.TickIntervalHours, _store.Options.EnableLlmBody ? "on" : "off");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch
            {
                // Shutting down; ignore.
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var hours = Math.Max(1, _store.Options.TickIntervalHours);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
            // Wait one interval before the first tick so a restart does not tick immediately.
            while (await timer.WaitForNextTickAsync(ct))
            {
                await TickAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Runs one tick immediately for owner validation, bypassing the interval and the activity gate. Returns the outcome.</summary>
    public Task<string> ForceTickAsync(CancellationToken ct) => TickAsync(ct, forced: true);

    private async Task<string> TickAsync(CancellationToken ct, bool forced = false)
    {
        try
        {
            var state = _store.Current;

            // Activity gate: do not evolve a world nobody is watching. Do NOT stamp lastTickAt on a skip, so the
            // next tick after any activity still fires. A forced (owner) tick bypasses the gate.
            if (!forced && !_participants.AnyActivitySince(state.LastTickAt))
            {
                Emit("skipped", state.Mood.Label, state.Body.Length);
                _logger.LogInformation("empire_tick outcome=skipped mood={Mood} (no activity since last tick)", state.Mood.Label);
                return "skipped";
            }

            var opts = _store.Options;
            var (mood, agedRanks) = EmpireTick.Advance(state, opts, pending: null);
            var body = state.Body;
            var ranks = agedRanks;
            var outcome = "committed";

            if (opts.EnableLlmBody)
            {
                var candidates = _participants.Names(opts.CandidateSampleSize);
                var consolidation = await _consolidator.ConsolidateAsync(state, candidates, opts, ct);
                if (consolidation is not null)
                {
                    body = consolidation.Body;
                    ranks = EmpireTick.MergeRankOps(agedRanks, consolidation.RankOps, opts);
                }
                else
                {
                    // The rewrite failed or did not verify; keep the old body but still advance mood/ranks/time.
                    outcome = "rejected";
                }
            }

            var next = state with
            {
                Mood = mood,
                Ranks = ranks,
                Body = body,
                LastTickAt = DateTimeOffset.UtcNow,
            };
            _store.Commit(next);

            Emit(outcome, mood.Label, body.Length);
            _logger.LogInformation("empire_tick outcome={Outcome} mood={Mood} bodyLen={Len} ranks={Ranks} version={Version}",
                outcome, mood.Label, body.Length, ranks.Count, _store.Current.Version);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empire tick failed; state unchanged.");
            return "error";
        }
    }

    private void Emit(string outcome, string moodLabel, int bodyLen)
        => _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.EmpireTick,
            Kind: moodLabel,
            Outcome: outcome,
            Count: bodyLen));

    public void Dispose() => _cts?.Dispose();
}
