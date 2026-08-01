using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed class WorldAutonomyStewardHealthService(
    WorldAutonomyConfiguration configuration,
    StewardMcpSupervisor supervisor,
    ILogger<WorldAutonomyStewardHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PerGuildTimeout = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.IsEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var guildId in configuration.EnabledGuilds.Keys.Order())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(PerGuildTimeout);
                try
                {
                    await supervisor.GetSessionAsync(guildId, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Steward health repair failed for guild {GuildId}.", guildId);
                }
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}