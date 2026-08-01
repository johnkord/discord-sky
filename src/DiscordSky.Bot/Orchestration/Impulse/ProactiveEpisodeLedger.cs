using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Impulse;

public interface IProactiveEpisodeLedger
{
    IReadOnlyList<ColdOpenEpisodeSnapshot> GetRecent(ulong channelId);
    void Record(ColdOpenEpisodeSnapshot snapshot);
}

public sealed class FileBackedProactiveEpisodeLedger : IProactiveEpisodeLedger, IHostedService
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Configuration.ColdOpenOptions _options;
    private readonly ILogger<FileBackedProactiveEpisodeLedger> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, string, string> _writeSnapshot;
    private readonly List<ColdOpenEpisodeSnapshot> _entries = new();
    private readonly object _lock = new();

    public FileBackedProactiveEpisodeLedger(
        IOptions<Configuration.ColdOpenOptions> options,
        ILogger<FileBackedProactiveEpisodeLedger> logger)
        : this(options.Value, logger, () => DateTimeOffset.UtcNow, WriteSnapshotAtomically)
    {
    }

    internal FileBackedProactiveEpisodeLedger(
        Configuration.ColdOpenOptions options,
        ILogger<FileBackedProactiveEpisodeLedger> logger,
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
        if (_options.EpisodeNoveltyMode == Configuration.ColdOpenEpisodeNoveltyMode.Off)
        {
            _logger.LogInformation("Cold-open episode novelty ledger disabled.");
            return Task.CompletedTask;
        }

        try
        {
            var fullPath = Path.GetFullPath(_options.NoveltyLedgerPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(fullPath))
            {
                _logger.LogInformation(
                    "Cold-open novelty ledger enabled: mode={Mode} path={Path} active=0.",
                    _options.EpisodeNoveltyMode,
                    fullPath);
                return Task.CompletedTask;
            }
            var snapshot = JsonSerializer.Deserialize<LedgerSnapshot>(File.ReadAllText(fullPath), JsonOptions);
            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(snapshot?.Entries ?? Array.Empty<ColdOpenEpisodeSnapshot>());
                var changed = PruneAndBoundLocked();
                if (changed) PersistLocked();
            }
            _logger.LogInformation(
                "Cold-open novelty ledger enabled: mode={Mode} path={Path} active={Count}.",
                _options.EpisodeNoveltyMode,
                fullPath,
                Count);
        }
        catch (Exception ex)
        {
            lock (_lock) _entries.Clear();
            _logger.LogWarning(ex, "Cold-open novelty ledger load failed; starting empty.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<ColdOpenEpisodeSnapshot> GetRecent(ulong channelId)
    {
        if (_options.EpisodeNoveltyMode == Configuration.ColdOpenEpisodeNoveltyMode.Off)
        {
            return Array.Empty<ColdOpenEpisodeSnapshot>();
        }
        lock (_lock)
        {
            var changed = PruneAndBoundLocked();
            if (changed) PersistLocked();
            return _entries
                .Where(entry => entry.ChannelId == channelId)
                .OrderByDescending(entry => entry.FiredAt)
                .ToArray();
        }
    }

    public void Record(ColdOpenEpisodeSnapshot snapshot)
    {
        if (_options.EpisodeNoveltyMode == Configuration.ColdOpenEpisodeNoveltyMode.Off) return;
        lock (_lock)
        {
            _entries.RemoveAll(entry => entry.EpisodeId.Equals(snapshot.EpisodeId, StringComparison.Ordinal));
            _entries.Add(Freeze(snapshot));
            PruneAndBoundLocked();
            PersistLocked();
        }
    }

    private bool PruneAndBoundLocked()
    {
        var cutoff = _clock().AddHours(-Math.Clamp(_options.NoveltyRetentionHours, 1, 24 * 30));
        var changed = _entries.RemoveAll(entry => entry.FiredAt < cutoff) > 0;
        var max = Math.Clamp(_options.NoveltyLedgerMaxEntries, 1, 1_000);
        if (_entries.Count <= max) return changed;
        var remove = _entries
            .OrderBy(entry => entry.FiredAt)
            .ThenBy(entry => entry.EpisodeId, StringComparer.Ordinal)
            .Take(_entries.Count - max)
            .Select(entry => entry.EpisodeId)
            .ToHashSet(StringComparer.Ordinal);
        changed |= _entries.RemoveAll(entry => remove.Contains(entry.EpisodeId)) > 0;
        return changed;
    }

    private void PersistLocked()
    {
        try
        {
            var snapshot = new LedgerSnapshot(
                CurrentVersion,
                _entries.OrderBy(entry => entry.FiredAt).ThenBy(entry => entry.EpisodeId, StringComparer.Ordinal).ToArray());
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var path = Path.GetFullPath(_options.NoveltyLedgerPath);
            _writeSnapshot(path + ".tmp", path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cold-open novelty ledger write failed; continuing in memory.");
        }
    }

    private static ColdOpenEpisodeSnapshot Freeze(ColdOpenEpisodeSnapshot snapshot) => snapshot with
    {
        SourceMessageIds = snapshot.SourceMessageIds.Distinct().OrderBy(id => id).ToArray(),
        ReferencedMessageIds = snapshot.ReferencedMessageIds.Distinct().OrderBy(id => id).ToArray(),
        ResourceIds = snapshot.ResourceIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
        TopicAnchors = snapshot.TopicAnchors.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
    };

    private static void WriteSnapshotAtomically(string tempPath, string path, string json)
    {
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private sealed record LedgerSnapshot(int Version, IReadOnlyList<ColdOpenEpisodeSnapshot> Entries);
}