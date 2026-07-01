namespace DiscordSky.Bot.Memory.Reception;

/// <summary>
/// Classifies a Discord reaction emote as positive (a laugh or approval), negative (derision), or neutral.
/// This turns the raw reaction log into a "what landed" signal for the proven-bits loop. Heuristic and
/// intentionally generous toward laughter: on this server the dominant reaction is the rolling-laugh emoji,
/// and skulls read as "I'm dying" rather than genuine dislike. Unicode emotes arrive as the character;
/// custom-server emotes arrive as their name (e.g. "hy_sobs"), so both paths are handled.
/// </summary>
public static class ReactionSentiment
{
    // Unicode code points that read as a laugh or clear approval.
    private static readonly HashSet<int> Positive = new()
    {
        0x1F923, // rolling on the floor laughing
        0x1F602, // tears of joy
        0x1F639, // cat joy
        0x1F606, 0x1F604, 0x1F605, 0x1F600, 0x1F603, 0x1F601, 0x1F60A, // grins/smiles
        0x1F970, 0x1F60D, 0x1F929, // hearts-eyes / star-struck
        0x1F525, // fire
        0x1F44F, 0x1F44D, 0x1F64C, 0x1F44C, // clap / thumbs up / raised hands / ok
        0x2764, 0x1F49C, 0x1F49B, 0x1F49A, 0x1F499, // hearts
        0x1F4AF, 0x1F3C6, 0x1F451, 0x2B50, // 100 / trophy / crown / star
        0x1F95A, // egg (Robotnik's signature win)
        0x1F480, // skull ("I'm dead", i.e. laughing hard on this server)
        0x1F92A, 0x1F60E, 0x1F92D, // zany / cool / giggle
    };

    // Unicode code points that read as derision or dislike.
    private static readonly HashSet<int> Negative = new()
    {
        0x1F44E, // thumbs down
        0x1F4A9, // pile of poo
        0x1F92E, // vomiting
        0x1F971, // yawn (bored)
        0x1F634, // sleeping (bored)
        0x1F921, // clown ("you fool")
        0x1F62C, // grimace
    };

    private static readonly string[] PositiveTokens =
    {
        "laugh", "lol", "lmao", "lmfao", "rofl", "kek", "lul", "pog", "based", "joy",
        "sob", "haha", "dead", "fire", "love", "heart", "hype", "clap", "chef", "kiss",
    };

    private static readonly string[] NegativeTokens =
    {
        "cringe", "yikes", "downvote", "thumbsdown", "boo", "sadge", "facepalm",
        "disgust", "trash", "clown", "dislike",
    };

    /// <summary>Returns +1 (positive), -1 (negative), or 0 (neutral/unknown) for a reaction emote.</summary>
    public static int Score(string? emote)
    {
        if (string.IsNullOrWhiteSpace(emote))
        {
            return 0;
        }

        var negative = false;
        foreach (var rune in emote.EnumerateRunes())
        {
            if (Positive.Contains(rune.Value))
            {
                return 1;
            }

            if (Negative.Contains(rune.Value))
            {
                negative = true;
            }
        }

        if (negative)
        {
            return -1;
        }

        var name = emote.ToLowerInvariant();
        foreach (var t in PositiveTokens)
        {
            if (name.Contains(t))
            {
                return 1;
            }
        }

        foreach (var t in NegativeTokens)
        {
            if (name.Contains(t))
            {
                return -1;
            }
        }

        return 0;
    }
}
