using System.Collections.Concurrent;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record WorldAutonomyOpportunity(
    ulong GuildId,
    string Trigger,
    string Prompt,
    string? SourceMessageId = null,
    string? SourceEpisodeId = null,
    string? TraceId = null,
    string? ModelOverride = null,
    bool IsDirectAddress = false,
    string? PersonaDirective = null,
    ulong? SourceChannelId = null,
    string? SourceChannelName = null,
    ulong? SourceAuthorId = null,
    string? SourceAuthorDisplayName = null,
    VisualRequestIntent VisualIntent = VisualRequestIntent.None);

public sealed record WorldAutonomyRunResult(
    string? RunId,
    ulong GuildId,
    string Status,
    string? FinalText,
    string? FailureReason,
    bool SpokeInChannel = false);

public interface IWorldAutonomyRunner
{
    Task<WorldAutonomyRunResult> RunAsync(
        WorldAutonomyOpportunity opportunity,
        CancellationToken cancellationToken);
}

public sealed class WorldAutonomyOrchestrator : IWorldAutonomyRunner
{
    private readonly WorldAutonomyConfiguration _configuration;
    private readonly IOptionsMonitor<LlmOptions> _llmOptions;
    private readonly StewardMcpSupervisor _stewardSupervisor;
    private readonly WorldAutonomyAgentFactory _agentFactory;
    private readonly IWorldAutonomyLedger _ledger;
    private readonly ILogger<WorldAutonomyOrchestrator> _logger;
    private readonly WorldAutonomySpeechTool? _speechTool;
    private readonly WorldAutonomyVisualTool? _visualTool;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly LlmProviderGuard _providerGuard;
    private readonly TimeProvider _timeProvider;

    public WorldAutonomyOrchestrator(
        WorldAutonomyConfiguration configuration,
        IOptionsMonitor<LlmOptions> llmOptions,
        StewardMcpSupervisor stewardSupervisor,
        WorldAutonomyAgentFactory agentFactory,
        IWorldAutonomyLedger ledger,
        ILogger<WorldAutonomyOrchestrator> logger,
        WorldAutonomySpeechTool? speechTool = null,
        WorldAutonomyVisualTool? visualTool = null,
        IRecallTelemetrySink? telemetry = null,
        LlmProviderGuard? providerGuard = null,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _llmOptions = llmOptions;
        _stewardSupervisor = stewardSupervisor;
        _agentFactory = agentFactory;
        _ledger = ledger;
        _logger = logger;
        _speechTool = speechTool;
        _visualTool = visualTool;
        _telemetry = telemetry ?? new NoOpTelemetrySink();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _providerGuard = providerGuard ?? new LlmProviderGuard(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LlmProviderGuard>.Instance,
            _timeProvider);
    }

    public async Task<WorldAutonomyRunResult> RunAsync(
        WorldAutonomyOpportunity opportunity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        if (!_configuration.TryGetBinding(opportunity.GuildId, out var binding))
        {
            throw new InvalidOperationException(
                $"Discord guild '{opportunity.GuildId}' has no autonomy binding.");
        }

        var provider = _llmOptions.CurrentValue.GetActiveProvider();
        if (!provider.UseResponsesApi)
        {
            throw new InvalidOperationException(
                "World autonomy requires an LLM provider configured for the OpenAI Responses API and hosted tool search.");
        }

        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new InvalidOperationException("World autonomy requires an API key for the active LLM provider.");
        }

        if (!_providerGuard.TryEnter(out var circuit))
        {
            EmitCircuitEvent(opportunity, "suppressed", circuit.Reason);
            _telemetry.Emit(WorldAutonomyRunTelemetry.Create(
                opportunity,
                context: null,
                model: null,
                status: "provider_circuit_open",
                failureReason: circuit.Reason,
                startedAt: _timeProvider.GetUtcNow(),
                completedAt: _timeProvider.GetUtcNow(),
                activity: null,
                usage: new LlmRunUsageAccumulator().Snapshot()));
            return new WorldAutonomyRunResult(
                RunId: null,
                opportunity.GuildId,
                "provider_circuit_open",
                opportunity.IsDirectAddress ? BuildProviderUnavailableDecree(circuit.Reason) : null,
                circuit.Reason);
        }

