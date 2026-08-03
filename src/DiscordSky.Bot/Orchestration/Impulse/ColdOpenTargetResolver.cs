using DiscordSky.Bot.Configuration;

namespace DiscordSky.Bot.Orchestration.Impulse;

internal sealed record ColdOpenResolvableChannel(
    ulong GuildId,
    string GuildName,
    ulong ChannelId,
    string ChannelName);

internal sealed record ColdOpenTargetResolution(
    ulong? ChannelId,
    string Status)
{
    internal bool IsResolved => ChannelId.HasValue;
}

internal static class ColdOpenTargetResolver
{
    internal static ColdOpenTargetResolution Resolve(
        ColdOpenChannel target,
        IEnumerable<ColdOpenResolvableChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(channels);
        var candidates = channels.ToArray();
        var hasGuildId = target.GuildId is > 0;
        var hasChannelId = target.ChannelId is > 0;
        if (hasGuildId != hasChannelId || target.GuildId == 0 || target.ChannelId == 0)
        {
            return new ColdOpenTargetResolution(null, "invalid_partial_ids");
        }

        if (hasGuildId)
        {
            var byChannelId = candidates.Where(candidate => candidate.ChannelId == target.ChannelId).ToArray();
            if (byChannelId.Length == 0)
            {
                return new ColdOpenTargetResolution(null, "channel_id_unresolved");
            }
            if (byChannelId.All(candidate => candidate.GuildId != target.GuildId))
            {
                return new ColdOpenTargetResolution(null, "guild_id_mismatch");
            }

            var exact = byChannelId.Single(candidate => candidate.GuildId == target.GuildId);
            return new ColdOpenTargetResolution(exact.ChannelId, "resolved_id");
        }

        if (string.IsNullOrWhiteSpace(target.Channel))
        {
            return new ColdOpenTargetResolution(null, "channel_name_missing");
        }

        var nameMatches = candidates
            .Where(candidate => string.IsNullOrWhiteSpace(target.Guild) ||
                string.Equals(candidate.GuildName, target.Guild.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.Equals(
                candidate.ChannelName,
                target.Channel.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return nameMatches.Length switch
        {
            1 => new ColdOpenTargetResolution(nameMatches[0].ChannelId, "resolved_name_fallback"),
            > 1 => new ColdOpenTargetResolution(null, "name_fallback_ambiguous"),
            _ => new ColdOpenTargetResolution(null, "name_fallback_unresolved"),
        };
    }
}