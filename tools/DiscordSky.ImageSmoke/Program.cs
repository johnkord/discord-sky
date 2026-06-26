using System.Diagnostics;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

// Pre-deploy smoke test for the LIVE image pipeline. It runs the bot's ACTUAL code end to end: the
// ImageRewriter (gpt-5.5 on the Responses API) turns a raw request into an in-character prompt + caption,
// then OpenAIImageGenerator renders it. This proves the whole un-unit-tested live path (org verification,
// the chat + image API calls, option translation, real bytes) for under a cent. Run it BEFORE deploying.
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

var chatModel = GetArg("--chat-model", "gpt-5.5");
var imageModel = GetArg("--model", "gpt-image-1-mini");
var quality = GetArg("--quality", "low");
var size = GetArg("--size", "1024x1024");
var format = GetArg("--format", "jpeg");
var request = GetArg("--request", "crown yourself emperor of Mobius");
var persona = GetArg("--persona", "Robotnik from AOSTH");
var outPath = GetArg("--out", $"robotnik-smoke.{(format is "jpeg" or "jpg" ? "jpg" : format)}");

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));

// --- Step 1: the rewrite (the call that 400'd in production the first time). ---
var llmOptions = new LlmOptions
{
    ActiveProvider = "OpenAI",
    Providers =
    {
        ["OpenAI"] = new LlmProviderOptions { ApiKey = apiKey, ChatModel = chatModel, UseResponsesApi = true },
    },
};
var chatClient = new OpenAIClient(apiKey).GetResponsesClient(chatModel).AsIChatClient();
var rewriter = new ImageRewriter(
    chatClient, new StaticOptionsMonitor<LlmOptions>(llmOptions), loggerFactory.CreateLogger<ImageRewriter>());

Console.WriteLine($"Rewriting (chat model={chatModel}): \"{request}\"");
var rwSw = Stopwatch.StartNew();
var rewrite = await rewriter.RewriteAsync(persona, request, "smoke-tester", CancellationToken.None);
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
    AllowHighQuality = true,
});
var generator = new OpenAIImageGenerator(
    new OpenAIClient(apiKey).GetImageClient(imageModel),
    loggerFactory.CreateLogger<OpenAIImageGenerator>());

Console.WriteLine($"Generating: model={imageModel} size={size} quality={options.Quality} format={format} (5 to 60+ s)...");
var genSw = Stopwatch.StartNew();
var result = await generator.GenerateAsync(prompt, options, CancellationToken.None);
genSw.Stop();

if (!result.Success || result.Bytes is null || result.Bytes.Length == 0)
{
    Console.Error.WriteLine($"GENERATION FAILED after {genSw.ElapsedMilliseconds} ms. error={result.Error}");
    Console.Error.WriteLine("If the logged message mentions 'verified'/403, finish API Organization Verification. 401 = bad key.");
    return 1;
}

await File.WriteAllBytesAsync(outPath, result.Bytes);
Console.WriteLine($"OK. Full pipeline works: rewrite -> generate in {rwSw.ElapsedMilliseconds + genSw.ElapsedMilliseconds} ms total.");
Console.WriteLine($"Wrote {result.Bytes.Length:N0} bytes to {Path.GetFullPath(outPath)}. Open it to eyeball the result.");
return 0;

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
