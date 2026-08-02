using System.ComponentModel;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Reception;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public interface IWorldAutonomyVisualTransport
{
    Task<WorldAutonomyDeliveredMessage> SendAsync(
        ulong guildId,
        ulong channelId,
        byte[] imageBytes,
        string fileName,
        string caption,
        ulong? replyTargetMessageId,
        CancellationToken cancellationToken);
}

public sealed class DiscordWorldAutonomyVisualTransport(DiscordSocketClient client)
    : IWorldAutonomyVisualTransport
{
    public async Task<WorldAutonomyDeliveredMessage> SendAsync(
        ulong guildId,
        ulong channelId,
        byte[] imageBytes,
        string fileName,
        string caption,
        ulong? replyTargetMessageId,
        CancellationToken cancellationToken)
    {
        if (client.GetChannel(channelId) is not ISocketMessageChannel channel
            || channel is not SocketGuildChannel guildChannel
            || guildChannel.Guild.Id != guildId)
        {
            throw new InvalidOperationException(
                $"Robotnik cannot find image channel '{channelId}' in guild '{guildId}'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(imageBytes, writable: false);
        var reference = replyTargetMessageId.HasValue
            ? new MessageReference(replyTargetMessageId.Value)
            : null;
        var message = await channel.SendFileAsync(
            stream,
            fileName,
            text: caption,
            messageReference: reference).ConfigureAwait(false);
        return new WorldAutonomyDeliveredMessage(message.Id, channelId);
    }
}

public sealed class WorldAutonomyVisualTool
{
    private const string GeneratedBitmap = "generated_bitmap";
    private const string TextArt = "text_art";
    private const int DiscordMaxCaptionLength = 2000;

    private readonly ImageToolService _imageToolService;
    private readonly IWorldAutonomyVisualTransport _visualTransport;
    private readonly WorldAutonomySpeechTool _speechTool;
    private readonly SentMessageRegistry _sentMessages;
    private readonly ITranscriptSink _transcripts;
    private readonly IRecallTelemetrySink _telemetry;
    private readonly BotOptions _botOptions;
    private readonly ILogger<WorldAutonomyVisualTool> _logger;
    private readonly TimeProvider _timeProvider;

    public WorldAutonomyVisualTool(
        ImageToolService imageToolService,
        IWorldAutonomyVisualTransport visualTransport,
        WorldAutonomySpeechTool speechTool,
        SentMessageRegistry sentMessages,
        ITranscriptSink transcripts,
        IRecallTelemetrySink telemetry,
        IOptions<BotOptions> botOptions,
        ILogger<WorldAutonomyVisualTool> logger,
        TimeProvider? timeProvider = null)
    {
        _imageToolService = imageToolService;
        _visualTransport = visualTransport;
        _speechTool = speechTool;
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
            throw new InvalidOperationException("Robotnik visual creation requires a source Discord channel and author.");
        }

        var bound = new BoundVisual(this, opportunity, context, run);
        return AIFunctionFactory.Create(
            bound.CreateAsync,
            name: "create_visual",
            description: "Choose and deliver exactly one visual medium for this petition. generated_bitmap runs the image foundry and posts an attachment; text_art posts exact ASCII/text art through Robotnik's registered voice. The tool itself delivers successful output, so do not repeat it with speak_as_robotnik.");
    }

    public void RecordNotSelected(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context)
    {
        if (opportunity.VisualIntent == VisualRequestIntent.None || !opportunity.SourceAuthorId.HasValue)
        {
            return;
        }

        _imageToolService.RecordOpportunity(
            opportunity.SourceAuthorId.Value,
            opportunity.SourceChannelName,
            ImageTier.Commissioned,
            ImageContext(opportunity, context, toolSelected: false));
        EmitVisual(opportunity, context, "none", "not_selected", null);
    }

    private async Task<WorldAutonomyVisualResult> CreateAsync(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        WorldAutonomyRunState run,
        string medium,
        string? visualPrompt,
        string? textArt,
        string? caption,
        string? replyToMessageId,
        CancellationToken cancellationToken)
    {
        var normalizedMedium = medium.Trim().ToLowerInvariant();
        if (normalizedMedium is not (GeneratedBitmap or TextArt))
        {
            throw new ArgumentException("medium must be generated_bitmap or text_art.", nameof(medium));
        }
        if (opportunity.VisualIntent == VisualRequestIntent.BitmapRequired && normalizedMedium != GeneratedBitmap)
        {
            throw new ArgumentException(
                "This petition explicitly requires generated_bitmap; text_art is not an eligible substitute.",
                nameof(medium));
        }
        if (normalizedMedium == TextArt && string.IsNullOrWhiteSpace(textArt))
        {
            throw new ArgumentException("text_art requires non-empty text_art content.", nameof(textArt));
        }
        if (normalizedMedium == GeneratedBitmap && string.IsNullOrWhiteSpace(visualPrompt))
        {
            throw new ArgumentException("generated_bitmap requires a non-empty visual_prompt.", nameof(visualPrompt));
        }
        if (normalizedMedium == GeneratedBitmap && caption?.Trim().Length > DiscordMaxCaptionLength)
        {
            throw new ArgumentException("generated_bitmap caption cannot exceed 2000 characters.", nameof(caption));
        }
        if (!run.TrySelectVisualMedium())
        {
            throw new InvalidOperationException("A visual medium was already selected for this run.");
        }

        EmitVisual(opportunity, context, normalizedMedium, "selected", null);
        if (normalizedMedium == TextArt)
        {
            _imageToolService.RecordOpportunity(
                opportunity.SourceAuthorId!.Value,
                opportunity.SourceChannelName,
                ImageTier.Commissioned,
                ImageContext(opportunity, context, toolSelected: false));
            var speech = await _speechTool.SendAsync(
                opportunity,
                context,
                run,
                textArt!,
                replyToMessageId,
                cancellationToken).ConfigureAwait(false);
            EmitVisual(opportunity, context, TextArt, "delivered", ParseMessageId(speech.MessageIds.FirstOrDefault()));
            return new WorldAutonomyVisualResult(
                speech.Outcome,
                TextArt,
                speech.ChannelId,
                speech.MessageIds,
                speech.ReplyTargetMessageId,
                null);
        }

        var outcome = await _imageToolService.GenerateAsync(
            opportunity.SourceAuthorId!.Value,
            opportunity.SourceChannelName,
            visualPrompt!,
            ImageTier.Commissioned,
            cancellationToken,
            ImageContext(opportunity, context, toolSelected: true)).ConfigureAwait(false);
        if (!outcome.Generated || outcome.Bytes is null || outcome.FileName is null)
        {
            EmitVisual(opportunity, context, GeneratedBitmap, "refused", null);
            return new WorldAutonomyVisualResult(
                "refused",
                GeneratedBitmap,
                null,
                [],
                null,
                outcome.RefusalText ?? ImageRefusals.GenericRefusal);
        }

        var channelId = opportunity.SourceChannelId!.Value;
        var replyTarget = ParseMessageId(replyToMessageId)
            ?? (opportunity.IsDirectAddress ? ParseMessageId(opportunity.SourceMessageId) : null);
        var finalCaption = string.IsNullOrWhiteSpace(caption) ? "Behold." : caption.Trim();
        var delivered = await _visualTransport.SendAsync(
            opportunity.GuildId,
            channelId,
            outcome.Bytes,
            outcome.FileName,
            finalCaption,
            replyTarget,
            cancellationToken).ConfigureAwait(false);
        _sentMessages.Register(
            delivered.MessageId,
            _botOptions.DefaultPersona,
            "world_autonomy_visual",
            ParseMessageId(opportunity.SourceMessageId),
            context.RunId,
            replyTarget);
        try
        {
            await run.RecordDiscordDeliveryAsync(
                channelId,
                [delivered.MessageId],
                replyTarget,
                finalCaption.Length).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Robotnik visual {MessageId} delivered but run {RunId} could not record delivery evidence.",
                delivered.MessageId,
                context.RunId);
        }

        var now = _timeProvider.GetUtcNow();
        _transcripts.Record(new TranscriptEntry(
            Timestamp: now,
            UserId: opportunity.SourceAuthorId.Value,
            UserDisplayName: opportunity.SourceAuthorDisplayName ?? "unknown",
            ChannelId: channelId,
            ChannelName: opportunity.SourceChannelName,
            Persona: _botOptions.DefaultPersona,
            InvocationKind: opportunity.IsDirectAddress ? "WorldAutonomyDirectVisual" : "WorldAutonomyAmbientVisual",
            Prompt: opportunity.Prompt,
            Reply: finalCaption,
            TranscriptSchemaVersion: FileBackedTranscriptSink.CurrentSchemaVersion,
            EpisodeId: context.RunId,
            TriggerMessageId: ParseMessageId(opportunity.SourceMessageId),
            ReplyTargetMessageId: replyTarget,
            Outcome: "delivered",
            ModelInvoked: true));
        EmitVisual(opportunity, context, GeneratedBitmap, "delivered", delivered.MessageId);
        return new WorldAutonomyVisualResult(
            "delivered",
            GeneratedBitmap,
            channelId.ToString(CultureInfo.InvariantCulture),
            [delivered.MessageId.ToString(CultureInfo.InvariantCulture)],
            replyTarget?.ToString(CultureInfo.InvariantCulture),
            null);
    }

    private static ImageGenerationContext ImageContext(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        bool toolSelected) => new(
            Source: "world_autonomy_visual",
            InvocationKind: opportunity.IsDirectAddress ? "direct" : "ambient",
            TriggerMessageId: ParseMessageId(opportunity.SourceMessageId),
            OpportunityId: context.RunId,
            ToolOffered: true,
            ToolSelected: toolSelected,
            GuildId: opportunity.GuildId);

    private void EmitVisual(
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        string medium,
        string outcome,
        ulong? messageId)
    {
        _telemetry.Emit(new TelemetryEvent(
            Timestamp: _timeProvider.GetUtcNow(),
            EventType: TelemetryEventTypes.WorldAutonomyVisual,
            UserHash: opportunity.SourceAuthorId.HasValue
                ? UserIdHash.Hash(opportunity.SourceAuthorId.Value)
                : null,
            Channel: opportunity.SourceChannelName,
            Kind: medium,
            Outcome: outcome,
            MessageId: messageId,
            OperationId: context.RunId,
            Reason: opportunity.VisualIntent switch
            {
                VisualRequestIntent.BitmapRequired => "bitmap_required",
                VisualRequestIntent.MediumChoice => "medium_choice",
                _ => "none",
            }));
    }

    private static ulong? ParseMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
        {
            throw new ArgumentException($"'{value}' is not a valid Discord message ID.");
        }
        return parsed;
    }

    private sealed class BoundVisual(
        WorldAutonomyVisualTool owner,
        WorldAutonomyOpportunity opportunity,
        WorldAutonomyRunContext context,
        WorldAutonomyRunState run)
    {
        public Task<WorldAutonomyVisualResult> CreateAsync(
            [Description("Exactly generated_bitmap or text_art. Explicit image/picture/photo petitions require generated_bitmap.")]
            string medium,
            [Description("Concrete image-generation prompt for generated_bitmap. Omit for text_art.")]
            string? visual_prompt = null,
            [Description("Exact ASCII/text artwork to post for text_art. Omit for generated_bitmap.")]
            string? text_art = null,
            [Description("Short in-character caption for generated_bitmap. Omit to use 'Behold.'")]
            string? caption = null,
            [Description("Optional Discord message ID to reply to. Direct petitions default to their trigger message.")]
            string? reply_to_message_id = null,
            CancellationToken cancellationToken = default) =>
            owner.CreateAsync(
                opportunity,
                context,
                run,
                medium,
                visual_prompt,
                text_art,
                caption,
                reply_to_message_id,
                cancellationToken);
    }
}

public sealed record WorldAutonomyVisualResult(
    string Outcome,
    string Medium,
    string? ChannelId,
    IReadOnlyList<string> MessageIds,
    string? ReplyTargetMessageId,
    string? RefusalText);