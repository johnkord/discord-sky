using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.Commands;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration;

public sealed class ContextAggregator
{
    private readonly ILogger<ContextAggregator> _logger;
    private readonly ILinkUnfurler _linkUnfurler;
    private readonly string _commandPrefix;
    private readonly int _historyLimit;
    private readonly bool _allowImageContext;
    private readonly int _historyImageLimit;
    private readonly HashSet<string> _imageHostAllowList;
    private readonly int _replyChainDepth;
    private readonly bool _includeOwnMessagesInHistory;
    private readonly bool _enableLinkUnfurling;
    private ulong? _botUserId;

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
        IOptions<BotOptions> botOptions,
        ILinkUnfurler linkUnfurler,
        ILogger<ContextAggregator> logger)
    {
        _logger = logger;
        _linkUnfurler = linkUnfurler;

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

        _replyChainDepth = Math.Clamp(bot.ReplyChainDepth, 1, 100);
        _includeOwnMessagesInHistory = bot.IncludeOwnMessagesInHistory;
        _enableLinkUnfurling = bot.EnableLinkUnfurling;
    }

    /// <summary>
    /// Sets the bot's user ID for filtering purposes. Must be called after the Discord client is ready.
    /// </summary>
    public void SetBotUserId(ulong botUserId)
    {
        _botUserId = botUserId;
    }

    public async Task<CreativeContext> BuildContextAsync(CreativeRequest request, SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var history = await GatherHistoryAsync(commandContext, cancellationToken);
        return new CreativeContext(history);
    }

    private async Task<IReadOnlyList<ChannelMessage>> GatherHistoryAsync(SocketCommandContext commandContext, CancellationToken cancellationToken)
    {
        var messages = new List<ChannelMessage>();
        var fetchLimit = _historyLimit * 2;

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

                    // Include this bot's messages if configured, skip other bots
                    if (message.Author.IsBot)
                    {
                        var isThisBot = _botUserId.HasValue && message.Author.Id == _botUserId.Value;
                        if (!isThisBot || !_includeOwnMessagesInHistory)
                        {
                            continue;
                        }
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
                        Images = images,
                        UnfurledLinks = ExtractEmbedsAsUnfurledLinks(message.Embeds, message.Timestamp),
                        ReferencedMessageId = message.Reference?.MessageId.IsSpecified == true ? message.Reference.MessageId.Value : null,
                        IsFromThisBot = _botUserId.HasValue && message.Author.Id == _botUserId.Value
                    });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to gather channel history for {ChannelId}", commandContext.Channel.Id);
        }

        if (_enableLinkUnfurling)
        {
            messages = await UnfurlLinksInMessagesAsync(messages, cancellationToken);
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

    /// <summary>
    /// Gathers the reply chain by walking ReferencedMessage backwards from the trigger message.
    /// </summary>
    public async Task<IReadOnlyList<ChannelMessage>> GatherReplyChainAsync(
        IMessage triggerMessage,
        IMessageChannel channel,
        CancellationToken cancellationToken = default)
    {
        var chain = new List<ChannelMessage>();
        var current = triggerMessage;
        var visited = new HashSet<ulong>();

        while (current != null && chain.Count < _replyChainDepth)
        {
            if (!visited.Add(current.Id))
            {
                // Prevent infinite loops from circular references
                break;
            }

            IReadOnlyList<ChannelImage> images = Array.Empty<ChannelImage>();
            if (_allowImageContext)
            {
                images = CollectImages(current);
            }

            var embedLinks = ExtractEmbedsAsUnfurledLinks(current.Embeds, current.Timestamp);
            var httpLinks = _enableLinkUnfurling
                ? await _linkUnfurler.UnfurlAsync(current.Content ?? string.Empty, current.Timestamp, cancellationToken)
                : Array.Empty<UnfurledLink>();
            var unfurledLinks = MergeUnfurledLinks(httpLinks, embedLinks);

            chain.Add(new ChannelMessage
            {
                MessageId = current.Id,
                Author = current.Author.Username,
                Content = current.Content ?? string.Empty,
                Timestamp = current.Timestamp,
                IsBot = current.Author.IsBot,
                Images = images,
                UnfurledLinks = unfurledLinks,
                ReferencedMessageId = current.Reference?.MessageId.IsSpecified == true ? current.Reference.MessageId.Value : null,
                IsFromThisBot = _botUserId.HasValue && current.Author.Id == _botUserId.Value
            });

            // Walk to the parent message
            if (current.Reference?.MessageId.IsSpecified != true)
            {
                break;
            }

            try
            {
                current = await channel.GetMessageAsync(current.Reference.MessageId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch referenced message {MessageId} in reply chain", current.Reference.MessageId.Value);
                break;
            }
        }

        chain.Reverse(); // Oldest first
        
        _logger.LogInformation(
            "Gathered reply chain with {Count} messages: {Chain}",
            chain.Count,
            string.Join(" -> ", chain.Select(m => $"{(m.IsFromThisBot ? "Bot" : m.Author)}:{m.MessageId}")));
        
        return chain;
    }

    private async Task<List<ChannelMessage>> UnfurlLinksInMessagesAsync(
        List<ChannelMessage> messages,
        CancellationToken cancellationToken)
    {
        var tasks = messages.Select(async msg =>
        {
            try
            {
                var httpLinks = await _linkUnfurler.UnfurlAsync(msg.Content, msg.Timestamp, cancellationToken);
                var merged = MergeUnfurledLinks(httpLinks, msg.UnfurledLinks);
                return merged.Count > 0 ? msg with { UnfurledLinks = merged } : msg;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to unfurl links in message {MessageId}", msg.MessageId);
                return msg; // Keep any embed-derived links already on the message
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    internal IReadOnlyList<ChannelImage> CollectImages(IMessage message)
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

    /// <summary>
    /// Extracts content from Discord-generated embeds on a message, producing UnfurledLink objects
    /// without any HTTP calls. This is especially valuable for sites like Reddit that block
    /// datacenter IPs — Discord has already fetched and cached the content in its embed.
    /// </summary>
    internal static IReadOnlyList<UnfurledLink> ExtractEmbedsAsUnfurledLinks(
        IReadOnlyCollection<IEmbed> embeds, DateTimeOffset messageTimestamp)
    {
        if (embeds == null || embeds.Count == 0)
        {
            return Array.Empty<UnfurledLink>();
        }

        var results = new List<UnfurledLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var embed in embeds)
        {
            if (string.IsNullOrWhiteSpace(embed.Url))
            {
                continue;
            }

            // Only extract from rich/link embeds with meaningful content
            if (embed.Type != EmbedType.Rich && embed.Type != EmbedType.Link)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(embed.Title) && string.IsNullOrWhiteSpace(embed.Description))
            {
                continue;
            }

            if (!Uri.TryCreate(embed.Url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (!seenUrls.Add(embed.Url))
            {
                continue;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(embed.Title))
            {
                sb.AppendLine(embed.Title);
            }

            if (!string.IsNullOrWhiteSpace(embed.Description))
            {
                sb.Append(embed.Description);
            }

            var text = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Extract images from the embed
            var images = new List<ChannelImage>();
            if (embed.Image.HasValue && !string.IsNullOrWhiteSpace(embed.Image.Value.Url)
                && Uri.TryCreate(embed.Image.Value.Url, UriKind.Absolute, out var imgUri))
            {
                images.Add(new ChannelImage
                {
                    Url = imgUri,
                    Filename = Path.GetFileName(imgUri.LocalPath),
                    Source = "embed-image",
                    Timestamp = messageTimestamp
                });
            }
            else if (embed.Thumbnail.HasValue && !string.IsNullOrWhiteSpace(embed.Thumbnail.Value.Url)
                && Uri.TryCreate(embed.Thumbnail.Value.Url, UriKind.Absolute, out var thumbUri))
            {
                images.Add(new ChannelImage
                {
                    Url = thumbUri,
                    Filename = Path.GetFileName(thumbUri.LocalPath),
                    Source = "embed-thumbnail",
                    Timestamp = messageTimestamp
                });
            }

            // Build author string from embed metadata
            var author = embed.Author?.Name ?? string.Empty;
            if (embed.Footer.HasValue && !string.IsNullOrWhiteSpace(embed.Footer.Value.Text))
            {
                author = !string.IsNullOrWhiteSpace(author)
                    ? $"{author} · {embed.Footer.Value.Text}"
                    : embed.Footer.Value.Text;
            }

            // Determine source type from URL
            var sourceType = DetermineEmbedSourceType(uri);

            results.Add(new UnfurledLink
            {
                SourceType = sourceType,
                OriginalUrl = uri,
                Text = text,
                Author = author,
                Images = images
            });
        }

        return results;
    }

    /// <summary>
    /// Merges HTTP-unfurled links (from the unfurler pipeline) with embed-derived links.
    /// HTTP-unfurled links take priority for the same URL since they typically contain richer content.
    /// Embed-derived links fill in for URLs where the HTTP unfurler failed (e.g. Reddit 403s from datacenters).
    /// </summary>
    internal static IReadOnlyList<UnfurledLink> MergeUnfurledLinks(
        IReadOnlyList<UnfurledLink> httpLinks,
        IReadOnlyList<UnfurledLink> embedLinks)
    {
        if (embedLinks.Count == 0)
        {
            return httpLinks;
        }

        if (httpLinks.Count == 0)
        {
            return embedLinks;
        }

        var httpUrls = new HashSet<string>(
            httpLinks.Select(l => l.OriginalUrl.AbsoluteUri),
            StringComparer.OrdinalIgnoreCase);

        var merged = new List<UnfurledLink>(httpLinks);
        foreach (var embedLink in embedLinks)
        {
            if (!httpUrls.Contains(embedLink.OriginalUrl.AbsoluteUri))
            {
                merged.Add(embedLink);
            }
        }

        return merged;
    }

    private static string DetermineEmbedSourceType(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("reddit.com") || host == "redd.it")
        {
            return "reddit";
        }

        if (host.Contains("twitter.com") || host.Contains("x.com") || host == "t.co")
        {
            return "tweet";
        }

        if (host.Contains("wikipedia.org"))
        {
            return "wikipedia";
        }

        if (host == "news.ycombinator.com")
        {
            return "hackernews";
        }

        return "embed";
    }
}
