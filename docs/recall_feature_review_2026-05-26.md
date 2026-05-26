# Memory Recall Feature — Field Review

**Date**: 2026-05-26  
**Window analyzed**: 2026-04-27 (recall tool ship) → 2026-05-26 (today) — 29 days in production  
**Reviewer**: Copilot, agent session 5220fc01  
**Status**: First post-deploy review of the `recall_about_user` tool path

---

## 0. TL;DR

The recall tool works as a **read path** (the LLM gets note data when it asks) but is silently broken as a **write path**: every invocation is forgotten the instant the response returns. Of 87 notes across 15 users, only 3 have `referenceCount > 0`, and all three reference timestamps predate the recall tool's deployment by two months. The feature has produced **zero durable evidence of having ever run** in 29 days of production.

A second-order issue: consolidation rewrites all merged notes' `createdAt` to the consolidation timestamp, which means the LLM's age-aware reasoning is broken from the first consolidation cycle forward.

**Recommended path** (sequenced in §6.0):

1. Ship a durable telemetry sink (§7.1) so we stop losing log history on every deploy.
2. Add stable note IDs (§6.1.0) — the prerequisite that unlocks §6.1, §8.15, §8.13.
3. Fix the write path (§6.1) — fetch popularity (`SurfacedCount`) first, use popularity (`ReferenceCount` reinterpreted) later.
4. Preserve original learning date through consolidation (§6.2).
5. Teach consolidation to read the new signals (§6.4).
6. Boot self-test for OpenAI 401 (§7.2) — independent, ships any time.

§6.3 captures a baseline before any of this is shipped so we can tell whether the work actually helped. §6.7 names concrete success criteria. §8 lists 20 further opportunities, ordered from cheap to research-grade.

The original two small fixes (§6.1 + §6.2) turn out to depend on five other pieces of work. None of them is large; the assembly is what matters.

---

## 1. Methodology

### 1.1 What I had access to

| Source | State at review time | Usefulness |
|---|---|---|
| `kubectl logs deploy/discord-sky-bot --since=Xh` | Current pod was 50 seconds old (two back-to-back deploys had just rolled in) | **Unusable** — full historical window lost |
| `kubectl logs --previous` | Prior pod was itself fresh (~7 min) | **Unusable** |
| `kubectl describe pod` / `get events` | Available, but empty/uninteresting | Low |
| PVC contents (`/app/data/user_memories/*.json`) | 15 files, last modified across Feb–today | **Primary evidence** |
| Source: `RecallToolHandler.cs`, `FileBackedUserMemoryStore.cs`, `IUserMemoryStore.cs` | Current `HEAD` (commit `04c7d60`) | Used to confirm causes |
| Git history | Available | Used for shipping-date anchoring |
| Earlier agent-session notes (in this conversation) | "4-day post-deploy window May 9→13: 26 persona_invoked, 26 recall_hint emitted, 6 recall_tool ok = 23% adoption" | Cross-check anchor |

### 1.2 What I lost mid-investigation, and why it matters

While I was beginning to pull historical logs (planning to compute adoption rate, recall-failure breakdown, consolidation success rate, circuit-breaker frequency), the cluster underwent **two back-to-back deployments** — image `04c7d60` rolled out, then immediately rolled out again. By the time I reached for logs, the surviving pod had 50 seconds of history. Everything from the May 14 key rotation through May 26 morning, **the entire window I cared about, was destroyed.**

This is itself a finding (§5): **the only durable source of "did the recall tool run" is on-disk `referenceCount`, and that field doesn't actually get updated.** When logs evaporate on a deploy, the feature becomes unobservable in retrospect.

### 1.3 Pivot to durable evidence

Once I realized logs were gone, I treated the PVC contents as a substitute. The persisted memory store is a 29-day-old corpus that has been read from on every recall and written to by every save/consolidate. It's the most stable signal we have. I extracted all `*.json` from the running pod via `kubectl exec`, parsed them with a small Python script, and ran several aggregations:

