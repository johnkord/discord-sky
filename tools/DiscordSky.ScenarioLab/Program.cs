using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using DiscordSky.Bot.Orchestration.Impulse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// ScenarioLab: Phase 0 of the eval harness (docs/scenario_eval_harness_design_2026-07-04.md).
// Runs the REAL ColdOpenComposer (and the ColdOpenCritic second pass) over scenario fixtures and dumps the raw
// output plus the critic's verdict. It does NOT judge; judging is the session's and the human's job (see the
// discord-sky-eval skill). Nothing is posted to Discord.
//
// Fidelity: loads the bot's REAL LlmOptions from src/DiscordSky.Bot/appsettings.json (plus env overrides) and
// builds the IChatClient the same way Program.cs does, so it exercises official bot code and config, not a
// parallel reimplementation. Each run is stamped with the git SHA it built from (a dirty tree is flagged);
// since we deploy after every change, that SHA is the deployed bot.
//
// Usage:
//   dotnet run --project tools/DiscordSky.ScenarioLab -- <fixtures.json|dir> \
//       [--candidate <provider[:model[:effort]]>]... [--critic <provider[:model[:effort]]>] \
//       [--no-critic] [--runs 1] [--json]
//
//   --candidate  add an OpenAI or xAI trial arm; repeat for a matrix run. Missing model/effort use real config.
//   --critic     freeze the advisory critic independently; default is the active provider's configured critic.
//   --no-critic  skip the advisory critic entirely. Human humor review never depends on it.
//   --model      legacy single-provider model override; cannot be combined with --candidate.
//   --runs N     compose each scenario N times to see stochastic variance.
//   --json       emit machine-readable records stamped with the bot source SHA.
//   --artifact   also save the complete machine-readable result to a local JSON file.
//   --review-doc create a balanced blind A/B review doc plus a separate local reveal key (two candidates only).

if (args.Length > 0 && args[0].Equals("episode", StringComparison.OrdinalIgnoreCase))
{
    return await RunEpisodeReplayAsync(args[1..]);
}
if (args.Length > 0 && args[0].Equals("novelty", StringComparison.OrdinalIgnoreCase))
{
    return RunNoveltyReplay(args[1..]);
}
if (args.Length > 0 && args[0].Equals("memory", StringComparison.OrdinalIgnoreCase))
{
    return RunMemoryReplay(args[1..]);
}

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: dotnet run --project tools/DiscordSky.ScenarioLab -- <fixtures.json|dir> [--candidate <provider[:model[:effort]]>]... [--critic <provider[:model[:effort]]>] [--no-critic] [--model <legacy-override>] [--runs 1] [--json] [--artifact <results.local.json>] [--review-doc <review.md>] [--review-seed <int>]");
    Console.WriteLine("Example: --candidate OpenAI:gpt-5.6-sol:medium --candidate xAI:grok-4.5:medium");
    Console.WriteLine("Episode replay: dotnet run --project tools/DiscordSky.ScenarioLab -- episode [fixtures.json] [--json]");
    Console.WriteLine("Novelty replay: dotnet run --project tools/DiscordSky.ScenarioLab -- novelty [fixtures.json] [--json]");
    Console.WriteLine("Memory replay: dotnet run --project tools/DiscordSky.ScenarioLab -- memory [fixtures.json] [--json]");
    Console.WriteLine("Keys: LLM__Providers__OpenAI__ApiKey / OPENAI_API_KEY and LLM__Providers__xAI__ApiKey / XAI_API_KEY, or ScenarioLab user-secrets.");
    return 0;
}

var path = args[0];
string? modelOverride = null;
var candidateSpecs = new List<string>();
string? criticSpec = null;
var noCritic = false;
var runs = 1;
var asJson = false;
string? artifactPath = null;
string? reviewDocPath = null;
int? reviewSeed = null;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--candidate" when i + 1 < args.Length: candidateSpecs.Add(args[++i]); break;
        case "--critic" when i + 1 < args.Length: criticSpec = args[++i]; break;
        case "--no-critic": noCritic = true; break;
        case "--model" when i + 1 < args.Length: modelOverride = args[++i]; break;
        case "--runs" when i + 1 < args.Length && int.TryParse(args[i + 1], out var r): runs = Math.Max(1, r); i++; break;
        case "--json": asJson = true; break;
        case "--artifact" when i + 1 < args.Length: artifactPath = args[++i]; break;
        case "--review-doc" when i + 1 < args.Length: reviewDocPath = args[++i]; break;
        case "--review-seed" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seed): reviewSeed = seed; i++; break;
        default: Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}"); return 1;
    }
}

