using System.Globalization;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

var guildId = ParseGuildId(RequiredArgument(args, "--guild-id"));
var profilePath = Path.GetFullPath(RequiredArgument(args, "--profile"));
var stewardCommand = RequiredArgument(args, "--steward-command");
var stewardWorkingDirectory = Path.GetFullPath(RequiredArgument(args, "--steward-working-directory"));
var ledgerPath = Path.GetFullPath(RequiredArgument(args, "--ledger-path"));
var model = OptionalArgument(args, "--model", "gpt-5.6-sol");
var mode = OptionalArgument(args, "--mode", "read");
var timeoutSeconds = ParseTimeout(OptionalArgument(args, "--timeout-seconds", "600"));
var apiKey = FirstNonEmpty(
    Environment.GetEnvironmentVariable("LLM__Providers__OpenAI__ApiKey"),
    Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
    Environment.GetEnvironmentVariable("OpenAI__ApiKey"));
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set an OpenAI API key in the environment. Do not pass it as an argument.");
    return 2;
}

if (!File.Exists(profilePath))
{
    Console.Error.WriteLine($"Steward profile was not found: {profilePath}");
    return 2;
}

if (!Directory.Exists(stewardWorkingDirectory))
{
    Console.Error.WriteLine($"Steward working directory was not found: {stewardWorkingDirectory}");
    return 2;
}

var roleName = mode == "role-lifecycle" ? $"ds-sky-maf-{Guid.NewGuid():N}"[..25] : null;
var prompt = BuildPrompt(mode, roleName);
var options = new WorldAutonomyOptions
{
    StewardCommand = stewardCommand,
    StewardWorkingDirectory = stewardWorkingDirectory,
    SessionTimeoutMinutes = Math.Clamp((int)Math.Ceiling(timeoutSeconds / 60d), 1, 60),
    RequestIdPoolSize = 40,
    LedgerPath = ledgerPath,
    EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
    {
        [guildId.ToString(CultureInfo.InvariantCulture)] = new()
        {
            ProfilePath = profilePath,
            Model = model
        }
    }
};
var configuration = WorldAutonomyConfiguration.FromOptions(options);
var llmOptions = new LlmOptions
{
    ActiveProvider = "OpenAI",
    Providers = new Dictionary<string, LlmProviderOptions>(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenAI"] = new()
        {
            ApiKey = apiKey!,
            ChatModel = model,
            RequestTimeoutMinutes = Math.Clamp((int)Math.Ceiling(timeoutSeconds / 60d), 1, 60),
            UseResponsesApi = true
        }
    }
};

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "O ";
    }));
using var ledger = new FileBackedWorldAutonomyLedger(options);
await using var supervisor = new StewardMcpSupervisor(
    configuration,
    loggerFactory,
    loggerFactory.CreateLogger<StewardMcpSupervisor>());
var runner = new WorldAutonomyOrchestrator(
    configuration,
    new FixedOptionsMonitor<LlmOptions>(llmOptions),
    supervisor,
    new WorldAutonomyAgentFactory(),
    ledger,
    loggerFactory.CreateLogger<WorldAutonomyOrchestrator>());

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var traceId = Guid.NewGuid().ToString("N");
Console.WriteLine(JsonSerializer.Serialize(new
{
    phase = "starting",
    guildId,
    mode,
    roleName,
    ledgerPath,
    traceId
}));

try
{
    var result = await runner.RunAsync(
        new WorldAutonomyOpportunity(
            guildId,
            "controlled_canary",
            prompt,
            SourceMessageId: null,
            SourceEpisodeId: null,
            TraceId: traceId,
            ModelOverride: model),
        timeout.Token);
    var successfulReadTools = mode == "read"
        ? ReadSuccessfulNativeTools(ledgerPath)
        : Array.Empty<string>();
    var missingReadTools = mode == "read"
        ? GetRequiredReadTools().Where(tool => !successfulReadTools.Contains(tool, StringComparer.Ordinal)).ToArray()
        : Array.Empty<string>();
    var lifecycle = mode == "role-lifecycle" &&
        string.Equals(result.Status, WorldAutonomyRunStatuses.Succeeded, StringComparison.Ordinal) &&
        result.RunId is not null
        ? await VerifyRoleLifecycleAsync(
            configuration,
            options,
            supervisor,
            loggerFactory,
            guildId,
            profilePath,
            ledgerPath,
            result.RunId,
            timeout.Token)
        : null;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        phase = "completed",
        result.RunId,
        result.GuildId,
        result.Status,
        result.FailureReason,
        finalTextLength = result.FinalText?.Length ?? 0,
        successfulReadTools,
        missingReadTools,
        lifecycle,
        roleName,
        ledgerPath
    }));
    return string.Equals(result.Status, WorldAutonomyRunStatuses.Succeeded, StringComparison.Ordinal) &&
        missingReadTools.Length == 0 &&
        (mode != "role-lifecycle" || lifecycle is not null) ? 0 : 1;
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
    Console.Error.WriteLine($"WORLD AUTONOMY CANARY TIMED OUT after {timeoutSeconds} seconds.");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(
        $"WORLD AUTONOMY CANARY FAILED error_type={exception.GetType().Name} message={exception.Message}");
    return 1;
}

