---
name: discord-sky-investigation
description: "Evidence-first workflow for investigating Discord Sky's production performance, behavior, telemetry, and improvement opportunities. Use when reviewing latest operations, bot health, quality, reception, latency, cost, regressions, silent or inert features, deployment windows, context/action coordination, memory, images, reactions, cold opens, safety, or writing an investigation report. Discovers environment access from repo-local runbooks instead of hardcoding infrastructure; gathers live logs, durable telemetry, transcripts, reactions, image records, memory/state, Discord truth, audit history, config, and source history; reconciles funnels; traces root causes; and ranks testable improvements. Use discord-sky-eval for humor verdicts and discord-sky-ops for environment-specific access commands."
argument-hint: "Optional focus or time window, for example: latest deployment, ambient replies, images, memory, or full review"
---

# Discord Sky Investigation

Run a complete, evidence-first review of how the bot is operating and where it can improve. This is
an analysis workflow, not an infrastructure runbook and not an incident-only checklist. It should
explain what happened, how confidently we know it, why the system behaved that way, and what change
would produce a measurable improvement.

Do not put cluster names, namespaces, registry names, resource groups, secret names, mount paths, or
other environment identifiers in this skill. Discover them at runtime from repository-local private
inventory and the `discord-sky-ops` skill.

## Boundaries

- Investigation mode is read-only. Do not edit source, change live configuration, restart, deploy,
  post to Discord, or trigger paid generation unless the user separately asks for implementation or
  a controlled probe.
- Capture ephemeral evidence before any action that could replace a process or container.
- Use `discord-sky-ops` to resolve environment-specific access and commands. This skill decides what
  evidence to collect and how to reason about it.
- Use `discord-sky-eval` when the question becomes "is this funny?" The investigation may measure
  reception and check grounding, targeting, repetition, and timing, but the human owns humor verdicts.
- Never reveal credentials. Pass secrets through process environment or request headers without
  printing them. Do not persist raw private exports in the repository.

## Default contract

When the user asks for a full or latest review and gives no other constraints:

1. Use the latest complete deployment lifetime as the primary window. If it is too long, use the most
   recent 72 hours and say that the window is partial.
2. Use UTC internally. Include local dates only as a convenience.
3. Cover health, traffic, LLM calls, interaction quality, reception, proactive behavior, images,
   memory, persistent state, and safety.
4. Write a local investigation document under `docs/` with the date in its name. Confirm it is ignored
   before including raw message text or owner-private details.
5. Finish the investigation before implementing recommendations. If implementation was also requested,
   preserve the evidence snapshot first, then proceed as a separate phase.

Ask the user only when a missing decision changes validity, for example:

- no time window can be inferred;
- raw Discord content would need to be persisted and the privacy stance is unknown;
- the relevant live environment or access note cannot be discovered;
- the requested conclusion requires a humor verdict rather than an operational or checkable judgment.

## Truth hierarchy

No single data stream is "the logs." Treat each source as evidence for a different claim.

| Question | Strongest source | Useful corroboration |
|---|---|---|
| What code/config is running? | live deployment identity and effective config | source at deployed SHA, startup logs |
| Did the process stay healthy? | runtime state, probes, restarts, resource metrics | stdout, historical platform logs |
| Was an opportunity considered? | durable telemetry opportunity/gate event | stdout |
| Did the model run? | `llm_call` telemetry | invocation logs, transcript timestamp |
| What did the model see and answer? | transcript record | source prompt builders, Discord context |
| Was a message or image delivered? | Discord message state | sent registry logs, transcript/image record |
| How was it received? | current Discord replies/reactions and durable reaction deltas | transcript continuation, explicit praise/complaint |
| Did memory/state change? | current durable files plus transition events | model call and consolidation/tick telemetry |
| How much abuse occurred? | moderation/audit ground truth | detector and AutoMod events |
| Why did behavior occur? | controlling source path plus effective config | telemetry sequence and exact interaction |

Runbooks are navigation, not ground truth. If a runbook disagrees with current source, schemas, or live
config, trust the current implementation and record the documentation drift.

