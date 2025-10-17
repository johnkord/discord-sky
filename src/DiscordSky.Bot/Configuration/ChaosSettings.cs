namespace DiscordSky.Bot.Configuration;

public sealed class ChaosSettings
{
    public double AnnoyanceLevel { get; init; } = 0.5;
    public int MaxScriptLines { get; init; } = 6;
    public int MaxPromptsPerHour { get; init; } = 20;
    public List<string> BanWords { get; init; } = new();
    /// <summary>
    /// Probability (0.0 - 1.0) that the bot will spontaneously reply to a non-command message
    /// in an allowed channel as though the command prefix was invoked. Defaults to 0.25.
    /// </summary>
    public double AmbientReplyChance { get; init; } = 0.25;

    public bool ContainsBanWord(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || BanWords is not { Count: > 0 })
        {
            return false;
        }

        return BanWords.Any(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