        var session = await _stewardSupervisor.GetSessionAsync(opportunity.GuildId, cancellationToken).ConfigureAwait(false);
        var mainProfile = provider.GetProfile(LlmWorkload.Main);
        var model = FirstNonEmpty(opportunity.ModelOverride, binding.Model, mainProfile.Model);
        var context = WorldAutonomyRunContext.Create(
            opportunity.GuildId,
            opportunity.Trigger,
            model,
            session.Catalog.Capabilities.ProfileDigest,
            session.Catalog.Capabilities.ManifestDigest,
            _configuration.RequestIdPoolSize,
            opportunity.SourceMessageId,
            opportunity.SourceEpisodeId,
            opportunity.TraceId,
            opportunity.PersonaDirective,
            opportunity.SourceChannelId,
            opportunity.SourceChannelName,
            opportunity.SourceAuthorId,
            opportunity.SourceAuthorDisplayName);
        var startedAt = _timeProvider.GetUtcNow();
        await _ledger.StartRunAsync(context.ToRunStart(startedAt), cancellationToken).ConfigureAwait(false);
        var usageAccumulator = new LlmRunUsageAccumulator();
        WorldAutonomyRunState? runState = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_configuration.SessionTimeout);
        try
        {
            var catalog = session.Catalog.Bind(context);
            await _ledger.RecordRunEventAsync(
                context.RunId,
                "catalog_bound",
                JsonSerializer.Serialize(new
                {
                    catalogSchemaDigest = catalog.ManifestDigest,
                    toolCount = catalog.Tools.Length,
                    profile = session.Catalog.Capabilities.Profile
                }),
                startedAt,
                cancellationToken).ConfigureAwait(false);

            using var instrumentedClient = new LlmCallTaggingChatClient(
                new TelemetryChatClient(
                    LlmChatClientFactory.Create(provider, model),
                    _llmOptions.CurrentValue.ActiveProvider,
                    _telemetry,
                    _providerGuard,
                    ownsProviderGuardLease: false),
                "world_autonomy",
                mainProfile with { Model = model },
                messageId: ulong.TryParse(opportunity.SourceMessageId, out var sourceMessageId) ? sourceMessageId : null,
                evaluationId: context.RunId,
                usageAccumulator: usageAccumulator);
            runState = new WorldAutonomyRunState(context, _ledger, catalog.Tools, _timeProvider);
            var supplementaryTools = catalog.SupplementaryTools.Cast<AITool>().ToList();
            if (_speechTool is not null && opportunity.SourceChannelId.HasValue)
            {
                supplementaryTools.Add(_speechTool.Bind(
                    opportunity,
                    context,
                    runState,
                    _configuration.TerminalDeliveryEnabled));
            }
            if (_visualTool is not null
                && opportunity.SourceChannelId.HasValue
                && opportunity.VisualIntent != VisualRequestIntent.None)
            {
                supplementaryTools.Add(_visualTool.Bind(
                    opportunity,
                    context,
                    runState,
                    _configuration.TerminalDeliveryEnabled));
            }

            var agent = _agentFactory.Create(
                instrumentedClient,
                runState,
                catalog.Tools,
                supplementaryTools,
                workloadProfile: mainProfile with { Model = model },
                terminalDeliveryEnabled: _configuration.TerminalDeliveryEnabled,
                promptCacheMode: _configuration.PromptCacheMode);
            var agentSession = await agent.CreateSessionAsync().ConfigureAwait(false);
            var response = await agent.RunAsync(
                opportunity.Prompt,
                agentSession,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            if (_visualTool is not null
                && opportunity.VisualIntent != VisualRequestIntent.None
                && !runState.VisualMediumSelected)
            {
                _visualTool.RecordNotSelected(opportunity, context);
            }
            if (_providerGuard.RecordSuccess())
            {
                EmitCircuitEvent(opportunity, "recovered", null);
            }
            var completedAt = _timeProvider.GetUtcNow();
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.Succeeded,
                response.Text,
                failureReason: null,
                completedAt,
                CancellationToken.None).ConfigureAwait(false);
            EmitRunEvent(opportunity, context, model, WorldAutonomyRunStatuses.Succeeded, null, startedAt, completedAt, runState, usageAccumulator);
            return new WorldAutonomyRunResult(
                context.RunId,
                opportunity.GuildId,
                WorldAutonomyRunStatuses.Succeeded,
                response.Text,
                FailureReason: null,
                runState.SpokeInChannel);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            _providerGuard.RecordFailure(new TimeoutException("World autonomy provider probe timed out."));
            var completedAt = _timeProvider.GetUtcNow();
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.TimedOut,
                finalText: null,
                failureReason: "session_timeout",
                completedAt,
                CancellationToken.None).ConfigureAwait(false);
            EmitRunEvent(opportunity, context, model, WorldAutonomyRunStatuses.TimedOut, "session_timeout", startedAt, completedAt, runState, usageAccumulator);
            return new WorldAutonomyRunResult(
                context.RunId,
                opportunity.GuildId,
                WorldAutonomyRunStatuses.TimedOut,
                FinalText: null,
                FailureReason: "session_timeout");
        }
        catch (OperationCanceledException)
        {
            _providerGuard.RecordFailure(new OperationCanceledException("World autonomy provider probe was cancelled."));
            var completedAt = _timeProvider.GetUtcNow();
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.Failed,
                finalText: null,
                failureReason: "cancelled",
                completedAt,
                CancellationToken.None).ConfigureAwait(false);
            await _ledger.RecordRunEventAsync(
                context.RunId,
                "cancelled",
                payloadJson: null,
                completedAt,
                CancellationToken.None).ConfigureAwait(false);
            EmitRunEvent(opportunity, context, model, WorldAutonomyRunStatuses.Failed, "cancelled", startedAt, completedAt, runState, usageAccumulator);
            throw;
        }
        catch (Exception exception)
        {
            var circuitOpened = _providerGuard.RecordFailure(exception);
            if (circuitOpened)
            {
                EmitCircuitEvent(opportunity, "opened", _providerGuard.Snapshot().Reason);
            }
            _logger.LogError(exception, "Autonomy run {RunId} failed for guild {GuildId}.", context.RunId, opportunity.GuildId);
            var completedAt = _timeProvider.GetUtcNow();
            var failureReason = exception is LlmProviderBlockedException blocked
                ? blocked.Reason
                : circuitOpened
                    ? _providerGuard.Snapshot().Reason ?? exception.GetType().Name
                    : exception.GetType().Name;
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.Failed,
                finalText: null,
                failureReason,
                completedAt,
                CancellationToken.None).ConfigureAwait(false);
            EmitRunEvent(opportunity, context, model, WorldAutonomyRunStatuses.Failed, failureReason, startedAt, completedAt, runState, usageAccumulator);
            return new WorldAutonomyRunResult(
                context.RunId,
                opportunity.GuildId,
                WorldAutonomyRunStatuses.Failed,
                FinalText: opportunity.IsDirectAddress &&
                    (circuitOpened || exception is LlmProviderBlockedException)
                        ? BuildProviderUnavailableDecree(failureReason)
                        : null,
                FailureReason: failureReason);
        }
    }

    private void EmitCircuitEvent(WorldAutonomyOpportunity opportunity, string outcome, string? reason) =>
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: _timeProvider.GetUtcNow(),
            EventType: TelemetryEventTypes.WorldAutonomyCircuit,
            Kind: opportunity.IsDirectAddress ? "direct" : "ambient",
            Outcome: outcome,
            MessageId: ulong.TryParse(opportunity.SourceMessageId, out var messageId) ? messageId : null,
            Reason: reason));

    private void EmitRunEvent(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        string model,
        string status,
        string? failureReason,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        WorldAutonomyRunState? runState,
        LlmRunUsageAccumulator usageAccumulator) =>
        _telemetry.Emit(WorldAutonomyRunTelemetry.Create(
            opportunity,
            context,
            model,
            status,
            failureReason,
            startedAt,
            completedAt,
            runState?.ActivitySnapshot,
            usageAccumulator.Snapshot()));

    internal static string BuildProviderUnavailableDecree(string? reason) => reason switch
    {
        "hourly_cost_budget_exhausted" =>
            "The Imperial model audience has reached its hourly spending decree. " +
            "Your petition remains beneath my boot until the hour turns.",
        "daily_cost_budget_exhausted" =>
            "The Imperial model audience has reached its daily spending decree. " +
            "Your petition remains beneath my boot until tomorrow's ledger opens.",
        "credit_balance_exhausted" =>
            "The Imperial model treasury has sealed its vaults while the accountants scream. " +
            "Your petition remains beneath my boot until funding resumes.",
        "authentication_failed" =>
            "The Imperial model seal has failed authentication. " +
            "Your petition remains beneath my boot until the credentials are restored.",
        _ =>
            "The Imperial model court is temporarily unavailable. " +
            "Your petition remains beneath my boot until service resumes.",
    };

    private static string FirstNonEmpty(params string?[] values) => values
        .First(value => !string.IsNullOrWhiteSpace(value))!;
}

