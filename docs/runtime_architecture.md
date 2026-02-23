# Discord Sky — Runtime Architecture

This document describes how the Discord Sky bot operates at runtime: how it starts, processes messages, interacts with OpenAI, and delivers responses.

---

## 1. Technology Stack

| Component | Technology | Version |
|---|---|---|
| Runtime | .NET 8.0 | `net8.0` |
| Project SDK | `Microsoft.NET.Sdk.Web` | ASP.NET Core Web Host |
| Discord library | Discord.NET | 3.14.1 |
| AI abstraction | Microsoft.Extensions.AI | 10.3.0 (transitive) |
| AI provider bridge | Microsoft.Agents.AI.OpenAI | 1.0.0-rc1 |
| OpenAI SDK | OpenAI | 2.8.0 (transitive) |
| Deployment | Docker + Kubernetes (AKS) | — |

The `Microsoft.NET.Sdk.Web` provides ASP.NET Core hosting, logging, options, and dependency injection without additional NuGet references. Only two explicit packages are needed: `Discord.Net` and `Microsoft.Agents.AI.OpenAI`.

---

## 2. Startup Sequence

The entry point is `Program.cs`, which uses the ASP.NET Core `WebApplication` pattern.

### 2.1 Dependency Injection Registration

```
WebApplication.CreateBuilder(args)
  ├─ WebHost.UseUrls("http://+:8080")
  ├─ Configure<BotOptions>("Bot")
  ├─ Configure<ChaosSettings>("Chaos")
  ├─ Configure<OpenAIOptions>("OpenAI")
  ├─ Singleton: DiscordSocketConfig
  │     GatewayIntents = Guilds | GuildMessages | MessageContent | DirectMessages
  │     MessageCacheSize = 100
  ├─ Singleton: DiscordSocketClient
  ├─ Singleton: IChatClient
  │     OpenAIClient(apiKey).GetChatClient(model).AsIChatClient()
  ├─ Singleton: SafetyFilter
  ├─ Singleton: ContextAggregator
  ├─ Singleton: CreativeOrchestrator
  └─ HostedService: DiscordBotService
```

Key observations:
- **All services are singletons.** The bot runs as a single long-lived process with one Discord WebSocket connection and one OpenAI client.
- Configuration types (`BotOptions`, `ChaosSettings`, `OpenAIOptions`) are consumed via `IOptions<T>` — there is no bare singleton registration.
- The `IChatClient` is resolved once at startup using `OpenAIClient → ChatClient → AsIChatClient()`. The model string used for this initial `GetChatClient()` call may be overridden per-request via `ChatOptions.ModelId`, but the underlying transport client is shared.

### 2.2 Health Endpoint

After `builder.Build()`, the application registers an HTTP health endpoint:

```
GET /healthz
  → ConnectionState.Connected  → 200 { status: "healthy",  connection: "connected" }
  → otherwise                  → 503 { status: "degraded", connection: "<state>" }
```

This provides application-level health reporting to Kubernetes liveness and readiness probes (see Section 9).

### 2.3 Host Startup

`app.RunAsync()` starts the ASP.NET Core host, which:

1. Opens the HTTP listener on port 8080 for the `/healthz` endpoint.
2. Calls `DiscordBotService.StartAsync()`:
   - Hooks three event handlers: `Log`, `MessageReceived`, `Ready`.
   - If `Bot:Token` is empty, logs a warning and enters **dry mode** (no Discord connection — useful for testing).
   - Otherwise: calls `LoginAsync(TokenType.Bot, token)`, then `StartAsync()`, and optionally `SetGameAsync(status)`.
3. When the WebSocket connects and the `Ready` event fires, `OnReadyAsync` passes the bot's own user ID to `ContextAggregator.SetBotUserId()` so it can later distinguish its own messages in history.

---

## 3. Message Processing Pipeline

Every Discord message arrives via the `MessageReceived` event. The top-level handler `OnMessageReceivedAsync` wraps all processing in a try-catch: unhandled exceptions are logged and a brief error notification is sent to the channel, ensuring a single bad message never crashes the process.

### 3.1 Gate Filters (in order)

```
OnMessageReceivedAsync(rawMessage) → try { ProcessMessageAsync(rawMessage) }
  │
  ├─ [1] Type gate: must be SocketUserMessage (rejects system messages)
  ├─ [2] Bot gate: message.Author.IsBot → reject (never responds to bots)
  ├─ [3] Ban-word gate: ChaosSettings.ContainsBanWord(content) → reject
  └─ [4] Channel gate: BotOptions.IsChannelAllowed(channelName) → reject
```

