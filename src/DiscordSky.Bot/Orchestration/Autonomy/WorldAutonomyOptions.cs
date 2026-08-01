using System.Collections.Immutable;
using System.Globalization;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed class WorldAutonomyOptions
{
    public const string SectionName = "WorldAutonomy";

    public string StewardCommand { get; init; } = "dotnet";

    public IReadOnlyList<string> StewardArguments { get; init; } = [];

    public string? StewardWorkingDirectory { get; init; }

    public int SessionTimeoutMinutes { get; init; } = 20;

    public int RequestIdPoolSize { get; init; } = 40;

    public bool ValidateStewardOnStartup { get; init; }

    public string LedgerPath { get; init; } = "data/world-autonomy/world-autonomy.json";

    public Dictionary<string, WorldAutonomyGuildOptions> EnabledGuilds { get; init; } = new(StringComparer.Ordinal);
}

public sealed class WorldAutonomyGuildOptions
{
    public string ProfilePath { get; init; } = string.Empty;

    public string? Model { get; init; }
}

public sealed record WorldAutonomyGuildBinding(
    ulong GuildId,
    string ProfilePath,
    string? Model);

public sealed class WorldAutonomyConfiguration
{
    private WorldAutonomyConfiguration(
        string stewardCommand,
        ImmutableArray<string> stewardArguments,
        string? stewardWorkingDirectory,
        TimeSpan sessionTimeout,
        int requestIdPoolSize,
        bool validateStewardOnStartup,
        ImmutableDictionary<ulong, WorldAutonomyGuildBinding> enabledGuilds)
    {
        StewardCommand = stewardCommand;
        StewardArguments = stewardArguments;
        StewardWorkingDirectory = stewardWorkingDirectory;
        SessionTimeout = sessionTimeout;
        RequestIdPoolSize = requestIdPoolSize;
        ValidateStewardOnStartup = validateStewardOnStartup;
        EnabledGuilds = enabledGuilds;
    }

    public string StewardCommand { get; }

    public ImmutableArray<string> StewardArguments { get; }

    public string? StewardWorkingDirectory { get; }

    public TimeSpan SessionTimeout { get; }

    public int RequestIdPoolSize { get; }

    public bool ValidateStewardOnStartup { get; }

    public ImmutableDictionary<ulong, WorldAutonomyGuildBinding> EnabledGuilds { get; }

    public bool IsEnabled => !EnabledGuilds.IsEmpty;

    public bool TryGetBinding(ulong guildId, out WorldAutonomyGuildBinding binding) =>
        EnabledGuilds.TryGetValue(guildId, out binding!);

    public static WorldAutonomyConfiguration FromOptions(WorldAutonomyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.SessionTimeoutMinutes is < 1 or > 60)
        {
            throw new InvalidOperationException("WorldAutonomy:SessionTimeoutMinutes must be between 1 and 60.");
        }

        if (options.RequestIdPoolSize is < 1 or > 40)
        {
            throw new InvalidOperationException("WorldAutonomy:RequestIdPoolSize must be between 1 and 40.");
        }

        if (string.IsNullOrWhiteSpace(options.LedgerPath))
        {
            throw new InvalidOperationException("WorldAutonomy:LedgerPath must be non-empty.");
        }

        var bindings = ImmutableDictionary.CreateBuilder<ulong, WorldAutonomyGuildBinding>();
        foreach (var (configuredGuildId, configuredBinding) in options.EnabledGuilds ?? [])
        {
            if (!ulong.TryParse(configuredGuildId, NumberStyles.None, CultureInfo.InvariantCulture, out var guildId) || guildId == 0)
            {
                throw new InvalidOperationException(
                    $"WorldAutonomy:EnabledGuilds key '{configuredGuildId}' must be an exact non-zero Discord guild ID.");
            }

            if (configuredBinding is null || string.IsNullOrWhiteSpace(configuredBinding.ProfilePath))
            {
                throw new InvalidOperationException(
                    $"WorldAutonomy binding '{configuredGuildId}' requires a non-empty ProfilePath.");
            }

            var binding = new WorldAutonomyGuildBinding(
                guildId,
                configuredBinding.ProfilePath.Trim(),
                string.IsNullOrWhiteSpace(configuredBinding.Model) ? null : configuredBinding.Model.Trim());
            if (!bindings.TryAdd(guildId, binding))
            {
                throw new InvalidOperationException(
                    $"WorldAutonomy contains duplicate numeric guild binding '{guildId.ToString(CultureInfo.InvariantCulture)}'.");
            }
        }

        if (bindings.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(options.StewardCommand))
            {
                throw new InvalidOperationException("WorldAutonomy requires a non-empty StewardCommand when guilds are enabled.");
            }

            if (options.StewardArguments is not null &&
                options.StewardArguments.Any(argument => string.IsNullOrWhiteSpace(argument)))
            {
                throw new InvalidOperationException(
                    "WorldAutonomy StewardArguments cannot contain empty values.");
            }
        }

        return new WorldAutonomyConfiguration(
            options.StewardCommand?.Trim() ?? string.Empty,
            options.StewardArguments?.Select(argument => argument.Trim()).ToImmutableArray() ?? [],
            string.IsNullOrWhiteSpace(options.StewardWorkingDirectory) ? null : options.StewardWorkingDirectory.Trim(),
            TimeSpan.FromMinutes(options.SessionTimeoutMinutes),
            options.RequestIdPoolSize,
            options.ValidateStewardOnStartup,
            bindings.ToImmutable());
    }
}