internal static class WorldAutonomyRunTelemetry
{
    public static TelemetryEvent Create(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext? context,
        string? model,
        string status,
        string? failureReason,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        WorldAutonomyRunActivitySnapshot? activity,
        LlmRunUsageSnapshot usage) => new(
            Timestamp: completedAt,
            EventType: TelemetryEventTypes.WorldAutonomyRun,
            ChannelHash: opportunity.SourceChannelId.HasValue
                ? UserIdHash.Hash(opportunity.SourceChannelId.Value)
                : null,
            GuildHash: UserIdHash.Hash(opportunity.GuildId),
            Kind: opportunity.IsDirectAddress ? "direct" : "ambient",
            Outcome: status,
            ProviderCallCount: usage.ProviderCallCount,
            NativeReadCount: activity?.NativeReadCount ?? 0,
            NativeWriteCount: activity?.NativeWriteCount ?? 0,
            AcceptedWriteCount: activity?.AcceptedWriteCount ?? 0,
            SucceededWriteCount: activity?.SucceededWriteCount ?? 0,
            FailedWriteCount: activity?.FailedWriteCount ?? 0,
            PartialFailureWriteCount: activity?.PartialFailureWriteCount ?? 0,
            UnknownWriteCount: activity?.UnknownWriteCount ?? 0,
            MessageId: ulong.TryParse(opportunity.SourceMessageId, out var messageId) ? messageId : null,
            Reason: failureReason,
            Model: model,
            LatencyMs: Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds),
            EvaluationId: context?.RunId,
            Workload: "world_autonomy",
            InputTokens: usage.InputTokens,
            OutputTokens: usage.OutputTokens,
            CachedInputTokens: usage.CachedInputTokens,
            CacheWriteInputTokens: usage.CacheWriteInputTokens,
            ReasoningTokens: usage.ReasoningTokens,
            TotalTokens: usage.TotalTokens,
            OperationId: context?.RunId ?? opportunity.TraceId,
            EpisodeId: opportunity.SourceEpisodeId,
            DiscordDelivered: activity?.DiscordDelivered ?? false,
            VisualDelivered: activity?.VisualDelivered ?? false);
}

