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

    public ImageToolService(
        ImageBudget budget,
        IImageGenerator generator,
        IImageGenerationLog log,
        IOptions<ImageOptions> options,
        ILogger<ImageToolService> logger)
    {
        _budget = budget;
        _generator = generator;
        _log = log;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>True when a real backend is wired (enabled + an API key was found).</summary>
    public bool IsEnabled => _generator.IsEnabled;

    /// <summary>Probability the tool is offered on an ambient interjection (see <see cref="ImageOptions.AmbientChance"/>).</summary>
    public double AmbientChance => _options.AmbientChance;

    /// <summary>
    /// Runs the budget gate, appends the style suffix, generates, and logs the outcome. Returns the image
    /// bytes on success or an in-character refusal string on any non-drawing outcome. The caller owns the
    /// caption and the actual Discord send.
    /// </summary>
    public async Task<ImageGenerationOutcome> GenerateAsync(
        ulong userId, string? channelName, string imagePrompt, ImageTier tier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePrompt))
        {
            return ImageGenerationOutcome.Refused(ImageRefusals.GenericRefusal);
        }

        var lease = _budget.TryBegin(userId);
        if (!lease.Allowed)
        {
            _logger.LogInformation("image_refused reason={Reason} user={User}", lease.Reason, userId);
            return ImageGenerationOutcome.Refused(ImageRefusals.ForBudget(lease.Reason));
        }

        var startedAt = DateTimeOffset.UtcNow;
        var requestOptions = ImageRequestOptions.FromConfig(_options, tier);
        var estCost = ImageCost.Estimate(requestOptions.Model, requestOptions.Quality);

        try
        {
            var finalPrompt = imagePrompt.Trim() + " " + StyleSuffix;
            var result = await _generator.GenerateAsync(finalPrompt, requestOptions, cancellationToken);

            if (!result.Success || result.Bytes is null || result.Bytes.Length == 0)
            {
                var outcome = result.Error == ImageResult.ErrorModerationBlocked
                    ? ImageGenerationRecord.OutcomeModerationBlocked
                    : ImageGenerationRecord.OutcomeError;
                Record(channelName, userId, requestOptions, 0.0, startedAt, outcome);
                return ImageGenerationOutcome.Refused(ImageRefusals.ForError(result.Error));
            }

            Record(channelName, userId, requestOptions, estCost, startedAt, ImageGenerationRecord.OutcomeOk);
            _logger.LogInformation(
                "image_generated user={User} model={Model} quality={Quality} est_cost={Cost:F3} latency_ms={Latency}",
                userId, requestOptions.Model, requestOptions.Quality, estCost,
                (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

            return ImageGenerationOutcome.Ok(result.Bytes, $"robotnik.{result.FileExtension}");
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void Record(
        string? channelName, ulong userId, ImageRequestOptions options,
        double estCost, DateTimeOffset startedAt, string outcome)
    {
        _log.Record(new ImageGenerationRecord(
            Timestamp: DateTimeOffset.UtcNow,
            Channel: channelName,
            UserHash: UserIdHash.Hash(userId),
            Model: options.Model,
            Size: options.Size,
            Quality: options.Quality,
            EstCostUsd: estCost,
            LatencyMs: (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            Outcome: outcome));
    }
}