If `AllowedChannelNames` is empty, all channels pass gate 4.

### 3.2 Invocation Routing

After passing all gates, the message is classified into one of three invocation kinds:

```
              ┌─────────────────────────────────┐
              │     Is the message a reply to    │
              │       one of the bot's own       │
              │           messages?              │
              └────────┬───────────┬─────────────┘
                   Yes │           │ No
                       ▼           ▼
             HandleDirectReply   Does content start
                                 with CommandPrefix?
                                   │
                              Yes  │  No
                               ▼   ▼
                    HandlePersona  Roll ambient
                 (Command kind)    chance dice
                                      │
                            roll < AmbientReplyChance?
                               │           │
                           Yes │           │ No
                               ▼           ▼
                    HandlePersona     (ignored)
                    (Ambient kind)
```

**Direct Reply detection** checks `message.Reference.MessageId`, fetches the referenced message (from cache or API), and verifies the referenced author is the bot. The persona used for the original bot message is looked up in the **persona cache** (see Section 6.3), falling back to the configured default.

**Command invocation** strips the prefix and parses optional persona/topic syntax:
- `!sky` → default persona, no topic
- `!sky hello` → default persona, topic="hello"
- `!sky(Gandalf) You shall not pass` → persona="Gandalf", topic="You shall not pass"

**Ambient invocation** is probabilistic: `AmbientReplyChance` (default 0.25 = 25%) per message.

### 3.3 Typing Indicator

For `Command` and `DirectReply` invocations, the bot triggers a typing indicator (`TriggerTypingAsync`) before calling the orchestrator. Ambient invocations skip this to remain stealthy.

### 3.4 Cancellation

All handler methods accept the shutdown `CancellationToken` (from `_shutdownCts`). If the host is shutting down while a message is being processed, the token signals cancellation, allowing graceful interruption of long-running OpenAI calls or Discord API fetches.

---

## 4. Context Gathering

`ContextAggregator` builds the context payload that is sent to the AI model.

### 4.1 Channel History (`GatherHistoryAsync`)

