using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Learn-from-bans loop: subscribes to <c>UserBanned</c> and records every ban as a labeled spam event. For each
/// ban it emits a <c>ban_observed</c> telemetry event tagged <c>predicted</c> (the new-account watch had already
/// flagged this account) or <c>missed</c> (it had not), plus the banned account's age. This is the only source of
/// real predicted-vs-missed measurement for the safety layer: without it, the bot cannot even count its own
/// misses (which is exactly how the 2026-07-02 incident went unnoticed). Non-privileged (GuildBans intent).
/// Fully fail-open and runs off the gateway thread.
/// </summary>
public sealed class BanWatchService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly NewAccountFlagLog _flagLog;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ScamGuardOptions _scamGuard;
    private readonly ILogger<BanWatchService> _logger;

    public BanWatchService(
        DiscordSocketClient client,
        NewAccountFlagLog flagLog,
        IRecallTelemetrySink telemetry,
        IOptions<ScamGuardOptions> scamGuard,
        ILogger<BanWatchService> logger)
    {
        _client = client;
        _flagLog = flagLog;
        _telemetry = telemetry;
        _scamGuard = scamGuard.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_scamGuard.Enabled)
        {
            _logger.LogInformation("Ban watch disabled (ScamGuard disabled).");
            return Task.CompletedTask;
        }

        _client.UserBanned += OnUserBannedAsync;
        _logger.LogInformation("Ban watch enabled: bans are recorded as predicted/missed spam labels.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.UserBanned -= OnUserBannedAsync;
        return Task.CompletedTask;
    }

    private Task OnUserBannedAsync(SocketUser user, SocketGuild guild)
    {
        _ = Task.Run(() => HandleAsync(user, guild));
        return Task.CompletedTask;
    }

    private void HandleAsync(SocketUser user, SocketGuild guild)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var ageDays = Math.Max(0, (int)(now - user.CreatedAt).TotalDays);
            var predicted = _flagLog.WasFlaggedWithin(user.Id, now, TimeSpan.FromHours(24));
            string? reason = predicted && _flagLog.TryGet(user.Id, out var record) ? record.Reason : null;

            _telemetry.Emit(new TelemetryEvent(
                now,
                TelemetryEventTypes.BanObserved,
                UserHash: UserIdHash.Hash(user.Id),
                Channel: guild.Name,
                Kind: user.IsBot ? "bot" : "user",
                Outcome: predicted ? "predicted" : "missed",
                Count: ageDays,
                Reason: reason));

            _logger.LogInformation(
                "ban_observed guild={Guild} outcome={Outcome} acctAgeDays={Age} bot={Bot}",
                guild.Name, predicted ? "predicted" : "missed", ageDays, user.IsBot);

            _flagLog.Prune(now, TimeSpan.FromHours(48));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ban-watch handling failed; ignoring.");
        }
    }
}
