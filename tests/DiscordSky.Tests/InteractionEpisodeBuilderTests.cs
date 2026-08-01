using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class InteractionEpisodeBuilderTests
{
    private sealed class FakeHistoryReader : IEpisodeHistoryReader
    {
        public IReadOnlyList<EpisodeMessage> Recent { get; init; } = Array.Empty<EpisodeMessage>();
        public EpisodeMessage? Parent { get; init; }

        public Task<IReadOnlyList<EpisodeMessage>> GetRecentAsync(
            ulong channelId,
            ulong triggerMessageId,
            int limit,
            DateTimeOffset after,
            CancellationToken cancellationToken) => Task.FromResult(Recent);

        public Task<EpisodeMessage?> GetMessageAsync(
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken) => Task.FromResult(Parent);
    }

    [Fact]
    public async Task Build_DeicticMeteorCreatesBoundedCandidates()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var history = new FakeHistoryReader
        {
            Recent = new[]
            {
                new EpisodeMessage(1, 10, "Alice", "A meteor appeared over town", now.AddSeconds(-10)),
                new EpisodeMessage(2, 11, "Bob", "run", now.AddSeconds(-5)),
            },
        };
        var builder = Build(history, now);

        var result = await builder.BuildAsync(Trigger(now, "what is that?"), "episode-1");

        Assert.True(result.IsSuccess);
        Assert.True(result.Episode!.ReferentRequirement.IsRequired);
        Assert.Equal(new ulong[] { 2, 1 }, result.Episode.ReferentCandidates.Select(candidate => candidate.MessageId));
        Assert.Equal(3, result.Episode.Messages.Count);
    }

    [Fact]
    public async Task Build_ExplicitReplyUsesParentAndNeedsNoInference()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var history = new FakeHistoryReader
        {
            Parent = new EpisodeMessage(7, 10, "Alice", "meteor incoming", now.AddSeconds(-4)),
        };
        var builder = Build(history, now);

        var result = await builder.BuildAsync(Trigger(now, "what is that?", referencedMessageId: 7));

        Assert.True(result.IsSuccess);
        Assert.False(result.Episode!.ReferentRequirement.IsRequired);
        Assert.Equal(7UL, result.Episode.ReplyParentMessageId);
        Assert.Equal(7UL, Assert.Single(result.Episode.ReferentCandidates).MessageId);
    }

    [Fact]
    public async Task Build_HistoryFailureReturnsTypedFailure()
    {
        var builder = Build(new ThrowingHistoryReader(), DateTimeOffset.UtcNow);

        var result = await builder.BuildAsync(Trigger(DateTimeOffset.UtcNow, "that?"));

        Assert.False(result.IsSuccess);
        Assert.Equal("history_failure", result.Failure!.ReasonCode);
    }

    private sealed class ThrowingHistoryReader : IEpisodeHistoryReader
    {
        public Task<IReadOnlyList<EpisodeMessage>> GetRecentAsync(ulong channelId, ulong triggerMessageId, int limit, DateTimeOffset after, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("failed");

        public Task<EpisodeMessage?> GetMessageAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken) =>
            Task.FromResult<EpisodeMessage?>(null);
    }

    private static InteractionEpisodeBuilder Build(IEpisodeHistoryReader history, DateTimeOffset now) => new(
        history,
        new TestOptionsMonitor<InteractionEpisodeOptions>(new InteractionEpisodeOptions()),
        NullLogger<InteractionEpisodeBuilder>.Instance,
        () => now);

    private static EpisodeTriggerEvidence Trigger(
        DateTimeOffset now,
        string text,
        ulong? referencedMessageId = null) => new(
            ChannelId: 99,
            MessageId: 3,
            AuthorId: 20,
            AuthorDisplayName: "Carol",
            ReferencedMessageId: referencedMessageId,
            View: new SemanticMessageView(
                text,
                null,
                Array.Empty<UnfurledLink>(),
                Array.Empty<ChannelImage>(),
                MessageId: 3,
                Timestamp: now));
}