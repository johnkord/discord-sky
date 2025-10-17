using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Discord;
using Discord.Commands;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class ContextAggregator
{
    private readonly ChaosSettings _chaosSettings;
    private readonly ILogger<ContextAggregator> _logger;
    private readonly string _commandPrefix;
    private readonly int _historyLimit;
    private readonly bool _allowImageContext;
    private readonly int _historyImageLimit;
    private readonly HashSet<string> _imageHostAllowList;

    private static readonly string[] ImageExtensions =
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".bmp"
    };

    private static readonly Regex InlineUrlRegex = new(
        "https?://[^\\s<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ContextAggregator(
        ChaosSettings chaosSettings,
        IOptions<BotOptions> botOptions,
        ILogger<ContextAggregator> logger)
    {
        _chaosSettings = chaosSettings;
        _logger = logger;

        var bot = botOptions.Value;
        _commandPrefix = bot.CommandPrefix ?? string.Empty;
        _historyLimit = bot.HistoryMessageLimit > 0 ? Math.Clamp(bot.HistoryMessageLimit, 1, 100) : 20;
        _allowImageContext = bot.AllowImageContext;
        _historyImageLimit = !_allowImageContext
            ? 0
            : Math.Clamp(bot.HistoryImageLimit >= 0 ? bot.HistoryImageLimit : 3, 0, 12);

        _imageHostAllowList = new HashSet<string>(
            (bot.ImageHostAllowList ?? new List<string>()).Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CreativeContext> BuildContextAsync(CreativeRequest request, SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var history = await GatherHistoryAsync(commandContext, cancellationToken);
        return new CreativeContext(request, _chaosSettings, history);
    }

    private async Task<IReadOnlyList<ChannelMessage>> GatherHistoryAsync(SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var messages = new List<ChannelMessage>();
        var fetchLimit = Math.Max(_historyLimit * 2, _historyLimit);

        try
        {
            var history = commandContext.Channel.GetMessagesAsync(limit: fetchLimit);
            await foreach (var batch in history.WithCancellation(cancellationToken))
            {
                foreach (var message in batch)
                {
                    if (message.Id == commandContext.Message.Id)
                    {
                        continue;
                    }

                    var content = message.Content ?? string.Empty;
                    var trimmed = content.Trim();
                    IReadOnlyList<ChannelImage> images = Array.Empty<ChannelImage>();

                    if (_allowImageContext)
                    {
                        images = CollectImages(message);
                    }

                    if (string.IsNullOrWhiteSpace(trimmed) && (images.Count == 0))
                    {
                        continue;
                    }

                    if (message.Author.IsBot)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(_commandPrefix) && trimmed.StartsWith(_commandPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    messages.Add(new ChannelMessage
                    {
                        MessageId = message.Id,
                        Author = message.Author.Username,
                        Content = message.Content ?? string.Empty,
                        Timestamp = message.Timestamp,
                        IsBot = message.Author.IsBot,
                        Images = images
                    });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to gather channel history for {ChannelId}", commandContext.Channel.Id);
        }

        var ordered = messages
            .OrderByDescending(m => m.Timestamp)
            .Take(_historyLimit)
            .OrderBy(m => m.Timestamp)
            .ToList();

        if (!_allowImageContext || _historyImageLimit == 0)
        {
            return ordered;
        }

        return TrimImageOverflow(ordered, _historyImageLimit);
    }

    private IReadOnlyList<ChannelImage> CollectImages(IMessage message)
    {
        var results = new List<ChannelImage>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var attachment in message.Attachments)
            {
                if (!IsAttachmentImage(attachment))
                {
                    continue;
                }

                if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (!IsAllowedHost(uri))
                {
                    continue;
                }

                var filename = string.IsNullOrWhiteSpace(attachment.Filename)
                    ? Path.GetFileName(uri.LocalPath)
                    : attachment.Filename;

                var image = new ChannelImage
                {
                    Url = uri,
                    Filename = filename,
                    Source = "attachment",
                    Timestamp = message.Timestamp
                };

                if (dedupe.Add(uri.ToString()))
                {
                    results.Add(image);
                }
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                foreach (Match match in InlineUrlRegex.Matches(message.Content))
                {
                    var cleaned = TrimTrailingPunctuation(match.Value);
                    if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
                    {
                        continue;
                    }

                    if (!IsAllowedHost(uri))
                    {
                        continue;
                    }

                    if (!HasImageExtension(uri))
                    {
                        continue;
                    }

                    var filename = Path.GetFileName(uri.LocalPath);
                    var image = new ChannelImage
                    {
                        Url = uri,
                        Filename = string.IsNullOrWhiteSpace(filename) ? uri.Host : filename,
                        Source = "inline",
                        Timestamp = message.Timestamp
                    };

                    if (dedupe.Add(uri.ToString()))
                    {
                        results.Add(image);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse images from message {MessageId}", message.Id);
        }

        return results.Count == 0 ? Array.Empty<ChannelImage>() : results;
    }

    private static bool IsAttachmentImage(IAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.ContentType)
            && attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(attachment.Filename) && HasImageExtension(attachment.Filename))
        {
            return true;
        }

        return false;
    }

    private bool IsAllowedHost(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_imageHostAllowList.Count == 0)
        {
            return true;
        }

        return _imageHostAllowList.Contains(uri.Host);
    }

    private static bool HasImageExtension(string candidate)
    {
        var extension = Path.GetExtension(candidate);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasImageExtension(Uri uri) => HasImageExtension(uri.AbsolutePath);

    private static string TrimTrailingPunctuation(string value)
    {
        var trimmed = value.TrimEnd('.', ',', ';', '!', '?', ')', ']');
        return trimmed;
    }

    internal static IReadOnlyList<ChannelMessage> TrimImageOverflow(IReadOnlyList<ChannelMessage> messages, int limit)
    {
        if (limit <= 0)
        {
            return messages.Select(m => m.Images.Count == 0 ? m : m with { Images = Array.Empty<ChannelImage>() }).ToArray();
        }

        var orderedImages = messages
            .SelectMany(message => message.Images.Select(image => (message, image)))
            .OrderBy(entry => entry.image.Timestamp)
            .ToList();

        if (orderedImages.Count <= limit)
        {
            return messages;
        }

        var retained = new HashSet<ChannelImage>(orderedImages
            .Skip(Math.Max(0, orderedImages.Count - limit))
            .Select(entry => entry.image));

        var result = new List<ChannelMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Images.Count == 0)
            {
                result.Add(message);
                continue;
            }

            var filtered = message.Images.Where(retained.Contains).ToArray();
            result.Add(filtered.Length == message.Images.Count
                ? message
                : message with { Images = filtered });
        }

        return result;
    }
}