if (candidateSpecs.Count > 0 && !string.IsNullOrWhiteSpace(modelOverride))
{
    Console.Error.WriteLine("--model is the legacy single-provider override and cannot be combined with --candidate.");
    return 1;
}
if (noCritic && !string.IsNullOrWhiteSpace(criticSpec))
{
    Console.Error.WriteLine("--critic and --no-critic cannot be combined.");
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

// Resolve the repo root (independent of cwd) so we can load the bot's real config and stamp the git SHA.
var repoRoot = FindRepoRoot();
var appsettings = Path.Combine(repoRoot, "src", "DiscordSky.Bot", "appsettings.json");
if (!File.Exists(appsettings)) { Console.Error.WriteLine($"Could not find bot config at {appsettings}"); return 1; }

// Base config: the bot's appsettings + its Development overlay + local user-secrets (the API key, stored OUTSIDE
// the repo so it is never committed) + environment (so LLM__... env vars still override, exactly like the bot).
// Later sources win, so env beats user-secrets beats appsettings. Then overlay the resolved key and --model.
var baseCfg = new ConfigurationBuilder()
    .AddJsonFile(appsettings, optional: false)
    .AddJsonFile(Path.Combine(repoRoot, "src", "DiscordSky.Bot", "appsettings.Development.json"), optional: true)
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .Build();

var baseLlm = baseCfg.GetSection("LLM").Get<LlmOptions>() ?? new LlmOptions();
var activeProvider = baseLlm.ActiveProvider;
if (candidateSpecs.Count == 0)
{
    candidateSpecs.Add(string.IsNullOrWhiteSpace(modelOverride)
        ? activeProvider
        : $"{activeProvider}:{modelOverride}");
}

List<TrialCandidate> candidates;
TrialCandidate? criticTrial = null;
try
{
    candidates = candidateSpecs
        .Select(spec => BuildTrial(spec, LlmWorkload.ColdOpen, baseCfg, baseLlm))
        .ToList();

    var duplicate = candidates
        .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(group => group.Count() > 1);
    if (duplicate is not null)
        throw new ArgumentException($"Duplicate candidate '{duplicate.Key}'.");

    if (!noCritic)
        criticTrial = BuildTrial(criticSpec ?? activeProvider, LlmWorkload.ColdOpenCritic, baseCfg, baseLlm);

    if (!string.IsNullOrWhiteSpace(reviewDocPath) && candidates.Count != 2)
        throw new ArgumentException("--review-doc requires exactly two candidates.");
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var critic = criticTrial is null
    ? null
    : new ColdOpenCritic(
        criticTrial.ChatClient,
        new StaticOptionsMonitor<LlmOptions>(criticTrial.Options),
        NullLogger<ColdOpenCritic>.Instance);
var composers = candidates.ToDictionary(
    candidate => candidate.Id,
    candidate => new ColdOpenComposer(
        candidate.ChatClient,
        new LlmWorkloadProfile(candidate.Model, candidate.ReasoningEffort),
        NullLogger<ColdOpenComposer>.Instance,
        surfaceFailures: true),
    StringComparer.OrdinalIgnoreCase);

var sourceSha = GitStamp(repoRoot);
if (sourceSha.EndsWith("-dirty", StringComparison.Ordinal))
    Console.Error.WriteLine("warning: working tree is dirty; this eval reflects uncommitted changes, not a committed/deployed state.");
Console.Error.WriteLine(
    $"Composing {scenarios.Count} scenario(s) x {runs} run(s) x {candidates.Count} candidate(s) | " +
    $"{string.Join(", ", candidates.Select(candidate => candidate.Id))} | " +
    $"critic {(criticTrial?.Id ?? "disabled")} | bot source {sourceSha}");

var records = new List<OutputRecord>();
for (var scenarioIndex = 0; scenarioIndex < scenarios.Count; scenarioIndex++)
{
    var s = scenarios[scenarioIndex];
    var ctx = new ColdOpenContext(
        PersonaName: string.IsNullOrWhiteSpace(s.PersonaName) ? "Robotnik from Adventures of Sonic the Hedgehog" : s.PersonaName!,
        MoodLabel: s.MoodLabel,
        SituationLog: s.SituationLog ?? string.Empty,
        RecentPeople: s.RecentPeople ?? new List<string>(),
        RecentLines: s.RecentLines);

    for (var run = 1; run <= runs; run++)
    {
        // Rotate the first caller across matched scenario/run cells so transient provider conditions do not
        // always favor the candidate listed first on the command line.
        var firstCandidate = (scenarioIndex + run - 1) % candidates.Count;
        for (var offset = 0; offset < candidates.Count; offset++)
        {
            var candidate = candidates[(firstCandidate + offset) % candidates.Count];
            var composeTimer = System.Diagnostics.Stopwatch.StartNew();
            ColdOpenDraft? draft = null;
            var status = "ok";
            string? error = null;
            try
            {
                draft = await composers[candidate.Id].ComposeAsync(ctx, CancellationToken.None);
            }
            catch (Exception ex)
            {
                status = "failed";
                error = ex.GetType().Name;
            }
            composeTimer.Stop();
            var declined = status == "ok" && (draft is null || string.IsNullOrWhiteSpace(draft.Line));

            // The critic is one frozen advisory treatment for every candidate. It never gates output or
            // decides humor, and --no-critic removes it from the experiment entirely.
            ColdOpenCritique? critique = null;
            long? criticLatencyMs = null;
            if (!declined && draft is not null && critic is not null)
            {
                var criticTimer = System.Diagnostics.Stopwatch.StartNew();
                critique = await critic.ReviewAsync(ctx, draft, CancellationToken.None);
                criticTimer.Stop();
                criticLatencyMs = criticTimer.ElapsedMilliseconds;
            }

            records.Add(new OutputRecord(
                candidate.Id, candidate.Provider, candidate.Model, candidate.ReasoningEffort,
                s.Name ?? "(unnamed)", run, status, error, draft?.Worth, draft?.Hook, draft?.Line, declined,
                composeTimer.ElapsedMilliseconds, critique?.Worth, critique?.Flaw, criticLatencyMs));
        }
    }
}

var generatedAt = DateTimeOffset.UtcNow;
var payload = new
{
    botSourceSha = sourceSha,
    generatedAt,
    candidates = candidates.Select(candidate => new
    {
        id = candidate.Id,
        provider = candidate.Provider,
        model = candidate.Model,
        reasoningEffort = candidate.ReasoningEffort,
        useResponsesApi = candidate.UseResponsesApi,
    }),
    critic = criticTrial is null ? null : new
    {
        id = criticTrial.Id,
        provider = criticTrial.Provider,
        model = criticTrial.Model,
        reasoningEffort = criticTrial.ReasoningEffort,
        useResponsesApi = criticTrial.UseResponsesApi,
    },
    records,
};
var serializedPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

if (!string.IsNullOrWhiteSpace(artifactPath))
{
    WriteLocalFile(artifactPath, serializedPayload);
    Console.Error.WriteLine($"Raw artifact: {Path.GetFullPath(artifactPath)}");
}

if (!string.IsNullOrWhiteSpace(reviewDocPath))
{
    var (writtenReviewPath, revealPath) = WriteBlindReviewArtifacts(
        reviewDocPath,
        reviewSeed ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
        scenarios,
        records,
        sourceSha,
        generatedAt);
    Console.Error.WriteLine($"Blind review: {writtenReviewPath}");
    Console.Error.WriteLine($"Reveal key: {revealPath}");
}

if (asJson)
{
    Console.WriteLine(serializedPayload);
    return 0;
}

foreach (var group in records.GroupBy(r => r.Scenario))
{
    Console.WriteLine();
    Console.WriteLine($"=== {group.Key} ===");
    foreach (var candidateGroup in group.GroupBy(record => record.CandidateId))
    {
        Console.WriteLine($"--- {candidateGroup.Key} ---");
        foreach (var r in candidateGroup.OrderBy(record => record.Run))
        {
            var tag = runs > 1 ? $"[run {r.Run}] " : string.Empty;
            if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{tag}FAILED ({r.Error ?? "unknown"})  compose {r.ComposeLatencyMs}ms");
            }
            else if (r.Declined)
            {
                Console.WriteLine($"{tag}DECLINE (no line){(r.Worth is { } w ? $"  worth {w:0.00}" : string.Empty)}  compose {r.ComposeLatencyMs}ms");
            }
            else
            {
                Console.WriteLine($"{tag}worth {r.Worth:0.00}  hook {r.Hook}  compose {r.ComposeLatencyMs}ms");
                Console.WriteLine($"{tag}  {r.Line}");
                if (r.CriticWorth is { } cw)
                    Console.WriteLine($"{tag}  critic (advisory) {cw:0.00} ({r.CriticFlaw})  {r.CriticLatencyMs}ms");
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine($"{records.Count} output(s) from {scenarios.Count} scenario(s) and {candidates.Count} candidate(s), bot source {sourceSha}. This tool does not judge; that is the session's and your job.");
return 0;

// --- helpers ---

static async Task<int> RunEpisodeReplayAsync(string[] episodeArgs)
{
    var repoRoot = FindRepoRoot();
    var defaultPath = Path.Combine(
        repoRoot,
        "tools",
        "DiscordSky.ScenarioLab",
        "fixtures",
        "episode-scenarios.json");
    var path = episodeArgs.FirstOrDefault(argument => !argument.StartsWith('-')) ?? defaultPath;
    var asJson = episodeArgs.Contains("--json", StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Episode fixture file not found: {path}");
        return 1;
    }

    List<EpisodeScenario>? scenarios;
    try
    {
        scenarios = JsonSerializer.Deserialize<List<EpisodeScenario>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"Invalid episode fixture JSON: {ex.Message}");
        return 1;
    }
    if (scenarios is not { Count: > 0 })
    {
        Console.Error.WriteLine("No episode scenarios parsed.");
        return 1;
    }

    var results = new List<EpisodeReplayResult>();
    foreach (var scenario in scenarios)
    {
        var capturedAt = scenario.CapturedAt ?? DateTimeOffset.UtcNow;
        var history = new ScenarioEpisodeHistoryReader(scenario.RecentMessages ?? Array.Empty<EpisodeFixtureMessage>());
        var builder = new InteractionEpisodeBuilder(
            history,
            new StaticOptionsMonitor<InteractionEpisodeOptions>(new InteractionEpisodeOptions
            {
                RecentMessageLimit = 6,
                RecentWindowMinutes = 10,
                ReferentConfidenceThreshold = 0.70,
            }),
            NullLogger<InteractionEpisodeBuilder>.Instance,
            () => capturedAt);
        var trigger = scenario.Trigger ?? throw new InvalidOperationException($"Scenario '{scenario.Name}' has no trigger.");
        var mediaContext = trigger.HasMedia ? "Visual media present: 1 image(s)." : null;
        var build = await builder.BuildAsync(new EpisodeTriggerEvidence(
            ChannelId: scenario.ChannelId,
            MessageId: trigger.MessageId,
            AuthorId: trigger.AuthorId,
            AuthorDisplayName: trigger.Author,
            ReferencedMessageId: trigger.ReferencedMessageId,
            View: new SemanticMessageView(
                trigger.Content,
                mediaContext,
                Array.Empty<UnfurledLink>(),
                Array.Empty<ChannelImage>(),
                trigger.MessageId,
                trigger.Timestamp,
                trigger.HasMedia)),
            episodeId: $"fixture-{scenario.Name}");

        if (!build.IsSuccess)
        {
            results.Add(new EpisodeReplayResult(
                scenario.Name,
                Passed: false,
                Error: build.Failure?.ReasonCode,
                ReferentRequired: null,
                CandidateIds: Array.Empty<ulong>(),
                SelectedReferentId: null,
                ReferentStatus: null,
                EvidenceDigest: null,
                JudgeProjectionDigest: null,
                GeneratorProjectionDigest: null));
            continue;
        }

        var episode = build.Episode!;
        var verdict = new WorthVerdict(
            0.8,
            "fixture",
            ReferentMessageId: scenario.ModelReferentId,
            ReferentConfidence: scenario.ModelReferentConfidence,
            ReferentStatus: scenario.ModelReferentId.HasValue
                ? ReferentResolutionStatus.Resolved
                : ReferentResolutionStatus.Unresolved);
        var decision = ImpulseJudge.ValidateReferentDecision(verdict, episode, 0.70);
        var judgeProjection = EpisodeProjectionBuilder.BuildJudgeProjection(episode, scenario.MoodLabel);
        var generatorProjection = EpisodeProjectionBuilder.BuildGeneratorProjection(
            episode,
            new EpisodeActionDecision(decision));
        var candidates = episode.ReferentCandidates.Select(candidate => candidate.MessageId).ToArray();
        var passed = episode.ReferentRequirement.IsRequired == scenario.ExpectReferentRequired
            && Nullable.Equals(decision.SelectedMessageId, scenario.ExpectSelectedReferentId)
            && (scenario.ExpectCandidateIds is null
                || scenario.ExpectCandidateIds.SequenceEqual(candidates));
        results.Add(new EpisodeReplayResult(
            scenario.Name,
            passed,
            Error: passed ? null : "expectation_mismatch",
            episode.ReferentRequirement.IsRequired,
            candidates,
            decision.SelectedMessageId,
            decision.Status.ToString(),
            episode.Fingerprint.EvidenceDigest,
            judgeProjection.ProjectionDigest,
            generatorProjection.ProjectionDigest));
    }

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        foreach (var result in results)
        {
            Console.WriteLine(
                $"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: required={result.ReferentRequired} " +
                $"candidates=[{string.Join(',', result.CandidateIds)}] selected={result.SelectedReferentId?.ToString() ?? "none"} " +
                $"status={result.ReferentStatus ?? result.Error}");
        }
        Console.WriteLine($"{results.Count(result => result.Passed)}/{results.Count} episode fixture(s) passed.");
    }
    return results.All(result => result.Passed) ? 0 : 1;
}

static int RunNoveltyReplay(string[] noveltyArgs)
{
    var repoRoot = FindRepoRoot();
    var defaultPath = Path.Combine(
        repoRoot,
        "tools",
        "DiscordSky.ScenarioLab",
        "fixtures",
        "novelty-scenarios.json");
    var path = noveltyArgs.FirstOrDefault(argument => !argument.StartsWith('-')) ?? defaultPath;
    var asJson = noveltyArgs.Contains("--json", StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Novelty fixture file not found: {path}");
        return 1;
    }

    List<NoveltyScenario>? scenarios;
    try
    {
        scenarios = JsonSerializer.Deserialize<List<NoveltyScenario>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"Invalid novelty fixture JSON: {ex.Message}");
        return 1;
    }
    if (scenarios is not { Count: > 0 })
    {
        Console.Error.WriteLine("No novelty scenarios parsed.");
        return 1;
    }

    var results = new List<NoveltyReplayResult>();
    foreach (var scenario in scenarios)
    {
        if (!Enum.TryParse<ColdOpenEpisodeNoveltyMode>(scenario.Mode, true, out var mode))
        {
            results.Add(new NoveltyReplayResult(scenario.Name, false, "invalid_mode", null, null, null));
            continue;
        }
        var candidate = (scenario.Candidate ?? Array.Empty<NoveltyEvidenceFixture>())
            .Select(item => new ColdOpenRoomEvidence(
                item.MessageId,
                item.ReferencedMessageId,
                item.Timestamp,
                item.Author ?? "user",
                item.RenderedLine ?? "user: fixture",
                item.TopicAnchors ?? Array.Empty<string>(),
                item.ResourceIds ?? Array.Empty<string>()))
            .ToArray();
        var prior = scenario.Prior ?? Array.Empty<ColdOpenEpisodeSnapshot>();
        var decision = ColdOpenNoveltyEvaluator.Evaluate(candidate, prior, mode);
        var passed = decision.Stage.ToString().Equals(scenario.ExpectStage, StringComparison.OrdinalIgnoreCase)
            && decision.ShouldSuppress == scenario.ExpectShouldSuppress;
        results.Add(new NoveltyReplayResult(
            scenario.Name,
            passed,
            passed ? null : "expectation_mismatch",
            decision.Stage.ToString(),
            decision.WouldSuppress,
            decision.ShouldSuppress));
    }

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        foreach (var result in results)
        {
            Console.WriteLine(
                $"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: stage={result.Stage ?? result.Error} " +
                $"would_suppress={result.WouldSuppress} should_suppress={result.ShouldSuppress}");
        }
        Console.WriteLine($"{results.Count(result => result.Passed)}/{results.Count} novelty fixture(s) passed.");
    }
    return results.All(result => result.Passed) ? 0 : 1;
}

static int RunMemoryReplay(string[] memoryArgs)
{
    var repoRoot = FindRepoRoot();
    var defaultPath = Path.Combine(
        repoRoot,
        "tools",
        "DiscordSky.ScenarioLab",
        "fixtures",
        "memory-opportunity-scenarios.json");
    var path = memoryArgs.FirstOrDefault(argument => !argument.StartsWith('-')) ?? defaultPath;
    var asJson = memoryArgs.Contains("--json", StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Memory opportunity fixture file not found: {path}");
        return 1;
    }

    List<MemoryOpportunityScenario>? scenarios;
    try
    {
        scenarios = JsonSerializer.Deserialize<List<MemoryOpportunityScenario>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"Invalid memory opportunity fixture JSON: {ex.Message}");
        return 1;
    }
    if (scenarios is not { Count: > 0 })
    {
        Console.Error.WriteLine("No memory opportunity scenarios parsed.");
        return 1;
    }

    var classifier = new DiscordSky.Bot.Memory.MemoryOpportunityClassifier();
    var results = new List<MemoryOpportunityReplayResult>();
    foreach (var scenario in scenarios)
    {
        var messages = (scenario.Messages ?? Array.Empty<MemoryOpportunityMessageFixture>())
            .Select(message => new BufferedMessage(
                message.MessageId,
                message.AuthorId,
                message.Author ?? "user",
                message.Content ?? string.Empty,
                message.Timestamp,
                message.HasMedia))
            .ToArray();
        if (messages.Length == 0)
        {
            results.Add(new MemoryOpportunityReplayResult(
                scenario.Name, false, "no_messages", null, null, null, null, null));
            continue;
        }
        var memories = (scenario.CurrentMemories ?? Array.Empty<string>())
            .Select(content => new UserMemory(
                content,
                "fixture",
                scenario.CapturedAt,
                scenario.CapturedAt,
                0))
            .ToArray();
        var features = DiscordSky.Bot.Memory.MemoryOpportunityFeatureExtractor.Extract(
            messages,
            memories,
            scenario.IsShutdownFlush,
            scenario.PriorExtractionAgeMinutes.HasValue
                ? TimeSpan.FromMinutes(scenario.PriorExtractionAgeMinutes.Value)
                : null);
        var decision = classifier.Classify(features);
        var passed = decision.WouldRun == scenario.ExpectWouldRun
            && decision.ReasonCodes.Contains(scenario.ExpectReason, StringComparer.Ordinal);
        results.Add(new MemoryOpportunityReplayResult(
            scenario.Name,
            passed,
            passed ? null : "expectation_mismatch",
            decision.WouldRun,
            decision.ReasonCodes.FirstOrDefault(),
            features.LexicalNovelty,
            features.FirstPersonAssertionCount,
            features.PreferenceIdentityChangeCount));
    }

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        foreach (var result in results)
        {
            Console.WriteLine(
                $"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: would_run={result.WouldRun} " +
                $"reason={result.Reason ?? result.Error} novelty={result.LexicalNovelty:0.00} " +
                $"first_person={result.FirstPersonAssertions} cues={result.PreferenceCues}");
        }
        Console.WriteLine($"{results.Count(result => result.Passed)}/{results.Count} memory opportunity fixture(s) passed.");
    }
    return results.All(result => result.Passed) ? 0 : 1;
}

