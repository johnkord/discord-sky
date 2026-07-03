namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Behavioral signals for the new-account watch. This is deliberately NOT a content/link detector: the real
/// threat on this server is a brand-new throwaway account posting a payload (a link, an invite, an attachment,
/// a mass mention), which the link-gated <see cref="ScamLinkDetector"/> misses whenever the payload is not a
/// parseable link in the text. Account age is the durable signal (recent research: behavior beats content once
/// LLMs write the content); everything else is corroboration.
/// </summary>
public readonly record struct NewAccountSignals(
    double AccountAgeDays,
    bool HasInvite,
    bool MentionsEveryone,
    bool HasShortener,
    bool HasLinkOrEmbed,
    bool HasAttachment,
    int MentionedCount);

/// <summary>Result of scoring a message: whether to alert the mods, the score, and a compact reason string.</summary>
public readonly record struct NewAccountVerdict(bool ShouldAlert, int Score, string Reason);

/// <summary>
/// Pure, multi-signal scorer for the new-account watch. Multi-signal on purpose: the incident (and the 2026
/// literature, e.g. PhishNChips) shows single-signal gates are brittle and invertible, so we sum several weak
/// signals rather than hard-gating on one. Alert-only by design: a false positive on a real newcomer is far
/// more costly here than a miss a present mod bans in minutes, so this never blocks or bans, it only flags.
/// </summary>
public static class NewAccountHeuristics
{
    public static NewAccountVerdict Evaluate(NewAccountSignals s, int newAccountDays, int threshold)
    {
        // Only genuinely new accounts are in scope. Every regular here is a years-old account, so an old account
        // sharing a link is almost certainly a friend, not a spammer. This age gate is the precision guard.
        if (s.AccountAgeDays >= Math.Max(1, newAccountDays))
        {
            return new NewAccountVerdict(false, 0, string.Empty);
        }

        // Being new is the base signal (worth 2), but on its own it never alerts: a newcomer just saying hi is
        // fine. A payload signal is required to clear the default threshold of 3.
        var score = 2;
        var reasons = new List<string> { $"new_account({s.AccountAgeDays:F0}d)" };

        // Strong signals (each +2): the unambiguous new-account spam shapes. At a stricter threshold (4+) any one
        // of these alerts on its own, while benign newcomer behavior does not.
        if (s.HasInvite) { score += 2; reasons.Add("invite"); }
        if (s.MentionsEveryone) { score += 2; reasons.Add("everyone"); }
        if (s.HasShortener) { score += 2; reasons.Add("shortener"); }

        // Weak signals (each +1): benign for a real newcomer alone, corroborating together. A link and the embed
        // Discord auto-generates for it are ONE signal, not two, so a single shared link is not double-counted.
        if (s.HasLinkOrEmbed) { score += 1; reasons.Add("link"); }
        if (s.HasAttachment) { score += 1; reasons.Add("attachment"); }
        if (s.MentionedCount >= 3) { score += 1; reasons.Add($"mentions({s.MentionedCount})"); }

        var alert = score >= Math.Max(1, threshold);
        return new NewAccountVerdict(alert, score, string.Join("+", reasons));
    }
}
