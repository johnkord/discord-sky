namespace DiscordSky.Bot.Models;

public enum GremlinArtifactKind
{
    ImageMashup,
    MemeCaptionBatch,
    AudioHook
}

public sealed record GremlinPrompt(string Seed, GremlinArtifactKind PreferredKind, IReadOnlyList<string> Attachments);

public sealed record GremlinArtifact(GremlinArtifactKind Kind, string Title, IReadOnlyList<string> Payloads);
