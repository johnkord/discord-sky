using System.ComponentModel;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record WorldAutonomyDeliveredMessage(ulong MessageId, ulong ChannelId);

public interface IWorldAutonomyMessageTransport
{
    Task<IReadOnlyList<WorldAutonomyDeliveredMessage>> SendAsync(
        ulong guildId,
        ulong channelId,
        string content,
        ulong? replyTargetMessageId,
        CancellationToken cancellationToken);
}

public sealed class DiscordWorldAutonomyMessageTransport(DiscordSocketClient client)
    : IWorldAutonomyMessageTransport
{
    public async Task<IReadOnlyList<WorldAutonomyDeliveredMessage>> SendAsync(
        ulong guildId,
        ulong channelId,
        string content,
        ulong? replyTargetMessageId,
        CancellationToken cancellationToken)
    {
        if (client.GetChannel(channelId) is not ISocketMessageChannel channel ||
            channel is not SocketGuildChannel guildChannel ||
            guildChannel.Guild.Id != guildId)
        {
            throw new InvalidOperationException(
                $"Robotnik cannot find message channel '{channelId}' in guild '{guildId}'.");
        }

        var chunks = DiscordBotService.ChunkMessage(content, DiscordBotService.DiscordMaxMessageLength);
        var delivered = new List<WorldAutonomyDeliveredMessage>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = index == 0 && replyTargetMessageId.HasValue
                ? new MessageReference(replyTargetMessageId.Value)
                : null;
            var message = await channel.SendMessageAsync(
                chunks[index],
                messageReference: reference).ConfigureAwait(false);
            delivered.Add(new WorldAutonomyDeliveredMessage(message.Id, channelId));
        }

        return delivered;
    }
}

public sealed class WorldAutonomySpeechTool
{
    private readonly IWorldAutonomyMessageTransport _transport;
    private readonly SentMessageRegistry _sentMessages;
    private readonly ITranscriptSink _transcripts;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly BotOptions _botOptions;
    private readonly ILogger<WorldAutonomySpeechTool> _logger;
    private readonly TimeProvider _timeProvider;

    public WorldAutonomySpeechTool(
        IWorldAutonomyMessageTransport transport,
        SentMessageRegistry sentMessages,
        ITranscriptSink transcripts,
        IRecallTelemetrySink telemetry,
        IOptions<BotOptions> botOptions,
        ILogger<WorldAutonomySpeechTool> logger,
        TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _sentMessages = sentMessages;
        _transcripts = transcripts;
        _telemetry = telemetry;
        _botOptions = botOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AIFunction Bind(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        WorldAutonomyRunState run)
    {
        if (!opportunity.SourceChannelId.HasValue || !opportunity.SourceAuthorId.HasValue)
        {
            throw new InvalidOperationException("Robotnik speech requires a source Discord channel and author.");
        }

        var bound = new BoundSpeech(this, opportunity, context, run);
        return AIFunctionFactory.Create(
            bound.SpeakAsync,
            name: "speak_as_robotnik",
            description: "Speak as Robotnik in the Discord channel that summoned you. This is your normal voice and preserves reply, reaction, transcript, and run attribution. Long text is split safely.");
    }

    internal async Task<WorldAutonomySpeechResult> SendAsync(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        WorldAutonomyRunState run,
        string content,
        string? replyToMessageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Robotnik cannot deliver an empty proclamation.", nameof(content));
        }

        var channelId = opportunity.SourceChannelId!.Value;
        var replyTarget = ParseMessageId(replyToMessageId) ??
            (opportunity.IsDirectAddress ? ParseMessageId(opportunity.SourceMessageId) : null);
        var delivered = await _transport.SendAsync(
            opportunity.GuildId,
            channelId,
            content.Trim(),
            replyTarget,
            cancellationToken).ConfigureAwait(false);
        if (delivered.Count == 0)
        {
            throw new InvalidOperationException("Discord accepted no Robotnik messages.");
        }

        foreach (var message in delivered)
        {
            _sentMessages.Register(
                message.MessageId,
                _botOptions.DefaultPersona,
                "world_autonomy",
                ParseMessageId(opportunity.SourceMessageId),
                context.RunId,
                replyTarget);
        }

        try
        {
            await run.RecordDiscordDeliveryAsync(
                channelId,
                delivered.Select(message => message.MessageId).ToArray(),
                replyTarget,
                content.Trim().Length).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Discord already delivered the message. Never turn an audit-write failure into a duplicate send.
            _logger.LogError(
                exception,
                "Robotnik delivery {MessageId} succeeded but run {RunId} could not record delivery evidence.",
                delivered[0].MessageId,
                context.RunId);
        }

        var now = _timeProvider.GetUtcNow();
        _transcripts.Record(new TranscriptEntry(
            Timestamp: now,
            UserId: opportunity.SourceAuthorId!.Value,
            UserDisplayName: opportunity.SourceAuthorDisplayName ?? "unknown",
            ChannelId: channelId,
            ChannelName: opportunity.SourceChannelName,
            Persona: _botOptions.DefaultPersona,
            InvocationKind: opportunity.IsDirectAddress ? "WorldAutonomyDirect" : "WorldAutonomyAmbient",
            Prompt: opportunity.Prompt,
            Reply: content.Trim(),
            TranscriptSchemaVersion: FileBackedTranscriptSink.CurrentSchemaVersion,
            EpisodeId: context.RunId,
            TriggerMessageId: ParseMessageId(opportunity.SourceMessageId),
            ReplyTargetMessageId: replyTarget,
            Outcome: "delivered",
            ModelInvoked: true));
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: now,
            EventType: TelemetryEventTypes.WorldAutonomySpeech,
            UserHash: UserIdHash.Hash(opportunity.SourceAuthorId.Value),
            Channel: opportunity.SourceChannelName,
            Kind: opportunity.IsDirectAddress ? "direct" : "ambient",
            Outcome: "delivered",
            Count: delivered.Count,
            MessageId: delivered[0].MessageId,
            OperationId: context.RunId,
            EpisodeId: opportunity.SourceEpisodeId,
            CharacterCount: content.Trim().Length));

        return new WorldAutonomySpeechResult(
            "delivered",
            channelId.ToString(CultureInfo.InvariantCulture),
            delivered.Select(message => message.MessageId.ToString(CultureInfo.InvariantCulture)).ToArray(),
            replyTarget?.ToString(CultureInfo.InvariantCulture));
    }

    private static ulong? ParseMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
        {
            throw new ArgumentException($"'{value}' is not a valid Discord message ID.");
        }

        return parsed;
    }

    private sealed class BoundSpeech(
        WorldAutonomySpeechTool owner,
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        WorldAutonomyRunState run)
    {
        public Task<WorldAutonomySpeechResult> SpeakAsync(
            [Description("The exact in-character text Robotnik will post. Markdown and Discord mentions are allowed.")]
            string content,
            [Description("Optional Discord message ID to reply to. Omit it to reply to the direct trigger, or broadcast on an ambient turn.")]
            string? reply_to_message_id = null,
            CancellationToken cancellationToken = default) =>
            owner.SendAsync(opportunity, context, run, content, reply_to_message_id, cancellationToken);
    }
}

public sealed record WorldAutonomySpeechResult(
    string Outcome,
    string ChannelId,
    IReadOnlyList<string> MessageIds,
    string? ReplyTargetMessageId);