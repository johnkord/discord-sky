using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class PhishingDomainFeedTests
{
    [Fact]
    public void ApplyFull_PopulatesSet_CaseInsensitiveAndCleaned()
    {
        var feed = Build();
        feed.ApplyFull(new[] { "evil.com", "Bad.NET", "   ", "nodot" });

        Assert.True(feed.Contains("evil.com"));
        Assert.True(feed.Contains("BAD.net"));
        Assert.False(feed.Contains("good.com"));
        Assert.Equal(2, feed.Count); // blank and dot-less entries dropped
    }

    [Fact]
    public void ApplyDelta_AddsAndRemoves()
    {
        var feed = Build();
        feed.ApplyFull(new[] { "evil.com" });

        feed.ApplyDelta(new[]
        {
            new PhishingDomainFeed.DbEdit("add", new[] { "new.com" }),
            new PhishingDomainFeed.DbEdit("delete", new[] { "evil.com" }),
        });

        Assert.True(feed.Contains("new.com"));
        Assert.False(feed.Contains("evil.com"));
    }

    [Fact]
    public void Cache_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phish-{Guid.NewGuid():N}.json");
        try
        {
            var feed = Build(path);
            feed.ApplyFull(new[] { "evil.com", "bad.net" });
            feed.SaveCache();

            var reloaded = Build(path);
            reloaded.LoadCache();

            Assert.True(reloaded.Contains("evil.com"));
            Assert.True(reloaded.Contains("bad.net"));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static PhishingDomainFeed Build(string? cachePath = null)
    {
        var options = new ScamGuardOptions
        {
            PhishingFeedCachePath = cachePath ?? Path.Combine(Path.GetTempPath(), $"phish-{Guid.NewGuid():N}.json"),
        };
        return new PhishingDomainFeed(
            Options.Create(options), new StubHttpClientFactory(), NullLogger<PhishingDomainFeed>.Instance);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
