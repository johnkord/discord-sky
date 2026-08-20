# Discord Sky Runtime Architecture

This document describes the current Discord Sky runtime. It focuses on ownership boundaries, provider routing,
world autonomy, durable state, and the operational signals required to explain production behavior.

## 1. Runtime And Dependencies

| Component | Current implementation |
| --- | --- |
| Runtime | .NET 8, `Microsoft.NET.Sdk.Web` |
| Discord | Discord.Net 3.20.1 |
| AI abstraction | Microsoft.Extensions.AI 10.8.3 |
| Agent framework | Microsoft Agent Framework OpenAI adapter 1.15.0 |
| MCP | ModelContextProtocol 1.4.1 |
| World-autonomy child | Discord Steward, .NET 10, pinned and bundled at deploy time |
| Deployment | One AKS Deployment, one replica, Recreate strategy, Azure Files PVC |

`Program.cs` binds typed options, registers singleton services, creates the active-provider `IChatClient`, wraps
it in telemetry and the shared provider guard, and starts the Discord gateway plus background services.

The active provider is selected by `LLM:ActiveProvider`. Each workload resolves a typed profile containing model,
reasoning effort, and optional reasoning summary. Per-request model selection stays inside one shared transport,
telemetry, deadline, and guard boundary.

## 2. Startup And Health

Startup performs these material checks:

1. Bind and validate bot, LLM, image, memory, reaction, Empire, and world-autonomy options.
2. Construct one `DiscordSocketClient` with the intents needed by enabled features.
3. Construct the active-provider chat client and log the workload routing matrix.
4. Start durable telemetry, transcript, reaction, image, memory, and state services.
5. Probe the bundled Steward executable.
6. Authenticate the active LLM provider with `GET /v1/models`; OpenAI also validates configured model access.
7. Connect Discord and start one isolated Steward child per exact private guild binding.

`GET /healthz` returns 200 only when:

- the Discord gateway is Connected; and
- every configured Steward child is healthy.

The payload includes configured and healthy autonomy guild counts plus per-guild state. Health does not make a
paid LLM call. Provider health comes from startup validation, `llm_call`, and `llm_provider_guard` telemetry.

## 3. Message Ownership

Every Discord message enters `DiscordBotService`. Ownership is decided before creative generation so two Robotnik
paths do not answer the same trigger.

High-level order:

1. Ignore self messages and record channel pulse/activity.
2. Run safety surfaces that must see bots or non-allow-listed channels.
3. Handle local bot-management commands and explicit persona/image commands.
4. If the guild is bound to world autonomy, route the message through the autonomy owner.
5. Otherwise use direct mention/reply/command or ordinary ambient orchestration.
6. If no text reply is selected, the reaction judge may add one bounded emoji verdict.
7. Buffer eligible human text for debounced memory extraction.

Local commands such as memory deletion, memory inspection, scam reporting, Empire inspection, explicit persona
overrides, and explicit image commands remain outside world autonomy because they address the application rather
than petitioning Robotnik.

## 4. Ordinary Creative Pipeline

The ordinary pipeline builds an immutable interaction episode and a semantic message view containing normalized
text, Discord embeds/attachments, cached media semantics, and bounded HTTP unfurls. `CreativeOrchestrator` then:

- resolves the workload profile;
- applies the Robotnik character core and per-turn flavor;
- adds bounded channel/reply context;
- offers recall and image tools when the invocation permits them;
- enforces target IDs against server-owned known-message state;
- records prompt/reply transcript and provider telemetry;
- returns text, reply target, and optional attachment bytes.

`SendChunkedAsync` enforces Discord's 2,000-character limit. Only the first chunk carries a reply reference. Every
sent chunk is registered in `SentMessageRegistry` with persona, source, trigger, episode, and reply-target metadata.

## 5. World Autonomy

World autonomy is enabled only by exact private guild bindings. Each enabled guild owns:

- one isolated Steward child process;
- one native tool catalog bound to that guild;
- one mailbox that preserves direct FIFO and coalesces ambient work;
- independent durable Steward paths and a shared Sky autonomy ledger.

### 5.1 Ambient admission

Rapid ambient fragments are coalesced before semantic work. A utility-model audience judge scores independent
conversation, reaction, and structural-action worth. The host chooses one route:

| Route | Behavior |
| --- | --- |
| `Silence` | No creative model call |
| `Reaction` | One constrained emoji path |
| `Conversation` | One Sol/xhigh call, no tools, one concise line |
| `FullAutonomy` | Sol/xhigh agent plus deferred Steward tool discovery |

Canary mode enforces the prediction and samples bounded asymmetric exploration. Predicted silence/reaction may
explore conversation; predicted conversation may explore full autonomy. Structural predictions remain full.

Persistent route budgets cap ambient full, ambient conversation, direct full, and direct conversation separately.
An exhausted ambient full route degrades to conversation. Direct petitions bypass ambient admission but still obey
direct route and global provider budgets.

### 5.2 Full agent

Sky creates a run context, binds the Steward catalog, and gives the model a hosted `tool_search` namespace. Native
schemas use deferred loading, so all tools remain callable without placing every schema in the first prompt.

The full agent receives:

- guild/channel/self identity;
- recent channel history and current trigger;
- Robotnik sovereignty and opportunity directives;
- current Empire directive;
- registered Sky speech and visual tools;
- unrestricted Steward authority for the bound guild.

