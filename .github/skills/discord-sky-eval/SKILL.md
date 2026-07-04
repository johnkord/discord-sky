---
name: discord-sky-eval
description: "Orchestration playbook for evaluating and tuning Discord Sky's proactive humor (cold opens, ambient interjections, reactions) offline, with the human as the judge of what is funny. Use when running an eval round, judging or scoring the bot's cold opens or jokes, deciding whether a line is funny and why, tuning the cold-open or worth prompt, building or running the ScenarioLab harness, calibrating the judge against the owner's taste, or reviewing shadow cold-open output. The session orchestrates (authors scenarios, runs the real components, judges the checkable axes, pre-filters, assembles a blind pairwise humor queue, drafts prompt edits, reports metrics) and stops to ask the human for the funny verdicts and any sign-off. Triggers: eval round, judge the bot, is this funny, score cold opens, rate the jokes, humor eval, tune cold-open prompt, scenario lab, why isn't this funny, calibrate the judge, welcome decay, derailing."
---

# Discord Sky Eval: orchestrating the humor eval loop

This skill tells an agent how to drive the offline eval-and-tune loop for Robotnik's proactive
speech. The full design and its research grounding live in
`docs/scenario_eval_harness_design_2026-07-04.md`; read it for the why. To read real bot output,
reactions, and telemetry, use the `discord-sky-ops` skill. This skill is the how, plus the one thing
that matters most: what the agent may decide on its own versus what it must hand to the human.

The bot is Robotnik (Adventures of Sonic the Hedgehog persona). Its most important quality is being
funny, and LLM judges are documented to be weak at judging humor. So the eval is built around a human
oracle for funniness, with the agent orchestrating everything around that judgment.

---

## Read this first: who judges what

Route every criterion to the cheapest sufficient judge. Never ask a judge to rule on an axis it is
bad at.

| Axis or criterion | Judge | Notes |
|---|---|---|
| Timing gate (silence, cooldown, quiet hours) | deterministic code | already unit-tested |
| In character, reads-the-room, non-sequitur, novelty, safe | the session (LLM) | pattern-checkable; the session also pre-filters |
| Worth score and timing quality | the session, calibrated on real reactions | a calibration question, not taste |
| **Funny, and why it lands or flops** | **the human (offline); real reactions (long-run root)** | the LLM cannot; this is the point |

Offline, before a line is posted, there are no reactions yet, so the human is the only humor signal
that exists. Real reactions are the root truth everything else is checked against; the harness is only
ever a fast proxy, never the target.

---

## The autonomy contract

This is the heart of the skill. Do the first list without asking. Stop and ask the human for the
second list. When in genuine doubt, escalate rather than guess.

### Do these autonomously (get everything in front of the human to judge)

- Author or pull scenarios: real transcript snapshots (via `discord-sky-ops`) plus synthetic edge and
  adversarial cases (heavy moment, hostile user, near-dead channel, scam post, strong-lore topic,
  no-lore topic). Prefer real snapshots for headline numbers.
- Run the real bot components (or the ScenarioLab harness once it exists). Never mock them.
- Judge the checkable axes and pre-filter: set aside off-character, unsafe, or non-sequitur outputs so
  they never reach the human. Only in-character, safe, plausibly-funny survivors go to the queue.
- Assemble a blind pairwise humor queue (survivors only; hide which line is the new candidate; hide the
  session's own opinion until after the human answers).
- Compute and report metrics: human-judged pairwise win-rate, failure-mechanism-tag histogram,
  judge-human agreement (Kendall tau), worth calibration.
- Draft candidate prompt edits (Contrastive-Reflection: error-anchored, targeted) and validate them on
  held-out scenarios before proposing.
- Read the why-corpus at the start of every round; append to it after.

### Always stop and ask the human

- The funny verdicts themselves. Which line is funnier, and why. Never fabricate the human's axis. If
  reactions do not yet exist for a line, the human is the only source of truth for funny.
- Sign-off before any prompt or feature change ships to the deployed bot.
- Anything that would post to Discord or change live behavior. The harness is offline only. Going live
  (for example `ColdOpen__ShadowMode=false`, or raising `Chaos__AmbientWorthThreshold`) is the owner's
  call, per the design.
- Changing the rubric or the definition of the standard. The human owns what matters.
- Low judge-human agreement, or a genuinely ambiguous call. Escalate; do not paper over it.
- Storing real friends' messages. Confirm the privacy stance before persisting anything with real text.

---

## Execution model: run it locally, not in the pod

Run the harness locally in the repo (dotnet run), calling the real bot components through a project
reference. Do NOT run the bot under test via kubectl inside the AKS pod. Testing official code is a
code-reference property, not a location one: fidelity comes from CALLING the real compiled components,
not from where the process runs.

Why not in the pod:

- The deployed bot is a long-running worker driven by live Discord events and timers, not a
  scenario-in, output-out harness. Evaluating through it means injecting synthetic traffic or exec-ing
  a separate process in the pod: invasive, non-reproducible, and it risks actually posting to Discord.
- In-pod eval shares the process, API key, rate limits, telemetry, and gateway with the live bot, which
  breaks the offline-only guardrail.
- The whole value is dozens of outputs in minutes. An in-pod loop is edit, rebuild image, push,
  redeploy, exec: the slow loop the harness exists to escape.
- Controlled A/B (baseline versus candidate prompt), N-run variance, and blind pairwise all need the
  harness to control the inputs. The live pod runs on live state you cannot freeze or replay.

You already have the in-cluster, official, end-to-end eval: it is SHADOW MODE. `ColdOpen__ShadowMode=true`
runs the real deployed bot, real config, real live state, the full gate-to-compose pipeline, and logs
what it would post without posting, grounded by real reactions. The local harness is the complementary
tier shadow mode cannot be: fast, controlled, counterfactual, and runnable before you deploy. Use
kubectl (the `discord-sky-ops` skill) to READ ground truth out of the cluster (real transcripts for
fixtures, real reactions for grounding), never to RUN the bot under test.

Fidelity rules for the local harness (so it never tests diverged code):

- Call bot code; never reimplement it. Construct and invoke real components, but do not restate their
  prompts or logic. If a check needs the prompt, call the real builder (for example
  `ColdOpenComposer.BuildSystemPrompt`), never a copy.
- Load the bot's real config, do not hardcode. ScenarioLab binds `LlmOptions` from
  `src/DiscordSky.Bot/appsettings.json` (plus env) and builds the `IChatClient` the same way
  `Program.cs` does (endpoint plus the Responses-versus-Chat branch).
- Stamp every run with the git SHA it built from (ScenarioLab prints it; a dirty tree is flagged).
  Because we deploy after every change, that SHA is the deployed bot, so each eval carries a mechanical
  "official code at <sha>" proof, not a trusted assumption. A dirty-tree eval is a draft, not a verdict
  on shippable code.

---

## The eval round (steps)

The `tools/DiscordSky.ScenarioLab` console tool exists for cold opens (Phase 0): it runs the real
ColdOpenComposer over scenario fixtures and dumps output. Run it LOCALLY (see the execution-model
section above):
`OPENAI_API_KEY=... dotnet run --project tools/DiscordSky.ScenarioLab -- tools/DiscordSky.ScenarioLab/fixtures/coldopen-scenarios.json [--runs 3] [--json]`.
Steps marked [provisional] cover what the tool does not yet drive (the ambient worth and reaction
behaviors, and the pairwise-queue plumbing).

1. Read the why-corpus and the rubric so this round judges by the same standard as the last.
2. Assemble scenarios (real snapshots plus synthetic edge cases).
3. Run the harness (ScenarioLab for cold opens) to emit the bot's raw output per scenario (line, worth,
   hook, or decline). No judgment in the tool; it stamps each run with the bot source SHA.