static string BuildPrompt(string mode, string? roleName) => mode switch
{
    "read" => """
        This is an owner-approved deployment canary in a disposable Discord guild. Use hosted tool search in the discord_steward namespace to find and call get_steward_capabilities, get_guild_configuration_snapshot, and list_operations. Do not invoke any mutation. After all three native read results succeed, reply exactly: controlled read canary complete.
        """,
    "role-lifecycle" when !string.IsNullOrWhiteSpace(roleName) => $$"""
        This is an owner-approved deployment canary in a disposable Discord guild. Use hosted tool search in the discord_steward namespace and complete exactly one isolated role lifecycle.

        Create only an empty role named {{roleName}}, using a reserved request_id and the current roles state digest. Fetch that role's state, then replace its permissions with only ViewChannel using another reserved request_id. Fetch the role again, analyze its deletion impact, and delete that exact role using a final reserved request_id and the required state and cascade digests. Do not modify any member or any pre-existing resource. Verify each write result before continuing. After the role is deleted, reply exactly: controlled role lifecycle canary complete.
        """,
    _ => throw new ArgumentException("--mode must be 'read' or 'role-lifecycle'.")
};

static string[] GetRequiredReadTools() =>
[
    "get_steward_capabilities",
    "get_guild_configuration_snapshot",
    "list_operations"
];

static string[] ReadSuccessfulNativeTools(string ledgerPath)
{
    using var document = JsonDocument.Parse(File.ReadAllText(ledgerPath));
    var successfulTools = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in document.RootElement.GetProperty("events").EnumerateArray())
    {
        if (!string.Equals(entry.GetProperty("kind").GetString(), "native_read", StringComparison.Ordinal))
        {
            continue;
        }

        var payloadJson = entry.GetProperty("payloadJson").GetString();
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            continue;
        }

        using var payload = JsonDocument.Parse(payloadJson);
        var root = payload.RootElement;
        if (!root.TryGetProperty("outcome", out var outcome) ||
            !IsSuccessfulReadOutcome(outcome.GetString()))
        {
            continue;
        }

        var toolName = root.GetProperty("toolName").GetString();
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            successfulTools.Add(toolName);
        }
    }

    return successfulTools.Order(StringComparer.Ordinal).ToArray();
}

static bool IsSuccessfulReadOutcome(string? outcome) =>
    string.Equals(outcome, "ok", StringComparison.Ordinal) ||
    string.Equals(outcome, "success", StringComparison.Ordinal) ||
    string.Equals(outcome, "succeeded", StringComparison.Ordinal);

