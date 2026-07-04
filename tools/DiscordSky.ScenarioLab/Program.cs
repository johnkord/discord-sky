using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Orchestration.Impulse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

// ScenarioLab: Phase 0 of the eval harness (docs/scenario_eval_harness_design_2026-07-04.md).
// Runs the REAL ColdOpenComposer over scenario fixtures and dumps its raw output. It does NOT judge;
// judging is the session's and the human's job (see the discord-sky-eval skill). Nothing is posted to Discord.
//
// Fidelity: loads the bot's REAL LlmOptions from src/DiscordSky.Bot/appsettings.json (plus env overrides) and
// builds the IChatClient the same way Program.cs does, so it exercises official bot code and config, not a
// parallel reimplementation. Each run is stamped with the git SHA it built from (a dirty tree is flagged);
// since we deploy after every change, that SHA is the deployed bot.
//
// Usage:
//   OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.ScenarioLab -- <fixtures.json|dir> \
//       [--model <override>] [--runs 1] [--json]
//
//   --model    override the configured ChatModel (for experiments); default is the bot's configured model.
//   --runs N   compose each scenario N times to see the variance of a stochastic generator.
//   --json     emit machine-readable records, stamped with the bot source SHA, for saving as a durable artifact.

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: OPENAI_API_KEY=... dotnet run --project tools/DiscordSky.ScenarioLab -- <fixtures.json|dir> [--model <override>] [--runs 1] [--json]");
    return 0;
}

var path = args[0];
string? modelOverride = null;
var runs = 1;
var asJson = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model" when i + 1 < args.Length: modelOverride = args[++i]; break;
        case "--runs" when i + 1 < args.Length && int.TryParse(args[i + 1], out var r): runs = Math.Max(1, r); i++; break;
        case "--json": asJson = true; break;
    }
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

// Resolve the repo root (independent of cwd) so we can load the bot's real config and stamp the git SHA.
var repoRoot = FindRepoRoot();
var appsettings = Path.Combine(repoRoot, "src", "DiscordSky.Bot", "appsettings.json");
if (!File.Exists(appsettings)) { Console.Error.WriteLine($"Could not find bot config at {appsettings}"); return 1; }

// Base config: the bot's appsettings + its Development overlay + environment (so LLM__... env overrides apply,
// exactly like the bot). Then overlay the API key (from OPENAI_API_KEY) and any --model override.
var baseCfg = new ConfigurationBuilder()
    .AddJsonFile(appsettings, optional: false)
    .AddJsonFile(Path.Combine(repoRoot, "src", "DiscordSky.Bot", "appsettings.Development.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();

var activeProvider = baseCfg["LLM:ActiveProvider"] ?? "OpenAI";
var apiKey = baseCfg[$"LLM:Providers:{activeProvider}:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey)) apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"No API key for provider '{activeProvider}'. Set OPENAI_API_KEY (or LLM__Providers__{activeProvider}__ApiKey).");
    return 1;
}

var overlay = new Dictionary<string, string?> { [$"LLM:Providers:{activeProvider}:ApiKey"] = apiKey };
if (!string.IsNullOrWhiteSpace(modelOverride)) overlay[$"LLM:Providers:{activeProvider}:ChatModel"] = modelOverride;
var cfg = new ConfigurationBuilder().AddConfiguration(baseCfg).AddInMemoryCollection(overlay).Build();

var llm = cfg.GetSection("LLM").Get<LlmOptions>() ?? new LlmOptions();
if (!llm.Providers.TryGetValue(llm.ActiveProvider, out var provider))
{
    Console.Error.WriteLine($"Active provider '{llm.ActiveProvider}' is not configured in appsettings.");
    return 1;
}
var model = provider.ChatModel;

// Build the IChatClient EXACTLY as Program.cs does: honor a custom endpoint, and choose the Responses API vs
// Chat Completions the same way the bot does. This is the real construction path, not a simplified copy.
var openAiClient = string.IsNullOrWhiteSpace(provider.Endpoint)
    ? new OpenAIClient(provider.ApiKey)
    : new OpenAIClient(new System.ClientModel.ApiKeyCredential(provider.ApiKey), new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint) });
var chatClient = provider.UseResponsesApi
    ? openAiClient.GetResponsesClient(model).AsIChatClient()
    : openAiClient.GetChatClient(model).AsIChatClient();

var composer = new ColdOpenComposer(chatClient, new StaticOptionsMonitor<LlmOptions>(llm), NullLogger<ColdOpenComposer>.Instance);

var sourceSha = GitStamp(repoRoot);
if (sourceSha.EndsWith("-dirty", StringComparison.Ordinal))
    Console.Error.WriteLine("warning: working tree is dirty; this eval reflects uncommitted changes, not a committed/deployed state.");
Console.Error.WriteLine($"Composing {scenarios.Count} scenario(s) x {runs} run(s) | provider {llm.ActiveProvider} | model {model} | bot source {sourceSha}");

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
    var payload = new { botSourceSha = sourceSha, model, activeProvider = llm.ActiveProvider, generatedAt = DateTimeOffset.UtcNow, records };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
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
Console.WriteLine($"{records.Count} output(s) from {scenarios.Count} scenario(s), bot source {sourceSha}. This tool does not judge; that is the session's and your job.");
return 0;

// --- helpers ---

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiscordSky.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static string GitStamp(string repoRoot)
{
    string Run(string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return string.Empty;
            var text = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? text : string.Empty;
        }
        catch { return string.Empty; }
    }

    var sha = Run("rev-parse --short HEAD");
    if (string.IsNullOrEmpty(sha)) return "unknown";
    return string.IsNullOrWhiteSpace(Run("status --porcelain")) ? sha : sha + "-dirty";
}

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
