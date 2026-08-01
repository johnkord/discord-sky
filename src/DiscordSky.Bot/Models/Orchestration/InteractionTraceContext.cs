namespace DiscordSky.Bot.Models.Orchestration;

public sealed record InteractionTraceContext(
    string? EpisodeId = null,
    string? OperationId = null,
    int? EpisodeSchemaVersion = null,
    string? EvidenceDigest = null,
    string? ProjectionDigest = null);