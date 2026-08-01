using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed class FileBackedWorldAutonomyLedger : IWorldAutonomyLedger, IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly string _ledgerPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LedgerSnapshot _snapshot = new();
    private bool _loaded;

    public FileBackedWorldAutonomyLedger(IOptions<WorldAutonomyOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public FileBackedWorldAutonomyLedger(WorldAutonomyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ledgerPath = Path.GetFullPath(options.LedgerPath);
    }

    public Task StartRunAsync(WorldAutonomyRunStart run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return MutateAsync(snapshot =>
        {
            if (!snapshot.Runs.TryAdd(run.RunId, new WorldAutonomyRunRecord(
                    run.RunId,
                    run.GuildId,
                    run.Trigger,
                    run.SourceMessageId,
                    run.SourceEpisodeId,
                    run.Model,
                    run.ProfileDigest,
                    run.ManifestDigest,
                    run.StartedAt,
                    CompletedAt: null,
                    WorldAutonomyRunStatuses.Running,
                    FinalText: null,
                    FailureReason: null)))
            {
                throw new InvalidOperationException($"Autonomy run '{run.RunId}' already exists.");
            }
        }, cancellationToken);
    }

    public Task RecordDispatchPendingAsync(
        WorldAutonomyPendingDispatch dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return MutateAsync(snapshot =>
        {
            if (!snapshot.Runs.ContainsKey(dispatch.RunId))
            {
                throw new InvalidOperationException($"Autonomy run '{dispatch.RunId}' does not exist.");
            }

            if (snapshot.ToolCalls.ContainsKey(dispatch.CallId) ||
                snapshot.ToolCalls.Values.Any(call =>
                    string.Equals(call.RunId, dispatch.RunId, StringComparison.Ordinal) &&
                    call.Sequence == dispatch.Sequence) ||
                (!string.IsNullOrWhiteSpace(dispatch.RequestId) && snapshot.ToolCalls.Values.Any(call =>
                    string.Equals(call.RequestId, dispatch.RequestId, StringComparison.Ordinal))))
            {
                throw new InvalidOperationException($"Autonomy tool call '{dispatch.CallId}' conflicts with an existing dispatch.");
            }

            snapshot.ToolCalls.Add(dispatch.CallId, new WorldAutonomyToolCall(
                dispatch.CallId,
                dispatch.RunId,
                dispatch.Sequence,
                dispatch.ToolName,
                dispatch.RequestId,
                dispatch.ArgumentsJson,
                dispatch.ArgumentsDigest,
                dispatch.SchemaDigest,
                WorldAutonomyDispatchStatuses.Pending,
                dispatch.CreatedAt,
                CompletedAt: null,
                ResultJson: null,
                ErrorMessage: null));
        }, cancellationToken);
    }

    public Task CompleteToolCallAsync(
        string callId,
        string dispatchStatus,
        string? resultJson,
        string? errorMessage,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        if (dispatchStatus != WorldAutonomyDispatchStatuses.Accepted && !IsTerminalDispatchStatus(dispatchStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchStatus));
        }

        return MutateAsync(snapshot =>
        {
            if (!snapshot.ToolCalls.TryGetValue(callId, out var current) ||
                current.DispatchStatus is not (WorldAutonomyDispatchStatuses.Pending or
                    WorldAutonomyDispatchStatuses.Accepted or
                    WorldAutonomyDispatchStatuses.Unknown))
            {
                throw new InvalidOperationException($"Autonomy tool call '{callId}' is not awaiting completion.");
            }

            snapshot.ToolCalls[callId] = current with
            {
                DispatchStatus = dispatchStatus,
                ResultJson = resultJson,
                ErrorMessage = errorMessage,
                CompletedAt = completedAt
            };
        }, cancellationToken);
    }

    public Task CompleteRunAsync(
        string runId,
        string status,
        string? finalText,
        string? failureReason,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        if (status is not WorldAutonomyRunStatuses.Succeeded and
            not WorldAutonomyRunStatuses.Failed and
            not WorldAutonomyRunStatuses.TimedOut)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return MutateAsync(snapshot =>
        {
            if (!snapshot.Runs.TryGetValue(runId, out var current) ||
                !string.Equals(current.Status, WorldAutonomyRunStatuses.Running, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Autonomy run '{runId}' is not running.");
            }

            snapshot.Runs[runId] = current with
            {
                Status = status,
                FinalText = finalText,
                FailureReason = failureReason,
                CompletedAt = completedAt
            };
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WorldAutonomyToolCall>> ListRecoverableCallsAsync(CancellationToken cancellationToken) =>
        ReadAsync(snapshot => (IReadOnlyList<WorldAutonomyToolCall>)snapshot.ToolCalls.Values
            .Where(call => call.DispatchStatus is WorldAutonomyDispatchStatuses.Pending or
                WorldAutonomyDispatchStatuses.Accepted or
                WorldAutonomyDispatchStatuses.Unknown)
            .OrderBy(call => call.CreatedAt)
            .ThenBy(call => call.Sequence)
            .ToArray(), cancellationToken);

    public Task<IReadOnlyList<WorldAutonomyRunRecord>> ListRunningRunsAsync(CancellationToken cancellationToken) =>
        ReadAsync(snapshot => (IReadOnlyList<WorldAutonomyRunRecord>)snapshot.Runs.Values
            .Where(run => run.Status == WorldAutonomyRunStatuses.Running)
            .OrderBy(run => run.StartedAt)
            .ToArray(), cancellationToken);

    public Task<IReadOnlyList<WorldAutonomyToolCall>> ListToolCallsAsync(
        string runId,
        CancellationToken cancellationToken) =>
        ReadAsync(snapshot => (IReadOnlyList<WorldAutonomyToolCall>)snapshot.ToolCalls.Values
            .Where(call => call.RunId == runId)
            .OrderBy(call => call.Sequence)
            .ToArray(), cancellationToken);

    public Task<WorldAutonomyRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
        ReadAsync(snapshot => snapshot.Runs.GetValueOrDefault(runId), cancellationToken);

    public Task RecordRunEventAsync(
        string runId,
        string kind,
        string? payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        MutateAsync(snapshot =>
        {
            if (!snapshot.Runs.ContainsKey(runId))
            {
                throw new InvalidOperationException($"Autonomy run '{runId}' does not exist.");
            }

            snapshot.Events.Add(new WorldAutonomyRunEvent(runId, kind, payloadJson, occurredAt));
        }, cancellationToken);

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<T> ReadAsync<T>(Func<LedgerSnapshot, T> reader, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return reader(_snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MutateAsync(Action<LedgerSnapshot> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var next = _snapshot.Clone();
            mutation(next);
            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            _snapshot = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_ledgerPath)
            ?? throw new InvalidOperationException("The autonomy ledger path has no directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_ledgerPath))
        {
            var json = await File.ReadAllTextAsync(_ledgerPath, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<LedgerSnapshot>(json, JsonOptions)
                ?? throw new InvalidOperationException("The autonomy ledger snapshot was empty.");
            if (loaded.Version != SchemaVersion)
            {
                throw new InvalidOperationException($"Unsupported autonomy ledger snapshot version {loaded.Version}.");
            }

            _snapshot = loaded.Normalize();
        }

        _loaded = true;
    }

    private async Task PersistAsync(LedgerSnapshot snapshot, CancellationToken cancellationToken)
    {
        var tempPath = $"{_ledgerPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _ledgerPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static bool IsTerminalDispatchStatus(string status) => status is
        WorldAutonomyDispatchStatuses.Succeeded or
        WorldAutonomyDispatchStatuses.Failed or
        WorldAutonomyDispatchStatuses.PartialFailure or
        WorldAutonomyDispatchStatuses.Unknown;

    private sealed class LedgerSnapshot
    {
        public int Version { get; init; } = SchemaVersion;

        public Dictionary<string, WorldAutonomyRunRecord> Runs { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorldAutonomyToolCall> ToolCalls { get; init; } = new(StringComparer.Ordinal);

        public List<WorldAutonomyRunEvent> Events { get; init; } = [];

        public LedgerSnapshot Clone() => new()
        {
            Version = Version,
            Runs = new Dictionary<string, WorldAutonomyRunRecord>(Runs, StringComparer.Ordinal),
            ToolCalls = new Dictionary<string, WorldAutonomyToolCall>(ToolCalls, StringComparer.Ordinal),
            Events = [.. Events]
        };

        public LedgerSnapshot Normalize() => new()
        {
            Version = Version,
            Runs = Runs is null
                ? new Dictionary<string, WorldAutonomyRunRecord>(StringComparer.Ordinal)
                : new Dictionary<string, WorldAutonomyRunRecord>(Runs, StringComparer.Ordinal),
            ToolCalls = ToolCalls is null
                ? new Dictionary<string, WorldAutonomyToolCall>(StringComparer.Ordinal)
                : new Dictionary<string, WorldAutonomyToolCall>(ToolCalls, StringComparer.Ordinal),
            Events = Events is null ? [] : [.. Events]
        };
    }

    private sealed record WorldAutonomyRunEvent(
        string RunId,
        string Kind,
        string? PayloadJson,
        DateTimeOffset OccurredAt);
}