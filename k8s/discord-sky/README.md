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

2. Deploy the stack:
   ```bash
   kubectl apply -k .
   ```
3. Update the deployment with a new image tag (substitute the actual login server stored in your private ops note):
   ```bash
   kubectl set image deployment/discord-sky-bot bot=<ACR_LOGIN_SERVER>/discordskybot:<tag> -n discord-sky
   ```

## Unrestricted Steward Child

World autonomy is disabled until the deployment configuration contains an exact guild binding. To build an
image that can run one isolated child per guild, publish the sibling Steward project and repeat
`--steward-profile` for every exact-guild profile:

```bash
scripts/deploy.sh ... \
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
separate inherited-configuration child process. Do not enable a binding before the live hosted-tool-search
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
ID pairs before rollout. Keep behavior policy such as `ColdOpen__Enabled` and `ColdOpen__ShadowMode` in the public
policy ConfigMap; the private binding ConfigMap cannot override those settings. Migrate or restore targets in
shadow mode and review opportunities before enabling live posting.

See [the autonomy validation runbook](../../docs/world_autonomy_validation_runbook.md) for the required
dark-rollout evidence, live provider smoke, disposable-guild R0 through R5 matrix, recovery checks, and
promotion criteria.
