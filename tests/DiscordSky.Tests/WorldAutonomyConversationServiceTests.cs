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
            NullLogger<WorldAutonomyConversationService>.Instance);

        var result = await service.RespondAsync(Request(direct), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, client.CallCount);
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
        var metric = Assert.Single(telemetry.Events);
        Assert.Equal(TelemetryEventTypes.WorldAutonomyConversation, metric.EventType);
        Assert.Equal("delivered", metric.Outcome);
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

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Options = options;
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

    private sealed class RecordingTransport : IWorldAutonomyMessageTransport
    {
        public List<TransportCall> Calls { get; } = [];

        public Task<IReadOnlyList<WorldAutonomyDeliveredMessage>> SendAsync(
            ulong guildId,
            ulong channelId,
            string content,
            ulong? replyTargetMessageId,
            CancellationToken cancellationToken)
        {
            Calls.Add(new TransportCall(content, replyTargetMessageId));
            return Task.FromResult<IReadOnlyList<WorldAutonomyDeliveredMessage>>(
                [new WorldAutonomyDeliveredMessage(7001, channelId)]);
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
