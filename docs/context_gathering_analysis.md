# Context Gathering Analysis for Discord Sky

This document examines the current context-aggregation strategy, surveys how other systems approach this problem, and offers concrete suggestions with trade-offs.

---

## 1. What Discord Sky Sends Today

Every AI call is built from a single, stateless snapshot assembled at invocation time:

| Context Source | What's Captured | Limits |
|---|---|---|
| Channel history | Up to 20 recent messages (text + up to 3 images), oldest-first | `HistoryMessageLimit * 2` fetched, then filtered & trimmed |
| Reply chain | Parent messages walked via `Reference.MessageId` | Up to 40 hops (configurable) |
| Trigger metadata | Invoker name, user ID, channel ID, persona, topic, timestamp | Always present |
| Image vision | `UriContent` for attachment/inline images on allowlisted hosts | 3 images max across all history |
| System instructions | Persona identity, invocation-kind hints, tool-call requirement | Rebuilt every request |

**What's not captured**: user identity beyond a display name, channel/server topic or description, emoji reactions, thread metadata, pinned messages, conversation summaries from prior invocations, cross-channel awareness, time-of-day or timezone context, or any persistent memory of past interactions.

---

## 2. How Others Approach This

### 2.1 Sliding Window + Summarization (Semantic Kernel, LangChain)

The most common pattern in framework-level chat systems:
- Keep the most recent N messages verbatim.
- When the window exceeds a token threshold, compress older messages into a summary that is prepended as a system/user message.
- Semantic Kernel provides `ChatHistorySummarizationReducer`: truncate the oldest messages, summarize them with a cheap model call, and re-inject the summary.

**Critical take**: Summarization trades recall for token budget. It works well for factual Q&A bots but is risky for a *persona/comedy bot* like Discord Sky — summaries flatten tone, lose running gags, and strip the chaotic callbacks that make responses entertaining. You'd need a very persona-aware summarization prompt to avoid sterilizing the context.

### 2.2 Compaction (OpenAI Responses API)

OpenAI's newer approach: when context grows past a token threshold, the API itself produces an opaque "compaction item" that carries forward key state in fewer tokens. This is server-side, encrypted, and not human-interpretable.

**Critical take**: This is an excellent "set and forget" solution for long-running 1:1 conversations, but Discord Sky's model is fundamentally different. Each invocation is stateless — there's no persistent response chain to compact. The bot doesn't maintain a session; it builds context from scratch per message. Compaction would only help if Discord Sky moved to a persistent conversation model (see Suggestion 3 below), which introduces significant complexity.

### 2.3 RAG / Vector Store Memory (Semantic Kernel Vector Stores, Pinecone, Qdrant)

Store past messages (or summaries thereof) as embeddings in a vector database. At query time, embed the current message and retrieve the most semantically similar past interactions.

**Critical take**: RAG is powerful for knowledge-heavy bots (support bots, documentation assistants) but is a heavy dependency for a creative persona bot. The value proposition is thinner here — Discord Sky's "memory" is mostly about *recent conversational flow*, not recalling a specific fact from three weeks ago. The operational cost (embedding generation, vector DB hosting, index maintenance) is high relative to the benefit for this use case. That said, a lightweight variant (see Suggestion 2) could be valuable without the full infrastructure.

### 2.4 Simulated Function Calls for Injecting Context (Semantic Kernel pattern)

Rather than stuffing everything into a user message, inject context via fake tool calls and tool results. The model has been trained to treat tool results as high-trust factual context, so information injected this way tends to be followed more faithfully than the same content in a user message.

**Critical take**: This is under-explored and particularly relevant for Discord Sky. User metadata, channel description, or "memory" about a user could be injected as a tool result for `get_user_profile` or `get_channel_info` — the model would treat it as ground truth rather than optional context it might ignore.

### 2.5 Multi-Level Context Windows (custom implementations)

Some bots separate context into tiers with different retention:
1. **Immediate**: last 5-10 messages, verbatim (high detail, high token cost)
2. **Recent**: last 20-50 messages, summarized (medium detail, low token cost)
3. **Long-term**: key facts/preferences extracted from all history, stored persistently (minimal tokens, persistent)