static async Task<RoleLifecycleEvidence> VerifyRoleLifecycleAsync(
    WorldAutonomyConfiguration configuration,
    WorldAutonomyOptions options,
    StewardMcpSupervisor supervisor,
    ILoggerFactory loggerFactory,
    ulong guildId,
    string profilePath,
    string ledgerPath,
    string runId,
    CancellationToken cancellationToken)
{
    var operations = ReadStewardLifecycleOperations(profilePath, runId);
    string[] expectedTools = ["create_role", "set_role_permissions", "delete_role"];
    if (!operations.Select(operation => operation.Kind).Order(StringComparer.Ordinal)
            .SequenceEqual(expectedTools.Order(StringComparer.Ordinal), StringComparer.Ordinal))
    {
        throw new InvalidOperationException("The role lifecycle did not create exactly the expected Steward operations.");
    }

    var create = operations.Single(operation => operation.Kind == "create_role");
    var roleId = ObservedResourceId(create.ObservedStateJson);
    if (operations.Where(operation => operation.Kind != "create_role")
            .Any(operation => !string.Equals(operation.ResourceId, roleId, StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("The role lifecycle operations did not target one role.");
    }

    VerifySkyDispatches(ledgerPath, operations);
    var recoveryStatuses = await RecoverLifecycleDispatchesAsync(
        configuration,
        options,
        supervisor,
        loggerFactory,
        ledgerPath,
        operations.Select(operation => operation.RequestId).ToArray(),
        cancellationToken).ConfigureAwait(false);
    var recoveredOperations = ReadStewardLifecycleOperations(profilePath, runId);
    if (recoveredOperations.Any(operation => !string.Equals(operation.Status, "succeeded", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("The Steward role lifecycle did not reach terminal success after recovery.");
    }

    var session = await supervisor.GetSessionAsync(guildId, cancellationToken).ConfigureAwait(false);
    await VerifyDeletedRoleAsync(session, roleId, cancellationToken).ConfigureAwait(false);
    return new RoleLifecycleEvidence(
        roleId,
        operations.OrderBy(operation => operation.Kind, StringComparer.Ordinal)
            .Select(operation => operation.RequestId)
            .ToArray(),
        recoveryStatuses);
}

    static StewardLifecycleOperation[] ReadStewardLifecycleOperations(string profilePath, string runId)
{
    using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
    var journalPath = profile.RootElement.GetProperty("Steward").GetProperty("DataPath").GetString();
    if (string.IsNullOrWhiteSpace(journalPath) || !File.Exists(journalPath))
    {
        throw new InvalidOperationException("The role lifecycle did not create the expected Steward journal snapshot.");
    }

    using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
    return journal.RootElement.GetProperty("operations")
        .EnumerateObject()
        .Select(property => property.Value)
        .Where(operation => IsRunMetadata(operation.GetProperty("requestMetadataJson").GetString(), runId))
        .Select(operation => new StewardLifecycleOperation(
            RequiredString(operation, "requestId"),
            RequiredString(operation, "kind"),
            RequiredString(operation, "resourceId"),
            operation.GetProperty("observedStateJson").ValueKind == JsonValueKind.String
                ? operation.GetProperty("observedStateJson").GetString()
                : null,
            RequiredString(operation, "status")))
        .ToArray();
}

static bool IsRunMetadata(string? metadataJson, string runId)
{
    if (string.IsNullOrWhiteSpace(metadataJson))
    {
        return false;
    }

    using var metadata = JsonDocument.Parse(metadataJson);
    return metadata.RootElement.TryGetProperty("discordSky", out var discordSky) &&
        string.Equals(discordSky.GetProperty("runId").GetString(), runId, StringComparison.Ordinal);
}

static string ObservedResourceId(string? observedStateJson)
{
    if (string.IsNullOrWhiteSpace(observedStateJson))
    {
        throw new InvalidOperationException("The create_role operation did not retain observed role evidence.");
    }

    using var observed = JsonDocument.Parse(observedStateJson);
    return RequiredString(observed.RootElement, "id");
}

static void VerifySkyDispatches(string ledgerPath, IReadOnlyList<StewardLifecycleOperation> operations)
{
    var expectedRequestIds = operations.Select(operation => operation.RequestId).ToHashSet(StringComparer.Ordinal);
    using var ledger = JsonDocument.Parse(File.ReadAllText(ledgerPath));
    var calls = ledger.RootElement.GetProperty("toolCalls")
        .EnumerateObject()
        .Select(property => property.Value)
        .Where(call => expectedRequestIds.Contains(call.GetProperty("requestId").GetString() ?? string.Empty))
        .Select(call => new SkyLifecycleDispatch(
            RequiredString(call, "toolName"),
            RequiredString(call, "requestId"),
            RequiredString(call, "dispatchStatus")))
        .ToArray();
    if (calls.Length != operations.Count ||
        calls.Any(call => call.DispatchStatus is not
            (WorldAutonomyDispatchStatuses.Accepted or
             WorldAutonomyDispatchStatuses.Succeeded or
             WorldAutonomyDispatchStatuses.Unknown)) ||
        !calls.Select(call => call.ToolName).Order(StringComparer.Ordinal)
            .SequenceEqual(operations.Select(operation => operation.Kind).Order(StringComparer.Ordinal), StringComparer.Ordinal))
    {
        throw new InvalidOperationException("Sky did not durably record exactly the expected role lifecycle dispatches.");
    }
}

static async Task<string[]> RecoverLifecycleDispatchesAsync(
    WorldAutonomyConfiguration configuration,
    WorldAutonomyOptions options,
    StewardMcpSupervisor supervisor,
    ILoggerFactory loggerFactory,
    string ledgerPath,
    string[] requestIds,
    CancellationToken cancellationToken)
{
    using var recoveredLedger = new FileBackedWorldAutonomyLedger(options);
    using var recovery = new WorldAutonomyRecoveryService(
        configuration,
        recoveredLedger,
        supervisor,
        loggerFactory.CreateLogger<WorldAutonomyRecoveryService>());
    await recovery.StartAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var statuses = ReadSkyDispatchStatuses(ledgerPath, requestIds);
            if (statuses.Length == requestIds.Length &&
                statuses.All(status => string.Equals(status, WorldAutonomyDispatchStatuses.Succeeded, StringComparison.Ordinal)))
            {
                return statuses;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Sky did not preserve terminal success for every role lifecycle dispatch after reopening.");
    }
    finally
    {
        await recovery.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

static string[] ReadSkyDispatchStatuses(string ledgerPath, IReadOnlyCollection<string> requestIds)
{
    var expectedRequestIds = requestIds.ToHashSet(StringComparer.Ordinal);
    using var ledger = JsonDocument.Parse(File.ReadAllText(ledgerPath));
    return ledger.RootElement.GetProperty("toolCalls")
        .EnumerateObject()
        .Select(property => property.Value)
        .Where(call => expectedRequestIds.Contains(call.GetProperty("requestId").GetString() ?? string.Empty))
        .Select(call => RequiredString(call, "dispatchStatus"))
        .Order(StringComparer.Ordinal)
        .ToArray();
}

static async Task VerifyDeletedRoleAsync(
    WorldAutonomyStewardSession session,
    string roleId,
    CancellationToken cancellationToken)
{
    var result = await session.Catalog.GetNativeTool("get_role")
        .CallAsync(new Dictionary<string, object?> { ["role_id"] = roleId }, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    var envelope = ReadEnvelope(result);
    if (!string.Equals(RequiredString(envelope, "outcome"), "error", StringComparison.Ordinal) ||
        !string.Equals(ReadErrorCode(envelope), "resource_not_found", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The generated role still exists after the lifecycle delete.");
    }
}

static JsonElement ReadEnvelope(CallToolResult result)
{
    if (result.IsError == true)
    {
        throw new InvalidOperationException("Steward lifecycle verification received an MCP tool error.");
    }

    var text = result.Content.OfType<TextContentBlock>().SingleOrDefault()?.Text
        ?? throw new InvalidOperationException("Steward lifecycle verification received no JSON envelope.");
    using var document = JsonDocument.Parse(text);
    return document.RootElement.Clone();
}

static string RequiredString(JsonElement root, string propertyName) =>
    root.TryGetProperty(propertyName, out var property) &&
    property.ValueKind == JsonValueKind.String &&
    !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()!
        : throw new InvalidOperationException($"Steward lifecycle evidence omitted '{propertyName}'.");

static string? ReadErrorCode(JsonElement root) => root.TryGetProperty("error", out var error) &&
    error.ValueKind == JsonValueKind.Object &&
    error.TryGetProperty("code", out var code) &&
    code.ValueKind == JsonValueKind.String
        ? code.GetString()
        : null;

static string RequiredArgument(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length && !string.IsNullOrWhiteSpace(arguments[index + 1])
        ? arguments[index + 1]
        : throw new ArgumentException($"{name} is required.");
}

static string OptionalArgument(string[] arguments, string name, string fallback)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length && !string.IsNullOrWhiteSpace(arguments[index + 1])
        ? arguments[index + 1]
        : fallback;
}

static ulong ParseGuildId(string value) =>
    ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var guildId) && guildId != 0
        ? guildId
        : throw new ArgumentException("--guild-id must be a non-zero Discord guild ID.");

static int ParseTimeout(string value) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 60 and <= 3600
        ? seconds
        : throw new ArgumentException("--timeout-seconds must be between 60 and 3600.");

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    where T : class
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;
}

sealed class NoopDisposable : IDisposable
{
    public static NoopDisposable Instance { get; } = new();

    public void Dispose()
    {
    }
}

sealed record StewardLifecycleOperation(
    string RequestId,
    string Kind,
    string ResourceId,
    string? ObservedStateJson,
    string Status);

sealed record SkyLifecycleDispatch(string ToolName, string RequestId, string DispatchStatus);

sealed record RoleLifecycleEvidence(string RoleId, string[] RequestIds, string[] RecoveryStatuses);