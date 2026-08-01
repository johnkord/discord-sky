using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public static class WorldAutonomyRunStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
}

public static class WorldAutonomyDispatchStatuses
{
    public const string Pending = "dispatch_pending";
    public const string Accepted = "accepted";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string PartialFailure = "partial_failure";
    public const string Unknown = "unknown";
}

public sealed record WorldAutonomyRunStart(
    string RunId,
    ulong GuildId,
    string Trigger,
    string? SourceMessageId,
    string? SourceEpisodeId,
    string Model,
    string ProfileDigest,
    string ManifestDigest,
    DateTimeOffset StartedAt);

public sealed record WorldAutonomyRunRecord(
    string RunId,
    ulong GuildId,
    string Trigger,
    string? SourceMessageId,
    string? SourceEpisodeId,
    string Model,
    string ProfileDigest,
    string ManifestDigest,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? FinalText,
    string? FailureReason);

public sealed record WorldAutonomyPendingDispatch(
    string CallId,
    string RunId,
    int Sequence,
    string ToolName,
    string? RequestId,
    string ArgumentsJson,
    string ArgumentsDigest,
    string SchemaDigest,
    DateTimeOffset CreatedAt);

public sealed record WorldAutonomyToolCall(
    string CallId,
    string RunId,
    int Sequence,
    string ToolName,
    string? RequestId,
    string ArgumentsJson,
    string ArgumentsDigest,
    string SchemaDigest,
    string DispatchStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ResultJson,
    string? ErrorMessage);

public interface IWorldAutonomyLedger
{
    Task StartRunAsync(WorldAutonomyRunStart run, CancellationToken cancellationToken);

    Task RecordDispatchPendingAsync(WorldAutonomyPendingDispatch dispatch, CancellationToken cancellationToken);

