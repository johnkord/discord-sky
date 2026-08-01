using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public sealed class InteractionEpisodeTests
{
    [Fact]
    public void Create_CopiesAndCanonicallyOrdersEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var images = new List<ChannelImage> { Image("a.png", now) };
        var messages = new List<EpisodeMessage>
        {
            new(2, 20, "trigger", "look at that", now, Images: images),
            new(1, 10, "prior", "meteor incoming", now.AddSeconds(-5)),
        };

        var episode = InteractionEpisode.Create(
            "episode-1",
            now,
            99,
            2,
            messages,
            null,
            new ReferentRequirement(true, "deictic_command"),
            new[] { new ReferentCandidate(1, 0.75, "recent_message") });
        images.Clear();
        messages.Clear();

        Assert.Equal(new ulong[] { 1, 2 }, episode.Messages.Select(message => message.MessageId));
        Assert.Single(episode.Trigger.Images!);
        Assert.Equal(EpisodeEvidenceMask.Trigger | EpisodeEvidenceMask.RecentMessages | EpisodeEvidenceMask.Media | EpisodeEvidenceMask.DeicticRisk, episode.EvidenceMask);
        Assert.DoesNotContain(episode.GetType().GetProperties(), property => property.PropertyType == typeof(ReferentDecision));
    }

    [Fact]
    public void EvidenceDigest_IgnoresRawTextButChangesWithMembership()
    {
        var first = BuildEpisode("first private text", includePrior: true);
        var textChanged = BuildEpisode("different private text", includePrior: true);
        var membershipChanged = BuildEpisode("first private text", includePrior: false);

        Assert.Equal(first.Fingerprint.EvidenceDigest, textChanged.Fingerprint.EvidenceDigest);
        Assert.NotEqual(first.Fingerprint.EvidenceDigest, membershipChanged.Fingerprint.EvidenceDigest);
    }

    [Fact]
    public void ProjectionDigests_DifferAndRemainBoundToEvidence()
    {
        var episode = BuildEpisode("look at that", includePrior: true);
        var judge = EpisodeDigest.ComputeProjectionDigest(episode, "judge", episode.Fingerprint.MessageIds);
        var generator = EpisodeDigest.ComputeProjectionDigest(episode, "generator", episode.Fingerprint.MessageIds, 1);

        Assert.NotEqual(judge, generator);
        Assert.Equal(64, episode.Fingerprint.EvidenceDigest.Length);
        Assert.Equal(64, judge.Length);
    }

    private static InteractionEpisode BuildEpisode(string triggerText, bool includePrior)
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var messages = new List<EpisodeMessage>();
        if (includePrior) messages.Add(new EpisodeMessage(1, 10, "prior", "meteor incoming", now.AddSeconds(-5)));
        messages.Add(new EpisodeMessage(2, 20, "trigger", triggerText, now));
        return InteractionEpisode.Create(
            "episode-1", now, 99, 2, messages, null,
            new ReferentRequirement(true, "bare_deictic"),
            includePrior ? new[] { new ReferentCandidate(1, 0.75, "recent_message") } : Array.Empty<ReferentCandidate>());
    }

    private static ChannelImage Image(string name, DateTimeOffset timestamp) => new()
    {
        Url = new Uri($"https://cdn.discordapp.com/{name}"),
        Filename = name,
        Source = "attachment",
        Timestamp = timestamp,
    };
}