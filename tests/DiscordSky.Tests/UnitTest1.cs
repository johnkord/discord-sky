using System;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models;
using DiscordSky.Bot.Services;

namespace DiscordSky.Tests;

public class ServiceSmokeTests
{
    private readonly ChaosSettings _settings = new();

    [Fact]
    public void BitStarter_ProducesScriptWithParticipants()
    {
        var service = new BitStarterService(new Random(42));
        var response = service.Generate(new BitStarterRequest("The Great Meme", ["alice", "bob"]), _settings);

        Assert.NotEmpty(response.ScriptLines);
        Assert.StartsWith("@", response.MentionTags[0]);
    }

    [Fact]
    public void QuestService_AddsBonusStepWhenSpicy()
    {
        var spicySettings = new ChaosSettings { AnnoyanceLevel = 0.9 };
        var service = new MischiefQuestService(new Random(13));

        var quest = service.DrawQuest(spicySettings);

        Assert.Contains(quest.Steps, step => step.Contains("Bonus chaos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BotOptions_AllowlistHonorsConfiguredNames()
    {
        var options = new BotOptions
        {
            AllowedChannelNames = ["chaos-lab", "quest-board"]
        };

        Assert.True(options.IsChannelAllowed("chaos-lab"));
        Assert.False(options.IsChannelAllowed("general"));
        Assert.False(options.IsChannelAllowed(null));

        options.AllowedChannelNames.Clear();
        Assert.True(options.IsChannelAllowed("anything"));
    }
}
