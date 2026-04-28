using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Memory.Recall;

/// <summary>
/// Per-request handler for the <c>recall_about_user</c> tool. Constructed at the top of
/// <c>CreativeOrchestrator.ExecuteAsync</c> and discarded when the reply finishes. Holds the
/// participant allow-list and a small per-request memory cache so repeat calls within one turn
/// don't hit the store more than once per user.
///
/// See docs/recall_tool_design.md §3 and §4.2.
/// </summary>
public sealed class RecallToolHandler
{
    private readonly IUserMemoryStore _store;
    private readonly IMemoryScorer _scorer;
    private readonly MemoryRelevanceOptions _options;
    private readonly IReadOnlySet<ulong> _allowedUserIds;
    private readonly ILogger _logger;
    private readonly Dictionary<ulong, IReadOnlyList<UserMemory>> _cache = new();

    public RecallToolHandler(
        IUserMemoryStore store,
        IMemoryScorer scorer,
        MemoryRelevanceOptions options,
        IReadOnlySet<ulong> allowedUserIds,
        ILogger logger,
        IReadOnlyDictionary<ulong, IReadOnlyList<UserMemory>>? prefetched = null)
    {
        _store = store;
        _scorer = scorer;
        _options = options;
        _allowedUserIds = allowedUserIds;
        _logger = logger;

        if (prefetched is not null)
        {
            foreach (var (uid, mems) in prefetched)
            {
                _cache[uid] = mems;
            }
        }
    }

    public int RecallsPerformed { get; private set; }

    /// <summary>
    /// Execute one invocation of the recall tool.
    /// </summary>
    /// <param name="userId">user_id arg from the model.</param>
    /// <param name="query">Optional query string. If non-null/non-empty, used to re-rank results.</param>
    /// <param name="asOf">Reference timestamp for note ages. Pass <see cref="CreativeRequest.Timestamp"/> for determinism;
    /// defaults to <see cref="DateTimeOffset.UtcNow"/> if omitted.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RecallToolResult> RecallAsync(ulong userId, string? query, DateTimeOffset asOf = default, CancellationToken ct = default)
    {
        if (asOf == default) asOf = DateTimeOffset.UtcNow;
        RecallsPerformed++;

        if (!_allowedUserIds.Contains(userId))
        {
            _logger.LogInformation(
                "recall_tool unknown_user_id requested_user={RequestedUser} allowed_count={AllowedCount}",
                userId, _allowedUserIds.Count);
            return RecallToolResult.UnknownUser;
        }

        var memories = await GetForUserAsync(userId, ct);
        var admissible = MemoryFilter.Admissible(memories, _options.SuppressionOverlapThreshold);

        if (admissible.Count == 0)
        {
            _logger.LogInformation(
                "recall_tool no_notes user={UserId} total_stored={TotalStored} query_present={QueryPresent}",
                userId, memories.Count, !string.IsNullOrWhiteSpace(query));
            return RecallToolResult.NoNotes;
        }

        // Optional ranking. If query is empty, preserve store ordering (most-recent-touched first per the
        // store contract). If non-empty, score and sort by score descending — but never *exclude* anything,
        // so a query that happens to share no tokens with any memory still returns the full set.
        IReadOnlyList<UserMemory> ranked;
        double topScore = 0.0;
        if (!string.IsNullOrWhiteSpace(query))
        {
            try
            {
                var scoring = _scorer.Score(admissible, new[] { query });
                // Combine admitted+rejected, preserving each memory's score, then order desc.
                var scored = scoring.Admitted
                    .Concat(scoring.Rejected)
                    .OrderByDescending(s => s.Score)
                    .Select(s => s.Memory)
                    .ToList();
                ranked = scored;
                topScore = scoring.TopScore;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "recall_tool scorer_threw — returning unranked");
                ranked = admissible;
            }
        }
        else
        {
            ranked = admissible;
        }

        var topK = _options.RecallTopK;
        var truncated = ranked.Count > topK;
        var slice = truncated ? ranked.Take(topK).ToList() : ranked;

        var notes = slice.Select(m => new RecalledNote(
            Content: m.Content,
            Kind: m.Kind.ToString(),
            Context: string.IsNullOrWhiteSpace(m.Context) ? "background chatter" : m.Context,
            Age: HumanizedAge.Format(asOf - m.LastReferencedAt))).ToList();

        _logger.LogInformation(
            "recall_tool ok user={UserId} returned={Returned} total={Total} truncated={Truncated} query_present={QueryPresent} top_score={TopScore:F3} call_index={CallIndex}",
            userId, notes.Count, ranked.Count, truncated, !string.IsNullOrWhiteSpace(query), topScore, RecallsPerformed);

        return new RecallToolResult(notes, ranked.Count, truncated, Note: null);
    }

    private async Task<IReadOnlyList<UserMemory>> GetForUserAsync(ulong userId, CancellationToken ct)
    {
        if (_cache.TryGetValue(userId, out var cached)) return cached;
        var fetched = await _store.GetMemoriesAsync(userId, ct);
        _cache[userId] = fetched;
        return fetched;
    }
}
