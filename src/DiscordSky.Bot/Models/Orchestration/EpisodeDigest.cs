using System.Security.Cryptography;
using System.Text.Json;

namespace DiscordSky.Bot.Models.Orchestration;

public static class EpisodeDigest
{
    public static string ComputeEvidenceDigest(
        DateTimeOffset capturedAt,
        ulong channelId,
        ulong triggerMessageId,
        IReadOnlyList<EpisodeMessage> messages,
        ulong? replyParentMessageId,
        ReferentRequirement requirement,
        IReadOnlyList<ReferentCandidate> candidates,
        EpisodeEvidenceMask mask) => ComputeHash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("schema", InteractionEpisode.CurrentSchemaVersion);
        writer.WriteNumber("captured_at_ms", capturedAt.ToUnixTimeMilliseconds());
        writer.WriteNumber("channel_id", channelId);
        writer.WriteNumber("trigger_message_id", triggerMessageId);
        if (replyParentMessageId.HasValue) writer.WriteNumber("reply_parent_message_id", replyParentMessageId.Value);
        writer.WriteNumber("evidence_mask", (int)mask);
        writer.WriteBoolean("referent_required", requirement.IsRequired);
        writer.WriteString("referent_reason", requirement.ReasonCode);
        writer.WriteStartArray("messages");
        foreach (var message in messages.OrderBy(item => item.Timestamp).ThenBy(item => item.MessageId))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", message.MessageId);
            writer.WriteNumber("author_id", message.AuthorId);
            writer.WriteNumber("timestamp_ms", message.Timestamp.ToUnixTimeMilliseconds());
            if (message.ReferencedMessageId.HasValue) writer.WriteNumber("referenced_id", message.ReferencedMessageId.Value);
            writer.WriteBoolean("bot", message.IsBot);
            writer.WriteBoolean("media", message.HasMedia);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("candidates");
        foreach (var candidate in candidates.OrderByDescending(item => item.CandidateScore).ThenBy(item => item.MessageId))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", candidate.MessageId);
            writer.WriteNumber("score", candidate.CandidateScore);
            writer.WriteString("reason", candidate.CandidateReason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string ComputeProjectionDigest(
        InteractionEpisode episode,
        string projectionType,
        IEnumerable<ulong> includedMessageIds,
        ulong? validatedReferentMessageId = null) => ComputeHash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("evidence_digest", episode.Fingerprint.EvidenceDigest);
        writer.WriteString("projection_type", projectionType);
        writer.WriteStartArray("message_ids");
        foreach (var id in includedMessageIds.Distinct().OrderBy(id => id)) writer.WriteNumberValue(id);
        writer.WriteEndArray();
        if (validatedReferentMessageId.HasValue) writer.WriteNumber("referent_message_id", validatedReferentMessageId.Value);
        writer.WriteEndObject();
    });

    private static string ComputeHash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}