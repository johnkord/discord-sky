using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Members;

/// <summary>
/// Handles <c>UserJoined</c>: in-house mass-join raid detection (a sliding-window counter that alerts once when
/// it trips) plus an optional in-character greeting (suppressed during a raid so the bot never amplifies one).
/// Off unless <see cref="MemberEventsOptions.Enabled"/>. Requires the privileged GuildMembers intent, which
/// Program.cs only requests when the feature is enabled, so a default deploy is safe. Runs off the gateway
/// thread and is fully fail-open.
/// </summary>
public sealed class MemberJoinService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly MemberEventsOptions _options;
    private readonly JoinRaidTracker _tracker;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<MemberJoinService> _logger;
    private readonly IRandomProvider _random;

    public MemberJoinService(
        DiscordSocketClient client,
        IOptions<MemberEventsOptions> options,
        JoinRaidTracker tracker,
        IRecallTelemetrySink telemetry,
        ILogger<MemberJoinService> logger,
        IRandomProvider? random = null)
    {
        _client = client;
        _options = options.Value;
        _tracker = tracker;
        _telemetry = telemetry;
        _logger = logger;
        _random = random ?? DefaultRandomProvider.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Member events disabled.");
            return Task.CompletedTask;
        }

        _client.UserJoined += OnUserJoinedAsync;
        _logger.LogInformation(
            "Member events enabled (greet={Greet}, raid={Threshold}/{Window}s).",
            _options.GreetNewMembers, _options.JoinRaidThreshold, _options.JoinRaidWindowSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.UserJoined -= OnUserJoinedAsync;
        return Task.CompletedTask;
    }

    private Task OnUserJoinedAsync(SocketGuildUser member)
    {
        _ = Task.Run(() => HandleAsync(member));
        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketGuildUser member)
    {
        try
        {
            var guild = member.Guild;
            if (_options.GuildAllowList.Count > 0
                && !_options.GuildAllowList.Any(g => string.Equals(g, guild.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var result = _tracker.Record(
                guild.Id, DateTimeOffset.UtcNow, _options.JoinRaidWindowSeconds, _options.JoinRaidThreshold);

            if (result.IsRaid)
            {
                if (result.JustCrossed)
                {
                    await AlertRaidAsync(guild, result.CountInWindow);
                }

                return; // Stay quiet during a raid: do not greet each raider.
            }

            if (_options.GreetNewMembers)
            {
                await GreetAsync(guild, member);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Member-join handling failed; ignoring.");
        }
    }

    private async Task GreetAsync(SocketGuild guild, SocketGuildUser member)
    {
        var channel = ResolveChannel(guild, _options.GreetChannelName) ?? guild.SystemChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.SendMessageAsync(
                MemberGreetings.Random(_random, member.DisplayName ?? member.Username),
                allowedMentions: AllowedMentions.None);
            _logger.LogInformation("member_greeted guild={Guild} user={User}", guild.Name, member.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to greet in {Guild}.", guild.Name);
        }
    }

    private async Task AlertRaidAsync(SocketGuild guild, int count)
    {
        _logger.LogWarning("join_raid guild={Guild} count={Count} window={Window}s", guild.Name, count, _options.JoinRaidWindowSeconds);

        try
        {
            _telemetry.Emit(new TelemetryEvent(
                DateTimeOffset.UtcNow,
                TelemetryEventTypes.AutoModAction,
                Channel: guild.Name,
                Kind: "join_raid",
                Outcome: "alerted",
                Count: count,
                Reason: $"joins={count};window={_options.JoinRaidWindowSeconds}s"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit join-raid telemetry.");
        }

        var channel = ResolveChannel(guild, _options.AlertChannelName);
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.SendMessageAsync(
                $"INTRUDER ALERT. {count} newcomers breached the gates within {_options.JoinRaidWindowSeconds} seconds. A raid, or my empire is simply irresistible. Mods, inspect the rabble.",
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to post join-raid alert in {Guild}.", guild.Name);
        }
    }

    private static SocketTextChannel? ResolveChannel(SocketGuild guild, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : guild.TextChannels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
