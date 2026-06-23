using System.Text.Json;
using DiscordSky.Bot.Memory.Analysis;
using DiscordSky.Bot.Memory.Logging;

// Fun-score harness runner (fun_assessment_2026-06-21 §3.11). Reads transcript JSONL written by the
// bot's FileBackedTranscriptSink (pull them per the discord-sky-ops runbook) and prints the
// behavioral scorecard.
//
// Usage:
//   dotnet run --project tools/DiscordSky.FunScore -- <transcripts-dir-or-file> [--persona <name>] [--json]

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: dotnet run --project tools/DiscordSky.FunScore -- <transcripts-dir-or-file> [--persona <name>] [--json]");
    Console.WriteLine();
    Console.WriteLine("  <transcripts-dir-or-file>  A directory of transcript-*.jsonl files, or a single .jsonl file.");
    Console.WriteLine("  --persona <name>           Only score replies whose persona contains <name> (e.g. Robotnik).");
    Console.WriteLine("  --json                     Emit the raw report as JSON instead of the formatted scorecard.");
    return 0;
}

var path = args[0];
string? persona = null;
var asJson = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--persona" when i + 1 < args.Length:
            persona = args[++i];
            break;
        case "--json":
            asJson = true;
            break;
    }
}

var files = new List<string>();
if (Directory.Exists(path))
{
    files.AddRange(Directory.EnumerateFiles(path, "transcript-*.jsonl"));
}
else if (File.Exists(path))
{
    files.Add(path);
}
else
{
    Console.Error.WriteLine($"Path not found: {path}");
    return 1;
}

if (files.Count == 0)
{
    Console.Error.WriteLine($"No transcript-*.jsonl files found under {path}");
    return 1;
}

var entries = new List<TranscriptEntry>();
var skipped = 0;
foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
{
    foreach (var line in File.ReadLines(file))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            var entry = JsonSerializer.Deserialize<TranscriptEntry>(line);
            if (entry is not null) entries.Add(entry);
        }
        catch (JsonException)
        {
            skipped++;
        }
    }
}

var report = FunScoreAnalyzer.Analyze(entries, personaFilter: persona);

if (asJson)
{
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
else
{
    Console.Write(report.Format());
    Console.WriteLine($"\n({files.Count} file(s) read{(skipped > 0 ? $", {skipped} unparseable line(s) skipped" : "")}.)");
}

return report.AllPass ? 0 : 2;
