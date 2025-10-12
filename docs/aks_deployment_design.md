# Discord Sky Bot AKS Deployment Design

## Objective
Run the Discord Sky bot as a continuously available workload inside Azure Kubernetes Service (AKS), using Azure Container Registry (ACR) for image storage and Kubernetes primitives for configuration, secrets, and lifecycle management.

## Current Environment Snapshot
- **AKS Cluster**: `<AKS_CLUSTER_NAME>` in resource group `<AKS_RESOURCE_GROUP>` (region `<AKS_REGION>`), Kubernetes `<AKS_VERSION>`, autoscaler constrained to a single `Standard_D2s_v5` system node pool (`d2sv5`).
- **Namespaces**: Platform namespaces plus shared add-on namespaces (ingress, policy, cert-manager) and at least one application namespace; `discord-sky` will be created during deployment.
- **Azure Container Registry**: `<ACR_NAME>` (Basic SKU, admin disabled) with existing repositories; login server `<ACR_LOGIN_SERVER>`.
- **Authentication**: AKS cluster uses system-assigned managed identity already authorized to pull from the paired ACR.

> Store the real Azure subscription, resource group, cluster, and registry identifiers in a private, untracked operations note (for example `docs/private/azure_resources.local.md`) so you can substitute them for the placeholders below when running commands.

## Target Topology
- **Container Image**: Single .NET 8 worker container exposing no inbound ports; maintains outbound WebSocket connection to Discord and HTTPS calls to OpenAI.
- **Registry**: Azure Container Registry hosts versioned images (e.g., `discordskybot:<git-sha>`).
- **AKS Workload**: One replica `Deployment` in namespace `discord-sky`, backed by a `ClusterIP` `Service` (optional) and `Secret` for credentials.
- **Config**: Environment variables override `appsettings.json` values; no persistent volumes required.

## Prerequisites
1. Azure CLI (`az`) ≥ 2.51 and `kubectl` installed and logged in.
2. Existing Azure subscription and permission to create resources.
3. Discord bot token and OpenAI API key stored securely for later secret creation.
4. (Optional) Existing AKS cluster and resource group; otherwise plan to provision them.

## Containerization Strategy
1. Use the production-ready `Dockerfile` at the repo root (multi-stage build):
  ```dockerfile
  FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
  WORKDIR /src
  COPY DiscordSky.sln ./
  COPY src/DiscordSky.Bot/DiscordSky.Bot.csproj src/DiscordSky.Bot/
  COPY tests/DiscordSky.Tests/DiscordSky.Tests.csproj tests/DiscordSky.Tests/
  RUN dotnet restore DiscordSky.sln
  COPY . .
  RUN dotnet publish src/DiscordSky.Bot/DiscordSky.Bot.csproj -c Release -o /app/publish /p:UseAppHost=false

  FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
  WORKDIR /app
  COPY --from=build /app/publish .
  ENV DOTNET_EnableDiagnostics=0
  ENTRYPOINT ["dotnet", "DiscordSky.Bot.dll"]
  ```
  (See the checked-in `Dockerfile` for the complete multi-stage build used during image construction.)
2. Publish the app and build the image:
  ```bash
  dotnet publish src/DiscordSky.Bot/DiscordSky.Bot.csproj -c Release -o src/DiscordSky.Bot/bin/Release/net8.0/publish
  az acr build --registry <ACR_NAME> --image discordskybot:<tag> .
  ```
   > Alternatively, use `docker build` + `docker push` if local Docker is preferred.

## Azure Setup Workflow
1. Authenticate and select subscription:
   ```bash
   az login
  az account set --subscription <AZURE_SUBSCRIPTION_ID>
   ```
2. Create or reuse resource group:
   ```bash
  az group show --name <AKS_RESOURCE_GROUP> || \
  az group create --name <AKS_RESOURCE_GROUP> --location <AKS_REGION>
   ```
3. Provision ACR (skip if one exists):
   ```bash
  az acr show --resource-group <ACR_RESOURCE_GROUP> --name <ACR_NAME>
   ```
4. Provision AKS (skip if already created). Enable managed identity and attach ACR pull rights:
   ```bash
  az aks show --resource-group <AKS_RESOURCE_GROUP> --name <AKS_CLUSTER_NAME>
  az aks check-acr --name <AKS_CLUSTER_NAME> --resource-group <AKS_RESOURCE_GROUP> --acr <ACR_NAME>
   ```
5. Fetch cluster credentials:
   ```bash
  az aks get-credentials --resource-group <AKS_RESOURCE_GROUP> --name <AKS_CLUSTER_NAME> --overwrite-existing
   ```

## Kubernetes Configuration
The repository now includes Kubernetes manifests in `k8s/discord-sky/`:

1. Copy `secret.template.yaml` to `secret.yaml` (gitignored) and populate the required values, then add it to `kustomization.yaml` or apply it separately:
  ```bash
  cp k8s/discord-sky/secret.template.yaml k8s/discord-sky/secret.yaml
  # edit k8s/discord-sky/secret.yaml with real secrets
  ```
2. Apply the base manifests (namespace, config map, deployment) via kustomize:
  ```bash
  kubectl apply -k k8s/discord-sky/
  ```

## Deployment Manifests
Example `k8s/discord-sky/deployment.yaml`:
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: discord-sky-bot
  namespace: discord-sky
spec:
  replicas: 1
  selector:
    matchLabels:
      app: discord-sky-bot
  template:
    metadata:
      labels:
        app: discord-sky-bot
    spec:
      containers:
        - name: bot
          image: <ACR_LOGIN_SERVER>/discordskybot:<tag>
          imagePullPolicy: IfNotPresent
          envFrom:
            - secretRef:
                name: discord-sky-secrets
            - configMapRef:
                name: discord-sky-config
          resources:
            requests:
              cpu: 100m
              memory: 256Mi
            limits:
              cpu: 250m
              memory: 512Mi
      restartPolicy: Always
```
Apply manifests:
```bash
kubectl apply -k k8s/discord-sky/
```
(Add a `Service` only if future HTTP ingress is needed.)

## Operations & Observability
- Validate rollout: `kubectl get pods -n discord-sky`, `kubectl logs deployment/discord-sky-bot -n discord-sky`.
- Roll updates by tagging a new image and reapplying the deployment: `kubectl set image deployment/discord-sky-bot bot=<ACR_LOGIN_SERVER>/discordskybot:<newTag> -n discord-sky`.
- Configure Azure Monitor Container Insights for cluster-level metrics if desired.

## Security & Secrets
- Rotate Discord/OpenAI credentials by updating the Kubernetes secret and forcing a rollout: `kubectl rollout restart deployment/discord-sky-bot -n discord-sky`.
- Use Azure Key Vault + CSI Secret Store driver for production-grade secret management if compliance requires.

## Rollback Strategy
- Keep previous image tags; revert with `kubectl set image deployment/discord-sky-bot bot=<ACR_LOGIN_SERVER>/discordskybot:<oldTag> -n discord-sky`.
- If deployment fails, use `kubectl rollout undo deployment/discord-sky-bot -n discord-sky`.

## Next Steps
- Automate build + deploy in CI (GitHub Actions/Azure DevOps) using `az acr build` and `kubectl apply`.
- Add health probes and readiness scripts once the bot exposes HTTP endpoints for diagnostics.
- Evaluate Horizontal Pod Autoscaler if concurrent workloads grow.
