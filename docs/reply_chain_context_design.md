# Reply Chain Context Design (Discord Sky)

## Summary

When someone replies directly to a bot message, the bot should recognize this as an explicit engagement and respond with full awareness of the conversation thread. This creates more entertaining back-and-forth exchanges where the bot can riff on its own previous chaos, double down on absurd statements, or escalate running jokes.

## Goals

- Detect when a user replies to the bot's own message and treat it as a direct conversation continuation.
- Provide the model with the full reply chain (up to 3-4 messages) so it can reference and build upon previous exchanges.
- Make replies feel like a continuation of the bit, not a fresh start.
- Maintain the chaotic, fun persona even when directly engaged.

## Non-Goals

- Building a "helpful assistant" conversational experience.
- Implementing thread-based memory or persistent conversation state.
- Guaranteeing coherent multi-turn dialogue (chaos is the feature, not a bug).

## Current Behavior

1. User A sends a message, bot (as persona) responds with message B.
2. User A replies to message B using Discord's reply feature.
3. Bot receives User A's reply but:
   - Does NOT know it's a reply to its own message
   - Does NOT have message B in its context (bot messages are filtered out)
   - Treats this like any random channel message
   - May or may not respond (subject to ambient reply chance roll)

## Proposed Behavior

1. User A sends a message, bot responds with message B.
2. User A replies to message B.
3. Bot detects this is a reply to its own message and:
   - **Always** responds (deterministic trigger, no random chance)
   - Includes message B and the full reply chain in context
   - Knows "this human is talking back to me specifically"
   - Can reference, contradict, or escalate its previous statement

## Design

### New Invocation Kind

Add `DirectReply` to the existing enum:

```csharp
public enum CreativeInvocationKind
{
    Command,      // User explicitly invoked with !sky prefix
    Ambient,      // Random chance trigger on normal message
    DirectReply   // User replied to a bot message
}
```

**Behavior differences:**

| Aspect | Command | Ambient | DirectReply |
|--------|---------|---------|-------------|
| Trigger | Deterministic (prefix match) | Probabilistic (AmbientReplyChance) | Deterministic (reply detected) |
| Empty response handling | Show placeholder | Suppress silently | Show placeholder |
| Context priority | Topic > History | History only | Reply chain > History |
| Persona | Extracted from command | Default persona | **Same persona as original message** (tracked in app state) |

### Reply Chain Collection

When a `DirectReply` is detected, fetch the reply chain by walking `ReferencedMessage` backwards:

```
User A: "What's your favorite food?"
Bot (Message B): "PINGAS! I mean... chili dogs. Obviously."
User A (replies to B): "That doesn't make sense"
Bot (replies to User A): "Nothing I say makes sense. That's the POINT, you fool!"
```

The chain passed to the model would include:
1. User A's original message (if within chain depth)
2. Bot's message B (the one being replied to)
3. User A's reply (the triggering message)

**Chain depth limit:** 40 messages (configurable via `BotOptions.ReplyChainDepth`)

### Data Model Changes

#### ChannelMessage (extend existing)

```csharp
public sealed record ChannelMessage
{
    public ulong MessageId { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public bool IsBot { get; init; }
    public IReadOnlyList<ChannelImage> Images { get; init; } = Array.Empty<ChannelImage>();
    
    // New fields
    public ulong? ReferencedMessageId { get; init; }  // What this message is replying to
    public bool IsFromThisBot { get; init; }          // Specifically from our bot (vs other bots)
}
```

#### CreativeRequest (extend existing)

```csharp
public sealed record CreativeRequest(
    string Persona,
    string? Topic,
    string UserDisplayName,
    ulong UserId,
    ulong ChannelId,
    ulong? GuildId,
    DateTimeOffset Timestamp,
    CreativeInvocationKind InvocationKind = CreativeInvocationKind.Command,
    
    // New fields
    IReadOnlyList<ChannelMessage>? ReplyChain = null,  // The chain being replied to
    bool IsInThread = false                             // Whether this is in a Discord thread
);
```

### ContextAggregator Changes

#### Include Bot Messages in History