## Phase 1: establish scope and identity

Before collecting large data:

1. Record the requested focus and investigation question.
2. Resolve the live environment through private repo inventory and `discord-sky-ops`; never guess names.
3. Record the deployed image/version, source SHA, process creation time, effective configuration, and
   current wall-clock time.
4. Choose exact inclusive start/end timestamps. State whether the window covers a complete deployment
   lifetime, crosses deployments, or has stdout gaps.
5. Identify comparison windows: previous deployment, previous equal-duration window, or a documented
   baseline. Do not compare unequal windows without rates.
6. Capture current readiness, restart count, health response, resource request/limit/usage, storage
   size, and recent platform events.

If no prior baseline exists, make the current deployment the first explicit baseline and label it
"first snapshot; no comparison available." If the window crosses deployments, partition analysis by
source SHA and effective config. Do not merge metrics whose schema or behavior changed across the boundary.

Do not roll or restart before capturing stdout from the current process.

## Phase 2: discover the current data model from code

Do not assume last month's event schema. Find the sinks, options, retention, and fields in the checked-out
source before querying data. Start with searches like:

```bash
rg -n "TelemetryEventTypes|JsonPropertyName|I.*Sink|FileBacked.*(Sink|Log|Store)" src
rg -n "BaseDirectory|RetentionDays|Record\(|Emit\(" src
rg -n "persona_invoked|llm_call|cold_open|reaction_judged|image_generated" src
rg -n "Configure<|SectionName|GetSection|RequestTimeout|ReasoningEffort" src
```

The event names above are discovery examples across telemetry, image logs, and stdout, not a promise
that every name appears in the same sink. For each event or log marker, confirm all four:

- it is declared in the current source/schema;
- its code path is reachable under the effective live feature flags;
- it is written to a durable sink rather than stdout only;
- its meaning did not change across a deployment in the selected window.

An event with zero recent rows may be healthy, disabled, unreachable, sampled, renamed, expired by
retention, or broken. Distinguish these before interpreting the zero.

Locate and inspect the current implementations of these conceptual stores:

- general durable telemetry and its event schema;
- full prompt/reply transcripts;
- human reaction add/remove records;
- image opportunity and generation records;
- per-user memory files;
- persistent character/world state;
- sent-message ownership/correlation state;
- current effective configuration and startup routing;
- Discord messages, replies, attachments, and current reactions;
- moderation/audit records and external threat labels;
- historical platform logs when current stdout is incomplete.

For each source, write down:

- where the current code says it lives;
- retention and whether the source survives restarts;
- timestamp format and timezone;
- identifiers available for joining;
- whether records contain raw content, hashes, mutable state, or only metadata;
- known missing events, sampling, or failure modes.

## Phase 3: snapshot evidence safely

Collect read-only snapshots into a temporary directory outside tracked source, or an already ignored local
analysis directory. Prefer copying bounded files once over repeatedly querying live systems.

Collect, when available:

1. Current runtime/deployment state and effective configuration.
2. Complete stdout for the selected process lifetime and previous-container output after a crash.
3. Durable telemetry files overlapping the window.
4. Transcripts overlapping the window.
5. Reaction event files and current Discord reaction state.
6. Image records.
7. Current user-memory files and persistent world/state files.
8. Actual Discord messages in every active/relevant channel for the exact window.
9. Moderation and audit history needed to establish external ground truth.
10. Historical platform logs if ephemeral stdout has gaps.
11. Source at the deployed SHA, relevant git history, and configuration changes around the window.

Minimize private data:

- Never echo tokens or API keys.
- Avoid writing raw exports into `docs/` or tracked paths.
- Keep aggregate scratch data local and delete it after the report unless retained intentionally.
- Quote only the interaction excerpts needed to support a finding.
- Keep any necessary user-provided quotation in the ignored local report unless the user explicitly
  approves a sanitized tracked version. Mark raw-identity or raw-message excerpts `[PRIVATE]` so they
  are easy to audit before sharing or staging.
