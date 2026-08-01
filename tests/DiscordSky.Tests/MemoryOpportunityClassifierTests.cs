using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public sealed class MemoryOpportunityClassifierTests
{
    private readonly MemoryOpportunityClassifier _classifier = new();

    [Theory]
    [InlineData("I prefer tea to coffee", "preference_identity_change")]
    [InlineData("I moved to Vancouver last week", "preference_identity_change")]
    [InlineData("My raid team finally beat the boss", "first_person_assertion")]
    public void ProductiveFirstPersonWindowsAlwaysRun(string content, string reason)
    {
        var features = Extract(new BufferedMessage(1, 100, "Alice", content, Timestamp));

        var decision = _classifier.Classify(features);

        Assert.True(decision.WouldRun);
        Assert.Contains(reason, decision.ReasonCodes);
    }

    [Fact]
    public void QuestionOnlyWindowWouldSkip()
    {
        var features = Extract(
            new BufferedMessage(1, 100, "Alice", "Did the patch ship?", Timestamp),
            new BufferedMessage(2, 200, "Bob", "When is maintenance?", Timestamp.AddSeconds(2)));

        var decision = _classifier.Classify(features);

        Assert.False(decision.WouldRun);
        Assert.Contains("question_only", decision.ReasonCodes);
    }

    [Fact]
    public void MediaOnlyWindowWouldSkip()
    {
        var features = Extract(new BufferedMessage(1, 100, "Alice", string.Empty, Timestamp, HasMedia: true));

        var decision = _classifier.Classify(features);

        Assert.True(features.MediaOnly);
        Assert.False(decision.WouldRun);
        Assert.Contains("media_only", decision.ReasonCodes);
    }

    [Fact]
    public void TinySingleMessageWouldSkipButUncertainWindowRuns()
    {
        var tiny = _classifier.Classify(Extract(new BufferedMessage(1, 100, "Alice", "gg", Timestamp)));
        var uncertain = _classifier.Classify(Extract(
            new BufferedMessage(1, 100, "Alice", "The patch notes contain several unrelated changes", Timestamp),
            new BufferedMessage(2, 200, "Bob", "The rollout timing still looks uncertain to everyone", Timestamp.AddSeconds(3))));

        Assert.False(tiny.WouldRun);
        Assert.True(uncertain.WouldRun);
    }

    [Fact]
    public void LexicalNoveltyReflectsCurrentMemoryWithoutMutation()
    {
        var memories = new[] { Memory("Plays World of Warcraft every weekend") };
        var messages = new[]
        {
            new BufferedMessage(1, 100, "Alice", "World of Warcraft every weekend", Timestamp)
        };

        var features = MemoryOpportunityFeatureExtractor.Extract(
            messages,
            memories,
            isShutdownFlush: false,
            priorExtractionAge: TimeSpan.FromHours(2));

        Assert.InRange(features.LexicalNovelty, 0.0, 0.3);
        Assert.Equal(TimeSpan.FromHours(2), features.PriorExtractionAge);
        Assert.Equal("Plays World of Warcraft every weekend", memories[0].Content);
    }

    [Fact]
    public void FeatureExtractionIsDeterministic()
    {
        var messages = new[]
        {
            new BufferedMessage(1, 100, "Alice", "I started pottery", Timestamp),
            new BufferedMessage(2, 200, "Bob", "nice", Timestamp.AddSeconds(4)),
        };

        var first = MemoryOpportunityFeatureExtractor.Extract(messages, Array.Empty<UserMemory>(), false, null);
        var second = MemoryOpportunityFeatureExtractor.Extract(messages, Array.Empty<UserMemory>(), false, null);

        Assert.Equal(first, second);
    }

    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private static MemoryOpportunityFeatures Extract(params BufferedMessage[] messages) =>
        MemoryOpportunityFeatureExtractor.Extract(
            messages,
            Array.Empty<UserMemory>(),
            isShutdownFlush: false,
            priorExtractionAge: null);

    private static UserMemory Memory(string content) => new(
        content,
        "context",
        Timestamp,
        Timestamp,
        0);
}