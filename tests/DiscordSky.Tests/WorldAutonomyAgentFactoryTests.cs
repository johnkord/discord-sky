using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using System.Text.Json;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyAgentFactoryTests
{
    [Fact]
    public async Task Agent_TerminalDeliveryUsesOneProviderCallWhenEnabled()
    {
        var context = new WorldAutonomyRunContext(
            "run-1", 667956000757776386, "message", null, null, null,
            "gpt-5.6-sol", "profile-digest", "manifest-digest", []);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var provider = new ScriptedChatClient(
            requestId: null,
            toolName: WorldAutonomySpeechTool.TerminalToolName,
            includeValue: false);
        var delivered = 0;
        var finish = AIFunctionFactory.Create(
            () =>
            {
                delivered++;
                return "delivered";
            },
            name: WorldAutonomySpeechTool.TerminalToolName,
            description: "Deliver final speech.");
        var agent = new WorldAutonomyAgentFactory().Create(
            provider,
            new WorldAutonomyRunState(context, ledger, []),
            [],
            supplementaryTools: [finish],
            terminalDeliveryEnabled: true);

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("finish", session);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task Agent_FinalSpeechWithSiblingDoesNotDeliver()
    {
        var context = new WorldAutonomyRunContext(
            "run-1", 667956000757776386, "message", null, null, null,
            "gpt-5.6-sol", "profile-digest", "manifest-digest", []);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var delivered = 0;
        var siblingInvoked = 0;
        var finish = AIFunctionFactory.Create(
            () =>
            {
                delivered++;
                return "delivered";
            },
            name: WorldAutonomySpeechTool.TerminalToolName,
            description: "Deliver final speech.");
        var sibling = AIFunctionFactory.Create(
            () =>
            {
                siblingInvoked++;
                return "read";
            },
            name: "inspect_state",
            description: "Inspect state.");
        var agent = new WorldAutonomyAgentFactory().Create(
            new SiblingThenDoneChatClient(),
            new WorldAutonomyRunState(context, ledger, []),
            [],
            supplementaryTools: [finish, sibling],
            terminalDeliveryEnabled: true);

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("finish and inspect", session);

        Assert.Equal(0, delivered);
        Assert.Equal(1, siblingInvoked);
    }

    [Fact]
    public async Task Agent_RefusedVisualContinuesToFallbackTurn()
    {
        var context = new WorldAutonomyRunContext(
            "run-1", 667956000757776386, "message", null, null, null,
            "gpt-5.6-sol", "profile-digest", "manifest-digest", []);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var provider = new ScriptedChatClient(
            requestId: null,
            toolName: WorldAutonomyVisualTool.ToolName,
            includeValue: false);
        var run = new WorldAutonomyRunState(context, ledger, []);
        var visual = AIFunctionFactory.Create(
            () => new WorldAutonomyVisualResult("refused", "generated_bitmap", null, [], null, "refused"),
            name: WorldAutonomyVisualTool.ToolName,
            description: "Create a visual.");
        var agent = new WorldAutonomyAgentFactory().Create(
            provider,
            run,
            [],
            supplementaryTools: [visual],
            terminalDeliveryEnabled: true);

        var session = await agent.CreateSessionAsync();
        var response = await agent.RunAsync("draw", session);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal("done", response.Text);
        Assert.True(await run.TryBeginTerminalizationAsync(CancellationToken.None));
        await run.CancelTerminalizationAsync();
    }

    [Fact]
    public async Task Agent_DurablyRecordsBeforeInvokingAnApprovalRequiredWrite()
    {
        var requestId = "01900000-0000-7000-8000-000000000001";
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            "100",
            "episode-1",
            "trace-1",
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            [requestId]);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var invoked = 0;
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string request_id, string value) =>
                {
                    invoked++;
                    Assert.Equal(requestId, request_id);
                    var pending = Assert.Single(ledger.PendingDispatches);
                    Assert.Equal(WorldAutonomyDispatchStatuses.Pending, pending.DispatchStatus);
                    return $"wrote:{value}";
                },
                name: "write_value",
                description: "Write a value."),
            IsWrite: true,
            RequiresRequestId: true,
            SchemaDigest: "write-schema");
        var run = new WorldAutonomyRunState(
            context,
            ledger,
            [descriptor]);
        var agent = new WorldAutonomyAgentFactory().Create(
            new ScriptedChatClient(requestId),
            run,
            [descriptor]);

        var session = await agent.CreateSessionAsync();
        var response = await agent.RunAsync("go", session);

        Assert.Equal("done", response.Text);
        Assert.Equal(1, invoked);
        var dispatch = Assert.Single(ledger.PendingDispatches);
        Assert.Equal("write_value", dispatch.ToolName);
        Assert.Equal(requestId, dispatch.RequestId);
        Assert.Equal(WorldAutonomyDispatchStatuses.Accepted, dispatch.DispatchStatus);
        Assert.Equal(1, run.ActivitySnapshot.NativeWriteCount);
        Assert.Equal(1, run.ActivitySnapshot.AcceptedWriteCount);
        Assert.False(run.HasUnsettledWrites);
        Assert.True(await run.TryBeginTerminalizationAsync(CancellationToken.None));
        await run.CancelTerminalizationAsync();
    }

    [Fact]
    public async Task Agent_RejectsARequestIdOutsideTheRunPoolBeforeWriteInvocation()
    {
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            ["01900000-0000-7000-8000-000000000001"]);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var invoked = 0;
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string request_id) =>
                {
                    invoked++;
                    return request_id;
                },
                name: "write_value",
                description: "Write a value."),
            IsWrite: true,
            RequiresRequestId: true,
            SchemaDigest: "write-schema");
        var agent = new WorldAutonomyAgentFactory().Create(
            new ScriptedChatClient("01900000-0000-7000-8000-000000000999"),
            new WorldAutonomyRunState(context, ledger, [descriptor]),
            [descriptor]);

        var session = await agent.CreateSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("go", session));

        Assert.Equal(0, invoked);
        Assert.Empty(ledger.PendingDispatches);
    }

    [Fact]
    public async Task Agent_RecordsAnMcpToolErrorAsFailedForANonJournaledSideEffect()
    {
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            []);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string value) => JsonDocument.Parse("{\"isError\":true}").RootElement.Clone(),
                name: "register_asset",
                description: "Register an asset."),
            IsWrite: true,
            RequiresRequestId: false,
            SchemaDigest: "asset-schema");
        var agent = new WorldAutonomyAgentFactory().Create(
            new ScriptedChatClient(requestId: null, toolName: "register_asset", includeValue: true),
            new WorldAutonomyRunState(context, ledger, [descriptor]),
            [descriptor]);

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("go", session);

        var dispatch = Assert.Single(ledger.PendingDispatches);
        Assert.Equal(WorldAutonomyDispatchStatuses.Failed, dispatch.DispatchStatus);
        Assert.Equal("mcp_tool_error", dispatch.ErrorMessage);
    }

    [Fact]
    public async Task Agent_BlocksIdenticalDeterministicRetryUnderFreshRequestId()
    {
        var requestIds = new[]
        {
            "01900000-0000-7000-8000-000000000001",
            "01900000-0000-7000-8000-000000000002",
        };
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            requestIds.ToImmutableHashSet(StringComparer.Ordinal));
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var invocationCount = 0;
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string request_id, string value) =>
                {
                    invocationCount++;
                    return JsonDocument.Parse(
                        "{\"outcome\":\"failed\",\"error\":{\"code\":\"invalid_argument\"}}")
                        .RootElement.Clone();
                },
                name: "write_value",
                description: "Write a value."),
            IsWrite: true,
            RequiresRequestId: true,
            SchemaDigest: "write-schema");
        var agent = new WorldAutonomyAgentFactory().Create(
            new DeterministicRetryChatClient(requestIds),
            new WorldAutonomyRunState(context, ledger, [descriptor]),
            [descriptor]);

        var session = await agent.CreateSessionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("go", session));

        Assert.Contains("change the arguments or stop", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, invocationCount);
        Assert.Single(ledger.PendingDispatches);
    }

    [Fact]
    public async Task Agent_AllowsChangedRepairAfterDeterministicFailure()
    {
        var requestIds = new[]
        {
            "01900000-0000-7000-8000-000000000001",
            "01900000-0000-7000-8000-000000000002",
        };
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            requestIds.ToImmutableHashSet(StringComparer.Ordinal));
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var invocationCount = 0;
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string request_id, string value) =>
                {
                    invocationCount++;
                    var json = value == "corrected"
                        ? "{\"outcome\":\"succeeded\"}"
                        : "{\"outcome\":\"failed\",\"error\":{\"code\":\"invalid_argument\"}}";
                    return JsonDocument.Parse(json).RootElement.Clone();
                },
                name: "write_value",
                description: "Write a value."),
            IsWrite: true,
            RequiresRequestId: true,
            SchemaDigest: "write-schema");
        var agent = new WorldAutonomyAgentFactory().Create(
            new CorrectedRetryChatClient(requestIds),
            new WorldAutonomyRunState(context, ledger, [descriptor]),
            [descriptor]);

        var response = await agent.RunAsync("go", await agent.CreateSessionAsync());

        Assert.Equal("done", response.Text);
        Assert.Equal(2, invocationCount);
        Assert.Equal(2, ledger.PendingDispatches.Count);
        Assert.Equal(WorldAutonomyDispatchStatuses.Failed, ledger.PendingDispatches[0].DispatchStatus);
        Assert.Equal(WorldAutonomyDispatchStatuses.Succeeded, ledger.PendingDispatches[1].DispatchStatus);
    }

    [Theory]
    [InlineData("succeeded", WorldAutonomyDispatchStatuses.Succeeded, null)]
    [InlineData("failed", WorldAutonomyDispatchStatuses.Failed, "state_conflict")]
    [InlineData("partial_failure", WorldAutonomyDispatchStatuses.PartialFailure, "discord_partial_failure")]
    [InlineData("unknown", WorldAutonomyDispatchStatuses.Unknown, "outcome_unknown")]
    public async Task Agent_RecordsTerminalStewardOutcomeImmediately(
        string outcome,
        string expectedStatus,
        string? errorCode)
    {
        const string requestId = "01900000-0000-7000-8000-000000000001";
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            [requestId]);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var error = errorCode is null
            ? string.Empty
            : $",\"error\":{{\"code\":\"{errorCode}\"}}";
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string request_id) => new TextContent($"{{\"outcome\":\"{outcome}\"{error}}}"),
                name: "write_value",
                description: "Write a value."),
            IsWrite: true,
            RequiresRequestId: true,
            SchemaDigest: "write-schema");
        var run = new WorldAutonomyRunState(context, ledger, [descriptor]);
        var agent = new WorldAutonomyAgentFactory().Create(
            new ScriptedChatClient(requestId, includeValue: false),
            run,
            [descriptor]);

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("go", session);

        var dispatch = Assert.Single(ledger.PendingDispatches);
        Assert.Equal(expectedStatus, dispatch.DispatchStatus);
        Assert.Equal(errorCode, dispatch.ErrorMessage);
        var activity = run.ActivitySnapshot;
        Assert.Equal(1, activity.NativeWriteCount);
        Assert.Equal(expectedStatus == WorldAutonomyDispatchStatuses.Succeeded ? 1 : 0, activity.SucceededWriteCount);
        Assert.Equal(expectedStatus == WorldAutonomyDispatchStatuses.Failed ? 1 : 0, activity.FailedWriteCount);
        Assert.Equal(expectedStatus == WorldAutonomyDispatchStatuses.PartialFailure ? 1 : 0, activity.PartialFailureWriteCount);
        Assert.Equal(expectedStatus == WorldAutonomyDispatchStatuses.Unknown ? 1 : 0, activity.UnknownWriteCount);
    }

    [Fact]
    public async Task Agent_RecordsSuccessfulNativeReadOutcome()
    {
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            []);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string value) => new TextContent("{\"capability\":\"ready\"}"),
                name: "get_snapshot",
                description: "Read a snapshot."),
            IsWrite: false,
            RequiresRequestId: false,
            SchemaDigest: "read-schema");
        var run = new WorldAutonomyRunState(context, ledger, [descriptor]);
        var agent = new WorldAutonomyAgentFactory().Create(
            new ScriptedChatClient(requestId: null, toolName: "get_snapshot", includeValue: true),
            run,
            [descriptor]);

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("go", session);

        var read = Assert.Single(ledger.RunEvents);
        Assert.Equal("native_read", read.Kind);
        Assert.NotNull(read.PayloadJson);
        using var payload = JsonDocument.Parse(read.PayloadJson!);
        Assert.Equal("get_snapshot", payload.RootElement.GetProperty("toolName").GetString());
        Assert.Equal("success", payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("errorCode").ValueKind);
        Assert.Equal(1, run.ActivitySnapshot.NativeReadCount);
    }

    [Fact]
    public async Task Agent_RecordsMcpTextReadErrorEnvelope()
    {
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            []);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var descriptor = new WorldAutonomyToolDescriptor(
            AIFunctionFactory.Create(
                (string value) => new TextContent("{\"outcome\":\"error\",\"error\":{\"code\":\"journal_unavailable\"}}"),
                name: "list_operations",
                description: "List operations."),
            IsWrite: false,
            RequiresRequestId: false,
            SchemaDigest: "read-schema");
        var agent = new WorldAutonomyAgentFactory().Create(
            new ScriptedChatClient(requestId: null, toolName: "list_operations", includeValue: true),
            new WorldAutonomyRunState(context, ledger, [descriptor]),
            [descriptor]);

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("go", session);

        var read = Assert.Single(ledger.RunEvents);
        Assert.NotNull(read.PayloadJson);
        using var payload = JsonDocument.Parse(read.PayloadJson!);
        Assert.Equal("error", payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("journal_unavailable", payload.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Agent_AppliesTheResolvedWorkloadReasoningProfile()
    {
        var context = new WorldAutonomyRunContext(
            "run-1",
            667956000757776386,
            "message",
            null,
            null,
            null,
            "gpt-5.6-sol",
            "profile-digest",
            "manifest-digest",
            []);
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var client = new ReasoningCapturingChatClient();
        var telemetry = new InMemoryTelemetrySink();
        using var telemetryClient = new LlmCallTaggingChatClient(
            new TelemetryChatClient(client, "OpenAI", telemetry),
            "world_autonomy",
            new LlmWorkloadProfile("gpt-5.6-sol", "ExtraHigh"),
            evaluationId: "run-1");
        var agent = new WorldAutonomyAgentFactory().Create(
            telemetryClient,
            new WorldAutonomyRunState(context, ledger, []),
            [],
            workloadProfile: new LlmWorkloadProfile("gpt-5.6-sol", "ExtraHigh"));

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("scheme", session);

        Assert.Equal("gpt-5.6-sol", client.ModelId);
        Assert.Equal(ReasoningEffort.ExtraHigh, client.ReasoningEffort);
        var llmCall = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.LlmCall, llmCall.EventType);
        Assert.Equal("world_autonomy", llmCall.Workload);
        Assert.Equal("world_autonomy", llmCall.Kind);
        Assert.Equal("gpt-5.6-sol", llmCall.Model);
        Assert.Equal("ExtraHigh", llmCall.ReasoningEffort);
        Assert.Equal("run-1", llmCall.EvaluationId);
    }

    [Fact]
    public async Task Agent_ExplicitCacheUsesNativePrefixWhileOffKeepsLegacyInstructions()
    {
        var context = new WorldAutonomyRunContext(
            "run-cache",
            667956000757776386,
            "message",
            "100",
            "episode-1",
            "trace-1",
            "gpt-5.6-sol",
            "profile-digest",
            "manifest-digest",
            ["01900000-0000-7000-8000-000000000001"],
            SourceChannelId: 200,
            SourceChannelName: "general");
        var ledger = new RecordingLedger();
        await ledger.StartRunAsync(context.ToRunStart(DateTimeOffset.UtcNow), CancellationToken.None);
        var cachedClient = new ReasoningCapturingChatClient();
        var cachedAgent = new WorldAutonomyAgentFactory().Create(
            cachedClient,
            new WorldAutonomyRunState(context, ledger, []),
            [],
            promptCacheMode: WorldAutonomyPromptCacheMode.Explicit);

        _ = await cachedAgent.RunAsync("scheme", await cachedAgent.CreateSessionAsync());

        Assert.Null(cachedClient.Instructions);
        Assert.NotNull(cachedClient.RawRepresentationFactory);

        var legacyClient = new ReasoningCapturingChatClient();
        var legacyAgent = new WorldAutonomyAgentFactory().Create(
            legacyClient,
            new WorldAutonomyRunState(context, ledger, []),
            []);

        _ = await legacyAgent.RunAsync("scheme", await legacyAgent.CreateSessionAsync());

        Assert.Contains("THIS IS NOT A BIT", legacyClient.Instructions, StringComparison.Ordinal);
        Assert.Null(legacyClient.RawRepresentationFactory);
    }

    private sealed class ScriptedChatClient(
        string? requestId,
        string toolName = "write_value",
        bool includeValue = true) : IChatClient
    {
        private int _calls;

        public int CallCount => _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_calls++ == 0)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "model-call-1",
                        toolName,
                        BuildArguments())])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private IDictionary<string, object?> BuildArguments()
        {
            var arguments = new Dictionary<string, object?>();
            if (requestId is not null)
            {
                arguments.Add("request_id", requestId);
            }

            if (includeValue)
            {
                arguments.Add("value", "test");
            }

            return arguments;
        }
    }

    private sealed class SiblingThenDoneChatClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            return Task.FromResult(_calls == 1
                ? new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "finish-call-1",
                            WorldAutonomySpeechTool.TerminalToolName,
                            new Dictionary<string, object?>()),
                        new FunctionCallContent(
                            "read-call-1",
                            "inspect_state",
                            new Dictionary<string, object?>()),
                    ]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DeterministicRetryChatClient(IReadOnlyList<string> requestIds) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var index = Math.Min(_calls++, requestIds.Count - 1);
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    $"model-call-{index + 1}",
                    "write_value",
                    new Dictionary<string, object?>
                    {
                        ["request_id"] = requestIds[index],
                        ["value"] = "unchanged",
                    })])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CorrectedRetryChatClient(IReadOnlyList<string> requestIds) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = _calls++;
            if (call >= requestIds.Count)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    $"model-call-{call + 1}",
                    "write_value",
                    new Dictionary<string, object?>
                    {
                        ["request_id"] = requestIds[call],
                        ["value"] = call == 0 ? "invalid" : "corrected",
                    })])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ReasoningCapturingChatClient : IChatClient
    {
        public string? ModelId { get; private set; }

        public ReasoningEffort? ReasoningEffort { get; private set; }

        public string? Instructions { get; private set; }

        public Func<IChatClient, object?>? RawRepresentationFactory { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ModelId = options?.ModelId;
            ReasoningEffort = options?.Reasoning?.Effort;
            Instructions = options?.Instructions;
            RawRepresentationFactory = options?.RawRepresentationFactory;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLedger : IWorldAutonomyLedger
    {
        internal List<WorldAutonomyToolCall> PendingDispatches { get; } = [];

        internal List<RecordedRunEvent> RunEvents { get; } = [];

        private readonly Dictionary<string, WorldAutonomyRunRecord> _runs = new(StringComparer.Ordinal);

        public Task StartRunAsync(WorldAutonomyRunStart run, CancellationToken cancellationToken)
        {
            _runs.Add(run.RunId, new WorldAutonomyRunRecord(
                run.RunId, run.GuildId, run.Trigger, run.SourceMessageId, run.SourceEpisodeId,
                run.Model, run.ProfileDigest, run.ManifestDigest, run.StartedAt, null,
                WorldAutonomyRunStatuses.Running, null, null));
            return Task.CompletedTask;
        }

        public Task RecordDispatchPendingAsync(WorldAutonomyPendingDispatch dispatch, CancellationToken cancellationToken)
        {
            PendingDispatches.Add(new WorldAutonomyToolCall(
                dispatch.CallId, dispatch.RunId, dispatch.Sequence, dispatch.ToolName, dispatch.RequestId,
                dispatch.ArgumentsJson, dispatch.ArgumentsDigest, dispatch.SchemaDigest,
                WorldAutonomyDispatchStatuses.Pending, dispatch.CreatedAt, null, null, null));
            return Task.CompletedTask;
        }

        public Task CompleteToolCallAsync(
            string callId,
            string dispatchStatus,
            string? resultJson,
            string? errorMessage,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            var index = PendingDispatches.FindIndex(call => call.CallId == callId);
            var existing = PendingDispatches[index];
            PendingDispatches[index] = existing with
            {
                DispatchStatus = dispatchStatus,
                CompletedAt = completedAt,
                ResultJson = resultJson,
                ErrorMessage = errorMessage
            };
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(
            string runId,
            string status,
            string? finalText,
            string? failureReason,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            _runs[runId] = _runs[runId] with
            {
                Status = status,
                FinalText = finalText,
                FailureReason = failureReason,
                CompletedAt = completedAt
            };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorldAutonomyToolCall>> ListRecoverableCallsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorldAutonomyToolCall>>(PendingDispatches
                .Where(call => call.DispatchStatus is WorldAutonomyDispatchStatuses.Pending or WorldAutonomyDispatchStatuses.Accepted or WorldAutonomyDispatchStatuses.Unknown)
                .ToArray());

        public Task<IReadOnlyList<WorldAutonomyRunRecord>> ListRunningRunsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorldAutonomyRunRecord>>(_runs.Values
                .Where(run => run.Status == WorldAutonomyRunStatuses.Running)
                .ToArray());

        public Task<IReadOnlyList<WorldAutonomyToolCall>> ListToolCallsAsync(
            string runId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorldAutonomyToolCall>>(PendingDispatches
                .Where(call => call.RunId == runId)
                .OrderBy(call => call.Sequence)
                .ToArray());

        public Task<WorldAutonomyRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
            Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);

        public Task RecordRunEventAsync(
            string runId,
            string kind,
            string? payloadJson,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken)
        {
            RunEvents.Add(new RecordedRunEvent(runId, kind, payloadJson, occurredAt));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedRunEvent(
        string RunId,
        string Kind,
        string? PayloadJson,
        DateTimeOffset OccurredAt);
}