using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Impulse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

// ScenarioLab: Phase 0 of the eval harness (docs/scenario_eval_harness_design_2026-07-04.md).
// Runs the REAL ColdOpenComposer against scenario fixtures and dumps its raw output. It does NOT judge;
// judging is the session's and the human's job (see the discord-sky-eval skill). Nothing is posted to Discord.
//
// Usage:
//   OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.ScenarioLab -- <fixtures.json|dir> \
//       [--model gpt-5.5] [--runs 1] [--json]
//
//   --runs N   compose each scenario N times to see the variance of a stochastic generator.
//   --json     emit machine-readable records (one per scenario+run) for saving as a durable artifact.

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: OPENAI_API_KEY=... dotnet run --project tools/DiscordSky.ScenarioLab -- <fixtures.json|dir> [--model gpt-5.5] [--runs 1] [--json]");
    return 0;
}

var path = args[0];
var model = "gpt-5.5";
var runs = 1;
var asJson = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model" when i + 1 < args.Length: model = args[++i]; break;
        case "--runs" when i + 1 < args.Length && int.TryParse(args[i + 1], out var r): runs = Math.Max(1, r); i++; break;
        case "--json": asJson = true; break;
    }
}

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? Environment.GetEnvironmentVariable("LLM__Providers__OpenAI__ApiKey");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set OPENAI_API_KEY (or LLM__Providers__OpenAI__ApiKey) in the environment.");
    return 1;
}

var files = Directory.Exists(path)
    ? Directory.EnumerateFiles(path, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList()
    : File.Exists(path) ? new List<string> { path } : new List<string>();
if (files.Count == 0) { Console.Error.WriteLine($"No scenario JSON found at {path}"); return 1; }

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var scenarios = new List<Scenario>();
foreach (var f in files)
{
    try
    {
        var loaded = JsonSerializer.Deserialize<List<Scenario>>(File.ReadAllText(f), jsonOpts);
        if (loaded is not null) scenarios.AddRange(loaded);
    }
    catch (JsonException ex) { Console.Error.WriteLine($"Skipping {f}: {ex.Message}"); }
}
if (scenarios.Count == 0) { Console.Error.WriteLine("No scenarios parsed."); return 1; }

// Build the REAL ColdOpenComposer against the live model, using the Responses API path exactly as the bot does
// for gpt-5.5 (Program.cs). The composer sets ModelId per request from the options below.
var chatClient = new OpenAIClient(apiKey).GetResponsesClient(model).AsIChatClient();
var llm = new LlmOptions
{
    ActiveProvider = "OpenAI",
    Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenAI"] = new LlmProviderOptions { ApiKey = apiKey, ChatModel = model, UseResponsesApi = true },
    },
};
var composer = new ColdOpenComposer(chatClient, new StaticOptionsMonitor<LlmOptions>(llm), NullLogger<ColdOpenComposer>.Instance);

Console.Error.WriteLine($"Composing {scenarios.Count} scenario(s) x {runs} run(s) with model {model}...");

var records = new List<OutputRecord>();
foreach (var s in scenarios)
{
    var ctx = new ColdOpenContext(
        PersonaName: string.IsNullOrWhiteSpace(s.PersonaName) ? "Robotnik from Adventures of Sonic the Hedgehog" : s.PersonaName!,
        MoodLabel: s.MoodLabel,
        SituationLog: s.SituationLog ?? string.Empty,
        RecentPeople: s.RecentPeople ?? new List<string>(),
        RecentLines: s.RecentLines);

    for (var run = 1; run <= runs; run++)
    {
        var draft = await composer.ComposeAsync(ctx, CancellationToken.None);
        var declined = draft is null || string.IsNullOrWhiteSpace(draft.Line);
        records.Add(new OutputRecord(s.Name ?? "(unnamed)", run, draft?.Worth, draft?.Hook, draft?.Line, declined));
    }
}

if (asJson)
{
    Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

foreach (var group in records.GroupBy(r => r.Scenario))
{
    Console.WriteLine();
    Console.WriteLine($"=== {group.Key} ===");
    foreach (var r in group)
    {
        var tag = runs > 1 ? $"[run {r.Run}] " : string.Empty;
        if (r.Declined)
        {
            Console.WriteLine($"{tag}DECLINE (no line){(r.Worth is { } w ? $"  worth {w:0.00}" : string.Empty)}");
        }
        else
        {
            Console.WriteLine($"{tag}worth {r.Worth:0.00}  hook {r.Hook}");
            Console.WriteLine($"{tag}  {r.Line}");
        }
    }
}

Console.WriteLine();
Console.WriteLine($"{records.Count} output(s) from {scenarios.Count} scenario(s). This tool does not judge; that is the session's and your job.");
return 0;

// --- types (fixtures schema + output record + a minimal options monitor) ---

/// <summary>One scenario fixture. Mirrors ColdOpenContext plus a name; all fields are plain strings so a fixture
/// needs no Empire State machinery, just a situation snapshot.</summary>
internal sealed record Scenario(
    string? Name,
    string? PersonaName,
    string? MoodLabel,
    string? SituationLog,
    List<string>? RecentPeople,
    List<string>? RecentLines);

internal sealed record OutputRecord(string Scenario, int Run, double? Worth, string? Hook, string? Line, bool Declined);

/// <summary>Fixed-value IOptionsMonitor, matching the repo's test double signature so it builds warning-free.</summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
