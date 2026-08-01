using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var profilePath = RequiredArgument(args, "--profile");
var guildId = RequiredArgument(args, "--guild-id");
var stewardAssembly = RequiredArgument(args, "--steward-assembly");
var botToken = Environment.GetEnvironmentVariable("Discord__BotToken");
if (string.IsNullOrWhiteSpace(botToken))
{
    Console.Error.WriteLine("Set Discord__BotToken in the environment. Do not pass a token as an argument.");
    return 2;
}

var suppliedDataRoot = GetArgument(args, "--data-root", fallback: null);
var dataRoot = suppliedDataRoot ?? Path.Combine(Path.GetTempPath(), $"discord-sky-disposable-canary-{Guid.NewGuid():N}");
var roleName = $"ds-autonomy-role-{Guid.NewGuid():N}"[..25];
var runId = Guid.NewGuid().ToString("D");
var requestIds = new[]
{
    Guid.NewGuid().ToString("D"),
    Guid.NewGuid().ToString("D"),
    Guid.NewGuid().ToString("D")
};
var roleId = (string?)null;
var createdRole = false;
var stage = "initialization";

if (!File.Exists(stewardAssembly))
{
    Console.Error.WriteLine($"Steward assembly was not found: {stewardAssembly}");
    return 2;
}

Console.WriteLine($"DISPOSABLE AUTONOMY CANARY CONTEXT guild={guildId} role={roleName} run_id={runId} data_root={dataRoot} operations={string.Join(',', requestIds)}");

