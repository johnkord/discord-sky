#pragma warning disable MEAI001

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [StewardFact]
    public async Task SiblingSteward_AbandonedCallsReconcileWithoutReplayingDiscordWrites()
    {
        var stewardRoot = GetSiblingStewardRoot();
        var assembly = Path.Combine(stewardRoot, "src/DiscordSteward/bin/Debug/net10.0/DiscordSteward.dll");
        var directory = Directory.CreateTempSubdirectory("sky-steward-recovery-");
        try
        {
            var profile = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(stewardRoot, "config/profiles/unrestricted-autonomy.example.json")))!;
            var guild = profile["Discord"]!["GuildId"]!.GetValue<string>();
            var childJournal = Path.Combine(directory.FullName, "child.json");
            var profilePath = Path.Combine(directory.FullName, "profile.json");
            profile["Steward"]!["JournalBackend"] = "File";
            profile["Steward"]!["DataPath"] = childJournal;
            await File.WriteAllTextAsync(profilePath, profile.ToJsonString());
            var options = new WorldAutonomyOptions
            {
                LedgerPath = Path.Combine(directory.FullName, "sky.json"),
                StewardCommand = "env",
                StewardArguments = ["-i", $"PATH={Environment.GetEnvironmentVariable("PATH")}", $"HOME={directory.FullName}", $"Steward__ProfilePath={profilePath}", "dotnet", assembly],
                StewardWorkingDirectory = directory.FullName,
                EnabledGuilds = new() { [guild] = new() { ProfilePath = profilePath } }
            };
            var now = DateTimeOffset.UtcNow;
            var boundary = now.AddMinutes(-1);
            var old = now.AddMinutes(-5);
            var cases = new[]
            {
                new RecoveryCase("reservation", "reserved", old, old, null, "failed", "state_conflict"),
                new RecoveryCase("lost_ack", "executing", old, old, null, "unknown", "outcome_unknown"),
                new RecoveryCase("lost_role_id", "executing", old, old, null, "unknown", "outcome_unknown", "create_role"),
                new RecoveryCase("lost_channel_id", "executing", old, old, null, "unknown", "outcome_unknown", "create_text_channel"),
                new RecoveryCase("lost_message_id", "executing", old, old, null, "unknown", "outcome_unknown", "send_message"),
                new RecoveryCase("found_ack", "unknown", old, old, "{\"accepted\":true}", "succeeded", null),
                new RecoveryCase("terminal", "failed", old, old, null, "failed", "discord_permission_denied"),
                new RecoveryCase("young_claim", "executing", old, now, null, "dispatch_pending", null),
                new RecoveryCase("active_owner", "reserved", now, old, null, "dispatch_pending", null),
                new RecoveryCase("missing", null, old, old, null, "unknown", WorldAutonomyRecoveryService.MissingOperationReason)
            };
            var operations = new Dictionary<string, object>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using (var firstProcess = new FileBackedWorldAutonomyLedger(options))
            {
                foreach (var item in cases)
                {
                    var id = item.RequestId;
                    var tool = item.Tool;
                    await firstProcess.StartRunAsync(new(item.Run, ulong.Parse(guild), "fixture", null, null, "fixture", "profile", "manifest", item.RunStart), deadline.Token);
                    var args = WorldAutonomyCanonicalizer.SerializeArguments(new Dictionary<string, object?> { ["request_id"] = id, ["reason"] = "fixture", ["channel_id"] = "6001" });
                    await firstProcess.RecordDispatchPendingAsync(new(item.Run, item.Run, 1, tool, id, args, WorldAutonomyCanonicalizer.ComputeDigest(args), "schema", item.RunStart), deadline.Token);
                    if (item.Initial is null) continue;
                    var invocation = JsonSerializer.Serialize(new { schemaVersion = 1, kind = tool, requestId = id, resourceId = "6001", reason = "fixture", arguments = new { channelId = "6001" } });
                    operations[id] = new { schemaVersion = 1, requestId = id, kind = tool, guildId = guild, resourceId = "6001", reason = "fixture", risk = "R3", policyDigest = "profile", manifestDigest = "manifest",
                        invocationDigest = WorldAutonomyCanonicalizer.ComputeDigest(invocation), invocationJson = invocation, createdAt = item.Since, status = item.Initial,
                        executionStartedAt = item.Initial == "reserved" ? (DateTimeOffset?)null : item.Since, completedAt = item.Initial is "unknown" or "failed" ? item.Since : (DateTimeOffset?)null,
                        outcome = item.Initial switch { "unknown" => "write_outcome_unresolved", "failed" => "write_rejected", _ => null },
                        errorCode = item.Initial switch { "unknown" => "outcome_unknown", "failed" => "discord_permission_denied", _ => null },
                        beforeStateJson = "{}", desiredStateJson = "{\"accepted\":true}", observedStateJson = item.Receipt };
                }
            }
            await File.WriteAllTextAsync(childJournal, JsonSerializer.Serialize(new { version = 1, operations }));
            var config = WorldAutonomyConfiguration.FromOptions(options);
            await using var supervisor = new StewardMcpSupervisor(config, NullLoggerFactory.Instance, NullLogger<StewardMcpSupervisor>.Instance);
            using var ledger = new FileBackedWorldAutonomyLedger(options);
            using var recovery = new WorldAutonomyRecoveryService(config, ledger, supervisor, NullLogger<WorldAutonomyRecoveryService>.Instance);
            await recovery.RunRecoveryCycleAsync(boundary, deadline.Token);
            var skyAfter = await File.ReadAllTextAsync(options.LedgerPath);
            var childAfter = await File.ReadAllTextAsync(childJournal);
            using var snapshot = JsonDocument.Parse(childAfter);
            var records = snapshot.RootElement.GetProperty("operations");
            Assert.Equal(operations.Count, records.EnumerateObject().Count());
            foreach (var item in cases)
            {
                var call = Assert.Single(await ledger.ListToolCallsAsync(item.Run, deadline.Token));
                Assert.True(call.DispatchStatus == item.Expected, $"{item.Run}: expected {item.Expected}, got {call.DispatchStatus}");
                Assert.Equal(item.Error, call.ErrorMessage);
                var parent = await ledger.GetRunAsync(item.Run, deadline.Token);
                Assert.Equal(item.Expected == "dispatch_pending" ? "running" : "failed", parent?.Status);
                if (item.Initial is null) { Assert.False(records.TryGetProperty(item.RequestId, out _)); continue; }
                var actual = records.GetProperty(item.RequestId).GetProperty("status").GetString();
                Assert.Equal(item.Expected == "dispatch_pending" ? item.Initial : item.Expected, actual);
            }
            await recovery.RunRecoveryCycleAsync(boundary, deadline.Token);
            Assert.Equal(skyAfter, await File.ReadAllTextAsync(options.LedgerPath));
            Assert.Equal(childAfter, await File.ReadAllTextAsync(childJournal));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed record RecoveryCase(string Run, string? Initial, DateTimeOffset RunStart, DateTimeOffset Since, string? Receipt, string Expected, string? Error, string Tool = "trigger_typing_indicator")
    {
        public string RequestId { get; } = Guid.NewGuid().ToString("D");
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