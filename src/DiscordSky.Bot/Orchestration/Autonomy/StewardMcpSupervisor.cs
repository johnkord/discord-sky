using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record StewardCapabilitiesSnapshot(
    ulong GuildId,
    string Profile,
    string ProfileDigest,
    string AuthorizationMode,
    string Mode,
    string ManifestDigest,
    ImmutableArray<string> RegisteredTools,
    int ProtectedResourceCount,
    int ProtectedNamePrefixCount,
    int DeniedPermissionCount)
{
    internal static StewardCapabilitiesSnapshot Parse(CallToolResult result, ulong expectedGuildId)
    {
        if (result.IsError == true)
        {
            throw new InvalidOperationException("Steward get_steward_capabilities returned an MCP tool error.");
        }

        var text = result.Content.OfType<TextContentBlock>().SingleOrDefault()?.Text
            ?? throw new InvalidOperationException("Steward capabilities response did not contain JSON text.");
        using var document = JsonDocument.Parse(text);
        return Parse(document.RootElement, expectedGuildId);
    }

    internal static StewardCapabilitiesSnapshot Parse(JsonElement root, ulong expectedGuildId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Steward capabilities response must be a JSON object.");
        }

        var guildIdText = RequiredString(root, "guildId");
        if (!ulong.TryParse(guildIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var guildId) || guildId != expectedGuildId)
        {
            throw new InvalidOperationException(
                $"Steward reported guild '{guildIdText}', not the bound guild '{expectedGuildId.ToString(CultureInfo.InvariantCulture)}'.");
        }

        var registeredTools = root.GetProperty("registeredTools")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var policy = root.GetProperty("policy");
        var snapshot = new StewardCapabilitiesSnapshot(
            guildId,
            RequiredString(root, "profile"),
            RequiredString(root, "profileDigest"),
            RequiredString(root, "authorizationMode"),
            RequiredString(root, "mode"),
            RequiredString(root, "manifestDigest"),
            registeredTools,
            policy.GetProperty("protectedResourceCount").GetInt32(),
            policy.GetProperty("protectedNamePrefixCount").GetInt32(),
            policy.GetProperty("deniedPermissionCount").GetInt32());
        snapshot.EnsureUnrestricted();
        return snapshot;
    }

    internal void ValidateRegisteredTools(IEnumerable<string> actualToolNames)
    {
        var actual = actualToolNames.Order(StringComparer.Ordinal).ToArray();
        if (!RegisteredTools.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Steward capability manifest does not match the tools exposed by its MCP server.");
        }
    }

    private void EnsureUnrestricted()
    {
        if (!string.Equals(AuthorizationMode, "UnrestrictedAutonomy", StringComparison.Ordinal) ||
            !string.Equals(Mode, "unrestricted", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The bound Steward profile is not running UnrestrictedAutonomy mode.");
        }

        if (ProtectedResourceCount != 0 || ProtectedNamePrefixCount != 0 || DeniedPermissionCount != 0)
        {
            throw new InvalidOperationException(
                "The bound unrestricted Steward profile still reports local policy protections.");
        }
    }

    private static string RequiredString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidOperationException($"Steward capabilities response omitted '{propertyName}'.");
}

public sealed class WorldAutonomyStewardCatalog
{
    private readonly ImmutableArray<McpClientTool> _nativeTools;

    internal WorldAutonomyStewardCatalog(
        StewardCapabilitiesSnapshot capabilities,
        IEnumerable<McpClientTool> nativeTools)
    {
        Capabilities = capabilities;
        _nativeTools = nativeTools
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        Capabilities.ValidateRegisteredTools(_nativeTools.Select(tool => tool.Name));
    }

    public StewardCapabilitiesSnapshot Capabilities { get; }

    public ImmutableArray<string> ToolNames => _nativeTools.Select(tool => tool.Name).ToImmutableArray();

