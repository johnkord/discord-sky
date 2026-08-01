using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Reactions;

public sealed record ReactionCapabilityState(
    ulong GuildId,
    ulong UserId,
    int DiscordCode,
    DateTimeOffset BlockedAt,
    DateTimeOffset LastFailureAt,
    DateTimeOffset ExpiresAt,
    int FailureCount);

public interface IReactionCapabilityRegistry
{
    bool TryGetActive(ulong guildId, ulong userId, out ReactionCapabilityState state);
    ReactionCapabilityState? RecordExactBlock(ulong guildId, ulong userId, int discordCode);
    bool Clear(ulong guildId, ulong userId);
}

public sealed class FileBackedReactionCapabilityRegistry : IReactionCapabilityRegistry, IHostedService
{
    private const int CurrentVersion = 1;
    private const int MaxEntries = 1_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly ReactionOptions _options;
    private readonly ILogger<FileBackedReactionCapabilityRegistry> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, string, string> _writeSnapshot;
    private readonly Dictionary<(ulong GuildId, ulong UserId), ReactionCapabilityState> _entries = new();
    private readonly object _lock = new();

    public FileBackedReactionCapabilityRegistry(
        IOptions<ReactionOptions> options,
        ILogger<FileBackedReactionCapabilityRegistry> logger)
        : this(options.Value, logger, () => DateTimeOffset.UtcNow, WriteSnapshotAtomically)
    {
    }

    internal FileBackedReactionCapabilityRegistry(
        ReactionOptions options,
        ILogger<FileBackedReactionCapabilityRegistry> logger,
        Func<DateTimeOffset> clock,
        Action<string, string, string> writeSnapshot)
    {
        _options = options;
        _logger = logger;
        _clock = clock;
        _writeSnapshot = writeSnapshot;
    }

    internal int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CapabilityCooldownEnabled)
        {
            _logger.LogInformation("Reaction capability cooldown disabled.");
            return Task.CompletedTask;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_options.CapabilityStorePath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(_options.CapabilityStorePath))
            {
                _logger.LogInformation(
                    "Reaction capability cooldown enabled: path={Path} ttl={Hours}h active=0.",
                    _options.CapabilityStorePath,
                    Math.Clamp(_options.BlockedUserCooldownHours, 1, 24 * 30));
                return Task.CompletedTask;
            }

            var json = File.ReadAllText(_options.CapabilityStorePath);
            var snapshot = JsonSerializer.Deserialize<ReactionCapabilitySnapshot>(json, JsonOptions);
            lock (_lock)
            {
                _entries.Clear();
                foreach (var state in snapshot?.Entries ?? Array.Empty<ReactionCapabilityState>())
                {
                    if (state.DiscordCode != ReactionDeliveryFailureClassifier.ReactionBlockedCode) continue;
                    _entries[(state.GuildId, state.UserId)] = state;
                }
                var changed = PruneAndBoundLocked(_clock());
                if (changed) PersistLocked();
            }
            _logger.LogInformation(
                "Reaction capability cooldown enabled: path={Path} ttl={Hours}h active={Count}.",
                _options.CapabilityStorePath,
                Math.Clamp(_options.BlockedUserCooldownHours, 1, 24 * 30),
                Count);
        }
        catch (Exception ex)
        {
            lock (_lock) _entries.Clear();
            _logger.LogWarning(ex, "Reaction capability registry load failed; starting empty.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public bool TryGetActive(ulong guildId, ulong userId, out ReactionCapabilityState state)
    {
        state = null!;
        if (!_options.CapabilityCooldownEnabled) return false;

        lock (_lock)
        {
            if (!_entries.TryGetValue((guildId, userId), out var found)) return false;
            if (found.ExpiresAt <= _clock())
            {
                _entries.Remove((guildId, userId));
                PersistLocked();
                return false;
            }
            state = found;
            return true;
        }
    }

    public ReactionCapabilityState? RecordExactBlock(ulong guildId, ulong userId, int discordCode)
    {
        if (!_options.CapabilityCooldownEnabled
            || discordCode != ReactionDeliveryFailureClassifier.ReactionBlockedCode)
        {
            return null;
        }

        lock (_lock)
        {
            var now = _clock();
            var key = (guildId, userId);
            var count = _entries.TryGetValue(key, out var existing)
                ? existing.FailureCount + 1
                : 1;
            var state = new ReactionCapabilityState(
                guildId,
                userId,
                discordCode,
                existing?.BlockedAt ?? now,
                now,
                now.AddHours(Math.Clamp(_options.BlockedUserCooldownHours, 1, 24 * 30)),
                count);
            _entries[key] = state;
            PruneAndBoundLocked(now);
            PersistLocked();
            return _entries.TryGetValue(key, out var persisted) ? persisted : state;
        }
    }

    public bool Clear(ulong guildId, ulong userId)
    {
        if (!_options.CapabilityCooldownEnabled) return false;
        lock (_lock)
        {
            if (!_entries.Remove((guildId, userId))) return false;
            PersistLocked();
            return true;
        }
    }

    private bool PruneAndBoundLocked(DateTimeOffset now)
    {
        var changed = false;
        foreach (var key in _entries
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            changed |= _entries.Remove(key);
        }
        if (_entries.Count <= MaxEntries) return changed;
        foreach (var key in _entries
                     .OrderBy(pair => pair.Value.LastFailureAt)
                     .ThenBy(pair => pair.Key.GuildId)
                     .ThenBy(pair => pair.Key.UserId)
                     .Take(_entries.Count - MaxEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            changed |= _entries.Remove(key);
        }
        return changed;
    }

    private void PersistLocked()
    {
        try
        {
            var snapshot = new ReactionCapabilitySnapshot(
                CurrentVersion,
                _entries.Values
                    .OrderBy(state => state.GuildId)
                    .ThenBy(state => state.UserId)
                    .ToArray());
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var path = Path.GetFullPath(_options.CapabilityStorePath);
            _writeSnapshot(path + ".tmp", path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reaction capability registry write failed; continuing in memory.");
        }
    }

    private static void WriteSnapshotAtomically(string tempPath, string path, string json)
    {
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private sealed record ReactionCapabilitySnapshot(
        int Version,
        IReadOnlyList<ReactionCapabilityState> Entries);
}