static TrialCandidate BuildTrial(
    string rawSpec,
    LlmWorkload workload,
    IConfiguration baseConfig,
    LlmOptions baseOptions)
{
    var parts = rawSpec.Split(':', 3, StringSplitOptions.TrimEntries);
    if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        throw new ArgumentException($"Invalid candidate '{rawSpec}'. Expected provider[:model[:effort]].");

    var providerName = baseOptions.Providers.Keys.FirstOrDefault(
        name => string.Equals(name, parts[0], StringComparison.OrdinalIgnoreCase));
    if (providerName is null)
        throw new ArgumentException(
            $"Provider '{parts[0]}' is not configured. Available: [{string.Join(", ", baseOptions.Providers.Keys)}].");
    if (!providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        && !providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("ScenarioLab provider comparisons currently support OpenAI and xAI only.");
    }

    var configuredProvider = baseOptions.Providers[providerName];
    var configuredProfile = configuredProvider.GetProfile(workload);
    var model = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
        ? parts[1]
        : configuredProfile.Model;
    var effort = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
        ? parts[2]
        : configuredProfile.ReasoningEffort;

    // Grok 4.5 defaults to high and cannot disable reasoning. An implicit default would make comparisons
    // needlessly slow and ambiguous, so use the documented balanced setting unless the caller is explicit.
    if (providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase)
        && model.Equals("grok-4.5", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(effort))
    {
        effort = "medium";
    }
    if (!string.IsNullOrWhiteSpace(effort)
        && !Enum.TryParse<ReasoningEffort>(effort, ignoreCase: true, out _))
    {
        throw new ArgumentException($"Unknown reasoning effort '{effort}' in candidate '{rawSpec}'.");
    }
    if (providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase)
        && model.Equals("grok-4.5", StringComparison.OrdinalIgnoreCase)
        && string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("Grok 4.5 reasoning cannot be disabled; choose low, medium, or high.");
    }

    var apiKey = ResolveApiKey(baseConfig, providerName);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        var environmentName = providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase)
            ? "XAI_API_KEY"
            : "OPENAI_API_KEY";
        throw new ArgumentException(
            $"No API key for provider '{providerName}'. Set LLM:Providers:{providerName}:ApiKey in ScenarioLab " +
            $"user-secrets or export {environmentName}.");
    }

    var modelKey = workload switch
    {
        LlmWorkload.ColdOpen => "ColdOpenModel",
        LlmWorkload.ColdOpenCritic => "ColdOpenCriticModel",
        _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, "ScenarioLab trial workload is unsupported."),
    };
    var effortKey = workload switch
    {
        LlmWorkload.ColdOpen => "ColdOpenReasoningEffort",
        LlmWorkload.ColdOpenCritic => "ColdOpenCriticReasoningEffort",
        _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, "ScenarioLab trial workload is unsupported."),
    };
    var overlay = new Dictionary<string, string?>
    {
        ["LLM:ActiveProvider"] = providerName,
        [$"LLM:Providers:{providerName}:ApiKey"] = apiKey,
        [$"LLM:Providers:{providerName}:ChatModel"] = model,
        [$"LLM:Providers:{providerName}:{modelKey}"] = model,
        [$"LLM:Providers:{providerName}:{effortKey}"] = effort,
    };
    var candidateConfig = new ConfigurationBuilder()
        .AddConfiguration(baseConfig)
        .AddInMemoryCollection(overlay)
        .Build();
    var candidateOptions = candidateConfig.GetSection("LLM").Get<LlmOptions>() ?? new LlmOptions();
    var candidateProvider = candidateOptions.GetActiveProvider();
    var resolvedProfile = candidateProvider.GetProfile(workload);
    var chatClient = LlmChatClientFactory.Create(candidateProvider, resolvedProfile.Model);
    var id = $"{providerName}:{resolvedProfile.Model}:{resolvedProfile.ReasoningEffort ?? "default"}";

    return new TrialCandidate(
        id,
        providerName,
        resolvedProfile.Model,
        resolvedProfile.ReasoningEffort,
        candidateProvider.UseResponsesApi,
        candidateOptions,
        chatClient);
}