Terminal Sky speech or visual delivery stops the loop when no unsettled write remains. Tool calls and run outcomes
are journaled for recovery. A failed or interrupted write is reconciled against Steward's durable operation state.

### 5.3 Conversation route

The conversation service deliberately receives no Steward tools, request IDs, mutation contract, or function loop.
It may not claim server changes. It uses the same Sol/xhigh voice quality, registered transport, transcript sink,
reaction attribution, and provider guard as other routes.

### 5.4 Continuity shadow

The dominant autonomy routes do not currently receive stored user memory text. A shadow-only observer ranks up to
two admissible memories against the trigger and includes current Empire rank presence in a bounded private brief.
It emits memory IDs, counts, score, digest, and length, but never changes a prompt. Promotion requires relevance
review and a separate canary.

## 6. Memory

Human messages are collected in per-channel debounced windows capped by message count and duration. The extraction
pipeline uses Luna with structured memory-operation verification and durable yield telemetry.

The opportunity gate supports:

- `Off`: run every sampled extraction;
- `Shadow`: record would-run/would-skip, never suppress;
- `Live`: skip predicted zero-yield windows while retaining bounded exploration.

Shutdown flush policy is independent so pending windows can still run during graceful termination.

Memories are stored per user on the PVC. Read paths apply suppression, supersession, instruction-shape, kind, and
relevance policies. Recall touches only surfaced memory IDs/content, restoring recency and reference counts.

## 7. Empire State

Empire State is a persistent JSON snapshot with a structured mood/rank spine and a bounded freeform war-room body.
A six-hour activity-gated tick advances mood/ranks deterministically and optionally asks the utility model to rewrite
the body. The rewrite must pass a structural verifier before commit. Writes are atomic and retain a rollback ring.

The current tick sees the prior body, mood, and recent participant names. It does not yet consume a durable queue of
verified Discord actions or reception events.

## 8. Images

Explicit commands, model-selected image tools, ambient visual choice, and world-autonomy visuals share
`ImageToolService`. It owns:

- daily, per-user, monthly, and concurrency budgets;
- mandatory 1990s cartoon style suffix;
- approved-model policy;
- provider call and fixed-cost accounting;
- durable outcome records.

The private image log stores model/quality/latency/cost, source/tier, trigger and evidence IDs, prompt digest, and a
bounded final prompt actually sent to the provider. Image bytes are not persisted by Sky.

## 9. Provider Guard And Cost

`LlmProviderGuard` is a singleton shared by active-provider chat calls and OpenAI images. It provides:

- quota/auth circuit opening;
- one half-open probe after the configured interval;
- conservative in-flight reservation by known model;
- persistent UTC hourly/daily estimated spend;
- pre-provider blocking and durable guard telemetry.

Known Sol, mini/Luna, image, and unknown models use separate reservations. On success, the reservation is released
and replaced with measured token cost or fixed image cost. Corrupt persisted state fails closed through the current
UTC day. Persistence failure after a successful provider response is fail-soft so it never causes a paid retry.

## 10. Delivery And Reception

`SentMessageRegistry` is the process-local, bounded ownership index for every explicit Sky send. It is capped at
1,000 entries and evicts entries older than 24 hours when pruning is needed.

Human reactions normally resolve source/persona through the registry. On a miss, Sky now fetches the Discord
message, verifies that its author is the bot, and recovers it as `post_restart` or `discord_system`. Robotnik's own
emoji reactions are rejected before this read path.

Deterministic no-model autonomy fallbacks are recorded in the transcript sink with `model_invoked=false`, concrete
reason, trigger, and reply target. Successful full-agent final-text fallbacks are marked model-invoked.

## 11. Durable Observability

Primary durable sources:

| Source | Purpose |
| --- | --- |
| Telemetry JSONL | opportunities, routes, calls, costs, resources, transitions, failures |
| Transcript JSONL | full model prompt/reply and host fallback records |
| Reaction JSONL | human add/remove reception events |
| Image JSONL | generation opportunity, prompt evidence, cost, outcome |
| User memory JSON | current per-user learned state |
| Empire JSON | current mood/ranks/body |
| Guard/budget JSON | restart-surviving spend and route counters |
| Autonomy/Steward journals | run, tool, dispatch, and recovery evidence |

`runtime_resource` samples cgroup current/limit, Sky RSS, direct-child RSS/count, managed heap, GC heap and
fragmentation, collection counts, threads, and bounded memory/media/sent-message cache counts. Warning and critical
bands are 80 and 90 percent of the cgroup limit.

Telemetry is owner-private. Most identifiers are hashed, but selected features may retain bounded raw context for
quality review. Transcripts and final image prompts contain raw private content by design.

## 12. Shutdown And Deployment

On graceful shutdown, Sky flushes pending memory windows, cancels in-flight work, unhooks Discord message/reaction
handlers, logs out, and disposes the client. Persistent route/guard/journal state survives pod replacement.

Production uses the hardened deploy script or serialized GitHub Actions workflow. The image includes a pinned
Steward executable but no private profile. Private profile and binding resources are validated before apply. A failed
rollout restores the prior deployment and private resources. Recreate strategy prevents concurrent writers on the
Azure Files-backed file journals.
