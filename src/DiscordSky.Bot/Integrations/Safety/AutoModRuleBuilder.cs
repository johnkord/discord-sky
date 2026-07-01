namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// The content of a Discord AutoMod keyword rule, produced independently of Discord.Net so it can be unit
/// tested. The sync service turns this into the SDK's rule properties plus actions.
/// </summary>
public sealed record AutoModRulePlan(
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> RegexPatterns,
    IReadOnlyList<string> AllowList);

/// <summary>
/// Maps ScamGuard's lists onto an AutoMod keyword rule. Pure and testable. Phrases become "anywhere" keywords
/// (wrapped in wildcards, mirroring our detector's substring match); the lookalike fragments become one
/// case-insensitive Rust-flavored regex pattern. Respects Discord's size limits.
/// </summary>
public static class AutoModRuleBuilder
{
    private const int MaxKeywordLength = 60;   // Discord: each keyword <= 60 chars
    private const int MaxKeywords = 1000;       // Discord: <= 1000 keywords
    private const int MaxRegexLength = 260;     // Discord: each regex pattern <= 260 chars

    // Block tier: the lookalike-domain regex plus moderator-reported bad hosts. High precision and hard to
    // mutate without breaking the fake domain, so it is safe to block.
    public static AutoModRulePlan BuildBlockPlan(string lookalikePattern, IReadOnlyCollection<string> learnedHosts)
    {
        var regexPatterns = new List<string>();
        if (!string.IsNullOrWhiteSpace(lookalikePattern))
        {
            var pattern = "(?i)" + lookalikePattern; // case-insensitive for Discord's Rust regex engine
            if (pattern.Length <= MaxRegexLength)
            {
                regexPatterns.Add(pattern);
            }
        }

        return new AutoModRulePlan(WrapKeywords(learnedHosts), regexPatterns, Array.Empty<string>());
    }

    // Alert tier: scam phrases (built-in + learned). AutoMod cannot require a link and phrases are easily
    // mutated, so these alert rather than block.
    public static AutoModRulePlan BuildAlertPlan(
        IReadOnlyList<string> builtInPhrases, IReadOnlyCollection<string> learnedPhrases)
    {
        var all = new List<string>(builtInPhrases);
        all.AddRange(learnedPhrases);
        return new AutoModRulePlan(WrapKeywords(all), Array.Empty<string>(), Array.Empty<string>());
    }

    private static IReadOnlyList<string> WrapKeywords(IEnumerable<string> values)
    {
        var keywords = new List<string>();
        foreach (var value in values)
        {
            var trimmed = value?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(trimmed) || keywords.Count >= MaxKeywords)
            {
                continue;
            }

            // "*keyword*" matches anywhere, mirroring our detector's Contains() semantics.
            var wildcarded = "*" + trimmed + "*";
            if (wildcarded.Length <= MaxKeywordLength && !keywords.Contains(wildcarded))
            {
                keywords.Add(wildcarded);
            }
        }

        return keywords;
    }
}
