#pragma warning disable MEAI001

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyTerminalIntegrationTests
{
    [Fact]
    public async Task ProvidedFunctionClient_TerminatesAfterSoleFinalToolWithoutSecondProviderCall()
    {
        var baselineProvider = new FinalToolProvider();
        var baselineInvocations = 0;
        var baselineTool = CreateFinalTool(() => baselineInvocations++);
        var baselineAgent = CreateAgent(baselineProvider, baselineTool, useProvidedClientAsIs: false);

        var baselineSession = await baselineAgent.CreateSessionAsync();
        _ = await baselineAgent.RunAsync("finish", baselineSession);

        Assert.Equal(2, baselineProvider.CallCount);
        Assert.Equal(1, baselineInvocations);

        var terminalProvider = new FinalToolProvider();
        var terminalInvocations = 0;
        var terminalTool = CreateFinalTool(() => terminalInvocations++);
        using var functionClient = CreateTerminalClient(terminalProvider);
        var terminalAgent = CreateAgent(functionClient, terminalTool, useProvidedClientAsIs: true);

        var terminalSession = await terminalAgent.CreateSessionAsync();
        _ = await terminalAgent.RunAsync("finish", terminalSession);

        Assert.Equal(1, terminalProvider.CallCount);
        Assert.Equal(1, terminalInvocations);
    }

    [Fact]
    public async Task ProvidedFunctionClient_DoesNotTerminateOrSkipSiblingCalls()
    {
        var provider = new SiblingToolProvider();
        var finishInvocations = 0;
        var siblingInvocations = 0;
        var finish = CreateFinalTool(() => finishInvocations++);
        var sibling = AIFunctionFactory.Create(
            () =>
            {
                siblingInvocations++;
                return "inspected";
            },
            name: "inspect_state",
            description: "Inspect state.");
        using var functionClient = CreateTerminalClient(provider);
        var agent = CreateAgent(functionClient, [finish, sibling], useProvidedClientAsIs: true);

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("finish and inspect", session);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, finishInvocations);
        Assert.Equal(1, siblingInvocations);
    }

    [Fact]
    public async Task ProvidedFunctionClient_PreservesApprovalAndHostedToolSearchBeforeFinalTool()
    {
        var provider = new WriteThenFinalProvider();
        var writeInvocations = 0;
        var finishInvocations = 0;
        AIFunction write = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            (string value) =>
            {
                writeInvocations++;
                return $"wrote:{value}";
            },
            name: "update_channel",
            description: "Update a channel."));
        var finish = CreateFinalTool(() => finishInvocations++);
        var search = new HostedToolSearchTool
        {
            DeferredTools = ["update_channel"],
            Namespace = "discord_steward",
            NamespaceDescription = "Discord administration tools.",
        };
        using var functionClient = CreateTerminalClient(provider);
        var agent = CreateAgent(
                functionClient,
                [write, finish, search],
                useProvidedClientAsIs: true)
            .AsBuilder()
            .UseToolApproval(new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [_ => ValueTask.FromResult(true)],
            })
            .Build();

        var session = await agent.CreateSessionAsync();
        _ = await agent.RunAsync("write, then finish", session);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, writeInvocations);
        Assert.Equal(1, finishInvocations);
        Assert.Contains(provider.FirstRequestTools, tool => tool is HostedToolSearchTool);
    }

    private static AIAgent CreateAgent(
        IChatClient client,
        AIFunction tool,
        bool useProvidedClientAsIs) => CreateAgent(client, [tool], useProvidedClientAsIs);

    private static AIAgent CreateAgent(
        IChatClient client,
        IList<AITool> tools,
        bool useProvidedClientAsIs) => new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = "Robotnik",
                UseProvidedChatClientAsIs = useProvidedClientAsIs,
                ChatOptions = new ChatOptions { Tools = tools },
            });

    private static FunctionInvokingChatClient CreateTerminalClient(IChatClient inner) => new(
        inner,
        NullLoggerFactory.Instance,
        functionInvocationServices: null)
    {
        FunctionInvoker = async (context, cancellationToken) =>
        {
            var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
            if (context.Function.Name == "finish_with_robotnik_speech" &&
                context.FunctionCount == 1 &&
                context.FunctionCallIndex == 0)
            {
                context.Terminate = true;
            }

            return result;
        },
    };

    private static AIFunction CreateFinalTool(Action invoked) => AIFunctionFactory.Create(
        () =>
        {
            invoked();
            return "delivered";
        },
        name: "finish_with_robotnik_speech",
        description: "Deliver Robotnik's final speech.");

    private sealed class FinalToolProvider : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(CallCount == 1
                ? new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "finish-call-1",
                        "finish_with_robotnik_speech",
                        new Dictionary<string, object?>())]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
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

    private sealed class SiblingToolProvider : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(CallCount == 1
                ? new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("finish-call-1", "finish_with_robotnik_speech", new Dictionary<string, object?>()),
                        new FunctionCallContent("inspect-call-1", "inspect_state", new Dictionary<string, object?>()),
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

    private sealed class WriteThenFinalProvider : IChatClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<AITool> FirstRequestTools { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                FirstRequestTools = options?.Tools?.ToArray() ?? [];
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "write-call-1",
                        "update_channel",
                        new Dictionary<string, object?> { ["value"] = "test" })])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    "finish-call-1",
                    "finish_with_robotnik_speech",
                    new Dictionary<string, object?>())])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}