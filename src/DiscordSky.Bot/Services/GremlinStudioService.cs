using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models;

namespace DiscordSky.Bot.Services;

public sealed class GremlinStudioService
{
    private static readonly IReadOnlyDictionary<GremlinArtifactKind, string[]> Palette = new Dictionary<GremlinArtifactKind, string[]>
    {
        [GremlinArtifactKind.ImageMashup] =
        [
            "Collage of {0} riding a neon {1}",
            "Blueprint for a {0}-powered {1}",
            "Magazine cover: '{0} vs {1} – Battle of the century'",
            "Pixel art shrine dedicated to {0}'s {1}"
        ],
        [GremlinArtifactKind.MemeCaptionBatch] =
        [
            "When you promise {0} but deliver {1} instead",
            "POV: {0} hears about {1}",
            "{0} explaining {1} with interpretive dance",
            "How it started ({0}) vs how it's going ({1})"
        ],
        [GremlinArtifactKind.AudioHook] =
        [
            "8-bit jingle chanting '{0}' over {1} bass",
            "Lo-fi loop that samples {0} saying '{1}'",
            "Hyperpop intro screaming '{0}' before dropping into {1}",
            "Spooky whisper track layering '{0}' and '{1}'"
        ]
    };

    private readonly Random _random;

    public GremlinStudioService(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    public GremlinArtifact Remix(GremlinPrompt prompt, ChaosSettings settings)
    {
        if (prompt.Attachments.Count == 0)
        {
            return new GremlinArtifact(
                prompt.PreferredKind,
                $"{prompt.Seed} Remix",
                ["Need at least one attachment – toss the Gremlin a bone!"]
            );
        }

        var palette = Palette[prompt.PreferredKind];
        var outputs = new List<string>();
        var attachmentsSample = prompt.Attachments.Take(3).ToArray();

        foreach (var attachment in attachmentsSample)
        {
            var template = Pick(palette);
            outputs.Add(string.Format(template, prompt.Seed, attachment));
        }

        if (settings.AnnoyanceLevel > 0.75)
        {
            outputs.Add($"Bonus chaos loop: splice {string.Join(" + ", attachmentsSample)} into a glitch reel.");
        }

        var title = $"Gremlin Remix: {prompt.Seed}";
        return new GremlinArtifact(prompt.PreferredKind, title, outputs);
    }

    private string Pick(IReadOnlyList<string> source) => source[_random.Next(source.Count)];
}
