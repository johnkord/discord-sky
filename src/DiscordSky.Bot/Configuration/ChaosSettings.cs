namespace DiscordSky.Bot.Configuration;

public sealed class ChaosSettings
{
    public int MaxPromptsPerHour { get; init; } = 20;
    public List<string> BanWords { get; init; } = new();
    /// <summary>
    /// Probability (0.0 - 1.0) that the bot will spontaneously reply to a non-command message
    /// in an allowed channel as though the command prefix was invoked. Defaults to 0.25.
    /// </summary>
    public double AmbientReplyChance { get; init; } = 0.25;

    /// <summary>
    /// When true, an ambient candidate that passes the <see cref="AmbientReplyChance"/> cost roll is then scored
    /// by the inner-thought worth judge and only becomes a reply if the score clears
    /// <see cref="AmbientWorthThreshold"/>. The roll is the cost bound; the judge is the quality bound. When
    /// false, a passing roll replies directly (the original behavior). Off by default; dormant until flipped.
    /// </summary>
    public bool UseWorthGate { get; init; } = false;

    /// <summary>
    /// Minimum worth score (0..1) for an ambient interjection to fire when <see cref="UseWorthGate"/> is on.
    /// Higher is quieter and pickier. Set to 0.0 to collect worth telemetry without actually gating (every
    /// rolled candidate still replies).
    /// </summary>
    public double AmbientWorthThreshold { get; init; } = 0.5;

    public bool ContainsBanWord(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || BanWords is not { Count: > 0 })
        {
            return false;
        }

        return BanWords.Any(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
