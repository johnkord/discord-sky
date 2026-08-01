#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/deploy.sh [options]

Builds the Discord Sky bot, builds and pushes a container image to ACR, and rolls out the update to an AKS cluster.

Required options:
  --aks-resource-group <name>   Resource group containing the AKS cluster.
  --aks-cluster <name>          Name of the AKS cluster.
  --acr-name <name>             Azure Container Registry name (without .azurecr.io).

Optional:
  --subscription-id <id>        Azure subscription to target. Uses current subscription if omitted.
  --acr-resource-group <name>   Resource group containing the ACR (defaults to the AKS resource group).
  --image-name <name>           Container repository name (default: discordskybot).
  --image-tag <tag>             Image tag (default: current git commit or timestamp).
  --project <path>              Path to the .csproj to build (default: src/DiscordSky.Bot/DiscordSky.Bot.csproj).
  --include-steward             Publish and include a self-contained Discord Steward child executable.
  --steward-project <path>      Path to DiscordSteward.csproj (required with --include-steward).
  --steward-profile <path>      Repeatable schema-4 profile. Bundled as /app/steward/profiles/<guild-id>.json.
  --build-configuration <cfg>   dotnet build configuration (default: Release).
  --dockerfile <path>           Dockerfile path (default: Dockerfile in repo root).
  --k8s-dir <path>              Kubernetes manifest directory (default: k8s/discord-sky).
  --skip-build                  Skip dotnet build step.
  --skip-rollout                Skip kubectl apply/rollout steps (build and push only).
  --help                        Show this help message and exit.

Environment prerequisites:
  - az CLI, docker, dotnet, kubectl must be installed.
  - You must be logged into Azure (az login).
  - The discord-sky-secrets Secret must already exist in the discord-sky namespace.
    It is managed out-of-band via `kubectl patch secret` / `kubectl create secret`
    and is intentionally not part of this deploy.
EOF
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

SUBSCRIPTION_ID=""
AKS_RESOURCE_GROUP=""
AKS_CLUSTER=""
ACR_NAME=""
ACR_RESOURCE_GROUP=""
IMAGE_NAME="discordskybot"
IMAGE_TAG=""
PROJECT="src/DiscordSky.Bot/DiscordSky.Bot.csproj"
INCLUDE_STEWARD=0
STEWARD_PROJECT=""
STEWARD_PROFILES=()
PACKAGED_STEWARD_GUILDS=()
BUILD_CONFIGURATION="Release"
DOCKERFILE="${REPO_ROOT}/Dockerfile"
K8S_DIR="k8s/discord-sky"
SKIP_BUILD=0
SKIP_ROLLOUT=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --subscription-id)
      SUBSCRIPTION_ID="$2"; shift 2 ;;
    --aks-resource-group)
      AKS_RESOURCE_GROUP="$2"; shift 2 ;;
    --aks-cluster)
      AKS_CLUSTER="$2"; shift 2 ;;
    --acr-name)
      ACR_NAME="$2"; shift 2 ;;
    --acr-resource-group)
      ACR_RESOURCE_GROUP="$2"; shift 2 ;;
    --image-name)
      IMAGE_NAME="$2"; shift 2 ;;
    --image-tag)
      IMAGE_TAG="$2"; shift 2 ;;
    --project)
      PROJECT="$2"; shift 2 ;;
    --include-steward)
      INCLUDE_STEWARD=1; shift ;;
    --steward-project)
      STEWARD_PROJECT="$2"; shift 2 ;;
    --steward-profile)
      STEWARD_PROFILES+=("$2"); shift 2 ;;
    --build-configuration)
      BUILD_CONFIGURATION="$2"; shift 2 ;;
    --dockerfile)
      DOCKERFILE="$2"; shift 2 ;;
    --k8s-dir)
      K8S_DIR="$2"; shift 2 ;;
    --skip-build)
      SKIP_BUILD=1; shift ;;
    --skip-rollout)
      SKIP_ROLLOUT=1; shift ;;
    --help)
      usage; exit 0 ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1 ;;
  esac
done

if [[ -z "$AKS_RESOURCE_GROUP" || -z "$AKS_CLUSTER" || -z "$ACR_NAME" ]]; then
  echo "Error: --aks-resource-group, --aks-cluster, and --acr-name are required." >&2
  echo >&2
  usage >&2
  exit 1
