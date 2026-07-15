using Discord;

namespace DiscordSky.Bot.Integrations.Safety;

internal sealed record AutoModRuleSnapshot(
    string Name,
    AutoModEventType EventType,
    AutoModTriggerType TriggerType,
    IReadOnlyCollection<string> KeywordFilter,
    IReadOnlyCollection<string> RegexPatterns,
    IReadOnlyCollection<string> AllowList,
    IReadOnlyCollection<KeywordPresetTypes> Presets,
    int? MentionLimit,
    bool? MentionRaidProtectionEnabled,
    IReadOnlyCollection<AutoModActionSnapshot> Actions,
    bool Enabled,
    IReadOnlyCollection<ulong> ExemptRoles,
    IReadOnlyCollection<ulong> ExemptChannels)
{
    internal static AutoModRuleSnapshot CreateDesired(
        string name,
        AutoModTriggerType triggerType,
        IEnumerable<AutoModRuleActionProperties> actions,
        IEnumerable<ulong> exemptChannels,
        IEnumerable<string>? keywordFilter = null,
        IEnumerable<string>? regexPatterns = null,
        IEnumerable<string>? allowList = null,
        int? mentionLimit = null,
        bool? mentionRaidProtectionEnabled = null) =>
        new(
            name,
            AutoModEventType.MessageSend,
            triggerType,
            (keywordFilter ?? []).ToArray(),
            (regexPatterns ?? []).ToArray(),
            (allowList ?? []).ToArray(),
            [],
            mentionLimit,
            mentionRaidProtectionEnabled,
            actions.Select(AutoModActionSnapshot.From).ToArray(),
            true,
            [],
            exemptChannels.ToArray());

    internal static AutoModRuleSnapshot From(IAutoModRule rule) =>
        new(
            rule.Name,
            rule.EventType,
            rule.TriggerType,
            rule.KeywordFilter,
            rule.RegexPatterns,
            rule.AllowList,
            rule.Presets,
            rule.MentionTotalLimit,
            rule.MentionRaidProtectionEnabled,
            rule.Actions.Select(AutoModActionSnapshot.From).ToArray(),
            rule.Enabled,
            rule.ExemptRoles,
            rule.ExemptChannels);

    internal bool SemanticallyEquals(AutoModRuleSnapshot other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && EventType == other.EventType
        && TriggerType == other.TriggerType
        && SetEquals(KeywordFilter, other.KeywordFilter, StringComparer.Ordinal)
        && SetEquals(RegexPatterns, other.RegexPatterns, StringComparer.Ordinal)
        && SetEquals(AllowList, other.AllowList, StringComparer.Ordinal)
        && SetEquals(Presets, other.Presets)
        && MentionLimit == other.MentionLimit
        && MentionRaidProtectionEnabled == other.MentionRaidProtectionEnabled
        && SetEquals(Actions, other.Actions)
        && Enabled == other.Enabled
        && SetEquals(ExemptRoles, other.ExemptRoles)
        && SetEquals(ExemptChannels, other.ExemptChannels);

    private static bool SetEquals<T>(
        IEnumerable<T> left,
        IEnumerable<T> right,
        IEqualityComparer<T>? comparer = null) =>
        new HashSet<T>(left, comparer).SetEquals(right);
}

internal sealed record AutoModActionSnapshot(
    AutoModActionType Type,
    ulong? ChannelId,
    TimeSpan? TimeoutDuration)
{
    // Discord omits custom_message from GET rule responses even immediately after accepting it on update.
    // It is therefore write-only for reconciliation: Apply still sets it, but equality uses observable fields.
    internal static AutoModActionSnapshot From(AutoModRuleActionProperties action) =>
        new(
            action.Type,
            action.ChannelId,
            action.TimeoutDuration);

    internal static AutoModActionSnapshot From(AutoModRuleAction action) =>
        new(
            action.Type,
            action.ChannelId,
            action.TimeoutDuration);
}