var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
environment["DOTNET_ENVIRONMENT"] = "Production";
environment["Steward__ProfilePath"] = Path.GetFullPath(profilePath);
environment["Discord__BotToken"] = botToken;
environment["Steward__DataPath"] = Path.Combine(dataRoot, "operations.db");
environment["Steward__AssetInboxPath"] = Path.Combine(dataRoot, "asset-inbox");
environment["Steward__AssetVaultPath"] = Path.Combine(dataRoot, "assets");
environment["Steward__WebhookSecretVaultPath"] = Path.Combine(dataRoot, "webhook-secrets");

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var stopwatch = Stopwatch.StartNew();
try
{
    await using var client = await McpClient.CreateAsync(
        new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Discord Sky disposable autonomy canary",
            Command = "dotnet",
            Arguments = [Path.GetFullPath(stewardAssembly)],
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(stewardAssembly)),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment
        }),
        cancellationToken: timeout.Token);

    var tools = (await client.ListToolsAsync(cancellationToken: timeout.Token))
        .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    string[] requiredTools =
    [
        "get_steward_capabilities",
        "get_guild_configuration_snapshot",
        "list_operations",
        "create_role",
        "get_role",
        "set_role_permissions",
        "analyze_deletion_impact",
        "delete_role",
        "get_operation",
        "reconcile_operation"
    ];
    var missing = requiredTools.Where(name => !tools.ContainsKey(name)).ToArray();
    if (missing.Length > 0)
    {
        throw new InvalidOperationException($"The unrestricted Steward catalog omitted: {string.Join(", ", missing)}.");
    }

    // R0: local capabilities, then assert the full unrestricted profile selected this exact guild.
    stage = "R0 capabilities";
    using (var capabilities = await CallAsync(tools["get_steward_capabilities"], null, timeout.Token))
    {
        var data = capabilities.RootElement;
        RequireEqual("UnrestrictedAutonomy", RequiredString(data, "authorizationMode"), "authorization mode");
        RequireEqual("unrestricted", RequiredString(data, "mode"), "mode");
        RequireEqual(guildId, RequiredString(data, "guildId"), "guild ID");
        if (data.GetProperty("policy").GetProperty("protectedResourceCount").GetInt32() != 0 ||
            data.GetProperty("policy").GetProperty("protectedNamePrefixCount").GetInt32() != 0 ||
            data.GetProperty("policy").GetProperty("deniedPermissionCount").GetInt32() != 0)
        {
            throw new InvalidOperationException("The disposable canary profile still reports local policy protections.");
        }
    }

    // R1: obtain the current roles digest needed to bind the role creation.
    stage = "R1 guild configuration snapshot";
    string rolesStateDigest;
    using (var snapshot = await CallOkAsync(tools["get_guild_configuration_snapshot"], null, timeout.Token))
    {
        rolesStateDigest = RequiredString(Data(snapshot.RootElement), "rolesStateDigest");
    }

    // R2: prove the sensitive journal-read tier is available before any write.
    stage = "R2 operation listing";
    using (var operations = await CallOkAsync(
        tools["list_operations"],
        new Dictionary<string, object?> { ["limit"] = 10 },
        timeout.Token))
    {
        _ = RequiredData(operations.RootElement);
    }

    var metadata = new JsonObject
    {
        ["discordSky"] = new JsonObject
        {
            ["canary"] = "disposable_role_lifecycle",
            ["runId"] = runId,
            ["guildId"] = guildId
        }
    };
    McpClientTool WriteTool(string name) => tools[name].WithMeta((JsonObject)metadata.DeepClone());

    // R3: create a uniquely named empty role.
    stage = "R3 role creation";
    using (var created = await CallSucceededAsync(
        WriteTool("create_role"),
        new Dictionary<string, object?>
        {
            ["request_id"] = requestIds[0],
            ["reason"] = "Disposable autonomy canary R3 role creation",
            ["expected_roles_state_digest"] = rolesStateDigest,
            ["name"] = roleName,
            ["permissions"] = Array.Empty<string>()
        },
        timeout.Token))
    {
        roleId = RequiredString(Data(created.RootElement).GetProperty("observed"), "id");
        createdRole = true;
    }

    // R4: replace the role permission set with a harmless single permission.
    stage = "R4 role permission update";
    string roleStateDigest;
    using (var role = await CallOkAsync(
        tools["get_role"],
        new Dictionary<string, object?> { ["role_id"] = roleId },
        timeout.Token))
    {
        roleStateDigest = RequiredString(Data(role.RootElement), "stateDigest");
    }
    using (var updated = await CallSucceededAsync(
        WriteTool("set_role_permissions"),
        new Dictionary<string, object?>
        {
            ["request_id"] = requestIds[1],
            ["role_id"] = roleId,
            ["reason"] = "Disposable autonomy canary R4 role permission update",
            ["expected_state_digest"] = roleStateDigest,
            ["permissions"] = new[] { "ViewChannel" }
        },
        timeout.Token))
    {
        _ = Data(updated.RootElement);
    }

    // R5: delete only the role created above after checking its cascade is empty.
    stage = "R5 role cleanup";
    using (var role = await CallOkAsync(
        tools["get_role"],
        new Dictionary<string, object?> { ["role_id"] = roleId },
        timeout.Token))
    {
        roleStateDigest = RequiredString(Data(role.RootElement), "stateDigest");
    }
    string cascadeDigest;
    using (var impact = await CallOkAsync(
        tools["analyze_deletion_impact"],
        new Dictionary<string, object?> { ["resource_type"] = "role", ["resource_id"] = roleId },
        timeout.Token))
    {
        var impactData = Data(impact.RootElement);
        if (impactData.GetProperty("blockers").GetArrayLength() != 0 || impactData.GetProperty("cascade").GetArrayLength() != 0)
        {
            throw new InvalidOperationException("The disposable role unexpectedly has deletion blockers or a cascade.");
        }

        cascadeDigest = RequiredString(impactData, "cascadeDigest");
    }
    using (var deleted = await CallAsync(
        WriteTool("delete_role"),
        new Dictionary<string, object?>
        {
            ["request_id"] = requestIds[2],
            ["role_id"] = roleId,
            ["reason"] = "Disposable autonomy canary R5 role cleanup",
            ["expected_state_digest"] = roleStateDigest,
            ["expected_cascade_digest"] = cascadeDigest
        },
        timeout.Token))
    {
        var deleteOutcome = RequiredString(deleted.RootElement, "outcome");
        if (string.Equals(deleteOutcome, "unknown", StringComparison.Ordinal))
        {
            stage = "R5 operation lookup";
            using var unknown = await CallOkAsync(
                tools["get_operation"],
                new Dictionary<string, object?> { ["request_id"] = requestIds[2] },
                timeout.Token);
            RequireEqual("unknown", RequiredString(Data(unknown.RootElement), "status"), "unknown delete status");

            stage = "R5 reconciliation";
            using var reconciled = await CallSucceededAsync(
                tools["reconcile_operation"],
                new Dictionary<string, object?> { ["request_id"] = requestIds[2] },
                timeout.Token);
            RequireEqual("succeeded", RequiredString(Data(reconciled.RootElement), "status"), "reconciled delete status");
        }
        else if (!string.Equals(deleteOutcome, "succeeded", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"delete_role returned outcome '{ErrorCode(deleted.RootElement)}'.");
        }
    }
    createdRole = false;

    foreach (var requestId in requestIds)
    {
        stage = "operation evidence";
        using var operation = await CallOkAsync(
            tools["get_operation"],
            new Dictionary<string, object?> { ["request_id"] = requestId },
            timeout.Token);
        var operationData = Data(operation.RootElement);
        RequireEqual("succeeded", RequiredString(operationData, "status"), $"operation {requestId} status");
        var requestMetadata = RequiredString(operationData, "requestMetadataJson");
        if (!requestMetadata.Contains("disposable_role_lifecycle", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Operation {requestId} did not retain canary request metadata.");
        }
    }

    stopwatch.Stop();
    Console.WriteLine($"DISPOSABLE AUTONOMY CANARY PASSED guild={guildId} role={roleName} elapsed_ms={stopwatch.ElapsedMilliseconds} operations={string.Join(',', requestIds)}");
    return 0;
}
catch (Exception exception)
{
    stopwatch.Stop();
    var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
    Console.Error.WriteLine($"DISPOSABLE AUTONOMY CANARY FAILED guild={guildId} stage={stage} role={roleName} role_id={roleId ?? "none"} error_type={exception.GetType().Name} message={message[..Math.Min(message.Length, 500)]} elapsed_ms={stopwatch.ElapsedMilliseconds}");
    if (createdRole && roleId is not null)
    {
        Console.Error.WriteLine("The created role may require manual cleanup. Its exact ID was printed above.");
    }

    return 1;
}
finally
{
    environment.Remove("Discord__BotToken");
    Console.Error.WriteLine($"Disposable canary evidence retained at {dataRoot}.");
}

