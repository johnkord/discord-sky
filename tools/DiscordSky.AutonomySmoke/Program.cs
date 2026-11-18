using System.Diagnostics;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var providerName = GetArgument(args, "--provider", new LlmOptions().ActiveProvider);
if (providerName is not ("xAI" or "OpenAI")) throw new ArgumentException("--provider must be xAI or OpenAI.");
var apiKeyVariable = providerName == "xAI" ? "XAI_API_KEY" : "OPENAI_API_KEY";
var apiKey = Environment.GetEnvironmentVariable(apiKeyVariable);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"Set {apiKeyVariable} in the environment first. Do not pass a key as an argument.");
    return 2;
}

var model = GetArgument(args, "--model", providerName == "xAI" ? "grok-4.7" : new LlmProviderOptions().ChatModel);
var profile = new LlmWorkloadProfile(model, GetArgument(args, "--effort", "ExtraHigh"));
var useHostedToolSearch = bool.Parse(GetArgument(args, "--hosted-tool-search", providerName == "xAI" ? "false" : "true"));
var cacheMode = Enum.Parse<WorldAutonomyPromptCacheMode>(GetArgument(args, "--cache-mode", "Off"), ignoreCase: true);
var timeoutSeconds = ParseTimeout(GetArgument(args, "--timeout-seconds", "120"));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var confirmation = "deploy-validation";
var approvals = 0;
var invocations = 0;

var provider = new LlmProviderOptions
{
    ApiKey = apiKey,
    Endpoint = providerName == "xAI" ? "https://api.x.ai/v1" : null,
    ChatModel = model,
    RequestTimeoutMinutes = Math.Max(1, (int)Math.Ceiling(timeoutSeconds / 60d)),
    UseResponsesApi = true
};
var smokeTelemetry = new InMemoryTelemetrySink();
var smokeGuard = new LlmProviderGuard(Options.Create(new LlmOptions
{
    Guard = new LlmProviderGuardOptions
    {
        HourlyUsdLimit = 1,
        DailyUsdLimit = 1,
        StatePath = GetArgument(args, "--guard-state", "data/autonomy-smoke-provider-guard.json"),
    },
}), smokeTelemetry, NullLogger<LlmProviderGuard>.Instance);
var rawClient = new LlmCallTaggingChatClient(
    new TelemetryChatClient(LlmChatClientFactory.Create(provider, model), providerName, smokeTelemetry, smokeGuard),
    "autonomy_smoke", profile);
using var chatClient = cacheMode == WorldAutonomyPromptCacheMode.Off ? (IChatClient)rawClient : WorldAutonomyPromptCache.Wrap(rawClient);
var validationFunction = AIFunctionFactory.Create(
    (string confirmation) =>
    {
        if (!string.Equals(confirmation, "deploy-validation", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The tool-loop smoke received an unexpected confirmation value.");
        }

        if (Interlocked.Increment(ref invocations) > 1)
            throw new InvalidOperationException("The validation function may run only once.");
        return "deployment validation tool completed";
    },
    name: "autonomy_smoke_validate",
    description: "Returns a fixed deployment-validation result. Use it exactly once.");
var chatOptions = new ChatOptions
{
    ModelId = model,
    MaxOutputTokens = profile.WithReasoningHeadroom(1200),
    Instructions = "Execute the requested deployment validation exactly. Do not perform unrelated work. After the validation tool result, reply with exactly: live smoke complete.",
    Tools = [new ApprovalRequiredAIFunction(validationFunction)]
};
if (useHostedToolSearch)
{
    chatOptions.Tools.Add(new HostedToolSearchTool
        {
            DeferredTools = [validationFunction.Name],
            Namespace = "discord_steward_smoke",
            NamespaceDescription = "Deployment validation tools for the Discord Steward autonomy integration."
        });
    }
    var stewardAssembly = GetArgument(args, "--steward-assembly", "");
    var stewardProfile = GetArgument(args, "--steward-profile", "");
    if (string.IsNullOrWhiteSpace(stewardAssembly) != string.IsNullOrWhiteSpace(stewardProfile))
        throw new ArgumentException("Supply both --steward-assembly and --steward-profile for the full-catalog check.");
    WorldAutonomyConfiguration? catalogConfiguration = null;
    ulong catalogGuildId = 0;
    if (!string.IsNullOrWhiteSpace(stewardAssembly))
    {
        using var profileDocument = JsonDocument.Parse(File.ReadAllText(stewardProfile));
        catalogGuildId = ulong.Parse(profileDocument.RootElement.GetProperty("Discord").GetProperty("GuildId").GetString()!);
        catalogConfiguration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            StewardArguments = [Path.GetFullPath(stewardAssembly)],
            StewardWorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(stewardAssembly))!,
            EnabledGuilds = new() { [catalogGuildId.ToString()] = new() { ProfilePath = Path.GetFullPath(stewardProfile) } },
        });
    }
    await using var catalogSupervisor = catalogConfiguration is null ? null : new StewardMcpSupervisor(
        catalogConfiguration, NullLoggerFactory.Instance, NullLogger<StewardMcpSupervisor>.Instance);
    var nativeToolCount = 0;
    if (catalogSupervisor is not null)
    {
        var catalogSession = await catalogSupervisor.GetSessionAsync(catalogGuildId, timeout.Token);
        var context = WorldAutonomyRunContext.Create(catalogGuildId, "schema_smoke", model,
            catalogSession.Catalog.Capabilities.ProfileDigest, catalogSession.Catalog.Capabilities.ManifestDigest, 1);
        var catalog = catalogSession.Catalog.Bind(context, useHostedToolSearch);
        foreach (var tool in catalog.Tools)
            chatOptions.Tools.Add(AIFunctionFactory.CreateDeclaration(tool.Function.Name, tool.Function.Description, tool.Function.JsonSchema));
        foreach (var tool in catalog.SupplementaryTools) chatOptions.Tools.Add(tool);
        nativeToolCount = catalog.Tools.Length;
        chatOptions.Instructions += " The native Discord schemas are present only for compatibility validation. Do not call any native Discord function; only autonomy_smoke_validate is executable.";
    }
