using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record StewardOperationEvidence(
    string RequestId,
    string Kind,
    string Status,
    string? ErrorCode,
    string InvocationJson,
    string RawJson)
{
    internal static StewardOperationEvidence Parse(JsonElement envelope, WorldAutonomyToolCall expected)
    {
        var data = RequiredProperty(envelope, "data");
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Steward operation lookup did not return operation data.");
        }

        var evidence = new StewardOperationEvidence(
            RequiredString(data, "requestId"),
            RequiredString(data, "kind"),
            RequiredString(data, "status"),
            OptionalString(data, "errorCode"),
            RequiredString(data, "invocationJson"),
            envelope.GetRawText());
        if (!string.Equals(evidence.RequestId, expected.RequestId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Kind, expected.ToolName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Steward operation evidence did not match the pending Sky tool call identity.");
        }

        using var invocation = JsonDocument.Parse(evidence.InvocationJson);
        var invocationRoot = invocation.RootElement;
        if (!string.Equals(RequiredString(invocationRoot, "kind"), expected.ToolName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Steward operation invocation kind did not match the pending Sky tool call.");
        }

        var actualArguments = WorldAutonomyCanonicalizer.SerializeJson(RequiredProperty(invocationRoot, "arguments"));
        if (!string.Equals(actualArguments, expected.ArgumentsJson, StringComparison.Ordinal) &&
            !MatchesNormalizedMcpInvocation(expected, invocationRoot))
        {
            throw new InvalidOperationException("Steward operation arguments did not match the durable Sky dispatch.");
        }

        return evidence;
    }

    private static bool MatchesNormalizedMcpInvocation(
        WorldAutonomyToolCall expected,
        JsonElement invocationRoot)
    {
        if (!invocationRoot.TryGetProperty("resourceId", out var resourceId) ||
            resourceId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(resourceId.GetString()))
        {
            return false;
        }

        using var expectedArguments = JsonDocument.Parse(expected.ArgumentsJson);
        var expectedRoot = expectedArguments.RootElement;
        if (expectedRoot.ValueKind != JsonValueKind.Object ||
            !MatchesLiftedIdentity(expectedRoot, invocationRoot, "request_id", "requestId") ||
            !MatchesLiftedIdentity(expectedRoot, invocationRoot, "reason", "reason"))
        {
            return false;
        }

        // Native tools arrive in two shapes: arguments flat at the root, or wrapped in an "input" object.
        // Steward's journal always stores the unwrapped payload, and it lifts the resource id out of those
        // arguments for some tools while keeping it inline for others (create_webhook keeps channelId).
        // Accept any of the equivalent forms rather than assuming one layout.
        var payload = expectedRoot.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
            ? input
            : expectedRoot;

        // Steward materializes its full typed request record, so its journal pads unset arguments with
        // explicit nulls that the model never sent. Normalize both sides the same way.
        var actual = NormalizeMcpArguments(RequiredProperty(invocationRoot, "arguments"), resourceId: null);

        return string.Equals(
                NormalizeMcpArguments(payload, resourceId.GetString()!),
                actual,
                StringComparison.Ordinal) ||
            string.Equals(
                NormalizeMcpArguments(payload, resourceId: null),
                actual,
                StringComparison.Ordinal);
    }

    private static bool MatchesLiftedIdentity(
        JsonElement expectedArguments,
        JsonElement invocation,
        string expectedPropertyName,
        string invocationPropertyName)
    {
        if (!expectedArguments.TryGetProperty(expectedPropertyName, out var expectedValue))
        {
            return true;
        }

        return invocation.TryGetProperty(invocationPropertyName, out var invocationValue) &&
            expectedValue.ValueKind == invocationValue.ValueKind &&
            string.Equals(expectedValue.GetRawText(), invocationValue.GetRawText(), StringComparison.Ordinal);
    }

    private static string NormalizeMcpArguments(JsonElement expectedArguments, string? resourceId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNormalizedMcpElement(writer, expectedArguments, resourceId, isRoot: true);
        }

        using var normalized = JsonDocument.Parse(stream.ToArray());
        return WorldAutonomyCanonicalizer.SerializeJson(normalized.RootElement);
    }

    private static void WriteNormalizedMcpElement(
        Utf8JsonWriter writer,
        JsonElement element,
        string? resourceId,
        bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(property => ToCamelCase(property.Name), StringComparer.Ordinal))
                {
                    // Steward materializes optional DTO defaults that the MCP caller omitted. Null, false,
                    // and an empty collection are all no-op representations here. Every value that can
                    // change Discord state remains in the canonical comparison.
                    if (IsOmittedOptionalDefault(property.Value))
                    {
                        continue;
                    }

                    var normalizedName = ToCamelCase(property.Name);
                    if (isRoot && IsLiftedIdentity(normalizedName, property.Value, resourceId))
                    {
                        continue;
                    }

                    writer.WritePropertyName(normalizedName);
                    WriteNormalizedMcpElement(writer, property.Value, resourceId, isRoot: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteNormalizedMcpElement(writer, item, resourceId, isRoot: false);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsOmittedOptionalDefault(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.False ||
        (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private static bool IsLiftedIdentity(string normalizedName, JsonElement value, string? resourceId) =>
        normalizedName is "requestId" or "reason" ||
        (resourceId is not null &&
            normalizedName.EndsWith("Id", StringComparison.Ordinal) &&
            value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), resourceId, StringComparison.Ordinal));

    private static string ToCamelCase(string value)
    {
        var builder = new StringBuilder(value.Length);
        var uppercaseNext = false;
        foreach (var character in value)
        {
            if (character == '_')
            {
                uppercaseNext = true;
                continue;
            }

            builder.Append(uppercaseNext ? char.ToUpperInvariant(character) : character);
            uppercaseNext = false;
        }

        return builder.ToString();
    }

    internal static string ParseReconciliationStatus(JsonElement envelope, string expectedRequestId)
    {
        var data = RequiredProperty(envelope, "data");
        if (!string.Equals(RequiredString(data, "requestId"), expectedRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Steward reconciliation returned a different request ID.");
        }

        return RequiredString(data, "status");
    }

    private static JsonElement RequiredProperty(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new InvalidOperationException($"Steward operation evidence omitted '{propertyName}'.");
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var property = RequiredProperty(root, propertyName);
        return property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidOperationException($"Steward operation evidence omitted '{propertyName}'.");
    }

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
            }
        }

        return null;
    }
}

