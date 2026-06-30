using System.Text.RegularExpressions;
using DiscordSky.Bot.Bot;

namespace DiscordSky.Bot.Integrations.Images;

/// <summary>
/// Detects a natural-language image request ("draw me as a knight", "make a picture of my cat") so the bot
/// can route to the image pipeline without the <c>!sky(image)</c> command, which the ops analysis
/// (docs/ops_analysis_2026-06-29.md) showed nobody uses. Applied only when the bot is addressed (a direct
/// reply or a mention), so a stray "that game was a draw" in open chat does not trip it.
/// </summary>
public static class ImageIntentDetector
{
    // Each branch requires the verb to be followed by a plausible target, which keeps idioms like
    // "the match was a draw" or "draw money from the bank" from matching.
    private static readonly Regex Pattern = new(
        @"\b(?:draw|sketch|paint|render|illustrate)\s+(?:me|us|him|her|it|them|this|that|the|a|an|my|your|our|their|some)\b" +
        @"|\bmake (?:me |us )?(?:a |an )?(?:picture|image|drawing|portrait|poster|painting)\b" +
        @"|\b(?:picture|portrait|poster|painting|drawing|image)\s+of\b" +
        @"|\bshow (?:me|us)\s+(?:a|an|your|the)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeImageRequest(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Pattern.IsMatch(text);
}

/// <summary>
/// In-character "the Foundry is firing up" placeholders, posted the instant a slow (commissioned) image
/// starts, so a ~70s wait reads as a deliberate bit instead of a hang. The picture follows when it is ready.
/// </summary>
public static class ImagePlaceholders
{
    private static readonly string[] Lines =
    {
        "Stand back, peasant. The Royal Egg Art Foundry is stoking its furnaces...",
        "Powering up the Egg-O-Matic Easel. Genius cannot be rushed, only admired in advance...",
        "Summoning my finest robo-artisans to the canvas. Try not to swoon at the masterpiece to come...",
        "Behold, the kiln of my brilliance is warming. Hold your applause, but only for a moment...",
        "My mechanical Michelangelos are sharpening their styluses. Prepare to be artistically conquered...",
    };

    public static string Random(IRandomProvider rng)
    {
        var i = (int)(Math.Clamp(rng.NextDouble(), 0.0, 0.999999) * Lines.Length);
        return Lines[i];
    }
}