static string? ResolveApiKey(IConfiguration config, string providerName)
{
    var configured = config[$"LLM:Providers:{providerName}:ApiKey"];
    if (!string.IsNullOrWhiteSpace(configured)) return configured;

    var environmentName = providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase)
        ? "XAI_API_KEY"
        : "OPENAI_API_KEY";
    return Environment.GetEnvironmentVariable(environmentName);
}

static (string ReviewPath, string RevealPath) WriteBlindReviewArtifacts(
    string reviewPath,
    int seed,
    IReadOnlyList<Scenario> scenarios,
    IReadOnlyList<OutputRecord> records,
    string sourceSha,
    DateTimeOffset generatedAt)
{
    var scenarioByName = scenarios.ToDictionary(
        scenario => scenario.Name ?? "(unnamed)",
        StringComparer.Ordinal);
    var pairs = records
        .GroupBy(record => (record.Scenario, record.Run))
        .Select(group =>
        {
            var outputs = group.ToList();
            if (outputs.Count != 2)
                throw new InvalidOperationException(
                    $"Blind review expected two outputs for {group.Key.Scenario} run {group.Key.Run}, found {outputs.Count}.");
            return new ReviewPair(group.Key.Scenario, group.Key.Run, outputs[0], outputs[1]);
        })
        .ToList();

    var random = new Random(seed);
    for (var i = pairs.Count - 1; i > 0; i--)
    {
        var swapIndex = random.Next(i + 1);
        (pairs[i], pairs[swapIndex]) = (pairs[swapIndex], pairs[i]);
    }

    var firstSideOffset = random.Next(2);
    var review = new System.Text.StringBuilder();
    review.AppendLine("# GPT vs Grok Cold-Open Blind Review");
    review.AppendLine();
    review.AppendLine($"Generated: {generatedAt:O}");
    review.AppendLine($"Bot source: `{sourceSha}`");
    review.AppendLine();
    review.AppendLine("Provider identities are intentionally hidden. Do not open the reveal key until every answer is filled in.");
    review.AppendLine("For each pair, judge the action as it would appear in that room. Staying silent can be the correct action.");
    review.AppendLine();

    var revealItems = new List<object>();
    for (var index = 0; index < pairs.Count; index++)
    {
        var pair = pairs[index];
        var swapSides = (index + firstSideOffset) % 2 == 1;
        var a = swapSides ? pair.Second : pair.First;
        var b = swapSides ? pair.First : pair.Second;
        var itemId = $"pair-{index + 1:000}";
        scenarioByName.TryGetValue(pair.Scenario, out var scenario);

        review.AppendLine($"## {itemId}: {EscapeNonAscii(pair.Scenario)} (run {pair.Run})");
        review.AppendLine();
        review.AppendLine("### Room");
        review.AppendLine();
        if (scenario?.RecentLines is { Count: > 0 })
        {
            foreach (var line in scenario.RecentLines)
                review.AppendLine($"> {EscapeNonAscii(line.Replace("\n", " ").Replace("\r", " "))}");
        }
        else
        {
            review.AppendLine("> (No fresh room chatter.)");
        }
        review.AppendLine();
        AppendBlindDraft(review, "A", a);
        AppendBlindDraft(review, "B", b);
        review.AppendLine("Your preference (`A` / `B` / `tie` / `both bad`): ");
        review.AppendLine();
        review.AppendLine("A individually correct (`yes` / `borderline` / `no`): ");
        review.AppendLine();
        review.AppendLine("B individually correct (`yes` / `borderline` / `no`): ");
        review.AppendLine();
        review.AppendLine("Why, if useful: ");
        review.AppendLine();

        revealItems.Add(new
        {
            itemId,
            scenario = pair.Scenario,
            run = pair.Run,
            a = a.CandidateId,
            b = b.CandidateId,
        });
    }

    review.AppendLine("## Overall");
    review.AppendLine();
    review.AppendLine("Which side felt more consistently like the bot we should ship, if either: ");
    review.AppendLine();
    review.AppendLine("Any repeated failure pattern: ");

    var fullReviewPath = Path.GetFullPath(reviewPath);
    var reviewDirectory = Path.GetDirectoryName(fullReviewPath) ?? Directory.GetCurrentDirectory();
    var reviewStem = Path.GetFileNameWithoutExtension(fullReviewPath);
    var revealPath = Path.Combine(reviewDirectory, $"{reviewStem}.reveal.local.json");
    var reveal = new
    {
        seed,
        generatedAt,
        botSourceSha = sourceSha,
        items = revealItems,
    };

    WriteLocalFile(fullReviewPath, review.ToString());
    WriteLocalFile(revealPath, JsonSerializer.Serialize(reveal, new JsonSerializerOptions { WriteIndented = true }));
    return (fullReviewPath, revealPath);
}