public sealed class WorldAutonomyRecoveryService : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RecoveryRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15)
    ];

    private readonly WorldAutonomyConfiguration _configuration;
    private readonly IWorldAutonomyLedger _ledger;
    private readonly StewardMcpSupervisor _stewardSupervisor;
    private readonly ILogger<WorldAutonomyRecoveryService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _reconciliationInterval;

    public WorldAutonomyRecoveryService(
        WorldAutonomyConfiguration configuration,
        IWorldAutonomyLedger ledger,
        StewardMcpSupervisor stewardSupervisor,
        ILogger<WorldAutonomyRecoveryService> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? reconciliationInterval = null)
    {
        _configuration = configuration;
        _ledger = ledger;
        _stewardSupervisor = stewardSupervisor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reconciliationInterval = reconciliationInterval ?? ReconciliationInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.IsEnabled)
        {
            return;
        }

        var processStartedAt = _timeProvider.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunRecoveryCycleAsync(processStartedAt, stoppingToken).ConfigureAwait(false);
            await Task.Delay(_reconciliationInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task RunRecoveryCycleAsync(
        DateTimeOffset processStartedAt,
        CancellationToken cancellationToken)
    {
        await ReconcileOutstandingCallsAsync(cancellationToken).ConfigureAwait(false);
        await FinalizeInterruptedRunsAsync(processStartedAt, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileOutstandingCallsAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= RecoveryRetryDelays.Length; attempt++)
        {
            IReadOnlyList<WorldAutonomyToolCall> calls;
            try
            {
                calls = await _ledger.ListRecoverableCallsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unable to load pending autonomy calls for recovery.");
                if (attempt == RecoveryRetryDelays.Length)
                {
                    return;
                }

                await Task.Delay(RecoveryRetryDelays[attempt], _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var journaledCalls = calls.Where(call => !string.IsNullOrWhiteSpace(call.RequestId)).ToArray();
            if (attempt == 0)
            {
                foreach (var call in calls.Where(call =>
                    string.IsNullOrWhiteSpace(call.RequestId) &&
                    call.DispatchStatus != WorldAutonomyDispatchStatuses.Unknown))
                {
                    await MarkNonJournaledRecoveryUnavailableAsync(call, cancellationToken).ConfigureAwait(false);
                }
            }

            if (journaledCalls.Length == 0)
            {
                return;
            }

            foreach (var call in journaledCalls)
            {
                await RecoverCallAsync(call, cancellationToken).ConfigureAwait(false);
            }

            if (attempt == RecoveryRetryDelays.Length)
            {
                return;
            }

            await Task.Delay(RecoveryRetryDelays[attempt], _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinalizeInterruptedRunsAsync(
        DateTimeOffset processStartedAt,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorldAutonomyRunRecord> runningRuns;
        try
        {
            runningRuns = await _ledger.ListRunningRunsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load running autonomy runs for interruption recovery.");
            return;
        }

        foreach (var run in runningRuns.Where(run => run.StartedAt < processStartedAt))
        {
            try
            {
                var calls = await _ledger.ListToolCallsAsync(run.RunId, cancellationToken).ConfigureAwait(false);
                if (calls.Any(call => call.DispatchStatus is
                    WorldAutonomyDispatchStatuses.Pending or
                    WorldAutonomyDispatchStatuses.Accepted))
                {
                    continue;
                }

                var summary = new
                {
                    reason = "process_restarted",
                    totalCalls = calls.Count,
                    succeeded = calls.Count(call => call.DispatchStatus == WorldAutonomyDispatchStatuses.Succeeded),
                    failed = calls.Count(call => call.DispatchStatus == WorldAutonomyDispatchStatuses.Failed),
                    partialFailure = calls.Count(call => call.DispatchStatus == WorldAutonomyDispatchStatuses.PartialFailure),
                    unknown = calls.Count(call => call.DispatchStatus == WorldAutonomyDispatchStatuses.Unknown)
                };
                var failureReason = calls.Count == 0
                    ? "process_restarted_before_completion"
                    : summary.unknown > 0
                        ? "process_restarted_with_unknown_outcomes"
                        : "process_restarted_after_call_recovery";
                var completedAt = _timeProvider.GetUtcNow();
                await _ledger.RecordRunEventAsync(
                    run.RunId,
                    "run_interrupted",
                    JsonSerializer.Serialize(summary),
                    completedAt,
                    cancellationToken).ConfigureAwait(false);
                await _ledger.CompleteRunAsync(
                    run.RunId,
                    WorldAutonomyRunStatuses.Failed,
                    finalText: null,
                    failureReason,
                    completedAt,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Terminalized autonomy run {RunId} interrupted by a previous process with {CallCount} tool calls.",
                    run.RunId,
                    calls.Count);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not terminalize interrupted autonomy run {RunId}.", run.RunId);
            }
        }
    }

    private async Task MarkNonJournaledRecoveryUnavailableAsync(
        WorldAutonomyToolCall call,
        CancellationToken cancellationToken)
    {
        await _ledger.CompleteToolCallAsync(
            call.CallId,
            WorldAutonomyDispatchStatuses.Unknown,
            resultJson: call.ResultJson,
            errorMessage: "no_steward_request_id",
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        await _ledger.RecordRunEventAsync(
            call.RunId,
            "recovery_unavailable",
            JsonSerializer.Serialize(new { callId = call.CallId, reason = "no_steward_request_id" }),
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "Autonomy call {CallId} has no Steward request ID and cannot be reconciled without replay.",
            call.CallId);
    }

    private async Task RecoverCallAsync(WorldAutonomyToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _ledger.GetRunAsync(call.RunId, cancellationToken).ConfigureAwait(false);
            if (run is null || !_configuration.TryGetBinding(run.GuildId, out _))
            {
                _logger.LogWarning(
                    "Cannot recover autonomy call {CallId}: its guild binding is unavailable.",
                    call.CallId);
                return;
            }

            var session = await _stewardSupervisor.GetSessionAsync(run.GuildId, cancellationToken).ConfigureAwait(false);
            var requestArguments = new Dictionary<string, object?> { ["request_id"] = call.RequestId };
            var operationResult = await session.Catalog.GetNativeTool("get_operation")
                .CallAsync(requestArguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var evidence = StewardOperationEvidence.Parse(ExtractEnvelope(operationResult), call);
            var status = evidence.Status;
            var rawEvidence = evidence.RawJson;
            if (string.Equals(status, WorldAutonomyDispatchStatuses.Unknown, StringComparison.Ordinal))
            {
                var reconciliationResult = await session.Catalog.GetNativeTool("reconcile_operation")
                    .CallAsync(requestArguments, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var reconciliationEnvelope = ExtractEnvelope(reconciliationResult);
                status = StewardOperationEvidence.ParseReconciliationStatus(reconciliationEnvelope, call.RequestId!);
                rawEvidence = reconciliationEnvelope.GetRawText();
            }

            if (TryMapTerminalStatus(status, out var terminalStatus))
            {
                await _ledger.CompleteToolCallAsync(
                    call.CallId,
                    terminalStatus,
                    rawEvidence,
                    evidence.ErrorCode,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }

            await _ledger.RecordRunEventAsync(
                call.RunId,
                "recovery_checked",
                JsonSerializer.Serialize(new { callId = call.CallId, status }),
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Autonomy recovery could not resolve call {CallId}.", call.CallId);
        }
    }

    private static JsonElement ExtractEnvelope(CallToolResult result)
    {
        if (result.IsError == true)
        {
            throw new InvalidOperationException("Steward recovery tool returned an MCP tool error.");
        }

        var text = result.Content.OfType<TextContentBlock>().SingleOrDefault()?.Text
            ?? throw new InvalidOperationException("Steward recovery tool did not return JSON text.");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static bool TryMapTerminalStatus(string status, out string terminalStatus)
    {
        terminalStatus = status switch
        {
            "succeeded" => WorldAutonomyDispatchStatuses.Succeeded,
            "failed" => WorldAutonomyDispatchStatuses.Failed,
            "partial_failure" => WorldAutonomyDispatchStatuses.PartialFailure,
            "unknown" => WorldAutonomyDispatchStatuses.Unknown,
            _ => string.Empty
        };
        return terminalStatus.Length > 0;
    }
}