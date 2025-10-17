using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class AmbientReplyTests
{
    private sealed class FixedRandomProvider : IRandomProvider
    {
        private readonly double _value;
        public FixedRandomProvider(double value) => _value = value;
        public double NextDouble() => _value;
    }

    [Fact]
    public void AmbientReplyChance_Default_Is25Percent()
    {
        var chaos = new ChaosSettings();
        Assert.InRange(chaos.AmbientReplyChance, 0.20, 0.30);
    }

    [Fact]
    public void FixedRandomProvider_ReturnsConfiguredValue()
    {
        var provider = new FixedRandomProvider(0.1234);
        Assert.Equal(0.1234, provider.NextDouble(), 4);
    }
}