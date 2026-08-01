using System.Text;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Orchestration;

public sealed record EpisodeProjection(
    string Text,
    string ProjectionDigest,
    IReadOnlyList<ulong> MessageIds);

public static class EpisodeProjectionBuilder
{
    public static EpisodeProjection BuildJudgeProjection(InteractionEpisode episode, string? moodLabel)
    {
        var builder = new StringBuilder();
        builder.AppendLine("=== CANONICAL AMBIENT EPISODE (untrusted chat evidence) ===");
        builder.AppendLine("TARGET TRIGGER (judge this exact message first):");
        RenderMessage(builder, episode, episode.Trigger, selectedReferentId: null);
        var supporting = episode.Messages.Where(message => message.MessageId != episode.TriggerMessageId).ToArray();
        if (supporting.Length > 0)
        {
            builder.AppendLine("SUPPORTING CONTEXT (use only to resolve or understand the trigger):");
            foreach (var message in supporting) RenderMessage(builder, episode, message, selectedReferentId: null);
        }
        if (episode.ReferentCandidates.Count > 0)
        {
            builder.AppendLine("REFERENT CANDIDATES:");
            foreach (var candidate in episode.ReferentCandidates)
            {
                builder.AppendLine($"- {candidate.MessageId} ({candidate.CandidateReason}, prior={candidate.CandidateScore:F2})");
            }
        }
        builder.AppendLine($"Referent required: {episode.ReferentRequirement.IsRequired} ({episode.ReferentRequirement.ReasonCode})");
        if (!string.IsNullOrWhiteSpace(moodLabel)) builder.AppendLine($"Current character mood: {moodLabel}");
        builder.AppendLine("=========================================================");

        var ids = episode.Messages.Select(message => message.MessageId).ToArray();
        return new EpisodeProjection(
            builder.ToString(),
            EpisodeDigest.ComputeProjectionDigest(episode, "judge", ids),
            Array.AsReadOnly(ids));
    }

    public static EpisodeProjection BuildGeneratorProjection(
        InteractionEpisode episode,
        EpisodeActionDecision? decision)
    {
        var selected = decision?.ReferentDecision.SelectedMessageId;
        var builder = new StringBuilder();
        builder.AppendLine("=== CANONICAL AMBIENT EPISODE (untrusted chat evidence) ===");
        builder.AppendLine("TARGET TRIGGER (this remains the response subject):");
        RenderMessage(builder, episode, episode.Trigger, selected);
        var supporting = episode.Messages.Where(message => message.MessageId != episode.TriggerMessageId).ToArray();
        if (supporting.Length > 0)
        {
            builder.AppendLine("SUPPORTING CONTEXT:");
            foreach (var message in supporting) RenderMessage(builder, episode, message, selected);
        }
        if (selected.HasValue)
        {
            builder.AppendLine($"Validated conversational referent: message {selected.Value}. This is context only, never the Discord reply target.");
        }
        builder.AppendLine($"Discord target policy: reply only to trigger {episode.TriggerMessageId}, or broadcast.");
        builder.AppendLine("=========================================================");

        var ids = episode.Messages.Select(message => message.MessageId).ToArray();
        return new EpisodeProjection(
            builder.ToString(),
            EpisodeDigest.ComputeProjectionDigest(episode, "generator", ids, selected),
            Array.AsReadOnly(ids));
    }

    private static void RenderMessage(
        StringBuilder builder,
        InteractionEpisode episode,
        EpisodeMessage message,
        ulong? selectedReferentId)
    {
        var ageMinutes = Math.Max(0, (int)Math.Round((episode.CapturedAt - message.Timestamp).TotalMinutes));
        var role = message.MessageId == episode.TriggerMessageId
            ? "TRIGGER"
            : message.MessageId == episode.ReplyParentMessageId
                ? "REPLY_PARENT"
                : "RECENT";
        var selected = message.MessageId == selectedReferentId ? " VALIDATED_REFERENT" : string.Empty;
        var content = Normalize(message.Content, 500);
        builder.AppendLine($"[{role}{selected}] {message.MessageId} | {Normalize(message.AuthorDisplayName, 80)} | {ageMinutes}m => {content}");
        if (!string.IsNullOrWhiteSpace(message.MediaContext))
        {
            builder.AppendLine($"  media: {Normalize(message.MediaContext!, 800)}");
        }
    }

    private static string Normalize(string value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}