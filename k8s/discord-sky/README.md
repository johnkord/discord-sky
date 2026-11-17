# Discord Sky Kubernetes Manifests

This directory contains the Kubernetes resources required to run the Discord Sky bot on AKS.

> Keep the actual Azure resource identifiers (registry login server, cluster name, etc.) in a private, untracked document so you can substitute them for the placeholders referenced below.

## Files

- `namespace.yaml` – creates the `discord-sky` namespace.
- `configmap.yaml` – non-secret configuration overrides for the bot.
- `pvc.yaml` – Azure Files state storage for Sky snapshots, memories, assets, and file-backed Steward journals.
- `secret.template.yaml` – reference of the credential keys the bot expects. The live `discord-sky-secrets` Secret is managed imperatively and is never overwritten by `scripts/deploy.sh`.
- `deployment.yaml` – deploys the bot container image from `<ACR_LOGIN_SERVER>`.
- `kustomization.yaml` – allows quick deployment via `kubectl apply -k`.

## Usage

1. Create the cluster Secret once (no file on disk). Example for all three keys:
   ```bash
   kubectl create secret generic discord-sky-secrets \
     --namespace discord-sky \
     --from-literal=Bot__Token='...' \
     --from-literal=LLM__ActiveProvider='OpenAI' \
     --from-literal=LLM__Providers__OpenAI__ApiKey='...' \
     --from-literal=LLM__Providers__xAI__ApiKey='...' \
     --dry-run=client -o yaml | kubectl apply -f -
   ```
   To rotate a single value later without touching the others:
   ```bash
   read -s OPENAI_KEY
   kubectl patch secret discord-sky-secrets -n discord-sky --type merge \
     -p "{\"stringData\":{\"LLM__Providers__OpenAI__ApiKey\":\"$OPENAI_KEY\"}}"
   unset OPENAI_KEY
   kubectl rollout restart deploy/discord-sky-bot -n discord-sky
   ```

2. For production, commit the validated source and use `bash scripts/deploy.sh` or the serialized GitHub Actions
   workflow. The script renders this Kustomize tree, validates private autonomy resources, builds/probes the combined
   Sky plus Steward image, applies the resources, waits for health, and restores the prior revision on failure.

3. Use raw `kubectl apply -k` only for client-side validation or deliberate dark/local experiments. Do not use
   `kubectl set image` as the normal production path; it bypasses combined-runtime and private-binding checks.

## Chat Provider

Production chat uses `LLM__ActiveProvider=OpenAI` and `gpt-6-astra` for all text workloads. Keep
`LLM__Providers__OpenAI__ApiKey` in the existing Secret. The ConfigMap follows the Secret in `envFrom`, so the
committed active-provider setting takes precedence over any legacy Secret entry. Images keep the same OpenAI
key and Flare/Sunburst models. No credential rotation is needed for the provider switch.

Astra uses Responses with `UseHostedToolSearch=true`. The inactive Grok profile retains direct tool exposure
because that account's hosted search is alpha-gated. Its continuation adapter omits repeated instructions on
linked response turns; that adapter does not affect OpenAI. All shared reply/approval/recovery fixes remain active.
Verify private autonomy/persona model overrides, startup routing and model-access logs, then `llm_call` records
with `provider=OpenAI` and `model=gpt-6-astra`. `/healthz` alone does not prove provider inference works.

The harmless live smoke accepts `--provider xAI --model grok-4.7 --effort ExtraHigh`; supply `XAI_API_KEY` through
the environment, never on the command line. Optional `--steward-assembly` and `--steward-profile` point to a local
Steward binary and example profile for a full-catalog schema check. Native tools are sent as non-executable
declarations, and only one fixed local validation function runs. Do not use a production profile for this probe.
Use `--provider OpenAI --model gpt-6-astra --effort ExtraHigh --hosted-tool-search true` for the current production
profile, supplying `OPENAI_API_KEY` through the environment.

## Images

Image generation uses Flare for new pictures and Sunburst for edits, with one in-flight render, bounded reference
downloads, and one preview by default. The existing provider hourly/daily limits remain unchanged. Inspect private
image records for request IDs, `cost_basis`, token usage, reference counts, and structured failures before retrying.
Both image model IDs must be accessible to the configured OpenAI account. A successful `/healthz` does not prove
image API billing or generation works. The current controls and guarded live smoke procedure are in
[../../docs/image_generation_2_5.md](../../docs/image_generation_2_5.md).

## Tool Failure Diagnostics

The ordinary creative path requires native tool calls. A log with `tool_envelope=true` means its text fallback
recognized an internal tool envelope; `recovered_text=true` indicates the validated send text was recovered.
It never executes arbitrary tools found in that text, and explicit reply ownership remains host-controlled.

Mixed read/write batches are covered by tests through the real agent framework with both terminal modes and
both tool orderings. A successful single-tool provider smoke does not cover these approval interactions.

