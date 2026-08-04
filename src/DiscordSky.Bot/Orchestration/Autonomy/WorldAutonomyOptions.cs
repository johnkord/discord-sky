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

    public bool TerminalDeliveryEnabled { get; init; }

    public WorldAutonomyPromptCacheMode PromptCacheMode { get; init; } = WorldAutonomyPromptCacheMode.Off;

    public string LedgerPath { get; init; } = "data/world-autonomy/world-autonomy.json";

    public WorldAutonomyAmbientGateMode AmbientGateMode { get; init; } = WorldAutonomyAmbientGateMode.Off;

    public double AmbientFullThreshold { get; init; } = 0.65;

    public double AmbientReactionThreshold { get; init; } = 0.35;

    public double AmbientActionThreshold { get; init; } = 0.60;

    public double AmbientJudgeConfidenceFloor { get; init; } = 0.35;

    public bool AmbientPostSpeechHoldEnabled { get; init; }

    public bool AmbientLowValueHoldEnabled { get; init; }

    public double AmbientLowValueFloor { get; init; } = 0.15;

    public int AmbientCanaryExplorationPercent { get; init; } = 10;

    public int AmbientLiveExplorationPercent { get; init; } = 5;

    public bool AmbientEpisodeCoalescingEnabled { get; init; }

    public int AmbientEpisodeWindowMilliseconds { get; init; } = 1500;

    public bool AmbientPostSpeechGuardEnabled { get; init; }

    public int AmbientPostSpeechHumanTurns { get; init; } = 2;

    public int AmbientPostSpeechWindowMinutes { get; init; } = 10;

    public WorldAutonomyBudgetOptions Budget { get; init; } = new();

    public Dictionary<string, WorldAutonomyGuildOptions> EnabledGuilds { get; init; } = new(StringComparer.Ordinal);
}

public enum WorldAutonomyAmbientGateMode
{
    Off,
    Shadow,
    Canary,
    Live,
}

