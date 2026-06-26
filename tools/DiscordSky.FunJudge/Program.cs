using System.Text.Json;
using DiscordSky.Bot.Memory.Analysis;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.AI;
using OpenAI;

// Pairwise LLM-judge runner (fun_assessment_2026-06-25 P3, the second half of 3.11). Reads transcript
// replies, asks a strong model to compare them pairwise (in-character + funny + chaotic), and prints a
// win-rate ranking. Optionally annotates with P1 reaction counts to check the judge against real reception.
//
// Usage:
//   OPENAI_API_KEY=sk-... dotnet run --project tools/DiscordSky.FunJudge -- <transcripts-dir-or-file> \
//       [--model gpt-5.5] [--sample 12] [--persona Robotnik] [--reactions <dir>]

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: OPENAI_API_KEY=... dotnet run --project tools/DiscordSky.FunJudge -- <transcripts-dir-or-file> [--model gpt-5.5] [--sample 12] [--persona Robotnik] [--reactions <dir>]");
    return 0;
}

var path = args[0];
var model = "gpt-5.5";
string? persona = null;
string? reactionsDir = null;
var sample = 12;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model" when i + 1 < args.Length: model = args[++i]; break;
        case "--persona" when i + 1 < args.Length: persona = args[++i]; break;
        case "--reactions" when i + 1 < args.Length: reactionsDir = args[++i]; break;
        case "--sample" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): sample = s; i++; break;
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
    ? Directory.EnumerateFiles(path, "transcript-*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList()
    : File.Exists(path) ? [path] : new List<string>();
if (files.Count == 0) { Console.Error.WriteLine($"No transcripts found at {path}"); return 1; }

var replies = new List<string>();
foreach (var f in files)
{
    foreach (var line in File.ReadLines(f))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            var e = JsonSerializer.Deserialize<TranscriptEntry>(line);
            if (e is null || string.IsNullOrWhiteSpace(e.Reply)) continue;
            if (persona is not null && !(e.Persona?.Contains(persona, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            replies.Add(e.Reply);
        }
        catch (JsonException) { }
    }
}

if (replies.Count < 2) { Console.Error.WriteLine("Need at least 2 replies to compare."); return 1; }

// Sample evenly down to bound the O(n^2) LLM calls.
if (replies.Count > sample)
{
    var step = (double)replies.Count / sample;
    replies = Enumerable.Range(0, sample).Select(k => replies[(int)(k * step)]).ToList();
}

var reactionExcerpts = new List<string>();
if (reactionsDir is not null && Directory.Exists(reactionsDir))
{
    foreach (var f in Directory.EnumerateFiles(reactionsDir, "reactions-*.jsonl"))
    {
        foreach (var line in File.ReadLines(f))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<ReactionEvent>(line);
                if (r?.ReplyExcerpt is { Length: > 0 } ex) reactionExcerpts.Add(ex);
            }
            catch (JsonException) { }
        }
    }
}

var chat = new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient();
var pairCount = replies.Count * (replies.Count - 1) / 2;
Console.WriteLine($"Judging {replies.Count} replies ({pairCount} pairwise comparisons) with model {model}...");

char Judge(string a, string b)
{
    var prompt = FunJudge.BuildComparisonPrompt(a, b);
    var resp = chat.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]).GetAwaiter().GetResult();
    return FunJudge.ParseChoice(resp.Text);
}

var ranked = FunJudge.Rank(replies, Judge);

Console.WriteLine();
Console.WriteLine("=== Top replies (highest pairwise win rate) ===");
foreach (var r in ranked.Take(3)) PrintRanked(r);
Console.WriteLine();
Console.WriteLine("=== Bottom replies (lowest win rate) ===");
foreach (var r in ranked.TakeLast(3)) PrintRanked(r);

if (reactionExcerpts.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Calibration: win rate vs reaction count (do top-ranked replies draw more reactions?)");
    foreach (var r in ranked)
    {
        Console.WriteLine($"  winrate {r.WinRate:0.00}  reactions {FunJudge.ReactionCountFor(r.Reply, reactionExcerpts)}  | {Trunc(r.Reply)}");
    }
}

return 0;

void PrintRanked(JudgedReply r)
{
    var rx = reactionExcerpts.Count > 0 ? $", {FunJudge.ReactionCountFor(r.Reply, reactionExcerpts)} reactions" : "";
    Console.WriteLine($"[winrate {r.WinRate:0.00}, {r.Wins}/{r.Comparisons}{rx}] {Trunc(r.Reply)}");
}

static string Trunc(string s) => s.Length <= 160 ? s : s[..160] + "...";
