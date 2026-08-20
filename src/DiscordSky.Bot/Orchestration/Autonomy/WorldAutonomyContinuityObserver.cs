using System.Security.Cryptography;
using System.Text;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Orchestration.Empire;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record WorldAutonomyContinuityBrief(
    string Text,
    IReadOnlyList<string> MemoryIds,
    int AdmissibleMemoryCount,
    double? TopScore,
    bool RankPresent,
    string Digest);

/// <summary>
/// Builds the bounded continuity intelligence that a future autonomy canary may receive. The current release is
/// shadow-only: it records IDs and a digest for relevance review but never changes a model prompt.
/// </summary>
public sealed class WorldAutonomyContinuityObserver
{
    internal const int MaxMemories = 2;
    internal const int MaxBriefChars = 600;

    private readonly IUserMemoryStore _memoryStore;
    private readonly IOptionsMonitor<MemoryRelevanceOptions> _memoryOptions;
    private readonly IMemoryScorer _memoryScorer;
    private readonly EmpireStateStore? _empireState;
    private readonly WorldAutonomyOptions _options;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<WorldAutonomyContinuityObserver> _logger;
    private readonly TimeProvider _timeProvider;

    public WorldAutonomyContinuityObserver(
        IUserMemoryStore memoryStore,
        IOptionsMonitor<MemoryRelevanceOptions> memoryOptions,
        IMemoryScorer memoryScorer,
        IOptions<WorldAutonomyOptions> options,
        IRecallTelemetrySink telemetry,
        ILogger<WorldAutonomyContinuityObserver> logger,
        EmpireStateStore? empireState = null,
        TimeProvider? timeProvider = null)
    {
        _memoryStore = memoryStore;
        _memoryOptions = memoryOptions;
        _memoryScorer = memoryScorer;
        _empireState = empireState;
        _options = options.Value;
        _telemetry = telemetry;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorldAutonomyContinuityBrief?> ObserveAsync(
        string route,
        ulong authorId,
        string authorDisplayName,
        string query,
        ulong triggerMessageId,
        string? operationId,
        CancellationToken cancellationToken)
    {
        if (!_options.ContinuityBriefShadowEnabled)
        {
            return null;
        }

        try
        {
            var admissible = await _memoryStore.GetAdmissibleMemoriesAsync(
                authorId,
                _memoryOptions,
                cancellationToken).ConfigureAwait(false);
            var ranked = _memoryScorer.RankForRecall(admissible, query, _timeProvider.GetUtcNow());
            var selected = ranked.Take(MaxMemories).ToArray();
            var rank = _empireState is { Enabled: true }
                ? _empireState.RankFor(authorDisplayName)
                : null;
            var brief = BuildBrief(selected, admissible.Count, rank?.Title);

            _telemetry.Emit(new TelemetryEvent(
                Timestamp: _timeProvider.GetUtcNow(),
                EventType: TelemetryEventTypes.WorldAutonomyContinuity,
                UserHash: UserIdHash.Hash(authorId),
                Kind: route,
                Outcome: brief is null ? "no_candidate" : "shadow_candidate",
                Count: brief?.MemoryIds.Count ?? 0,
                Total: admissible.Count,
                TopScore: brief?.TopScore,
                MessageId: triggerMessageId,
                Reason: brief is null
                    ? "no_memory_or_rank"
                    : brief.RankPresent
                        ? "rank_present"
                        : "memory_only",
                OperationId: operationId,
                ProjectionDigest: brief?.Digest,
                CharacterCount: brief?.Text.Length,
                MemoryIds: brief?.MemoryIds,
                RankPresent: brief?.RankPresent));
            return brief;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "World-autonomy continuity shadow failed for message {MessageId}.", triggerMessageId);
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: _timeProvider.GetUtcNow(),
                EventType: TelemetryEventTypes.WorldAutonomyContinuity,
                UserHash: UserIdHash.Hash(authorId),
                Kind: route,
                Outcome: "failed",
                MessageId: triggerMessageId,
                Reason: exception.GetType().Name,
                OperationId: operationId));
            return null;
        }
    }

    internal static WorldAutonomyContinuityBrief? BuildBrief(
        IReadOnlyList<ScoredMemory> selected,
        int admissibleMemoryCount,
        string? rankTitle)
    {
        if (selected.Count == 0 && string.IsNullOrWhiteSpace(rankTitle))
        {
            return null;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(rankTitle))
        {
            builder.Append("Current title: ").Append(rankTitle!.ReplaceLineEndings(" ").Trim()).AppendLine();
        }
        foreach (var item in selected)
        {
            builder.Append("- ").Append(item.Memory.Content.ReplaceLineEndings(" ").Trim()).AppendLine();
        }

        var text = builder.ToString().Trim();
        if (text.Length > MaxBriefChars)
        {
            text = text[..MaxBriefChars];
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new WorldAutonomyContinuityBrief(
            text,
            selected
                .Select(item => item.Memory.MemoryId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray(),
            admissibleMemoryCount,
            selected.Count > 0 ? selected[0].Score : null,
            !string.IsNullOrWhiteSpace(rankTitle),
            digest);
    }
}