using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.Images;

/// <summary>The result of one generation attempt through <see cref="ImageToolService"/>.</summary>
public sealed record ImageGenerationOutcome(bool Generated, byte[]? Bytes, string? FileName, string? RefusalText)
{
    public static ImageGenerationOutcome Ok(byte[] bytes, string fileName) => new(true, bytes, fileName, null);
    public static ImageGenerationOutcome Refused(string refusalText) => new(false, null, null, refusalText);
}

/// <summary>Non-content metadata that links an image opportunity to the interaction that created it.</summary>
public sealed record ImageGenerationContext(
    string Source,
    string InvocationKind,
    ulong? TriggerMessageId = null,
    string? OpportunityId = null,
    bool? ToolOffered = null,
    bool? ToolSelected = null,
    double? VisualWorth = null,
    ulong? GuildId = null,
    IReadOnlyList<ulong>? EvidenceMessageIds = null,
    string? PromptDigest = null,
    ulong? ChannelId = null)
{
    public const string SourceAmbientVisual = "ambient_visual_impulse";
    public static readonly ImageGenerationContext Unknown = new("unknown", "unknown");
}

/// <summary>
/// The shared core of image generation, used by BOTH the <c>!sky(image)</c> command (Phase 1) and the
/// model-decided <c>generate_image</c> tool (Phase 2). It owns everything between "we have a prompt" and
/// "we have bytes or a refusal": the budget gate, the mandatory style suffix, the API call, and the
/// durable log. Callers supply the already-decided image prompt; what text accompanies the image is the
/// caller's business.
///
/// <para>Pulling this out of the command handler keeps the two trigger paths from duplicating the caps,
/// the cost accounting, and the safety-by-style decision.</para>
/// </summary>
public sealed class ImageToolService
{
    internal const int MaxLoggedPromptChars = 3_000;

    /// <summary>
    /// Appended to every image prompt. Mandating a 1990s cartoon look anchors the character's appearance,
    /// nails the aesthetic, and is a safety lever (a cartoon is far lower-risk than a photoreal image).
    /// Applied here, downstream of both trigger paths, so the model-authored prompt gets it too.
    /// </summary>
    public const string StyleSuffix =
        "Art style: a vibrant 1990s Saturday-morning cartoon cel illustration in the style of " +
        "Adventures of Sonic the Hedgehog (1993): bold clean black outlines, flat saturated colors, " +
        "exaggerated comic expressions, simple painted background. Absolutely not photorealistic, " +
        "not a photograph, not 3D render.";

    private readonly ImageBudget _budget;
    private readonly IImageGenerator _generator;
    private readonly IImageGenerationLog _log;
    private readonly ImageOptions _options;
    private readonly ILogger<ImageToolService> _logger;
    private readonly IImageReferenceResolver? _references;

    public ImageToolService(
        ImageBudget budget,
        IImageGenerator generator,
        IImageGenerationLog log,
        IOptions<ImageOptions> options,
        ILogger<ImageToolService> logger,
        IImageReferenceResolver? references = null)
    {
        _budget = budget;
        _generator = generator;
        _log = log;
        _options = options.Value;
        ImageRequestOptions.FromConfig(_options);
        ImageRequestOptions.FromConfig(_options, editing: true);
        if (_options.MaxReferenceImages is < 1 or > 8 || _options.MaxReferenceBytes is < 1024 or > 20 * 1024 * 1024 ||
            _options.MaxTotalReferenceBytes < _options.MaxReferenceBytes || _options.MaxTotalReferenceBytes > 32 * 1024 * 1024)
            throw new InvalidOperationException("Image reference limits are invalid or exceed the host memory envelope.");
        _logger = logger;
        _references = references;
    }

    /// <summary>True when a real backend is wired (enabled + an API key was found).</summary>
    public bool IsEnabled => _generator.IsEnabled;

