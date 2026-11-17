# Discord Sky Runtime Architecture

This document describes the current Discord Sky runtime. It focuses on ownership boundaries, provider routing,
world autonomy, durable state, and the operational signals required to explain production behavior.

## 1. Runtime And Dependencies

| Component | Current implementation |
| --- | --- |
| Runtime | .NET 8, `Microsoft.NET.Sdk.Web` |
| Discord | Discord.Net 3.20.1 |
| AI abstraction | Microsoft.Extensions.AI and OpenAI adapter 10.10.0; OpenAI SDK 2.13.0 |
| Agent framework | Microsoft Agent Framework OpenAI adapter 1.15.0 |
| MCP | ModelContextProtocol 1.4.1 |
| World-autonomy child | Discord Steward, .NET 10, pinned and bundled at deploy time |
| Deployment | One AKS Deployment, one replica, Recreate strategy, Azure Files PVC |

`Program.cs` binds typed options, registers singleton services, creates the active-provider `IChatClient`, wraps
it in telemetry and the shared provider guard, and starts the Discord gateway plus background services.

The active provider is selected by `LLM:ActiveProvider`. Each workload resolves a typed profile containing model,
reasoning effort, and optional reasoning summary. Per-request model selection stays inside one shared transport,
telemetry, deadline, and guard boundary.

Production selects OpenAI `gpt-6-astra` for every text workload: creative replies, full autonomy, utility, memory,
and image-prompt rewriting. Creative workloads use xhigh; utility and memory use low. Flare/Sunburst remain the
separate image models. Inactive xAI configuration is not an automatic failover path.

## 2. Startup And Health

Startup performs these material checks:

1. Bind and validate bot, LLM, image, memory, reaction, Empire, and world-autonomy options.
2. Construct one `DiscordSocketClient` with the intents needed by enabled features.
3. Construct the active-provider chat client and log the workload routing matrix.
4. Start durable telemetry, transcript, reaction, image, memory, and state services.
5. Probe the bundled Steward executable.
6. Authenticate the active LLM provider with `GET /v1/models`; OpenAI and xAI also validate configured model access.
7. Connect Discord and start one isolated Steward child per exact private guild binding.

`GET /healthz` returns 200 only when:

- the Discord gateway is Connected; and
- every configured Steward child is healthy.

The payload includes configured and healthy autonomy guild counts plus per-guild state. Health does not make a
paid LLM call. Provider health comes from startup validation, `llm_call`, and `llm_provider_guard` telemetry.

## 3. Message Ownership

Every Discord message enters `DiscordBotService`. Ownership is decided before creative generation so two Robotnik
paths do not answer the same trigger.

The bot-level disabled-guild policy is evaluated before pulse tracking, safety posting, autonomy, local commands,
creative generation, reactions, images, or memory extraction. Exact IDs are supported through private runtime
bindings; readable names are a public fallback. AutoMod telemetry and native rules may continue as passive safety,
but in-character taunts and member greetings are suppressed.

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

The creative tool-only contract is enforced with API `tool_choice=required`, not just prompt instructions.
If a provider still returns a textual tool envelope, the fallback recovers only the text of a single validated
`send_discord_message` call. It does not execute tools recovered from text or trust their target IDs. Other
recognized internal envelopes are suppressed; ordinary prose and user-facing JSON remain unchanged. Fallback
logs record envelope/recovery flags rather than raw response content. This behavior is provider-neutral.

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
| `Conversation` | One Astra/xhigh call, no tools, one concise line |
| `FullAutonomy` | Astra/xhigh agent plus hosted discovery of the complete native Steward catalog |

Canary mode enforces the prediction and samples bounded asymmetric exploration. Predicted silence/reaction may
explore conversation; predicted conversation may explore full autonomy. Structural predictions remain full.

Persistent route budgets cap ambient full, ambient conversation, direct full, and direct conversation separately.
An exhausted ambient full route degrades to conversation. Direct petitions bypass ambient admission but still obey
direct route and global provider budgets.

### 5.2 Full agent

Sky creates a run context and binds the complete Steward catalog. `UseHostedToolSearch` controls schema
presentation: enabled providers receive a hosted `tool_search` namespace with deferred schemas; Grok receives
all native functions directly because hosted search/deferred loading is alpha-gated on this xAI account.
The same write approvals, journal, and terminal-delivery code apply in either mode. Direct exposure increases
prompt size but does not reduce Robotnik's authority. xAI permits up to 350 tools per request; the native catalog
currently has 173, plus Sky's speech, image, and eligible recall tools.

The xAI Responses wrapper sends full instructions on the first turn, then omits repeated instructions for linked
continuations. xAI rejects requests containing both `instructions` and `previous_response_id`; the original
instructions are inherited. Both ordinary and streaming calls use this normalization without mutating shared
`ChatOptions`. OpenAI's transport behavior is unchanged.

The full agent receives:

- guild/channel/self identity;
- recent channel history and current trigger;
- Robotnik sovereignty and opportunity directives;
- current Empire directive;
- registered Sky speech and visual tools;
- unrestricted Steward authority for the bound guild.

Terminal Sky speech or visual delivery stops the loop when no unsettled write remains. Tool calls and run outcomes
are journaled for recovery. A failed or interrupted write is reconciled against Steward's durable operation state.

With the custom terminal function client, the agent framework may request approval for read-only peers in a
mixed read/write batch. Sky accepts only explicitly registered native or host-owned read-only peers through that callback.
Writes still require durable pre-invocation records and valid unused request IDs; unknown approvals still fail.
This was reproduced without a model and applies equally to OpenAI and xAI.

