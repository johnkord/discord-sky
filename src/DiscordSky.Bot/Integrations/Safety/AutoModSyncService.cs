using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Keeps a native Discord AutoMod keyword rule in sync with ScamGuard's lists, so scam phrases and lookalike
/// hosts are blocked or alerted BEFORE a message posts, for every sender including bots and webhooks. Mirrors
/// <see cref="PhishingDomainFeed"/> in shape: reconciles on Ready and on a timer, per guild, permission-gated,
/// idempotent (only ever touches a rule it created with our name), and fully fail-open.
/// </summary>
public sealed class AutoModSyncService : IHostedService, IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly AutoModOptions _options;
    private readonly LearnedScamStore? _learned;
    private readonly ILogger<AutoModSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    public AutoModSyncService(
        DiscordSocketClient client,
        IOptions<AutoModOptions> options,
        ILogger<AutoModSyncService> logger,
        LearnedScamStore? learned = null)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
        _learned = learned;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AutoMod sync disabled.");
            return Task.CompletedTask;
        }

        _client.Ready += OnReadyAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Ready -= OnReadyAsync;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    private Task OnReadyAsync()
    {
        // Reconcile off the gateway thread; re-sync every 15 minutes to pick up learned-list additions.
        _ = Task.Run(ReconcileAllAsync);
        _timer = new Timer(_ => _ = ReconcileAllAsync(), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        return Task.CompletedTask;
    }

    private async Task ReconcileAllAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var plan = AutoModRuleBuilder.Build(
                ScamLinkDetector.BuiltInScamPhrases,
                ScamLinkDetector.BuiltInLookalikePattern,
                _learned?.Phrases ?? Array.Empty<string>(),
                _learned?.Hosts ?? Array.Empty<string>(),
                _options.IncludePhrases,
                _options.IncludeLookalikeRegex);

            if (plan.Keywords.Count == 0 && plan.RegexPatterns.Count == 0)
            {
                _logger.LogWarning("AutoMod sync: nothing to sync (phrases and regex both empty/disabled).");
                return;
            }

            foreach (var guild in _client.Guilds)
            {
                try
                {
                    await ReconcileGuildAsync(guild, plan);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AutoMod sync failed for guild {Guild}.", guild.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoMod sync: reconcile pass failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileGuildAsync(SocketGuild guild, AutoModRulePlan plan)
    {
        if (_options.GuildAllowList.Count > 0
            && !_options.GuildAllowList.Any(g => string.Equals(g, guild.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!guild.CurrentUser.GuildPermissions.ManageGuild)
        {
            _logger.LogInformation("AutoMod sync: no Manage Server in {Guild}; skipping.", guild.Name);
            return;
        }

        var actions = BuildActions(guild);
        if (actions.Count == 0)
        {
            _logger.LogWarning(
                "AutoMod sync: no actions resolvable for {Guild} (alert channel '{Channel}' not found and blocking off); skipping.",
                guild.Name, _options.AlertChannelName);
            return;
        }

        var exemptChannelIds = _options.ExemptChannelNames.Count == 0
            ? Array.Empty<ulong>()
            : guild.TextChannels
                .Where(c => _options.ExemptChannelNames.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.Id)
                .ToArray();

        void Apply(AutoModRuleProperties props)
        {
            props.Name = _options.RuleName;
            props.TriggerType = AutoModTriggerType.Keyword;
            props.EventType = AutoModEventType.MessageSend;
            props.KeywordFilter = plan.Keywords.ToArray();
            props.RegexPatterns = plan.RegexPatterns.ToArray();
            props.AllowList = plan.AllowList.ToArray();
            props.Actions = actions.ToArray();
            props.Enabled = true;
            if (exemptChannelIds.Length > 0)
            {
                props.ExemptChannels = exemptChannelIds;
            }
        }

        var rules = await guild.GetAutoModRulesAsync();
        var existing = rules.Cast<IAutoModRule>().FirstOrDefault(r =>
            string.Equals(r.Name, _options.RuleName, StringComparison.OrdinalIgnoreCase)
            && r.CreatorId == _client.CurrentUser.Id);

        if (existing is null)
        {
            await guild.CreateAutoModRuleAsync(Apply);
            _logger.LogInformation(
                "automod_synced action=created guild={Guild} rule={Rule} keywords={Keywords} regex={Regex} block={Block}",
                guild.Name, _options.RuleName, plan.Keywords.Count, plan.RegexPatterns.Count, _options.BlockMessages);
        }
        else
        {
            await existing.ModifyAsync(Apply);
            _logger.LogInformation(
                "automod_synced action=updated guild={Guild} rule={Rule} keywords={Keywords} regex={Regex} block={Block}",
                guild.Name, _options.RuleName, plan.Keywords.Count, plan.RegexPatterns.Count, _options.BlockMessages);
        }
    }

    private List<AutoModRuleActionProperties> BuildActions(SocketGuild guild)
    {
        var actions = new List<AutoModRuleActionProperties>();

        if (!string.IsNullOrWhiteSpace(_options.AlertChannelName))
        {
            var alertChannel = guild.TextChannels.FirstOrDefault(
                c => string.Equals(c.Name, _options.AlertChannelName, StringComparison.OrdinalIgnoreCase));
            if (alertChannel is not null)
            {
                actions.Add(new AutoModRuleActionProperties
                {
                    Type = AutoModActionType.SendAlertMessage,
                    ChannelId = alertChannel.Id,
                });
            }
        }

        if (_options.BlockMessages)
        {
            actions.Add(new AutoModRuleActionProperties
            {
                Type = AutoModActionType.BlockMessage,
                CustomMessage = Truncate(_options.BlockMessageText, 150),
            });
        }

        return actions;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