    /// <summary>
    /// Runs the budget gate, appends the style suffix, generates, and logs the outcome. Returns the image
    /// bytes on success or an in-character refusal string on any non-drawing outcome. The caller owns the
    /// caption and the actual Discord send.
    /// </summary>
    public async Task<ImageGenerationOutcome> GenerateAsync(
        ulong userId,
        string? channelName,
        string imagePrompt,
        ImageTier tier,
        CancellationToken cancellationToken,
        ImageGenerationContext? context = null,
        ImageRenderSettings? settings = null,
        Func<ImagePreview, CancellationToken, Task>? onPreview = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var requestOptions = ImageRequestOptions.FromConfig(_options, tier);
        context ??= ImageGenerationContext.Unknown;

        if (string.IsNullOrWhiteSpace(imagePrompt))
        {
            Record(channelName, userId, requestOptions, tier, context, 0.0, startedAt,
                ImageGenerationRecord.OutcomeRefused, "empty_prompt");
            return ImageGenerationOutcome.Refused(ImageRefusals.GenericRefusal);
        }

        var lease = _budget.TryBegin(userId);
        if (!lease.Allowed)
        {
            _logger.LogInformation("image_refused reason={Reason} user={User}", lease.Reason, userId);
            Record(channelName, userId, requestOptions, tier, context, 0.0, startedAt,
                ImageGenerationRecord.OutcomeRefused, BudgetReason(lease.Reason));
            return ImageGenerationOutcome.Refused(ImageRefusals.ForBudget(lease.Reason));
        }

        var finalPrompt = imagePrompt.Trim() + " " + StyleSuffix;
        ImageReferenceSet references = ImageReferenceSet.Empty;
        var action = "generate";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.RequestTimeoutMinutes, 1, 10)));

        try
        {
            if (!string.Equals(settings?.Action, "generate", StringComparison.OrdinalIgnoreCase) && _references is not null && _options.EditingEnabled)
                references = await _references.ResolveAsync(context, timeout.Token).ConfigureAwait(false);
            action = ImageRenderPolicy.ResolveAction(settings?.Action, references.Images.Count > 0);
            requestOptions = ImageRequestOptions.FromConfig(_options, tier, settings, editing: action == "edit");
            if (action == "edit")
            {
                if (!_options.EditingEnabled) throw new ArgumentException("Image editing is disabled.");
                finalPrompt += " Preserve the supplied composition and subjects except for the requested changes.";
            }
            if (requestOptions.Background == "transparent")
                finalPrompt += " Keep the background transparent; do not paint a backdrop or transparency checkerboard.";
            if (onPreview is null) requestOptions = requestOptions with { PartialImages = 0 };
            var result = await _generator.RenderAsync(new ImageRenderRequest(finalPrompt, requestOptions,
                action == "edit" ? references.Images : null, references.Mask, onPreview), timeout.Token).ConfigureAwait(false);
            var estCost = result.CostUsd ?? (result.Success ? ImageCost.Estimate(requestOptions.Model, requestOptions.Quality) : 0);

            if (!result.Success || result.Bytes is null || result.Bytes.Length == 0)
            {
                var outcome = result.Error == ImageResult.ErrorModerationBlocked
                    ? ImageGenerationRecord.OutcomeModerationBlocked
                    : ImageGenerationRecord.OutcomeError;
                Record(
                    channelName,
                    userId,
                    requestOptions,
                    tier,
                    context,
                    estCost,
                    startedAt,
                    outcome,
                    result.Error,
                    finalPrompt, result, action, references);
                return ImageGenerationOutcome.Refused(ImageRefusals.ForError(result.Error));
            }

            Record(
                channelName,
                userId,
                requestOptions,
                tier,
                context,
                estCost,
                startedAt,
                ImageGenerationRecord.OutcomeOk,
                finalPrompt: finalPrompt, result: result, action: action, references: references);
            _logger.LogInformation(
                "image_generated user={User} model={Model} quality={Quality} est_cost={Cost:F3} latency_ms={Latency}",
                userId, requestOptions.Model, requestOptions.Quality, estCost,
                (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

            return ImageGenerationOutcome.Ok(result.Bytes, $"robotnik.{result.FileExtension}");
        }
        catch (ArgumentException exception)
        {
            Record(channelName, userId, requestOptions, tier, context, 0, startedAt,
                ImageGenerationRecord.OutcomeRefused, "invalid_request");
            return ImageGenerationOutcome.Refused("The Foundry needs a correction: " + exception.Message);
        }
        catch (HttpRequestException)
        {
            Record(channelName, userId, requestOptions, tier, context, 0, startedAt,
                ImageGenerationRecord.OutcomeError, "reference_download_failed");
            return ImageGenerationOutcome.Refused("The Foundry could not retrieve that source image. Attach it again before another attempt.");
        }
        catch (OperationCanceledException exception)
        {
            var result = (exception as ImageGenerationCanceledException)?.Result;
            Record(channelName, userId, requestOptions, tier, context, result?.CostUsd ?? 0, startedAt,
                ImageGenerationRecord.OutcomeCancelled, "operation_cancelled", finalPrompt, result, action, references);
            if (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                return ImageGenerationOutcome.Refused("The Foundry's time is up. That render did not finish; it may still have incurred a charge.");
            throw;
        }
        catch (Exception ex)
        {
            Record(channelName, userId, requestOptions, tier, context, 0.0, startedAt,
                ImageGenerationRecord.OutcomeError, ex.GetType().Name, finalPrompt);
            throw;
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Records a creative turn where image generation was available but no render was attempted.</summary>
    public void RecordOpportunity(
        ulong userId,
        string? channelName,
        ImageTier tier,
        ImageGenerationContext context)
    {
        var requestOptions = ImageRequestOptions.FromConfig(_options, tier);
        var outcome = context.ToolOffered == true
            ? ImageGenerationRecord.OutcomeNotSelected
            : ImageGenerationRecord.OutcomeNotOffered;
        Record(channelName, userId, requestOptions, tier, context, 0.0, DateTimeOffset.UtcNow, outcome);
    }

    private void Record(
        string? channelName,
        ulong userId,
        ImageRequestOptions options,
        ImageTier tier,
        ImageGenerationContext context,
        double estCost,
        DateTimeOffset startedAt,
        string outcome,
        string? reason = null,
        string? finalPrompt = null,
        ImageResult? result = null,
        string? action = null,
        ImageReferenceSet? references = null)
    {
        _log.Record(new ImageGenerationRecord(
            Timestamp: DateTimeOffset.UtcNow,
            Channel: channelName,
            UserHash: UserIdHash.Hash(userId),
            Model: options.Model,
            Size: result?.ActualSize ?? options.Size,
            Quality: result?.ActualQuality ?? options.Quality,
            EstCostUsd: estCost,
            LatencyMs: (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            Outcome: outcome,
            Source: context.Source,
            InvocationKind: context.InvocationKind,
            Tier: tier.ToString().ToLowerInvariant(),
            TriggerMessageId: context.TriggerMessageId,
            OpportunityId: context.OpportunityId,
            ToolOffered: context.ToolOffered,
            ToolSelected: context.ToolSelected,
            VisualWorth: context.VisualWorth,
            Reason: reason,
            GuildId: context.GuildId,
            EvidenceMessageIds: context.EvidenceMessageIds,
            PromptDigest: context.PromptDigest,
            FinalPrompt: BoundPrompt(finalPrompt),
            Action: action,
            ReferenceMessageIds: references?.Images.Select(image => image.MessageId).OfType<ulong>().Distinct().ToArray(),
            ReferenceCount: references?.Images.Count,
            HasMask: references?.Mask is not null,
            OutputFormat: options.OutputFormat,
            Background: options.Background,
            OutputCompression: options.OutputCompression,
            PreviewCount: result?.PreviewCount,
            Usage: result?.Usage,
            CostBasis: result?.CostBasis,
            RequestId: result?.RequestId,
            HttpStatus: result?.HttpStatus,
            ProviderErrorCode: result?.ProviderErrorCode,
            ModerationStage: result?.ModerationStage,
            ModerationCategories: result?.ModerationCategories));
    }

    internal static string? BoundPrompt(string? prompt)
    {
        var normalized = prompt?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized.Length <= MaxLoggedPromptChars
            ? normalized
            : normalized[..MaxLoggedPromptChars];
    }

    private static string BudgetReason(BudgetRefusalReason reason) => reason switch
    {
        BudgetRefusalReason.UserHourlyLimit => "user_hourly_limit",
        BudgetRefusalReason.DailyLimit => "daily_limit",
        BudgetRefusalReason.MonthlyGuard => "monthly_guard",
        BudgetRefusalReason.ConcurrencyBusy => "concurrency_busy",
        _ => "budget_refused",
    };
}
