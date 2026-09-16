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
    Task<IWorldAutonomyVisualProgress?> BeginAsync(
        ulong guildId, ulong channelId, ulong? replyTargetMessageId, CancellationToken cancellationToken) =>
        Task.FromResult<IWorldAutonomyVisualProgress?>(null);

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
    public async Task<IWorldAutonomyVisualProgress?> BeginAsync(
        ulong guildId, ulong channelId, ulong? replyTargetMessageId, CancellationToken cancellationToken)
    {
        if (client.GetChannel(channelId) is not ISocketMessageChannel channel ||
            channel is not SocketGuildChannel guildChannel || guildChannel.Guild.Id != guildId)
            throw new InvalidOperationException("The image channel is unavailable.");
        var placeholder = await channel.SendMessageAsync(
            "Stand back. The Royal Egg Art Foundry is firing up...",
            messageReference: replyTargetMessageId.HasValue ? new MessageReference(replyTargetMessageId.Value) : null,
            options: new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
        return new DiscordWorldAutonomyVisualProgress(placeholder, channelId);
    }

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
    public const string ToolName = "create_visual";
    private const string GeneratedBitmap = "generated_bitmap";
    private const int DiscordMaxCaptionLength = 2000;

    private readonly ImageToolService _imageToolService;
    private readonly IWorldAutonomyVisualTransport _visualTransport;
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
        if (!CanBind(opportunity))
        {
            throw new InvalidOperationException("Robotnik visual creation requires a source Discord channel and author.");
        }

        var bound = new BoundVisual(this, opportunity, context, run);
        return AIFunctionFactory.Create(
            bound.CreateAsync,
            name: ToolName,
            description: $"Create or edit and deliver exactly one OpenAI image. Use this whenever you choose to draw, sketch, illustrate, or depict something, including your own visual ideas. ASCII and text art are not drawing alternatives. Current-message and same-channel reply attachments are supplied as reference pixels automatically; a mask.png attachment edits transparent areas of the first PNG reference. image_options.action is auto (use available references), edit (references required), or generate (fresh image). New images default to Flare; edits to Sunburst. Only select high/xhigh/max quality when the user asks; otherwise leave quality at medium. The tool itself delivers successful output, so do not repeat it with {(terminalDeliveryEnabled ? WorldAutonomySpeechTool.TerminalToolName : WorldAutonomySpeechTool.ToolName)}. If the tool refuses or fails, acknowledge that in character without substituting text art or claiming an image exists.");
    }

    internal static bool CanBind(WorldAutonomyOpportunity opportunity) =>
        opportunity.SourceChannelId.HasValue && opportunity.SourceAuthorId.HasValue;

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
        string visualPrompt,
        string? caption,
        string? replyToMessageId,
        ImageRenderSettings? settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(visualPrompt))
        {
            throw new ArgumentException("generated_bitmap requires a non-empty visual_prompt.", nameof(visualPrompt));
        }
        if (caption?.Trim().Length > DiscordMaxCaptionLength)
        {
            throw new ArgumentException("generated_bitmap caption cannot exceed 2000 characters.", nameof(caption));
        }
        if (!run.TrySelectVisualMedium())
        {
            throw new InvalidOperationException("An image was already attempted for this run.");
        }

        EmitVisual(opportunity, context, GeneratedBitmap, "selected", null);

        var channelId = opportunity.SourceChannelId!.Value;
        var replyTarget = ParseMessageId(replyToMessageId)
            ?? (opportunity.IsDirectAddress ? ParseMessageId(opportunity.SourceMessageId) : null);
        var finalCaption = string.IsNullOrWhiteSpace(caption) ? "Behold." : caption.Trim();
        IWorldAutonomyVisualProgress? progress = null;
        try
        {
            progress = await _visualTransport.BeginAsync(opportunity.GuildId, channelId, replyTarget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Image progress message unavailable: {ErrorType}", exception.GetType().Name);
        }
        await using var progressLifetime = progress;
        var outcome = await _imageToolService.GenerateAsync(
            opportunity.SourceAuthorId!.Value,
            opportunity.SourceChannelName,
            visualPrompt!,
            ImageTier.Commissioned,
            cancellationToken,
            ImageContext(opportunity, context, toolSelected: true),
            settings,
            progress is null ? null : progress.PreviewAsync).ConfigureAwait(false);
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

        var delivered = progress is null
            ? await _visualTransport.SendAsync(opportunity.GuildId, channelId, outcome.Bytes,
                outcome.FileName, finalCaption, replyTarget, cancellationToken).ConfigureAwait(false)
            : await progress.CompleteAsync(outcome.Bytes, outcome.FileName, finalCaption, cancellationToken).ConfigureAwait(false);
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
            run.RecordVisualDelivery();

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
            GuildId: opportunity.GuildId,
            ChannelId: opportunity.SourceChannelId);

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
            [Description("Concrete image-generation or edit prompt. Every drawing uses an OpenAI image model, never ASCII or text art.")]
            string visual_prompt,
            [Description("Short in-character image caption. Omit to use 'Behold.'")]
            string? caption = null,
            [Description("Optional Discord message ID to reply to. Direct petitions default to their trigger message.")]
            string? reply_to_message_id = null,
            [Description("Optional image settings: action auto/generate/edit; model flare/sunburst; quality low/medium/high/xhigh/max/auto; size auto or WIDTHxHEIGHT (multiples of 16, max edge 3840, 655360-8294400 pixels, aspect up to 3:1); output_format png/jpeg/webp; background opaque/transparent/auto; output_compression 0-100 (jpeg/webp); partial_images 0-3. Transparent needs png/webp. Leave unspecified settings out.")]
            ImageRenderSettings? image_options = null,
            CancellationToken cancellationToken = default) =>
            owner.CreateAsync(
                opportunity,
                context,
                run,
                visual_prompt,
                caption,
                reply_to_message_id,
                image_options,
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