static void AppendBlindDraft(System.Text.StringBuilder review, string label, OutputRecord record)
{
    review.AppendLine($"### Draft {label}");
    review.AppendLine();
    review.AppendLine(!record.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
        ? "`GENERATION FAILED`"
        : record.Declined
            ? "`STAY SILENT`"
            : $"> {EscapeNonAscii((record.Line ?? string.Empty).Replace("\n", " ").Replace("\r", " "))}");
    review.AppendLine();
    review.AppendLine($"Composer worth: `{record.Worth?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}`");
    review.AppendLine($"Hook: `{EscapeNonAscii(record.Hook ?? "(none)")}`");
    review.AppendLine();
}

static string EscapeNonAscii(string value)
{
    var escaped = new System.Text.StringBuilder(value.Length);
    foreach (var character in value)
    {
        if (character is >= ' ' and <= '~' || character is '\n' or '\r' or '\t')
            escaped.Append(character);
        else
            escaped.Append($"\\u{(int)character:X4}");
    }
    return escaped.ToString();
}

static void WriteLocalFile(string path, string content)
{
    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    File.WriteAllText(fullPath, content);
}

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
    // Untracked files (local eval scratch, fixtures, docs) do not make the committed bot source dirty.
    return string.IsNullOrWhiteSpace(Run("status --porcelain --untracked-files=no")) ? sha : sha + "-dirty";
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

