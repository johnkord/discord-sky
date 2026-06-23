using System.Globalization;
using System.Text;

namespace DiscordSky.Bot.Memory.Analysis;

/// <summary>
/// Pass/fail thresholds for the harness, taken straight from the fun_assessment_2026-06-21 §3.11
/// target metrics. Defaults encode the audit's stated bars; override to experiment.
/// </summary>
public sealed record FunScoreTargets
{
    /// <summary>The over-used catchphrase whose rate is the headline number (audit baseline: 62%).</summary>
    public string HeadlineToken { get; init; } = "Scratch";

    /// <summary>"Scratch" mention rate target: under 25%.</summary>
    public double MaxHeadlineTokenRate { get; init; } = 0.25;

    /// <summary>A meaningful share of true one-liners (the audit had zero under 213 chars).</summary>
    public double MinShortShare { get; init; } = 0.15;

    /// <summary>Chars below which a reply counts as a punchy one-liner.</summary>
    public int ShortLengthChars { get; init; } = 120;

    /// <summary>Chars above which a reply counts as a rant (the audit capped at 501).</summary>
    public int LongLengthChars { get; init; } = 600;

    /// <summary>Helpful-leak rate target: near zero.</summary>
    public double MaxHelpfulLeakRate { get; init; } = 0.10;

    /// <summary>In-character rate target: above 95%.</summary>
    public double MinInCharacterRate { get; init; } = 0.95;

    /// <summary>Formulaic-opening rate target: kill the "Ohohoho, [name]" template.</summary>
    public double MaxOpeningRepetitionRate { get; init; } = 0.30;

    /// <summary>Catchphrases tracked for over-use (the audit's worn grooves).</summary>
    public IReadOnlyList<string> TrackedTokens { get; init; } =
        ["Scratch", "Grounder", "Ohohoho", "Badnik"];

    /// <summary>
    /// Lowercase substrings that suggest a reply is still in the Robotnik voice. A crude proxy for the
    /// in-character rate pending the LLM judge, but it catches the obvious assistant-without-a-hat case.
    /// </summary>
    public IReadOnlyList<string> PersonaMarkers { get; init; } =
    [
        "robotnik", "sonic", "hedgehog", "ohohoho", "bahaha", "minion", "scheme", "egg", "scratch",
        "grounder", "badnik", "fool", "doomed", "mobius", "prrr", "mustache", "pingas", "coconuts",
        "momma", "henchman", "henchperson", "snooping",
    ];

    /// <summary>
    /// High-precision assistant tells: any one of these in a reply marks it as a helpful leak.
    /// </summary>
    public IReadOnlyList<string> HelpfulLeakStrongMarkers { get; init; } =
    [
        "here's how", "here are the", "step 1", "step 2", "follow these steps",
        "in summary", "to summarize", "i hope this helps", "let me know if",
    ];

    /// <summary>
    /// Low-precision tells that also show up in villain banter ("you should grovel"). Only count as a
    /// leak when two or more cluster inside a long, earnest block.
    /// </summary>
    public IReadOnlyList<string> HelpfulLeakWeakMarkers { get; init; } =
    [
        "you can ", "you should ", "you could ", "firstly", "secondly",
    ];
}

/// <summary>How often a tracked catchphrase appears across the corpus.</summary>
public sealed record TokenRate(string Token, int Count, double Rate);

/// <summary>Reply length distribution. The fix for the "every reply is the same size" monotone.</summary>
public sealed record LengthStats(int Min, int Median, double Mean, int Max, double ShortShare, double LongShare);

/// <summary>A formulaic opening phrase (first two words) and how many replies share it.</summary>
public sealed record OpeningCount(string Opening, int Count);

