using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyVisualToolTests
{
    [Fact]
    public async Task GeneratedBitmap_DeliversAndRegistersAttachment()
    {
        var fixture = new Fixture(VisualRequestIntent.BitmapRequired);
        var function = fixture.Bind();

        var raw = await function.InvokeAsync(new AIFunctionArguments
        {
            ["medium"] = "generated_bitmap",
            ["visual_prompt"] = "Robotnik presiding over a ridiculous department",
            ["caption"] = "Behold my administrative masterpiece.",
        }, CancellationToken.None);

        var result = Assert.IsType<System.Text.Json.JsonElement>(raw);
        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.Equal("generated_bitmap", result.GetProperty("medium").GetString());
        var call = Assert.Single(fixture.VisualTransport.Calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, call.Bytes);
        Assert.Equal((ulong)5001, call.ReplyTargetMessageId);
        Assert.True(fixture.Registry.TryGet(7001, out var sent));
        Assert.Equal("world_autonomy_visual", sent.Source);
        Assert.True(fixture.Run.SpokeInChannel);
        Assert.True(fixture.Run.VisualMediumSelected);
        Assert.True(fixture.Run.VisualDelivered);
        Assert.Contains(fixture.ImageLog.Records, record =>
            record.Outcome == ImageGenerationRecord.OutcomeOk && record.ToolSelected == true);
        Assert.Contains(fixture.Telemetry.Events, metric =>
            metric.EventType == TelemetryEventTypes.WorldAutonomyVisual
            && metric.Kind == "generated_bitmap"
            && metric.Outcome == "delivered"
            && metric.MessageId == 7001);
    }

    [Fact]
    public async Task TextArt_DeliversThroughRegisteredSpeechPath()
    {
        var fixture = new Fixture(VisualRequestIntent.MediumChoice);
        var function = fixture.Bind();

        var raw = await function.InvokeAsync(new AIFunctionArguments
        {
            ["medium"] = "text_art",
            ["text_art"] = "[ EGGMAN ASCII MURAL ]",
        }, CancellationToken.None);

        var result = Assert.IsType<System.Text.Json.JsonElement>(raw);
        Assert.Equal("text_art", result.GetProperty("medium").GetString());
        Assert.Empty(fixture.VisualTransport.Calls);
        Assert.Equal("[ EGGMAN ASCII MURAL ]", Assert.Single(fixture.SpeechTransport.Calls).Content);
        Assert.True(fixture.Registry.TryGet(7002, out var sent));
        Assert.Equal("world_autonomy", sent.Source);
        Assert.True(fixture.Run.VisualDelivered);
        Assert.Contains(fixture.ImageLog.Records, record =>
            record.Outcome == ImageGenerationRecord.OutcomeNotSelected);
        Assert.Contains(fixture.Telemetry.Events, metric =>
            metric.EventType == TelemetryEventTypes.WorldAutonomyVisual
            && metric.Kind == "text_art"
            && metric.Outcome == "delivered");
    }

    [Fact]
    public async Task InvalidExplicitTextArt_DoesNotConsumeRequiredBitmapChoice()
    {
        var fixture = new Fixture(VisualRequestIntent.BitmapRequired);
        var function = fixture.Bind();

        await Assert.ThrowsAnyAsync<Exception>(() => function.InvokeAsync(new AIFunctionArguments
        {
            ["medium"] = "text_art",
            ["text_art"] = "forbidden substitute",
        }, CancellationToken.None).AsTask());

        Assert.False(fixture.Run.VisualMediumSelected);
        await function.InvokeAsync(new AIFunctionArguments
        {
            ["medium"] = "generated_bitmap",
            ["visual_prompt"] = "the required bitmap",
        }, CancellationToken.None);
        Assert.True(fixture.Run.VisualMediumSelected);
        Assert.Single(fixture.VisualTransport.Calls);
    }

    [Fact]
    public async Task OverlongCaption_DoesNotGenerateOrConsumeBitmapChoice()
    {
        var fixture = new Fixture(VisualRequestIntent.BitmapRequired);
        var function = fixture.Bind();

        await Assert.ThrowsAnyAsync<Exception>(() => function.InvokeAsync(new AIFunctionArguments
        {
            ["medium"] = "generated_bitmap",
            ["visual_prompt"] = "an expensive masterpiece",
            ["caption"] = new string('x', 2001),
        }, CancellationToken.None).AsTask());

        Assert.False(fixture.Run.VisualMediumSelected);
        Assert.Empty(fixture.VisualTransport.Calls);
        Assert.Empty(fixture.ImageLog.Records);
    }

    private sealed class Fixture
    {
        internal RecordingVisualTransport VisualTransport { get; } = new();
        internal RecordingSpeechTransport SpeechTransport { get; } = new();
        internal SentMessageRegistry Registry { get; } = new();
        internal RecordingTranscriptSink Transcripts { get; } = new();
        internal RecordingTelemetrySink Telemetry { get; } = new();
        internal RecordingImageLog ImageLog { get; } = new();
        internal WorldAutonomyRunState Run { get; }

        private readonly WorldAutonomyVisualTool _tool;
        private readonly WorldAutonomyOpportunity _opportunity;
        private readonly WorldAutonomyRunContext _context;

        internal Fixture(VisualRequestIntent intent)
        {
            _context = new WorldAutonomyRunContext(
                "run-1", 4001, "discord_message", "5001", "episode-1", "trace-1",
                "gpt-5.6-sol", "profile", "manifest", []);
            _opportunity = new WorldAutonomyOpportunity(
                4001,
                "discord_message",
                "Draw for your court.",
                SourceMessageId: "5001",
                IsDirectAddress: true,
                SourceChannelId: 6001,
                SourceChannelName: "general",
                SourceAuthorId: 8001,
                SourceAuthorDisplayName: "test-member",
                VisualIntent: intent);
            Run = new WorldAutonomyRunState(_context, new NoOpLedger(), []);
            var options = new ImageOptions
            {
                Model = "gpt-image-2",
                PerUserPerHour = 0,
                GlobalPerDay = 0,
                MonthlyUsdGuard = 0,
                MaxConcurrent = 4,
            };
            var imageService = new ImageToolService(
                new ImageBudget(Options.Create(options), ImageLog),
                new StubImageGenerator(),
                ImageLog,
                Options.Create(options),
                NullLogger<ImageToolService>.Instance);
            var speechTool = new WorldAutonomySpeechTool(
                SpeechTransport,
                Registry,
                Transcripts,
                Telemetry,
                Options.Create(new BotOptions { DefaultPersona = "Robotnik from AOSTH" }),
                NullLogger<WorldAutonomySpeechTool>.Instance);
            _tool = new WorldAutonomyVisualTool(
                imageService,
                VisualTransport,
                speechTool,
                Registry,
                Transcripts,
                Telemetry,
                Options.Create(new BotOptions { DefaultPersona = "Robotnik from AOSTH" }),
                NullLogger<WorldAutonomyVisualTool>.Instance);
        }

        internal AIFunction Bind() => _tool.Bind(_opportunity, _context, Run);
    }

    private sealed class StubImageGenerator : DiscordSky.Bot.Integrations.Images.IImageGenerator
    {
        public bool IsEnabled => true;

        public Task<ImageResult> GenerateAsync(
            string prompt,
            ImageRequestOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult(ImageResult.Ok([1, 2, 3], "jpg", null));
    }

    private sealed class RecordingVisualTransport : IWorldAutonomyVisualTransport
    {
        internal List<VisualCall> Calls { get; } = [];

        public Task<WorldAutonomyDeliveredMessage> SendAsync(
            ulong guildId,
            ulong channelId,
            byte[] imageBytes,
            string fileName,
            string caption,
            ulong? replyTargetMessageId,
            CancellationToken cancellationToken)
        {
            Calls.Add(new VisualCall(imageBytes, caption, replyTargetMessageId));
            return Task.FromResult(new WorldAutonomyDeliveredMessage(7001, channelId));
        }
    }

    private sealed record VisualCall(byte[] Bytes, string Caption, ulong? ReplyTargetMessageId);

    private sealed class RecordingSpeechTransport : IWorldAutonomyMessageTransport
    {
        internal List<SpeechCall> Calls { get; } = [];

        public Task<IReadOnlyList<WorldAutonomyDeliveredMessage>> SendAsync(
            ulong guildId,
            ulong channelId,
            string content,
            ulong? replyTargetMessageId,
            CancellationToken cancellationToken)
        {
            Calls.Add(new SpeechCall(content, replyTargetMessageId));
            return Task.FromResult<IReadOnlyList<WorldAutonomyDeliveredMessage>>(
                [new WorldAutonomyDeliveredMessage(7002, channelId)]);
        }
    }

    private sealed record SpeechCall(string Content, ulong? ReplyTargetMessageId);

    private sealed class RecordingImageLog : IImageGenerationLog
    {
        internal List<ImageGenerationRecord> Records { get; } = [];
        public void Record(ImageGenerationRecord record) => Records.Add(record);
        public int CountSuccessesOnUtcDay(DateOnly utcDay) => 0;
        public double SumSuccessCostInUtcMonth(DateTimeOffset now) => 0;
    }

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

    private sealed class NoOpLedger : IWorldAutonomyLedger
    {
        public Task StartRunAsync(WorldAutonomyRunStart run, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordDispatchPendingAsync(WorldAutonomyPendingDispatch dispatch, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CompleteToolCallAsync(string callId, string dispatchStatus, string? resultJson, string? errorMessage, DateTimeOffset completedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, string status, string? finalText, string? failureReason, DateTimeOffset completedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<WorldAutonomyToolCall>> ListRecoverableCallsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorldAutonomyToolCall>>([]);
        public Task<IReadOnlyList<WorldAutonomyRunRecord>> ListRunningRunsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorldAutonomyRunRecord>>([]);
        public Task<IReadOnlyList<WorldAutonomyToolCall>> ListToolCallsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorldAutonomyToolCall>>([]);
        public Task<WorldAutonomyRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<WorldAutonomyRunRecord?>(null);
        public Task RecordRunEventAsync(string runId, string kind, string? payloadJson, DateTimeOffset occurredAt, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}