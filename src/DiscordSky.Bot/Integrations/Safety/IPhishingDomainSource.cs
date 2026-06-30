namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// A set of known phishing domains the scam detector can consult. Kept tiny and synchronous so the detector
/// stays a pure, local, instant check; the heavy lifting (fetching/refreshing the list) lives behind the
/// implementation. <see cref="Contains"/> takes an exact registrable domain (the caller expands suffixes).
/// </summary>
public interface IPhishingDomainSource
{
    bool Contains(string domain);

    int Count { get; }
}

/// <summary>No-op source used when the phishing feed is disabled, so the detector falls back to heuristics.</summary>
public sealed class NullPhishingDomainSource : IPhishingDomainSource
{
    public static readonly NullPhishingDomainSource Instance = new();

    public bool Contains(string domain) => false;

    public int Count => 0;
}