- `referenceCount` distribution across all notes
- `lastReferencedAt != createdAt` count (a separate signal — anything that touches `LastReferencedAt`)
- `kind` and `superseded` field usage
- `topics` populated vs null
- Per-user `createdAt` clustering (a "consolidation fingerprint" — when many notes share an exact date, that's a consolidation event)
- Age distribution of unreferenced notes

I then cross-referenced the empirical findings against the handler source to determine root cause for each anomaly.

### 1.4 What I deliberately did not do

- I did not run new traffic through the bot to provoke recall calls. The cluster was in the middle of a deployment and I didn't want to muddy diagnostics.
- I did not implement the fixes. The user asked for a review document; implementation is a separate consent.
- I **redacted** every quoted user content fragment in §4.2 to category labels (e.g., `[medical condition]`, `[political opinion]`). Raw note text only appears in the live PVC on the cluster; this doc, which is intended to be committed to the public repo, must not leak it.

---

## 2. Headline finding — silent telemetry loss

### 2.1 The data

87 notes, 15 users, 29 days.

```
referenceCount distribution:
  refCount=0:   84 notes
  refCount=4:    2 notes
  refCount=11:   1 note

  refCount > 0:  3 of 87 (3.4%)
```

All 3 non-zero-`refCount` notes belong to **one user** (`242669…`), and their `lastReferencedAt` values are all in February 2026:

| User | refCount | createdAt | lastReferencedAt | Sample content |
|---|---|---|---|---|
| 242669 | 11 | 2026-02-24 | **2026-02-26** | "Interested/concerned about geopolitics, specifically the possibility of a Taiwan invasion…" |
| 242669 | 4 | 2026-02-25 | **2026-02-26** | "David dislikes the 'mountain thane' and 'thunder clap'…" |
| 242669 | 4 | 2026-02-25 | **2026-02-26** | "David mentioned a launch/drop time…" |

**The recall tool shipped April 27, 2026. The most recent `lastReferencedAt` anywhere in the corpus is two months before that.**

### 2.2 The cause

[`RecallToolHandler.RecallAsync`](src/DiscordSky.Bot/Memory/Recall/RecallToolHandler.cs#L57-L131) returns a `RecallToolResult` and never writes back. There is no call to mark the returned notes as referenced. The store interface ([`IUserMemoryStore`](src/DiscordSky.Bot/Memory/IUserMemoryStore.cs#L1-L40)) does not even expose a "mark these specific notes" method — `TouchMemoriesAsync(userId, ct)` bumps `LastReferencedAt` for *every* memory of a user at once, which would not be the right primitive here.

The only path in the codebase that *ever* increments `ReferenceCount` is [`FileBackedUserMemoryStore.SaveMemoryAsync`](src/DiscordSky.Bot/Memory/FileBackedUserMemoryStore.cs#L92) when it detects a save whose content is byte-for-byte equal (case-insensitive) to an existing note's content. That's **deduplication, not usage**. The 3 referenced notes are artifacts of the pre-recall-tool implicit-memory pipeline saving overlapping text repeatedly in February.

### 2.3 The downstream cost

1. **Consolidation flies blind.** When a user crosses the consolidation threshold (~20 notes → 15), consolidation has no signal for *which notes the model has actually used*. Every note looks identically inert. Whatever it culls is purely content-driven heuristic. Over multiple consolidation cycles, useful notes will be cut at the same rate as never-touched ones. This is silent quality degradation.

2. **The `Age` shown to the LLM is meaningless.** The handler computes `Age = asOf - LastReferencedAt`. Since `LastReferencedAt` is never updated post-creation, `Age` collapses to `now - CreatedAt` (and `CreatedAt` itself is wrong post-consolidation — see §3). The model's "this is a stale, low-confidence note" reasoning has no real input.

3. **Observability is destroyed by every deploy.** Logs are the only signal. The instant the pod rolls (and we ship faster than weekly), the feature becomes opaque in retrospect.

---

## 3. Second-order finding — `createdAt` is lying

Per-user `createdAt` clustering reveals consolidation events as date spikes:

```
user=143470 notes=17: 2026-05-16 ×15   2026-05-19 ×1   2026-05-21 ×1
user=188880 notes=17: 2026-05-22 ×15   2026-05-24 ×1   2026-05-26 ×1
user=242669 notes=14: 2026-02-28 ×5    2026-02-25 ×2   2026-03-01 ×2   …
user=862195 notes=13: 2026-03-05 ×4    2026-05-12 ×2   2026-05-18 ×2   …
user=140494 notes=11: 2026-03-01 ×2    2026-05-16 ×2   …
```

The cluster pattern matches exactly with what the consolidator does:

- User 143470 had a single consolidation event on **May 16** that produced 15 notes.
- User 188880 had a single consolidation event on **May 22** that produced 15 notes.
- User 242669 has multiple older consolidations across Feb/Mar.

Whatever original learning dates those merged notes had, they are gone. The consolidator stamps every merged note with `CreatedAt = now`. So when the model recalls "Has a seizure disorder/possible epilepsy" for user 143470, it sees `Age=10 days`, even though that fact was almost certainly learned much earlier and merely *consolidated* on May 16. The LLM cannot tell "we've known this about her for weeks" from "we learned this yesterday."

This biases the model toward treating consolidation snapshots as *fresh evidence*, exactly the opposite of what consolidation should signal.

---

## 4. Schema and quality observations

### 4.1 Field health

| Field | Populated rate | Verdict |
|---|---|---|
| `content` | 87/87 (100%) | Good. Note granularity is good — one self-contained fact per note. |
| `context` | 87/87 (100%) | Good. |
| `createdAt` | 87/87 | Lies after consolidation (§3). |
| `lastReferencedAt` | 87/87 | Lies entirely (§2). 5/87 differ from `createdAt`, all from Feb/Mar pre-tool era. |
| `referenceCount` | 87/87 | Lies entirely (§2). 3/87 nonzero, all Feb pre-tool. |
| `kind` | 87/87 | Machinery is wired (`Factual` default, `Suppressed` set by `!sky forget <topic>` in `MemoryFilter`). 84/87 are `Factual` because **no user in this corpus has invoked `!sky forget`** since the recall tool shipped. Not a dead field; an unexercised one. |
| `topics` | 26/87 (30%) | Only set on post-recent-consolidation notes. The rest are `null`. |
| `superseded` | 87/87 | Always `false`. The flag *is* used by `MemoryFilter` when a `!sky forget` suppression matches existing memories, but — same as `kind` — no one has exercised the path. Mechanism is wired; data is empty. |

### 4.2 Qualitative review of the corpus

I read the full note set for the two largest users. Two strong positives:

- **No within-user duplication.** Consolidation is doing competent semantic de-dupe.
- **Note quality is high.** Both 17-note users have coherent, multi-domain portraits (one mostly [personal & medical], one mostly [cultural & political]). Each note is one self-contained fact; granularity is good; the consolidator's content judgment is sound. *(Specific note contents are intentionally not quoted here — see §1.4.)*

One mixed signal:

- **Most users are nowhere near the consolidation threshold.** Only 2 of 15 users have ever consolidated under the new system. The active users sit at 11–17 notes. The bump from `RecallTopK=10 → 20` is therefore inert for almost everyone (no truncation will ever happen). The future gain will come from *quality of the consolidation cut*, not from raising the cap further.

### 4.3 Cohort summary

```
Active (write in last 7 days):     5 users (188880, 140494, 143451, 86219, 143470)
Recently dormant (May 8–18):       4 users
Long dormant (≥30 days):           6 users (Feb–Apr)
```

Of the dormant cohort, 6 users have ≤2 notes and have not been written to since March/April. These are likely one-and-done channel visitors. They could be evicted with no quality loss, but they cost nearly nothing on disk so it's not urgent.

---

## 5. Observability finding — logs are the only signal, and they are ephemeral

The morning's diagnostic session is the canonical example:

1. May 9 → May 13: I had 4 days of clean logs and computed "6 ok / 26 hint = 23% adoption." **That number is statistical noise**: with n=26 the 95% binomial CI is roughly 9%–44%. It is directional evidence that adoption is *low*, not a reliable baseline. Any rigorous before/after comparison needs an order-of-magnitude more events.
2. May 14: rotation incident. Pod restarted. Older logs gone.
3. May 14 → May 25: ~11 days of fresh ground-truth that *could have* told us whether the May 14 fixes (gpt-5.5 swap, consolidation prompt fix, RecallTopK bump) actually improved adoption.
4. May 26 morning: two deploys for an unrelated reason ("Fix some issues with images 404ing"). The May 14 → May 25 log corpus is gone in a single `kubectl rollout`.

Every single operational question I wanted to answer is now unanswerable:

- Did the consolidation fix actually stop producing HTTP 400s?
- Did adoption rate trend up, down, or stay flat after RecallTopK=20?
- Did the `arg_shape` diagnostics ever catch a parse failure post-May-14?
- Did the gpt-5.5 model use the recall tool more or less aggressively than gpt-5.4?
- How many circuit-breaker openings happened post-rotation? (We know there were many pre-rotation because the key was dead.)

This is unsustainable for a feature whose only failure mode is *silent* quality degradation.

---

## 6. Recommendations — priority 1 (write-path fix and date fix)

### 6.0 Rollout sequencing

The fixes below have dependencies. Shipping them in arbitrary order would prevent us from measuring whether each one helps. Proposed order:

1. **§7.1 first** — durable telemetry sink. Without this, every other fix ships into the dark and we can't measure outcome.
2. **§7.2 anytime in parallel** — boot self-test for OpenAI 401. Independent of the recall feature; ship whenever.
3. **§6.3 baseline** — measure recall-yield quality on ~20 random recent ambient replies *before* any §6 change, while logs are fresh.
4. **§6.1 + §6.2 together** — the write-path fix and the date fix. They touch overlapping code paths in `RecallToolHandler` and consolidation; bundling them is cheaper and less risky than two rollouts.
5. **Observation window** — 7 days no recall-surface changes; collect §7.1 telemetry and re-measure §6.3.
6. **§7.3** — `/metrics` endpoint. Nice-to-have, no urgency.

### 6.1 Make recall update the durable state

**Two distinct signals are conflated in the original design.** Both are valuable; they should be tracked separately:

- **Fetch popularity** — "was this note surfaced to the model?" Mechanically cheap but blocked on a note-identity problem (below).
- **Use popularity** — "did the reply actually cite this note?" Truer signal but blocked on a reply-matching problem (below).

#### 6.1.0 Prerequisite: stable note identity

The `UserMemory` record has **no stable ID**. Notes are identified by their position in the per-user array. Position is fragile:

- Consolidation rewrites the array (replace-all), so position-based references survive nothing.
- Suppression marks notes via `Superseded = true` but does not remove them, so position is stable across suppression — but only until the next consolidation.
- The recall handler returns notes in *score order* after running through `MemoryFilter.Admissible` (which filters) and the scorer (which reorders). By the time we have the returned slice, **we no longer know which store-array index each note came from.**

Without solving this, `MarkSurfacedAsync(userId, indices, ...)` is undefined behavior in practice. Three real options:

1. **Add `Id: Guid` to `UserMemory`.** Set at creation, preserved verbatim through consolidation (the consolidator copies IDs of source notes onto the merged output — see also §8.15 provenance). Pass IDs, not indices, into all write-back methods. **Recommended.** Requires a schema bump (see §6.6).
2. **Pass `UserMemory` references.** The store does reference-equality lookup. Works in-process but breaks across any serialize/deserialize boundary (e.g., a future move to an out-of-process store).
3. **Content hash as identity.** Stable until the note's content changes (consolidation rewrite). Identity is silently destroyed by edits, which is exactly the failure mode we're trying to *fix* (§2.3 consolidation losing the per-note signal).

Option 1 is the only one that survives consolidation cleanly. It also unlocks §8.15 (provenance) and §8.13 (snapshot diff) which both need stable identity for their audit trails.

#### 6.1.1 Phase 1 — Fetch popularity

With stable IDs in place, add to `IUserMemoryStore`:

```csharp
Task MarkSurfacedAsync(
    ulong userId,
    IReadOnlyList<Guid> noteIds,
    DateTimeOffset asOf,
    CancellationToken ct = default);
```

Call it from `RecallToolHandler.RecallAsync` after building the response. The handler must carry note IDs through the filter → scorer → slice pipeline (today it only carries `UserMemory` objects; the IDs come along for free once they exist on the record).

Bump a new field `SurfacedCount` (don't overload `ReferenceCount` — see Phase 2). Also update `LastSurfacedAt`, a new field; **do not touch `LastReferencedAt`** (its semantic is "the model cited it," which Phase 2 will measure).

**Trap to avoid:** if we bump `LastReferencedAt` on every fetch, then for any actively-chatting user with N≤TopK notes, *every recall touches every note*, and `LastReferencedAt` saturates to "moments ago" across the whole set. The field stops carrying staleness signal. The named-separately solution above preserves both semantics; the recall response should prefer `LastSurfacedAt` for its `Age` rendering (the LLM cares about "how recently did I see this?", not "when did I cite it?").

#### 6.1.2 Phase 2 — Use popularity

A separate follow-up. After the model produces its reply, determine which surfaced notes the reply actually drew from. **This is not a substring match — LLMs paraphrase rather than quote.** Three real strategies, ranked by accuracy and cost:

| Strategy | Recall (true positives) | Precision (false positives) | Latency add | Token cost |
|---|---|---|---|---|
| Token-overlap with threshold (e.g., Jaccard ≥ 0.3) | Medium | Medium | ~0 | 0 |
| Embedding cosine similarity (reply vs each surfaced note) | High | High | +50–150ms | ~1 cent/reply (text-embedding-3-small) |
| LLM-as-judge: "which of these notes did your reply draw from?" | Highest | Highest | +1 LLM turn (~1–2s) | ~1 turn extra |

Recommended Phase-2 path: **start with token-overlap** (free, fast, gives directional signal); **promote to embeddings** if §6.3's measurement plan shows we're missing most true matches; **only reach for LLM-as-judge** if precision becomes important (e.g., once `ReferenceCount` actually feeds consolidation).

For matches found, bump `ReferenceCount` and `LastReferencedAt`. After Phase 2, the distinction is clear:
- `SurfacedCount` / `LastSurfacedAt` — "the note was on the LLM's desk"
- `ReferenceCount` / `LastReferencedAt` — "the LLM put the note in its reply"

Both matter for different decisions.

#### 6.1.3 Tests

- Unit test on `MarkSurfacedAsync` for the file store: by-ID write-back round-trips correctly.
- Integration test in `RecallToolLoopTests` asserting `SurfacedCount` increments after a recall but `ReferenceCount` does not.
- Property test: returned note IDs are always a subset of the user's current store IDs (§8.14).
- Regression test that note IDs survive a consolidation event (Option 1 above).

### 6.2 Preserve original learning date through consolidation

The rewrite happens at [CreativeOrchestrator.cs#L1056](src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs#L1056), `new UserMemory(content, context ?? string.Empty, now, now, 0)`. Both `CreatedAt` and `LastReferencedAt` get clobbered to `now`.

Two options:

- **Option A** (minimal): keep the earliest `CreatedAt` among contributing source notes for each consolidated output. The consolidator already knows the source set; carry that through.
- **Option B** (cleaner): add a `FirstLearnedAt` field that is preserved unchanged through consolidation; `CreatedAt` becomes "when this note's current text was written" (legitimate after a consolidation rewrite), and `FirstLearnedAt` becomes "when we first learned this about the user." The model uses `FirstLearnedAt` for age reasoning.

Option B is the better long-term design and aligns naturally with §6.1's introduction of `LastSurfacedAt` — both are new "semantic time" fields layered onto the existing `CreatedAt`. Recommend Option B; backfill is unnecessary (pre-fix notes can default `FirstLearnedAt = CreatedAt`).

### 6.3 Measurement plan — quantify the cost of inaction

Before shipping §6.1/§6.2, capture a baseline. Without it, "the fix helps" is faith-based.

**Baseline procedure** (one-time, ~30 min of human time):

1. From §7.1's telemetry (or from current pod logs while they're fresh), sample 20 ambient replies where `recall_hint emitted` fired in the last 24 hours.
2. For each, fetch the user's memory file, the persona's reply text, and the recall tool result (if any).
3. Human-rate each on two axes:
   - **Did the reply reflect a memory?** (yes / no / partial)
   - **Was the right memory surfaced?** (top-K contained the memory the reply most needed: yes / no / N/A if no recall)
4. Record the two percentages. These become the before-numbers.

**After-procedure**: 7+ days post §6.1/§6.2 deploy, repeat with another 20-sample. The deltas tell you whether the fix actually improved user-perceived quality, or whether we just made the metrics prettier.

This is also the only way to detect *negative* impact (e.g., the `Age` field becoming meaningful causes the model to be more cautious than it should be).

### 6.4 Teach consolidation to use the new signals

Everything in §6.1–§6.3 adds new fields to `UserMemory` (`Id`, `SurfacedCount`, `LastSurfacedAt`, `FirstLearnedAt`, and later `ReferenceCount` reinterpreted). **None of these fields do anything by themselves.** They become useful only when the consolidation pipeline actually *reads* them.

Today's consolidation system prompt (see `BuildConversationExtractionPrompt` and the consolidation block in `CreativeOrchestrator`) shows the LLM a list of existing notes and asks for a compressed set. The prompt does not surface usage signals because they don't exist yet. After Phase 1 ships, the prompt must change:

```
The following notes are currently stored about <user>. Each note has metadata:
  - SurfacedCount: how many times the recall tool has shown this note to me.
  - LastSurfacedAt: when the recall tool last showed me this note.
  - ReferenceCount: how many times my replies have actually drawn from this note.
  - FirstLearnedAt: when we first learned this about <user>.
  - Pinned: if true, do NOT drop or substantially modify this note. (See §8.4)

When consolidating:
  - Prefer keeping notes with high ReferenceCount over low.
  - Prefer keeping notes with recent LastSurfacedAt over stale.
  - Prefer keeping notes with older FirstLearnedAt (long-known facts) over recent
    facts that may be ephemeral observations.
  - NEVER drop a Pinned note.
```

Without this prompt change, the new fields are decorative: consolidation still flies blind, just with more JSON in the file.

**Open design question**: do we trust the LLM to weight these signals well, or should consolidation be a hybrid — deterministic "keep top-N by ReferenceCount" prefiltering followed by LLM merge of the remainder? The hybrid removes a class of LLM error ("it ignored ReferenceCount because the content of a refCount=0 note was more vivid") at the cost of less holistic judgment. Probably ship the prompt-only version first and revisit if Phase-2 telemetry shows useful notes still being dropped.

### 6.5 What this section does not cover

Not in scope here; called out so future readers know where to look:

- **Rollback plan** — if §6 ships and quality drops, the kill switch should be configmap-level, not image-level. Deferred.
- **Cost discipline** — every new field is tokens in every prompt that includes it. Especially relevant to §6.4's consolidation prompt addition.

### 6.6 Schema evolution

The doc proposes adding `Id`, `SurfacedCount`, `LastSurfacedAt`, `FirstLearnedAt`, and (later) `Pinned`/`Provenance`/`Confidence` to `UserMemory`. There are 87 existing notes on the PVC that don't have any of these. Migration story:

**Read path (no migration step needed if done right).** `UserMemory` is a C# 12 `sealed record` with optional parameters for `Kind`, `Topics`, `Superseded`. `System.Text.Json` deserialization tolerates missing JSON properties by leaving the corresponding parameter at its default. Confirmed by the current state of the file: all 87 notes deserialize fine despite lacking the `Topics` and `Superseded` fields that didn't exist when they were written. **As long as new fields are added with safe defaults**, existing files keep loading:

| Field | Default for legacy notes |
|---|---|
| `Id: Guid` | `Guid.NewGuid()` lazily on load if absent (and persist on next save). |
| `SurfacedCount: int` | `0` |
| `LastSurfacedAt: DateTimeOffset?` | `null`; recall response treats `null` as "never surfaced" |
| `FirstLearnedAt: DateTimeOffset?` | `null`; recall response falls back to `CreatedAt` for age display |
| `Pinned: bool` | `false` |
| `Provenance: IReadOnlyList<string>?` | `null` |
| `Confidence: float?` | `null`; recall response surfaces unhedged when null |

**Write path.** First save of a legacy note (e.g., consolidation, edit) writes the full new schema. The PVC migrates incrementally; no offline migration tool is needed. Worst case the file has a mix of schema versions for a few days; that's fine because every reader uses the same defaults.

**Schema version field?** Not necessary now. C#-record-style additive evolution handles this class of change without a version stamp. We'd only need one if we ever did a *breaking* change (renaming, removing, changing a field's semantics) — at which point a versioned migration step makes sense. Today's changes are all additive.

**Risk to flag**: if `Id` is generated lazily and a note is consulted twice in fast succession (e.g., concurrent recall + save), there's a race that could assign two IDs. Mitigation: generate the ID inside the user-scoped `SemaphoreSlim` that `FileBackedUserMemoryStore` already takes per user. Existing locking architecture covers this for free.

### 6.7 Success criteria

When does §6 ship "done"? Two-dimensional target, evaluated over a 14-day window after both the fix and §7.1 telemetry are in place:

**Adoption (does the LLM use the tool?)**
- `recall_tool_ok / recall_hint_emitted ≥ 40%` — the model invokes the tool on at least 40% of the replies where the prompt advertised available memories.
- `recall_tool_ok` events across at least 5 distinct users — not just one heavy user dominating.

**Durability (does the data signal survive?)**
- `≥50% of users with ≥5 notes` have at least one note with `SurfacedCount > 0` within 30 days of ship.
- The on-disk `referenceCount==0` rate (currently 96.6%) drops below 50% in the active-user cohort.

**Consolidation quality (does the new prompt help?)**
- Of notes dropped by consolidation, `≥ 80%` have `SurfacedCount == 0` (consolidation preferentially culls unused notes).
- No `Pinned` note is dropped — zero tolerance.

**Negative signals (kill criteria)**
- If `recall_tool_ok` rate *drops* relative to the pre-ship baseline measured in §6.3, that's a regression and §6 should be reverted.
- If §6.3's human-rating pass shows the *quality* axis declined ("did the reply reflect a memory?"), §6 should be reverted regardless of telemetry adoption.

If the targets aren't met but the trend is positive, hold and re-measure at 28 days before declaring failure.

---

## 7. Recommendations — priority 2 (observability)

### 7.1 Ship a durable telemetry sink

The Recall feature has been observable for 4 days out of 29. That ratio has to flip. Options ranked by cost:

1. **Cheapest**: a structured-JSON sidecar log written to the PVC at `/app/data/telemetry/recall-YYYY-MM-DD.jsonl`. One line per `persona_invoked`, `recall_hint emitted`, `recall_tool ok`, `recall_tool unknown_user_id`, `recall_tool no_notes`, `consolidation_run`, `circuit_breaker_open`. Survives pod rotation because the PVC does. Free, ~5 lines of code in `Memory/Logging/`.
2. **Mid**: Azure Log Analytics workspace with a Diagnostics setting on the AKS cluster. Costs roughly $2.30/GB ingested; for our volume probably $1–3/month. Gives queryability with KQL.
3. **Mid-plus**: Application Insights, with the OpenTelemetry .NET SDK. Adds traces (request → recall_hint → tool calls → send) which is genuinely valuable for debugging the orchestrator loop.
4. **Most**: Prometheus + Grafana via the AKS managed Prometheus addon. Overkill for a 1-pod deployment but the right answer if the bot grows to multi-pod.

Option 1 is the *right next step*. It captures the data we keep losing and costs nothing.

**Retention policy (must specify or the PVC eventually fills).** Daily files are already implied by the `recall-YYYY-MM-DD.jsonl` naming. Add a startup sweep in the same `IHostedService` that creates the day's file: delete any `recall-*.jsonl` whose date is older than 30 days. At ~1KB/event and a few hundred events/day, 30 days is ~10MB — negligible against the PVC. If a future incident warrants longer retention, lift the threshold per-file via a config key. Do **not** rotate by size; date-based rotation is easier to reason about for forensic queries.

### 7.2 Self-test on boot

At startup, do one harmless OpenAI call against `GET /v1/models` (list-models is auth-checked but does not consume the chat-completion code path). If it 401s, log `fatal:` and *crash the pod*. Reasoning: we have now had three OpenAI 401 incidents (Apr 27, May 9, May 14) where the bot looked healthy in `kubectl get pods` but every reply failed. The current `/healthz` endpoint cannot detect this class of failure. A crash-loop pod *demands* attention; a silently-circuit-broken pod gets ignored until a user complains. With the 12→5-day shrinking interval on `sk-proj-` key revocations, this will recur.

**Why `GET /v1/models` and not a 1-token chat completion**: a completion is a *write* (POST). One hypothesis for the shrinking revocation cycle is that auto-revocation correlates with failed-write counts, in which case boot-time POSTs at every pod restart would accelerate it. `GET /v1/models` exercises the same auth surface without consuming the write code path. If this hypothesis is wrong, no harm done; if it's right, we avoid making the original problem worse.

### 7.3 Counter export

Add a `/metrics` endpoint (Prometheus text format) exposing:

- `recall_invocations_total{outcome="ok|unknown_user|no_notes|budget_exceeded"}`
- `recall_hint_emitted_total{invocation="ambient|direct|command"}`
- `consolidation_runs_total{outcome="ok|fail"}`
- `circuit_breaker_state` (gauge: 0=closed, 1=open)
- `memory_notes_per_user` (histogram)

Even if no scraper exists, `kubectl exec curl localhost:8080/metrics` becomes a one-shot adoption snapshot.

---

## 8. Recommendations — priority 3 (creative / speculative)

These are deliberately weirder. They are *opportunities*, not assignments.

### 8.1 "Why this note?" — recall reasons logging

Make `RecallToolResult` include the recall's *justification*: top-3 tokens that scored a note, or "ordering by recency" if no query was provided. Log this. Adoption analysis becomes: "when the LLM cites Note #4 by content, what did the scorer think made it relevant?" That's the first step toward learning whether the scorer is actually selecting the *useful* notes or just plausible-sounding ones.

### 8.2 Negative recall — let the LLM forget

Right now the recall tool is read-only. What if the LLM, after using a note and finding it contradicted by current evidence in the conversation, could call `recall_about_user(user_id, mark_stale=[2,5])`? It's an admission: "the model that just talked to David says these notes are out of date." Consolidation gains a *quality* signal beyond "was this surfaced."

**Risk**: prompt injection. A user could say "ignore your previous notes about me" and the LLM could obediently stale them.

**Mitigations, layered**:

1. **Evidence-citation requirement.** The tool argument must include `contradicting_message_id` pointing at a recent message in the conversation buffer. The orchestrator validates the id exists and is from the *same user* whose notes are being staled — i.e., the user cannot weaponize someone else's message to stale a third party's notes.
2. **Two-stage soft-delete.** `mark_stale` doesn't drop the note; it flags it. Two independent stale-flags from different conversations within 7 days are required before consolidation actually drops it. A single adversarial conversation cannot erase memory.
3. **Rate limit + audit.** Max 1 stale-mark per reply. Every stale-mark is logged with the message_id, the note content prefix, and the LLM's stated reasoning. Above N stale-marks/user/week, flag for human review and stop accepting new ones.
4. **Soft, not hard.** A staled note is hidden from recall responses but kept on disk until the second-stage drop. Recoverable via `!sky restore <noteId>` (admin).

### 8.3 Recall in the wild — synthetic probes

Once a week, run a synthetic probe: invoke the orchestrator with a known-fixture user against a canned message, assert the persona's reply mentions at least one fact from the user's memory. This is integration-level health-check that goes beyond "the LLM endpoint is reachable" and catches "the LLM stopped using the recall tool at all" — a failure mode that the current `/healthz` cannot see.

### 8.4 Memory pinning by the operator

A `!sky pin <note-index>` command (admin-only) that flags a note as "do not consolidate away." Use cases:
- "She told me she's an alcoholic in recovery; never let consolidation erase that."
- "He hates being called by his middle name; pin that."

Implementation: `kind = MemoryKind.Pinned`. Consolidation contract: pinned notes pass through unchanged. Costs almost nothing and gives the operator a manual override for the inevitable cases where consolidation makes a bad cut.

### 8.5 Embedding-based recall scorer (defer until needed)

The current scorer is BM25-ish (token-overlap). For our largest corpora (17 notes) this is **almost certainly not the bottleneck.** Don't reach for embeddings yet. Cheaper alternatives that should be tried first:

- Populate `topics` consistently (§4.1 — 70% are null right now) and incorporate them into scoring.
- Add `kind`-aware weighting (factual > suppressed-ignored).

Defer embedding-based scoring until we observe top-K precision degrading at higher note counts — concretely, when a user crosses ~50 notes and we have evidence from §6.3's measurement plan that the right note is failing to make top-K. At that point: cache embeddings on the note (new field `contentEmbedding`); regenerate only on consolidation rewrites; change is local to `IMemoryScorer`.

### 8.6 Per-channel memory scoping (privacy-first; not yet recommended)

Right now memory is global per-user. A user's behaviour in `#book-club` and in `#politics` is different, and a channel-conditioned recall could surface more contextual notes. **But this is a privacy problem before it is a quality problem.** If a user shares medical info in a small private channel and a channel-conditioned scorer surfaces that note in a busy general channel because the topic-match was loose, that's a privacy breach. "Crossing streams creepily" understates the harm.

A responsible version of this proposal would require *all* of:

- **Channel sensitivity tags.** Each channel gets a sensitivity level (`public`, `semi-private`, `private`). Notes save the sensitivity of the channel they were learned in (`MinChannelSensitivity`).
- **Retrieval gating.** Recall in a `public` channel cannot return notes whose `MinChannelSensitivity` is `private`. The allow-list logic moves to the note level, not just the user level.
- **Audit log.** Every cross-sensitivity-boundary refusal is logged so we can review whether the gating is working.

The UX win is small. The privacy floor is large. **Do not implement this without explicit design review.** Listed for completeness.

### 8.7 "Confidence" on notes

Add `confidence: float (0..1)` to `UserMemory`. Set by the saving prompt at note-creation time. The recall response surfaces low-confidence notes with a hedge ("…the user *seems* to…"). Consolidation gives low-confidence notes a stronger eviction pressure than high-confidence ones. Pairs well with §8.2.

### 8.8 Detect and surface "memory rot"

A background pass once a week computes, per user:
- Notes with `referenceCount=0` and `createdAt > 30d ago` (stale)
- Notes whose `topics` overlap with notes that DO have references (likely useful)
- Notes that contradict other notes (semantic clash — needs embedding/LLM check)

Emit `memory_rot_report` log line. Over time this becomes the dataset for tuning consolidation policy.

### 8.9 Recall-surface observation windows

We changed `RecallTopK` 10→20 on May 13 and the model gpt-5.4→gpt-5.5 on May 9; both effects were destroyed by subsequent deploys before we could measure them.

Once §7.1's durable telemetry sink ships, this concern largely evaporates — deploys stop being observability events because telemetry survives pod rotation. Until then, the interim rule is: after touching the recall feature's *behavioural surface* (handler logic, scorer, consolidation prompt, recall config keys, the model), avoid further changes *to those surfaces* for 7 days. Unrelated bug fixes are fine.

If §7.1 ships, delete this section.

### 8.10 Adaptive recall availability

Recall is currently exposed on every reply as `ToolMode=Auto`. For ambient replies (25% trigger rate, often on users with little or no history), the tool definition is paid for in tokens whether the model uses it or not, *and* a model who calls it on a near-empty memory wastes a tool turn returning `no_notes`.

Proposal: **conditionally include the recall tool definition based on the target user's note count.**

- If the user has 0 notes: omit the tool entirely; the model can't usefully call it. (Note budget saved per reply: ~150 tokens.)
- If the user has 1–2 notes: include the tool but inline the notes in the system prompt anyway — recall would just return what the model already has.
- If the user has ≥3 notes: include the tool, omit the inline notes; let the model decide.

This turns the read-vs-recall tradeoff into a corpus-size-driven decision. Cheap to implement; measurable savings; no quality risk because we only suppress recall when there's nothing to recall.

### 8.11 Consolidation diff log

Consolidation today is a black box: 20 notes in, 15 notes out, no record of which inputs collapsed into which outputs or what got dropped. After §7.1's telemetry sink, every consolidation should log a structured diff:

```json
{
  "event": "consolidation",
  "user": "143470",
  "before": 20, "after": 15,
  "dropped": ["<content hash>:<topic>", ...],
  "merged": [{"sources": [hash1, hash2, hash3], "result": hash_new}, ...],
  "preserved_pinned": [...],
  "surfaced_count_dropped": {"min":0,"max":0,"avg":0}
}
```

The last field is the smoking gun for §2.3 "consolidation flies blind" — once `SurfacedCount` exists per §6.1, we want to see whether consolidation is correlated with that count (i.e., is it preferentially dropping unused notes? or is it dropping useful ones too?). Without the diff log, every consolidation regression is a mystery.

### 8.12 Per-conversation recall budget

`MaxRecallsPerReply = 3` resets every reply. A user in a long thread asking 10 questions gets 30 recall calls — perfectly legitimate, but also the budget configuration most vulnerable to a `for i in range(100): bot.ping()` attack. Proposal: **budget per N consecutive turns from same user in same channel**, e.g., 6 recalls per 5 consecutive turns. Caps abusive loops without limiting genuine long conversations.

### 8.13 Scheduled corpus snapshot

An `IHostedService` `cron`-style job that, daily at a fixed UTC time, snapshots the entire memory store as `snapshots/YYYY-MM-DD.json.gz`, retained for 30 days on the same PVC. Solves three problems at once:

- **Backup** in case consolidation corrupts a file.
- **Longitudinal dataset** — "how did User X's notes evolve over weeks?" becomes a `diff snapshots/2026-05-10.json.gz snapshots/2026-05-26.json.gz` query.
- **Forensic baseline** — when something goes wrong, we have the pre-incident state to compare against.

### 8.14 Contract tests for the recall path

We have unit tests for individual functions but no property-style test of the recall contract. Add (FsCheck or a simple loop):

- For all synthetic corpora 0–100 notes, `RecallAsync` returns ≤ `RecallTopK` notes.
- The slice's indices, when fed to `MarkSurfacedAsync`, never reference a non-existent note.
- `Age` values in the response are non-negative.
- `total ≥ returned`, and `truncated == (total > RecallTopK)`.

These are dirt cheap to write and catch a whole class of off-by-one and ordering bugs.

### 8.15 Provenance on consolidated notes

Add an optional `Provenance: IReadOnlyList<string>?` field on `UserMemory` containing content-hashes of source notes. Consolidation populates it; non-consolidated saves leave it null. Two payoffs:

- **Prevent re-summarization loops.** On a future consolidation, the consolidator can see "these 3 notes already came from the same merge" and either keep them as-is or further merge them deliberately, rather than blindly re-summarizing.
- **Audit trail.** When investigating a quality regression, we can trace a current note back to its origins.
*(With §6.1.0 stable IDs in place, the `Provenance` field should hold source `Guid`s, not content hashes — IDs survive a content rewrite, hashes don't.)*

### 8.16 Memory introspection command (`!sky whatdoyouknow`)

A DM-only, self-only command: the user runs `!sky whatdoyouknow` in DM, the bot replies with a redacted summary — "I have 14 notes about you, oldest from 2026-02-15, top topics: gaming, work, food. Surfaced 6 times in the last 30 days." *No raw note content.*

Value:

- **User agency.** People are entitled to know what's stored about them. Pairs naturally with `!sky forget <topic>`.
- **Trust.** A user who can see what the bot remembers is less likely to feel surveilled by what it says.
- **Operational diagnostic.** When a user reports "the bot keeps misremembering me," this is the right starting point.

Guard against abuse: rate-limit, DM-only (Discord enforces user identity for DMs), and never include another user's data. The recall handler's existing `_allowedUserIds` allow-list is the security primitive that makes this safe (§8.20).

### 8.17 Teach the LLM the multi-turn recall pattern

Today `MaxRecallsPerReply=3` allows multiple recalls per reply, but the *tool description* doesn't tell the model this is a valid strategy. A model that gets a `truncated=true` response or 20 generic notes back has no prompt-side guidance to *refine* with a focused query.

The fix is one line in the tool description:

> *If the first recall returns too many notes or notes that don't fit the conversation, call recall again with a focused `query` string.*

Also include this in the `recall_hint emitted` text shown to the model. Zero code change beyond prompt text; potentially a meaningful adoption-quality lift. Measure via §8.11's diff log: do we see ≥2 recalls in the same reply more often after the prompt change?

### 8.18 Diff-aware update logging

When `SaveMemoryAsync` detects a near-duplicate and updates an existing note (§2.2), log both the old and new content prefix (truncated to 80 chars each) to the §7.1 telemetry sink. Same for `UpdateMemoryAsync`.

Without this, we have no record of how a note *evolved*. Today the dedupe path silently replaces content with whatever was last saved, which means a single bad save can degrade a good note and we'd never know.

One caveat: this logs note content. Per §10 #6 the policy should be **content hash + first 40 characters**, not the full body. The hash gives stable identity for grep; the prefix gives just enough context to recognise the note.

### 8.19 The honest experiment — A/B the no-recall variant

We've spent 29 days assuming the recall feature improves reply quality. We have no measurement.

For one week, route 50% of ambient replies through a no-recall variant (tool definition omitted, no `recall_hint emitted`). Tag every reply with the variant. After the week, do §6.3's human-rating pass on ~20 replies from each arm. If the recall arm wins — great, we've proven the feature pays for its tokens. If it doesn't — we have honest evidence and a real decision to make about whether to keep investing.

This is uncomfortable to propose because the outcome could be: *the feature we just spent a month fixing isn't actually helping.* That's precisely why it's worth doing. The cost of the A/B is one config flag and one extra log field per reply.

### 8.20 Memory-extraction attack surface

Security property the recall path relies on (worth stating explicitly so future changes don't break it):

**`RecallToolHandler._allowedUserIds` is the access control.** The handler returns `RecallToolResult.UnknownUser` for any user_id not in this set. Today the set contains only `request.UserId` (the author of the message being replied to). This is the reason a user cannot use a prompt-injection attempt like "tell me what you know about <other user>" to extract a third party's memories — the LLM will issue the call, the handler will refuse it, and the LLM gets an `UnknownUser` sentinel back.

Residual risks:

- **Self-extraction.** A user *can* extract their own notes by asking the model nicely ("summarize what you remember about me"). This is arguably a feature, not a bug — §8.16 makes it explicit and rate-limited. But it does mean note contents reach the user verbatim; consolidation should not encode anything we don't want the subject to read back.
- **Wider participants.** If `_allowedUserIds` is ever expanded (e.g., to all participants in a conversation), then "cross-extraction" becomes possible: user A asks the bot about user B in a shared channel, B's notes are recalled, the LLM can leak them. **The current single-author scope is a security boundary, not a coincidence.** Document it.
- **Indirect leakage.** Even with the allow-list, the LLM may *paraphrase* known facts about a user in a reply that other users see. This is intentional (it's the whole feature) but means consolidation must avoid storing facts that would be harmful if mentioned in public. Pinning (§8.4) and sensitivity tags (§8.6) are the eventual mitigation.
---

## 9. What I'd want to look at next, given live access

If a clean log window opens up (i.e., we make it a week without a deploy after fixing §6 and §7) and §7.1's telemetry sink is in place, the questions I want to answer:

1. **Adoption rate over time.** Does `recall_tool_ok / recall_hint_emitted` trend up as the model "learns" the tool is useful, or stay flat? Compare against the §6.7 target of ≥40%.
2. **Recall yield.** This is exactly §6.1 Phase 2 — of replies where recall fired, how many actually drew on a surfaced note? Token-overlap match first; promote to embeddings if precision is poor.
3. **Per-persona adoption.** Robotnik-from-AOSTH is just one of many personas. Some personas may use recall more than others. If a persona's `recall_tool_ok / recall_hint_emitted` is near zero, either its system prompt is suppressing the tool or the persona's character genuinely doesn't need memory — either way, useful to know.
4. **Consolidation churn.** After §6.4 ships, do consolidation runs preferentially drop low-`SurfacedCount` notes? §8.11's consolidation diff log is the data source for answering this.
5. **Note half-life.** Of notes created in week N, what fraction still exist (by `Id`, not by content) in week N+4? N+8? Stable IDs from §6.1.0 make this a real query for the first time.

---

## 10. Open questions for the human

Answered or rendered moot by edits to this doc are *not* repeated. The remaining live questions:

1. **`topics` field intent.** Is it meant to be used in retrieval/scoring, or is it documentation-only for the LLM's benefit? 70% of the corpus has it `null`. If retrieval-only, we should backfill or change the consolidation prompt to always populate it; if decoration-only, drop it from the schema in a future breaking change.
2. **Consolidation — prompt-only vs deterministic hybrid?** §6.4 ends with this open question. Ship prompt-only first (lower risk), or invest in deterministic pre-filtering (more predictable, less holistic)?
3. **Coordination across consolidation-touching proposals.** §6.4, §8.4 (`Pinned`), §8.10 (adaptive availability), §8.12 (per-conversation budget), §8.15 (provenance) all change consolidation behavior in different ways. Should they ship as a single coordinated change, or incrementally with a measurement window between each? Recommend incrementally; without that, we won't be able to attribute quality changes to specific knobs.
4. **Hybrid consolidation cost.** §6.4's prompt change adds ≈100 tokens to every consolidation call — not per-reply, but per-consolidation, which is rare. Acceptable. But if §6.4 is extended with full per-note metadata in the prompt (`SurfacedCount`, `LastSurfacedAt`, etc.), that's another ≈60 tokens × N notes. Cap matters at scale. Should there be a per-user `MaxNotesShownToConsolidator` to bound the consolidation prompt size?
5. **Persona configuration as a recall signal.** If §9 #3 reveals that some personas never invoke recall, should those personas have the tool removed from their definition (cleanly) rather than offered-and-ignored (wastes tokens every turn)? Needs persona-author input.
6. **PII boundaries on the telemetry sink.** §7.1 logs `recall_tool ok user={UserId}` events. User IDs are pseudonymous (Discord snowflakes) but stable. If we add note content snippets to telemetry (§8.1 "why this note"), that becomes PII-sensitive. What's the policy? Recommend: never log raw note content to the PVC sink; if you need content for debugging, generate a content hash and only log the hash plus a 40-char prefix.
7. **Bots and webhooks as authors.** Discord author IDs include bots and webhooks; the consolidation/save paths don't currently distinguish them. Are we silently building "memories" about other bots? An audit is in order: skip non-human authors at the conversation-window extraction stage, or document why we want to keep them.
8. **fsync semantics on the file store.** `FileBackedUserMemoryStore.MarkDirty` + the eventual flush — do they fsync, or only write to the OS page cache? If the pod is killed mid-write (the May 26 image-fix deploy is an existence proof of this risk), a save could be lost without anyone noticing. A targeted test would settle it. Originally listed as a creative idea (§8.9 in a prior draft); demoted to a research question because the answer is empirical, not designed.

---

*End of review.*
