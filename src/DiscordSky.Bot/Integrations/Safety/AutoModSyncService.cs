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
            foreach (var guild in _client.Guilds)
            {
                try
                {
                    await ReconcileGuildAsync(guild);
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

    private async Task ReconcileGuildAsync(SocketGuild guild)
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

        var exemptChannelIds = _options.ExemptChannelNames.Count == 0
            ? Array.Empty<ulong>()
            : guild.TextChannels
                .Where(c => _options.ExemptChannelNames.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.Id)
                .ToArray();

        ulong? alertChannelId = null;
        if (!string.IsNullOrWhiteSpace(_options.AlertChannelName))
        {
            alertChannelId = guild.TextChannels
                .FirstOrDefault(c => string.Equals(c.Name, _options.AlertChannelName, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        var rules = (await guild.GetAutoModRulesAsync()).Cast<IAutoModRule>().ToList();
        IAutoModRule? Find(string name) =>
            rules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
                && r.CreatorId == _client.CurrentUser.Id);

        var blockName = _options.RuleNamePrefix + "-block";
        var alertName = _options.RuleNamePrefix + "-alert";
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Block tier: lookalike domains + moderator-reported hosts. High precision, evasion-resistant -> block.
        var blockPlan = AutoModRuleBuilder.BuildBlockPlan(
            ScamLinkDetector.BuiltInLookalikePattern, _learned?.Hosts ?? Array.Empty<string>());
        var blockActions = new List<AutoModRuleActionProperties>();
        if (alertChannelId is not null)
        {
            blockActions.Add(new AutoModRuleActionProperties { Type = AutoModActionType.SendAlertMessage, ChannelId = alertChannelId });
        }
        if (_options.BlockLookalikes)
        {
            blockActions.Add(new AutoModRuleActionProperties { Type = AutoModActionType.BlockMessage, CustomMessage = Truncate(_options.BlockMessageText, 50) });
        }
        if (await ApplyRuleAsync(guild, Find(blockName), blockName, blockPlan, blockActions, exemptChannelIds, "block"))
        {
            keep.Add(blockName);
        }

        // Alert tier: scam phrases. FP-prone and easily mutated -> alert only (needs an alert channel).
        if (_options.AlertPhrases && alertChannelId is not null)
        {
            var alertPlan = AutoModRuleBuilder.BuildAlertPlan(
                ScamLinkDetector.BuiltInScamPhrases, _learned?.Phrases ?? Array.Empty<string>());
            var alertActions = new List<AutoModRuleActionProperties>
            {
                new() { Type = AutoModActionType.SendAlertMessage, ChannelId = alertChannelId },
            };
            if (await ApplyRuleAsync(guild, Find(alertName), alertName, alertPlan, alertActions, exemptChannelIds, "alert"))
            {
                keep.Add(alertName);
            }
        }

        // Cleanup: delete any rule we previously created under this prefix that we are no longer keeping
        // (the old single "sky-scamguard" rule, or a tier that got disabled/emptied).
        foreach (var rule in rules)
        {
            if (rule.CreatorId == _client.CurrentUser.Id
                && rule.Name.StartsWith(_options.RuleNamePrefix, StringComparison.OrdinalIgnoreCase)
                && !keep.Contains(rule.Name))
            {
                await rule.DeleteAsync();
                _logger.LogInformation("automod_synced action=deleted guild={Guild} rule={Rule}", guild.Name, rule.Name);
            }
        }
    }

    private async Task<bool> ApplyRuleAsync(
        SocketGuild guild, IAutoModRule? existing, string ruleName,
        AutoModRulePlan plan, List<AutoModRuleActionProperties> actions, ulong[] exemptChannelIds, string tier)
    {
        var hasContent = plan.Keywords.Count > 0 || plan.RegexPatterns.Count > 0;
        if (!hasContent || actions.Count == 0)
        {
            return false;
        }

        void Apply(AutoModRuleProperties props)
        {
            props.Name = ruleName;
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

        var blocks = actions.Any(a => a.Type == AutoModActionType.BlockMessage);
        if (existing is null)
        {
            await guild.CreateAutoModRuleAsync(Apply);
            _logger.LogInformation(
                "automod_synced action=created guild={Guild} rule={Rule} tier={Tier} keywords={Keywords} regex={Regex} block={Block}",
                guild.Name, ruleName, tier, plan.Keywords.Count, plan.RegexPatterns.Count, blocks);
        }
        else
        {
            await existing.ModifyAsync(Apply);
            _logger.LogInformation(
                "automod_synced action=updated guild={Guild} rule={Rule} tier={Tier} keywords={Keywords} regex={Regex} block={Block}",
                guild.Name, ruleName, tier, plan.Keywords.Count, plan.RegexPatterns.Count, blocks);
        }

        return true;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
