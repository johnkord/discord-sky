using DiscordSky.Bot.Configuration;

namespace DiscordSky.Tests;

public sealed class BotGuildPolicyTests
{
    [Theory]
    [InlineData(42UL, "Renamed Guild", true)]
    [InlineData(7UL, "Flavorful Feral's Funhouse", true)]
    [InlineData(7UL, "flavorful feral's funhouse", true)]
    [InlineData(7UL, "Dr. Robotnik's Imperial Ballroom", false)]
    [InlineData(7UL, null, false)]
    public void IsGuildDisabled_MatchesExactIdOrCaseInsensitiveName(
        ulong guildId,
        string? guildName,
        bool expected)
    {
        var options = new BotOptions
        {
            DisabledGuildIds = [42],
            DisabledGuildNames = ["Flavorful Feral's Funhouse"],
        };

        Assert.Equal(expected, options.IsGuildDisabled(guildId, guildName));
    }
}