static async Task<JsonDocument> CallOkAsync(
    McpClientTool tool,
    IReadOnlyDictionary<string, object?>? arguments,
    CancellationToken cancellationToken)
{
    var document = await CallAsync(tool, arguments, cancellationToken);
    if (!string.Equals(RequiredString(document.RootElement, "outcome"), "ok", StringComparison.Ordinal))
    {
        var error = ErrorCode(document.RootElement);
        document.Dispose();
        throw new InvalidOperationException($"{tool.Name} returned outcome '{error}'.");
    }

    return document;
}

static async Task<JsonDocument> CallSucceededAsync(
    McpClientTool tool,
    IReadOnlyDictionary<string, object?> arguments,
    CancellationToken cancellationToken)
{
    var document = await CallAsync(tool, arguments, cancellationToken);
    if (!string.Equals(RequiredString(document.RootElement, "outcome"), "succeeded", StringComparison.Ordinal))
    {
        var error = ErrorCode(document.RootElement);
        document.Dispose();
        throw new InvalidOperationException($"{tool.Name} returned outcome '{error}'.");
    }

    return document;
}

static async Task<JsonDocument> CallAsync(
    McpClientTool tool,
    IReadOnlyDictionary<string, object?>? arguments,
    CancellationToken cancellationToken)
{
    var result = await tool.CallAsync(arguments, cancellationToken: cancellationToken);
    if (result.IsError == true)
    {
        throw new InvalidOperationException($"{tool.Name} returned an MCP tool error.");
    }

    var content = result.Content.OfType<TextContentBlock>().SingleOrDefault()
        ?? throw new InvalidOperationException($"{tool.Name} returned no JSON text content.");
    return JsonDocument.Parse(content.Text);
}

static JsonElement RequiredData(JsonElement envelope) => envelope.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null
    ? data
    : throw new InvalidOperationException("Steward envelope omitted data.");

static JsonElement Data(JsonElement envelope)
{
    var data = RequiredData(envelope);
    return data.ValueKind == JsonValueKind.Object
        ? data
        : throw new InvalidOperationException("Steward envelope data was not an object.");
}

static string RequiredString(JsonElement root, string propertyName) =>
    root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
    !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()!
        : throw new InvalidOperationException($"Steward response omitted '{propertyName}'.");

static string ErrorCode(JsonElement envelope) => envelope.TryGetProperty("error", out var error) &&
    error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code)
    ? code.GetString() ?? "unknown"
    : "unknown";

static void RequireEqual(string expected, string actual, string field)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected {field} '{expected}', got '{actual}'.");
    }
}

static string RequiredArgument(string[] arguments, string name)
{
    var value = GetArgument(arguments, name, fallback: null);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException($"{name} is required.");
    }

    return value;
}

static string? GetArgument(string[] arguments, string name, string? fallback)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : fallback;
}
