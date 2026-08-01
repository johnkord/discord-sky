using System.Diagnostics;
using DiscordSky.Bot.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set OPENAI_API_KEY in the environment first. Do not pass a key as an argument.");
    return 2;
}

var model = GetArgument(args, "--model", "gpt-5.6-sol");
var timeoutSeconds = ParseTimeout(GetArgument(args, "--timeout-seconds", "120"));
var confirmation = "deploy-validation";
var approvals = 0;
var invocations = 0;

var provider = new LlmProviderOptions
{
    ApiKey = apiKey,
    ChatModel = model,
    RequestTimeoutMinutes = Math.Max(1, (int)Math.Ceiling(timeoutSeconds / 60d)),
    UseResponsesApi = true
};
using var chatClient = LlmChatClientFactory.Create(provider, model);
var validationFunction = AIFunctionFactory.Create(
    (string confirmation) =>
    {
        if (!string.Equals(confirmation, "deploy-validation", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The hosted-tool-search smoke received an unexpected confirmation value.");
        }

        Interlocked.Increment(ref invocations);
        return "deployment validation tool completed";
    },
    name: "autonomy_smoke_validate",
    description: "Returns a fixed deployment-validation result. Use it exactly once after finding it through tool search.");
var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "Autonomy deployment smoke",
        UseProvidedChatClientAsIs = false,
        ChatOptions = new ChatOptions
        {
            ModelId = model,
            Instructions = "Execute the requested deployment validation exactly. Do not perform unrelated work.",
            Tools =
            [
                new ApprovalRequiredAIFunction(validationFunction),
                new HostedToolSearchTool
                {
                    DeferredTools = [validationFunction.Name],
                    Namespace = "discord_steward_smoke",
                    NamespaceDescription = "Deployment validation tools for the Discord Steward autonomy integration."
                }
            ]
        }
    })
    .AsBuilder()
    .UseToolApproval(new ToolApprovalAgentOptions
    {
        AutoApprovalRules = [context =>
        {
            Interlocked.Increment(ref approvals);
            return ValueTask.FromResult(true);
        }]
    })
    .Build();

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var stopwatch = Stopwatch.StartNew();
try
{
    var session = await agent.CreateSessionAsync();
    var response = await agent.RunAsync(
        $"Use tool search to find the deployment validation tool in the discord_steward_smoke namespace. Call it exactly once with confirmation '{confirmation}'. After the tool result, reply with exactly: live smoke complete.",
        session,
        cancellationToken: timeout.Token);
    stopwatch.Stop();

    if (Volatile.Read(ref approvals) != 1 || Volatile.Read(ref invocations) != 1)
    {
        throw new InvalidOperationException(
            $"Expected one approved invocation, got approvals={approvals}, invocations={invocations}.");
    }

    if (!response.Text.Contains("live smoke complete", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("The model did not produce the expected completion after the tool result.");
    }

    Console.WriteLine($"LIVE AUTONOMY SMOKE PASSED model={model} elapsed_ms={stopwatch.ElapsedMilliseconds} approvals={approvals} invocations={invocations}");
    return 0;
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
    Console.Error.WriteLine($"LIVE AUTONOMY SMOKE TIMED OUT model={model} timeout_seconds={timeoutSeconds}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"LIVE AUTONOMY SMOKE FAILED model={model} error_type={exception.GetType().Name}");
    return 1;
}

static string GetArgument(string[] arguments, string name, string fallback)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : fallback;
}

static int ParseTimeout(string value) =>
    int.TryParse(value, out var seconds) && seconds is >= 30 and <= 300
        ? seconds
        : throw new ArgumentException("--timeout-seconds must be between 30 and 300.");
