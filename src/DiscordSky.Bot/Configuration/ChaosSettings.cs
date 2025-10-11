namespace DiscordSky.Bot.Configuration;

public sealed class ChaosSettings
{
    public double AnnoyanceLevel { get; init; } = 0.5;
    public int MaxScriptLines { get; init; } = 6;
    public int MaxPromptsPerHour { get; init; } = 4;
    public TimeOnly QuietHoursStart { get; init; } = new(0, 0);
    public TimeOnly QuietHoursEnd { get; init; } = new(6, 0);
    public List<string> BanWords { get; init; } = new();

    public bool IsQuietHour(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime().TimeOfDay;
        var start = QuietHoursStart.ToTimeSpan();
        var end = QuietHoursEnd.ToTimeSpan();

        return start <= end
            ? local >= start && local < end
            : local >= start || local < end;
    }

    public bool ContainsBanWord(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || BanWords is not { Count: > 0 })
        {
            return false;
        }

        return BanWords.Any(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
