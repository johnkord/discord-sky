using System.Collections.Concurrent;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
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
    string? SourceAuthorDisplayName = null);

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
    private readonly TimeProvider _timeProvider;

    public WorldAutonomyOrchestrator(
        WorldAutonomyConfiguration configuration,
        IOptionsMonitor<LlmOptions> llmOptions,
        StewardMcpSupervisor stewardSupervisor,
        WorldAutonomyAgentFactory agentFactory,
        IWorldAutonomyLedger ledger,
        ILogger<WorldAutonomyOrchestrator> logger,
        WorldAutonomySpeechTool? speechTool = null,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _llmOptions = llmOptions;
        _stewardSupervisor = stewardSupervisor;
        _agentFactory = agentFactory;
        _ledger = ledger;
        _logger = logger;
        _speechTool = speechTool;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            using var rawClient = LlmChatClientFactory.Create(provider, model);
            var runState = new WorldAutonomyRunState(context, _ledger, catalog.Tools, _timeProvider);
            var supplementaryTools = catalog.SupplementaryTools.Cast<AITool>().ToList();
            if (_speechTool is not null && opportunity.SourceChannelId.HasValue)
            {
                supplementaryTools.Add(_speechTool.Bind(opportunity, context, runState));
            }

            var agent = _agentFactory.Create(
                rawClient,
                runState,
                catalog.Tools,
                supplementaryTools,
                workloadProfile: mainProfile with { Model = model });
            var agentSession = await agent.CreateSessionAsync().ConfigureAwait(false);
            var response = await agent.RunAsync(
                opportunity.Prompt,
                agentSession,
                cancellationToken: timeout.Token).ConfigureAwait(false);
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
                FinalText: null,
                FailureReason: exception.GetType().Name);
        }
    }

    private static string FirstNonEmpty(params string?[] values) => values
        .First(value => !string.IsNullOrWhiteSpace(value))!;
}

public sealed class WorldAutonomyRouter
{
    private readonly WorldAutonomyConfiguration _configuration;
    private readonly IWorldAutonomyRunner _runner;
    private readonly ILogger<WorldAutonomyRouter> _logger;
    private readonly ConcurrentDictionary<ulong, GuildMailbox> _guildMailboxes = new();

    public WorldAutonomyRouter(
        WorldAutonomyConfiguration configuration,
        IWorldAutonomyRunner runner,
        ILogger<WorldAutonomyRouter> logger)
    {
        _configuration = configuration;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// True when this guild has granted Robotnik real administrative control. Callers use this to avoid
    /// doing autonomy-only work for the guilds that have not.
    /// </summary>
    public bool IsEnabled(ulong guildId) => _configuration.TryGetBinding(guildId, out _);

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
        lock (mailbox.Gate)
        {
            queued = mailbox.WorkerRunning;
            mailbox.Direct.Enqueue(new PendingOpportunity(opportunity, cancellationToken, completion));
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
            PendingOpportunity? pending;
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

            var result = await RunOpportunityAsync(pending, guildId).ConfigureAwait(false);
            pending.Completion?.TrySetResult(result);
        }
    }

    private async Task<WorldAutonomyRunResult?> RunOpportunityAsync(
        PendingOpportunity pending,
        ulong guildId)
    {
        try
        {
            var result = await _runner.RunAsync(pending.Opportunity, pending.CancellationToken).ConfigureAwait(false);
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