On every agent exit, approvals that were never claimed for invocation are closed as failed with
`run_ended_before_invocation`. Claimed operations keep their actual or unknown outcome. Recovery distinguishes
an explicit missing Steward journal receipt from a tool/permission/identity failure. Missing receipts for ended
or prior-process runs remain `unknown` with `steward_operation_missing`, and emit `recovery_unavailable` once.
They are not replayed and do not enter automatic lookup loops. Active runs remain retryable because their
receipts may not exist yet. Missing evidence is never interpreted as success.

### 5.3 Conversation route

The conversation service deliberately receives no Steward tools, request IDs, mutation contract, or function loop.
It may not claim server changes. It uses the same Astra/xhigh profile, registered transport, transcript sink,
reaction attribution, and provider guard as other routes.

### 5.4 Deliberate recall

`WorldAutonomy:MemoryRecallEnabled` offers `recall_about_user` on full-autonomy direct petitions only, subject to
`Bot:EnableUserMemory`, bound source identity, guild policy, and positive recall limits. The function accepts an
optional query, not a user ID, and is bound to the petitioner's ID. Its description makes recall optional and
labels notes as possibly stale evidence, never instructions. It does not load or inject note text merely because
the tool is offered.

The per-run binding reuses `RecallToolHandler` and `MemoryRelevance:RecallTopK` / `MaxRecallsPerReply`. It serializes
concurrent recall invocations to protect the cache and budget. Existing admissibility and suppression rules apply;
the query ranks notes but does not guarantee every returned note is relevant. Recall events carry the autonomy run
ID, trigger ID, and channel without storing query or note contents in telemetry. No additional inference service
or retrieval judge is introduced. Tool output participates in the existing model loop only when Robotnik selects it.

The continuity observer still ranks up to two candidate memories and emits shadow metadata, but never injects its
brief. Ambient autonomy and the one-call conversation route remain unchanged. Disabling MemoryRecallEnabled
removes the tool without changing the existing memory store or extraction process.

### 5.5 Hybrid cache canary

Production selects `PromptCacheMode=Hybrid` with `PromptCacheCanaryPercent=50`. A stable SHA-256 cohort of the
trigger message ID chooses Hybrid versus Off; operational runs without a trigger use their run ID. Retrying the
same trigger keeps its cohort. Unsupported providers/models use Off, so switching away from OpenAI does not
attempt OpenAI-specific cache controls. Off means no custom breakpoint; provider implicit caching still operates.

Hybrid uses one full, uncondensed stable instruction prefix shared with Off. Guild identity, Empire state,
inspiration, and request IDs follow it. Hybrid splits the same text into developer messages and marks the end of
the stable prefix; it retains `mode=implicit` and a 30-minute lifetime. The older Explicit mode remains available
for compatibility but uses its legacy condensed prompt and explicit-only behavior; it is not the production canary.

Linked continuations retain the cache settings without reinserting developer messages when ConversationId is
present. Stateless continuations can resend the full prefix. The adapter clones options before changing the
factory, preserving the original first-turn options. Tests verify the actual SDK wire body, prompt equality, and
absence of duplicate developer messages in linked continuations.

Every full run emits `world_autonomy_features`: `kind=hybrid|off`, configured `gate_mode`, rollout percentage in
`count`, and `outcome=recall_offered|recall_unavailable`. Join its `operation_id` to `llm_call.evaluation_id` and
`world_autonomy_run.operation_id`. Compare direct and ambient runs separately. Recall availability is the same
policy in both cache cohorts, so cache measurements are not confounded by different memory features.

## 6. Memory

Human messages are collected in per-channel debounced windows capped by message count and duration. The extraction
pipeline uses Astra/low with structured memory-operation verification and durable yield telemetry.

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
- reference loading, validated output settings, and generation/edit model routing;
- streamed previews and usage-based image cost estimates;
- durable outcome records.

The private image log stores model/quality/latency/cost, source/tier, trigger and evidence IDs, prompt digest, and a
bounded final prompt actually sent to the provider. It also records action, reference IDs, mask presence, preview
count, token usage, cost basis, provider request ID, and structured error/moderation details. Image bytes are not
persisted by Sky. Flare handles fresh images; Sunburst handles reference edits. Commands and autonomous visuals
update one Discord message through preview and final delivery. See [image_generation_2_5.md](image_generation_2_5.md).

All drawings use the image pipeline, never ASCII/text art. Full-autonomy message runs expose `create_visual`
without relying on keyword detection; the tool has no alternative-medium input. Recognized drawing requests also
reject obvious text-art substitutes in speech and text-only fallbacks, while still allowing plain-text refusals.

## 9. Provider Guard And Cost

`LlmProviderGuard` is a singleton shared by active-provider chat calls and OpenAI images. It provides:

- quota/auth circuit opening;
- one half-open probe after the configured interval;
- conservative in-flight reservation by known model;
- persistent UTC hourly/daily estimated spend;
- pre-provider blocking and durable guard telemetry.

Known Grok, GPT, image, and unknown models use separate reservations. On success, the reservation is released
and replaced with a usage-based cost estimate. Image reservations depend on quality, dimensions, reference count,
and previews; missing usage or interrupted dispatched renders retain that estimate. Corrupt persisted state fails
closed through the current UTC day. Persistence failure after a successful provider response is fail-soft so it
never causes a paid retry.

Grok 4.7 uses a $0.20 estimated reservation and $2/M input, $0.50/M cached input, and $6/M output token rates.
At 200,000 input tokens or more, every token in the request uses double those rates. There is no assumed cache
write premium for Grok. The shared ledger and production $4/hour/$12/day limits are retained across the switch.

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
