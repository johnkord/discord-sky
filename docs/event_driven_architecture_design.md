# Event-Driven Architecture Design

## Summary

Decouple Discord message handling from AI orchestration using an in-process event bus, enabling background processing, better testability, and future extensibility (webhooks, scheduled tasks, multi-worker scaling).

## Current Flow

```
DiscordSocketClient.MessageReceived
        ↓ (synchronous)
DiscordBotService.OnMessageReceivedAsync
        ↓ (synchronous)
CreativeOrchestrator.ExecuteAsync
        ↓ (synchronous)
Channel.SendMessageAsync
```

**Problems:**
- Blocking Discord gateway thread during AI calls (30s+ timeout risk)
- No retry/replay capability for failed orchestrations
- Difficult to add scheduled or delayed responses
- Testing requires full Discord client setup

## Proposed Flow

```
DiscordSocketClient.MessageReceived
        ↓
DiscordBotService (thin adapter)
        ↓ publish
   ┌────────────────┐
   │  Event Channel │  (System.Threading.Channels)
   └───────┬────────┘
           ↓ consume
  OrchestratorWorker (BackgroundService)
           ↓
  CreativeOrchestrator.ExecuteAsync
           ↓ publish
   ┌────────────────┐
   │ Response Queue │
   └───────┬────────┘
           ↓ consume
  DiscordSenderWorker (BackgroundService)
           ↓
  Channel.SendMessageAsync
```

## Core Components

### 1. Event Contracts

```csharp
public sealed record MessageReceivedEvent(
    ulong MessageId,
    ulong ChannelId,
    ulong? GuildId,
    ulong AuthorId,
    string AuthorName,
    string Content,
    bool IsReplyToBot,
    ulong? ReferencedMessageId,
    IReadOnlyList<string> AttachmentUrls,
    DateTimeOffset Timestamp);

public sealed record SendResponseEvent(
    ulong ChannelId,
    string Text,
    ulong? ReplyToMessageId,
    string Mode); // "reply" | "broadcast"
```

### 2. Event Bus (Channels-based)

```csharp
public interface IEventBus
{
    ValueTask PublishAsync<T>(T @event, CancellationToken ct = default);
    IAsyncEnumerable<T> SubscribeAsync<T>(CancellationToken ct = default);
}

public sealed class InMemoryEventBus : IEventBus
{
    private readonly Channel<object> _channel = Channel.CreateBounded<object>(
        new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    public ValueTask PublishAsync<T>(T @event, CancellationToken ct) =>
        _channel.Writer.WriteAsync(@event!, ct);

    public async IAsyncEnumerable<T> SubscribeAsync<T>([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            if (item is T typed)
                yield return typed;
        }
    }
}
```

### 3. Thin Discord Adapter

```csharp
// DiscordBotService.OnMessageReceivedAsync becomes:
private async Task OnMessageReceivedAsync(SocketMessage rawMessage)
{
    if (rawMessage is not SocketUserMessage message || message.Author.IsBot)
        return;

    if (!PassesBasicFilters(message))
        return;

    var @event = new MessageReceivedEvent(
        message.Id,
        message.Channel.Id,
        (message.Channel as SocketGuildChannel)?.Guild.Id,
        message.Author.Id,
        GetDisplayName(message.Author),
        message.Content,
        IsReplyToBot(message),
        message.Reference?.MessageId.ToNullable(),
        message.Attachments.Select(a => a.Url).ToList(),
        message.Timestamp);

    await _eventBus.PublishAsync(@event);
    // Returns immediately - Discord gateway thread freed
}
```

### 4. Orchestrator Worker

```csharp
public sealed class OrchestratorWorker : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly CreativeOrchestrator _orchestrator;
    private readonly IDiscordClient _discord; // For fetching context

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var evt in _eventBus.SubscribeAsync<MessageReceivedEvent>(ct))
        {
            try
            {
                var result = await ProcessEventAsync(evt, ct);
                if (!string.IsNullOrWhiteSpace(result.PrimaryMessage))
                {
                    await _eventBus.PublishAsync(new SendResponseEvent(
                        evt.ChannelId,
                        result.PrimaryMessage,
                        result.ReplyToMessageId,
                        result.Mode ?? "broadcast"), ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process event {MessageId}", evt.MessageId);
                // Future: publish to dead-letter queue
            }
        }
    }
}
```

### 5. Discord Sender Worker

```csharp
public sealed class DiscordSenderWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var evt in _eventBus.SubscribeAsync<SendResponseEvent>(ct))
        {
            var channel = await _discord.GetChannelAsync(evt.ChannelId) as IMessageChannel;
            if (channel == null) continue;

            var reference = evt.ReplyToMessageId.HasValue
                ? new MessageReference(evt.ReplyToMessageId.Value)
                : null;

            await channel.SendMessageAsync(evt.Text, messageReference: reference);
        }
    }
}
```

## Registration

```csharp
// Program.cs
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<DiscordBotService>();      // Publisher
builder.Services.AddHostedService<OrchestratorWorker>();     // Consumer
builder.Services.AddHostedService<DiscordSenderWorker>();    // Consumer
```

## Migration Strategy

| Phase | Scope | Risk |
|-------|-------|------|
| 1 | Add event bus, keep synchronous path as fallback | None |
| 2 | Route ambient replies through event bus only | Low |
| 3 | Route all invocations through event bus | Medium |
| 4 | Remove synchronous fallback | Low |

## Future Extensions

Once the bus exists, these become trivial:

- **Scheduled chaos**: `ScheduledEventWorker` publishes synthetic `MessageReceivedEvent`s
- **Webhook notifications**: `WebhookWorker` subscribes to `SendResponseEvent` for admin alerts
- **Dead-letter queue**: Failed events go to a retry queue with exponential backoff
- **Metrics collection**: `TelemetryWorker` subscribes to all events for observability
- **Multi-instance scaling**: Swap `InMemoryEventBus` for Redis Streams or Azure Service Bus

## Trade-offs

| Pro | Con |
|-----|-----|
| Non-blocking Discord gateway | Added complexity (3 new classes) |
| Testable without Discord client | In-memory bus loses events on crash |
| Enables scheduled/delayed responses | Slight latency increase (~5-10ms) |
| Foundation for horizontal scaling | Debugging requires correlation IDs |

## Out of Scope

- Persistent message queue (Redis/RabbitMQ) – future work if multi-instance needed
- Saga orchestration for multi-step flows
- Event sourcing / replay from beginning of time
