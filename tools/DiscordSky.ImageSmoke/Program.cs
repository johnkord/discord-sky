using System.Diagnostics;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using Microsoft.Extensions.Logging;
using OpenAI;

// Pre-deploy smoke test for the LIVE OpenAI image path. It uses the bot's actual OpenAIImageGenerator
// with the exact production options, calls the API once (a few tenths of a cent), and writes the image to
// a file. This proves the one path that has no unit coverage: org verification, the API key, the option
// translation, and that real bytes come back. Run it BEFORE enabling the feature in production.
//
// Usage (never put the key on the command line):
//   OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.ImageSmoke
//   OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.ImageSmoke -- --quality medium --prompt "a statue of me"

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

var model = GetArg("--model", "gpt-image-1-mini");
var quality = GetArg("--quality", "low");
var size = GetArg("--size", "1024x1024");
var format = GetArg("--format", "jpeg");
var subject = GetArg("--prompt",
    "Dr. Robotnik, the rotund cartoon supervillain with a huge orange mustache and round black goggles, " +
    "posing triumphantly atop a golden throne shaped like an egg, imperial propaganda-poster composition");
var outPath = GetArg("--out", $"robotnik-smoke.{(format is "jpeg" or "jpg" ? "jpg" : format)}");

// Exactly what production does: append the mandatory style suffix, resolve options from config defaults.
var prompt = subject + " " + ImageToolService.StyleSuffix;
var options = ImageRequestOptions.FromConfig(new ImageOptions
{
    Model = model,
    Quality = quality,
    Size = size,
    OutputFormat = format,
    AllowHighQuality = true, // let the smoke test exercise any quality you pass
});

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));

var generator = new OpenAIImageGenerator(
    new OpenAIClient(apiKey).GetImageClient(model),
    loggerFactory.CreateLogger<OpenAIImageGenerator>());

Console.WriteLine($"Generating: model={model} size={size} quality={options.Quality} format={format}");
Console.WriteLine($"Subject: {subject}");
Console.WriteLine("(GPT Image can take 5 to 60+ seconds)...");

var sw = Stopwatch.StartNew();
var result = await generator.GenerateAsync(prompt, options, CancellationToken.None);
sw.Stop();

if (!result.Success || result.Bytes is null || result.Bytes.Length == 0)
{
    Console.Error.WriteLine($"FAILED after {sw.ElapsedMilliseconds} ms. error={result.Error}");
    Console.Error.WriteLine(
        "If the logged message above mentions 'verified' or a 403, complete API Organization Verification " +
        "in the OpenAI console (Settings -> Organization), then retry. A 401 means the key is wrong.");
    return 1;
}

await File.WriteAllBytesAsync(outPath, result.Bytes);
Console.WriteLine($"OK in {sw.ElapsedMilliseconds} ms. Wrote {result.Bytes.Length:N0} bytes to {Path.GetFullPath(outPath)}");
if (!string.IsNullOrWhiteSpace(result.RevisedPrompt))
{
    Console.WriteLine($"Model revised prompt: {result.RevisedPrompt}");
}
Console.WriteLine("Open the file to eyeball whether Robotnik looks like Robotnik (the consistency question).");
return 0;
