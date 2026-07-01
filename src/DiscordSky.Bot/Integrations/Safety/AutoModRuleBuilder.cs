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

    public static AutoModRulePlan Build(
        IReadOnlyList<string> builtInPhrases,
        string lookalikePattern,
        IReadOnlyCollection<string> learnedPhrases,
        IReadOnlyCollection<string> learnedHosts,
        bool includePhrases,
        bool includeLookalikeRegex)
    {
        var keywords = new List<string>();

        void AddKeyword(string? value)
        {
            var trimmed = value?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(trimmed) || keywords.Count >= MaxKeywords)
            {
                return;
            }

            // "*keyword*" matches anywhere, mirroring our detector's Contains() semantics.
            var wildcarded = "*" + trimmed + "*";
            if (wildcarded.Length <= MaxKeywordLength && !keywords.Contains(wildcarded))
            {
                keywords.Add(wildcarded);
            }
        }

        if (includePhrases)
        {
            foreach (var phrase in builtInPhrases)
            {
                AddKeyword(phrase);
            }
            foreach (var phrase in learnedPhrases)
            {
                AddKeyword(phrase);
            }
        }

        // Learned hosts are always useful as keywords: a known-bad domain substring matches inside a URL.
        foreach (var host in learnedHosts)
        {
            AddKeyword(host);
        }

        var regexPatterns = new List<string>();
        if (includeLookalikeRegex && !string.IsNullOrWhiteSpace(lookalikePattern))
        {
            var pattern = "(?i)" + lookalikePattern; // case-insensitive for Discord's Rust regex engine
            if (pattern.Length <= MaxRegexLength)
            {
                regexPatterns.Add(pattern);
            }
        }

        return new AutoModRulePlan(keywords, regexPatterns, Array.Empty<string>());
    }
}
