# Per-User Memory — Design Proposal

This document proposes a per-user memory system for Discord Sky. It covers the research behind the design, the proposed architecture, integration points with the existing codebase, storage strategy, privacy considerations, and a phased implementation plan.

---

## 1. Problem Statement

Discord Sky treats every user identically. It has no awareness that it has spoken to someone before, what topics they gravitate toward, what running jokes have formed, or how a user prefers to interact. Every invocation starts cold — the only "memory" is whatever happens to be in the 20-message channel history window.

This creates a ceiling on conversational quality. A persona bot that remembers "Dave always challenges me to rap battles" or "Sarah claims to be a time traveler" can produce dramatically more engaging responses than one that rebuilds context from scratch each time.

---

## 2. Research: How Others Solve This

### 2.1 ChatGPT Memory (OpenAI Production System)

ChatGPT's memory has two tiers:

1. **Saved memories** — explicit facts the model decides to persist (`"User prefers Python over JavaScript"`). Created proactively by the model or when the user says "remember that...". Stored as short natural-language strings.  
2. **Chat history references** — implicit, evolving context drawn from conversation history. Not user-visible or editable, used by the model to rewrite search queries and personalize responses.

Key design decisions:
- The model controls what to save — no special commands needed. It calls an internal `save_memory` tool.
- Users can ask "what do you remember about me?" and the model surfaces saved memories.
- Users can ask the model to forget something, and the model deletes the corresponding memory.
- The model avoids proactively saving sensitive information (health details, financial data) unless explicitly asked.
- Memory is prioritized by recency and topic frequency — stale or rarely-referenced memories naturally decay.
- There is a size cap on saved memories per user.

**Takeaway for Discord Sky**: The model-driven extraction pattern is battle-tested at massive scale. The two-tier split (explicit saved vs implicit conversational) is elegant but the "chat history reference" tier requires persistent conversation state that Discord Sky doesn't have. The "saved memories" tier maps directly to our use case.

### 2.2 LangMem / LangGraph (Open-Source Reference)

LangChain's memory framework provides two tools:
- `create_manage_memory_tool` — the agent decides what to store. Supports `upsert` (create or update by ID) and `delete`. Each memory has `content` (the fact) and `context` (why it was stored).
- `create_search_memory_tool` — the agent searches memories when relevant, using embedding-based vector similarity against an `InMemoryStore` or `AsyncPostgresStore`.

Two integration patterns:
- **Hot path**: the agent uses the memory tools *during* the conversation. New memories are saved inline before responding. Advantage: immediate availability. Disadvantage: adds latency, increases tool call complexity.
- **Background**: a separate process extracts memories *after* the conversation ends. Advantage: no latency impact, cleaner separation. Disadvantage: new memories aren't available until the next session.

Memory schema: `{ content: string, context: string }` stored under a namespace like `("memories", user_id)`. Search uses 1536-dimensional embeddings (`text-embedding-3-small`).

**Takeaway for Discord Sky**: The hot-path pattern is the right fit — Discord Sky processes messages one at a time, so there's no "session end" to trigger background extraction. The simple `content + context` schema is good for minimal storage. However, vector search adds significant infrastructure cost (embedding API calls, vector DB). For our scale, simpler approaches suffice.

### 2.3 Semantic Kernel — Simulated Function Calls for Context Injection

Semantic Kernel recommends injecting user context via "simulated function calls" — fake tool call/result pairs inserted into the chat history:

```
Assistant: [tool_call: get_user_profile(user_id="12345")]
Tool: {"name": "Dave", "preferences": ["vegetarian", "likes puns"], "last_seen": "2 hours ago"}
```

Models treat tool results as high-trust factual context and follow them more reliably than the same content embedded in a user message. This is because models are trained to treat tool responses as authoritative external data.

