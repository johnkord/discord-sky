using DiscordSky.Bot.Bot;

namespace DiscordSky.Bot.Orchestration;

/// <summary>
/// Unicode emoji Robotnik slaps on other people's messages when he deigns to editorialize WITHOUT speaking.
/// Derisive, villainous, or grudgingly amused. Canned (no LLM call), so reacting is instant and free. Every
/// entry is a single fully-qualified code point (no variation selector), which Discord accepts as a reaction.
/// </summary>
public static class RobotnikReactions
{
    private static readonly string[] Palette =
    {
        "\U0001F95A", // egg (his signature)
        "\U0001F921", // clown (you fool)
        "\U0001F644", // eye roll
        "\U0001F4A2", // anger symbol
        "\U0001F916", // robot
        "\U0001F440", // eyes (watching you)
        "\U0001F602", // laughing at you
        "\U0001F44E", // thumbs down
        "\U0001F925", // lying face
        "\U0001F4C9", // chart decreasing (your stock is falling)
    };

    public static string Pick(IRandomProvider rng)
    {
        var i = (int)(Math.Clamp(rng.NextDouble(), 0.0, 0.999999) * Palette.Length);
        return Palette[i];
    }
}