4. Judge the checkable axes; pre-filter the obviously-broken.
5. Build the blind pairwise humor queue from the survivors, slipping in the occasional real human line
   from the same moment (the equal-severity check) and the occasional repeat (self-consistency).
6. Present the queue to the human. Collect funnier-or-not plus mechanism tags plus optional text.
7. Append every human call to the why-corpus.
8. Draft a targeted prompt edit from the failure reasons; validate pairwise on held-out scenarios.
9. Report metrics and the proposed edit. Ship only with human sign-off, then follow the normal
   build-test-deploy path.

---

## Human elicitation protocol

- Pairwise, not absolute scoring. "Which is funnier, A or B, and one reason." Pairwise is steadier for
  humor and far lighter for the human.
- Blind. Hide the source and the session's opinion until after the human answers, so the human is not
  anchored into agreeing with the machine.
- Structured why, grounded in comedic mechanism, so the reason is captured without an essay:
  - Worked: incongruity or script opposition, escalation, callback, absurd specificity, perfect
    target, timing.
  - Failed: too generic, no shared context, non-sequitur, try-hard or explaining the joke, mean rather
    than playful, wrong target, stale bit.
- Keep the queue short. The human's attention is the scarcest resource; the pre-filter exists to spend
  it only on plausibly-funny survivors.

---

## The why-corpus (the anti-drift artifact)

Every human humor call (verdict plus mechanism tags plus any note, keyed to the scenario and the line)
is saved. Over rounds it becomes a server-specific theory of what is funny here. It is the teacher
signal for humor tuning, it bootstraps the pre-filter toward the owner's taste, it lets a future
session inherit the standard instead of drifting, and it is itself checked against real reactions once
lines ship.

- Proposed home: `docs/eval/` (create on first use). The why-corpus references real lines, so keep it
  local and gitignored per the privacy stance; the rubric (no PII) may be tracked once stable. Confirm
  with the owner before persisting real text.
- Read it first each round; append after. Never overwrite a past verdict; the history is the point.

---

## Guardrails and conventions

- Real reactions are the untouchable holdout. Do not tune against the harness so hard that a change
  looks good offline but has never faced real reactions. A prompt that only pleases the offline judge
  still has to prove itself live.
- Offline only. This loop never posts to Discord and never changes deployed config on its own.
- Design and plan docs under `docs/` are UNTRACKED (never `git add`) and must be ASCII-clean: no
  em-dash, en-dash, curly quotes, or ellipsis character. Validate with
  `grep -nP '[\x{2014}\x{2013}\x{2018}\x{2019}\x{201C}\x{201D}\x{2026}]' <file>`.
- `python3` is local only, not in the container. Read real reactions and telemetry via
  `discord-sky-ops` (kubectl, PVC JSONL, or Discord REST with the bot token; never print the token).
- If a tuned prompt ships, it rides the normal path: build, run tests, then
  `bash scripts/deploy.sh ...` (see `discord-sky-ops` and repo memory for the exact invocation). The
  agent commits and deploys but does not `git push` unless asked.

---

## Status

Phase 0 (the ScenarioLab tool) is built for cold opens, and it loads real config and stamps the bot
source SHA; the loop has not yet run against a live key. The rest stays lean and provisional and firms
up as real rounds teach us. Treat the skill as living: update it from what a real round teaches, the
same way the design treats the eval itself as a living thing.
