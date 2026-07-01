using System.Text.Json;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Durable, moderator-taught scam signals (extra phrases and hosts) that the detector consults alongside its
/// built-in lists. This is the pragmatic, local version of the "close the loop" recommendation: when the guard
/// misses something, a moderator reports it and it is remembered on the PVC so the miss is not repeated.
/// </summary>
public sealed class LearnedScamStore
{
    private readonly string _path;
    private readonly ILogger<LearnedScamStore> _logger;
    private readonly object _lock = new();
    private volatile Snapshot _snapshot = new(Array.Empty<string>(), Array.Empty<string>());

    public LearnedScamStore(IOptions<ScamGuardOptions> options, ILogger<LearnedScamStore> logger)
    {
        _path = options.Value.LearnedListPath;
        _logger = logger;
        Load();
    }

    public IReadOnlyCollection<string> Phrases => _snapshot.Phrases;

    public IReadOnlyCollection<string> Hosts => _snapshot.Hosts;

    public bool AddPhrase(string phrase) => Add(phrase, isHost: false);

    public bool AddHost(string host) => Add(host, isHost: true);

    private bool Add(string value, bool isHost)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        // Guardrails: a host must look like a domain; a phrase must be specific enough not to nuke normal chat.
        if (isHost && normalized.IndexOf('.') <= 0)
        {
            return false;
        }
        if (!isHost && normalized.Length < 4)
        {
            return false;
        }

        lock (_lock)
        {
            var phrases = _snapshot.Phrases.ToList();
            var hosts = _snapshot.Hosts.ToList();
            var target = isHost ? hosts : phrases;
            if (target.Contains(normalized))
            {
                return false;
            }

            target.Add(normalized);
            _snapshot = new Snapshot(phrases, hosts);
            Save();
            return true;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (persisted is not null)
            {
                _snapshot = new Snapshot(
                    persisted.Phrases ?? new List<string>(), persisted.Hosts ?? new List<string>());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Learned-scam store: load failed.");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var persisted = new Persisted
            {
                Phrases = _snapshot.Phrases.ToList(),
                Hosts = _snapshot.Hosts.ToList(),
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(persisted));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Learned-scam store: save failed.");
        }
    }

    private sealed record Snapshot(IReadOnlyList<string> Phrases, IReadOnlyList<string> Hosts);

    private sealed class Persisted
    {
        public List<string> Phrases { get; set; } = new();
        public List<string> Hosts { get; set; } = new();
    }
}