public sealed class WorldAutonomyRouter
{
    private readonly WorldAutonomyConfiguration _configuration;
    private readonly IWorldAutonomyRunner _runner;
    private readonly ILogger<WorldAutonomyRouter> _logger;
    private readonly WorldAutonomyPostSpeechGuard? _postSpeechGuard;
    private readonly ConcurrentDictionary<ulong, GuildMailbox> _guildMailboxes = new();

    public WorldAutonomyRouter(
        WorldAutonomyConfiguration configuration,
        IWorldAutonomyRunner runner,
        ILogger<WorldAutonomyRouter> logger,
        WorldAutonomyPostSpeechGuard? postSpeechGuard = null)
    {
        _configuration = configuration;
        _runner = runner;
        _logger = logger;
        _postSpeechGuard = postSpeechGuard;
    }

    /// <summary>
    /// True when this guild has granted Robotnik real administrative control. Callers use this to avoid
    /// doing autonomy-only work for the guilds that have not.
    /// </summary>
    public bool IsEnabled(ulong guildId) => _configuration.TryGetBinding(guildId, out _);

    public void RecordDeliveredSpeech(ulong guildId, ulong channelId) =>
        _postSpeechGuard?.RecordSpeech(guildId, channelId);

    /// <summary>
    /// Fire-and-forget entry point for an ambient episode that already passed admission. While a run is active,
    /// only the newest waiting ambient episode is retained and direct audiences keep priority.
    /// </summary>
    public Task TryRunAsync(WorldAutonomyOpportunity opportunity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        if (!_configuration.TryGetBinding(opportunity.GuildId, out _))
        {
            return Task.CompletedTask;
        }

        var mailbox = _guildMailboxes.GetOrAdd(opportunity.GuildId, _ => new GuildMailbox());
        Task? worker = null;
        lock (mailbox.Gate)
        {
            mailbox.Ambient = new PendingOpportunity(opportunity, cancellationToken, Completion: null);
            if (!mailbox.WorkerRunning)
            {
                mailbox.WorkerRunning = true;
                worker = ProcessMailboxAsync(mailbox, opportunity.GuildId);
            }
        }

        if (worker is null)
        {
            _logger.LogDebug(
                "Replaced pending ambient autonomy episode for guild {GuildId}, trigger {Trigger}.",
                opportunity.GuildId,
                opportunity.Trigger);
            return Task.CompletedTask;
        }

        return worker;
    }

