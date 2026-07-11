using System.Threading.Channels;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Impulse;

public sealed record ColdOpenShadowOpportunity(
    string EvaluationId,
    DateTimeOffset CapturedAt,
    string Channel,
    ColdOpenContext Context,
    ColdOpenDraft? ChampionDraft,
    double WorthThreshold,
    IReadOnlyList<string> RoomLines);

public interface IColdOpenShadowSink
{
    bool TryEnqueue(ColdOpenShadowOpportunity opportunity);
}

/// <summary>
/// Evaluates Grok on the same pre-post cold-open snapshot as the OpenAI champion. The bounded queue never blocks
/// the production path, and the worker owns no Discord client, memory store, or mutating tool surface.
/// </summary>
public sealed class GrokColdOpenShadowService : BackgroundService, IColdOpenShadowSink
{
    private readonly GrokColdOpenShadowOptions _options;
    private readonly Channel<ColdOpenShadowOpportunity> _queue;
    private readonly Func<ColdOpenContext, CancellationToken, Task<ColdOpenDraft?>>? _compose;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<GrokColdOpenShadowService> _logger;
    private readonly Func<double> _nextSample;

    public GrokColdOpenShadowService(
        IOptions<ModelEvaluationOptions> evaluationOptions,
        IOptions<LlmOptions> llmOptions,
        IRecallTelemetrySink telemetry,
        ILoggerFactory loggerFactory,
        ILogger<GrokColdOpenShadowService> logger)
        : this(
            evaluationOptions.Value.GrokColdOpen,
            BuildComposer(evaluationOptions.Value.GrokColdOpen, llmOptions.Value, loggerFactory),
            telemetry,
            logger,
            () => Random.Shared.NextDouble())
    {
    }