/// <summary>The computed scorecard for a transcript corpus, with pass/fail against <see cref="FunScoreTargets"/>.</summary>
public sealed record FunScoreReport(
    int TotalReplies,
    IReadOnlyDictionary<string, int> PersonaCounts,
    IReadOnlyList<TokenRate> TokenRates,
    LengthStats Length,
    double OpeningRepetitionRate,
    IReadOnlyList<OpeningCount> TopRepeatedOpenings,
    double HelpfulLeakRate,
    double InCharacterRate,
    FunScoreTargets Targets)
{
    public double HeadlineTokenRate => TokenRates
        .FirstOrDefault(t => string.Equals(t.Token, Targets.HeadlineToken, StringComparison.OrdinalIgnoreCase))
        ?.Rate ?? 0.0;

    public bool HeadlinePass => HeadlineTokenRate <= Targets.MaxHeadlineTokenRate;
    public bool LengthSpreadPass => Length.ShortShare >= Targets.MinShortShare
        && (Length.Max > Targets.LongLengthChars || Length.LongShare > 0);
    public bool OpeningRepetitionPass => OpeningRepetitionRate <= Targets.MaxOpeningRepetitionRate;
    public bool HelpfulLeakPass => HelpfulLeakRate <= Targets.MaxHelpfulLeakRate;
    public bool InCharacterPass => InCharacterRate >= Targets.MinInCharacterRate;

    public bool AllPass => HeadlinePass && LengthSpreadPass && OpeningRepetitionPass
        && HelpfulLeakPass && InCharacterPass;

    /// <summary>Human-readable scorecard for the console runner.</summary>
    public string Format()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("=== Discord Sky fun-score scorecard ===");
        sb.AppendLine($"Replies analyzed: {TotalReplies}");
        if (PersonaCounts.Count > 0)
        {
            var personas = string.Join(", ", PersonaCounts.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine($"Personas: {personas}");
        }
        sb.AppendLine();

        sb.AppendLine($"[{Flag(HeadlinePass)}] Headline '{Targets.HeadlineToken}' rate: {Pct(HeadlineTokenRate)} (target <= {Pct(Targets.MaxHeadlineTokenRate)})");
        foreach (var t in TokenRates.Where(t => !string.Equals(t.Token, Targets.HeadlineToken, StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine($"        '{t.Token}' rate: {Pct(t.Rate)} ({t.Count}/{TotalReplies})");
        }
        sb.AppendLine();

        sb.AppendLine($"[{Flag(LengthSpreadPass)}] Length spread: min {Length.Min}, median {Length.Median}, mean {Length.Mean.ToString("0", c)}, max {Length.Max}");
        sb.AppendLine($"        one-liners (<{Targets.ShortLengthChars}): {Pct(Length.ShortShare)} (target >= {Pct(Targets.MinShortShare)}); rants (>{Targets.LongLengthChars}): {Pct(Length.LongShare)}");
        sb.AppendLine();

        sb.AppendLine($"[{Flag(OpeningRepetitionPass)}] Formulaic openings: {Pct(OpeningRepetitionRate)} share repeated first-two-words (target <= {Pct(Targets.MaxOpeningRepetitionRate)})");
        foreach (var o in TopRepeatedOpenings)
        {
            sb.AppendLine($"        repeated opening: \"{o.Opening}\" x{o.Count}");
        }
        sb.AppendLine($"[{Flag(HelpfulLeakPass)}] Helpful-leak rate (heuristic): {Pct(HelpfulLeakRate)} (target <= {Pct(Targets.MaxHelpfulLeakRate)})");
        sb.AppendLine($"[{Flag(InCharacterPass)}] In-character rate (heuristic): {Pct(InCharacterRate)} (target >= {Pct(Targets.MinInCharacterRate)})");
        sb.AppendLine();
        sb.AppendLine($"Overall: {(AllPass ? "PASS" : "NEEDS WORK")}");
        sb.AppendLine();
        sb.AppendLine("Note: helpful-leak and in-character are regex proxies. Holistic funniness still needs a");
        sb.AppendLine("pairwise LLM judge calibrated to real reactions (emoji/replies), per fun_assessment §6.6.");
        return sb.ToString();
    }

    private static string Flag(bool pass) => pass ? "PASS" : "FAIL";
    private static string Pct(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