1. Fetches `HistoryMessageLimit * 2` recent messages from the channel (over-fetches to account for filtering).
2. Filters out:
   - The trigger message itself
   - Messages from other bots (unless it's this bot with `IncludeOwnMessagesInHistory = true`)
   - Messages starting with the command prefix (to avoid feeding `!sky` invocations back)
   - Messages with no text content and no images
3. Orders by timestamp (oldest first), takes the most recent `HistoryMessageLimit` messages.
4. Calls `TrimImageOverflow` to ensure the total image count across all messages doesn't exceed `HistoryImageLimit`. When trimming, it retains the **most recent** images.

### 4.2 Image Collection (`CollectImages`)

For each message, images are extracted from two sources:

1. **Attachments**: checked via `ContentType` (starts with `image/`) or file extension (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.bmp`).
2. **Inline URLs**: extracted via regex from message content, filtered by extension.

Both sources are gated by:
- HTTPS-only scheme enforcement
- Host allowlist (`cdn.discordapp.com`, `media.discordapp.net` by default)
- Deduplication by URL

### 4.3 Reply Chain (`GatherReplyChainAsync`)

For `DirectReply` invocations, the aggregator walks the `Reference.MessageId` chain backwards:

1. Starts at the trigger message
2. Fetches each parent message via `channel.GetMessageAsync()`
3. Stops when: no more parents, circular reference detected (via `HashSet<ulong>`), or `ReplyChainDepth` (default 40) reached
4. Reverses the chain to oldest-first order

This chain is later rendered prominently in the user prompt, distinct from the channel history.

### 4.4 Channel & Temporal Context (`BuildChannelContext`)

`DiscordBotService` assembles a `ChannelContext` record from the `SocketCommandContext` before each invocation. All data comes from Discord's cached state — no additional API calls are required.

| Field | Source |
|---|---|
| `ChannelName` | `SocketGuildChannel.Name` |
| `ChannelTopic` | `SocketTextChannel.Topic` (the channel description) |
| `ServerName` | `SocketGuild.Name` |
| `IsNsfw` | `SocketTextChannel.IsNsfw` |
| `ThreadName` | `IThreadChannel.Name` (if in a thread) |
| `MemberCount` | `SocketGuild.MemberCount` |
| `RecentMessageCount` | Count of cached messages in the last hour (up to 100 sampled) |
| `BotLastSpokeAt` | Timestamp of the bot's most recent cached message in the channel |

This context is passed on the `CreativeRequest` and rendered in the user prompt as two metadata lines: a channel/server line and a temporal line (see Section 5.2).

---

## 5. AI Interaction

`CreativeOrchestrator.ExecuteAsync` is the core orchestration method.

### 5.1 Rate Limiting Pre-Check

`SafetyFilter.ShouldRateLimit()` uses a sliding 1-hour window with thread-safe locking. A `Queue<DateTimeOffset>` (guarded by `lock`) tracks prompt timestamps, evicts entries older than 1 hour, and rejects if the count exceeds `MaxPromptsPerHour` (default 20). If rate-limited:
- Command/DirectReply: returns "I'm catching my breath—try again soon!"
- Ambient: returns empty string (silently suppressed)

### 5.2 Prompt Construction

The AI request is built from two components:

**System Instructions** (`BuildSystemInstructions`):
- Sets the persona: "You are roleplaying as {persona}"
- Adjusts behavior based on invocation kind (DirectReply gets specific instructions about reading reply chains and staying responsive to the current message)
- Thread-aware: if in a Discord thread, encourages more chaotic responses
- Includes a critical requirement to always call the `send_discord_message` tool

**User Content** (`BuildUserContent`):
- **Channel & temporal metadata**: if `ChannelContext` is present, renders two lines — a channel line (`Channel: #cooking | Server: Friend Group | Topic: "Share recipes" | Server members: 34`) and a temporal line (`Current time: Tuesday 2:34 PM UTC | Channel activity: 12 messages (moderate) | Bot last spoke: 15 min ago`)
- For DirectReply: renders the reply chain prominently with `=== CONVERSATION HISTORY ===` markers, then highlights the current message with `>>> CURRENT MESSAGE <<<`
- Renders metadata: invoker name, user ID
- Appends the topic (if present) or instructs the model to continue conversation naturally
- Appends each history message as a structured `TextContent` line: `{MessageId} | {Author} | age_minutes={N} | bot={true/false} => {content}`
- For messages with images, appends `UriContent` entries (passed as vision inputs to the API)

### 5.3 Chat Options

```csharp
ChatOptions {
    ModelId = ResolveModel(persona),     // per-persona override or default
    Instructions = <system prompt>,       // injected automatically
    MaxOutputTokens = hasReasoning ? clamp(MaxTokens*3, 1500, 4096)
                                   : clamp(MaxTokens, 300, 1024),
    Tools = [SendDiscordMessageTool],     // AIFunctionDeclaration (schema-only)
    ToolMode = ChatToolMode.RequireSpecific("send_discord_message"),
    Reasoning = { Effort, Output }        // optional, parsed from config strings
}
```

**Ambient token cap**: for `Ambient` invocations, `MaxOutputTokens` is further clamped to a maximum of 512 tokens to reduce cost and response length.

Key design decisions:
- **Forced tool call**: `ChatToolMode.RequireSpecific` forces the model to always produce a tool call rather than free-form text. This guarantees structured output.
- **Schema-only tool declaration**: Using `AIFunctionDeclaration` (no implementation delegate) prevents the M.E.AI auto-invoke loop. The tool call is parsed manually.
- **Model override per persona**: `IntentModelOverrides["Remix"] = "gpt-5.2"` routes specific personas to different models.
- **Reasoning budget**: When reasoning is enabled, output tokens are tripled to accommodate the reasoning trace plus the tool call.

### 5.4 Retry with Exponential Backoff

`GetResponseWithRetryAsync` wraps `_chatClient.GetResponseAsync()` with automatic retry:

- Up to **3 attempts** before giving up.
- Only retries on **transient errors**: `HttpRequestException`, `TaskCanceledException`, `TimeoutException`.
- **Exponential backoff**: 2s, 4s delays between retries (2^attempt seconds).
- Respects the shutdown `CancellationToken` — won't retry after cancellation.

### 5.5 Response Parsing

After the response is received:

1. Scans all `FunctionCallContent` items in the response for one named `send_discord_message`.
2. Calls `TryParseToolCallArguments()` to extract `mode`, `text`, and optional `target_message_id`.
3. Validates the `target_message_id` against the `knownMessages` dictionary (built from history) — if the model hallucinates an ID not in the history, it downgrades to broadcast mode.
4. Scrubs the text through `SafetyFilter.ScrubBannedContent()` (replaces ban words with `***`).
5. If the tool call is missing or unparseable, falls back to either raw response text or a placeholder like `[{persona} pauses dramatically but says nothing.]`.

---

## 6. Response Delivery

Back in `DiscordBotService`, the orchestrator's `CreativeResult` is consumed.

### 6.1 Reply Behavior

| Invocation Kind | Reply Behavior |
|---|---|
| Command | Sends to channel; if `ReplyToMessageId` is set, uses Discord's reply feature |
| Ambient | Same as Command, but if result is empty, message is silently suppressed (no send) |
| DirectReply | Always replies (defaults to replying to the trigger message if orchestrator doesn't specify a different target) |

Discord's `MessageReference` is used for reply threading.

### 6.2 Message Chunking

`SendChunkedAsync` handles Discord's 2000-character message limit. If the response exceeds the limit, it is split into multiple messages using `ChunkMessage`:

1. Attempts to split at the nearest **newline** within the last half of the chunk.
2. Falls back to splitting at the nearest **space**.
3. As a last resort, performs a hard split at the character limit.
4. Leading whitespace is trimmed from each subsequent chunk.
5. Only the first chunk carries the reply `MessageReference`; follow-up chunks are free-standing.

### 6.3 Persona Cache

After each send, `CachePersona` stores the mapping `messageId → (persona, timestamp)` in a `ConcurrentDictionary`. This allows `HandleDirectReplyAsync` to look up which persona was used when the bot originally responded, maintaining character continuity across reply chains.

- **TTL**: entries older than 24 hours are evicted.
- **Size cap**: when the cache exceeds 500 entries, stale entries are pruned.

---

## 7. Safety Mechanisms

| Mechanism | Implementation | Default |
|---|---|---|
| Rate limiting | Sliding 1-hour window, `lock` + `Queue<DateTimeOffset>` (thread-safe) | 20 prompts/hour |
| Ban words (inbound) | `ChaosSettings.ContainsBanWord()` — drops messages on sight | Empty list |
| Ban words (outbound) | `SafetyFilter.ScrubBannedContent()` — pre-compiled `Regex` replaces with `***` | Empty list |
| Channel allowlist | `BotOptions.IsChannelAllowed()` — empty = all channels allowed | Empty (all) |
| Image host allowlist | HTTPS + specific hosts for image vision | Discord CDN only |
| Bot ignores bots | `message.Author.IsBot` in gate filter | Always on |
| Reply target validation | Only known message IDs from history accepted | Always on |

The ban-word regex is compiled once at `SafetyFilter` construction (`RegexOptions.IgnoreCase | RegexOptions.Compiled`) and reused for all subsequent scrub operations. If no ban words are configured, the regex is `null` and scrubbing is a no-op.

---

## 8. Shutdown & Disposal

`DiscordBotService` implements both `IHostedService.StopAsync` and `IAsyncDisposable`.

**StopAsync:**
1. Signals `_shutdownCts` via `CancelAsync()`, which cancels all in-flight message handlers and OpenAI calls.
2. Unhooks all event handlers (`Log`, `MessageReceived`, `Ready`).
3. Calls `LogoutAsync` + `StopAsync` on the Discord client.

**DisposeAsync:**
1. Disposes the `CancellationTokenSource`.
2. Calls `_client.DisposeAsync()` to release the WebSocket.

The ASP.NET Core host handles SIGTERM/SIGINT, triggering `StopAsync` followed by `DisposeAsync`. The HTTP health endpoint also shuts down gracefully as part of the host lifecycle.

---

## 9. Deployment Architecture

The bot runs as a single-replica Kubernetes Deployment on AKS:

- **Image**: Multi-stage Docker build (SDK for build → `aspnet:8.0` runtime for execution), exposes port 8080.
- **Configuration**: `ConfigMap` for non-sensitive settings (including `ASPNETCORE_URLS: "http://+:8080"`), `Secret` for token and API key.
- **Resources**: 100m–250m CPU, 256Mi–512Mi memory.
- **Health probes**: Liveness and readiness probes use `httpGet` on port 8080, path `/healthz`. The endpoint returns connection state from the `DiscordSocketClient`, providing application-level health monitoring rather than just process-level checks.
