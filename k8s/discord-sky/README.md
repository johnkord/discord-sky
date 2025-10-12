# Discord Sky Kubernetes Manifests

This directory contains the Kubernetes resources required to run the Discord Sky bot on AKS.

> Keep the actual Azure resource identifiers (registry login server, cluster name, etc.) in a private, untracked document so you can substitute them for the placeholders referenced below.

## Files

- `namespace.yaml` – creates the `discord-sky` namespace.
- `configmap.yaml` – non-secret configuration overrides for the bot.
- `secret.template.yaml` – template for required secrets; copy to `secret.yaml` and replace placeholder values before applying.
- `deployment.yaml` – deploys the bot container image from `<ACR_LOGIN_SERVER>`.
- `kustomization.yaml` – allows quick deployment via `kubectl apply -k`.

## Usage

1. Copy the secret template and fill in secrets (the generated `secret.yaml` is gitignored so it stays local):
   ```bash
   cp secret.template.yaml secret.yaml
   # edit secret.yaml with real values
   ```
   Add `secret.yaml` to `kustomization.yaml` or apply it separately with `kubectl apply -f secret.yaml`.
2. Deploy the stack:
   ```bash
   kubectl apply -k .
   ```
3. Update the deployment with a new image tag (substitute the actual login server stored in your private ops note):
   ```bash
   kubectl set image deployment/discord-sky-bot bot=<ACR_LOGIN_SERVER>/discordskybot:<tag> -n discord-sky
   ```