    Task CompleteToolCallAsync(
        string callId,
        string dispatchStatus,
        string? resultJson,
        string? errorMessage,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task CompleteRunAsync(
        string runId,
        string status,
        string? finalText,
        string? failureReason,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorldAutonomyToolCall>> ListRecoverableCallsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorldAutonomyRunRecord>> ListRunningRunsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorldAutonomyToolCall>> ListToolCallsAsync(
        string runId,
        CancellationToken cancellationToken);

    Task<WorldAutonomyRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken);

    Task RecordRunEventAsync(
        string runId,
        string kind,
        string? payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}

public sealed class SqliteWorldAutonomyLedger : IWorldAutonomyLedger, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteWorldAutonomyLedger(IOptions<WorldAutonomyOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public SqliteWorldAutonomyLedger(WorldAutonomyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _databasePath = Path.GetFullPath(options.LedgerPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
    }

    public async Task StartRunAsync(WorldAutonomyRunStart run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_runs (
                run_id, guild_id, trigger, source_message_id, source_episode_id,
                model, profile_digest, manifest_digest, started_at, status)
            VALUES (
                $runId, $guildId, $trigger, $sourceMessageId, $sourceEpisodeId,
                $model, $profileDigest, $manifestDigest, $startedAt, $status);
            """;
        command.Parameters.AddWithValue("$runId", run.RunId);
        command.Parameters.AddWithValue("$guildId", run.GuildId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$trigger", run.Trigger);
        command.Parameters.AddWithValue("$sourceMessageId", (object?)run.SourceMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceEpisodeId", (object?)run.SourceEpisodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", run.Model);
        command.Parameters.AddWithValue("$profileDigest", run.ProfileDigest);
        command.Parameters.AddWithValue("$manifestDigest", run.ManifestDigest);
        command.Parameters.AddWithValue("$startedAt", FormatTimestamp(run.StartedAt));
        command.Parameters.AddWithValue("$status", WorldAutonomyRunStatuses.Running);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordDispatchPendingAsync(
        WorldAutonomyPendingDispatch dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tool_calls (
                call_id, run_id, sequence, tool_name, request_id, arguments_json,
                arguments_digest, schema_digest, dispatch_status, created_at)
            VALUES (
                $callId, $runId, $sequence, $toolName, $requestId, $argumentsJson,
                $argumentsDigest, $schemaDigest, $dispatchStatus, $createdAt);
            """;
        command.Parameters.AddWithValue("$callId", dispatch.CallId);
        command.Parameters.AddWithValue("$runId", dispatch.RunId);
        command.Parameters.AddWithValue("$sequence", dispatch.Sequence);
        command.Parameters.AddWithValue("$toolName", dispatch.ToolName);
        command.Parameters.AddWithValue("$requestId", (object?)dispatch.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$argumentsJson", dispatch.ArgumentsJson);
        command.Parameters.AddWithValue("$argumentsDigest", dispatch.ArgumentsDigest);
        command.Parameters.AddWithValue("$schemaDigest", dispatch.SchemaDigest);
        command.Parameters.AddWithValue("$dispatchStatus", WorldAutonomyDispatchStatuses.Pending);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(dispatch.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteToolCallAsync(
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tool_calls
            SET dispatch_status = $dispatchStatus,
                result_json = $resultJson,
                error_message = $errorMessage,
                completed_at = $completedAt
            WHERE call_id = $callId
              AND dispatch_status IN ('dispatch_pending', 'accepted', 'unknown');
            """;
        command.Parameters.AddWithValue("$dispatchStatus", dispatchStatus);
        command.Parameters.AddWithValue("$resultJson", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completedAt));
        command.Parameters.AddWithValue("$callId", callId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Autonomy tool call '{callId}' is not awaiting completion.");
        }
    }

    public async Task CompleteRunAsync(
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_runs
            SET status = $status,
                final_text = $finalText,
                failure_reason = $failureReason,
                completed_at = $completedAt
            WHERE run_id = $runId
              AND status = 'running';
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$finalText", (object?)finalText ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureReason", (object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completedAt));
        command.Parameters.AddWithValue("$runId", runId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Autonomy run '{runId}' is not running.");
        }
    }

    public async Task<IReadOnlyList<WorldAutonomyToolCall>> ListRecoverableCallsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {ToolCallSelect}
            WHERE dispatch_status IN ('dispatch_pending', 'accepted', 'unknown')
            ORDER BY created_at, sequence;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var calls = new List<WorldAutonomyToolCall>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            calls.Add(ReadToolCall(reader));
        }

        return calls;
    }

    public async Task<IReadOnlyList<WorldAutonomyRunRecord>> ListRunningRunsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {RunSelect}
            WHERE status = 'running'
            ORDER BY started_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var runs = new List<WorldAutonomyRunRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public async Task<IReadOnlyList<WorldAutonomyToolCall>> ListToolCallsAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {ToolCallSelect}
            WHERE run_id = $runId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var calls = new List<WorldAutonomyToolCall>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            calls.Add(ReadToolCall(reader));
        }

        return calls;
    }

    public async Task<WorldAutonomyRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {RunSelect}
            WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRun(reader)
            : null;
    }

    public async Task RecordRunEventAsync(
        string runId,
        string kind,
        string? payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_events (run_id, event_kind, payload_json, occurred_at)
            VALUES ($runId, $kind, $payloadJson, $occurredAt);
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payloadJson", (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$occurredAt", FormatTimestamp(occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _initializationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string RunSelect = """
        SELECT run_id, guild_id, trigger, source_message_id, source_episode_id,
               model, profile_digest, manifest_digest, started_at, completed_at,
               status, final_text, failure_reason
        FROM agent_runs
        """;

    private const string ToolCallSelect = """
        SELECT call_id, run_id, sequence, tool_name, request_id, arguments_json,
               arguments_digest, schema_digest, dispatch_status, created_at,
               completed_at, result_json, error_message
        FROM tool_calls
        """;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath)
                ?? throw new InvalidOperationException("The autonomy ledger path has no directory.");
            Directory.CreateDirectory(directory);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (version == 0)
            {
                await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            else if (version != SchemaVersion)
            {
                throw new InvalidOperationException($"Unsupported autonomy ledger schema version {version}.");
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE agent_runs (
                run_id TEXT PRIMARY KEY,
                guild_id TEXT NOT NULL,
                trigger TEXT NOT NULL,
                source_message_id TEXT NULL,
                source_episode_id TEXT NULL,
                model TEXT NOT NULL,
                profile_digest TEXT NOT NULL,
                manifest_digest TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                status TEXT NOT NULL CHECK (status IN ('running', 'succeeded', 'failed', 'timed_out')),
                final_text TEXT NULL,
                failure_reason TEXT NULL
            ) STRICT;
            CREATE TABLE tool_calls (
                call_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES agent_runs(run_id),
                sequence INTEGER NOT NULL,
                tool_name TEXT NOT NULL,
                request_id TEXT NULL UNIQUE,
                arguments_json TEXT NOT NULL,
                arguments_digest TEXT NOT NULL,
                schema_digest TEXT NOT NULL,
                dispatch_status TEXT NOT NULL CHECK (dispatch_status IN (
                    'dispatch_pending', 'accepted', 'succeeded', 'failed', 'partial_failure', 'unknown')),
                created_at TEXT NOT NULL,
                completed_at TEXT NULL,
                result_json TEXT NULL,
                error_message TEXT NULL,
                UNIQUE(run_id, sequence)
            ) STRICT;
            CREATE TABLE run_events (
                event_id INTEGER PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES agent_runs(run_id),
                event_kind TEXT NOT NULL,
                payload_json TEXT NULL,
                occurred_at TEXT NOT NULL
            ) STRICT;
            CREATE INDEX ix_autonomy_tool_calls_recovery
                ON tool_calls(dispatch_status, created_at, sequence);
            CREATE INDEX ix_autonomy_runs_guild_started
                ON agent_runs(guild_id, started_at DESC);
            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = FULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static WorldAutonomyRunRecord ReadRun(SqliteDataReader reader) => new(
        reader.GetString(0),
        ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        reader.GetString(2),
        ReadNullableString(reader, 3),
        ReadNullableString(reader, 4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        ReadTimestamp(reader, 8)!.Value,
        ReadTimestamp(reader, 9),
        reader.GetString(10),
        ReadNullableString(reader, 11),
        ReadNullableString(reader, 12));

    private static WorldAutonomyToolCall ReadToolCall(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        ReadNullableString(reader, 4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        ReadTimestamp(reader, 9)!.Value,
        ReadTimestamp(reader, 10),
        ReadNullableString(reader, 11),
        ReadNullableString(reader, 12));

    private static bool IsTerminalDispatchStatus(string status) => status is
        WorldAutonomyDispatchStatuses.Succeeded or
        WorldAutonomyDispatchStatuses.Failed or
        WorldAutonomyDispatchStatuses.PartialFailure or
        WorldAutonomyDispatchStatuses.Unknown;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

public static class WorldAutonomyCanonicalizer
{
    public static string SerializeArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalElement(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    public static string SerializeJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalElement(writer, element);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    public static string ComputeDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}