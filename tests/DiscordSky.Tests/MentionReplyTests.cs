using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public class MentionReplyTests
{
    private const ulong BotId = 883566518678487120UL;

    [Fact]
    public void Replies_when_bot_is_directly_mentioned()
    {
        Assert.True(DiscordBotService.ShouldReplyToDirectMention(
            enabled: true, botUserId: BotId, mentionedUserIds: new[] { 1UL, BotId, 2UL }));
    }

    [Fact]
    public void Does_not_reply_when_bot_not_mentioned()
    {
        Assert.False(DiscordBotService.ShouldReplyToDirectMention(
            enabled: true, botUserId: BotId, mentionedUserIds: new[] { 1UL, 2UL }));
    }

    [Fact]
    public void Does_not_reply_when_disabled()
    {
        Assert.False(DiscordBotService.ShouldReplyToDirectMention(
            enabled: false, botUserId: BotId, mentionedUserIds: new[] { BotId }));
    }

    [Fact]
    public void Does_not_reply_when_bot_id_unknown()
    {
        Assert.False(DiscordBotService.ShouldReplyToDirectMention(
            enabled: true, botUserId: null, mentionedUserIds: new[] { BotId }));
    }

    [Fact]
    public void Does_not_reply_when_no_mentions()
    {
        Assert.False(DiscordBotService.ShouldReplyToDirectMention(
            enabled: true, botUserId: BotId, mentionedUserIds: Array.Empty<ulong>()));
    }

    [Fact]
    public void Mention_uses_the_fuller_non_ambient_length_treatment()
    {
        // A @mention is an explicit address, so it should get the same length treatment as a command,
        // not the shortest ambient bias. FixedRng(0.35) lands short for ambient (cut 0.45) but medium for
        // non-ambient (cut 0.25), so the directives must differ from ambient and match command.
        var rng = new FixedRng(0.35);
        var mention = RobotnikPersona.RollTurnFlavor(rng, CreativeInvocationKind.Mention);
        var command = RobotnikPersona.RollTurnFlavor(rng, CreativeInvocationKind.Command);
        var ambient = RobotnikPersona.RollTurnFlavor(rng, CreativeInvocationKind.Ambient);

        Assert.Equal(command.LengthDirective, mention.LengthDirective);
        Assert.NotEqual(ambient.LengthDirective, mention.LengthDirective);
    }

    private sealed class FixedRng : IRandomProvider
    {
        private readonly double _value;
        public FixedRng(double value) => _value = value;
        public double NextDouble() => _value;
    }
}
