using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models;

namespace DiscordSky.Bot.Services;

public sealed class HeckleCycleService
{
    private static readonly string[] ReminderFormats =
    [
        "👀 {0}, future-you just called and asked where that '{1}' update is.",
        "{0}, the procrastination goblins are circling '{1}' again…",
        "Friendly chaos ping! {0}, remember when '{1}' was a thing? It's still a thing.",
        "Countdown initiated: {1} detonates in 42 faux minutes unless {0} reports back.",
        "{0}, we put {1} on the leaderboard under 'pending shenanigans'."
    ];

    private static readonly string[] CelebrationFormats =
    [
        "🎉 {0} actually finished '{1}'! Confetti cannons armed.",
        "Server lore updated: {0} conquered '{1}' like a legend.",
        "{0}'s '{1}' completion triggered a custom emote unlock!",
        "Wholesome mode unlocked for 5 minutes in honor of {0} finishing '{1}'."
    ];

    private readonly Random _random;

    public HeckleCycleService(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    public HeckleResponse BuildResponse(HeckleTrigger trigger, ChaosSettings settings)
    {
        if (settings.IsQuietHour(trigger.Timestamp))
        {
            return new HeckleResponse(
                Reminder: $"Shh… quiet hours. Logging '{trigger.Declaration}' for later mischief.",
                FollowUpCelebration: "",
                NextNudgeAt: trigger.Timestamp.AddHours(1)
            );
        }

        var reminder = string.Format(Pick(ReminderFormats), trigger.Username, trigger.Declaration);
        var celebration = string.Format(Pick(CelebrationFormats), trigger.Username, trigger.Declaration);

        var delayMinutes = settings.AnnoyanceLevel switch
        {
            >= 0.8 => 15,
            >= 0.5 => 30,
            _ => 60
        };

        var nextNudge = trigger.Timestamp.AddMinutes(delayMinutes);
        return new HeckleResponse(reminder, celebration, nextNudge);
    }

    private string Pick(IReadOnlyList<string> options) => options[_random.Next(options.Count)];
}
