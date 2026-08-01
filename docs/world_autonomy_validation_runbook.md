# World Autonomy Validation Runbook

Status: AKS dark deployment, live provider smoke, direct R0 through R5 validation, and deployed hosted-search lifecycle recovery validation completed on an owner-approved disposable guild. A non-disposable guild remains disabled pending an explicit profile and binding change.

## Purpose

This runbook validates Discord Sky's unrestricted Discord autonomy in stages. The stages separate packaging and provider behavior from actual Discord mutations. Do not use a real friend server as the disposable-guild test environment.

The system is unrestricted only after an exact guild binding is configured. An empty `WorldAutonomy:EnabledGuilds` map keeps the bundled Steward executable inert: Sky does not start a guild MCP child and Robotnik receives no native Discord tools.

## Evidence Rules

- Never print, commit, or paste Discord or OpenAI credentials.
- Use an owner-approved disposable guild with only test accounts and test resources for mutation validation.
- Record image digest, profile digest, manifest digest, request IDs, and outcome status. Do not record token values.
- Keep raw Discord message exports and private validation transcripts outside tracked files.
- Roll back immediately if the real deployment becomes unhealthy. A dark rollout must preserve normal Sky reply behavior.

## Stage 1: Local Build And Artifact

Run the normal suites first:

```bash
dotnet test tests/DiscordSky.Tests/DiscordSky.Tests.csproj --no-restore
dotnet test ../discord-steward/tests/DiscordSteward.Tests/DiscordSteward.Tests.csproj --no-restore
```

Build a self-contained Steward artifact and validate its configuration-only probe:

```bash
dotnet publish ../discord-steward/src/DiscordSteward/DiscordSteward.csproj \
  -c Release -r linux-x64 --self-contained true \
  -o /tmp/discord-steward-validation

/tmp/discord-steward-validation/DiscordSteward --probe
```

The probe emits one JSON object with `status: "ready"`. It does not contact Discord or start an MCP server.

## Stage 2: Dark Deployment

Build and deploy Sky with the self-contained Steward bundle, but do not add any `WorldAutonomy__EnabledGuilds__...` configuration entries:

```bash
bash scripts/deploy.sh \
  --aks-resource-group <resource-group> \
  --aks-cluster <cluster> \
  --acr-name <acr-name> \
  --include-steward \
  --steward-project ../discord-steward/src/DiscordSteward/DiscordSteward.csproj
```

The deploy script runs `DiscordSteward --probe` before the image build. The production ConfigMap enables `WorldAutonomy__ValidateStewardOnStartup=true`, so Sky runs the same probe during startup. With no binding it validates the default child manifest. With a binding it additionally validates the exact guild ID, `UnrestrictedAutonomy` mode, and zero local policy protections from the selected profile. A failed probe prevents the pod from becoming ready.

Verify the rollout:

```bash
kubectl rollout status deployment/discord-sky-bot -n discord-sky
kubectl get pod -n discord-sky -l app=discord-sky-bot -o wide
kubectl logs -n discord-sky deploy/discord-sky-bot -c bot --since=15m --timestamps
kubectl top pod -n discord-sky
```

Required evidence:

- Pod is `1/1 Ready` with zero unexpected restarts.
- Logs include `Steward startup probe succeeded`.
- Logs include successful LLM auth and model-access checks.
- Logs include `Discord Sky bot started` and `Bot ready`.
- The effective ConfigMap contains no `WorldAutonomy__EnabledGuilds__...` keys.
- Normal reply behavior remains healthy after the rollout.

The first dark rollout exposed a dependency-injection defect in the autonomy ledger. The replacement pod crash-looped while the old pod remained available. The ledger now resolves through `IOptions<WorldAutonomyOptions>`, and a regression test covers the service graph. Treat an unhealthy new pod as a failed rollout, not as evidence that the old pod is safe to remove.

## Stage 3: Live Provider Smoke

The smoke tool proves the actual configured OpenAI model can search a deferred tool, pass MAF approval, invoke it once, and complete the run. The function is local and has no Discord side effect.

Obtain the existing OpenAI credential only in the child process environment. This command does not print the key:

```bash
OPENAI_API_KEY="$(kubectl get secret discord-sky-secrets -n discord-sky \
  -o jsonpath='{.data.LLM__Providers__OpenAI__ApiKey}' | base64 -d)" \
  dotnet run --project tools/DiscordSky.AutonomySmoke -- \
  --model <configured-autonomy-model> --timeout-seconds 180
```

Expected output:

```text
LIVE AUTONOMY SMOKE PASSED ... approvals=1 invocations=1
```

A provider error, timeout, zero invocation, or more than one invocation blocks the canary. Do not substitute a local fake endpoint for this stage.

## Stage 4: Disposable Guild Canary

Before enabling a real guild, prepare an owner-approved disposable guild:

- Use a dedicated bot identity or a permission-isolated test role.
- Add only test accounts and disposable channels, roles, messages, events, and AutoMod rules.
- Ensure the bot can perform the intended R0 through R5 operations but cannot affect a real community.
- Create a schema-4 `UnrestrictedAutonomy` profile with `toolAllowlist: ["*"]`, `Steward:JournalBackend` set to `File`, and journal and asset paths under `/app/data/user_memories/world-autonomy/<test-guild>/`. Use `Sqlite` only when the journal is on a local-disk filesystem rather than the Azure Files PVC.
- Add `Discord__BotToken` to the deployment Secret without printing it.
- Add exactly one `WorldAutonomy__EnabledGuilds__<test-guild-id>__ProfilePath` binding and, if needed, the model override.

