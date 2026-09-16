using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.Images;

public sealed partial class OpenAIImageGenerator
{
    public async Task<ImageResult> RenderAsync(ImageRenderRequest request, CancellationToken cancellationToken)
    {
        ImageModelPolicy.EnsureApproved(request.Options.Model);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OnPreview is null) request = request with { Options = request.Options with { PartialImages = 0 } };
        var reservation = ImageUsageCost.Reservation(request);
        if (!_providerGuard.TryBeginCall(request.Options.Model, true, out var lease, out var guard, reservation))
        {
            _logger.LogInformation("Image generation held by provider guard: {Reason}.", guard.Reason);
            return ImageResult.Fail(ImageResult.ErrorRateLimited) with { ProviderErrorCode = guard.Reason };
        }

        var dispatched = false;
        var settled = false;
        var previews = 0;
        var previewIndexes = new HashSet<int>();
        var charged = 0.0;
        string? requestId = null;
        ImageResult? completed = null;

        void Settle(double cost)
        {
            if (settled) return;
            settled = true;
            charged = cost;
            _providerGuard.RecordFixedCostSuccess(lease, cost);
        }

        ImageResult Finish(JsonElement root)
        {
            var usage = ImageUsageCost.Parse(root);
            var usageCost = ImageUsageCost.FromUsage(request.Options.Model, usage);
            Settle(usageCost ?? reservation);
            var image = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().FirstOrDefault() : root;
            var extension = ExtensionFor(Text(root, "output_format") ?? request.Options.OutputFormat);
            var encoded = Text(image, "b64_json");
            ImageResult result;
            try
            {
                result = string.IsNullOrEmpty(encoded) ? ImageResult.Fail(ImageResult.ErrorEmpty) :
                    encoded.Length > ImageRenderPolicy.MaxOutputBytes * 4L / 3 + 4
                        ? ImageResult.Fail(ImageResult.ErrorTooLarge)
                        : ImageResult.Ok(Convert.FromBase64String(encoded), extension, Text(image, "revised_prompt"));
            }
            catch (FormatException) { result = ImageResult.Fail(ImageResult.ErrorEmpty); }
            return result with
            {
                Usage = usage, CostUsd = charged, CostBasis = usageCost.HasValue ? "token_usage" : "reservation_estimate",
                RequestId = requestId, PreviewCount = previews,
                ActualSize = Text(root, "size"), ActualQuality = Text(root, "quality"),
            };
        }