internal sealed record TrialCandidate(
    string Id,
    string Provider,
    string Model,
    string? ReasoningEffort,
    bool UseResponsesApi,
    LlmOptions Options,
    IChatClient ChatClient);

internal sealed record ReviewPair(
    string Scenario,
    int Run,
    OutputRecord First,
    OutputRecord Second);

internal sealed record OutputRecord(
    string CandidateId, string Provider, string Model, string? ReasoningEffort,
    string Scenario, int Run, string Status, string? Error,
    double? Worth, string? Hook, string? Line, bool Declined,
    long ComposeLatencyMs, double? CriticWorth, string? CriticFlaw, long? CriticLatencyMs);

internal sealed record EpisodeScenario(
    string Name,
    ulong ChannelId,
    DateTimeOffset? CapturedAt,
    string? MoodLabel,
    EpisodeFixtureMessage? Trigger,
    IReadOnlyList<EpisodeFixtureMessage>? RecentMessages,
    ulong? ModelReferentId,
    double? ModelReferentConfidence,
    bool ExpectReferentRequired,
    IReadOnlyList<ulong>? ExpectCandidateIds,
    ulong? ExpectSelectedReferentId);

internal sealed record EpisodeFixtureMessage(
    ulong MessageId,
    ulong AuthorId,
    string Author,
    string Content,
    DateTimeOffset Timestamp,
    ulong? ReferencedMessageId = null,
    bool IsBot = false,
    bool HasMedia = false)
{
    public EpisodeMessage ToEpisodeMessage() => new(
        MessageId,
        AuthorId,
        Author,
        Content,
        Timestamp,
        ReferencedMessageId,
        IsBot,
        HasMedia ? "Visual media present: 1 image(s)." : null);
}

