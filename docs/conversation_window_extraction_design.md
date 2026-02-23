# Conversation Window Memory Extraction — Design Proposal

This document proposes replacing per-message memory extraction with a conversation-window approach. The current system extracts memories from each message in isolation, missing knowledge that emerges across multiple messages in a conversation thread.

---

## 1. Problem Statement

Our current `RunPassiveMemoryExtractionAsync` fires on every individual message. The extraction LLM receives:

```
User (Alice): yeah I'm moving there next month
```

It has zero conversational context. It doesn't know what "there" refers to, who Alice was talking to, or what preceded her statement. Consider a real Discord exchange:

> **Bob**: Where did you end up deciding to move?  
> **Alice**: Austin!  
> **Bob**: Nice, when?  
> **Alice**: Next month actually  
> **Charlie**: Oh cool, my sister lives there  

Per-message extraction processes 5 independent LLM calls. It might extract "Alice said Austin" from message 2, but it cannot derive "Alice is moving to Austin next month" because that fact is split across messages 2 and 4. It also misses "Charlie has a sister who lives in Austin" because message 5 alone only says "my sister lives there."

### Impact

- **Pronoun/reference resolution failure**: "there", "that", "it", "she" are meaningless without prior messages.
- **Multi-message facts missed**: Facts that span two or more turns are invisible.
- **Multi-user relationship facts missed**: "Alice and Bob are planning to meet up in Austin" requires seeing both sides of the exchange.
- **Wasted API cost**: 5 separate LLM calls instead of 1, most of which extract nothing because single messages are often too thin.
- **Context-free false positives**: The LLM might save "User said 'Austin'" as a memory rather than the richer "Alice is moving to Austin next month."

---

## 2. Research: How Others Solve This

### 2.1 LangMem — ReflectionExecutor with Debounce

LangMem's recommended production pattern is **delayed batch processing**. Their `ReflectionExecutor`:

1. Queues messages as they arrive.
2. Resets a configurable debounce timer (e.g. 30–60 minutes) on each new message.
3. When the conversation goes quiet and the timer fires, sends the **entire accumulated conversation** to the extraction LLM in one call.
4. If new messages arrive before the timer fires, **cancels the pending job and reschedules** with the updated message list.

