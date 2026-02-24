using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Chains multiple ILinkUnfurler implementations together.
/// Each handler processes the URLs it can handle from the message.
/// Results are deduplicated by URL so specialized handlers (e.g. TweetUnfurler,
/// registered first) take priority over the general WebContentUnfurler.
/// </summary>
public sealed class CompositeUnfurler : ILinkUnfurler
{
    private readonly IReadOnlyList<ILinkUnfurler> _unfurlers;
    private readonly ILogger<CompositeUnfurler> _logger;

    public CompositeUnfurler(IEnumerable<ILinkUnfurler> unfurlers, ILogger<CompositeUnfurler> logger)
    {
        _unfurlers = unfurlers.ToList();
        _logger = logger;
    }

    /// <summary>
    /// Returns true if any registered unfurler can handle this URL.
    /// </summary>
    public bool CanHandle(Uri url)
    {
        return _unfurlers.Any(u => u.CanHandle(url));
    }

    /// <summary>
    /// Extracts all URLs from the message, dispatches each to the appropriate handler,
    /// and aggregates the results.
    /// </summary>
    public async Task<IReadOnlyList<UnfurledLink>> UnfurlAsync(
        string messageContent,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageContent))
        {
            return Array.Empty<UnfurledLink>();
        }

        // Delegate to each handler — each handler internally finds the URLs it can handle
        var allResults = new List<UnfurledLink>();
        var processedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var unfurler in _unfurlers)
        {
            try
            {
                var results = await unfurler.UnfurlAsync(messageContent, messageTimestamp, cancellationToken);
                foreach (var link in results)
                {
                    // Deduplicate across handlers by URL
                    if (processedUrls.Add(link.OriginalUrl.AbsoluteUri))
                    {
                        allResults.Add(link);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Link unfurler {Type} failed", unfurler.GetType().Name);
            }
        }

        return allResults;
    }
}
