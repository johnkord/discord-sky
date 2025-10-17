using System;
using System.Collections.Generic;

namespace DiscordSky.Bot.Configuration;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; init; } = string.Empty;
    public ulong? GuildId { get; init; }
    public ulong? HomeChannelId { get; init; }
    public string Status { get; init; } = "Brewing chaos";
    public List<string> AllowedChannelNames { get; init; } = new();
    public string CommandPrefix { get; init; } = "!sky";
    public int HistoryMessageLimit { get; init; } = 20;
    public string DefaultPersona { get; init; } = "Weird Al";
    public bool AllowImageContext { get; init; } = true;
    public int HistoryImageLimit { get; init; } = 3;
    public List<string> ImageHostAllowList { get; init; } = new()
    {
        "cdn.discordapp.com",
        "media.discordapp.net"
    };

    public bool IsChannelAllowed(string? channelName)
    {
        if (AllowedChannelNames.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        return AllowedChannelNames.Any(allowed => string.Equals(allowed, channelName, StringComparison.OrdinalIgnoreCase));
    }
}
