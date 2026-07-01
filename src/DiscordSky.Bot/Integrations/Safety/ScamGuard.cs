using System.Text.RegularExpressions;
using DiscordSky.Bot.Bot;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Outcome of a scam check. <see cref="Reason"/> is a short tag for logs (e.g. "phishing-host",
/// "phrase:free nitro", "mass-mention"), never shown to users.
/// </summary>
public readonly record struct ScamDetection(bool IsScam, string? Reason)
{
    public static readonly ScamDetection None = new(false, null);
}

/// <summary>
/// High-precision detector for "obvious spam links" (phishing and crypto-scam bait). Tuned for precision over
/// recall: a false positive (warning on a legit link) is far more annoying in a friend server than a missed
/// scam, so every path requires a link plus a strong, unambiguous signal. Built after a server admin asked the
/// bot to call out spam links in character ("BAH THIS IS A THEFT ATTEMPT FROM THAT BLASTED HEDGEHOG").
/// </summary>
public static class ScamLinkDetector
{
    // Stronger tokens used to corroborate a URL shortener (which hides its destination). Tighter than the
    // mass-mention token list so "let's grab free pizza, rsvp bit.ly/party" does not trip it.
    private static readonly Regex StrongScamTokenRegex = new(
        @"nitro|crypto|airdrop|robux|v-?bucks|giveaway|usdt|casino|\bbtc\b|\beth\b|claim your|" +
        @"free (?:nitro|crypto|robux|money|bitcoin|gift)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Lookalike / fake hosts. Every fragment below is something a legitimate Discord/Steam/crypto domain never
    // contains, so matching one is essentially proof of phishing (near-zero false-positive rate). Kept as a
    // list so the AutoMod sync can reuse the exact same fragments as a native regex rule (one source of truth).
    private static readonly string[] LookalikeFragments =
    {
        "dlscord", "disc0rd", "discordgift", "discord-gift", "discord-nitro", "nitro-discord",
        "free-?nitro", @"discordapp\.(?:gift|click|info|ru)",
        "steamcommunlty", "steamcomunity", "steamnitro", "steam-gift", "steamgift",
    };

    private static readonly Regex PhishingHostRegex = new(
        string.Join("|", LookalikeFragments),
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Built-in scam phrases, exposed so the AutoMod rule can mirror them (one source of truth).</summary>
    public static IReadOnlyList<string> BuiltInScamPhrases => ScamPhrases;

    /// <summary>Lookalike-host fragments as a single alternation, for the AutoMod regex pattern.</summary>
    public static string BuiltInLookalikePattern => string.Join("|", LookalikeFragments);

    // Phrases that, paired with a link, are overwhelmingly scams in casual chat. Deliberately excludes terms
    // that ride along with legitimate shares (e.g. "mrbeast", "elon musk", "casino", "gift card") to avoid
    // flagging someone posting a YouTube video or a real giveaway.
    private static readonly string[] ScamPhrases =
    {
        "free nitro", "nitro free", "free discord nitro",
        "free robux", "free v-bucks", "free vbucks",
        "free bitcoin", "free crypto", "free money",
        "crypto casino", "withdrawal success", "withdraw success",
        "you have been selected", "you've been selected",
        "congratulations you won", "you have won", "you've won",
        "claim your reward", "claim your prize", "claim your free",
        "double your", "guaranteed profit", "click here to claim",
    };

    // Money/gift tokens used only to corroborate a mass-mention (@everyone/@here) raid, which is the classic
    // compromised-account pattern and catches novel wording the phrase list would miss.
    private static readonly Regex MoneyOrGiftRegex = new(
        @"\$\s?\d|\d+\s?(?:usd|usdt|btc|eth|dollars)|free|gift|nitro|claim|reward|prize|giveaway|airdrop|crypto|casino",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ScamDetection Detect(
        string? content,
        bool mentionsEveryone,
        IReadOnlyCollection<string>? extraPhrases = null,
        IReadOnlyCollection<string>? extraHosts = null,
        IPhishingDomainSource? phishingDomains = null,
        bool treatShortenersAsSignal = true,
        bool senderIsBot = false,
        bool senderIsNewAccount = false)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ScamDetection.None;
        }

        // Require a link: the request was about spam LINKS, and "has a host" is the cheapest precision gate.
        var hosts = DomainUtilities.ExtractHosts(content);
        if (hosts.Count == 0)
        {
            return ScamDetection.None;
        }

        var lower = content.ToLowerInvariant();

        // Layer 1 (highest confidence): a host confirmed bad by the live phishing-domain feed.
        if (phishingDomains is { Count: > 0 })
        {
            foreach (var host in hosts)
            {
                foreach (var candidate in DomainUtilities.SuffixCandidates(host))
                {
                    if (phishingDomains.Contains(candidate))
                    {
                        return new ScamDetection(true, "feed");
                    }
                }
            }
        }

        // Layer 2: a lookalike host, seen through homoglyph/punycode folding (so cyrillic and xn-- evasions fail).
        var foldedContent = DomainUtilities.FoldConfusables(lower);
        if (PhishingHostRegex.IsMatch(foldedContent)
            || hosts.Any(h => PhishingHostRegex.IsMatch(DomainUtilities.Skeleton(h)))
            || ContainsAny(lower, extraHosts))
        {
            return new ScamDetection(true, "lookalike");
        }

        // Layer 3: an unambiguous scam phrase.
        foreach (var phrase in ScamPhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
            {
                return new ScamDetection(true, $"phrase:{phrase}");
            }
        }

        if (ContainsAny(lower, extraPhrases))
        {
            return new ScamDetection(true, "phrase:custom");
        }

        // Layer: a Discord server invite. Benign on its own (friends share invites), so it only escalates when
        // corroborated by a mass-mention, a scam token, or a suspicious sender (untrusted bot / brand-new account).
        if (DomainUtilities.ContainsInvite(content)
            && (mentionsEveryone || senderIsBot || senderIsNewAccount || StrongScamTokenRegex.IsMatch(lower)))
        {
            return new ScamDetection(true, "invite");
        }

        // Layer: a destination-hiding shortener, corroborated by a strong token, a mass-mention, or a bot sender.
        if (treatShortenersAsSignal
            && hosts.Any(DomainUtilities.IsShortener)
            && (mentionsEveryone || senderIsBot || StrongScamTokenRegex.IsMatch(lower)))
        {
            return new ScamDetection(true, "shortener");
        }

        // Layer 5: the classic compromised-account raid (@everyone + a link + a money/gift token).
        if (mentionsEveryone && MoneyOrGiftRegex.IsMatch(lower))
        {
            return new ScamDetection(true, "mass-mention");
        }

        return ScamDetection.None;
    }

    private static bool ContainsAny(string haystackLower, IReadOnlyCollection<string>? needles)
    {
        if (needles is null || needles.Count == 0)
        {
            return false;
        }

        foreach (var n in needles)
        {
            if (!string.IsNullOrWhiteSpace(n)
                && haystackLower.Contains(n.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// In-character (Dr. Robotnik) anti-scam warnings. Canned on purpose: a safety call-out has to fire instantly
/// and reliably, with no dependence on the LLM being up or the circuit breaker being closed. Every line is
/// unambiguous that the link is a scam/theft and that nobody should click, wrapped in persona flavor (and the
/// obligatory hedgehog blame). Kept PG-13 to suit the servers the bot lives in.
/// </summary>
public static class ScamWarnings
{
    private static readonly string[] Lines =
    {
        "BAH! THIS is a THEFT ATTEMPT from that blasted hedgehog! Do NOT click it, you magnificent fools!",
        "HALT! That link reeks of Sonic's grubby paws. A scam, plain as my mustache. Touch it and you forfeit your wallet to the spin doctor.",
        "ALERT, minions! Obvious thievery detected. No true prize hides behind a shady link. Ignore it or face my disappointment.",
        "PAH. 'Free' anything from a random link is hedgehog-grade larceny. I forbid you from clicking, by royal decree.",
        "SWINDLE DETECTED in MY domain! That link is bait, not treasure. Delete it before I deploy the Egg-Pummeler.",
        "NICE TRY, scammer. I, Dr. Robotnik, declare that link a fraud. Anyone who clicks is hereby demoted to Wallet-Guarding Doormat.",
        "WARNING, peasants: that is a phishing trap, not a gift. Even my least competent badnik knows better. Be smarter than a badnik.",
        "THIEVES! This 'offer' is a con cooked up by that spiky menace. Keep your coins, ignore the link, and applaud my vigilance.",
        "BEHOLD a scam, exposed by my superior intellect! That link steals, it does not give. Do not click it, citizens.",
        "DENIED. No legitimate riches arrive via mysterious link. This is robbery in a clown costume. Stand down and trust the mustache.",
        "SECURITY BREACH! That is a malicious link. I would sooner share my doomsday plans than let one of you click it.",
        "ROBOTNIK'S DECREE: that link is a swindle, a hedgehog hustle, a wallet-snatching ruse. Do NOT click. This concludes today's public service of the Eggman Empire.",
    };

    public static string Random(IRandomProvider rng)
    {
        var i = (int)(Math.Clamp(rng.NextDouble(), 0.0, 0.999999) * Lines.Length);
        return Lines[i];
    }
}
