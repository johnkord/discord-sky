using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// The read side of AutoMod. <see cref="AutoModSyncService"/> writes the native rules; this reacts to them.
/// When Discord's AutoMod blocks/alerts/times-out a message, we record a durable "automod_action" telemetry
/// event, so we can actually measure whether the rules ever fire (they are otherwise fire-and-forget). When
/// <see cref="AutoModOptions.TauntOnBlock"/> is set, Robotnik also posts a canned in-character line in the
/// origin channel each time one of OUR rules blocks a message.
///
/// Requires the <see cref="GatewayIntents.AutoModerationActionExecution"/> intent (added in Program.cs). The
/// handler runs off the gateway dispatch thread and is fully fail-open: telemetry and taunts must never disrupt
/// the bot. Discord dispatches one event per action, so a rule with both an alert and a block action fires this
/// twice; the taunt keys on the block action only and is additionally rate-limited per channel.
/// </summary>
public sealed class AutoModActionResponder : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly AutoModOptions _options;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<AutoModActionResponder> _logger;
    private readonly IRandomProvider _random;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _tauntCooldown = new();

    public AutoModActionResponder(
        DiscordSocketClient client,
        IOptions<AutoModOptions> options,
        IRecallTelemetrySink telemetry,
        ILogger<AutoModActionResponder> logger,
        IRandomProvider? random = null)
    {
        _client = client;
        _options = options.Value;
        _telemetry = telemetry;
        _logger = logger;
        _random = random ?? DefaultRandomProvider.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.ReactToActions)
        {
            _logger.LogInformation("AutoMod action responder disabled.");
            return Task.CompletedTask;
        }

        _client.AutoModActionExecuted += OnAutoModActionExecutedAsync;
        _logger.LogInformation("AutoMod action responder enabled (tauntOnBlock={Taunt}).", _options.TauntOnBlock);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.AutoModActionExecuted -= OnAutoModActionExecutedAsync;
        return Task.CompletedTask;
    }

    // Never block the gateway dispatch thread (this bot has a history of disconnect sensitivity): hand off and
    // return immediately.
    private Task OnAutoModActionExecutedAsync(SocketGuild guild, AutoModRuleAction action, AutoModActionExecutedData data)
    {
        _ = Task.Run(() => HandleAsync(guild, action, data));
        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketGuild guild, AutoModRuleAction action, AutoModActionExecutedData data)
    {
        try
        {
            var ruleName = await TryResolveRuleNameAsync(data);
            var mine = IsOurRule(ruleName, _options.RuleNamePrefix);
            var channelName = data.Channel.Value?.Name
                ?? guild.GetTextChannel(data.Channel.Id)?.Name
                ?? data.Channel.Id.ToString();
            var outcome = MapOutcome(action.Type);
            var reason = BuildReason(ruleName, data.Rule.Id, data.TriggerType, data.MatchedKeyword);

            EmitTelemetry(data, channelName, outcome, reason);

            _logger.LogInformation(
                "automod_action rule={Rule} mine={Mine} trigger={Trigger} action={Action} user={User} channel={Channel} kw={Keyword}",
                ruleName ?? data.Rule.Id.ToString(), mine, data.TriggerType, action.Type, data.User.Id, channelName,
                Trim(data.MatchedKeyword, 40));

            if (_options.TauntOnBlock && mine && action.Type == AutoModActionType.BlockMessage)
            {
                await MaybeTauntAsync(guild, data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AutoMod action handling failed; ignoring.");
        }
    }

    private async Task<string?> TryResolveRuleNameAsync(AutoModActionExecutedData data)
    {
        try
        {
            var rule = await data.Rule.GetOrDownloadAsync();
            return rule?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve AutoMod rule {RuleId} name.", data.Rule.Id);
            return null;
        }
    }

    private void EmitTelemetry(AutoModActionExecutedData data, string channelName, string outcome, string reason)
    {
        try
        {
            _telemetry.Emit(new TelemetryEvent(
                DateTimeOffset.UtcNow,
                TelemetryEventTypes.AutoModAction,
                UserHash: UserIdHash.Hash(data.User.Id),
                Channel: channelName,
                Kind: "automod",
                Outcome: outcome,
                Reason: reason,
                MessageId: data.Message?.Id));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit AutoMod telemetry.");
        }
    }

    private async Task MaybeTauntAsync(SocketGuild guild, AutoModActionExecutedData data)
    {
        ISocketMessageChannel? channel = data.Channel.Value ?? guild.GetTextChannel(data.Channel.Id);
        if (channel is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _options.TauntCooldownSeconds));
        if (_tauntCooldown.TryGetValue(channel.Id, out var last) && now - last < cooldown)
        {
            return; // Stay quiet during a burst so a raid does not become Robotnik spam.
        }
        _tauntCooldown[channel.Id] = now;

        try
        {
            await channel.SendMessageAsync(
                AutoModBlockTaunts.Random(_random),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to post AutoMod taunt in channel {Channel}.", channel.Id);
        }
    }

    // ---- Pure decision helpers (unit-tested) ----

    /// <summary>Maps a Discord AutoMod action type to the telemetry outcome string.</summary>
    internal static string MapOutcome(AutoModActionType type) => type switch
    {
        AutoModActionType.BlockMessage => "blocked",
        AutoModActionType.SendAlertMessage => "alerted",
        AutoModActionType.Timeout => "timeout",
        AutoModActionType.BlockMemberInteraction => "blocked_interaction",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>True when the rule that fired is one of ours (name starts with our prefix).</summary>
    internal static bool IsOurRule(string? ruleName, string prefix) =>
        !string.IsNullOrWhiteSpace(ruleName)
        && !string.IsNullOrWhiteSpace(prefix)
        && ruleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the compact telemetry reason string; falls back to the rule id when the name is unknown.</summary>
    internal static string BuildReason(string? ruleName, ulong ruleId, AutoModTriggerType trigger, string? matchedKeyword)
    {
        var rule = string.IsNullOrWhiteSpace(ruleName) ? ruleId.ToString() : ruleName;
        var kw = Trim(matchedKeyword, 40);
        return string.IsNullOrEmpty(kw)
            ? $"rule={rule};trigger={trigger}"
            : $"rule={rule};trigger={trigger};kw={kw}";
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }
}
