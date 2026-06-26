namespace DiscordSky.Bot.Memory.Analysis;

/// <summary>
/// The pairwise LLM-judge core (fun_assessment_2026-06-25 P3, the second half of 3.11). The deterministic
/// <see cref="FunScoreAnalyzer"/> measures what a regex can; this measures the part it cannot, holistic
/// "is this reply more in character and funnier", by asking a strong model to compare replies pairwise.
///
/// <para>
/// Pairwise by design: section 6.6 of the prior audit (HumorRank, COMIC) argues humor is best judged by
/// tournament comparison, not absolute scores, and calibrated to real audience reactions. This class is
/// the model-agnostic logic (prompt, parse, tally); the runner in tools/DiscordSky.FunJudge wires a real
/// model into <see cref="Rank"/>, and the P1 reaction data is the calibration target.
/// </para>
/// </summary>
public static class FunJudge
{
    /// <summary>What the judge optimizes for. The bot is supposed to be a chaotic villain, not an assistant.</summary>
    public const string Criterion =
        "more in character as Dr. Robotnik (the vain, scheming, gleefully unhelpful Adventures of Sonic " +
        "the Hedgehog cartoon villain) AND funnier and more chaotic";

    public static string BuildComparisonPrompt(string replyA, string replyB) =>
        "You are judging a Discord bot that role-plays Dr. Robotnik from Adventures of Sonic the Hedgehog. " +
        "The bot is SUPPOSED to be vain, scheming, unhelpful, and funny, never a polite assistant. " +
        "A reply is better when it is " + Criterion + ".\n\n" +
        "Reply A:\n" + replyA + "\n\n" +
        "Reply B:\n" + replyB + "\n\n" +
        "Answer with exactly one character: A or B. If they are genuinely identical in quality, answer A.";

    /// <summary>Extract the model's A/B verdict from free-form output, defaulting to A.</summary>
    public static char ParseChoice(string? modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput)) return 'A';

        // The model is told to answer with a single character, so trust the first non-space char first.
        var first = char.ToUpperInvariant(modelOutput.TrimStart()[0]);
        if (first is 'A' or 'B') return first;

        // Fallback: the first A/B character anywhere.
        foreach (var ch in modelOutput)
        {
            var u = char.ToUpperInvariant(ch);
            if (u is 'A' or 'B') return u;
        }
        return 'A';
    }

    /// <summary>
    /// Round-robin pairwise tally. <paramref name="judge"/> returns 'A' or 'B' for which of two replies
    /// wins; injecting it keeps this pure and testable (the runner passes a real-LLM judge).
    /// </summary>
    public static IReadOnlyList<JudgedReply> Rank(IReadOnlyList<string> replies, Func<string, string, char> judge)
    {
        var wins = new int[replies.Count];
        var comparisons = new int[replies.Count];

        for (var i = 0; i < replies.Count; i++)
        {
            for (var j = i + 1; j < replies.Count; j++)
            {
                var choice = judge(replies[i], replies[j]);
                comparisons[i]++;
                comparisons[j]++;
                if (choice == 'B') wins[j]++; else wins[i]++;
            }
        }

        return Enumerable.Range(0, replies.Count)
            .Select(k => new JudgedReply(replies[k], wins[k], comparisons[k]))
            .OrderByDescending(r => r.WinRate)
            .ToList();
    }

    /// <summary>
    /// Calibration join: how many reactions a reply drew, matched by the reaction's stored reply excerpt
    /// being a prefix of the reply text (the excerpt is the first ~200 chars of the bot message). Lets the
    /// runner check whether the judge's ranking tracks real reception.
    /// </summary>
    public static int ReactionCountFor(string reply, IReadOnlyList<string> reactionExcerpts) =>
        reactionExcerpts.Count(ex => !string.IsNullOrEmpty(ex) && reply.StartsWith(ex, StringComparison.Ordinal));
}

/// <summary>A reply with its pairwise win count and how many comparisons it took part in.</summary>
public sealed record JudgedReply(string Reply, int Wins, int Comparisons)
{
    public double WinRate => Comparisons == 0 ? 0.0 : (double)Wins / Comparisons;
}
