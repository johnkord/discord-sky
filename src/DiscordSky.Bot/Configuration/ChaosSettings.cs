namespace DiscordSky.Bot.Configuration;

public sealed class ChaosSettings
{
    public int MaxPromptsPerHour { get; init; } = 20;
    /// <summary>
    /// Additional per-channel burst capacity reserved for explicit commands, mentions, and direct replies
    /// after the shared prompt budget is full. Ambient traffic can never consume this reserve.
    /// </summary>
    public int ExplicitReservePromptsPerHour { get; init; } = 4;
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

    /// <summary>
    /// Hard per-channel quiet period after a successful ambient reply. Unlike the probability dampener, this
    /// prevents another in-flight message handler from producing a second reply to the same burst.
    /// </summary>
    public int AmbientReplyQuietSeconds { get; init; } = 90;

    public bool ContainsBanWord(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || BanWords is not { Count: > 0 })
        {
            return false;
        }

        return BanWords.Any(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
