using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyConversationServiceTests
{
    [Theory]
    [InlineData(false, null, "WorldAutonomyAmbientConversation")]
    [InlineData(true, 5001UL, "WorldAutonomyDirectConversation")]
    public async Task Respond_UsesOneNoToolsCallAndPreservesDeliveryAttribution(
        bool direct,
        ulong? expectedReplyTarget,
        string expectedInvocationKind)
    {
        var client = new StubChatClient("A single imperial remark.");
        var transport = new RecordingTransport();
        var registry = new SentMessageRegistry();
        using var cdn = new VisionTestCdn();
        var transcripts = new RecordingTranscriptSink();
        var telemetry = new RecordingTelemetrySink();
        var service = new WorldAutonomyConversationService(
            client,
            OptionsMonitor(),
            transport,
            registry,
            transcripts,
            telemetry,
            Options.Create(new BotOptions { DefaultPersona = "Robotnik from AOSTH" }),
            NullLogger<WorldAutonomyConversationService>.Instance, vision: cdn.Reader());

        var result = await service.RespondAsync(Request(direct) with { ImageSelection = new([cdn.Attachment]) }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, client.CallCount);
        var user = Assert.Single(client.Messages);
        var image = Assert.Single(user.Contents.OfType<DataContent>());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(cdn.Png, image.Data.ToArray());
        Assert.NotNull(client.Options);
        Assert.Empty(client.Options!.Tools ?? []);
        Assert.Equal("gpt-5.6-sol", client.Options.ModelId);
        Assert.Equal(ReasoningEffort.ExtraHigh, client.Options.Reasoning?.Effort);
        Assert.DoesNotContain("discord_steward", client.Options.Instructions, StringComparison.Ordinal);
        Assert.Equal(expectedReplyTarget, Assert.Single(transport.Calls).ReplyTargetMessageId);
        Assert.True(registry.TryGet(7001, out var sent));
        Assert.Equal("world_autonomy_conversation", sent.Source);
        Assert.Equal((ulong)5001, sent.TriggerMessageId);
        Assert.Equal(expectedInvocationKind, Assert.Single(transcripts.Entries).InvocationKind);
        Assert.DoesNotContain("sensitive-signature", transcripts.Entries[0].Prompt);
        Assert.DoesNotContain(Convert.ToBase64String(cdn.Png), transcripts.Entries[0].Prompt);
        var metric = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.WorldAutonomyConversation, metric.EventType);
        Assert.Equal("delivered", metric.Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectSpeechFallbackHearsTheAppealButHasNoPowerToUndo(bool enabled)
    {
        var client = new StubChatClient("The calendar may submit a counteroffer.");
        var transport = new RecordingTransport();
        var telemetry = new RecordingTelemetrySink();
        var service = new WorldAutonomyConversationService(client, OptionsMonitor(), transport, new SentMessageRegistry(), new RecordingTranscriptSink(),
            telemetry, Options.Create(new BotOptions()), NullLogger<WorldAutonomyConversationService>.Instance, configuration:
            WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions { DecreeAppealsEnabled = enabled, EnabledGuilds = new() { ["4001"] = new() { ProfilePath = "/fixture" } } }));
        var appeal = new DecreeAppeal(4001, 6001, 4900, "Thursday belongs to the Crown.", false, "I need it back", true);
        await service.RespondAsync(Request(true) with { Appeal = appeal }, default);
        Assert.Equal(1, client.CallCount);
        Assert.Empty(client.Options!.Tools ?? []);
        Assert.Equal(enabled, Assert.Single(client.Messages).Text.Contains(appeal.Quotation, StringComparison.Ordinal));
        Assert.Equal(enabled, client.Messages[0].Text.Contains("spoken ruling only", StringComparison.Ordinal));
        Assert.Equal(5001UL, Assert.Single(transport.Calls).ReplyTargetMessageId);
        var events = telemetry.Events.Where(entry => entry.EventType == TelemetryEventTypes.WorldAutonomyAppeal);
        if (!enabled) { Assert.Empty(events); return; }
        var metric = Assert.Single(events);
        Assert.Equal(4900UL, metric.ReferentMessageId);
        Assert.Equal("quote_only", metric.Outcome);
        Assert.Null(metric.Note);
    }

    [Fact]
    public async Task PartialDeliveryRemainsHandledWithOnlyConfirmedText()
    {
        var client = new StubChatClient("First. Unconfirmed remainder.");
        var transport = new RecordingTransport(new([new(7001, 6001)], "First.", "partial_delivery", "IOException"));
        var transcripts = new RecordingTranscriptSink();
        var telemetry = new RecordingTelemetrySink();
        var registry = new SentMessageRegistry();
        var service = new WorldAutonomyConversationService(client, OptionsMonitor(), transport, registry, transcripts,
            telemetry, Options.Create(new BotOptions()), NullLogger<WorldAutonomyConversationService>.Instance);
        var result = await service.RespondAsync(Request(true), default);
        Assert.Equal("First.", result?.Text);
        Assert.Single(transport.Calls);
        Assert.Equal(1, client.CallCount);
        Assert.True(registry.TryGet(7001, out _));
        var transcript = Assert.Single(transcripts.Entries);
        Assert.Equal("First.", transcript.Reply);
        Assert.Equal("partial_delivery", transcript.Outcome);
        Assert.Equal("partial_delivery", Assert.Single(telemetry.Events).Outcome);
    }

    [Fact]
    public async Task Respond_EmptyModelOutputDoesNotSendOrRetry()
    {
        var client = new StubChatClient("   ");
        var transport = new RecordingTransport();
        var telemetry = new RecordingTelemetrySink();
        var service = new WorldAutonomyConversationService(
            client,
            OptionsMonitor(),
            transport,
            new SentMessageRegistry(),
            new RecordingTranscriptSink(),
            telemetry,
            Options.Create(new BotOptions { DefaultPersona = "Robotnik from AOSTH" }),
            NullLogger<WorldAutonomyConversationService>.Instance);

        var result = await service.RespondAsync(Request(direct: false), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, client.CallCount);
        Assert.Empty(transport.Calls);
        Assert.Equal("empty", Assert.Single(telemetry.Events).Outcome);
    }

    [Fact]
    public async Task Respond_TextOnlyFallbackCannotSubstituteAsciiForDrawing()
    {
        var client = new StubChatClient("```text\n ___\n/   \\\n|___|\n```");
        var transport = new RecordingTransport();
        var telemetry = new RecordingTelemetrySink();
        var service = new WorldAutonomyConversationService(client, OptionsMonitor(), transport,
            new SentMessageRegistry(), new RecordingTranscriptSink(), telemetry,
            Options.Create(new BotOptions()), NullLogger<WorldAutonomyConversationService>.Instance);

        var result = await service.RespondAsync(Request(true) with
        { MessageText = "can you draw Frieren as a world of warcraft player that's super bored" }, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, client.CallCount);
        Assert.Empty(transport.Calls);
        Assert.Equal("text_art_substitute", Assert.Single(telemetry.Events).Reason);
        Assert.Contains("Never draw with ASCII", client.Options!.Instructions);
    }

    private static WorldAutonomyConversationRequest Request(bool direct) => new(
        GuildId: 4001,
        ChannelId: 6001,
        TriggerMessageId: 5001,
        AuthorId: 8001,
        AuthorDisplayName: "member",
        ChannelName: "general",
        PersonaName: "Robotnik from AOSTH",
        MessageText: "An opening for mockery.",
        SituationContext: "The room is active.",
        MediaContext: null,
        MoodLabel: "gloating",
        EpisodeId: "episode-1",
        IsDirectAddress: direct);

    private static IOptionsMonitor<LlmOptions> OptionsMonitor() => new TestOptionsMonitor<LlmOptions>(new LlmOptions
    {
        ActiveProvider = "OpenAI",
        Providers = new Dictionary<string, LlmProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = new()
            {
                ChatModel = "gpt-5.6-sol",
                AmbientModel = "gpt-5.6-sol",
                AmbientReasoningEffort = "ExtraHigh",
            },
        },
    });

    private sealed class StubChatClient(string response) : IChatClient
    {
        public int CallCount { get; private set; }

        public ChatOptions? Options { get; private set; }
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Options = options;
            Messages = messages.ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingTransport(WorldAutonomyDeliveryReceipt? receipt = null) : IWorldAutonomyMessageTransport
    {
        public List<TransportCall> Calls { get; } = [];

        public Task<WorldAutonomyDeliveryReceipt> SendAsync(
            ulong guildId,
            ulong channelId,
            string content,
            ulong? replyTargetMessageId,
            CancellationToken cancellationToken)
        {
            Calls.Add(new TransportCall(content, replyTargetMessageId));
            return Task.FromResult(receipt ?? new WorldAutonomyDeliveryReceipt([new WorldAutonomyDeliveredMessage(7001, channelId)], content, "delivered"));
        }
    }

    private sealed record TransportCall(string Content, ulong? ReplyTargetMessageId);

    private sealed class RecordingTranscriptSink : ITranscriptSink
    {
        public List<TranscriptEntry> Entries { get; } = [];

        public void Record(TranscriptEntry entry) => Entries.Add(entry);
    }

    private sealed class RecordingTelemetrySink : IRecallTelemetrySink
    {
        public List<TelemetryEvent> Events { get; } = [];

        public void Emit(TelemetryEvent evt) => Events.Add(evt);
    }
}
