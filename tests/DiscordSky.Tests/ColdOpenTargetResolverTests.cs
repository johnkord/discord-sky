using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Impulse;

namespace DiscordSky.Tests;

public sealed class ColdOpenTargetResolverTests
{
    private static readonly ColdOpenResolvableChannel[] Channels =
    [
        new(100, "Renamed Guild", 200, "renamed-channel"),
        new(101, "Other Guild", 201, "general"),
        new(102, "Third Guild", 202, "general"),
    ];

    [Fact]
    public void Resolve_ExactIdsSurviveGuildAndChannelRenames()
    {
        var result = ColdOpenTargetResolver.Resolve(new ColdOpenChannel
        {
            GuildId = 100,
            ChannelId = 200,
            Guild = "old guild label",
            Channel = "old-channel-label",
        }, Channels);

        Assert.Equal(200UL, result.ChannelId);
        Assert.Equal("resolved_id", result.Status);
    }

    [Fact]
    public void Resolve_ChannelIdInDifferentGuildIsRejected()
    {
        var result = ColdOpenTargetResolver.Resolve(new ColdOpenChannel
        {
            GuildId = 101,
            ChannelId = 200,
        }, Channels);

        Assert.Null(result.ChannelId);
        Assert.Equal("guild_id_mismatch", result.Status);
    }

    [Fact]
    public void Resolve_NameFallbackRemainsBackwardCompatibleWhenUnambiguous()
    {
        var result = ColdOpenTargetResolver.Resolve(new ColdOpenChannel
        {
            Guild = "Renamed Guild",
            Channel = "renamed-channel",
        }, Channels);

        Assert.Equal(200UL, result.ChannelId);
        Assert.Equal("resolved_name_fallback", result.Status);
    }

    [Fact]
    public void Resolve_NameFallbackWithoutGuildRejectsAmbiguity()
    {
        var result = ColdOpenTargetResolver.Resolve(new ColdOpenChannel { Channel = "general" }, Channels);

        Assert.Null(result.ChannelId);
        Assert.Equal("name_fallback_ambiguous", result.Status);
    }

    [Theory]
    [InlineData(100UL, null)]
    [InlineData(null, 200UL)]
    [InlineData(0UL, 200UL)]
    public void Resolve_RejectsPartialOrZeroIdPairs(ulong? guildId, ulong? channelId)
    {
        var result = ColdOpenTargetResolver.Resolve(new ColdOpenChannel
        {
            GuildId = guildId,
            ChannelId = channelId,
        }, Channels);

        Assert.Null(result.ChannelId);
        Assert.Equal("invalid_partial_ids", result.Status);
    }
}