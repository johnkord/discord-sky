# Discord Sky Runtime Architecture

This document describes the current Discord Sky runtime. It focuses on ownership boundaries, provider routing,
world autonomy, durable state, and the operational signals required to explain production behavior.

## 1. Runtime And Dependencies

| Component | Current implementation |
| --- | --- |
| Runtime | .NET 10 LTS, `Microsoft.NET.Sdk.Web` |
| Discord | Discord.Net 3.20.1 |
| AI abstraction | Microsoft.Extensions.AI and OpenAI adapter 10.10.0; OpenAI SDK 2.13.0 |
| Agent framework | Microsoft Agent Framework OpenAI adapter 1.15.0 |
| MCP | ModelContextProtocol 1.4.1 |
| Image metadata / alternate local ledger | SkiaSharp with Linux native assets / Microsoft.Data.Sqlite.Core with system SQLite. Production uses the file ledger. |
| World-autonomy child | Discord Steward, .NET 10, pinned and bundled at deploy time |
| Deployment | One AKS Deployment, one replica, Recreate strategy, Azure Files PVC |

`Program.cs` binds typed options, registers singleton services, creates the active-provider `IChatClient`, wraps
it in telemetry and the shared provider guard, and starts the Discord gateway plus background services.

The active provider is selected by `LLM:ActiveProvider`. Each workload resolves a typed profile containing model,
reasoning effort, and optional reasoning summary. Per-request model selection stays inside one shared transport,
telemetry, deadline, and guard boundary.

Production selects OpenAI `gpt-6.2-astra` for every text workload: creative replies, full autonomy, utility, memory,
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

`GET /livez` only checks that the process can serve HTTP; losing Discord or a child does not by itself restart the pod. Readiness uses `GET /healthz`, which returns 200 only when:

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

`Bot:AllowImageContext` and the per-turn limit apply to image parts from the trigger, history, and unfurls; opting out also omits generated visual context, not article text. Article previews use public-destination HTTP(S) transport through validated addresses and bounded redirects. A denied fetch does not prevent a reply based on message text.

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

Model/tool-loop failures and session timeouts return a deterministic, in-character final failure reply for direct
petitions instead of falling through to a second, tool-poor persona call. With no native write attempts, the reply
says no server changes were attempted; after write attempts it warns that changes may already have occurred.
Already-delivered speech or visuals remain marked delivered, preventing a second reply after a late failure.
Ambient failures remain silent. Provider-budget/auth explanations retain their existing specific wording.
The host failure text and reason are persisted in the run ledger. No failed mutation is automatically replayed.

Full-run speech and final image tools journal `discord_delivery_attempt` before the Discord call. A receipt closes that attempt by ID and identifies confirmed chunks. An intent alone does not establish that the call was sent. A lost acknowledgement holds the turn without claiming success, retrying the send, or starting an ordinary persona answer. A definite rejection is distinct from an unknown. A terminal journal failure reports `journal_unavailable` through run telemetry without erasing an already observed delivery. The gateway can still publish an agent's own unsent final text, or yield a known pre-dispatch failure. Direct final text and budget decrees record dispatch intent and receipt in speech telemetry; a no-tools conversation with an unknown acknowledgement does not also send the decree.

In enforcing ambient modes, exploration is available only after a valid judge verdict at or above the confidence floor. A missing or weak verdict stays silent; Shadow remains observational. When a provider recovery probe is due, the gate checks availability without reserving it. The wrapped provider call owns the single probe.

The operational prompt treats errors as evidence about the reported target and action, not the entire catalog.
A protected owner nickname does not imply that a lower-position role title is unavailable. Spoken titles,
Discord nicknames, assigned roles, and stored Empire ranks are distinct state. This changes neither native
authority nor Robotnik's ability to decline a petition.

With the custom terminal function client, the agent framework may request approval for read-only peers in a
mixed read/write batch. Sky accepts only explicitly registered native or host-owned read-only peers through that callback.
Writes still require durable pre-invocation records and valid unused request IDs; unknown approvals still fail.
This was reproduced without a model and applies equally to OpenAI and xAI.

On every agent exit, approvals that were never claimed for invocation are closed as failed with
`run_ended_before_invocation`. Claimed operations keep their actual or unknown outcome. Recovery distinguishes
an explicit missing Steward journal receipt from a tool/permission/identity failure. Missing receipts for ended
or prior-process runs remain `unknown` with `steward_operation_missing`, and emit `recovery_unavailable` once.
They are not replayed and do not enter automatic lookup loops. The recovery worker leaves current active runs alone. For an earlier process's `reserved` or `executing` Steward row, it waits at least two minutes from the reservation or execution claim, then calls `reconcile_operation`, never the mutation. A missing creation ID is not permission to search by display name. A proven rejection is a verified failure, not a successful effect. Sky stores the status and error from the same identity-checked receipt. Inconclusive observations remain `unknown` and are rechecked no more than once per 15 minutes per process; transient read errors can retry on the regular 30-second cycle.