    /// <summary>
    /// Entry point for a message addressed straight at Robotnik. Every accepted audience is preserved in
    /// arrival order and this task completes with that audience's own run result. A busy guild therefore
    /// remains owned by autonomy instead of leaking the same message into the ordinary persona path.
    /// </summary>
    public async Task<WorldAutonomyRunResult?> TryRunDirectAsync(
        WorldAutonomyOpportunity opportunity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        if (!_configuration.TryGetBinding(opportunity.GuildId, out _))
        {
            return null;
        }

        var completion = new TaskCompletionSource<WorldAutonomyRunResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mailbox = _guildMailboxes.GetOrAdd(opportunity.GuildId, _ => new GuildMailbox());
        var queued = false;
        lock (mailbox.Gate)
        {
            queued = mailbox.WorkerRunning;
            mailbox.Direct.Enqueue(new PendingOpportunity(opportunity, cancellationToken, completion));
            mailbox.Ambient = null;
            if (!mailbox.WorkerRunning)
            {
                mailbox.WorkerRunning = true;
                _ = ProcessMailboxAsync(mailbox, opportunity.GuildId);
            }
        }

        if (queued)
        {
            _logger.LogDebug(
                "Queued direct autonomy audience for guild {GuildId}, source message {SourceMessageId}.",
                opportunity.GuildId,
                opportunity.SourceMessageId);
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task ProcessMailboxAsync(GuildMailbox mailbox, ulong guildId)
    {
        while (true)
        {
            PendingOpportunity? pending = null;
            lock (mailbox.Gate)
            {
                if (mailbox.Direct.Count > 0)
                {
                    pending = mailbox.Direct.Dequeue();
                }
                else if (mailbox.Ambient is not null)
                {
                    pending = mailbox.Ambient;
                    mailbox.Ambient = null;
                }
                else
                {
                    mailbox.WorkerRunning = false;
                    return;
                }
            }

            var selected = pending!;
            var result = await RunOpportunityAsync(selected, guildId).ConfigureAwait(false);
            selected.Completion?.TrySetResult(result);
        }
    }

    private async Task<WorldAutonomyRunResult?> RunOpportunityAsync(
        PendingOpportunity pending,
        ulong guildId)
    {
        try
        {
            var result = await _runner.RunAsync(pending.Opportunity, pending.CancellationToken).ConfigureAwait(false);
            if (result.SpokeInChannel && pending.Opportunity.SourceChannelId is { } channelId)
            {
                RecordDeliveredSpeech(guildId, channelId);
            }
            _logger.LogInformation(
                "Autonomy run {RunId} completed for guild {GuildId} with status {Status}.",
                result.RunId,
                guildId,
                result.Status);
            return result;
        }
        catch (OperationCanceledException) when (pending.CancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Autonomy opportunity cancelled for guild {GuildId}.", guildId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Autonomy opportunity failed for guild {GuildId}.", guildId);
        }

        return null;
    }

    private sealed class GuildMailbox
    {
        public object Gate { get; } = new();

        public Queue<PendingOpportunity> Direct { get; } = new();

        public PendingOpportunity? Ambient { get; set; }

        public bool WorkerRunning { get; set; }
    }

    private sealed record PendingOpportunity(
        WorldAutonomyOpportunity Opportunity,
        CancellationToken CancellationToken,
        TaskCompletionSource<WorldAutonomyRunResult?>? Completion);
}