    public McpClientTool GetNativeTool(string name) => _nativeTools.SingleOrDefault(tool =>
        string.Equals(tool.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Steward MCP child did not expose native tool '{name}'.");

    public WorldAutonomyBoundCatalog Bind(WorldAutonomyRunContext run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.GuildId != Capabilities.GuildId)
        {
            throw new InvalidOperationException("Autonomy run guild does not match its Steward child process.");
        }

        var metadata = run.CreateMcpMetadata();
        var tools = _nativeTools
            .Select(tool => CreateDescriptor(tool.WithMeta((JsonObject)metadata.DeepClone())))
            .ToImmutableArray();
        var search = new HostedToolSearchTool
        {
            DeferredTools = tools.Select(tool => tool.Function.Name).ToList(),
            Namespace = "discord_steward",
            NamespaceDescription = "Complete native Discord Steward catalog for the bound Discord guild."
        };
        return new WorldAutonomyBoundCatalog(
            tools,
            [search],
            ComputeManifestDigest(tools));
    }

    private static WorldAutonomyToolDescriptor CreateDescriptor(McpClientTool tool)
    {
        var inputSchema = tool.ProtocolTool.InputSchema;
        var requiresRequestId = inputSchema.ValueKind == JsonValueKind.Object &&
            inputSchema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty("request_id", out _);
        var schemaJson = WorldAutonomyCanonicalizer.SerializeJson(inputSchema);
        var schemaDigest = WorldAutonomyCanonicalizer.ComputeDigest(
            string.Concat(tool.Name, "\n", tool.Description, "\n", schemaJson));
        return new WorldAutonomyToolDescriptor(
            tool,
            IsWrite: tool.ProtocolTool.Annotations?.ReadOnlyHint != true,
            RequiresRequestId: requiresRequestId,
            SchemaDigest: schemaDigest);
    }

    private static string ComputeManifestDigest(IEnumerable<WorldAutonomyToolDescriptor> tools) =>
        WorldAutonomyCanonicalizer.ComputeDigest(string.Join(
            "\n",
            tools.OrderBy(tool => tool.Function.Name, StringComparer.Ordinal)
                .Select(tool => $"{tool.Function.Name}:{tool.SchemaDigest}")));
}

public sealed record WorldAutonomyBoundCatalog(
    ImmutableArray<WorldAutonomyToolDescriptor> Tools,
    ImmutableArray<AITool> SupplementaryTools,
    string ManifestDigest);

public sealed class WorldAutonomyStewardSession : IAsyncDisposable
{
    private readonly McpClient _client;

    internal WorldAutonomyStewardSession(
        WorldAutonomyGuildBinding binding,
        McpClient client,
        WorldAutonomyStewardCatalog catalog)
    {
        Binding = binding;
        _client = client;
        Catalog = catalog;
    }

    public WorldAutonomyGuildBinding Binding { get; }

    public WorldAutonomyStewardCatalog Catalog { get; }

