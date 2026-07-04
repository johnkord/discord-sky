using DiscordSky.Bot.Configuration;

namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>The starting world, used when there is no state file yet (fresh deploy or a reset).</summary>
public static class EmpireSeed
{
    public const string Body =
        "## The situation now\n" +
        "Operation Eggshell Dawn proceeds magnificently, which is to say it has not yet exploded. " +
        "The plan: reroute every conveyor on Mobius through my lair so all roads, and all eggs, lead to me. " +
        "Coconuts is supposed to be guarding the blueprints. I have my doubts.\n\n" +
        "## Lately\n" +
        "- Reminded the henchbots, at length, who is the genius here.\n" +
        "- That hedgehog was spotted near the perimeter. Probably nothing. Probably.";

    public static EmpireState Initial(EmpireStateOptions options, DateTimeOffset now)
        => new(
            Version: 1,
            UpdatedAt: now,
            LastTickAt: now,
            Mood: EmpireMood.Make(options.BaselineValence, options.BaselineArousal),
            Ranks: Array.Empty<Rank>(),
            Body: Body);
}