        try
        {
            var options = request.Options;
            var streaming = options.PartialImages > 0 && request.OnPreview is not null;
            var fields = new Dictionary<string, object>
            {
                ["model"] = options.Model, ["prompt"] = request.Prompt, ["n"] = 1,
                ["size"] = options.Size, ["quality"] = options.Quality,
                ["output_format"] = NormalizeFormat(options.OutputFormat), ["background"] = options.Background,
                ["moderation"] = options.Moderation,
            };
            if (options.OutputCompression.HasValue) fields["output_compression"] = options.OutputCompression.Value;
            if (streaming)
            {
                fields["stream"] = true;
                fields["partial_images"] = options.PartialImages;
            }
            var editing = request.References is { Count: > 0 };
            using var multipart = editing ? CreateMultipart(request, fields) : null;
            using var content = multipart is null
                ? BinaryContent.Create(BinaryData.FromObjectAsJson(fields))
                : BinaryContent.Create(await multipart.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var client = _openAiClient.GetImageClient(options.Model);
            var protocolOptions = new RequestOptions { CancellationToken = cancellationToken, BufferResponse = !streaming };
            if (streaming) protocolOptions.SetHeader("Accept", "text/event-stream");
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true;
            var result = editing
                ? await client.GenerateImageEditsAsync(content, multipart!.Headers.ContentType!.ToString(), protocolOptions).ConfigureAwait(false)
                : await client.GenerateImagesAsync(content, protocolOptions).ConfigureAwait(false);
            using var response = result.GetRawResponse();
            requestId = RequestId(response.Headers);
            if (!streaming)
            {
                if (response.Content.ToMemory().Length > ImageRenderPolicy.MaxResponseBytes)
                {
                    Settle(reservation);
                    return ImageResult.Fail(ImageResult.ErrorTooLarge) with
                    { RequestId = requestId, CostUsd = charged, CostBasis = "reservation_estimate" };
                }
                using var document = JsonDocument.Parse(response.Content);
                return Finish(document.RootElement);
            }

            await foreach (var update in SseParser.Create(response.ContentStream!).EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (update.Data.Length > ImageRenderPolicy.MaxResponseBytes)
                {
                    Settle(reservation);
                    return ImageResult.Fail(ImageResult.ErrorTooLarge) with
                    { RequestId = requestId, CostUsd = charged, CostBasis = "reservation_estimate", PreviewCount = previews };
                }
                using var document = JsonDocument.Parse(update.Data);
                var root = document.RootElement;
                var kind = Text(root, "type") ?? update.EventType;
                if (kind is "image_generation.completed" or "image_edit.completed")
                {
                    completed = Finish(root);
                    break;
                }
                if (kind == "error" || root.TryGetProperty("error", out _))
                {
                    Settle(reservation);
                    var failure = ParseError(root, 200, requestId);
                    _providerGuard.RecordFailure(new InvalidOperationException(failure.ProviderErrorCode));
                    return failure with { CostUsd = charged, CostBasis = "reservation_estimate", PreviewCount = previews };
                }
                if (kind is not ("image_generation.partial_image" or "image_edit.partial_image")) continue;
                if (previews >= options.PartialImages ||
                    !root.TryGetProperty("partial_image_index", out var indexValue) ||
                    !indexValue.TryGetInt32(out var index) || index < 0 || index > 2 || !previewIndexes.Add(index)) continue;
                var encoded = Text(root, "b64_json");
                if (encoded is null || encoded.Length > ImageRenderPolicy.MaxOutputBytes * 4L / 3 + 4) continue;
                previews++;
                var preview = new ImagePreview(Convert.FromBase64String(encoded),
                    ExtensionFor(Text(root, "output_format") ?? options.OutputFormat), index);
                try
                {
                    await request.OnPreview!(preview, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Image preview delivery failed: {ErrorType}; continuing the paid render.", exception.GetType().Name);
                }
            }
            if (completed is not null) return completed;
            Settle(reservation);
            return ImageResult.Fail(ImageResult.ErrorEmpty) with
            { RequestId = requestId, CostUsd = charged, CostBasis = "reservation_estimate", PreviewCount = previews };
        }
        catch (OperationCanceledException)
        {
            if (dispatched) Settle(reservation);
            else if (!settled) _providerGuard.RecordCallFailure(lease, new OperationCanceledException());
            throw new ImageGenerationCanceledException(ImageResult.Fail(ImageResult.ErrorCancelled) with
            { RequestId = requestId, CostUsd = charged, CostBasis = "reservation_estimate", PreviewCount = previews }, cancellationToken);
        }
        catch (ClientResultException exception)
        {
            if (!settled && exception.Status >= 500) Settle(reservation);
            if (!settled) { settled = true; _providerGuard.RecordCallFailure(lease, exception); }
            else _providerGuard.RecordFailure(exception);
            var failure = ImageResult.Fail(exception.Status == 429 ? ImageResult.ErrorRateLimited : ImageResult.ErrorGeneric);
            var response = exception.GetRawResponse();
            if (response is not null)
            {
                requestId ??= RequestId(response.Headers);
                try
                {
                    using var document = JsonDocument.Parse(response.Content);
                    failure = ParseError(document.RootElement, exception.Status, requestId);
                }
                catch (JsonException) { }
            }
            _logger.LogWarning("Image API error: status={Status} code={Code} request_id={RequestId} stage={Stage}",
                exception.Status, failure.ProviderErrorCode ?? failure.Error, requestId, failure.ModerationStage);
            return failure with { HttpStatus = exception.Status, RequestId = requestId, CostUsd = charged,
                CostBasis = charged > 0 ? "reservation_estimate" : "not_charged", PreviewCount = previews };
        }
        catch (Exception exception)
        {
            if (dispatched) Settle(reservation);
            else if (!settled) _providerGuard.RecordCallFailure(lease, exception);
            _logger.LogWarning("Image generation failed: {ErrorType} request_id={RequestId}", exception.GetType().Name, requestId);
            return ImageResult.Fail(ImageResult.ErrorGeneric) with
            { RequestId = requestId, CostUsd = charged, CostBasis = "reservation_estimate", PreviewCount = previews };
        }
    }

    private static MultipartFormDataContent CreateMultipart(ImageRenderRequest request, Dictionary<string, object> fields)
    {
        var content = new MultipartFormDataContent();
        foreach (var field in fields)
            content.Add(new StringContent(field.Value is bool enabled ? (enabled ? "true" : "false") :
                Convert.ToString(field.Value, CultureInfo.InvariantCulture)!), field.Key);
        foreach (var reference in request.References!) AddImage(content, "image[]", reference);
        if (request.Mask is not null) AddImage(content, "mask", request.Mask);
        return content;
    }

    private static void AddImage(MultipartFormDataContent content, string field, ImageReference image)
    {
        var part = new ByteArrayContent(image.Bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(image.MediaType);
        content.Add(part, field, image.FileName);
    }

    internal static ImageResult ParseError(JsonElement root, int status, string? requestId)
    {
        var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var code = Text(error, "code");
        var type = Text(error, "type");
        var classification = code is "moderation_blocked" or "content_policy_violation"
            ? ImageResult.ErrorModerationBlocked : status == 429 ? ImageResult.ErrorRateLimited :
            status >= 500 ? ImageResult.ErrorServer : type == "image_generation_user_error" || status == 400
                ? ImageResult.ErrorInvalidRequest : ImageResult.ErrorGeneric;
        string? stage = null;
        string[]? categories = null;
        if (error.TryGetProperty("moderation_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            stage = Text(details, "moderation_stage");
            if (stage is not ("input" or "output" or "unknown")) stage = "unknown";
            if (details.TryGetProperty("categories", out var values) && values.ValueKind == JsonValueKind.Array)
                categories = values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!).Where(value => value.Length <= 64).Take(16).ToArray();
        }
        return ImageResult.Fail(classification) with
        { HttpStatus = status, ProviderErrorCode = code?.Length <= 100 ? code : null, RequestId = requestId,
            ModerationStage = stage, ModerationCategories = categories };
    }

    private static string? RequestId(PipelineResponseHeaders headers) =>
        headers.TryGetValue("x-request-id", out var value) ? value : null;

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}