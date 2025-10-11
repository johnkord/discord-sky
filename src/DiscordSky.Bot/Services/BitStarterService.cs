using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models;

namespace DiscordSky.Bot.Services;

public sealed class BitStarterService
{
    private static readonly string[] Titles =
    [
        "Operation {0}",
        "The Ghost of {0}",
        "{0} But Make It Mythic",
        "Secret Lore Drop: {0}",
        "{0}: The Unauthorized Musical"
    ];

    private static readonly string[] ScriptFormats =
    [
        "{0}: Okay, but imagine if {1} was actually {2}.",
        "{0}: *dramatic gasp* {1} just unlocked a forbidden emote.",
        "{0}: Cut to the montage where {1} and {2} form a chaos duo.",
        "Narrator: Somewhere in the Discord strata, {1} whispers '{2}'.",
        "Stage Direction: A suspicious glitter bomb rolls toward {1}."
    ];

    private static readonly string[] Twists =
    [
        "Turns out the NPC was a mod all along.",
        "Someone accidentally summoned wholesome mode.",
        "The server pet starts narrating everything in rhymes.",
        "A rogue bot forks the timeline and demands snacks.",
        "Every emoji suddenly becomes sentient and unionizes."
    ];

    private readonly Random _random;

    public BitStarterService(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    public BitStarterResponse Generate(BitStarterRequest request, ChaosSettings settings)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
        {
            throw new ArgumentException("Topic must be provided", nameof(request));
        }

        var participantPool = request.Participants.Count > 0
            ? request.Participants
            : ["the void"];

        var maxLines = Math.Clamp((int)Math.Round(settings.MaxScriptLines * request.ChaosMultiplier), 3, 12);
        var scriptLines = new List<string>(capacity: maxLines);

        for (var i = 0; i < maxLines; i++)
        {
            var speaker = Pick(participantPool);
            var target = Pick(participantPool);
            var format = Pick(ScriptFormats);
            var twist = Pick(Twists);
            var line = string.Format(format, speaker, target, twist);
            scriptLines.Add(line);
        }

        var titleTemplate = Pick(Titles);
        var title = string.Format(titleTemplate, request.Topic);

        var mentionTags = participantPool
            .Select(name => $"@{NormalizeTag(name)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BitStarterResponse(title, scriptLines, mentionTags);
    }

    private string Pick(IReadOnlyList<string> source) => source[_random.Next(source.Count)];

    private static string NormalizeTag(string raw)
    {
        var trimmed = raw.Trim();
        return string.Join(string.Empty, trimmed.Where(char.IsLetterOrDigit));
    }
}
