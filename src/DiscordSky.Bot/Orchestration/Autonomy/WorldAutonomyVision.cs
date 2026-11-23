using System.Text.RegularExpressions;
using Discord;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public enum AutonomyImageOrigin { Trigger, Reply, Recent }

public sealed record AutonomyImageAttachment(ulong MessageId, string Url, long Size, AutonomyImageOrigin Origin);

public sealed record AutonomyImageSelection(IReadOnlyList<AutonomyImageAttachment> Attachments, bool MissingReply = false);

public sealed class WorldAutonomyVision(
    IHttpClientFactory clients,
    IOptions<BotOptions> botOptions,
    IOptions<ImageOptions> imageOptions,
    IRecallTelemetrySink telemetry)
{
    private static readonly Regex ImageReference = new(
        @"\b(?:(?:the|this|that|these|those|both|previous|last|attached) (?:screenshots?|images?|pictures?|photos?)|(?:what (?:do you (?:make|think) of|does|is|are)|read|look at|explain|describe|translate|transcribe) (?:this|that|these|those))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool RefersToRecentImage(string text) => ImageReference.IsMatch(text);

    internal static AutonomyImageOrigin? SelectOrigin(bool hasCurrentImage, bool allowRecent, MessageType type,
        MessageReference? reference, ulong channelId, string text)
    {
        if (hasCurrentImage) return AutonomyImageOrigin.Trigger;
        if (reference is not null)
            return type == MessageType.Reply && (!reference.ReferenceType.IsSpecified || reference.ReferenceType.Value == MessageReferenceType.Default) &&
                reference.MessageId.IsSpecified && reference.ChannelId == channelId ? AutonomyImageOrigin.Reply : null;
        return allowRecent && RefersToRecentImage(text) ? AutonomyImageOrigin.Recent : null;
    }

    internal static IEnumerable<AutonomyImageAttachment> RecentImages(
        IEnumerable<(ulong AuthorId, DateTimeOffset Timestamp, IReadOnlyList<AutonomyImageAttachment> ImageOnly)> newestFirst,
        DateTimeOffset triggerTime)
    {
        ulong? albumAuthor = null;
        foreach (var post in newestFirst.Take(2))
        {
            if (post.Timestamp > triggerTime || triggerTime - post.Timestamp > TimeSpan.FromMinutes(2) ||
                post.ImageOnly.Count == 0 || (albumAuthor.HasValue && post.AuthorId != albumAuthor)) break;
            albumAuthor = post.AuthorId;
            foreach (var image in post.ImageOnly) yield return image;
        }
    }

    public async Task<ChatMessage> BuildUserMessageAsync(string prompt, AutonomyImageSelection? selection,
        ulong channelId, ulong? triggerId, string operationId, string route, CancellationToken cancellationToken)
    {
        var message = new ChatMessage(ChatRole.User, prompt);
        var bot = botOptions.Value;
        if (bot.ImageInputLimit == 0 || selection is null ||
            (selection.Attachments.Count == 0 && !selection.MissingReply)) return message;

        var limits = imageOptions.Value;
        var limit = Math.Clamp(limits.MaxReferenceImages, 0, bot.ImageInputLimit);
        var candidates = selection.Attachments.DistinctBy(item => item.Url, StringComparer.Ordinal).ToArray();
        var skipped = Math.Max(0, candidates.Length - limit);
        var failed = 0;
        var sent = 0;
        var bytes = 0;
        using var http = clients.CreateClient(DiscordImageReferenceResolver.HttpClientName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        foreach (var attachment in candidates.Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri) ||
                    (bot.ImageHostAllowList is { Count: > 0 } hosts &&
                    !hosts.Any(host => string.Equals(host?.Trim(), uri.Host, StringComparison.OrdinalIgnoreCase))))
                    throw new ArgumentException("Image host not allowed.");
                var image = await DiscordImageReferenceResolver.DownloadAsync(http, attachment.Url, attachment.Size,
                    channelId, attachment.MessageId, Math.Min(limits.MaxReferenceBytes, limits.MaxTotalReferenceBytes - bytes),
                    mask: false, deadline.Token).ConfigureAwait(false);
                var source = attachment.Origin switch
                {
                    AutonomyImageOrigin.Trigger => "current message",
                    AutonomyImageOrigin.Reply => "replied-to message",
                    _ => "immediately preceding image post",
                };
                message.Contents.Add(new TextContent($"Attachment from {source} {attachment.MessageId}:"));
                message.Contents.Add(new DataContent(image.Bytes, image.MediaType));
                bytes += image.Bytes.Length;
                sent++;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failed++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failed + skipped > 0)
            message.Contents.Add(new TextContent($"{failed + skipped} image attachment(s) could not be included. Do not guess their contents."));
        if (selection.MissingReply)
            message.Contents.Add(new TextContent("The replied-to message could not be retrieved; its attachments are unknown."));

        telemetry.Emit(new TelemetryEvent(DateTimeOffset.UtcNow, TelemetryEventTypes.WorldAutonomyImageInput,
            OperationId: operationId, MessageId: triggerId, Kind: route, Count: sent, Total: candidates.Length,
            ReasonCode: selection.MissingReply ? "missing_reply" : candidates.FirstOrDefault()?.Origin.ToString().ToLowerInvariant(),
            ReferentMessageId: candidates.FirstOrDefault()?.MessageId,
            SourceMessageCount: candidates.Select(item => item.MessageId).Distinct().Count(),
            Truncated: skipped > 0, Outcome: sent == 0 ? "unavailable" :
                failed + skipped > 0 || selection.MissingReply ? "partial" : "attached"));
        return message;
    }
}