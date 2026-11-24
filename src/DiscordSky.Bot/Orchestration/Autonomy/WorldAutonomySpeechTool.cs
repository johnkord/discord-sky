using System.ComponentModel;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed record WorldAutonomyDeliveredMessage(ulong MessageId, ulong ChannelId);

public sealed record WorldAutonomyDeliveryReceipt(IReadOnlyList<WorldAutonomyDeliveredMessage> Messages,
    string ConfirmedText, string Outcome, string? Failure = null)
{
    internal static bool IsDefiniteRejection(Exception error) => error is WorldAutonomyDestinationUnavailableException ||
        error is Discord.Net.HttpException http && (int)http.HttpCode is >= 400 and < 500 and not 408 ||
        error is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500 and not 408;

    internal static RequestOptions SendOptions(CancellationToken token) => new() { CancelToken = token, RetryMode = RetryMode.RetryRatelimit };

    internal static async Task<WorldAutonomyDeliveryReceipt> SendOnceAsync(IWorldAutonomyMessageTransport transport, ulong guildId,
        ulong channelId, string text, ulong? target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var receipt = await transport.SendAsync(guildId, channelId, text, target, cancellationToken).ConfigureAwait(false);
            return receipt.Messages.Count > 0 ? receipt : new([], "", "delivery_unknown", "no_acknowledgement");
        }
        catch (Exception error)
        {
            return new([], "", IsDefiniteRejection(error) ? "not_sent" : "delivery_unknown", error.GetType().Name);
        }
    }

    internal void Register(SentMessageRegistry registry, string persona, string source, ulong? trigger, string? operation, ulong? target)
    {
        for (var index = 0; index < Messages.Count; index++)
            registry.Register(Messages[index].MessageId, persona, source, trigger, operation, index == 0 ? target : null);
    }
}

internal sealed class WorldAutonomyDestinationUnavailableException(string message) : InvalidOperationException(message);

public interface IWorldAutonomyMessageTransport
{
    Task<WorldAutonomyDeliveryReceipt> SendAsync(
        ulong guildId,
        ulong channelId,
        string content,
        ulong? replyTargetMessageId,
        CancellationToken cancellationToken);
}

public sealed class DiscordWorldAutonomyMessageTransport(DiscordSocketClient client)
    : IWorldAutonomyMessageTransport
{
    public Task<WorldAutonomyDeliveryReceipt> SendAsync(
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
            throw new WorldAutonomyDestinationUnavailableException(
                $"Robotnik cannot find message channel '{channelId}' in guild '{guildId}'.");
        }

        return SendChunksAsync(content, replyTargetMessageId, async (chunk, target, token) =>
        {
            var message = await channel.SendMessageAsync(chunk, options: WorldAutonomyDeliveryReceipt.SendOptions(token),
                messageReference: target.HasValue ? new MessageReference(target.Value) : null).ConfigureAwait(false);
            return new WorldAutonomyDeliveredMessage(message.Id, channelId);
        }, cancellationToken);
    }

    internal static async Task<WorldAutonomyDeliveryReceipt> SendChunksAsync(string content, ulong? replyTargetMessageId,
        Func<string, ulong?, CancellationToken, Task<WorldAutonomyDeliveredMessage>> send, CancellationToken cancellationToken)
    {
        var chunks = DiscordBotService.ChunkMessage(content, DiscordBotService.DiscordMaxMessageLength);
        var delivered = new List<WorldAutonomyDeliveredMessage>(chunks.Count);
        try
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                delivered.Add(await send(chunk, delivered.Count == 0 ? replyTargetMessageId : null, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception error) when (delivered.Count > 0)
        {
            return new(delivered, string.Join("\n", chunks.Take(delivered.Count)), "partial_delivery", error.GetType().Name);
        }
        return new(delivered, content, "delivered");
    }
}

public sealed class WorldAutonomySpeechTool
{
    public const string ToolName = "speak_as_robotnik";
    public const string TerminalToolName = "finish_with_robotnik_speech";
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
        WorldAutonomyRunState run,
        bool terminalDeliveryEnabled = false)
    {
        if (!opportunity.SourceChannelId.HasValue || !opportunity.SourceAuthorId.HasValue)
        {
            throw new InvalidOperationException("Robotnik speech requires a source Discord channel and author.");
        }

        var bound = new BoundSpeech(this, opportunity, context, run);
        return AIFunctionFactory.Create(
            bound.SpeakAsync,
            name: terminalDeliveryEnabled ? TerminalToolName : ToolName,
            description: (terminalDeliveryEnabled
                ? "Deliver Robotnik's final speech in the summoning channel and finish this run. Call it alone, after every intended Discord mutation has completed. It preserves reply, reaction, transcript, and run attribution."
                : "Speak as Robotnik in the Discord channel that summoned you. This is your normal voice and preserves reply, reaction, transcript, and run attribution. Long text is split safely.") +
                " This tool is for speech, not drawings. Use create_visual for all drawings; never substitute ASCII or text art. You may decline to draw in plain text.");
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
        if (ImageReplyPolicy.IsTextArtSubstitute(opportunity.VisualIntent, content))
        {
            _telemetry.Emit(new TelemetryEvent(
                Timestamp: _timeProvider.GetUtcNow(),
                EventType: TelemetryEventTypes.WorldAutonomySpeech,
                Kind: opportunity.IsDirectAddress ? "direct" : "ambient",
                Outcome: "refused",
                MessageId: ParseMessageId(opportunity.SourceMessageId),
                OperationId: context.RunId,
                Reason: "text_art_substitute"));
            throw new ArgumentException(ImageReplyPolicy.TextArtRefusal, nameof(content));
        }

        var channelId = opportunity.SourceChannelId!.Value;
        var replyTarget = ParseMessageId(replyToMessageId) ??
            (opportunity.IsDirectAddress ? ParseMessageId(opportunity.SourceMessageId) : null);
        var receipt = await run.DispatchHostDeliveryAsync("speech", channelId, replyTarget,
            () => _transport.SendAsync(opportunity.GuildId, channelId, content.Trim(), replyTarget, cancellationToken),
            _logger, cancellationToken).ConfigureAwait(false);
        var delivered = receipt.Messages;
        if (delivered.Count == 0)
        {
            return new(receipt.Outcome, channelId.ToString(CultureInfo.InvariantCulture), [], replyTarget?.ToString(CultureInfo.InvariantCulture));
        }

        receipt.Register(_sentMessages, _botOptions.DefaultPersona, "world_autonomy", ParseMessageId(opportunity.SourceMessageId), context.RunId, replyTarget);

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
            Reply: receipt.ConfirmedText,
            TranscriptSchemaVersion: FileBackedTranscriptSink.CurrentSchemaVersion,
            EpisodeId: context.RunId,
            TriggerMessageId: ParseMessageId(opportunity.SourceMessageId),
            ReplyTargetMessageId: replyTarget,
            Outcome: receipt.Outcome,
            ModelInvoked: true));
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: now,
            EventType: TelemetryEventTypes.WorldAutonomySpeech,
            UserHash: UserIdHash.Hash(opportunity.SourceAuthorId.Value),
            Channel: opportunity.SourceChannelName,
            Kind: opportunity.IsDirectAddress ? "direct" : "ambient",
            Outcome: receipt.Outcome,
            Reason: receipt.Failure,
            Count: delivered.Count,
            MessageId: delivered[0].MessageId,
            OperationId: context.RunId,
            EpisodeId: opportunity.SourceEpisodeId,
            CharacterCount: receipt.ConfirmedText.Length));

        return new WorldAutonomySpeechResult(
            receipt.Outcome,
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