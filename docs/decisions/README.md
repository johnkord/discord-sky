# Discord Sky decision register

Use one register. `Product` describes what the room or owner can count on. `Architecture` describes how Sky keeps a promise. `Experiment` is a reversible bet, not policy just because it is deployed. An entry can have two labels. The team can add separate views later; it should not maintain two versions of the same decision.

Write an entry when reversing a choice would surprise users, change authority or data use, require a migration, or reopen an argument the team has already settled. A bug fix, routine model version bump, deployment, or individual Robotnik decree belongs in git, a runbook, or the operation journal instead. Keep private chat, prompts, IDs, credentials, and raw telemetry out of this register.

Each entry needs **ID, state, date, accountable role, choice and reason, alternative or cost, evidence, and what would reopen or end it**. Use `Proposed`, `Accepted`, `Trial`, `Rejected`, or `Superseded`. On a reversal, add the successor ID to the old entry. The product owner accepts behavioral and data-use commitments; the maintainer accepts implementation choices and records the evidence. For one person wearing both hats, this need not mean two approvals.

DS-001 through DS-004 record earlier decisions. Use the [maintenance atlas](../maintenance/atlas.md) to find the responsible code and the [deployment runbook](../../k8s/discord-sky/README.md) for live controls.

## DS-001: A petition is not an instruction

**Product | Accepted | 2026-08-03, reaffirmed 2026-11-17 | Product owner.** In an explicitly bound guild, a direct petition gets the tool-capable route subject to admission and provider availability. It does not entitle the petitioner to a mutation. Robotnik may decline or improvise, but must not claim an unverified effect. Ambient remarks may receive speech, a reaction, action, or silence. Locally handled management commands remain literal commands.

This preserves a character with real agency. Automatically obeying owners in chat or silently downgrading casual mentions would be a different product. Reopen if the owner wants an operator assistant, or blind review shows that this policy repeatedly harms the room experience. [Original routing decision](../autonomy_routing_cost_controls_2026-08-03.md); [character contract](../robotnik_character_bible.md).

## DS-002: One gateway owner; guild-bound, journaled actions

**Architecture | Accepted | 2026-08, reconstructed | Maintainer.** Run one Sky gateway, with exact opt-in bindings, separate Steward children and durable state for their operations. Record intended writes before dispatch; an unknown receipt is not permission to replay. Keep private profiles outside image layers. This isolates guild authority; it does not make child processes security sandboxes or imply that all conversational state is per-guild.

The trade-off is a short reconnect during replacement rather than competing writers. Reopen the single-writer topology only for a demonstrated availability need and a tested handoff/recovery protocol. [Runtime](../runtime_architecture.md); [rollout and recovery](../../k8s/discord-sky/README.md).

## DS-003: Recall deliberately; do not inject the shadow brief

**Product + Architecture | Accepted scope, effectiveness under review | 2026-11-17 | Product owner.** On an eligible direct petition, the agent may ask for memories about the requester. An offer is not a recall; a recall is not proof that using a fact helped. Automatically inserting the observer's top two notes trades discretion for forced callbacks.

Reopen the retrieval design after owner review of actual opportunities, including cases where **not** mentioning a remembered fact was the right answer. Do not use a target tool-call rate as the success measure. [Recall binding](../../src/DiscordSky.Bot/Orchestration/Autonomy/WorldAutonomyRecallTool.cs); [runtime contract](../runtime_architecture.md).

## DS-004: Hybrid prompt caching has to earn promotion

**Experiment | Trial | 2026-11-17 | Maintainer; product owner reviews behavioral changes.** Compare the bounded stable-prefix breakpoint plus implicit history caching against implicit caching alone. Both cohorts keep the same full instructions and tool authority. A higher cache-hit rate can still cost more once cache writes are counted.

