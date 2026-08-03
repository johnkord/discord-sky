#pragma warning disable MEAI001

using System.Runtime.CompilerServices;
using System.Text.Json;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class StewardMcpSupervisorIntegrationTests
{
    [Fact]
    public async Task SiblingSteward_UnrestrictedProfileExposesOneReusableCompleteNativeCatalog()
    {
        var stewardRoot = GetSiblingStewardRoot();
        var assemblyPath = Path.Combine(stewardRoot, "src", "DiscordSteward", "bin", "Debug", "net10.0", "DiscordSteward.dll");
        if (!File.Exists(assemblyPath))
        {
            return;
        }

        var profilePath = Path.Combine(stewardRoot, "config", "profiles", "unrestricted-autonomy.example.json");
        using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
        var guildId = ulong.Parse(profile.RootElement.GetProperty("Discord").GetProperty("GuildId").GetString()!);
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            StewardArguments = [assemblyPath],
            StewardWorkingDirectory = stewardRoot,
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                [guildId.ToString()] = new() { ProfilePath = profilePath }
            }
        });
        await using var supervisor = new StewardMcpSupervisor(
            configuration,
            NullLoggerFactory.Instance,
            NullLogger<StewardMcpSupervisor>.Instance);

        var first = await supervisor.GetSessionAsync(guildId, CancellationToken.None);
        var second = await supervisor.GetSessionAsync(guildId, CancellationToken.None);
        var context = WorldAutonomyRunContext.Create(
            guildId,
            "integration_test",
            "gpt-5.5",
            first.Catalog.Capabilities.ProfileDigest,
            first.Catalog.Capabilities.ManifestDigest,
            requestIdPoolSize: 1);
        var bound = first.Catalog.Bind(context);

        Assert.Same(first, second);
        Assert.Equal("UnrestrictedAutonomy", first.Catalog.Capabilities.AuthorizationMode);
        Assert.Equal("unrestricted", first.Catalog.Capabilities.Mode);
        Assert.Equal(first.Catalog.ToolNames.Length, bound.Tools.Length);
        Assert.Equal(first.Catalog.ToolNames.ToArray(), bound.Tools.Select(tool => tool.Function.Name).ToArray());
        var search = Assert.IsType<HostedToolSearchTool>(Assert.Single(bound.SupplementaryTools));
        Assert.Equal("tool_search", search.Name);
        Assert.NotNull(search.DeferredTools);
        Assert.Equal(bound.Tools.Select(tool => tool.Function.Name), search.DeferredTools!);
        var health = supervisor.GetHealthSnapshot();
        Assert.True(health.IsHealthy);
        Assert.Equal(1, health.ConfiguredGuilds);
        Assert.Equal(1, health.HealthyGuilds);
        Assert.Equal("healthy", health.Guilds[guildId.ToString()]);

        await first.DisposeAsync();
        var recreated = await supervisor.GetSessionAsync(guildId, CancellationToken.None);

        Assert.NotSame(first, recreated);
        Assert.Equal(first.Catalog.Capabilities.ProfileDigest, recreated.Catalog.Capabilities.ProfileDigest);
        Assert.True(supervisor.GetHealthSnapshot().IsHealthy);
    }

    private static string GetSiblingStewardRoot([CallerFilePath] string? sourceFile = null)
    {
        var skyRoot = new DirectoryInfo(Path.GetDirectoryName(sourceFile) ?? string.Empty).Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Could not locate the Discord Sky repository root.");
        return Path.GetFullPath(Path.Combine(skyRoot, "..", "discord-steward"));
    }
}