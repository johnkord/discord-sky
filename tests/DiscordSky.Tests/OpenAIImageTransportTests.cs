using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace DiscordSky.Tests;

public sealed class OpenAIImageTransportTests
{
    private static readonly ImageRequestOptions Options = new("gpt-image-2.5-flare", "1536x864", "xhigh", "webp", "auto", "transparent", 80);
    private const string Usage = """
        {"input_tokens":150,"input_tokens_details":{"text_tokens":100,"image_tokens":50},"output_tokens":1000,"total_tokens":1150}
        """;

    [Fact]
    public async Task Generate_SendsNewSettingsAndSettlesUsage()
    {
        using var harness = new Harness($$"""{"data":[{"b64_json":"AQID"}],"usage":{{Usage}},"size":"1536x864","quality":"xhigh"}""");
        var result = await harness.Generator.GenerateAsync("a marble tower", Options, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.Equal("req-image-test", result.RequestId);
        Assert.Equal("token_usage", result.CostBasis);
        Assert.Equal(0.0309, result.CostUsd!.Value, 6);
        Assert.Equal(0.0309, harness.Guard.Snapshot().DailyCostUsd, 6);
        Assert.EndsWith("/images/generations", harness.Handler.Path);
        using var request = JsonDocument.Parse(harness.Handler.Body!);
        Assert.Equal("xhigh", request.RootElement.GetProperty("quality").GetString());
        Assert.Equal("transparent", request.RootElement.GetProperty("background").GetString());
        Assert.Equal(80, request.RootElement.GetProperty("output_compression").GetInt32());
        Assert.False(request.RootElement.TryGetProperty("response_format", out _));
        Assert.False(request.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task Edit_UploadsMultipleReferencesAndMask()
    {
        using var harness = new Harness($$"""{"data":[{"b64_json":"AQID"}],"usage":{{Usage}}}""");
        var source = new ImageReference([1, 2, 3], "source.png", "image/png", 10);
        var mask = new ImageReference([4, 5, 6], "mask.png", "image/png", 11);
        var result = await harness.Generator.RenderAsync(new("change the sky", Options with { Model = "gpt-image-2.5-sunburst" },
            [source, source with { FileName = "reference.png" }], mask), CancellationToken.None);

        Assert.True(result.Success);
        Assert.EndsWith("/images/edits", harness.Handler.Path);
        Assert.StartsWith("multipart/form-data; boundary=", harness.Handler.ContentType);
        Assert.Contains("source.png", harness.Handler.Body);
        Assert.Contains("reference.png", harness.Handler.Body);
        Assert.Contains("mask.png", harness.Handler.Body);
        Assert.Contains("gpt-image-2.5-sunburst", harness.Handler.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_PreviewFailureDoesNotAbortOrRepeatPaidRender(bool editing)
    {
        var eventPrefix = editing ? "image_edit" : "image_generation";
        var payload = $$"""
            event: {{eventPrefix}}.partial_image
            data: {"type":"{{eventPrefix}}.partial_image","b64_json":"AQID","partial_image_index":0}

            event: {{eventPrefix}}.completed
            data: {"type":"{{eventPrefix}}.completed","b64_json":"BAUG","output_format":"webp","usage":{{Usage}}}


            """;
        using var harness = new Harness(payload, mediaType: "text/event-stream");
        var callbacks = 0;
        var request = new ImageRenderRequest("a tower", Options with { PartialImages = 1 },
            editing ? [new ImageReference([1], "source.png", "image/png")] : null,
            OnPreview: (_, _) => { callbacks++; throw new InvalidOperationException("Discord edit failed"); });
        var result = await harness.Generator.RenderAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 4, 5, 6 }, result.Bytes);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, result.PreviewCount);
        Assert.Equal(1, harness.Handler.CallCount);
        Assert.Equal(0.0309, harness.Guard.Snapshot().DailyCostUsd, 6);
    }

    [Fact]
    public async Task ModerationError_IsStructuredAndNeverRetried()
    {
        using var harness = new Harness("""
            {"error":{"type":"image_generation_user_error","code":"moderation_blocked","message":"private input",
                "moderation_details":{"moderation_stage":"input","categories":["harassment"]}}}
            """, HttpStatusCode.BadRequest);
        var result = await harness.Generator.GenerateAsync("private input", Options, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ImageResult.ErrorModerationBlocked, result.Error);
        Assert.Equal("input", result.ModerationStage);
        Assert.Equal(new[] { "harassment" }, result.ModerationCategories);
        Assert.Equal("req-image-test", result.RequestId);
        Assert.Equal(1, harness.Handler.CallCount);
        Assert.Equal(0, harness.Guard.Snapshot().DailyCostUsd);
    }

    [Fact]
    public async Task QuotaError_OpensSharedGuardWithoutRetry()
    {
        using var harness = new Harness("""{"error":{"type":"insufficient_quota","code":"insufficient_quota"}}""",
            HttpStatusCode.TooManyRequests);
        var first = await harness.Generator.GenerateAsync("tower", Options, CancellationToken.None);
        var second = await harness.Generator.GenerateAsync("tower", Options, CancellationToken.None);

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.True(harness.Guard.Snapshot().IsOpen);
        Assert.Equal(1, harness.Handler.CallCount);
    }

    [Fact]
    public async Task CancelledDispatchedRequest_RetainsReservation()
    {
        using var harness = new Harness("", cancel: true);
        var request = new ImageRenderRequest("tower", Options);
        var exception = await Assert.ThrowsAsync<ImageGenerationCanceledException>(() =>
            harness.Generator.RenderAsync(request, CancellationToken.None));

        Assert.Equal(ImageUsageCost.Reservation(request), exception.Result.CostUsd!.Value, 6);
        Assert.Equal(exception.Result.CostUsd.Value, harness.Guard.Snapshot().DailyCostUsd, 6);
    }

    [Fact]
    public async Task CompletedMalformedImage_IsChargedOnce()
    {
        using var harness = new Harness($$"""{"data":[{"b64_json":"broken-base64"}],"usage":{{Usage}}}""");
        var result = await harness.Generator.GenerateAsync("tower", Options, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0.0309, harness.Guard.Snapshot().DailyCostUsd, 6);
    }

    [Fact]
    public void Usage_UsesCachedBreakdownAndConservativeUnknownInputs()
    {
        using var document = JsonDocument.Parse("""
            {"usage":{"input_tokens":175,"input_tokens_details":{"text_tokens":100,"image_tokens":50,
                "cached_tokens_details":{"text_tokens":20,"image_tokens":10}},"output_tokens":1000}}
            """);
        var usage = ImageUsageCost.Parse(document.RootElement);
        Assert.NotNull(usage);
        Assert.Equal(25, usage.UnclassifiedInputTokens);
        Assert.Equal(0.030965, ImageUsageCost.FromUsage("gpt-image-2.5-sunburst", usage)!.Value, 6);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "sky-image-transport-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _httpClient;
        public RecordingHandler Handler { get; }
        public LlmProviderGuard Guard { get; }
        public OpenAIImageGenerator Generator { get; }

        public Harness(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json", bool cancel = false)
        {
            Handler = new RecordingHandler(body, status, mediaType, cancel);
            _httpClient = new HttpClient(Handler);
            Guard = new LlmProviderGuard(NullLogger<LlmProviderGuard>.Instance, options: new LlmProviderGuardOptions
            { HourlyUsdLimit = 100, DailyUsdLimit = 100, StatePath = Path.Combine(_directory, "state.json") });
            var client = new OpenAIClient(new ApiKeyCredential("test-not-a-key"), new OpenAIClientOptions
            { Endpoint = new Uri("https://api.openai.test/v1"), RetryPolicy = new ClientRetryPolicy(0), Transport = new HttpClientPipelineTransport(_httpClient) });
            Generator = new OpenAIImageGenerator(client, Guard, NullLogger<OpenAIImageGenerator>.Instance);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status, string mediaType, bool cancel) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Path { get; private set; }
        public string? ContentType { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Path = request.RequestUri!.AbsolutePath;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (cancel) throw new OperationCanceledException(cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
            response.Headers.Add("x-request-id", "req-image-test");
            return response;
        }
    }
}