Currently bot messages are filtered out ([ContextAggregator.cs#L97-99](src/DiscordSky.Bot/Orchestration/ContextAggregator.cs#L97-99)). Change this to:

```csharp
// Old: Skip all bot messages
if (message.Author.IsBot)
{
    continue;
}

// New: Include this bot's messages, skip other bots
if (message.Author.IsBot && message.Author.Id != _botUserId)
{
    continue;
}
```

This lets the model see its own previous chaos in the channel history, enabling callbacks and running gags.

#### New Method: GatherReplyChainAsync

```csharp
public async Task<IReadOnlyList<ChannelMessage>> GatherReplyChainAsync(
    IMessage triggerMessage,
    int maxDepth = 40,
    CancellationToken cancellationToken = default)
{
    var chain = new List<ChannelMessage>();
    var current = triggerMessage;
    
    while (current != null && chain.Count < maxDepth)
    {
        chain.Add(ToChannelMessage(current));
        
        if (current.Reference?.MessageId.IsSpecified != true)
            break;
            
        current = await FetchMessageAsync(current.Channel, current.Reference.MessageId.Value);
    }
    
    chain.Reverse(); // Oldest first
    return chain;
}
```

### DiscordBotService Changes

#### Detection in OnMessageReceivedAsync

```csharp
private async Task OnMessageReceivedAsync(SocketMessage rawMessage)
{
    // ... existing validation ...
    
    // NEW: Check if this is a reply to the bot
    if (message.Reference != null && message.ReferencedMessage != null)
    {
        if (message.ReferencedMessage.Author.Id == _client.CurrentUser.Id)
        {
            _logger.LogDebug(
                "Direct reply detected: {UserId} replied to bot message {BotMessageId}",
                message.Author.Id,
                message.ReferencedMessage.Id);
            
            await HandlePersonaAsync(context, content, message, CreativeInvocationKind.DirectReply);
            return;
        }
    }
    
    // ... existing command prefix and ambient reply handling ...
}
```

### CreativeOrchestrator Changes

#### System Instructions for DirectReply

When `InvocationKind == DirectReply`, modify the system prompt:

```csharp
private static string BuildSystemInstructions(string persona, bool hasTopic, CreativeInvocationKind kind, IReadOnlyList<ChannelMessage>? replyChain)
{
    var builder = new StringBuilder();
    builder.Append($"You are roleplaying as {persona}. Stay fully in character...");
    
    if (kind == CreativeInvocationKind.DirectReply && replyChain?.Count > 0)
    {
        builder.Append(" Someone is replying directly to something you said earlier.");
        builder.Append(" This is your chance to double down, contradict yourself, escalate the bit, or go completely off the rails.");
        builder.Append(" Reference your previous statement if it's funny to do so.");
        builder.Append(" The reply chain is provided in the conversation history—your messages are marked as bot=true.");
    }
    
    // ... rest of instructions ...
}
```

#### User Content: Reply Chain Formatting

Format the reply chain prominently in the user content:

```
=== REPLY CHAIN (you're being talked back to) ===
[3 min ago] You (bot): "PINGAS! I mean... chili dogs. Obviously."
[1 min ago] ChaosUser42 (replying to you): "That doesn't make sense"
[now] ChaosUser42 is awaiting your response.
=================================================

Recent Discord messages follow...
```

#### Default Reply Target

For `DirectReply`, the bot should reply to the **user's message** (not its own previous message), creating a natural thread:

```csharp
if (request.InvocationKind == CreativeInvocationKind.DirectReply)
{
    // Default to replying to the user's message that triggered us
    // unless the model explicitly chooses differently
    defaultReplyTarget = request.TriggerMessageId;
}
```

### Thread Context

If the conversation is happening in a Discord thread (vs a regular channel), note this in the context:

```csharp
var isThread = commandContext.Channel is IThreadChannel;
```

Include in system instructions:
```
"This conversation is happening in a Discord thread, so the context is more focused. Feel free to be extra chaotic since you have a captive audience."
```

### Configuration

Add to `BotOptions`:

```csharp
public sealed class BotOptions
{
    // ... existing ...
    
    /// <summary>
    /// Maximum depth of reply chain to fetch when someone replies to the bot.
    /// </summary>
    public int ReplyChainDepth { get; init; } = 40;
    
    /// <summary>
    /// Whether to include this bot's own messages in channel history.
    /// Enables callbacks and running gags.
    /// </summary>
    public bool IncludeOwnMessagesInHistory { get; init; } = true;
}
```

## Chaos-Enhancing Considerations

Since the bot is meant to be **fun and chaotic**, not useful:

1. **Contradiction is comedy**: The model should feel free to contradict its previous statements. "I said chili dogs? I HATE chili dogs. Always have."

2. **Escalation over resolution**: If a user challenges something the bot said, it should escalate the absurdity rather than explain or backtrack.

3. **Memory as a liability**: The bot "remembering" what it said opens up opportunities for:
   - Pretending it never said that
   - Claiming the user misheard
   - Doubling down with even wilder claims
   - Breaking the fourth wall ("That was a different me. We rotate shifts.")

4. **Thread awareness**: Being in a thread means less chance of derailing other conversations, so the bot can go even harder.

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| Reply to deleted bot message | `ReferencedMessage` will be null; fall back to ambient reply behavior |
| Reply chain deeper than 40 messages | Truncate at `ReplyChainDepth` (default 40), oldest messages dropped |
| Reply to bot in non-allowed channel | Existing channel allowlist still applies; ignore |
| Reply contains ban words | Existing ban word filter applies; ignore |
| Nested reply (User → Bot → User → Bot → User) | Fetch full chain up to depth; all context preserved |
| Multiple users in reply chain | Include all; model can address multiple people |
| Bot replied with empty/placeholder | Include in chain; model can riff on the silence |

## Design Decisions

1. **Persona persistence**: When User A triggers the bot with `!sky(Robotnik)`, the bot responds as Robotnik. If User A then replies to that message, the bot **stays as Robotnik**. This requires tracking which persona sent which message, stored in application configuration/state.

2. **Rate limiting for DirectReply**: Direct replies are **subject to the same `MaxPromptsPerHour` rate limit** as other invocations. No bypass—this prevents users from exploiting direct replies to spam the bot.

3. **Typing indicator**: `DirectReply` invocations **show the typing indicator**, same as `Command` invocations. This signals to the user that the bot is processing their reply.

4. **Reply chain depth limit**: Reply chains are limited to the **40 most recent messages**. If a user keeps replying to bot responses indefinitely, only the 40 most recent messages in the chain are considered. This prevents runaway context while still allowing extended chaotic exchanges.

5. **Cross-channel isolation**: Cross-channel replies are **not handled**. Channels are completely separate contexts. If a bot message is quoted or forwarded to another channel and someone replies there, it is not treated as a DirectReply.

## Implementation Order

1. **Phase 1: Core Detection**
   - Add `DirectReply` to `CreativeInvocationKind`
   - Detect replies in `DiscordBotService`
   - Basic happy path: bot responds to direct replies

2. **Phase 2: Reply Chain**
   - Implement `GatherReplyChainAsync` in `ContextAggregator`
   - Update `CreativeRequest` with chain data
   - Format chain in user content

3. **Phase 3: Enhanced Context**
   - Include bot's own messages in history
   - Add thread detection
   - Update system instructions for DirectReply

4. **Phase 4: Polish**
   - Configuration options
   - Logging and observability
   - Unit tests for new paths

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Token explosion from long chains | Hard cap at `ReplyChainDepth`, truncate message content |
| Model breaks character when referencing previous messages | Explicit instructions to stay in persona; test prompts |
| Users exploit DirectReply for guaranteed responses | Consider rate limiting per-user for DirectReply |
| `ReferencedMessage` not cached by Discord.NET | Implement fallback fetch via `GetMessageAsync` |
| Privacy: bot messages may contain quoted user content | Same rules as existing history; ban words still filtered |

---

## Appendix: Example Flow

```
Channel: #chaos-zone

[10:00] User42: Hey Robotnik, what's your evil plan today?
[10:00] Bot (as Robotnik): "I shall finally capture that hedgehog using my PINGAS—I mean, my Egg-O-Matic! SnooPING AS usual, I see!"

[10:02] User42 replies to Bot: "Did you just say pingas?"

--- DirectReply triggered ---

Context sent to model:
- Reply chain: [Bot's message at 10:00, User42's reply at 10:02]
- System: "Someone is replying directly to something you said..."
- History: Recent channel messages (including bot's own messages now)

[10:02] Bot (as Robotnik) replies to User42: "PINGAS? I said GENIUS! My GENIUS plan! Your inferior human ears cannot comprehend the brilliance of—PROMOTION! I said PROMOTION!"
```
