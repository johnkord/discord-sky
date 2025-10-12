using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public class OrchestrationSmokeTests
{
    [Fact]
    public void SafetyFilter_RateLimitsBeyondConfiguredCap()
    {
        var settings = new ChaosSettings { MaxPromptsPerHour = 2 };
        var filter = new SafetyFilter(settings, NullLogger<SafetyFilter>.Instance);

        var now = DateTimeOffset.UtcNow;
        Assert.False(filter.ShouldRateLimit(now));
        Assert.False(filter.ShouldRateLimit(now.AddSeconds(1)));
        Assert.True(filter.ShouldRateLimit(now.AddSeconds(2)));
    }

    [Fact]
    public void SafetyFilter_SanitizesMentions()
    {
        var filter = new SafetyFilter(new ChaosSettings(), NullLogger<SafetyFilter>.Instance);

        var sanitized = filter.SanitizeMentions(" @Agent Chaos! ");

        Assert.Equal("AgentChaos", sanitized);
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
