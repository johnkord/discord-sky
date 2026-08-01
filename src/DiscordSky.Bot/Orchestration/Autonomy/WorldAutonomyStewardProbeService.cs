using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed class WorldAutonomyStewardProbeService : IHostedService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private readonly WorldAutonomyConfiguration _configuration;
    private readonly ILogger<WorldAutonomyStewardProbeService> _logger;

    public WorldAutonomyStewardProbeService(
        WorldAutonomyConfiguration configuration,
        ILogger<WorldAutonomyStewardProbeService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.ValidateStewardOnStartup)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_configuration.StewardCommand))
        {
            throw new InvalidOperationException("World autonomy startup validation requires StewardCommand.");
        }

        var bindings = _configuration.EnabledGuilds.Values
            .OrderBy(binding => binding.GuildId)
            .ToArray();
        if (bindings.Length == 0)
        {
            await ProbeAsync(binding: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var binding in bindings)
        {
            await ProbeAsync(binding, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ProbeAsync(
        WorldAutonomyGuildBinding? binding,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_configuration.StewardCommand)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _configuration.StewardWorkingDirectory
            }
        };
        foreach (var argument in _configuration.StewardArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (binding is not null)
        {
            process.StartInfo.Environment["Steward__ProfilePath"] = binding.ProfilePath;
        }

        process.StartInfo.ArgumentList.Add("--probe");
        if (!process.Start())
        {
            throw new InvalidOperationException("World autonomy could not start the Steward probe process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "Steward startup probe failed with exit code {ExitCode}; stderr length {StderrLength}.",
                process.ExitCode,
                stderr.Length);
            throw new InvalidOperationException("World autonomy Steward startup probe failed.");
        }

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "ready", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("World autonomy Steward startup probe returned an invalid result.");
        }

        var mode = RequiredString(root, "mode");
        var authorizationMode = RequiredString(root, "authorizationMode");
        var registeredToolCount = root.GetProperty("registeredToolCount").GetInt32();
        if (registeredToolCount <= 0)
        {
            throw new InvalidOperationException("World autonomy Steward startup probe reported no registered tools.");
        }

        if (binding is null)
        {
            _logger.LogInformation(
                "Steward startup probe succeeded: mode={Mode}, authorization={AuthorizationMode}, registered tools={ToolCount}.",
                mode,
                authorizationMode,
                registeredToolCount);
            return;
        }

        var expectedGuildId = binding.GuildId.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(RequiredString(root, "guildId"), expectedGuildId, StringComparison.Ordinal) ||
            !string.Equals(mode, "unrestricted", StringComparison.Ordinal) ||
            !string.Equals(authorizationMode, "UnrestrictedAutonomy", StringComparison.Ordinal) ||
            root.GetProperty("protectedResourceCount").GetInt32() != 0 ||
            root.GetProperty("protectedNamePrefixCount").GetInt32() != 0 ||
            root.GetProperty("deniedPermissionCount").GetInt32() != 0)
        {
            throw new InvalidOperationException(
                $"World autonomy Steward probe rejected guild binding '{expectedGuildId}': expected unrestricted mode with no local policy protections.");
        }

        _logger.LogInformation(
            "Steward startup probe succeeded for autonomy guild {GuildId}: registered tools={ToolCount}.",
            binding.GuildId,
            registeredToolCount);
    }

    private static string RequiredString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidOperationException($"World autonomy Steward startup probe omitted '{propertyName}'.");
}