From the [LangMem delayed processing guide](https://langchain-ai.github.io/langmem/guides/delayed_processing/):

> Processing memories on every message has drawbacks: Redundant work when messages arrive in quick succession, incomplete context when processing mid-conversation, unnecessary token consumption.

**Key insight**: They explicitly identify per-message extraction as a known anti-pattern. The fix is buffering + debounce.

### 2.2 Mem0 — Full Conversation Passthrough

Mem0's `add()` method accepts a **list of messages** representing a complete conversation. It concatenates all messages via `parse_messages(messages)` and sends the full text to the fact extraction LLM. The LLM sees the entire back-and-forth and can reason about cross-message facts.

Mem0 also supports **multi-actor conversations** through its `actor_id` metadata — each extracted fact is tagged with the actor it pertains to. This is critical for Discord where multiple people participate in the same thread.

Their extraction→dedup flow:
1. Send full conversation to LLM → get list of `new_retrieved_facts`
2. For each fact, embed and search existing memories for similar ones
3. Send `(new_facts, similar_existing_memories)` to a second LLM call that decides ADD / UPDATE / DELETE / NONE for each fact

### 2.3 Zep/Graphiti — Episodic Ingestion

Zep ingests data as **"episodes"** — a full conversation thread, a JSON business event, or a document. Each episode goes through entity extraction, relationship extraction, and fact extraction. Entities and facts are stored in a temporal knowledge graph with `valid_at` / `invalid_at` timestamps. This handles fact evolution (e.g., "User prefers Adidas" becomes invalidated when "User's Adidas broke, switching to Puma").

Their key architectural insight: the unit of ingestion is not a single message but a **coherent batch of related interactions** — an episode.

### 2.4 ChatGPT — Full-Session Extraction

ChatGPT's "Reference chat history" feature operates at the **conversation session level**, not per-message. The model extracts facts from the full chat thread. Their "saved memories" are also created from full conversation context — the model always has the complete chat history when deciding what to remember.

### 2.5 Common Pattern

All four systems agree: **the unit of memory extraction should be a conversation, not a message.** The only question is how to define "conversation" boundaries.

---

## 3. Proposed Design: Conversation Window with Debounce

### 3.1 Core Concept

Replace per-message extraction with a **per-channel sliding window** that accumulates messages and processes them as a batch after a period of conversational inactivity.

```
Message arrives → Buffer it → Reset debounce timer
                                    ↓ (timer fires after N minutes of silence)
                              Extract from full window
                                    ↓
                              Attribute facts to each user
                                    ↓
                              Apply memory operations per-user
                                    ↓
                              Clear buffer
```

### 3.2 Message Buffer

A `ConcurrentDictionary<ulong, ChannelMessageBuffer>` keyed by channel ID, where each buffer holds:

```csharp
internal sealed class ChannelMessageBuffer
{
    public List<BufferedMessage> Messages { get; } = new();
    public Timer? DebounceTimer { get; set; }
    public DateTimeOffset FirstMessageAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
}

internal record BufferedMessage(
    ulong AuthorId,
    string AuthorDisplayName,
    string Content,
    DateTimeOffset Timestamp);
```

### 3.3 Debounce Mechanics

When a message arrives in a channel:

1. Add it to the channel's buffer.
2. Reset the debounce timer to `ConversationWindowTimeout` (default: 3 minutes).
3. If the buffer exceeds `MaxWindowMessages` (default: 30), fire extraction immediately and reset.
4. If the buffer exceeds `MaxWindowDuration` (default: 30 minutes wall-clock since the first buffered message), fire extraction immediately regardless of ongoing activity.

The debounce timer fires `ProcessConversationWindowAsync(channelId)`.

### 3.4 Multi-User Extraction

The extraction prompt must handle **multiple participants** and attribute facts to the correct users. The prompt sees the full conversation formatted as:

```
You observed the following conversation in a Discord channel:

[12:01:23] Bob (bob_123): Where did you end up deciding to move?
[12:01:45] Alice (alice_456): Austin!
[12:02:01] Bob (bob_123): Nice, when?
[12:02:15] Alice (alice_456): Next month actually
[12:03:02] Charlie (charlie_789): Oh cool, my sister lives there

---

Extract facts worth remembering about EACH user who participated.
For each fact, specify which user it belongs to by their ID.
```

The `update_user_memory` tool schema gains a `user_id` parameter:

```json
{
  "name": "update_user_memory",
  "parameters": {
    "user_id": "The Discord user ID this memory belongs to (e.g. 'alice_456')",
    "action": "save | update | forget",
    "content": "The fact to remember",
    "context": "Why this is worth remembering",
    "memory_index": "Index of existing memory to update/forget"
  }
}
```

This means a single extraction call can produce memories for **multiple different users** — e.g.:

- `{user_id: "alice_456", action: "save", content: "Alice is moving to Austin next month"}`
- `{user_id: "charlie_789", action: "save", content: "Charlie has a sister who lives in Austin"}`
- `{user_id: "bob_123", action: "update", memory_index: 2, content: "Bob and Alice are friends who keep in touch about life events"}`

### 3.5 Handling Many Memories per Conversation

Active Discord channels can produce conversations with dozens of extractable facts. The system must handle this gracefully:

**Prompt-level guidance:**
- Instruct the LLM to focus on *durable, reusable* facts — things that will matter in future conversations — not ephemeral chatter.
- Set a soft ceiling in the prompt: "Extract at most 5 facts per user from this conversation. Focus on the most significant."
- Emphasize consolidation: "If multiple messages reveal related information, combine them into a single consolidated fact rather than saving each one separately."

**System-level guardrails:**
- `MaxMemoriesPerExtraction` (default: 15) — hard cap on tool calls per extraction. The LLM's `MaxOutputTokens` also serves as a natural bound.
- The existing `MaxMemoriesPerUser` (default: 50) cap still applies.
- The existing Jaccard dedup check catches near-duplicates even if the LLM produces them.

**Multi-user memory loading:**
- Before extraction, load existing memories for **all users who appeared in the window** (not just one). Pass each user's memories into the prompt so the LLM can check for redundancy and decide to update vs. save.
- Format as:

```
Existing memories for Alice (alice_456):
  [0] Alice lives in Portland
  [1] Alice works as a graphic designer

Existing memories for Bob (bob_123):
  [0] Bob is a software engineer
  [1] Bob lives in Denver

No existing memories for Charlie (charlie_789).
```

### 3.6 Configuration

New settings in `BotOptions`:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `ConversationWindowTimeout` | `TimeSpan` | `00:03:00` | Debounce delay — how long to wait after the last message before processing the window |
| `MaxWindowMessages` | `int` | `30` | Maximum messages to buffer before forcing extraction |
| `MaxWindowDuration` | `TimeSpan` | `00:30:00` | Maximum wall-clock time for a window before forcing extraction |
| `MaxMemoriesPerExtraction` | `int` | `15` | Hard cap on memory operations per extraction call |

Existing settings that continue to apply:
- `EnableUserMemory` — master toggle
- `MemoryExtractionRate` — probabilistic gating (applied per-window, not per-message)
- `MemoryExtractionModel` — which model to use
- `MaxMemoriesPerUser` — per-user cap

### 3.7 Orchestrator Changes

`CreativeOrchestrator` gets a new method:

```csharp
internal async Task<List<MultiUserMemoryOperation>> ExtractMemoriesFromConversationAsync(
    IReadOnlyList<BufferedMessage> conversation,
    Dictionary<ulong, IReadOnlyList<UserMemory>> existingMemoriesByUser,
    CancellationToken cancellationToken)
```

Returns a new type:

```csharp
internal record MultiUserMemoryOperation(
    ulong UserId,
    MemoryAction Action,
    string? Content,
    string? Context,
    int? MemoryIndex);
```

The prompt builder becomes `BuildConversationExtractionPrompt(conversation, existingMemoriesByUser)` — a static, testable method that formats the conversation and per-user existing memories.

### 3.8 ApplyMemoryOperations Changes

`ApplyMemoryOperationsAsync` needs to handle operations targeting **different users**:

```csharp
private async Task ApplyMultiUserMemoryOperationsAsync(
    List<MultiUserMemoryOperation> operations)
{
    // Group by user for dedup-efficient processing
    foreach (var userGroup in operations.GroupBy(o => o.UserId))
    {
        var userId = userGroup.Key;
        var userOps = userGroup.Select(o => new MemoryOperation(...)).ToList();
        await ApplyMemoryOperationsAsync(userId, userOps);
    }
}
```

---

## 4. Edge Cases and Design Decisions

### 4.1 Very Quiet Channels

If a channel gets only 1–2 messages per hour, the debounce timer correctly fires after each lull. A window with a single message degrades gracefully to the current per-message behavior — the LLM sees the one message and extracts from it.

### 4.2 Very Active Channels

Extremely active channels with sustained high-volume chat hit the `MaxWindowMessages` (30) cap and process immediately. This prevents unbounded memory usage in the buffer and ensures extraction doesn't wait indefinitely.

The `MaxWindowDuration` (30 min) cap handles channels with slow but steady trickle — never going quiet long enough for the debounce to fire but accumulating too much context.

### 4.3 Bot Restarts

Buffers are in-memory only. On restart, any pending buffers are lost. This is acceptable because:
- The window is short (3-minute debounce, 30-minute max)
- Lost extraction is a minor quality reduction, not a correctness issue
- Persisting buffers adds complexity with low ROI

### 4.4 Cross-Channel Conversations

Users might continue a topic across channels. This design does not attempt to link cross-channel conversations — each channel's window is independent. Cross-channel knowledge builds up naturally over time as the same user's messages are extracted from different channels. The per-user memory store provides the continuity.

### 4.5 Users Talking Past Each Other

In a busy channel, multiple independent conversations may overlap. The LLM must handle this — the prompt instructs it to extract facts per-user and attribute them correctly. An exchange like:

> **Alice**: I just adopted a cat!  
> **Bob**: Anyone watching the game tonight?  
> **Alice**: Named her Luna  
> **Charlie**: Yeah I've got it on, Celtics are up  

Should produce:
- Alice → "Alice adopted a cat named Luna"
- Charlie → "Charlie watches basketball / follows the Celtics"

And should NOT produce "Alice watches basketball" or "Bob adopted a cat."

### 4.6 The Same User's Rapid-Fire Messages

When one person sends 5 messages in a row (common on Discord — splitting thoughts across messages):

> **Dave**: oh man  
> **Dave**: I just got the job offer  
> **Dave**: that startup I interviewed at last week  
> **Dave**: they want me to start in march  
> **Dave**: 120k plus equity  

Per-message extraction might save "Dave got a job offer" from message 2 and miss the rest. Window extraction sees all 5 and extracts: "Dave received a job offer from a startup — 120k plus equity, starting in March." One consolidated, high-quality memory.

### 4.7 Multiple People Revealing Facts About Each Other

Discord conversations often contain third-party facts:

> **Eve**: Hey did you guys hear? Frank got engaged!  
> **Grace**: No way! To that girl he met at the concert?  
> **Eve**: Yeah, Heather! They've been together like 2 years  

The extraction prompt should handle this by saving facts about the person they pertain to:
- Frank → "Frank got engaged to Heather (they've been together ~2 years)"
- The fact is *about* Frank/Heather but *reported by* Eve

**Important design consideration**: The `user_id` in the tool call refers to the user being talked *about*, and the `context` field can note who reported it. For users the bot has never directly interacted with (e.g., Heather who may not be in the server), memories are only saved for known Discord users in the conversation.

### 4.8 Privacy — Opting Out

The existing `!sky forget-me` command wipes all memories. Users in the conversation window who have previously used `!sky forget-me` should be excluded from extraction. The bot should check an opt-out list before saving memories for any user in the window.

---

## 5. Token Economics

### Current (Per-Message)

For a 5-message conversation:
- **5 extraction calls** × (~300 prompt tokens + 100 message tokens + 300 max output) = ~3,500 tokens
- Most calls return nothing (single messages often contain nothing memorable)

### Proposed (Window)

For the same 5-message conversation:
- **1 extraction call** × (~500 prompt tokens + 500 message tokens + 500 max output) = ~1,500 tokens
- The single call has much richer context and higher extraction quality

**Estimated savings: 50–70% fewer tokens and API calls for equivalent or better extraction quality.** The savings increase with conversation length — a 20-message exchange goes from 20 calls to 1.

---

## 6. Implementation Plan

### Phase 1: Buffer and Debounce Infrastructure

1. Add `ChannelMessageBuffer`, `BufferedMessage` types.
2. Add `ConcurrentDictionary<ulong, ChannelMessageBuffer>` to `DiscordBotService`.
3. Replace `RunPassiveMemoryExtractionAsync(message)` with `BufferMessageForExtractionAsync(message)`.
4. Implement debounce timer management (reset on new message, fire on timeout/cap).
5. Add `ConversationWindowTimeout`, `MaxWindowMessages`, `MaxWindowDuration` to `BotOptions` and `appsettings.json`.
6. Write tests for buffer mechanics (debounce reset, cap triggers, timer disposal).

### Phase 2: Multi-User Extraction Prompt

1. Add `MultiUserMemoryOperation` type.
2. Add `BuildConversationExtractionPrompt(conversation, existingMemoriesByUser)` to `CreativeOrchestrator` (static, testable).
3. Add `ExtractMemoriesFromConversationAsync(...)` method.
4. Update `update_user_memory` tool schema to include `user_id` parameter.
5. Update `ParseMemoryOperations` to handle multi-user tool calls.
6. Write tests for prompt formatting (multi-user, existing memories, edge cases).

### Phase 3: Integration and Wiring

1. Wire `ProcessConversationWindowAsync` to call the new orchestrator methods.
2. Implement `ApplyMultiUserMemoryOperationsAsync` with per-user grouping.
3. Pre-load memories for all participants before extraction.
4. Apply dedup and safety filter per-user.
5. Remove old `RunPassiveMemoryExtractionAsync` and `ExtractMemoriesFromObservationAsync`.
6. End-to-end integration tests.

### Phase 4: Cleanup and Tuning

1. Remove `BuildObservationExtractionPrompt` and associated dead code.
2. Update `per_user_memory_proposal.md` to reflect the new architecture.
3. Monitor in production: extraction quality, token usage, memory creation rate.
4. Tune debounce duration and window caps based on real conversation patterns.

---

## 7. Open Questions

1. **Debounce duration**: 3 minutes is a guess. Discord conversations can have longer pauses (someone goes AFK, comes back 10 minutes later). Should we use a longer debounce (5 minutes) at the cost of delayed memory availability?

2. **Per-message fallback**: Should we keep a lightweight per-message extraction for obviously self-contained facts (e.g., "I'm from Canada") alongside the windowed extraction? This adds complexity but catches facts faster. The current recommendation is to NOT do this — the 3-minute delay is acceptable and the simplicity of a single extraction path is worth it.

3. **Third-party facts**: When Alice says "Bob just got a new dog," should the bot save a memory for Bob even though Bob didn't say it himself? This could be inaccurate (Alice might be joking or wrong). Current recommendation: yes, but tag the memory's `context` with "reported by Alice" so the LLM can weigh reliability.

4. **Extraction rate limiting**: With window extraction, `MemoryExtractionRate` (currently 1.0 / 100%) is applied per-window rather than per-message. Since each call does more work, we might want to lower this for cost control. Or, since we're already saving 50–70% on tokens, keep it at 1.0.

5. **Channel buffer memory limits**: In a server with 50 active channels, we could have 50 buffers × 30 messages = 1500 buffered messages. Each is small (a string + metadata), so this is negligible. But should we add a global cap across all channels?