### 5.3 Conversation route

The conversation service deliberately receives no Steward tools, request IDs, mutation contract, or function loop.
It may not claim server changes. It uses the same Astra/xhigh profile, registered transport, transcript sink,
reaction attribution, and provider guard as other routes.

Both full autonomy and conversation can read Discord attachments without invoking the image generator or a
second vision model. After route admission the gateway selects the trigger's own images first. Otherwise it
uses the explicit same-channel reply target (fetching missing message metadata if necessary). Only a direct,
unthreaded question referring to nearby images may select up to two immediately preceding, consecutive human
image-only posts from one author within two minutes. A conversation break stops selection. Cross-channel or
forwarded references do not fall back to channel history. Queued opportunities carry descriptors, not image bytes.

The selected reply downloads from allow-listed Discord attachment URLs in the source channel, without redirects.
`Bot:AllowImageContext` and `Bot:HistoryImageLimit` apply. Input is also capped by `Image:MaxReferenceImages`,
`MaxReferenceBytes`, and `MaxTotalReferenceBytes`; PNG, JPEG, and WebP input must be at most 16 megapixels. The
chat request carries validated bytes with their detected media type and a source label, not a filename-based
type guess or a signed URL. An unavailable image is identified as missing evidence, not a reason to discard
the text or attempt an edit. Reading is independent of `Image:Enabled`, which controls generation.

`world_autonomy_image_input` records `kind=full|conversation`, `outcome=attached|partial|unavailable`, source
kind in `reason_code`, source and trigger IDs, included and candidate counts, and truncation. Join `operation_id`
to the run or conversation event. This records what was assembled, not whether the provider accepted the call or
read every word correctly; check `llm_call` and the delivered reply too. The reader does not log bytes, extracted
text, or signed URLs. The existing private reply transcript can still contain what the bot actually said.

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

Cache keys are 64-character SHA-256 hex digests of a versioned identity containing the model, full manifest
digest, and stable instructions. Run-specific data is excluded. Do not concatenate the full manifest into a
readable key: production manifests are already 64 characters, which previously produced 110-character keys
rejected by OpenAI before tools ran. Wire tests use production-sized digests, including long model IDs and
identity-separation checks. The local full-runtime rejection test also exercises the actual Steward catalog.

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

Direct guild turns use [one reply-parent contract](decisions/README.md#ds-010-ground-replies-without-interpreting-them-for-robotnik). The source is Discord's native same-channel reference, not the wording of the latest message. Full autonomy, the no-tools fallback and ordinary creative turns render the selected parent as quoted user content; its text is omitted from duplicate history entries. Web links are omitted from the quote, and it is bounded to 600 UTF-16 code units. A parent with no quotable text is distinct from a parent that could not be retrieved. No automatic receipt search, extra provider call, or change to delivery targeting follows from this evidence. Empire ticks and personal recall are unchanged.

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

The autonomy preflight checks availability without acquiring a probe. Only the actual provider client acquires and settles a call lease; a local refusal cannot move the next recovery deadline. On chat cancellation `llm_call.stage` distinguishes local cancellation from a client dispatch with unknown or reported usage. Unknown usage is not an invoice or a zero-cost measurement.

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

Sky speech and one-call conversation retain confirmed Discord IDs and text when a later chunk fails. The outcome is `partial_delivery`; the first visible chunk is not replayed. A verified native send to another channel remains a recorded write but does not satisfy source-channel speech.

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

On `stage=sample`, `journal_bytes` is the file size. `journal_writes`, `journal_max_wait_ms` and `journal_max_write_ms` cover successful mutations since the previous sample. The last field measures serialization and persistence, not the whole model turn. An idle interval has no timing values.

Telemetry is owner-private. Most identifiers are hashed, but selected features may retain bounded raw context for
quality review. Transcripts and final image prompts contain raw private content by design.

The telemetry sink, and the transcript and reaction sinks when enabled, prune expired UTC day files at startup and hourly. The notes command clears the personal note store only; delivery receipts and independent evidence follow their own retention policies.

## 12. Shutdown And Deployment

On graceful shutdown, Sky flushes pending memory windows, cancels in-flight work, unhooks Discord message/reaction
handlers, logs out, and disposes the client. Persistent route/guard/journal state survives container restarts and releases.

Production runs as one Docker Compose service on a single Linux host (DS-011, [deploy/compose.yaml](../deploy/compose.yaml)).
CI publishes the combined image, which includes a pinned Steward executable but no private profile. A release backs up
first, then applies the new image digest and that commit's public settings under the host's private settings, then
waits for Discord Connected with every configured guild healthy, restoring the previous files otherwise. One container
and a stop-before-start replacement keep a single writer on the file journals in the host's `data/` directory.

Use the [maintenance atlas](maintenance/atlas.md) for symptom-specific tests, release commands, and conditions that should stop a rollout.