In the private autonomy ledger, `run_ended_before_invocation` is a failed approval that never reached a tool.
`steward_operation_missing` is different: the receipt is absent and the outcome remains unknown. These unknown
records stop automatic lookups and require operator review; do not replay the original mutation to find out
what happened. Other recovery errors retain normal retries and identity validation. Use the request ID to
inspect Steward's journal and Discord state before making a deliberate corrective operation.

AngleSharp is patched to 1.5.0. ImageSharp 3.1.12 still has known security advisories; its patched 3.2 line adds a
Release-build license requirement. Before upgrading it, choose an eligible licensed deployment or a replacement
parser. Do not disable the vendor's license validation. Existing bounded Discord-only reference downloads and
metadata limits remain in place, but are not a substitute for fixing the dependency advisories.

## Unrestricted Steward Child

World autonomy is disabled until the deployment configuration contains an exact guild binding. To build an
image that can run one isolated child per guild, publish the sibling Steward project and repeat
`--steward-profile` for every exact-guild profile:

```bash
bash scripts/deploy.sh ... \
   --include-steward \
   --steward-project ../discord-steward/src/DiscordSteward/DiscordSteward.csproj \
   --steward-profile config/world-autonomy/guild-111111111111111111.json \
   --steward-profile config/world-autonomy/guild-222222222222222222.json
```

This bundles only the executable at `/app/steward/DiscordSteward`. The deploy script validates each private
profile locally, writes it to a temporary `discord-sky-steward-profiles` Secret overlay, and mounts that Secret
read-only at `/app/steward/profiles`. Profile bytes never enter a container layer or the public manifest tree.
Deployment rejects duplicate guild IDs and any shared journal,
asset, inbox, or webhook-vault path. Before enabling a guild on the Azure Files PVC, create a real schema-4
profile with `Steward:JournalBackend` set to `File` and keep all durable paths under a unique
`/app/data/user_memories/world-autonomy/<guild-id>/steward` subtree. The file backend atomically replaces a
complete journal snapshot, which is compatible with the Azure Files mount; keep `Sqlite` for deployments
backed by a local disk filesystem. The single-replica deployment uses `Recreate` so old and new pods cannot
update a journal snapshot concurrently. The deploy script writes exact bindings to a separate temporary
`discord-sky-autonomy-bindings` ConfigMap. The public Deployment references both private resources optionally,
so dark deployments remain valid without publishing private guild IDs. An enabled binding still fails closed if
its matching `<guild-id>.json` Secret key is missing. The model falls back to the active Main
workload profile unless a private environment override is supplied:

```text
WorldAutonomy__EnabledGuilds__<exact-guild-id>__ProfilePath=/app/steward/profiles/<exact-guild-id>.json
```

The deployment Secret needs `Discord__BotToken` in addition to `Bot__Token`, because Steward runs as a
separate inherited-configuration child process. Do not enable a binding before the live provider/tool-loop
smoke and disposable-guild R0 through R5 validation have passed.

Automated production deployment checks out a pinned public Discord Steward revision and calls the same deploy
script with `--preserve-steward-profiles`. That mode requires every existing private binding to have a matching
Secret key before applying a new Deployment, verifies the image contains the executable and no profiles, and
restores the prior revision and private resources if rollout fails.

The default dark deployment sets `WorldAutonomy__ValidateStewardOnStartup=true`, which runs
`/app/steward/DiscordSteward --probe` during Sky startup. It validates the bundled executable and its
default immutable manifest without starting an MCP server, contacting Discord, or enabling any guild
binding. A probe failure prevents the pod from becoming ready.

## Private Proactive Target Bindings

Cold-open targets may be bound to exact Discord resource IDs without placing those IDs in this repository. Create
the optional ConfigMap out of band, providing `GuildId` and `ChannelId` together. Names are optional display labels
and backward-compatible fallback values; when IDs are present, renames do not affect resolution.

```bash
kubectl create configmap discord-sky-runtime-bindings \
   --namespace discord-sky \
   --from-literal=ColdOpen__Channels__0__GuildId='<exact-guild-id>' \
   --from-literal=ColdOpen__Channels__0__ChannelId='<exact-channel-id>' \
   --from-literal=ColdOpen__Channels__0__Guild='<display-label>' \
   --from-literal=ColdOpen__Channels__0__Channel='<display-label>' \
   --dry-run=client -o yaml | kubectl apply -f -
```

The public Deployment mounts this ConfigMap optionally, and `scripts/deploy.sh` validates its key allow-list and
ID pairs before rollout. The same private ConfigMap may contain rename-proof guild disable IDs:

```text
Bot__DisabledGuildIds__0=<exact-guild-id>
```

Keep readable disable names and behavior policy such as `ColdOpen__Enabled` in the public policy ConfigMap. Private
IDs reinforce identity across renames without publishing guild snowflakes. Migrate or restore cold-open targets in
shadow mode and review opportunities before enabling live posting.

See [the autonomy validation runbook](../../docs/world_autonomy_validation_runbook.md) for the required
dark-rollout evidence, live provider smoke, disposable-guild R0 through R5 matrix, recovery checks, and
promotion criteria.