Validate the child startup before sending a test message:

```bash
kubectl logs -n discord-sky deploy/discord-sky-bot -c bot --since=10m --timestamps
```

Expected child evidence:

- The Steward child reports `UnrestrictedAutonomy`.
- Its capability profile has zero local protection counts.
- The complete native catalog is discovered.
- Sky creates durable JSON run-ledger snapshots on the Azure Files PVC. The enabled Steward profile uses the file-backed operation journal on that same PVC, atomically replacing one complete snapshot for each state transition. The single-replica deployment uses `Recreate` so two revisions cannot update a per-guild journal concurrently.

## R0 Through R5 Matrix

Use request IDs and resource names unique to the test run. Record each operation's Sky run ID, Steward request ID, result, and observed Discord state.

The repository includes a controlled role-lifecycle harness that covers these tiers without modifying a member or an existing community resource. It creates one unique empty role, grants only `ViewChannel`, checks durable operation metadata, deletes that same role, and reconciles an unknown delete outcome if Discord applied it before the response became ambiguous:

```bash
Discord__BotToken="<dedicated-test-bot-token>" \
  dotnet run --project tools/DiscordSky.DisposableGuildCanary -- \
  --profile <private-schema-4-profile> \
  --guild-id <disposable-guild-id> \
  --steward-assembly <built-DiscordSteward.dll>
```

The utility preserves its local Steward journal evidence directory and prints its path, run ID, and request IDs. Inspect and retain that evidence before deleting the directory.

Validated on an owner-approved disposable guild on 2026-07-31: the R3 role create and R4 permission update were verified; the R5 delete reached Discord but initially returned `unknown`, then `reconcile_operation` resolved it to `succeeded` without replaying the delete. The generated role was absent after completion.

To validate Sky's hosted-tool-search path and durable run ledger inside the deployed container, copy the built `DiscordSky.WorldAutonomyCanary` output into a temporary pod directory and run it with a separate canary ledger path on the PVC. Its `read` mode exercises the production orchestrator, child supervisor, model, hosted search, and native read catalog. Its `role-lifecycle` mode additionally performs the isolated disposable role lifecycle through Sky's approval and dispatch ledger, verifies the corresponding Steward journal records, confirms role cleanup, then reopens the persisted Sky ledger through `WorldAutonomyRecoveryService` and requires each accepted dispatch to converge to `succeeded` without replay. Do not point this utility at the main `WorldAutonomy__LedgerPath` while the bot process is running.

Validated on 2026-07-31 in the deployed disposable profile: hosted search found the native catalog; the role lifecycle created one temporary role, changed it to only `ViewChannel`, deleted it, and left no generated role behind. The Azure Files file journal recorded all operation evidence and the fresh-ledger recovery pass promoted the three Sky dispatches from `accepted` to `succeeded` with `recovery_checked` events.

| Risk | Validate | Example disposable action |
| --- | --- | --- |
| R0 | Local and deterministic catalog behavior | `get_steward_capabilities`, asset registration, AutoMod simulation |
| R1 | Guild configuration reads | Get guild metadata, roles, channels, and current configuration snapshot |
| R2 | Sensitive or detailed reads | Read a bounded test message/member/operation record |
| R3 | Reversible configuration mutation | Create a channel, update its topic, then verify the observed state |
| R4 | Higher-impact mutation | Modify a test role or permission overwrite, send a test message, or apply a member change to a dedicated test account |
| R5 | Destructive mutation | Delete a test channel or role, delete a test message, or kick/ban a dedicated disposable test account |

Then run one multi-write autonomous session against the disposable guild. A good minimal campaign is: inspect state, create a test channel, update it, post a test message, inspect the resulting operation records, and delete the test channel. The model must choose tools through hosted search and Sky must persist each write before dispatch.

## Negative And Recovery Cases

Validate the cases that must fail safely without changing authority:

- A missing Discord permission fails before a write.
- A role-hierarchy or owner/self target fails because Discord forbids it.
- A malformed request ID is rejected before dispatch.
- A reused request ID does not repeat a write.
- An uncertain operation is reconciled through `get_operation` and `reconcile_operation`, never replayed.
- Restart the Sky pod during or immediately after a recorded dispatch, then confirm the ledger and Steward journal converge without a duplicate Discord write.

## Promotion And Rollback

Promote to a real guild only after an owner explicitly supplies and enables that guild's profile and exact binding. The dark rollout, live provider smoke, disposable R0 through R5 matrix, deployed multi-write session, and fresh-ledger recovery checks have been validated. Perform an actual pod-restart rehearsal around a recorded dispatch as the final operational check before enabling a non-disposable guild.

Rollback a failed deployment with:

```bash
kubectl rollout undo deployment/discord-sky-bot -n discord-sky
```

Disable a real autonomy binding by removing its exact `WorldAutonomy__EnabledGuilds__...` configuration entries and rolling the deployment. The bundled Steward executable remaining in the image grants no authority by itself.
