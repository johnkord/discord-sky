using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyConfigurationTests
{
    [Fact]
    public void EmptyConfiguration_DisablesAutonomyWithoutRequiringAChildCommand()
    {
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = string.Empty,
            StewardArguments = []
        });

        Assert.False(configuration.IsEnabled);
        Assert.Empty(configuration.EnabledGuilds);
        Assert.Equal(TimeSpan.FromMinutes(20), configuration.SessionTimeout);
        Assert.Equal(40, configuration.RequestIdPoolSize);
    }

    [Fact]
    public void Configuration_BindsOnlyExactGuildIds()
    {
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            StewardArguments = ["/app/steward/DiscordSteward.dll"],
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                ["667956000757776386"] = new()
                {
                    ProfilePath = "/app/steward/profiles/funhouse.json",
                    Model = "gpt-5.5"
                }
            }
        });

        Assert.True(configuration.IsEnabled);
        Assert.True(configuration.TryGetBinding(667956000757776386, out var binding));
        Assert.Equal("/app/steward/profiles/funhouse.json", binding.ProfilePath);
        Assert.Equal("gpt-5.5", binding.Model);
        Assert.False(configuration.TryGetBinding(667956000757776387, out _));
    }

    [Theory]
    [InlineData("not-a-guild")]
    [InlineData("0")]
    public void Configuration_RejectsInvalidGuildBindings(string guildId)
    {
        var options = new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            StewardArguments = ["/app/steward/DiscordSteward.dll"],
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                [guildId] = new() { ProfilePath = "/app/steward/profiles/test.json" }
            }
        };

        Assert.Throws<InvalidOperationException>(() => WorldAutonomyConfiguration.FromOptions(options));
    }

    [Fact]
    public void Configuration_AcceptsSelfContainedStewardWithoutProcessArguments()
    {
        var options = new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                ["667956000757776386"] = new() { ProfilePath = "profile.json" }
            }
        };

        var configuration = WorldAutonomyConfiguration.FromOptions(options);

        Assert.True(configuration.TryGetBinding(667956000757776386, out _));
        Assert.Empty(configuration.StewardArguments);
    }

    [Fact]
    public void Configuration_PreservesExplicitStartupProbeSetting()
    {
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "/app/steward/DiscordSteward",
            ValidateStewardOnStartup = true
        });

        Assert.True(configuration.ValidateStewardOnStartup);
    }
}