fi

if [[ -z "$ACR_RESOURCE_GROUP" ]]; then
  ACR_RESOURCE_GROUP="$AKS_RESOURCE_GROUP"
fi

if [[ -z "$IMAGE_TAG" ]]; then
  if command -v git &>/dev/null; then
    IMAGE_TAG="$(cd "$REPO_ROOT" && git rev-parse --short HEAD)"
  else
    IMAGE_TAG="$(date +%Y%m%d%H%M%S)"
  fi
fi

if [[ ! -f "$DOCKERFILE" ]]; then
  echo "Dockerfile not found at $DOCKERFILE" >&2
  exit 1
fi

PROJECT_PATH="$REPO_ROOT/${PROJECT#./}"
if [[ ! -f "$PROJECT_PATH" ]]; then
  echo "Project file not found at $PROJECT_PATH" >&2
  exit 1
fi

K8S_PATH="$REPO_ROOT/${K8S_DIR#./}"
if [[ ! -d "$K8S_PATH" ]]; then
  echo "Kubernetes directory not found at $K8S_PATH" >&2
  exit 1
fi

if [[ $INCLUDE_STEWARD -eq 0 && ( -n "$STEWARD_PROJECT" || ${#STEWARD_PROFILES[@]} -gt 0 ) ]]; then
  echo "--steward-project and --steward-profile require --include-steward" >&2
  exit 1
fi

if [[ $INCLUDE_STEWARD -ne 0 && -z "$STEWARD_PROJECT" ]]; then
  echo "--include-steward requires --steward-project" >&2
  exit 1
fi

for cmd in az docker kubectl dotnet; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Required command '$cmd' not found in PATH." >&2
    exit 1
  fi
done

if [[ ${#STEWARD_PROFILES[@]} -gt 0 ]] && ! command -v jq >/dev/null 2>&1; then
  echo "Required command 'jq' not found in PATH (needed for Steward profile validation)." >&2
  exit 1
fi

if [[ -n "$SUBSCRIPTION_ID" ]]; then
  echo "Setting Azure subscription $SUBSCRIPTION_ID"
  az account set --subscription "$SUBSCRIPTION_ID"
fi

echo "Resolving ACR login server for $ACR_NAME"
ACR_LOGIN_SERVER=$(az acr show --name "$ACR_NAME" --resource-group "$ACR_RESOURCE_GROUP" --query loginServer -o tsv)

if [[ -z "$ACR_LOGIN_SERVER" ]]; then
  echo "Failed to retrieve login server for ACR $ACR_NAME" >&2
  exit 1
fi

IMAGE_REF="$ACR_LOGIN_SERVER/$IMAGE_NAME:$IMAGE_TAG"

echo "Logging into ACR $ACR_NAME"
az acr login --name "$ACR_NAME"

if [[ $SKIP_BUILD -eq 0 ]]; then
  echo "Building dotnet project $PROJECT_PATH"
  dotnet build "$PROJECT_PATH" -c "$BUILD_CONFIGURATION"
fi

if [[ $INCLUDE_STEWARD -ne 0 ]]; then
  STEWARD_PROJECT_PATH="$REPO_ROOT/${STEWARD_PROJECT#./}"
  if [[ ! -f "$STEWARD_PROJECT_PATH" ]]; then
    echo "Discord Steward project not found at $STEWARD_PROJECT_PATH" >&2
    exit 1
  fi

  STEWARD_BUNDLE="$REPO_ROOT/artifacts/discord-steward"
  rm -rf "$STEWARD_BUNDLE"
  mkdir -p "$STEWARD_BUNDLE"
  touch "$STEWARD_BUNDLE/.gitkeep"
  echo "Publishing self-contained Discord Steward bundle"
  dotnet publish "$STEWARD_PROJECT_PATH" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "$STEWARD_BUNDLE"

  if [[ ${#STEWARD_PROFILES[@]} -gt 0 ]]; then
    declare -A PROFILE_GUILDS=()
    declare -A PROFILE_STORAGE_PATHS=()
    mkdir -p "$STEWARD_BUNDLE/profiles"
    for steward_profile in "${STEWARD_PROFILES[@]}"; do
      steward_profile_path="$REPO_ROOT/${steward_profile#./}"
      if [[ ! -f "$steward_profile_path" ]]; then
        echo "Discord Steward profile not found at $steward_profile_path" >&2
        exit 1
      fi

      guild_id=$(jq -er '.Discord.GuildId | strings | select(test("^[1-9][0-9]*$"))' "$steward_profile_path") || {
        echo "Discord Steward profile must contain an exact non-zero Discord.GuildId: $steward_profile_path" >&2
        exit 1
      }
      if [[ -n "${PROFILE_GUILDS[$guild_id]:-}" ]]; then
        echo "Duplicate Discord Steward profile for guild $guild_id: $steward_profile_path" >&2
        exit 1
      fi
      PROFILE_GUILDS[$guild_id]="$steward_profile_path"
      PACKAGED_STEWARD_GUILDS+=("$guild_id")

      for storage_key in DataPath AssetInboxPath AssetVaultPath WebhookSecretVaultPath; do
        storage_path=$(jq -er --arg key "$storage_key" '.Steward[$key] | strings | select(length > 0)' "$steward_profile_path") || {
          echo "Discord Steward profile $guild_id requires Steward.$storage_key." >&2
          exit 1
        }
        if [[ -n "${PROFILE_STORAGE_PATHS[$storage_path]:-}" ]]; then
          echo "Discord Steward profiles share durable path $storage_path ($guild_id and ${PROFILE_STORAGE_PATHS[$storage_path]})." >&2
          exit 1
        fi
        PROFILE_STORAGE_PATHS[$storage_path]="$guild_id"
      done

      bundled_profile="$STEWARD_BUNDLE/profiles/$guild_id.json"
      cp "$steward_profile_path" "$bundled_profile"
      Steward__ProfilePath="$bundled_profile" "$STEWARD_BUNDLE/DiscordSteward" --probe
      echo "Bundled unrestricted Steward profile for guild $guild_id"
    done
  else
    "$STEWARD_BUNDLE/DiscordSteward" --probe
  fi
fi

echo "Building container image $IMAGE_REF"
docker build -f "$DOCKERFILE" -t "$IMAGE_REF" "$REPO_ROOT"

echo "Pushing image $IMAGE_REF"
docker push "$IMAGE_REF"

if [[ $SKIP_ROLLOUT -ne 0 ]]; then
  echo "Skipping rollout as requested."
  exit 0
fi

echo "Fetching AKS credentials for $AKS_CLUSTER"
az aks get-credentials --resource-group "$AKS_RESOURCE_GROUP" --name "$AKS_CLUSTER" --overwrite-existing

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

MANIFEST_WORKDIR="$TEMP_DIR/$(basename "$K8S_PATH")"
cp -R "$K8S_PATH" "$TEMP_DIR/"

if [[ ${#PACKAGED_STEWARD_GUILDS[@]} -gt 0 ]]; then
  CONFIGMAP_FILE="$MANIFEST_WORKDIR/configmap.yaml"
  if [[ ! -f "$CONFIGMAP_FILE" ]]; then
    echo "ConfigMap file not found at $CONFIGMAP_FILE" >&2
    exit 1
  fi

  for guild_id in "${PACKAGED_STEWARD_GUILDS[@]}"; do
    cat >> "$CONFIGMAP_FILE" <<EOF
  WorldAutonomy__EnabledGuilds__${guild_id}__ProfilePath: "/app/steward/profiles/${guild_id}.json"
EOF
  done
fi

DEPLOYMENT_FILE="$MANIFEST_WORKDIR/deployment.yaml"
if [[ ! -f "$DEPLOYMENT_FILE" ]]; then
  echo "Deployment file not found at $DEPLOYMENT_FILE" >&2
  exit 1
fi

sed -i "s|<ACR_LOGIN_SERVER>|$ACR_LOGIN_SERVER|g" "$DEPLOYMENT_FILE"
sed -i "s|:latest|:$IMAGE_TAG|g" "$DEPLOYMENT_FILE"

echo "Applying manifests"
kubectl apply -k "$MANIFEST_WORKDIR"

echo "Waiting for rollout to complete"
kubectl rollout status deployment/discord-sky-bot -n discord-sky

echo "Deployment complete. Active image: $IMAGE_REF"