Review at least 30 first calls **per cohort**, within the same model and comparable prompt layout and route; also compare later-call reuse, errors, end-to-end cost, and outcomes. The runbook proposes a 15% input-cost improvement, not an automatic promotion. Stop the trial with cache mode `Off`; this retains ordinary implicit caching. Record a promote/continue/stop decision at the next qualifying review. [Protocol and rollback](../../k8s/discord-sky/README.md); [key construction](../../src/DiscordSky.Bot/Orchestration/Autonomy/WorldAutonomyPromptCache.cs).

## DS-005: A fresh start clears personal notes

**Product + Architecture | Accepted | 2026-11-23 | Product owner; maintainer implements.** `!sky forget-me` clears the requester's saved personal notes across the persona. Confirm only after deleting the stored notes. Serialize with extraction: work already applying a note finishes before erasure; old buffered evidence is discarded. Work from later messages may learn new notes. A failure is reported as a failure, not a fresh start.

This keeps the existing scope. It does not remove Discord posts, private transcripts, reaction logs, backups, or guild operation receipts. Retention is a separate policy, not an erasure receipt. A permanent learning opt-out or a wider purge would need its own product decision and coverage across stores. Reopen before extending to unrelated audiences. [Failure tests](../../tests/DiscordSky.Tests/FileBackedUserMemoryStoreTests.cs); [in-flight work](../../tests/DiscordSky.Tests/PerUserMemoryLockTests.cs).

## DS-006: Count observed effects at their destination

**Architecture | Accepted | 2026-11-23 | Maintainer.** A successful run, successful write, and a reply in the summoning channel are different facts. Retain confirmed Discord chunks if a later send fails. Credit native speech to the source channel only from a successful verified receipt at that destination. A deliberate post elsewhere remains an allowed action. Never repeat an uncertain write to establish whether it happened. Likewise a refused provider call is not a failed half-open probe, and cancellation after client dispatch is not proof of zero cost. Revisit if a provider or Discord supplies stronger receipts. [Delivery](../../tests/DiscordSky.Tests/WorldAutonomySpeechToolTests.cs); [provider leases](../../tests/DiscordSky.Tests/WorldAutonomyProviderCircuitTests.cs).

## DS-007: Supported runtime and a reproducible release gate

**Architecture | Accepted | 2026-11-23 | Maintainer.** Sky and Steward use .NET 10. SkiaSharp inspects image metadata under the existing format, byte and pixel limits; it replaces the advisory-bearing decoder without bypassing license checks. The optional local SQLite ledger uses system SQLite, not a bundled native SQLite copy. Production retains the file ledger. Reassess packages and base images at each upgrade: NuGet audit alone does not certify OS or native libraries.

Before Azure login in CI, validate the pinned Steward build, audited dependencies, full Release build and tests, including the named sibling integrations. A deployment is identified by the complete Sky git tree and Steward pin; registry digest and ready pod identify what is running. Keep one gateway writer. Reopen the gate only with equivalent evidence, or use a documented rollback to a known release. [Validation](../../scripts/validate-release.sh); [workflow](../../.github/workflows/deploy-aks.yml).

## DS-008: Trial one verified guild deed

**Experiment | Trial paused | 2026-11-23 | Maintainer measures; product owner judges.** The original trial offered at most one recent verified Steward effect to selected direct petitions. An offline fixture showed that a question naming one member could receive another member's nickname receipt in the same room. Room isolation worked; subject selection did not. Keep `WorldAutonomy:VerifiedDeeds:Percent=0`. Do not resume until an explicit member, role, or referenced decree matches the receipt and ambiguity yields no suggestion. No personal recall, native authority or cache policy changes with this pause.

The original expiry remains 2026-12-07 00:00 UTC, with no automatic restart, extension or promotion. Any successor still needs matching guild and source channel, a 48-hour age limit, no raw arguments or private notes, and human review. A historical receipt is not proof of current state. [Receipt selector](../../src/DiscordSky.Bot/Orchestration/Autonomy/WorldAutonomyVerifiedDeed.cs); [scope tests](../../tests/DiscordSky.Tests/WorldAutonomyLedgerTests.cs).