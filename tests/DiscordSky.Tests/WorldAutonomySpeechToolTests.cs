using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class WorldAutonomySpeechToolTests
{
    [Fact]
    public async Task Speak_DeliversThroughSkyAndRegistersEveryMessage()
    {
        var transport = new RecordingTransport(
            new WorldAutonomyDeliveredMessage(7001, 6001),
            new WorldAutonomyDeliveredMessage(7002, 6001));
        var registry = new SentMessageRegistry();
        var transcripts = new RecordingTranscriptSink();
        var telemetry = new RecordingTelemetrySink();
        var ledger = new RecordingLedger();
        var context = Context();
        var run = new WorldAutonomyRunState(context, ledger, []);
        var tool = BuildTool(transport, registry, transcripts, telemetry)
            .Bind(Opportunity(isDirect: true), context, run);

        var rawResult = await tool.InvokeAsync(
            new AIFunctionArguments { ["content"] = "Attend, fools. Your administrator has arrived." },
            CancellationToken.None);

        var result = Assert.IsType<System.Text.Json.JsonElement>(rawResult);
        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.Equal(
            ["7001", "7002"],
            result.GetProperty("messageIds").EnumerateArray().Select(item => item.GetString()).ToArray());
        var call = Assert.Single(transport.Calls);
        Assert.Equal((ulong)5001, call.ReplyTargetMessageId);

        Assert.True(registry.TryGet(7001, out var first));
        Assert.True(registry.TryGet(7002, out var second));
        Assert.Equal("Robotnik from AOSTH", first.Persona);
        Assert.Equal("world_autonomy", first.Source);
        Assert.Equal((ulong)5001, first.TriggerMessageId);
        Assert.Equal("run-1", first.EpisodeId);
        Assert.Equal(first with { CreatedAt = second.CreatedAt }, second);

        var transcript = Assert.Single(transcripts.Entries);
        Assert.Equal("WorldAutonomyDirect", transcript.InvocationKind);
        Assert.Equal("delivered", transcript.Outcome);
        Assert.Equal((ulong)5001, transcript.ReplyTargetMessageId);
        Assert.Equal("Attend, fools. Your administrator has arrived.", transcript.Reply);

        var metric = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.WorldAutonomySpeech, metric.EventType);
        Assert.Equal("delivered", metric.Outcome);
        Assert.Equal(2, metric.Count);
        Assert.Equal((ulong)7001, metric.MessageId);
        Assert.Equal("run-1", metric.OperationId);

        var delivery = Assert.Single(ledger.Events);
        Assert.Equal("discord_delivery", delivery.Kind);
        Assert.True(run.SpokeInChannel);
    }

    [Fact]
    public async Task Speak_AmbientBroadcastDoesNotInventAReplyTarget()
    {
        var transport = new RecordingTransport(new WorldAutonomyDeliveredMessage(7001, 6001));
        var context = Context();
        var run = new WorldAutonomyRunState(context, new RecordingLedger(), []);
        var tool = BuildTool(
                transport,
                new SentMessageRegistry(),
                new RecordingTranscriptSink(),
                new RecordingTelemetrySink())
            .Bind(Opportunity(isDirect: false), context, run);

        await tool.InvokeAsync(
            new AIFunctionArguments { ["content"] = "I have annexed this silence." },
            CancellationToken.None);

        Assert.Null(Assert.Single(transport.Calls).ReplyTargetMessageId);
    }

    [Fact]
    public async Task Speak_HonorsAnExplicitReplyTarget()
    {
        var transport = new RecordingTransport(new WorldAutonomyDeliveredMessage(7001, 6001));
        var context = Context();
        var run = new WorldAutonomyRunState(context, new RecordingLedger(), []);
        var tool = BuildTool(
                transport,
                new SentMessageRegistry(),
                new RecordingTranscriptSink(),
                new RecordingTelemetrySink())
            .Bind(Opportunity(isDirect: true), context, run);

        await tool.InvokeAsync(
            new AIFunctionArguments
            {
                ["content"] = "That one. The loud one.",
                ["reply_to_message_id"] = "5002"
            },
            CancellationToken.None);

        Assert.Equal((ulong)5002, Assert.Single(transport.Calls).ReplyTargetMessageId);
    }

    [Fact]
    public async Task Speak_DoesNotRetryDiscordWhenDeliveryEvidenceCannotBePersisted()
    {
        var transport = new RecordingTransport(new WorldAutonomyDeliveredMessage(7001, 6001));
        var registry = new SentMessageRegistry();
        var ledger = new RecordingLedger { FailDeliveryEvents = true };
        var context = Context();
        var run = new WorldAutonomyRunState(context, ledger, []);
        var tool = BuildTool(
                transport,
                registry,
                new RecordingTranscriptSink(),
                new RecordingTelemetrySink())
            .Bind(Opportunity(isDirect: true), context, run);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments { ["content"] = "The record-keeper has failed me again." },
            CancellationToken.None);

        var envelope = Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal("delivered", envelope.GetProperty("outcome").GetString());
        Assert.Single(transport.Calls);
        Assert.True(registry.TryGet(7001, out _));
        Assert.True(run.SpokeInChannel);
    }

    private static WorldAutonomySpeechTool BuildTool(
        IWorldAutonomyMessageTransport transport,
        SentMessageRegistry registry,
        ITranscriptSink transcripts,
        IRecallTelemetrySink telemetry) => new(
            transport,
            registry,
            transcripts,
            telemetry,
            Options.Create(new BotOptions { DefaultPersona = "Robotnik from AOSTH" }),
            NullLogger<WorldAutonomySpeechTool>.Instance);

    private static WorldAutonomyOpportunity Opportunity(bool isDirect) => new(
        GuildId: 4001,
        Trigger: "discord_message",
        Prompt: "The room has demanded an audience.",
        SourceMessageId: "5001",
        SourceEpisodeId: "episode-1",
        TraceId: "trace-1",
        IsDirectAddress: isDirect,
        SourceChannelId: 6001,
        SourceChannelName: "general",
        SourceAuthorId: 8001,
        SourceAuthorDisplayName: "test-member");

    private static WorldAutonomyRunContext Context() => new(
        RunId: "run-1",
        GuildId: 4001,
        Trigger: "discord_message",
        SourceMessageId: "5001",
        SourceEpisodeId: "episode-1",
        TraceId: "trace-1",
        Model: "gpt-5.6-sol",
        ProfileDigest: "profile",
        ManifestDigest: "manifest",
        RequestIdPool: []);

    private sealed class RecordingTransport(params WorldAutonomyDeliveredMessage[] delivered)
        : IWorldAutonomyMessageTransport
    {
        internal List<TransportCall> Calls { get; } = [];

        public Task<IReadOnlyList<WorldAutonomyDeliveredMessage>> SendAsync(
            ulong guildId,
            ulong channelId,
            string content,
            ulong? replyTargetMessageId,
            CancellationToken cancellationToken)
        {
            Calls.Add(new TransportCall(guildId, channelId, content, replyTargetMessageId));
            return Task.FromResult<IReadOnlyList<WorldAutonomyDeliveredMessage>>(delivered);
        }
    }

    private sealed record TransportCall(
        ulong GuildId,
        ulong ChannelId,
        string Content,
        ulong? ReplyTargetMessageId);

    private sealed class RecordingTranscriptSink : ITranscriptSink
    {
        internal List<TranscriptEntry> Entries { get; } = [];

        public void Record(TranscriptEntry entry) => Entries.Add(entry);
    }

    private sealed class RecordingTelemetrySink : IRecallTelemetrySink
    {
        internal List<TelemetryEvent> Events { get; } = [];

        public void Emit(TelemetryEvent evt) => Events.Add(evt);
    }

    private sealed class RecordingLedger : IWorldAutonomyLedger
    {
        internal List<RecordedEvent> Events { get; } = [];

        internal bool FailDeliveryEvents { get; init; }

        public Task StartRunAsync(WorldAutonomyRunStart run, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordDispatchPendingAsync(
            WorldAutonomyPendingDispatch dispatch,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteToolCallAsync(
            string callId,
            string dispatchStatus,
            string? resultJson,
            string? errorMessage,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteRunAsync(
            string runId,
            string status,
            string? finalText,
            string? failureReason,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<WorldAutonomyToolCall>> ListRecoverableCallsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorldAutonomyToolCall>>([]);

        public Task<IReadOnlyList<WorldAutonomyRunRecord>> ListRunningRunsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorldAutonomyRunRecord>>([]);

        public Task<IReadOnlyList<WorldAutonomyToolCall>> ListToolCallsAsync(
            string runId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorldAutonomyToolCall>>([]);

        public Task<WorldAutonomyRunRecord?> GetRunAsync(
            string runId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorldAutonomyRunRecord?>(null);

        public Task RecordRunEventAsync(
            string runId,
            string kind,
            string? payloadJson,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken)
        {
            if (FailDeliveryEvents && kind == "discord_delivery")
            {
                throw new IOException("The ledger has misplaced its quill.");
            }

            Events.Add(new RecordedEvent(runId, kind, payloadJson));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedEvent(string RunId, string Kind, string? PayloadJson);
}