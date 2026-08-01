# Discord Sky Kubernetes Manifests

This directory contains the Kubernetes resources required to run the Discord Sky bot on AKS.

> Keep the actual Azure resource identifiers (registry login server, cluster name, etc.) in a private, untracked document so you can substitute them for the placeholders referenced below.

## Files

- `namespace.yaml` – creates the `discord-sky` namespace.
- `configmap.yaml` – non-secret configuration overrides for the bot.
- `pvc.yaml` – Azure Files state storage for Sky snapshots, memories, assets, and file-backed Steward journals.
- `secret.template.yaml` – reference of the secret keys the bot expects. The live `discord-sky-secrets` Secret is managed imperatively in the cluster (see below) and is intentionally not part of `kustomization.yaml`, so `scripts/deploy.sh` can never overwrite it.
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

This bundles the executable at `/app/steward/DiscordSteward` and copies each profile to
`/app/steward/profiles/<guild-id>.json`. Deployment rejects duplicate guild IDs and any shared journal,
asset, inbox, or webhook-vault path. Before enabling a guild on the Azure Files PVC, create a real schema-4
profile with `Steward:JournalBackend` set to `File` and keep all durable paths under a unique
`/app/data/user_memories/world-autonomy/<guild-id>/steward` subtree. The file backend atomically replaces a
complete journal snapshot, which is compatible with the Azure Files mount; keep `Sqlite` for deployments
backed by a local disk filesystem. The single-replica deployment uses `Recreate` so old and new pods cannot
update a journal snapshot concurrently. The deploy script injects each exact binding into its temporary
ConfigMap copy; the model falls back to the active Main workload profile unless a private environment
override is supplied:

```text
WorldAutonomy__EnabledGuilds__<exact-guild-id>__ProfilePath=/app/steward/profiles/<exact-guild-id>.json
```

The deployment Secret needs `Discord__BotToken` in addition to `Bot__Token`, because Steward runs as a
separate inherited-configuration child process. Do not enable a binding before the live hosted-tool-search
smoke and disposable-guild R0 through R5 validation have passed.

The default dark deployment sets `WorldAutonomy__ValidateStewardOnStartup=true`, which runs
`/app/steward/DiscordSteward --probe` during Sky startup. It validates the bundled executable and its
default immutable manifest without starting an MCP server, contacting Discord, or enabling any guild
binding. A probe failure prevents the pod from becoming ready.

See [the autonomy validation runbook](../../docs/world_autonomy_validation_runbook.md) for the required
dark-rollout evidence, live provider smoke, disposable-guild R0 through R5 matrix, recovery checks, and
promotion criteria.