    internal GrokColdOpenShadowService(
        GrokColdOpenShadowOptions options,
        Func<ColdOpenContext, CancellationToken, Task<ColdOpenDraft?>>? compose,
        IRecallTelemetrySink telemetry,
        ILogger<GrokColdOpenShadowService> logger,
        Func<double> nextSample)
    {
        _options = options;
        _compose = compose;
        _telemetry = telemetry;
        _logger = logger;
        _nextSample = nextSample;
        _queue = Channel.CreateBounded<ColdOpenShadowOpportunity>(new BoundedChannelOptions(
            Math.Clamp(options.QueueCapacity, 1, 1024))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public bool TryEnqueue(ColdOpenShadowOpportunity opportunity)
    {
        if (!_options.Enabled || _compose is null) return false;
        if (_nextSample() >= Math.Clamp(_options.SampleRate, 0.0, 1.0)) return false;

        var snapshot = opportunity with
        {
            Context = new ColdOpenContext(
                opportunity.Context.PersonaName,
                opportunity.Context.MoodLabel,
                opportunity.Context.SituationLog,
                opportunity.Context.RecentPeople.ToArray(),
                opportunity.Context.RecentLines?.ToArray()),
            RoomLines = opportunity.RoomLines.ToArray(),
        };
        if (_queue.Writer.TryWrite(snapshot)) return true;

        Emit(snapshot, null, "dropped", null);
        _logger.LogWarning("cold_open_provider_shadow outcome=dropped provider={Provider} model={Model} channel={Channel}",
            _options.ProviderName, _options.Model, opportunity.Channel);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _compose is null)
        {
            _logger.LogInformation("Grok cold-open shadow disabled.");
            return;
        }

        _logger.LogInformation(
            "Grok cold-open shadow enabled: provider={Provider} model={Model} effort={Effort} sample={Sample:P0} queue={Queue}.",
            _options.ProviderName,
            _options.Model,
            _options.ReasoningEffort,
            Math.Clamp(_options.SampleRate, 0.0, 1.0),
            Math.Clamp(_options.QueueCapacity, 1, 1024));

        try
        {
            await foreach (var opportunity in _queue.Reader.ReadAllAsync(stoppingToken))
                await EvaluateAsync(opportunity, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    private async Task EvaluateAsync(ColdOpenShadowOpportunity opportunity, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var draft = await _compose!(opportunity.Context, cancellationToken);
            timer.Stop();
            var outcome = draft is null
                ? "declined"
                : draft.Worth >= opportunity.WorthThreshold ? "would_post" : "below_threshold";
            Emit(opportunity, draft, outcome, timer.ElapsedMilliseconds);
            _logger.LogInformation(
                "cold_open_provider_shadow outcome={Outcome} provider={Provider} model={Model} worth={Worth:F2} baseline={Baseline} latency_ms={Latency} channel={Channel}",
                outcome,
                _options.ProviderName,
                _options.Model,
                draft?.Worth ?? 0.0,
                ChampionOutcome(opportunity.ChampionDraft, opportunity.WorthThreshold),
                timer.ElapsedMilliseconds,
                opportunity.Channel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();
            Emit(opportunity, null, "failed", timer.ElapsedMilliseconds);
            _logger.LogWarning(ex,
                "cold_open_provider_shadow outcome=failed provider={Provider} model={Model} channel={Channel}",
                _options.ProviderName, _options.Model, opportunity.Channel);
        }
    }

    private void Emit(
        ColdOpenShadowOpportunity opportunity,
        ColdOpenDraft? draft,
        string outcome,
        long? latencyMs)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: TelemetryEventTypes.ColdOpenProviderShadow,
            Channel: opportunity.Channel,
            Kind: string.IsNullOrWhiteSpace(draft?.Hook) ? null : draft.Hook,
            Outcome: outcome,
            TopScore: draft?.Worth,
            Note: string.IsNullOrWhiteSpace(draft?.Line) ? null : draft.Line,
            Room: opportunity.RoomLines.Count > 0 ? opportunity.RoomLines : null,
            Provider: _options.ProviderName,
            Model: _options.Model,
            ReasoningEffort: _options.ReasoningEffort,
            LatencyMs: latencyMs,
            BaselineOutcome: ChampionOutcome(opportunity.ChampionDraft, opportunity.WorthThreshold),
            BaselineScore: opportunity.ChampionDraft?.Worth,
            EvaluationId: opportunity.EvaluationId,
            OpportunityAt: opportunity.CapturedAt));
    }

    internal static string ChampionOutcome(ColdOpenDraft? draft, double threshold) => draft is null
        ? "declined"
        : draft.Worth >= threshold ? "would_post" : "below_threshold";

    private static Func<ColdOpenContext, CancellationToken, Task<ColdOpenDraft?>>? BuildComposer(
        GrokColdOpenShadowOptions options,
        LlmOptions llmOptions,
        ILoggerFactory loggerFactory)
    {
        if (!options.Enabled) return null;
        if (!options.ProviderName.Equals("xAI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Grok cold-open shadow must use the configured xAI provider.");
        if (!llmOptions.Providers.TryGetValue(options.ProviderName, out var provider))
            throw new InvalidOperationException($"Grok cold-open shadow provider '{options.ProviderName}' is not configured.");
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException($"Grok cold-open shadow provider '{options.ProviderName}' has no API key.");
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("Grok cold-open shadow model is required.");
        if (!Enum.TryParse<Microsoft.Extensions.AI.ReasoningEffort>(options.ReasoningEffort, true, out _)
            || options.ReasoningEffort.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Grok 4.5 reasoning effort must be low, medium, or high.");
        }

        var client = LlmChatClientFactory.Create(provider, options.Model);
        var composer = new ColdOpenComposer(
            client,
            new LlmWorkloadProfile(options.Model, options.ReasoningEffort),
            loggerFactory.CreateLogger<ColdOpenComposer>(),
            surfaceFailures: true);
        return composer.ComposeAsync;
    }
}