using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class LearnedScamStoreTests
{
    [Fact]
    public void Add_AppliesGuardrails()
    {
        var store = Build();

        Assert.True(store.AddHost("Evil.Example"));
        Assert.Contains("evil.example", store.Hosts);
        Assert.False(store.AddHost("nodot"));        // must look like a domain
        Assert.False(store.AddHost("evil.example")); // duplicate

        Assert.True(store.AddPhrase("free wallet drain"));
        Assert.False(store.AddPhrase("ab"));         // too short
    }

    [Fact]
    public void Persists_AcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"learned-{Guid.NewGuid():N}.json");
        try
        {
            var a = Build(path);
            a.AddHost("evil.example");
            a.AddPhrase("drain your wallet");

            var b = Build(path);
            Assert.Contains("evil.example", b.Hosts);
            Assert.Contains("drain your wallet", b.Phrases);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static LearnedScamStore Build(string? path = null)
    {
        var options = new ScamGuardOptions
        {
            LearnedListPath = path ?? Path.Combine(Path.GetTempPath(), $"learned-{Guid.NewGuid():N}.json"),
        };
        return new LearnedScamStore(Options.Create(options), NullLogger<LearnedScamStore>.Instance);
    }
}
