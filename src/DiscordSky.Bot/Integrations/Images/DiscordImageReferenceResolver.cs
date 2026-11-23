using System.Net;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace DiscordSky.Bot.Integrations.Images;

public sealed record ImageReferenceSet(IReadOnlyList<ImageReference> Images, ImageReference? Mask = null)
{
    public static readonly ImageReferenceSet Empty = new([]);
}

public interface IImageReferenceResolver
{
    Task<ImageReferenceSet> ResolveAsync(ImageGenerationContext context, CancellationToken cancellationToken);
}

public sealed class DiscordImageReferenceResolver(
    DiscordSocketClient client,
    IHttpClientFactory httpClients,
    IOptions<ImageOptions> imageOptions,
    IOptions<BotOptions> botOptions) : IImageReferenceResolver
{
    public const string HttpClientName = "image-references";

    public async Task<ImageReferenceSet> ResolveAsync(ImageGenerationContext context, CancellationToken cancellationToken)
    {
        if (!context.ChannelId.HasValue || !context.TriggerMessageId.HasValue) return ImageReferenceSet.Empty;
        if (client.GetChannel(context.ChannelId.Value) is not IMessageChannel channel)
            throw new ArgumentException("The source image channel is no longer available.");
        var guildChannel = channel as SocketGuildChannel;
        if (context.GuildId != guildChannel?.Guild.Id ||
            (guildChannel is not null && botOptions.Value.IsGuildDisabled(guildChannel.Guild.Id, guildChannel.Guild.Name)))
            throw new ArgumentException("This channel is not eligible for image editing.");
        var requestOptions = new RequestOptions { CancelToken = cancellationToken };
        var trigger = await channel.GetMessageAsync(context.TriggerMessageId.Value, options: requestOptions).ConfigureAwait(false);
        if (trigger is null) throw new ArgumentException("The source message is no longer available.");
        var messages = new List<IMessage>();
        var reference = trigger.Reference;
        if (reference is not null && reference.MessageId.IsSpecified && reference.ChannelId == channel.Id)
        {
            var replied = await channel.GetMessageAsync(reference.MessageId.Value, options: requestOptions).ConfigureAwait(false);
            if (replied is not null) messages.Add(replied);
        }
        messages.Add(trigger);
        var candidates = messages.SelectMany(message => message.Attachments
            .Where(attachment => IsImageAttachment(attachment.Filename, attachment.ContentType))
            .Select(attachment => (Attachment: attachment, MessageId: message.Id))).ToArray();
        if (candidates.Length == 0) return ImageReferenceSet.Empty;
        var options = imageOptions.Value;
        if (candidates.Count(candidate => !IsMask(candidate.Attachment.Filename)) > options.MaxReferenceImages ||
            candidates.Count(candidate => IsMask(candidate.Attachment.Filename)) > 1)
            throw new ArgumentException($"Use at most {options.MaxReferenceImages} reference images and one mask.png.");
        var images = new List<ImageReference>();
        ImageReference? mask = null;
        var totalBytes = 0;
        using var http = httpClients.CreateClient(HttpClientName);
        foreach (var candidate in candidates)
        {
            var attachment = candidate.Attachment;
            var image = await DownloadAsync(http, attachment.Url, attachment.Size, channel.Id, candidate.MessageId,
                Math.Min(options.MaxReferenceBytes, options.MaxTotalReferenceBytes - totalBytes),
                IsMask(attachment.Filename), cancellationToken).ConfigureAwait(false);
            totalBytes += image.Bytes.Length;
            if (IsMask(attachment.Filename)) mask = image;
            else images.Add(image);
        }
        var result = new ImageReferenceSet(images, mask);
        ImageReferenceValidation.ValidateMask(result);
        return result;
    }

    internal static async Task<ImageReference> DownloadAsync(HttpClient http, string url, long declaredSize,
        ulong channelId, ulong messageId, int byteLimit, bool mask, CancellationToken cancellationToken)
    {
        if (!IsSafeAttachmentUrl(url, channelId))
            throw new ArgumentException("Reference images must be Discord attachments in this channel.");
        if (byteLimit <= 0 || declaredSize > byteLimit)
            throw new ArgumentException("A reference image exceeds the configured upload limit.");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new ArgumentException("A source image has expired or was deleted. Please attach it again.");
        if (!response.IsSuccessStatusCode)
            throw new ArgumentException("The source image could not be downloaded. Please attach it again.");
        if (response.Content.Headers.ContentLength > byteLimit)
            throw new ArgumentException("A reference image exceeds the configured upload limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > byteLimit)
                throw new ArgumentException("The reference images exceed the configured upload limit.");
            buffer.Write(chunk, 0, read);
        }
        return ImageReferenceValidation.Create(buffer.ToArray(), messageId, mask);
    }

    internal static bool IsSafeAttachmentUrl(string url, ulong channelId) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase)) &&
        uri.AbsolutePath.StartsWith($"/attachments/{channelId}/", StringComparison.Ordinal);

    internal static bool IsImageAttachment(string fileName, string? contentType) =>
        contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
        Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";

    private static bool IsMask(string fileName) => string.Equals(fileName, "mask.png", StringComparison.OrdinalIgnoreCase);
}

internal static class ImageReferenceValidation
{
    internal static ImageReference Create(byte[] bytes, ulong messageId, bool mask)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream) ?? throw new ArgumentException("A reference image is not a valid PNG, JPEG, or WebP image.");
        var info = codec.Info;
        var format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "png",
            SKEncodedImageFormat.Jpeg => "jpeg",
            SKEncodedImageFormat.Webp => "webp",
            _ => null,
        };
        if (format is null || info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 16_777_216)
            throw new ArgumentException("Reference images must be PNG, JPEG, or WebP, at most 16 megapixels.");
        if (mask && (format != "png" || bytes.Length < 33 || bytes[25] is not (4 or 6)))
            throw new ArgumentException("mask.png must be a PNG with an alpha channel; transparent areas are edited.");
        return new ImageReference(bytes, mask ? "mask.png" : $"reference-{messageId}.{format}", $"image/{format}", messageId);
    }

    internal static void ValidateMask(ImageReferenceSet references)
    {
        if (references.Mask is null) return;
        if (references.Images.Count == 0) throw new ArgumentException("A mask requires a source image.");
        var source = references.Images[0];
        using var sourceCodec = SKCodec.Create(new MemoryStream(source.Bytes, writable: false));
        using var maskCodec = SKCodec.Create(new MemoryStream(references.Mask.Bytes, writable: false));
        if (source.MediaType != "image/png" || sourceCodec is null || maskCodec is null || sourceCodec.Info.Size != maskCodec.Info.Size)
            throw new ArgumentException("mask.png must have the same dimensions as the first source image, which must also be PNG.");
    }
}