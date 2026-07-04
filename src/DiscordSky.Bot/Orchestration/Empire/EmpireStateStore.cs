using System.Text;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// Owns the canonical <see cref="EmpireState"/>: loads and seeds it, serves lock-free reads on the hot reply
/// path, commits validated updates atomically to the PVC, keeps a small rollback ring, and renders the
/// prompt directive. Mirrors LearnedScamStore (volatile snapshot plus lock) but hardened with an atomic
/// write, since this is the single source of truth. When disabled it loads read-only and never writes.
/// </summary>
public sealed class EmpireStateStore
{
    private const int RollbackMax = 5;

    private readonly EmpireStateOptions _options;
    private readonly ILogger<EmpireStateStore> _logger;
    private readonly object _lock = new();
    private readonly LinkedList<EmpireState> _rollback = new();
    private volatile EmpireState _current;

    public EmpireStateStore(IOptions<EmpireStateOptions> options, ILogger<EmpireStateStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _current = Load() ?? EmpireSeed.Initial(_options, DateTimeOffset.UtcNow);
    }

    public bool Enabled => _options.Enabled;
    public EmpireStateOptions Options => _options;
    public EmpireState Current => _current;

    /// <summary>The title he has given this person, if any (case-insensitive). Null when they have none.</summary>
    public Rank? RankFor(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var name = displayName.Trim();
        foreach (var r in _current.Ranks)
        {
            if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }
        return null;
    }

    /// <summary>Marks a rank freshly used (resets its idle counter) so active titles do not decay out. In-memory; the next commit persists it.</summary>
    public void TouchRank(string name)
    {
        lock (_lock)
        {
            var ranks = _current.Ranks;
            for (var i = 0; i < ranks.Count; i++)
            {
                if (string.Equals(ranks[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (ranks[i].IdleTicks == 0) return;
                    var next = ranks.ToList();
                    next[i] = next[i] with { IdleTicks = 0 };
                    _current = _current with { Ranks = next };
                    return;
                }
            }
        }
    }

    /// <summary>Commits the next state: bumps the version, stamps the time, pushes the prior onto the rollback ring, and writes atomically.</summary>
    public void Commit(EmpireState next)
    {
        lock (_lock)
        {
            var committed = next with { Version = _current.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
            _rollback.AddFirst(_current);
            while (_rollback.Count > RollbackMax)
            {
                _rollback.RemoveLast();
            }
            _current = committed;
            Save(committed);
        }
    }

    /// <summary>Renders the spotlighted, data-marked prompt block: the mood line, the freeform log, and a rank line for the current speaker if they have one.</summary>
    public string BuildDirective(string? speakerDisplayName)
    {
        var s = _current;
        var sb = new StringBuilder();
        sb.Append("=== YOUR WAR-ROOM LOG (your own notes; canonical, stay consistent, do NOT read it aloud) ===\n");
        sb.Append("Mood: ").Append(s.Mood.Label).Append(".\n\n");
        sb.Append(s.Body.Trim()).Append('\n');

        var rank = RankFor(speakerDisplayName);
        if (rank is not null)
        {
            sb.Append("\nYou have dubbed ").Append(Sanitize(rank.Name)).Append(" your ").Append(Sanitize(rank.Title)).Append(".\n");
            TouchRank(rank.Name);
        }

        sb.Append("Do not recite this log. Let it set your mood and let a detail surface only when it fits.");
        return sb.ToString();
    }

    private static string Sanitize(string s) => (s ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();

    private EmpireState? Load()
    {
        try
        {
            if (!File.Exists(_options.Path)) return null;
            var dto = JsonSerializer.Deserialize<EmpireState>(File.ReadAllText(_options.Path));
            if (dto is null || string.IsNullOrWhiteSpace(dto.Body)) return null;
            // Re-derive the mood label so a hand-edited file cannot inject a bogus label, and default a null rank list.
            var mood = EmpireMood.Make(dto.Mood?.Valence ?? _options.BaselineValence, dto.Mood?.Arousal ?? _options.BaselineArousal);
            return dto with { Mood = mood, Ranks = dto.Ranks ?? Array.Empty<Rank>() };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Empire state: load failed; will reseed.");
            return null;
        }
    }

    private void Save(EmpireState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_options.Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = _options.Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, _options.Path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Empire state: save failed.");
        }
    }
}
