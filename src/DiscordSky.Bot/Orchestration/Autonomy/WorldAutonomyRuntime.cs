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
    private readonly WorldAutonomyProviderCircuit _providerCircuit;
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
        WorldAutonomyProviderCircuit? providerCircuit = null,
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
        _providerCircuit = providerCircuit ?? new WorldAutonomyProviderCircuit(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldAutonomyProviderCircuit>.Instance,
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

        if (!_providerCircuit.TryEnter(out var circuit))
        {
            EmitCircuitEvent(opportunity, "suppressed", circuit.Reason);
            return new WorldAutonomyRunResult(
                RunId: null,
                opportunity.GuildId,
                "provider_circuit_open",
                opportunity.IsDirectAddress ? BuildProviderUnavailableDecree() : null,
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
                    _telemetry),
                "world_autonomy",
                mainProfile with { Model = model },
                messageId: ulong.TryParse(opportunity.SourceMessageId, out var sourceMessageId) ? sourceMessageId : null,
                evaluationId: context.RunId);
            var runState = new WorldAutonomyRunState(context, _ledger, catalog.Tools, _timeProvider);
            var supplementaryTools = catalog.SupplementaryTools.Cast<AITool>().ToList();
            if (_speechTool is not null && opportunity.SourceChannelId.HasValue)
            {
                supplementaryTools.Add(_speechTool.Bind(opportunity, context, runState));
            }
            if (_visualTool is not null
                && opportunity.SourceChannelId.HasValue
                && opportunity.VisualIntent != VisualRequestIntent.None)
            {
                supplementaryTools.Add(_visualTool.Bind(opportunity, context, runState));
            }

            var agent = _agentFactory.Create(
                instrumentedClient,
                runState,
                catalog.Tools,
                supplementaryTools,
                workloadProfile: mainProfile with { Model = model });
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
            if (_providerCircuit.RecordSuccess())
            {
                EmitCircuitEvent(opportunity, "recovered", null);
            }
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.Succeeded,
                response.Text,
                failureReason: null,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
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
            _providerCircuit.RecordFailure(new TimeoutException("World autonomy provider probe timed out."));
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.TimedOut,
                finalText: null,
                failureReason: "session_timeout",
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            return new WorldAutonomyRunResult(
                context.RunId,
                opportunity.GuildId,
                WorldAutonomyRunStatuses.TimedOut,
                FinalText: null,
                FailureReason: "session_timeout");
        }
        catch (OperationCanceledException)
        {
            _providerCircuit.RecordFailure(new OperationCanceledException("World autonomy provider probe was cancelled."));
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.Failed,
                finalText: null,
                failureReason: "cancelled",
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            await _ledger.RecordRunEventAsync(
                context.RunId,
                "cancelled",
                payloadJson: null,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var circuitOpened = _providerCircuit.RecordFailure(exception);
            if (circuitOpened)
            {
                EmitCircuitEvent(opportunity, "opened", _providerCircuit.Snapshot().Reason);
            }
            _logger.LogError(exception, "Autonomy run {RunId} failed for guild {GuildId}.", context.RunId, opportunity.GuildId);
            await _ledger.CompleteRunAsync(
                context.RunId,
                WorldAutonomyRunStatuses.Failed,
                finalText: null,
                failureReason: exception.GetType().Name,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            return new WorldAutonomyRunResult(
                context.RunId,
                opportunity.GuildId,
                WorldAutonomyRunStatuses.Failed,
                FinalText: circuitOpened && opportunity.IsDirectAddress ? BuildProviderUnavailableDecree() : null,
                FailureReason: exception.GetType().Name);
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

    private static string BuildProviderUnavailableDecree() =>
        "The Imperial model treasury has sealed its vaults while the accountants scream. " +
        "Your petition remains beneath my boot until funding resumes.";

    private static string FirstNonEmpty(params string?[] values) => values
        .First(value => !string.IsNullOrWhiteSpace(value))!;
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
    /// Fire-and-forget entry point for ambient opportunities. A burst coalesces to the newest room state,
    /// while every direct audience already waiting for Robotnik keeps its place ahead of ambient business.
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
        CancellationTokenSource? debounceToReset;
        lock (mailbox.Gate)
        {
            mailbox.Ambient = new PendingOpportunity(opportunity, cancellationToken, Completion: null);
            mailbox.AmbientReady = !_configuration.AmbientEpisodeCoalescingEnabled;
            mailbox.AmbientVersion++;
            debounceToReset = mailbox.AmbientDelay;
            if (!mailbox.WorkerRunning)
            {
                mailbox.WorkerRunning = true;
                worker = ProcessMailboxAsync(mailbox, opportunity.GuildId);
            }
        }
        TryCancel(debounceToReset);

        if (worker is null)
        {
            _logger.LogDebug(
                "Coalesced ambient autonomy opportunity for guild {GuildId}, trigger {Trigger}.",
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
        CancellationTokenSource? debounceToCancel;
        lock (mailbox.Gate)
        {
            queued = mailbox.WorkerRunning;
            mailbox.Direct.Enqueue(new PendingOpportunity(opportunity, cancellationToken, completion));
            mailbox.Ambient = null;
            mailbox.AmbientReady = false;
            mailbox.AmbientVersion++;
            debounceToCancel = mailbox.AmbientDelay;
            if (!mailbox.WorkerRunning)
            {
                mailbox.WorkerRunning = true;
                _ = ProcessMailboxAsync(mailbox, opportunity.GuildId);
            }
        }
        TryCancel(debounceToCancel);

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
            CancellationTokenSource? ambientDelay = null;
            long ambientVersion = 0;
            lock (mailbox.Gate)
            {
                if (mailbox.Direct.Count > 0)
                {
                    pending = mailbox.Direct.Dequeue();
                }
                else if (mailbox.Ambient is not null)
                {
                    if (_configuration.AmbientEpisodeCoalescingEnabled && !mailbox.AmbientReady)
                    {
                        ambientDelay = new CancellationTokenSource();
                        mailbox.AmbientDelay = ambientDelay;
                        ambientVersion = mailbox.AmbientVersion;
                    }
                    else
                    {
                        pending = mailbox.Ambient;
                        mailbox.Ambient = null;
                        mailbox.AmbientReady = false;
                    }
                }
                else
                {
                    mailbox.WorkerRunning = false;
                    return;
                }
            }

            if (ambientDelay is not null)
            {
                var elapsed = false;
                try
                {
                    await Task.Delay(_configuration.AmbientEpisodeWindow, ambientDelay.Token).ConfigureAwait(false);
                    elapsed = true;
                }
                catch (OperationCanceledException) when (ambientDelay.IsCancellationRequested)
                {
                }

                lock (mailbox.Gate)
                {
                    if (ReferenceEquals(mailbox.AmbientDelay, ambientDelay))
                    {
                        mailbox.AmbientDelay = null;
                    }
                    if (elapsed && mailbox.AmbientVersion == ambientVersion && mailbox.Ambient is not null)
                    {
                        mailbox.AmbientReady = true;
                    }
                }
                ambientDelay.Dispose();
                continue;
            }

            var selected = pending!;
            var result = await RunOpportunityAsync(selected, guildId).ConfigureAwait(false);
            selected.Completion?.TrySetResult(result);
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
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

        public bool AmbientReady { get; set; }

        public long AmbientVersion { get; set; }

        public CancellationTokenSource? AmbientDelay { get; set; }

        public bool WorkerRunning { get; set; }
    }

    private sealed record PendingOpportunity(
        WorldAutonomyOpportunity Opportunity,
        CancellationToken CancellationToken,
        TaskCompletionSource<WorldAutonomyRunResult?>? Completion);
}