    internal async Task ValidateHealthAsync(CancellationToken cancellationToken)
    {
        var result = await Catalog.GetNativeTool("get_steward_capabilities")
            .CallAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var current = StewardCapabilitiesSnapshot.Parse(result, Binding.GuildId);
        current.ValidateRegisteredTools(Catalog.ToolNames);
        if (!string.Equals(current.ProfileDigest, Catalog.Capabilities.ProfileDigest, StringComparison.Ordinal) ||
            !string.Equals(current.ManifestDigest, Catalog.Capabilities.ManifestDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Steward child capability identity changed after its session was established.");
        }
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync().ConfigureAwait(false);
}

public sealed record WorldAutonomyStewardHealthSnapshot(
    int ConfiguredGuilds,
    int HealthyGuilds,
    IReadOnlyDictionary<string, string> Guilds)
{
    public bool IsHealthy => ConfiguredGuilds == HealthyGuilds;
}

public sealed class StewardMcpSupervisor : IAsyncDisposable
{
    private readonly WorldAutonomyConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StewardMcpSupervisor> _logger;
    private readonly ConcurrentDictionary<ulong, Lazy<Task<WorldAutonomyStewardSession>>> _sessions = new();
    private readonly ConcurrentDictionary<ulong, string> _health = new();

    public StewardMcpSupervisor(
        WorldAutonomyConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<StewardMcpSupervisor> logger)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<WorldAutonomyStewardSession> GetSessionAsync(ulong guildId, CancellationToken cancellationToken)
    {
        if (!_configuration.TryGetBinding(guildId, out var binding))
        {
            throw new InvalidOperationException(
                $"Discord guild '{guildId.ToString(CultureInfo.InvariantCulture)}' has no autonomy Steward binding.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var lazy = _sessions.GetOrAdd(
                guildId,
                _ => new Lazy<Task<WorldAutonomyStewardSession>>(
                    () => StartSessionAsync(binding),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var session = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                await session.ValidateHealthAsync(cancellationToken).ConfigureAwait(false);
                _health[guildId] = "healthy";
                return session;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _health[guildId] = $"faulted:{exception.GetType().Name}";
                await RemoveSessionAsync(guildId, lazy).ConfigureAwait(false);
                if (attempt == 1)
                {
                    throw;
                }

                _logger.LogWarning(
                    exception,
                    "Steward child for guild {GuildId} failed its health check; recreating it once.",
                    guildId);
            }
        }

        throw new InvalidOperationException("Steward session retry loop ended unexpectedly.");
    }

    public WorldAutonomyStewardHealthSnapshot GetHealthSnapshot()
    {
        var guilds = _configuration.EnabledGuilds.Keys
            .Order()
            .ToDictionary(
                guildId => guildId.ToString(CultureInfo.InvariantCulture),
                guildId => _health.GetValueOrDefault(guildId, "not_started"),
                StringComparer.Ordinal);
        return new WorldAutonomyStewardHealthSnapshot(
            guilds.Count,
            guilds.Values.Count(status => string.Equals(status, "healthy", StringComparison.Ordinal)),
            guilds);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            try
            {
                var session = await lazy.Value.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to stop a Steward MCP child process.");
            }
        }

        _sessions.Clear();
        _health.Clear();
    }

    private async Task RemoveSessionAsync(
        ulong guildId,
        Lazy<Task<WorldAutonomyStewardSession>> expected)
    {
        if (!_sessions.TryGetValue(guildId, out var current) || !ReferenceEquals(current, expected) ||
            !_sessions.TryRemove(guildId, out var removed) || !removed.IsValueCreated)
        {
            return;
        }

        try
        {
            var session = await removed.Value.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to dispose unhealthy Steward child for guild {GuildId}.", guildId);
        }
    }

    private async Task<WorldAutonomyStewardSession> StartSessionAsync(WorldAutonomyGuildBinding binding)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = $"discord-steward-{binding.GuildId.ToString(CultureInfo.InvariantCulture)}",
            Command = _configuration.StewardCommand,
            Arguments = _configuration.StewardArguments.ToList(),
            WorkingDirectory = _configuration.StewardWorkingDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["Steward__ProfilePath"] = binding.ProfilePath
            },
            StandardErrorLines = line => _logger.LogDebug(
                "Steward child guild={GuildId}: {Line}",
                binding.GuildId,
                line)
        }, _loggerFactory);
        var client = await McpClient.CreateAsync(
            transport,
            loggerFactory: _loggerFactory,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var capabilitiesTool = tools.SingleOrDefault(tool => tool.Name == "get_steward_capabilities")
                ?? throw new InvalidOperationException("Steward MCP child did not expose get_steward_capabilities.");
            var capabilitiesResult = await capabilitiesTool.CallAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var capabilities = StewardCapabilitiesSnapshot.Parse(capabilitiesResult, binding.GuildId);
            var catalog = new WorldAutonomyStewardCatalog(capabilities, tools);
            _logger.LogInformation(
                "Started unrestricted Steward MCP child for guild {GuildId} with {ToolCount} native tools.",
                binding.GuildId,
                catalog.ToolNames.Length);
            return new WorldAutonomyStewardSession(binding, client, catalog);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

}