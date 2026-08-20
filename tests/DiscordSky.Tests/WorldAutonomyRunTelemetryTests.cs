using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyRunTelemetryTests
{
    [Fact]
    public void Create_EmitsJoinableContentFreeRunSummary()
    {
        var opportunity = new WorldAutonomyOpportunity(
            GuildId: 4001,
            Trigger: "discord_message",
            Prompt: "private room text",
            SourceMessageId: "5001",
            SourceEpisodeId: "episode-1",
            TraceId: "trace-1",
            IsDirectAddress: true,
            SourceChannelId: 6001,
            SourceChannelName: "private-channel");
        var context = new WorldAutonomyRunContext(
            "run-1", 4001, "discord_message", "5001", "episode-1", "trace-1",
            "gpt-5.6-sol", "profile", "manifest", []);
        var activity = new WorldAutonomyRunActivitySnapshot(
            NativeReadCount: 2,
            NativeWriteCount: 1,
            AcceptedWriteCount: 0,
            SucceededWriteCount: 1,
            FailedWriteCount: 0,
            PartialFailureWriteCount: 0,
            UnknownWriteCount: 0,
            DiscordDelivered: true,
            VisualDelivered: false);
        var usage = new LlmRunUsageSnapshot(3, 1000, 80, 400, 200, 30, 1080);
        var startedAt = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        var evt = WorldAutonomyRunTelemetry.Create(
            opportunity,
            context,
            "gpt-5.6-sol",
            WorldAutonomyRunStatuses.Succeeded,
            failureReason: null,
            startedAt,
            startedAt.AddSeconds(2),
            activity,
            usage);

        Assert.Equal(TelemetryEventTypes.WorldAutonomyRun, evt.EventType);
        Assert.Equal("direct", evt.Kind);
        Assert.Equal("succeeded", evt.Outcome);
        Assert.Equal("run-1", evt.EvaluationId);
        Assert.Equal("run-1", evt.OperationId);
        Assert.Equal("episode-1", evt.EpisodeId);
        Assert.Equal(UserIdHash.Hash(4001), evt.GuildHash);
        Assert.Equal(UserIdHash.Hash(6001), evt.ChannelHash);
        Assert.Equal(3, evt.ProviderCallCount);
        Assert.Equal(2, evt.NativeReadCount);
        Assert.Equal(1, evt.NativeWriteCount);
        Assert.Equal(1, evt.SucceededWriteCount);
        Assert.Equal(1000, evt.InputTokens);
        Assert.Equal(400, evt.CachedInputTokens);
        Assert.Equal(200, evt.CacheWriteInputTokens);
        Assert.Equal(30, evt.ReasoningTokens);
        Assert.Equal(2000, evt.LatencyMs);
        Assert.True(evt.DiscordDelivered);
        Assert.False(evt.VisualDelivered);
        Assert.Null(evt.Channel);
        Assert.Null(evt.Note);
        Assert.Null(evt.Room);
    }

    [Theory]
    [InlineData(false, "WorldAutonomyHostFallback")]
    [InlineData(true, "WorldAutonomyFinalText")]
    public void FallbackTranscript_PreservesReasonTargetAndInvocationTruth(
        bool modelInvoked,
        string expectedKind)
    {
        var timestamp = new DateTimeOffset(2026, 8, 20, 6, 0, 0, TimeSpan.Zero);

        var entry = DiscordBotService.CreateWorldAutonomyFallbackTranscript(
            timestamp,
            userId: 42,
            userDisplayName: "member",
            channelId: 84,
            channelName: "room",
            persona: "Robotnik",
            prompt: "petition",
            reply: "decree",
            triggerMessageId: 126,
            operationId: "run-1",
            outcome: "hourly_cost_budget_exhausted",
            modelInvoked);

        Assert.Equal(expectedKind, entry.InvocationKind);
        Assert.Equal("hourly_cost_budget_exhausted", entry.Outcome);
        Assert.Equal((ulong)126, entry.TriggerMessageId);
        Assert.Equal((ulong)126, entry.ReplyTargetMessageId);
        Assert.Equal("run-1", entry.EpisodeId);
        Assert.Equal(modelInvoked, entry.ModelInvoked);
    }
}