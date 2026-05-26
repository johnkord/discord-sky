# Discord Sky Kubernetes Manifests

This directory contains the Kubernetes resources required to run the Discord Sky bot on AKS.

> Keep the actual Azure resource identifiers (registry login server, cluster name, etc.) in a private, untracked document so you can substitute them for the placeholders referenced below.

## Files

- `namespace.yaml` – creates the `discord-sky` namespace.
- `configmap.yaml` – non-secret configuration overrides for the bot.
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