**Takeaway for Discord Sky**: This is a powerful injection technique. However, Discord Sky currently uses `ChatToolMode.RequireSpecific("send_discord_message")`, which forces the model to emit exactly one specific tool call. Injecting simulated tool calls would add complexity (we'd need to insert artificial `ChatMessage` entries with `FunctionCallContent` and `FunctionResultContent`). For Phase 1, injecting memories as a clearly-delimited text block in the user content is simpler and nearly as effective. The simulated function call approach can be explored later if the model tends to ignore the text-based memories.

### 2.4 Memory Taxonomy (LangGraph Conceptual Framework)

LangGraph categorizes memory into three types borrowed from cognitive psychology:

| Type | Human Analogy | AI Application |
|---|---|---|
| **Semantic** | Facts learned in school | User preferences, biographical facts |
| **Episodic** | Specific experiences | Past interactions, conversation highlights |
| **Procedural** | Motor skills, how-to | System prompts, behavioral rules |

For Discord Sky, the primary value is in **semantic memory** (facts about users) with secondary value in **episodic memory** (memorable past interactions). Procedural memory is already handled by system instructions and persona prompts.

Two storage strategies for semantic memory:
- **Profile**: a single JSON document per user, continuously updated. Compact, easy to inject, but can lose detail during updates as the model merges new facts with existing ones.
- **Collection**: a list of individual memory documents per user. Easier for the model to create (append new facts), harder to manage (deduplication, contradictions, search).

**Takeaway for Discord Sky**: A hybrid approach works best. Keep a small, bounded list of individual memory strings per user (collection-style, not a merged profile), but render them as a flat block for injection. This avoids the merge-and-lose problem of profiles while keeping injection simple.

---

## 3. Proposed Architecture

### 3.1 High-Level Flow

```
User sends message
       │
       ▼
DiscordBotService.ProcessMessageAsync
       │
       ├── Load existing memories for this user
       │   (from IUserMemoryStore)
       │
       ├── Attach memories to CreativeRequest
       │
       ▼
CreativeOrchestrator.ExecuteAsync
       │
       ├── Inject memories into BuildUserContent 
       │   (text block before history)
       │
       ├── Send to OpenAI with two tools:
       │   • send_discord_message (required)
       │   • update_user_memory (optional)
       │
       ├── Parse response:
       │   • Extract send_discord_message → reply
       │   • Extract update_user_memory → persist
       │
       ▼
Return CreativeResult + memory updates
```

### 3.2 Tool Call Strategy

The current architecture uses `ChatToolMode.RequireSpecific("send_discord_message")` to force the model to always produce a tool call. This ensures we always get structured output. Adding a second tool requires changing this approach.

**The challenge**: `RequireSpecific` accepts a single tool name. If we add `update_user_memory`, we can't require both — and we can't require neither (the model might produce plain text instead of tool calls).

**Proposed solution — Two-pass approach**:

1. **Primary call** (unchanged): Send the request with `ChatToolMode.RequireSpecific("send_discord_message")` exactly as today. The model produces the Discord message. This preserves the existing, proven flow.

2. **Memory extraction call** (new, conditional): After the primary call succeeds, make a second, lightweight API call with a focused memory extraction prompt. This call gets the conversation context + the bot's own response and is asked: "Based on this interaction, should you save or update any memories about this user?" It uses `ChatToolMode.Auto` with only the `update_user_memory` tool available, so it can either call the tool or produce no tool call (meaning nothing worth remembering).

**Why two passes instead of one?**
- Preserves the `RequireSpecific` guarantee for `send_discord_message` — no risk of the model choosing `update_user_memory` instead of responding.
- The memory extraction call can use a cheaper/faster model (e.g., `gpt-5-mini`) since it's doing simple fact extraction, not creative persona work.
- Memory extraction doesn't block the response — the Discord message can be sent immediately, and memory extraction can happen concurrently or after.
- Failure in memory extraction doesn't affect the user-facing response.

**Cost mitigation**: The memory extraction call is cheap:
- Input: a short system prompt (~200 tokens) + the user's message + the bot's response (~200 tokens) + existing memories (~100 tokens). Total: ~500 tokens input.
- Output: either nothing (no tool call) or a short tool call (~50 tokens).
- Using `gpt-5-mini`, this costs approximately $0.00015 per invocation — negligible.
- Memory extraction can be rate-limited (e.g., only run on 1-in-3 invocations, or skip for ambient replies) to further reduce cost.

**Alternative considered — Parallel tool calls**: Some models support parallel tool calling, where the model could call both `send_discord_message` and `update_user_memory` in a single response. However, this requires `ChatToolMode.Auto` or `ChatToolMode.Required`, which removes the guarantee that `send_discord_message` is always called. The model might call only `update_user_memory`, or call neither. The two-pass approach is more robust.

### 3.3 Memory Extraction Prompt

The memory extraction call needs a focused prompt that guides what's worth remembering:

```
You are a memory manager for a Discord bot. You've just observed an interaction 
between the bot (in character as {persona}) and a user named {displayName}.

Your job: decide if anything from this interaction is worth remembering about the 
user for future conversations. Only save memories that would genuinely improve 
future interactions.

SAVE things like:
- User preferences, interests, or opinions they've expressed
- Personal facts they've shared (name, location, hobbies, pets, job)
- Running jokes, catchphrases, or recurring themes
- How the user prefers to interact (serious, playful, confrontational)
- Corrections the user has made ("actually, I'm from Canada, not Australia")

DO NOT SAVE:
- Generic conversation filler ("user said hello")
- Anything that's only relevant to this specific conversation
- Sensitive information (health conditions, financial details, passwords) 
  unless the user explicitly asks you to remember it
- Redundant facts already in the existing memories below

Existing memories for this user:
{existingMemories}

The interaction:
User ({displayName}): {userMessage}
Bot ({persona}): {botResponse}
```

### 3.4 Memory Schema

Each memory entry:

```csharp
public sealed record UserMemory(
    string Content,       // "User is a software engineer who works with C#"
    string Context,       // "Mentioned during conversation about programming"
    DateTimeOffset CreatedAt,
    DateTimeOffset LastReferencedAt,  // Updated when injected into a prompt
    int ReferenceCount    // How many times this memory has been used
);
```

- **Content**: the fact itself, as a natural-language string. Short (1-2 sentences max).
- **Context**: when/why this was captured. Useful for disambiguation and debugging.
- **CreatedAt**: when the memory was first saved.
- **LastReferencedAt**: updated each time the memory is included in a prompt. Used for staleness detection.
- **ReferenceCount**: how many times this memory has been injected. Used for prioritization.

### 3.5 Memory Injection

Memories are injected into `BuildUserContent` as a clearly-delimited text block, placed before the channel history:

```
=== WHAT YOU REMEMBER ABOUT {UserDisplayName} ===
• User is a software engineer who works with C#
• Claims to have a pet raccoon named "Gerald"  
• Enjoys being challenged to rap battles
• Corrected you last time: "It's Dave, not David"
================================================
```

This placement ensures the model reads user context before processing the conversation, anchoring its persona response with knowledge about who it's talking to.

**Token budget**: With a 10-memory cap and ~20 tokens per memory, the injection block adds ~250 tokens. This is modest relative to the ~2000+ tokens typically used for channel history.

### 3.6 Memory Lifecycle

```
                    ┌───────────────────┐
                    │   Memory Created  │
                    │   (via tool call) │
                    └────────┬──────────┘
                             │
                    ┌────────▼──────────┐
                    │    Active Memory  │◄──── Referenced in prompt
                    │  (count updated)  │      (LastReferencedAt++)
                    └────────┬──────────┘
                             │
              ┌──────────────┼──────────────┐
              │              │              │
     ┌────────▼───────┐  ┌──▼───────┐  ┌───▼──────────┐
     │   Updated by   │  │  Decays  │  │  Explicitly   │
     │   model (new   │  │  (stale, │  │  forgotten by │
     │   tool call    │  │  >30d)   │  │  user request │
     │   with same    │  │          │  │               │
     │   content)     │  │          │  │               │
     └────────────────┘  └──┬───────┘  └───────────────┘
                            │
                   ┌────────▼──────────┐
                   │   Evicted / Aged  │
                   │   Out             │
                   └───────────────────┘
```

**Eviction policy** (bounded growth):
- Hard cap: 20 memories per user.
- When at cap and a new memory arrives, evict the memory with the oldest `LastReferencedAt` (LRU-style).
- No automatic staleness eviction — memories persist indefinitely until the cap is reached, at which point the LRU memory is evicted to make room for new ones.

**Update semantics**: The `update_user_memory` tool supports three operations:
- `save` — create a new memory.
- `update` — replace an existing memory by index (for corrections: "actually it's Dave, not David").
- `forget` — delete a memory by index (for user requests: "forget that I told you about my job").

---

## 4. Storage Strategy

### 4.1 Phase 1: In-Memory (`ConcurrentDictionary`)

```csharp
private readonly ConcurrentDictionary<ulong, List<UserMemory>> _userMemories = new();
```

- **Pros**: Zero infrastructure, zero latency, trivial to implement, matches the existing `_personaCache` pattern.
- **Cons**: Lost on restart. Acceptable for proving the feature works.
- **Capacity**: At 20 memories × ~200 bytes per memory × 10,000 users = ~40 MB. Well within bounds.

### 4.2 Phase 2: File-Backed Persistence

```
data/
  user_memories/
    123456789.json    ← one file per user
    987654321.json
```

- Load on first access, write-through on mutation (debounced to avoid excessive I/O).
- Simple JSON serialization — no database dependency.
- `FileSystemWatcher` or periodic flush (every 60s) to handle crash recovery.
- **Pros**: Survives restarts, simple, no new dependencies.
- **Cons**: Not suitable for multi-instance deployments (file conflicts). Fine for single-pod K8s.

### 4.3 Phase 3: External Store (Future)

If Discord Sky scales to multi-pod deployment, migrate to:
- **Redis**: fast, supports TTL, pub/sub for cache invalidation between pods.
- **SQLite**: single-file database, no server process, good for moderate scale.
- **PostgreSQL**: if the bot grows to need a proper data tier.

The `IUserMemoryStore` abstraction (see Section 5) ensures the storage backend is swappable without touching orchestration code.

---

## 5. Integration Points

### 5.1 New Abstraction: `IUserMemoryStore`

```csharp
public interface IUserMemoryStore
{
    Task<IReadOnlyList<UserMemory>> GetMemoriesAsync(ulong userId, CancellationToken ct = default);
    Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct = default);
    Task UpdateMemoryAsync(ulong userId, int index, string content, string context, CancellationToken ct = default);
    Task ForgetMemoryAsync(ulong userId, int index, CancellationToken ct = default);
    Task ForgetAllAsync(ulong userId, CancellationToken ct = default);
}
```

Registered as a singleton in DI. Phase 1 implementation: `InMemoryUserMemoryStore`. Phase 2: `FileBackedUserMemoryStore`.

### 5.2 Changes to `CreativeModels.cs`

```csharp
// Add to CreativeRequest:
public sealed record CreativeRequest(
    // ... existing parameters ...
    IReadOnlyList<UserMemory>? UserMemories = null   // ← new
);
```

### 5.3 Changes to `DiscordBotService.cs`

In both `HandlePersonaAsync` and `HandleDirectReplyAsync`:

```csharp
// Before creating CreativeRequest:
var memories = await _memoryStore.GetMemoriesAsync(request.UserId, cancellationToken);

// Pass to CreativeRequest:
var request = new CreativeRequest(
    // ... existing fields ...
    UserMemories: memories
);

// After receiving CreativeResult:
if (result.MemoryUpdates is { Count: > 0 })
{
    foreach (var update in result.MemoryUpdates)
    {
        // Apply save/update/forget operations
    }
}
```

### 5.4 Changes to `CreativeOrchestrator.cs`

**`BuildUserContent`**: Add memory injection block (see Section 3.5).

**New tool declaration**: 

```csharp
private static readonly AIFunctionDeclaration UpdateUserMemoryTool = AIFunctionFactory.CreateDeclaration(
    name: "update_user_memory",
    description: "Save, update, or forget a fact about the user you're talking to.",
    jsonSchema: JsonDocument.Parse("""
    {
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "action": {
                "type": "string",
                "enum": ["save", "update", "forget"]
            },
            "memory_index": {
                "anyOf": [
                    { "type": "integer", "minimum": 0 },
                    { "type": "null" }
                ],
                "description": "Index of the memory to update or forget. Required for 'update' and 'forget'. Null for 'save'."
            },
            "content": {
                "type": "string",
                "description": "The fact to remember. Required for 'save' and 'update'."
            },
            "context": {
                "type": "string",
                "description": "Brief context for why this is being remembered."
            }
        },
        "required": ["action"]
    }
    """).RootElement);
```

**Memory extraction call** (new method):

```csharp
internal async Task<List<MemoryOperation>> ExtractMemoriesAsync(
    CreativeRequest request,
    string botResponse,
    CancellationToken cancellationToken)
{
    var systemPrompt = BuildMemoryExtractionPrompt(request);
    var messages = new List<ChatMessage>
    {
        new(ChatRole.User, $"User ({request.UserDisplayName}): {request.Topic}\nBot ({request.Persona}): {botResponse}")
    };
    
    var options = new ChatOptions
    {
        ModelId = "gpt-5-mini", // cheap model for extraction
        Instructions = systemPrompt,
        MaxOutputTokens = 200,
        Tools = [UpdateUserMemoryTool],
        ToolMode = ChatToolMode.Auto, // may or may not call the tool
    };
    
    var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
    // Parse any update_user_memory tool calls from the response
    return ParseMemoryOperations(response);
}
```

### 5.5 Changes to `CreativeResult`

```csharp
public sealed record CreativeResult(
    string PrimaryMessage,
    ulong? ReplyToMessageId = null,
    IReadOnlyList<MemoryOperation>? MemoryUpdates = null   // ← new
);

public sealed record MemoryOperation(
    MemoryAction Action,  // Save, Update, Forget
    int? MemoryIndex,
    string? Content,
    string? Context
);

public enum MemoryAction { Save, Update, Forget }
```

---

## 6. Privacy & Safety

### 6.1 Principles

1. **Transparency**: Users should be able to ask the bot "what do you remember about me?" and get a clear answer. This is handled naturally by injecting memories into the prompt — the model sees them and can report them.

2. **User control**: Users must be able to request deletion. Two mechanisms:
   - Natural language: "forget everything you know about me" → the model calls `update_user_memory` with `action: "forget"`.
   - Explicit command: `!sky forget-me` → calls `IUserMemoryStore.ForgetAllAsync(userId)` directly, bypassing the model entirely. This is the guaranteed escape hatch.

3. **Sensitive data avoidance**: The memory extraction prompt explicitly instructs the model not to proactively save sensitive information (health conditions, financial details, passwords, relationship status) unless the user explicitly asks. This mirrors ChatGPT's approach.

4. **No cross-user leakage**: Memories are strictly scoped to `userId`. User A's memories are never injected when processing User B's message, even in the same channel.

### 6.2 GDPR-Like Considerations

Even for a hobby bot, good data practices matter:
- **Right to access**: `!sky what-do-you-know` surfaces all stored memories.
- **Right to deletion**: `!sky forget-me` wipes all memories for that user.
- **Data minimization**: 20-memory cap + LRU eviction + focused extraction prompt = we don't accumulate unbounded data.
- **Purpose limitation**: Memories are only used for response personalization, never shared, exported, or used for analytics.

### 6.3 Safety Filter Integration

The existing `SafetyFilter.ScrubBannedContent` should also be applied to memory content before storage. If a memory content matches a banned word, it should be silently discarded.

---

## 7. Token Budget Impact

| Component | Tokens (approx) | When |
|---|---|---|
| Memory injection block (10 memories) | ~250 | Every invocation with memories |
| Memory extraction system prompt | ~200 | Memory extraction call only |
| Memory extraction user content | ~300 | Memory extraction call only |
| Memory extraction tool call response | ~50 | When model decides to save |

**Primary call impact**: +250 tokens to input. On a typical invocation that already uses ~3000 input tokens, this is an 8% increase. Negligible cost impact.

**Secondary call (extraction)**: ~550 tokens input + ~50 tokens output = ~600 tokens total. At `gpt-5-mini` pricing (~$0.25/1M input, $2.00/1M output), this costs ~$0.000238 per extraction. If extraction runs on 50% of invocations, zero meaningful cost impact.

---

## 8. Configuration

New settings in `BotOptions`:

```csharp
/// <summary>
/// Maximum number of memories to store per user.
/// </summary>
public int MaxMemoriesPerUser { get; init; } = 20;

/// <summary>
/// Whether per-user memory is enabled.
/// </summary>
public bool EnableUserMemory { get; init; } = true;

/// <summary>
/// Model to use for memory extraction (should be cheap/fast).
/// </summary>
public string MemoryExtractionModel { get; init; } = "gpt-5-mini";

/// <summary>
/// Probability of running memory extraction on any given invocation (0.0-1.0).
/// Reduces cost by only extracting memories on a fraction of interactions.
/// </summary>
public double MemoryExtractionRate { get; init; } = 1.0;
```

---

## 9. Implementation Plan

### Phase 1: Core Memory (In-Memory, MVP)

**Goal**: Prove the feature works end-to-end. Memories stored in-memory, lost on restart.

1. Add `UserMemory`, `MemoryOperation`, `MemoryAction` types to `CreativeModels.cs`.
2. Create `IUserMemoryStore` interface and `InMemoryUserMemoryStore` implementation.
3. Register in DI (`Program.cs`).
4. Add `UserMemories` field to `CreativeRequest`.
5. Load memories in `DiscordBotService` handlers, attach to request.
6. Inject memories in `BuildUserContent`.
7. Add `update_user_memory` tool declaration.
8. Implement `ExtractMemoriesAsync` method and `BuildMemoryExtractionPrompt`.
9. Call extraction after successful response (fire-and-forget with error logging).
10. Apply memory operations to store.
11. Add `!sky forget-me` command handler.
12. Write unit tests for memory store, extraction parsing, injection rendering.
13. Update `runtime_architecture.md`.

**Estimated effort**: 2-3 focused sessions.

### Phase 2: Persistence

**Goal**: Memories survive restarts.

1. Implement `FileBackedUserMemoryStore` (JSON files in `data/user_memories/`).
2. Add write-through with 60s debounce.
3. Add startup loading from disk.
4. Add `!sky what-do-you-know` command to surface memories.
5. Integration test: save → restart → verify memories persist.

**Estimated effort**: 1 session.

### Phase 3: Polish & Tuning

**Goal**: Refine memory quality and user experience.

1. Tune memory extraction prompt based on observed behavior.
2. Add memory deduplication (cosine similarity or exact-match check before saving).
3. Add metrics/logging: memories created per day, avg memories per user, eviction rate.
5. Consider simulated function call injection (Section 2.3) if text injection proves unreliable.
6. Evaluate whether extraction rate can be reduced without impacting quality.

**Estimated effort**: 1-2 sessions.

### Phase 4: External Store (Future, If Needed)

1. Implement `RedisUserMemoryStore` or `SqliteUserMemoryStore`.
2. Add migration tooling from file-backed to external store.
3. Support multi-pod deployment with cache invalidation.

---

## 10. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Model saves low-quality memories ("user said hi") | Medium | Low | Focused extraction prompt with explicit DO NOT SAVE list; cap at 20 memories + LRU eviction |
| Memory extraction adds latency to responses | Low | Medium | Extraction runs after the Discord message is sent (fire-and-forget), not in the response path |
| Model ignores injected memories | Low | Medium | Clear delimiters + prominent placement in user content; escalate to simulated tool calls if needed |
| Memory content contains banned words | Low | Medium | Apply `SafetyFilter.ScrubBannedContent` before storage |
| Unbounded memory growth | Low | High | Hard cap per user + LRU eviction + `MemoryExtractionRate` throttle |
| User discomfort with bot "remembering" them | Medium | Medium | `!sky forget-me` command + transparent surfacing via `!sky what-do-you-know` |
| Two API calls per invocation doubles cost | Low | Low | Extraction uses `gpt-5-mini`, ~$0.000238/call; throttled to 50% of invocations; skipped for ambient replies |

---

## 11. Success Criteria

The feature is successful when:

1. **Continuity**: The bot references past interactions naturally ("Oh, you're back with another raccoon question, Dave").
2. **Accuracy**: Stored memories are factually correct and relevant — not low-quality filler.
3. **Persona consistency**: Memories are used *in character* — the persona remembers things through its own lens.
4. **User trust**: Users can inspect and delete memories. No surprises.
5. **Cost neutrality**: Memory extraction adds less than 10% to total API cost.
6. **Reliability**: Memory extraction failures never impact the primary response flow.

---

## 12. Open Questions

1. **Should ambient replies trigger memory extraction?** They're high-volume and low-signal. Current recommendation: no — only command and direct-reply invocations.

2. **Should the memory extraction model match the primary model?** Using a cheaper model reduces cost but may miss nuance. Start with `gpt-5-mini`, which offers strong reasoning at low cost.

3. **Should memories be persona-scoped?** If the bot has 5 personas, should each persona have its own memory of Dave, or share a global one? Global is simpler and allows cross-persona callbacks ("Even as Robotnik, I remember you said you're from Canada"). Persona-scoped would be more immersive but multiplies storage.

4. **Should we support memory across servers?** A user might interact with the bot in Server A and Server B. Sharing memories across servers enables richer continuity but might surprise users. Current recommendation: share by default (user ID is global), but add a per-server opt-out if feedback warrants it.

5. **What about server admins?** Should server admins be able to wipe all memories for their server's users? Edge case, defer to Phase 3.