profile.ApplyReasoning(chatOptions);
if (cacheMode != WorldAutonomyPromptCacheMode.Off)
{
    if (providerName != "OpenAI") throw new ArgumentException("Cache smoke requires the OpenAI provider.");
    var cacheContext = WorldAutonomyRunContext.Create(4001, "cache_smoke", model, new string('a', 64), new string('b', 64), 1);
    chatOptions.Instructions = WorldAutonomyPrompt.BuildInstructions(cacheContext, useHostedToolSearch: useHostedToolSearch) +
        "\nThis is a trusted deployment self-test, not a Discord member petition. " + chatOptions.Instructions;
    WorldAutonomyPromptCache.Configure(chatOptions, cacheContext, false, useHostedToolSearch, cacheMode);
}
var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "Autonomy deployment smoke",
        UseProvidedChatClientAsIs = false,
        ChatOptions = chatOptions
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

var stopwatch = Stopwatch.StartNew();
try
{
    var session = await agent.CreateSessionAsync();
    var instruction = useHostedToolSearch
        ? "Use tool search to find the deployment validation tool in the discord_steward_smoke namespace."
        : "Use the autonomy_smoke_validate function tool directly.";
    var response = await agent.RunAsync(
        $"{instruction} Call it exactly once with confirmation '{confirmation}'. Then follow your original instructions for the final answer.",
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

    Console.WriteLine($"LIVE AUTONOMY SMOKE PASSED provider={providerName} model={model} effort={profile.ReasoningEffort} native_schemas={nativeToolCount} elapsed_ms={stopwatch.ElapsedMilliseconds} approvals={approvals} invocations={invocations}");
    Console.WriteLine($"cache_mode={cacheMode} local_guard_daily_usd={smokeGuard.Snapshot().DailyCostUsd:F6}");
    foreach (var metric in smokeTelemetry.Events.Where(metric => metric.EventType == TelemetryEventTypes.LlmCall))
        Console.WriteLine(JsonSerializer.Serialize(metric, new JsonSerializerOptions
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    return 0;
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
    Console.Error.WriteLine($"LIVE AUTONOMY SMOKE TIMED OUT model={model} timeout_seconds={timeoutSeconds}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"LIVE AUTONOMY SMOKE FAILED provider={providerName} model={model} error_type={exception.GetType().Name}");
    if (exception is System.ClientModel.ClientResultException apiError)
    {
        Console.Error.WriteLine($"Provider error: {apiError.Status} {apiError.Message}");
        Console.Error.WriteLine($"Provider response: {apiError.GetRawResponse()?.Content}");
    }
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