internal sealed record EpisodeReplayResult(
    string Name,
    bool Passed,
    string? Error,
    bool? ReferentRequired,
    IReadOnlyList<ulong> CandidateIds,
    ulong? SelectedReferentId,
    string? ReferentStatus,
    string? EvidenceDigest,
    string? JudgeProjectionDigest,
    string? GeneratorProjectionDigest);

internal sealed class ScenarioEpisodeHistoryReader : IEpisodeHistoryReader
{
    private readonly IReadOnlyList<EpisodeFixtureMessage> _messages;

    public ScenarioEpisodeHistoryReader(IReadOnlyList<EpisodeFixtureMessage> messages) => _messages = messages;

    public Task<IReadOnlyList<EpisodeMessage>> GetRecentAsync(
        ulong channelId,
        ulong triggerMessageId,
        int limit,
        DateTimeOffset after,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EpisodeMessage>>(_messages
        .Where(message => message.MessageId != triggerMessageId && message.Timestamp >= after)
        .OrderBy(message => message.Timestamp)
        .TakeLast(limit)
        .Select(message => message.ToEpisodeMessage())
        .ToArray());

    public Task<EpisodeMessage?> GetMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken) => Task.FromResult(
        _messages.FirstOrDefault(message => message.MessageId == messageId)?.ToEpisodeMessage());
}

