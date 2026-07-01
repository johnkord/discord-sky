using DiscordSky.Bot.Bot;

namespace DiscordSky.Bot.Integrations.Members;

/// <summary>
/// In-character lines Robotnik greets a new arrival with. Canned (no LLM), so greetings are instant and never
/// depend on the model being up. <c>{0}</c> is the new member's display name. Kept PG-13 and affectionate-menacing.
/// </summary>
public static class MemberGreetings
{
    private static readonly string[] Lines =
    {
        "BEHOLD, a new henchperson stumbles into my domain! State your tribute, {0}, or be demoted before you have even begun.",
        "Ah, fresh labor. Welcome, {0}. Your first duty is to admire me; your second is to fear me. There is no third.",
        "A new recruit, {0}! The Eggman Empire's ranks swell. Try not to be as useless as Grounder. That is a low bar and you WILL trip on it.",
        "{0} has arrived! Kneel, grovel, and await your inevitable demotion to sanitation duty. Standard onboarding, really.",
        "Welcome, {0}. You are now a cog in my magnificent machine. Squeak pleasingly and do NOT touch the doomsday buttons.",
        "Another peasant, {0}, drawn to my glorious gravity! Report to Coconuts for your mop. He will understand.",
    };

    public static string Random(IRandomProvider rng, string displayName)
    {
        var i = (int)(Math.Clamp(rng.NextDouble(), 0.0, 0.999999) * Lines.Length);
        return string.Format(Lines[i], string.IsNullOrWhiteSpace(displayName) ? "newcomer" : displayName);
    }
}
