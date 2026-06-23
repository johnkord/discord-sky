using DiscordSky.Bot.Memory.Analysis;
using DiscordSky.Bot.Memory.Logging;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the deterministic fun-score harness (fun_assessment_2026-06-21 §3.11). Validates the
/// behavioral metrics on a small synthetic corpus so the numbers the audit relies on are trustworthy.
/// </summary>
public class FunScoreAnalyzerTests
{
    private static TranscriptEntry E(string reply, string persona = "Robotnik from AOSTH", string kind = "Ambient")
        => new(DateTimeOffset.UtcNow, 1UL, "u", 2UL, "chan", persona, kind, "prompt", reply);

    // A deliberately flawed corpus: over-uses Ohohoho, repeats an opening, and leaks one helpful reply.
    private static readonly List<TranscriptEntry> FlawedCorpus =
    [
        E("Ohohoho, you fool! Scratch will get you."),
        E("Ohohoho, you simpleton."),
        E("Pingas."),
        E("Here's how you can fix your code: step 1, run the tests. I hope this helps!"),
    ];

    [Fact]
    public void Analyze_ComputesTokenRates()
    {
        var report = FunScoreAnalyzer.Analyze(FlawedCorpus);

        Assert.Equal(4, report.TotalReplies);
        Assert.Equal(0.25, RateOf(report, "Scratch"), 3);
        Assert.Equal(0.50, RateOf(report, "Ohohoho"), 3);
        Assert.Equal(0.0, RateOf(report, "Grounder"), 3);
    }

    [Fact]
    public void Analyze_ComputesLengthStats()
    {
        var report = FunScoreAnalyzer.Analyze(FlawedCorpus);

        Assert.Equal(7, report.Length.Min);    // "Pingas."
        Assert.Equal(75, report.Length.Max);   // the helpful leak
        Assert.Equal(1.0, report.Length.ShortShare, 3); // all under 120
        Assert.Equal(0.0, report.Length.LongShare, 3);
    }

    [Fact]
    public void Analyze_DetectsFormulaicOpenings()
    {
        var report = FunScoreAnalyzer.Analyze(FlawedCorpus);
        // Two of four replies open with "ohohoho you".
        Assert.Equal(0.5, report.OpeningRepetitionRate, 3);
    }

    [Fact]
    public void Analyze_DetectsHelpfulLeakAndOutOfCharacter()
    {
        var report = FunScoreAnalyzer.Analyze(FlawedCorpus);
        Assert.Equal(0.25, report.HelpfulLeakRate, 3); // the "here's how... step 1... I hope this helps" reply
        Assert.Equal(0.75, report.InCharacterRate, 3); // that reply has no persona markers
    }

    [Fact]
    public void Analyze_FlawedCorpus_FailsMostTargets()
    {
        var report = FunScoreAnalyzer.Analyze(FlawedCorpus);
        Assert.False(report.LengthSpreadPass);     // no rants at all
        Assert.False(report.OpeningRepetitionPass); // 50% repeated openings
        Assert.False(report.HelpfulLeakPass);
        Assert.False(report.InCharacterPass);
        Assert.False(report.AllPass);
    }

    [Fact]
    public void Analyze_HealthyCorpus_PassesAllTargets()
    {
        var longRant = string.Concat(Enumerable.Repeat("Behold my egg-shaped genius, hedgehog! ", 20)); // > 600 chars
        var healthy = new List<TranscriptEntry>
        {
            E("Pingas!"),
            E("Bow before me, minion."),
            E("I shall conquer Mobius by Tuesday."),
            E(longRant),
        };

        var report = FunScoreAnalyzer.Analyze(healthy);

        Assert.True(report.HeadlinePass);          // no Scratch
        Assert.True(report.LengthSpreadPass);      // short replies plus one rant
        Assert.True(report.OpeningRepetitionPass); // all distinct openings
        Assert.True(report.HelpfulLeakPass);
        Assert.True(report.InCharacterPass);
        Assert.True(report.AllPass);
    }

    [Fact]
    public void Analyze_PersonaFilter_ExcludesOtherPersonas()
    {
        var mixed = new List<TranscriptEntry>(FlawedCorpus) { E("Scratch Scratch Scratch", persona: "Weird Al") };

        var report = FunScoreAnalyzer.Analyze(mixed, personaFilter: "Robotnik");

        Assert.Equal(4, report.TotalReplies);
        Assert.DoesNotContain("Weird Al", report.PersonaCounts.Keys);
        Assert.Equal(0.25, RateOf(report, "Scratch"), 3); // the Weird Al spam is excluded
    }

    [Fact]
    public void Analyze_EmptyCorpus_DoesNotThrow()
    {
        var report = FunScoreAnalyzer.Analyze(Array.Empty<TranscriptEntry>());
        Assert.Equal(0, report.TotalReplies);
        Assert.Equal(0, report.Length.Max);
        Assert.Contains("Replies analyzed: 0", report.Format());
    }

    [Fact]
    public void Analyze_SurfacesTopRepeatedOpenings()
    {
        var report = FunScoreAnalyzer.Analyze(FlawedCorpus);
        Assert.Contains(report.TopRepeatedOpenings, o => o.Opening == "ohohoho you" && o.Count == 2);
    }

    [Fact]
    public void Analyze_ShortMenaceWithWeakTells_IsNotAHelpfulLeak()
    {
        // Two weak tells ("you should", "you can") but short and in-voice: not a leak.
        var corpus = new List<TranscriptEntry> { E("You should grovel, and you can start now, fool!") };
        var report = FunScoreAnalyzer.Analyze(corpus);
        Assert.Equal(0.0, report.HelpfulLeakRate, 3);
    }

    private static double RateOf(FunScoreReport report, string token)
        => report.TokenRates.First(t => t.Token == token).Rate;
}
