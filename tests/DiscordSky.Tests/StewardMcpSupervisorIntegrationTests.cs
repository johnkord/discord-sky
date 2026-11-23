#pragma warning disable MEAI001

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
            using var listener = StartLocalProvider(out var endpoint);
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

    [StewardFact]
    public async Task SiblingSteward_CourtFilingChangesOnlyTheInvitedUserTurn()
    {
        var steward = GetSiblingStewardRoot();
        var profile = Path.Combine(steward, "config/profiles/unrestricted-autonomy.example.json");
        using var profileJson = JsonDocument.Parse(await File.ReadAllTextAsync(profile));
        var guild = profileJson.RootElement.GetProperty("Discord").GetProperty("GuildId").GetString()!;
        var guildId = ulong.Parse(guild);
        var directory = Directory.CreateTempSubdirectory("sky-court-");
        try
        {
            WorldAutonomyOptions Options(bool enabled) => new()
            {
                DecreeAppealsEnabled = enabled, LedgerPath = Path.Combine(directory.FullName, "ledger.json"),
                StewardCommand = "env", StewardWorkingDirectory = directory.FullName,
                StewardArguments = ["-i", $"PATH={Environment.GetEnvironmentVariable("PATH")}", $"HOME={directory.FullName}", $"Steward__ProfilePath={profile}",
                    "dotnet", Path.Combine(steward, "src/DiscordSteward/bin/Debug/net10.0/DiscordSteward.dll")],
                EnabledGuilds = new() { [guild] = new() { ProfilePath = profile } }
            };
            using var listener = StartLocalProvider(out var endpoint);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var opts = Options(true);
            await using var supervisor = new StewardMcpSupervisor(WorldAutonomyConfiguration.FromOptions(opts), NullLoggerFactory.Instance, NullLogger<StewardMcpSupervisor>.Instance);
            using var ledger = new FileBackedWorldAutonomyLedger(opts);
            var prior = WorldAutonomyRunContext.Create(guildId, "fixture", "fixture", "profile", "manifest", 1, sourceChannelId: 6001);
            var before = DateTimeOffset.UtcNow.AddMinutes(-2);
            await ledger.StartRunAsync(prior.ToRunStart(before), deadline.Token);
            var requestId = prior.RequestIdPool.Single();
            await ledger.RecordDispatchPendingAsync(new("prior-call", prior.RunId, 1, "set_member_nickname", requestId, "{\"private\":\"omitted-args\"}", "digest", "schema", before), deadline.Token);
            await ledger.CompleteToolCallAsync("prior-call", "succeeded", WorldAutonomyLedgerTests.NicknameReceipt(requestId, guild, "111", "Copper"), null, before, deadline.Token);
            await new WorldAutonomyRunState(prior, ledger, []).RecordDiscordDeliveryAsync(6001, [4900], 4800, 30);
            await ledger.CompleteRunAsync(prior.RunId, "succeeded", "omitted-prior-final", null, DateTimeOffset.UtcNow, deadline.Token);
            var llm = new TestOptionsMonitor<LlmOptions>(new LlmOptions { Providers = new() { ["OpenAI"] = new()
            { ApiKey = "test-only", Endpoint = endpoint, ChatModel = "gpt-6.2-astra", ReasoningEffort = "ExtraHigh", UseResponsesApi = true } } });
            var guard = new LlmProviderGuard(NullLogger<LlmProviderGuard>.Instance, options: new LlmProviderGuardOptions
            { Enabled = false, StatePath = Path.Combine(directory.FullName, "guard.json") });
            var cases = new (bool Enabled, bool Direct, string Filing, string? Outcome)[]
            {
                (true, true, "Objection! I want Thursday back", "quote_only"),
                (true, true, "appeal on the record: <@111> objects", "verified_effect"),
                (true, true, "appeal on the record: <@222> objects", "no_matching_receipt"),
                (true, false, "appeal: from the gallery", null),
                (false, true, "appeal: the court is closed", null)
            };
            foreach (var item in cases)
            {
                var telemetry = new InMemoryTelemetrySink();
                var errors = new FixtureErrorLog();
                var runner = new WorldAutonomyOrchestrator(WorldAutonomyConfiguration.FromOptions(Options(item.Enabled)), llm,
                    supervisor, new WorldAutonomyAgentFactory(), ledger, errors, telemetry: telemetry, providerGuard: guard);
                var exhibit = DecreeAppeal.TryOpen(item.Filing, Discord.MessageType.Reply, new Discord.MessageReference(4900, 6001), guildId, 6001, 42,
                    new(4900, 6001, 42, "<@111>, your nickname is Copper. https://example.invalid/not-for-the-record"));
                var run = runner.RunAsync(new WorldAutonomyOpportunity(guildId, "discord_message", item.Filing, "5001", IsDirectAddress: item.Direct,
                    SourceChannelId: 6001, SourceAuthorId: 8001) { Appeal = exhibit }, deadline.Token);
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(deadline.Token);
                request.Response.ContentType = "application/json";
                var response = JsonSerializer.SerializeToUtf8Bytes(new { id = "resp_fixture", @object = "response", created_at = 1, status = "completed", model = "gpt-6.2-astra",
                    output = new[] { new { type = "message", id = "msg_fixture", status = "completed", role = "assistant",
                        content = new[] { new { type = "output_text", text = "The calendar may counteroffer.", annotations = Array.Empty<object>() } } } }, parallel_tool_calls = false,
                    tool_choice = "auto", tools = Array.Empty<object>(), usage = new { input_tokens = 40, input_tokens_details = new { cached_tokens = 0 },
                        output_tokens = 6, output_tokens_details = new { reasoning_tokens = 0 }, total_tokens = 46 } });
                request.Response.ContentLength64 = response.Length;
                await request.Response.OutputStream.WriteAsync(response, deadline.Token);
                request.Response.Close();
                var result = await run;
                Assert.True(result.Status == "succeeded", $"{item.Filing}: {result.FailureReason} {errors.Error}");
                Assert.Equal("The calendar may counteroffer.", result.FinalText);
                Assert.Empty(await ledger.ListToolCallsAsync(result.RunId!, deadline.Token));
                using var document = JsonDocument.Parse(body);
                var input = document.RootElement.GetProperty("input").EnumerateArray().ToArray();
                var user = Assert.Single(input, entry => entry.TryGetProperty("role", out var role) && role.GetString() == "user");
                var content = user.GetProperty("content");
                var userText = content.ValueKind == JsonValueKind.String ? content.GetString()! : content.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text").GetProperty("text").GetString()!;
                var events = telemetry.Events.Where(entry => entry.EventType == TelemetryEventTypes.WorldAutonomyAppeal);
                if (item.Outcome is null)
                {
                    Assert.DoesNotContain("Court of Appeals", userText);
                    Assert.Empty(events);
                }
                else
                {
                    var metric = Assert.Single(events);
                    Assert.Equal(item.Outcome, metric.Outcome);
                    Assert.Equal(4900UL, metric.ReferentMessageId);
                    Assert.Equal(result.RunId, metric.OperationId);
                    using var shown = JsonDocument.Parse(userText.Split("\nExhibit: ", StringSplitOptions.None).Last());
                    Assert.Contains("your nickname is Copper", shown.RootElement.GetProperty("quotation").GetString());
                    var effect = shown.RootElement.GetProperty("historicalEffect");
                    if (item.Outcome == "verified_effect") Assert.Equal("111", effect.GetProperty("Subject").GetString());
                    else Assert.Equal(JsonValueKind.Null, effect.ValueKind);
                }
                Assert.Contains("set_member_nickname", body);
                Assert.DoesNotContain("omitted-args", body);
                Assert.DoesNotContain("omitted-prior-final", body);
                Assert.DoesNotContain("not-for-the-record", body);
                Assert.DoesNotContain(requestId, body);
                Assert.All(input.Where(entry => !entry.Equals(user)), entry => Assert.DoesNotContain("Court of Appeals", entry.GetRawText()));
                if (document.RootElement.TryGetProperty("instructions", out var instructions)) Assert.DoesNotContain("Court of Appeals", instructions.GetRawText());
                Assert.Single(telemetry.Events, entry => entry.EventType == TelemetryEventTypes.LlmCall);
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static HttpListener StartLocalProvider(out string endpoint)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        endpoint = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/";
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add(endpoint);
        listener.Start();
        return listener;
    }

    private sealed class FixtureErrorLog : ILogger<WorldAutonomyOrchestrator>
    {
        internal Exception? Error { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Error) Error = error;
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