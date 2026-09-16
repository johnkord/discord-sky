using System.Diagnostics;
using System.ClientModel;
using System.ClientModel.Primitives;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

// Pre-deploy smoke test for the LIVE image pipeline. It runs the bot's ACTUAL code end to end: the
// ImageRewriter (the configured balanced chat model on the Responses API) turns a raw request into an in-character prompt + caption,
// then OpenAIImageGenerator renders it. This proves the whole un-unit-tested live path (org verification,
// the chat + image API calls, option translation, real bytes). Run it BEFORE deploying.
//
// Usage (never put the key on the command line):
//   OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.ImageSmoke
//   OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.ImageSmoke -- --request "crown yourself emperor of Mobius"

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set OPENAI_API_KEY in the environment first (do not pass it as an argument):");
    Console.Error.WriteLine("  OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.ImageSmoke");
    return 2;
}

string GetArg(string name, string fallback)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : fallback;
}

var chatModel = GetArg("--chat-model", "gpt-5.6-sol");
var referencePath = GetArg("--reference", "");
var maskPath = GetArg("--mask", "");
var imageModel = GetArg("--model", referencePath.Length == 0 ? "gpt-image-2.5-flare" : "gpt-image-2.5-sunburst");
var quality = GetArg("--quality", "medium");
var size = GetArg("--size", "1024x1024");
var format = GetArg("--format", "jpeg");
var request = GetArg("--request", "crown yourself emperor of Mobius");
var persona = GetArg("--persona", "Robotnik from AOSTH");
var outPath = GetArg("--out", $"robotnik-smoke.{(format is "jpeg" or "jpg" ? "jpg" : format)}");
var direct = args.Contains("--direct", StringComparer.Ordinal);
var partialImages = int.Parse(GetArg("--previews", "1"), System.Globalization.CultureInfo.InvariantCulture);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));

// --- Step 1: the rewrite (the call that 400'd in production the first time). ---
var llmOptions = new LlmOptions
{
    ActiveProvider = "OpenAI",
    Guard = new LlmProviderGuardOptions
    {
        HourlyUsdLimit = 0.75,
        DailyUsdLimit = 0.75,
        StatePath = GetArg("--guard-state", "data/image-smoke-provider-guard.json"),
    },
    Providers =
    {
        ["OpenAI"] = new LlmProviderOptions
        {
            ApiKey = apiKey,
            ChatModel = chatModel,
            ImageRewriteModel = chatModel,
            ImageRewriteReasoningEffort = "ExtraHigh",
            RequestTimeoutMinutes = 15,
            UseResponsesApi = true,
        },
    },
};
var telemetry = new NoOpTelemetrySink();
var guard = new LlmProviderGuard(Options.Create(llmOptions), telemetry, loggerFactory.CreateLogger<LlmProviderGuard>());
using var chatClient = new TelemetryChatClient(
    LlmChatClientFactory.Create(llmOptions.GetActiveProvider(), chatModel), "OpenAI", telemetry, guard);
var memoryScorer = new LexicalMemoryScorer(new StaticOptionsMonitor<MemoryRelevanceOptions>(new MemoryRelevanceOptions()));
var rewriter = new ImageRewriter(
    chatClient, new StaticOptionsMonitor<LlmOptions>(llmOptions), memoryScorer, loggerFactory.CreateLogger<ImageRewriter>());

Console.WriteLine(direct ? "Direct image API smoke; no rewrite call." : $"Rewriting (chat model={chatModel}): \"{request}\"");
var rwSw = Stopwatch.StartNew();
var rewrite = direct ? ImageRewrite.Draw(request, "Smoke test") :
    await rewriter.RewriteAsync(persona, request, "smoke-tester", memories: null, replyContext: null, timeout.Token);
rwSw.Stop();

Console.WriteLine($"Rewrite in {rwSw.ElapsedMilliseconds} ms: refuse={rewrite.Refuse}");
if (rewrite.Refuse || string.IsNullOrWhiteSpace(rewrite.ImagePrompt))
{
    Console.Error.WriteLine($"REWRITE REFUSED (refusal_text=\"{rewrite.RefusalText}\").");
    Console.Error.WriteLine("If an HTTP 400 was logged above, the rewrite request shape is wrong (model / response-format).");
    Console.Error.WriteLine("If it was a clean refusal, the request tripped the safety screen; try a tamer --request.");
    return 1;
}
Console.WriteLine($"  caption: {rewrite.Caption}");
Console.WriteLine($"  image_prompt: {rewrite.ImagePrompt}");

// --- Step 2: generation, exactly as production does (style suffix appended by ImageToolService). ---
var prompt = rewrite.ImagePrompt + " " + ImageToolService.StyleSuffix;
var options = ImageRequestOptions.FromConfig(new ImageOptions
{
    Model = imageModel,
    Quality = quality,
    Size = size,
    OutputFormat = format,
    Background = GetArg("--background", "opaque"),
    PartialImages = partialImages,
    AllowHighQuality = true,
});
ImageReference? ReadReference(string path, bool mask)
{
    if (path.Length == 0) return null;
    if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new ArgumentException("Reference exceeds 8 MiB.");
    return ImageReferenceValidation.Create(File.ReadAllBytes(path), 0, mask);
}
var referenceImage = ReadReference(referencePath, false);
var references = new ImageReferenceSet(referenceImage is null ? [] : [referenceImage], ReadReference(maskPath, true));
ImageReferenceValidation.ValidateMask(references);
var generator = new OpenAIImageGenerator(
    new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
    { RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromMinutes(5) }),
    guard,
    loggerFactory.CreateLogger<OpenAIImageGenerator>());

Console.WriteLine($"Generating: model={imageModel} size={size} quality={options.Quality} format={format} (5 to 60+ s)...");
var genSw = Stopwatch.StartNew();
var result = await generator.RenderAsync(new ImageRenderRequest(prompt, options, references.Images, references.Mask,
    async (preview, cancellationToken) =>
    {
        var previewPath = Path.ChangeExtension(outPath, $"preview-{preview.Index}.{preview.FileExtension}");
        await File.WriteAllBytesAsync(previewPath, preview.Bytes, cancellationToken);
        Console.WriteLine($"Preview {preview.Index}: {preview.Bytes.Length:N0} bytes");
    }), timeout.Token);
genSw.Stop();

if (!result.Success || result.Bytes is null || result.Bytes.Length == 0)
{
    Console.Error.WriteLine($"GENERATION FAILED after {genSw.ElapsedMilliseconds} ms. error={result.Error} code={result.ProviderErrorCode} request_id={result.RequestId}");
    Console.Error.WriteLine("If the logged message mentions 'verified'/403, finish API Organization Verification. 401 = bad key.");
    return 1;
}

await File.WriteAllBytesAsync(outPath, result.Bytes);
Console.WriteLine($"OK. Full pipeline works: rewrite -> generate in {rwSw.ElapsedMilliseconds + genSw.ElapsedMilliseconds} ms total.");
Console.WriteLine($"Wrote {result.Bytes.Length:N0} bytes to {Path.GetFullPath(outPath)}. Open it to eyeball the result.");
Console.WriteLine($"Cost: ${result.CostUsd:F6} ({result.CostBasis}); previews={result.PreviewCount}; request_id={result.RequestId}");
Console.WriteLine($"Local smoke ledger today: ${guard.Snapshot().DailyCostUsd:F6}. This ledger is separate from production.");
return 0;

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
