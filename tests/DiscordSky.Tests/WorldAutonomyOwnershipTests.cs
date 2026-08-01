using DiscordSky.Bot.Bot;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyOwnershipTests
{
    [Theory]
    [InlineData(false, "", false)]
    [InlineData(true, "rename these channels", false)]
    [InlineData(true, "declare me Minister of Bad Decisions", false)]
    public void WorldAutonomy_OwnsAmbientAndRobotnikCommands(
        bool hasPrefix,
        string payload,
        bool isLocallyHandledImage)
    {
        Assert.True(DiscordBotService.ShouldWorldAutonomyOwnMessage(
            hasPrefix,
            payload,
            isLocallyHandledImage));
    }

    [Theory]
    [InlineData("forget-me")]
    [InlineData("what-do-you-know")]
    [InlineData("forget cats")]
    [InlineData("(image) build me a fortress")]
    [InlineData("(Weird Al) sing about lint")]
    [InlineData("scam-report")]
    [InlineData("empire")]
    [InlineData("empire-tick")]
    public void WorldAutonomy_LeavesBotManagementAndPersonaOverridesLocal(string payload)
    {
        Assert.False(DiscordBotService.ShouldWorldAutonomyOwnMessage(
            hasPrefix: true,
            payload,
            isLocallyHandledImage: false));
    }

    [Fact]
    public void WorldAutonomy_LeavesNaturalLanguageImageRequestsLocal()
    {
        Assert.False(DiscordBotService.ShouldWorldAutonomyOwnMessage(
            hasPrefix: false,
            payload: string.Empty,
            isLocallyHandledImage: true));
    }
}