- Preserve IDs only when they are needed for a join or a clickable Discord reference.

## Phase 4: normalize and inventory

Normalize all timestamps to UTC and filter every source to the exact window. Build a source inventory before
calculating conclusions:

| Source | Available interval | Rows/items | Gaps | Mutable? | Content sensitivity |
|---|---|---:|---|---|---|
| Runtime/stdout | | | | no | low |
| Telemetry | | | | no | mixed |
| Transcripts | | | | no | high |
| Durable reaction delta log | | | | no | low/mixed |
| Current Discord messages/reactions | | | | yes | high |
| Image records | | | | no | low |
| Memories/state | current snapshot | | historical transitions may be missing | yes | high |
| Audit/moderation | | | | platform-dependent | high |

If a source is unavailable, do not silently substitute a weaker source. Mark the gap and narrow the claim.
The durable reaction log is append-only evidence of adds/removes; current Discord reaction state is mutable.
Use both, and timestamp the current-state snapshot.

## Phase 5: reconcile before interpreting

Reconciliation catches missing logs, duplicate sends, false success labels, and denominator mistakes. Do it
before quality analysis.

### Core delivery funnel

Reconcile, by message ID, trigger ID, evaluation ID, opportunity ID, timestamp, and channel where possible:

```text
human trigger/opportunity
  -> gate or invocation
  -> one or more llm_call attempts
  -> transcript/result
  -> Discord delivery
  -> durable reaction deltas and current net reactions
  -> direct reply, quotation, or later uptake
```

Explain every mismatch. Common legitimate causes include rate limiting, silence decisions, provider failure,
fallback sends, message deletion, reaction removal, retention boundaries, and process replacement.

### Feature funnels

- Ambient: eligible human messages -> probability sample -> worth judgment -> text/image/silence action ->
  lease/budget veto -> model calls -> delivery -> uptake.
- Cold open: warm-channel gate -> judge cooldown -> composition -> hook dedupe -> shadow/live -> critique ->
  delivery -> uptake.
- Reaction: judge opportunity -> decline/invalid/failure/react -> Discord add success -> current reaction.
- Image: opportunity -> offered/selected -> budget -> generation -> attachment delivery -> reception.
- Memory: hint/inline availability -> recall call -> returned notes -> reference touch -> later use ->
  consolidation/eviction.
- Persistent state: tick due -> activity gate -> model call -> verifier -> commit -> version/mood/rank change.
- Safety: message/account opportunity -> detector/AutoMod/new-account decision -> alert/block -> moderator action ->
  later ban label.

Derive action labels from the code version in the window. Current ambient decisions may use `text`, `image`,
or `silence`; coordinator vetoes may be recorded as `held`, and older deployments may use `spoke`/`held`.
Build a small schema/version map instead of treating differently named historical outcomes as identical.

Never collapse these terms:

- opportunity is not attempt;
- attempt is not success;
- generated is not delivered;
- delivered is not received positively;
- no reaction is not proof of failure;
- detector output is not threat volume.

## Phase 6: calculate the bot scorecard

Use rates and distributions, not isolated totals. Report denominators and sample size next to every percentage.
Prefer median, p95, max, and failure count over averages alone.

### Health: the four golden signals

- Traffic: human messages, invocation rate, bot posts, reactions, workload calls.
- Errors: startup/auth failures, provider failures, timeouts, malformed tools, Discord send failures,
  verifier rejects, circuit-breaker events.
- Latency: end-to-end interaction latency and provider latency by workload; p50/p95/max; retries separately.
- Saturation: CPU, memory, storage, concurrency/busy vetoes, queue drops, rate and budget caps.

### Interaction and coordination

- Invocation mix by command, mention, direct reply, ambient, cold open, image, and safety action.
- Bot text share of room traffic and reaction coverage of human traffic.
- Exact trigger-target accuracy by invocation kind.
- Burst behavior, overlapping calls, in-flight vetoes, and post-send quiet-period compliance.
- Fallback frequency and whether metadata still points to the correct trigger.
- Context freshness and age of the material actually shown to each model.

