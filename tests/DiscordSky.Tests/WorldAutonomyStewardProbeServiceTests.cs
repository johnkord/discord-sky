using System.Runtime.CompilerServices;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyStewardProbeServiceTests
{
    [Fact]
    public async Task StartAsync_ValidatesTheSiblingStewardBinaryWithoutAnEnabledGuild()
    {
        var stewardRoot = GetSiblingStewardRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var assemblyPath = Path.Combine(
            stewardRoot,
            "src",
            "DiscordSteward",
            "bin",
            configuration,
            "net10.0",
            "DiscordSteward.dll");
        if (!File.Exists(assemblyPath))
        {
            return;
        }

        var options = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            StewardArguments = [assemblyPath],
            StewardWorkingDirectory = stewardRoot,
            ValidateStewardOnStartup = true
        });
        var service = new WorldAutonomyStewardProbeService(
            options,
            NullLogger<WorldAutonomyStewardProbeService>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await service.StartAsync(timeout.Token);

        Assert.False(options.IsEnabled);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ValidatesTheSiblingStewardBinaryForAnEnabledUnrestrictedGuild()
    {
        var stewardRoot = GetSiblingStewardRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var assemblyPath = Path.Combine(
            stewardRoot,
            "src",
            "DiscordSteward",
            "bin",
            configuration,
            "net10.0",
            "DiscordSteward.dll");
        if (!File.Exists(assemblyPath))
        {
            return;
        }

        var profilePath = Path.Combine(
            stewardRoot,
            "config",
            "profiles",
            "unrestricted-autonomy.example.json");
        var options = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            StewardArguments = [assemblyPath],
            StewardWorkingDirectory = stewardRoot,
            ValidateStewardOnStartup = true,
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                ["100000000000000001"] = new() { ProfilePath = profilePath }
            }
        });
        var service = new WorldAutonomyStewardProbeService(
            options,
            NullLogger<WorldAutonomyStewardProbeService>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await service.StartAsync(timeout.Token);

        Assert.True(options.IsEnabled);
        await service.StopAsync(CancellationToken.None);
    }

    private static string GetSiblingStewardRoot([CallerFilePath] string? sourceFile = null)
    {
        var skyRoot = new DirectoryInfo(Path.GetDirectoryName(sourceFile) ?? string.Empty).Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Could not locate the Discord Sky repository root.");
        return Path.GetFullPath(Path.Combine(skyRoot, "..", "discord-steward"));
    }
}