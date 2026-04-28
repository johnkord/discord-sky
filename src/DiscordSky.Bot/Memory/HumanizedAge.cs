namespace DiscordSky.Bot.Memory;

/// <summary>
/// Coarse humanised age strings for memory provenance bullets.
/// Intentionally lossy — the model doesn't need minute-precision recall.
/// </summary>
public static class HumanizedAge
{
    public static string Format(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(2)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} minutes ago";
        if (age < TimeSpan.FromHours(24)) return $"{(int)age.TotalHours} hours ago";
        if (age < TimeSpan.FromDays(14)) return $"{(int)age.TotalDays} days ago";
        if (age < TimeSpan.FromDays(60)) return $"{(int)(age.TotalDays / 7)} weeks ago";
        if (age < TimeSpan.FromDays(365)) return $"{(int)(age.TotalDays / 30)} months ago";
        return "over a year ago";
    }
}