**Critical take**: This maps well to Discord's chat flow — immediate context drives tone, recent context provides continuity, and long-term context lets the bot remember that "Dave always asks about cats." The challenge is building the summarization and fact-extraction layers without introducing latency.

---

## 3. Suggestions

### Suggestion 1: Channel & Server Metadata Injection

**The gap**: Discord Sky receives raw messages but no structural context about *where* it's talking. A `#cooking` channel has a very different expected vibe than `#shitposting`.

**What to add**:
- Channel name, channel topic/description (Discord's built-in topic field)
- Server (guild) name
- Whether the channel is NSFW-flagged
- Thread name (if in a thread)
- Number of active members in the channel (rough indicator of audience size)

**How**: Extend `CreativeRequest` or `CreativeContext` with these fields, pull them from `SocketCommandContext` (all available without additional API calls), and render them in `BuildUserContent` as a metadata block.

**Alternative A — Simple metadata block**: Add a few lines to the user content like `Channel: #cooking | Topic: "Share recipes and food pics" | Server: Friend Group | Members: 34`. Minimal code change, zero latency cost, provides useful behavioral anchoring.

**Alternative B — Simulated tool result injection**: Inject via a fake `get_channel_context` tool call/result pair in the chat messages (per the Semantic Kernel pattern in 2.4). The model would treat channel metadata as authoritative context. More complex, but the model follows it more reliably.

**Recommendation**: Start with Alternative A. It's 10 lines of code and immediately makes ambient replies more contextually appropriate.

---

### Suggestion 2: Lightweight Per-User Memory

**The gap**: The bot treats every user identically. It doesn't know that it's talked to someone before, what topics they tend to bring up, or what personas they prefer. Every conversation starts cold.

**What to add**: A small, in-memory or file-backed "user memory" — not full RAG, but a simple key-value store of facts about each user, populated by the model itself.

**How it works**:
1. Add a second tool declaration to the schema: `update_user_memory(user_id, facts: string[])`. The model can choose to call it alongside `send_discord_message`.
2. Store the facts in a `ConcurrentDictionary<ulong, List<string>>` (in-memory) or a simple JSON file (persistent across restarts).
3. Before each invocation, inject any known facts about the invoker into the user content or as a simulated tool result.
4. Cap facts per user (e.g., 10 most recent) to bound token usage.

**Alternative A — Model-driven extraction**: Let the model decide what's worth remembering by offering the `update_user_memory` tool. The model captures things like "Dave is vegetarian" or "Sarah likes being called 'Captain'." This is the most natural approach but adds a tool call to potentially every interaction.

**Alternative B — Rule-based extraction**: After each response, run a cheap regex or heuristic to extract patterns: repeated topics, names used, questions asked. No model call needed, but captures less nuance.

**Alternative C — Periodic summarization**: Every N interactions with a user, summarize the interaction history into a few bullet points using a cheap/fast model. Store the summary. This balances quality with cost but adds async complexity.

**Recommendation**: Alternative A is the most interesting for a persona bot — the model can remember things *in character* ("Dave claims to be a wizard but I have my doubts"). Start with in-memory storage, add file persistence later.

---

### Suggestion 3: Conversation-Scoped Context with Summarization

**The gap**: Each invocation is fully stateless. If someone has a rapid 5-message back-and-forth with the bot, each reply rebuilds context from scratch from the channel history. This wastes tokens re-sending the same messages and doesn't capture the *flow* of the conversation.

**What to add**: A short-lived conversation session that tracks a back-and-forth between the bot and a specific user, accumulating context and periodically summarizing it.

**How it works**:
1. When a DirectReply chain forms (user → bot → user → bot), group it into a "session" keyed by the initial bot message ID.
2. Maintain a `ConversationSession` object with the full message history of that exchange.
3. After 4-6 turns, summarize the earlier turns using a fast model (or the summarization reducer from Semantic Kernel) and replace verbatim messages with the summary.
4. Sessions expire after 30 min of inactivity.

**Alternative A — Pass-through + reply chain (current approach enhanced)**: Keep the stateless model but increase `ReplyChainDepth` and add the bot's own responses to the chain (currently the reply chain captures both sides). This is already mostly working — the enhancement would be to render older chain messages with less detail (truncate to first sentence) while keeping the last 3-4 verbatim.

**Alternative B — Explicit session tracking**: Build a `ConcurrentDictionary<ulong, ConversationSession>` keyed by the root bot message ID. Each back-and-forth appends to the session. Summarization is triggered at a token threshold. More complex, but gives the tightest context control.

**Alternative C — OpenAI server-side state**: If/when the M.E.AI abstraction supports `previous_response_id` or the Conversations API, offload state management entirely to OpenAI's servers. This would eliminate the bot's need to manage context at all, but creates tight coupling to one provider and may conflict with the forced-tool-call pattern.

**Recommendation**: Alternative A is the pragmatic choice. The reply chain already captures the conversation flow — the improvement is graduated detail (full text for recent messages, truncated for older ones). This requires no new data structures and respects the existing architecture.

---

### Suggestion 4: Reaction & Engagement Signals

**The gap**: The bot has no awareness of how its messages (or others') were received. Discord reactions (emoji) are a rich signal of engagement, approval, or disapproval that the model could use.

**What to add**: For messages in the history window, include reaction counts if they exceed a threshold (e.g., 3+ reactions).

**How it works**:
1. In `GatherHistoryAsync`, check `message.Reactions` (available on `IMessage`).
2. If a message has notable reactions, append a summary: `[Reactions: 😂×5, ❤️×3]`.
3. For the bot's own previous messages, this is especially useful — it tells the model which responses landed well.

**Alternative A — Simple reaction counts**: Append reaction summary to the message line in `BuildUserContent`. Minimal code, useful signal.

**Alternative B — Engagement scoring**: Compute a simple "engagement score" (reaction count + reply count) for each message and use it to *prioritize* which history messages to include. High-engagement messages are kept even if they're older; low-engagement messages are dropped first. This makes the history window more information-dense.

**Recommendation**: Start with Alternative A: it's a few lines of code and provides signal the model can use to calibrate tone (e.g., "my last joke got 5 laugh reactions — this crowd likes puns").

---

### Suggestion 5: Time & Temporal Awareness

**The gap**: The model receives `age_minutes` per message but has no broader temporal context. It doesn't know if it's 3 AM (when messages tend to be more unhinged) or Monday morning (when the channel is quiet).

**What to add**:
- Current time of day in the server's timezone (or UTC)
- Day of week
- Time since the bot last spoke in this channel
- Whether the channel has been active (message velocity over the last hour)

**How**: Add a "temporal context" block to `BuildUserContent`: `"Current time: Tuesday 2:34 AM UTC | Channel activity: 3 messages in the last hour (quiet) | Bot last spoke: 47 minutes ago"`.

**Alternative A — Static metadata block**: Just inject the timestamp and day-of-week. The model can infer "it's late, be weird" on its own.

**Alternative B — Activity-aware prompt adjustment**: Compute message velocity (messages/hour) and adjust the system instructions: busy channels get shorter, punchier responses; quiet channels get more elaborate ones. This moves beyond context injection into behavior shaping.

**Recommendation**: Alternative A is trivial and immediately useful. The model already has `age_minutes` per message — adding absolute time and day-of-week rounds out the picture.

---

## 4. Priority Ranking

| # | Suggestion | Effort | Impact | Risk |
|---|---|---|---|---|
| 1 | Channel & server metadata | Very low | Medium | None |
| 5 | Time & temporal awareness | Very low | Low-Medium | None |
| 4 | Reaction signals | Low | Medium | Minor (API call overhead for uncached messages) |
| 3 | Conversation-scoped context | Medium | Medium-High | Moderate (state management complexity) |
| 2 | Per-user memory | Medium-High | High | Moderate (storage, privacy, memory growth) |

Suggestions 1 and 5 are "free wins" — they add context that Discord already provides with no additional API calls or infrastructure. Suggestion 4 adds a useful signal at low cost. Suggestions 2 and 3 are more architectural but offer the biggest improvements to conversational quality.