public enum WorldAutonomyPromptCacheMode
{
    Off,
    Explicit,
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
        bool terminalDeliveryEnabled,
        WorldAutonomyPromptCacheMode promptCacheMode,
        WorldAutonomyAmbientGateMode ambientGateMode,
        double ambientFullThreshold,
        double ambientReactionThreshold,
        double ambientActionThreshold,
        double ambientJudgeConfidenceFloor,
        bool ambientPostSpeechHoldEnabled,
        bool ambientLowValueHoldEnabled,
        double ambientLowValueFloor,
        int ambientCanaryExplorationPercent,
        int ambientLiveExplorationPercent,
        bool ambientEpisodeCoalescingEnabled,
        TimeSpan ambientEpisodeWindow,
        bool ambientPostSpeechGuardEnabled,
        int ambientPostSpeechHumanTurns,
        TimeSpan ambientPostSpeechWindow,
        WorldAutonomyBudgetOptions budget,
        ImmutableDictionary<ulong, WorldAutonomyGuildBinding> enabledGuilds)
    {
        StewardCommand = stewardCommand;
        StewardArguments = stewardArguments;
        StewardWorkingDirectory = stewardWorkingDirectory;
        SessionTimeout = sessionTimeout;
        RequestIdPoolSize = requestIdPoolSize;
        ValidateStewardOnStartup = validateStewardOnStartup;
        TerminalDeliveryEnabled = terminalDeliveryEnabled;
        PromptCacheMode = promptCacheMode;
        AmbientGateMode = ambientGateMode;
        AmbientFullThreshold = ambientFullThreshold;
        AmbientReactionThreshold = ambientReactionThreshold;
        AmbientActionThreshold = ambientActionThreshold;
        AmbientJudgeConfidenceFloor = ambientJudgeConfidenceFloor;
        AmbientPostSpeechHoldEnabled = ambientPostSpeechHoldEnabled;
        AmbientLowValueHoldEnabled = ambientLowValueHoldEnabled;
        AmbientLowValueFloor = ambientLowValueFloor;
        AmbientCanaryExplorationPercent = ambientCanaryExplorationPercent;
        AmbientLiveExplorationPercent = ambientLiveExplorationPercent;
        AmbientEpisodeCoalescingEnabled = ambientEpisodeCoalescingEnabled;
        AmbientEpisodeWindow = ambientEpisodeWindow;
        AmbientPostSpeechGuardEnabled = ambientPostSpeechGuardEnabled;
        AmbientPostSpeechHumanTurns = ambientPostSpeechHumanTurns;
        AmbientPostSpeechWindow = ambientPostSpeechWindow;
        Budget = budget;
        EnabledGuilds = enabledGuilds;
    }

    public string StewardCommand { get; }

    public ImmutableArray<string> StewardArguments { get; }

    public string? StewardWorkingDirectory { get; }

    public TimeSpan SessionTimeout { get; }

    public int RequestIdPoolSize { get; }

    public bool ValidateStewardOnStartup { get; }

    public bool TerminalDeliveryEnabled { get; }

    public WorldAutonomyPromptCacheMode PromptCacheMode { get; }

    public WorldAutonomyAmbientGateMode AmbientGateMode { get; }

    public double AmbientFullThreshold { get; }

    public double AmbientReactionThreshold { get; }

    public double AmbientActionThreshold { get; }

    public double AmbientJudgeConfidenceFloor { get; }

    public bool AmbientPostSpeechHoldEnabled { get; }

    public bool AmbientLowValueHoldEnabled { get; }

    public double AmbientLowValueFloor { get; }

    public int AmbientCanaryExplorationPercent { get; }

    public int AmbientLiveExplorationPercent { get; }

    public bool AmbientEpisodeCoalescingEnabled { get; }

    public TimeSpan AmbientEpisodeWindow { get; }

    public bool AmbientPostSpeechGuardEnabled { get; }

    public int AmbientPostSpeechHumanTurns { get; }

    public TimeSpan AmbientPostSpeechWindow { get; }

    public WorldAutonomyBudgetOptions Budget { get; }

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

        if (options.AmbientFullThreshold is < 0.0 or > 1.0)
        {
            throw new InvalidOperationException("WorldAutonomy:AmbientFullThreshold must be between 0 and 1.");
        }

        if (options.AmbientReactionThreshold is < 0.0 or > 1.0
            || options.AmbientReactionThreshold > options.AmbientFullThreshold)
        {
            throw new InvalidOperationException(
                "WorldAutonomy:AmbientReactionThreshold must be between 0 and AmbientFullThreshold.");
        }

        if (options.AmbientActionThreshold is < 0.0 or > 1.0)
        {
            throw new InvalidOperationException("WorldAutonomy:AmbientActionThreshold must be between 0 and 1.");
        }

        if (options.AmbientJudgeConfidenceFloor is < 0.0 or > 1.0)
        {
            throw new InvalidOperationException("WorldAutonomy:AmbientJudgeConfidenceFloor must be between 0 and 1.");
        }

        if (options.AmbientLowValueFloor is < 0.0 or > 1.0)
        {
            throw new InvalidOperationException("WorldAutonomy:AmbientLowValueFloor must be between 0 and 1.");
        }

        if (options.AmbientCanaryExplorationPercent is < 0 or > 100)
        {
            throw new InvalidOperationException(
                "WorldAutonomy:AmbientCanaryExplorationPercent must be between 0 and 100.");
        }

        if (options.AmbientLiveExplorationPercent is < 0 or > 100)
        {
            throw new InvalidOperationException(
                "WorldAutonomy:AmbientLiveExplorationPercent must be between 0 and 100.");
        }

        if (options.AmbientEpisodeWindowMilliseconds is < 0 or > 10000)
        {
            throw new InvalidOperationException(
                "WorldAutonomy:AmbientEpisodeWindowMilliseconds must be between 0 and 10000.");
        }

        if (options.AmbientPostSpeechHumanTurns is < 1 or > 10)
        {
            throw new InvalidOperationException(
                "WorldAutonomy:AmbientPostSpeechHumanTurns must be between 1 and 10.");
        }

        if (options.AmbientPostSpeechWindowMinutes is < 1 or > 60)
        {
            throw new InvalidOperationException(
                "WorldAutonomy:AmbientPostSpeechWindowMinutes must be between 1 and 60.");
        }

        ValidateBudget(options.Budget);

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
            options.TerminalDeliveryEnabled,
            options.PromptCacheMode,
            options.AmbientGateMode,
            options.AmbientFullThreshold,
            options.AmbientReactionThreshold,
            options.AmbientActionThreshold,
            options.AmbientJudgeConfidenceFloor,
            options.AmbientPostSpeechHoldEnabled,
            options.AmbientLowValueHoldEnabled,
            options.AmbientLowValueFloor,
            options.AmbientCanaryExplorationPercent,
            options.AmbientLiveExplorationPercent,
            options.AmbientEpisodeCoalescingEnabled,
            TimeSpan.FromMilliseconds(options.AmbientEpisodeWindowMilliseconds),
            options.AmbientPostSpeechGuardEnabled,
            options.AmbientPostSpeechHumanTurns,
            TimeSpan.FromMinutes(options.AmbientPostSpeechWindowMinutes),
            options.Budget,
            bindings.ToImmutable());
    }

    private static void ValidateBudget(WorldAutonomyBudgetOptions budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (string.IsNullOrWhiteSpace(budget.StatePath))
        {
            throw new InvalidOperationException("WorldAutonomy:Budget:StatePath must be non-empty.");
        }

        foreach (var (name, value) in new Dictionary<string, int>
        {
            [nameof(budget.AmbientFullPerHour)] = budget.AmbientFullPerHour,
            [nameof(budget.AmbientFullPerDay)] = budget.AmbientFullPerDay,
            [nameof(budget.AmbientConversationPerHour)] = budget.AmbientConversationPerHour,
            [nameof(budget.AmbientConversationPerDay)] = budget.AmbientConversationPerDay,
            [nameof(budget.DirectFullPerHour)] = budget.DirectFullPerHour,
            [nameof(budget.DirectFullPerDay)] = budget.DirectFullPerDay,
            [nameof(budget.DirectConversationPerHour)] = budget.DirectConversationPerHour,
            [nameof(budget.DirectConversationPerDay)] = budget.DirectConversationPerDay,
        })
        {
            if (value is < 0 or > 10_000)
            {
                throw new InvalidOperationException(
                    $"WorldAutonomy:Budget:{name} must be between 0 and 10000.");
            }
        }
    }
}

public sealed class WorldAutonomyBudgetOptions
{
    public bool Enabled { get; init; } = true;

    public string StatePath { get; init; } = "data/world-autonomy/admission-budget.json";

    public int AmbientFullPerHour { get; init; } = 4;

    public int AmbientFullPerDay { get; init; } = 16;

    public int AmbientConversationPerHour { get; init; } = 12;

    public int AmbientConversationPerDay { get; init; } = 60;

    public int DirectFullPerHour { get; init; } = 8;

    public int DirectFullPerDay { get; init; } = 40;

    public int DirectConversationPerHour { get; init; } = 16;

    public int DirectConversationPerDay { get; init; } = 80;
}