### LLM performance and cost

- Calls, success/failure/cancel/timeout rates by provider, model, workload, and effort.
- Input, output, cached, and reasoning tokens; call index/retries; latency distributions.
- Effective model/effort versus configured model/effort to catch routing drift.
- Cost estimate where pricing data is trustworthy. Label estimates as estimates.
- Separate provider latency from surrounding context gathering, tools, generation, and Discord send time.

### Reception and quality proxies

- Human reactions per bot post, unique reactors, adds minus removals, and current net state.
- Direct human replies within a declared window.
- Textual continuation, quoted adoption, explicit praise, explicit complaint, correction, or confusion.
- Reception by source and invocation kind. Avoid comparing surfaces with different intent levels as if equal.
- Worth-score calibration against later reception. A high worth-prediction score that does not correlate with
  human uptake indicates an operationally miscalibrated gate, even if its distribution looks tidy. This does not
  make reception a humor verdict.

Do not declare a line funny from these proxies. Route that judgment to `discord-sky-eval` and the human.

### Feature-specific checks

- Ambient: held/spoke/image/silence distribution, score bands, lease vetoes, reception, and action replacement.
- Cold opens: timing legitimacy, fresh-context rate, repeated-hook rate, fire/decline balance, critiques, uptake.
- Reactions: decision validity, delivery success, emoji variety, media versus text behavior, density by channel.
- Media semantics: media detection, summary availability/failure, reuse versus duplicate vision calls, incoherence.
- Images: source, model/quality, budget/refusal/failure, latency, cost, delivery, and reception. Audit zero-use paths.
- Memory: notes/user, additions, exact/semantic duplicates, kind mix, ever-referenced share, recall adoption,
  consolidation success, stale accumulation, and suppression behavior.
- Persistent state: tick due/commit/reject rate, version movement, mood reachability, rank aging, and whether state
  leaks into repetitive response templates.
- Safety: detections, alerts, blocks, false positives, predicted versus missed bans, and no-op reconciliation writes.

## Phase 7: read the interactions

Metrics find where to look; they do not explain voice, grounding, or confusion. Read every bot interaction when
the window is small. For larger windows, use a stratified sample and disclose the method.

Include:

- every failure, complaint, correction, and strongest positive reception;
- every proactive text or image action;
- media and link messages;
- each invocation kind and active channel;
- high/low score examples;
- messages with no uptake and messages with unusually strong uptake.

For each sampled episode, reconstruct:

1. what the user actually sent;
2. what context and media meaning each decision/model saw;
3. which gate/action path ran;
4. what text, metadata, and attachment were delivered;
5. what humans did next.

Look for alignment failures between parallel systems: correct prose with the wrong reply target, a fresh gate with
stale composer context, a media-aware generator with a blind judge, a successful generation with failed delivery,
or a score optimized for a metric humans do not reward.

## Phase 8: trace findings to controlling code

For every material behavioral finding:

1. State the observed symptom and exact evidence.
2. Name the strongest alternative explanation.
3. Find the code and effective config that directly decide the behavior.
4. Form a falsifiable local hypothesis.
5. Use a cheap discriminating check: a neighboring test, current state comparison, focused replay, or source/history
   inspection.
6. Distinguish root cause from contributing conditions.

For timeouts, burst behavior, unexplained silence, or load-dependent failures, trace the async path as a timeline:
gate, lease/lock/queue, provider attempt and retries, tool calls, delivery, and release. Distinguish "never became
eligible" from "queued," "timed out," "cancelled," "dropped," and "generated but failed to send." Check whether
one slow feature holds ownership or shifts another feature beyond its opportunity window.

Use git history when behavior changed near a deployment boundary. Check whether the feature was ever reachable,
not merely whether code exists.

## Improvement-opportunity lenses

Apply these deliberately. The most valuable findings often live in negative space.

- Inert feature: shipped and enabled, but zero successful activations. Inspect multiplicative gates and conservative
  prompts rather than just raising probability.
