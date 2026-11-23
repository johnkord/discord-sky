#pragma warning disable MEAI001

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class StewardMcpSupervisorIntegrationTests
{
    [StewardFact]
    public async Task SiblingSteward_UnrestrictedProfileExposesOneReusableCompleteNativeCatalog()
    {
        var stewardRoot = GetSiblingStewardRoot();
        var assemblyPath = Path.Combine(stewardRoot, "src", "DiscordSteward", "bin", "Debug", "net10.0", "DiscordSteward.dll");
        Assert.True(File.Exists(assemblyPath), "Build the pinned Steward test fixture before release validation.");

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
        var direct = first.Catalog.Bind(context, useHostedToolSearch: false);
        Assert.Empty(direct.SupplementaryTools);
        Assert.Equal(bound.ManifestDigest, direct.ManifestDigest);
        Assert.Equal(bound.Tools.Select(tool => tool.Function.Name), direct.Tools.Select(tool => tool.Function.Name));
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

    [StewardFact]
    public async Task SiblingSteward_AutonomyProviderRejectionReturnsTruthfulFailure()
    {
        var stewardRoot = GetSiblingStewardRoot();
        var assemblyPath = Path.Combine(stewardRoot, "src", "DiscordSteward", "bin", "Debug", "net10.0", "DiscordSteward.dll");
        Assert.True(File.Exists(assemblyPath), "Build the pinned Steward test fixture before release validation.");
        var profilePath = Path.Combine(stewardRoot, "config", "profiles", "unrestricted-autonomy.example.json");
        using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
        var guildId = ulong.Parse(profile.RootElement.GetProperty("Discord").GetProperty("GuildId").GetString()!);
        var directory = Directory.CreateTempSubdirectory("sky-provider-rejection-").FullName;
        try
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var endpoint = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/";
            socket.Stop();
            using var listener = new HttpListener();
            listener.Prefixes.Add(endpoint);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var options = new WorldAutonomyOptions
            {
                LedgerPath = Path.Combine(directory, "autonomy.json"),
                StewardCommand = "dotnet",
                StewardArguments = [assemblyPath],
                StewardWorkingDirectory = stewardRoot,
                PromptCacheMode = WorldAutonomyPromptCacheMode.Hybrid,
                PromptCacheCanaryPercent = 100,
                EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
                {
                    [guildId.ToString()] = new() { ProfilePath = profilePath }
                }
            };
            var configuration = WorldAutonomyConfiguration.FromOptions(options);
            await using var supervisor = new StewardMcpSupervisor(configuration,
                NullLoggerFactory.Instance, NullLogger<StewardMcpSupervisor>.Instance);
            using var ledger = new FileBackedWorldAutonomyLedger(options);
            var llm = new TestOptionsMonitor<LlmOptions>(new LlmOptions
            {
                Providers = new Dictionary<string, LlmProviderOptions>
                {
                    ["OpenAI"] = new()
                    {
                        ApiKey = "not-a-real-key", Endpoint = endpoint,
                        ChatModel = "gpt-6.2-astra", ReasoningEffort = "ExtraHigh", UseResponsesApi = true
                    }
                }
            });
            var guard = new LlmProviderGuard(NullLogger<LlmProviderGuard>.Instance,
                options: new LlmProviderGuardOptions { Enabled = false, StatePath = Path.Combine(directory, "guard.json") });
            using var cdn = new VisionTestCdn();
            var orchestrator = new WorldAutonomyOrchestrator(configuration, llm, supervisor,
                new WorldAutonomyAgentFactory(), ledger, NullLogger<WorldAutonomyOrchestrator>.Instance,
                providerGuard: guard, vision: cdn.Reader());
            var opportunity = new WorldAutonomyOpportunity(guildId, "message", "What does this say?",
                SourceMessageId: "5001", IsDirectAddress: true, SourceChannelId: 6001,
                ImageSelection: new([cdn.Attachment with { MessageId = 5001 }]));

            var run = orchestrator.RunAsync(opportunity, deadline.Token);
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(deadline.Token);
            request.Response.StatusCode = 400;
            request.Response.ContentType = "application/json";
            var error = Encoding.UTF8.GetBytes("""{"error":{"message":"Synthetic provider rejection","type":"invalid_request_error"}}""");
            await request.Response.OutputStream.WriteAsync(error, deadline.Token);
            request.Response.Close();
            var result = await run;

            using var document = JsonDocument.Parse(body);
            Assert.Equal(64, document.RootElement.GetProperty("prompt_cache_key").GetString()!.Length);
            var user = Assert.Single(document.RootElement.GetProperty("input").EnumerateArray(),
                item => item.TryGetProperty("role", out var role) && role.GetString() == "user");
            var pixels = Assert.Single(user.GetProperty("content").EnumerateArray(),
                item => item.GetProperty("type").GetString() == "input_image");
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(cdn.Png), pixels.GetProperty("image_url").GetString());
            Assert.DoesNotContain("sensitive-signature", body);
            Assert.Contains("set_member_nickname", body, StringComparison.Ordinal);
            Assert.Equal(WorldAutonomyRunStatuses.Failed, result.Status);
            Assert.Equal("ClientResultException", result.FailureReason);
            Assert.False(result.SpokeInChannel);
            Assert.Contains("failed during this audience", result.FinalText, StringComparison.Ordinal);
            Assert.Contains("No server changes were attempted.", result.FinalText, StringComparison.Ordinal);
            Assert.Empty(await ledger.ListToolCallsAsync(result.RunId!, deadline.Token));
            var recorded = await ledger.GetRunAsync(result.RunId!, deadline.Token);
            Assert.NotNull(recorded);
            Assert.Equal(64, recorded!.ManifestDigest.Length);
            Assert.Equal(result.FinalText, recorded.FinalText);
            Assert.Equal(result.FailureReason, recorded.FailureReason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string GetSiblingStewardRoot([CallerFilePath] string? sourceFile = null)
    {
        if (Environment.GetEnvironmentVariable("STEWARD_TEST_ROOT") is { Length: > 0 } root) return Path.GetFullPath(root);
        var skyRoot = new DirectoryInfo(Path.GetDirectoryName(sourceFile) ?? string.Empty).Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Could not locate the Discord Sky repository root.");
        return Path.GetFullPath(Path.Combine(skyRoot, "..", "discord-steward"));
    }

    private sealed class StewardFactAttribute : FactAttribute
    {
        public StewardFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SKY_REQUIRE_STEWARD") != "1" &&
                !File.Exists(Path.Combine(GetSiblingStewardRoot(), "src/DiscordSteward/bin/Debug/net10.0/DiscordSteward.dll")))
                Skip = "Pinned sibling Steward fixture is not built (required by scripts/validate-release.sh).";
        }
    }
}