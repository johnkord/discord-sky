namespace DiscordSky.Bot.Orchestration;

/// <summary>
/// The base unicode palette Robotnik may slap on someone's message when he editorializes WITHOUT speaking.
/// Each entry pairs a stable <see cref="ReactionEmote.Token"/> (the name the reaction-judge LLM picks by) with
/// the emoji Discord posts and the villainous <see cref="ReactionEmote.Meaning"/> handed to the model so it
/// chooses in character. The guild's custom emotes are layered on top of this at judge time. Every emoji is a
/// single fully-qualified code point (no variation selector), which Discord accepts as a reaction.
/// </summary>
public static class RobotnikReactions
{
    /// <summary>One offerable reaction: a stable name token, the emoji to post, and its in-character meaning.</summary>
    public readonly record struct ReactionEmote(string Token, string Emoji, string Meaning);

    /// <summary>The base unicode palette, described in Robotnik's voice for the judge.</summary>
    public static IReadOnlyList<ReactionEmote> Unicode { get; } = new[]
    {
        new ReactionEmote("egg", "\U0001F95A", "his signature egg; grudging approval, or \"this is mine now\""),
        new ReactionEmote("clown", "\U0001F921", "the sender is a clown/fool who just embarrassed themselves"),
        new ReactionEmote("eyeroll", "\U0001F644", "contempt and boredom; \"how tiresome\""),
        new ReactionEmote("anger", "\U0001F4A2", "villainous rage or indignation"),
        new ReactionEmote("robot", "\U0001F916", "approval of machinery, menace, or a good scheme"),
        new ReactionEmote("eyes", "\U0001F440", "intrigued and scheming; \"go on...\""),
        new ReactionEmote("laughing", "\U0001F602", "gloating laughter at misfortune or a genuinely good joke"),
        new ReactionEmote("thumbsdown", "\U0001F44E", "flat dismissal; \"pathetic\""),
        new ReactionEmote("lying", "\U0001F925", "he thinks it is a lie, a cope, or a scam"),
        new ReactionEmote("chartdown", "\U0001F4C9", "mocking someone's failure or decline"),
        new ReactionEmote("skull", "\U0001F480", "brutal; that killed him, dead, dark delight at something savage"),
        new ReactionEmote("deadpan", "\U0001F5FF", "a stone-faced, unimpressed, flatly judgmental stare"),
    };
}