- Write-only telemetry: events count activity but cannot answer whether it delivered, landed, or helped.
- Split-brain context: gate, generator, reaction, and cold-open paths interpret the same message differently.
- Metadata/text disagreement: the words address one thing while reply IDs, source labels, or ownership point elsewhere.
- False idempotence: a periodic reconciler says "sync" but writes unchanged state repeatedly.
- Survivor bias: only successful transcripts exist, hiding held, failed, deleted, or timed-out opportunities.
- Proxy inversion: a heuristic improves while human reception worsens, or a detector reports few threats because it
  cannot see the real threat shape.
- Latency composition: model latency is blamed for time spent unfurling, queueing, generating, retrying, or sending.
- Feedback-loop contamination: the bot rewards its own reactions, tunes against its holdout, or repeats its winners
  until they decay.
- Config/source drift: code supports one policy while live overrides select another.
- Stale metric: a score still rewards behavior that a later intentional redesign removed.
- Feature coupling: enabling, suppressing, or slowing one subsystem changes another subsystem's opportunities.
- Timing cascade: context, recall, retries, image work, or delivery latency moves a later action outside its useful
  moment even when every individual step succeeds.
- Silent win: a feature reports zero failures but has no evidence of invocation, delivery, or user value. Confirm
  it is actually working before calling it healthy.

## Phase 9: rank recommendations

Do not produce an unranked wishlist. For each recommendation include:

- finding and root cause;
- expected user-visible consequence;
- evidence strength and uncertainty;
- impact, effort, blast radius, and reversibility;
- smallest owning code boundary;
- focused tests or replay needed;
- telemetry needed to know whether it worked;
- quantitative success criterion and observation window;
- kill switch or rollback path for live behavior.

Use priorities:

- P0: active correctness, safety, data-loss, or severe reliability issue.
- P1: repeated user-facing confusion, broken contract, or material operational waste.
- P2: quality, calibration, observability, or dormant-value improvement.
- P3: speculative experiment with weak evidence.

Prefer staged changes that remove demonstrated defects before broad architecture. Extract a shared abstraction only
after the evidence identifies the fields and ownership it must actually carry.

## Claims discipline

Label important statements as one of:

- Fact: directly observed in a named source.
- Reconciled fact: independently supported by two or more sources.
- Inference: best explanation, with alternatives stated.
- Hypothesis: testable but not yet demonstrated.
- Recommendation: proposed action, not a fact.

Use confidence levels for inferences. Never use "zero events" to mean "zero real-world occurrences" unless an
independent source proves the detector had complete coverage.

## Investigation artifact

Write the report so another agent can reproduce the reasoning without reading the whole chat.

Recommended structure:

1. Title, date, status, exact UTC window.
2. Deployed image/source SHA and comparison baseline.
3. Executive findings, including healthy areas.
4. Scope, source inventory, privacy handling, and caveats.
5. Operational health and golden signals.
6. Reconciliation ledger and traffic/action funnels.
7. Interaction and reception analysis.
8. Feature sections: proactive behavior, reactions/media, images, memory/state, safety.
9. Cross-system root causes.
10. Prioritized improvement plan with success criteria.
11. Appendix: metric definitions, queries/scripts, unmatched records, and data gaps.

Keep raw private exports out of the report. Use concise supporting excerpts. Validate local docs for the repository's
ASCII-only convention and confirm the artifact remains ignored unless the user explicitly wants a sanitized tracked
document.

## Completion checklist

An investigation is complete only when:

- the time window and deployed source are exact;
- every available data source has an inventory entry;
- ephemeral logs were captured before mutation;
- key funnels reconcile or every mismatch is explained;
- rates include denominators and sample sizes;
- actual interactions were read, not only counted;
- facts, inferences, and recommendations are distinct;
- each major finding reaches controlling code/config;
- healthy behavior and residual risk are both reported;
- recommendations are ranked, testable, observable, and reversible;
- the local artifact is privacy-safe, ASCII-clean, and not accidentally staged;
- no production mutation occurred during the investigation unless separately authorized.