internal sealed record NoveltyScenario(
    string Name,
    string Mode,
    IReadOnlyList<NoveltyEvidenceFixture>? Candidate,
    IReadOnlyList<ColdOpenEpisodeSnapshot>? Prior,
    string ExpectStage,
    bool ExpectShouldSuppress);

internal sealed record NoveltyEvidenceFixture(
    ulong MessageId,
    ulong? ReferencedMessageId,
    DateTimeOffset Timestamp,
    string? Author,
    string? RenderedLine,
    IReadOnlyList<string>? TopicAnchors,
    IReadOnlyList<string>? ResourceIds);

internal sealed record NoveltyReplayResult(
    string Name,
    bool Passed,
    string? Error,
    string? Stage,
    bool? WouldSuppress,
    bool? ShouldSuppress);

internal sealed record MemoryOpportunityScenario(
    string Name,
    DateTimeOffset CapturedAt,
    IReadOnlyList<MemoryOpportunityMessageFixture>? Messages,
    IReadOnlyList<string>? CurrentMemories,
    bool IsShutdownFlush,
    double? PriorExtractionAgeMinutes,
    bool ExpectWouldRun,
    string ExpectReason);

internal sealed record MemoryOpportunityMessageFixture(
    ulong MessageId,
    ulong AuthorId,
    string? Author,
    string? Content,
    DateTimeOffset Timestamp,
    bool HasMedia = false);

internal sealed record MemoryOpportunityReplayResult(
    string Name,
    bool Passed,
    string? Error,
    bool? WouldRun,
    string? Reason,
    double? LexicalNovelty,
    int? FirstPersonAssertions,
    int? PreferenceCues);

/// <summary>Fixed-value IOptionsMonitor, matching the repo's test double signature so it builds warning-free.</summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
