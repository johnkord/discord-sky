using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public sealed class EpisodeProjectionTests
{
    [Fact]
    public void JudgeAndGeneratorContainSameEvidenceAndGeneratorMarksValidatedReferent()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var episode = InteractionEpisode.Create(
            "episode-1",
            now,
            99,
            2,
            new[]
            {
                new EpisodeMessage(1, 10, "Alice", "meteor incoming", now.AddSeconds(-5)),
                new EpisodeMessage(2, 20, "Bob", "what is that?", now),
            },
            null,
            new ReferentRequirement(true, "deictic_question"),
            new[] { new ReferentCandidate(1, 0.75, "recent_message") });
        var decision = new EpisodeActionDecision(
            new ReferentDecision(1, 0.9, ReferentResolutionStatus.Resolved, "model_selected"));

        var judge = EpisodeProjectionBuilder.BuildJudgeProjection(episode, "scheming");
        var generator = EpisodeProjectionBuilder.BuildGeneratorProjection(episode, decision);

        Assert.All(new[] { "meteor incoming", "what is that?", "1", "2" }, value =>
        {
            Assert.Contains(value, judge.Text);
            Assert.Contains(value, generator.Text);
        });
        Assert.Contains("VALIDATED_REFERENT", generator.Text);
        Assert.Contains("never the Discord reply target", generator.Text);
        Assert.True(
            judge.Text.IndexOf("what is that?", StringComparison.Ordinal)
            < judge.Text.IndexOf("meteor incoming", StringComparison.Ordinal));
        Assert.True(
            generator.Text.IndexOf("what is that?", StringComparison.Ordinal)
            < generator.Text.IndexOf("meteor incoming", StringComparison.Ordinal));
        Assert.NotEqual(judge.ProjectionDigest, generator.ProjectionDigest);
        Assert.Equal(judge.MessageIds, generator.MessageIds);
    }
}