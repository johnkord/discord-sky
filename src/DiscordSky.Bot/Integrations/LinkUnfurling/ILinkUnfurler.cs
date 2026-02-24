using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

/// <summary>
/// Interface for link unfurling handlers. Each implementation handles a specific
/// category of URLs (e.g. tweets, general web pages) and extracts readable content.
/// </summary>
public interface ILinkUnfurler
{
    /// <summary>
    /// Checks whether this unfurler can handle the given URL.
    /// Called in priority order — the first handler that returns true wins.
    /// </summary>
    bool CanHandle(Uri url);

    /// <summary>
    /// Extracts content from the URLs found in the message text.
    /// Implementations should only process URLs they can handle.
    /// </summary>
    Task<IReadOnlyList<UnfurledLink>> UnfurlAsync(
        string messageContent,
        DateTimeOffset messageTimestamp,
        CancellationToken cancellationToken = default);
}
