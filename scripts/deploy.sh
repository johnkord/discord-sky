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
  --steward-profile <path>      Repeatable schema-4 profile. Stored in a private runtime Secret.
  --preserve-steward-profiles   Keep and validate the existing runtime profile Secret/bindings (CI mode).
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
STEWARD_PROFILE_GUILD_IDS=()
declare -A PROFILE_GUILDS=()
declare -A PROFILE_STORAGE_PATHS=()
PRESERVE_STEWARD_PROFILES=0
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
    --preserve-steward-profiles)
      PRESERVE_STEWARD_PROFILES=1; shift ;;
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

if [[ $PRESERVE_STEWARD_PROFILES -ne 0 && $INCLUDE_STEWARD -eq 0 ]]; then
  echo "--preserve-steward-profiles requires --include-steward" >&2
  exit 1
fi

if [[ $PRESERVE_STEWARD_PROFILES -ne 0 && ${#STEWARD_PROFILES[@]} -gt 0 ]]; then
  echo "--preserve-steward-profiles cannot be combined with --steward-profile" >&2
  exit 1
fi

for cmd in az docker kubectl dotnet jq; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Required command '$cmd' not found in PATH." >&2
    exit 1
  fi
done

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
  if [[ ! -x "$STEWARD_BUNDLE/DiscordSteward" || ! -s "$STEWARD_BUNDLE/DiscordSteward" ]]; then
    echo "Discord Steward publish produced no executable." >&2
    exit 1
  fi

  if [[ ${#STEWARD_PROFILES[@]} -gt 0 ]]; then
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
      STEWARD_PROFILE_GUILD_IDS+=("$guild_id")

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

      Steward__ProfilePath="$steward_profile_path" "$STEWARD_BUNDLE/DiscordSteward" --probe
      echo "Validated unrestricted Steward profile for guild $guild_id"
    done
  else
    "$STEWARD_BUNDLE/DiscordSteward" --probe
  fi
fi

echo "Building container image $IMAGE_REF"
docker build -f "$DOCKERFILE" -t "$IMAGE_REF" "$REPO_ROOT"

if [[ $INCLUDE_STEWARD -ne 0 ]]; then
  docker run --rm --entrypoint sh "$IMAGE_REF" -c '
    test -x /app/steward/DiscordSteward
    test -s /app/steward/DiscordSteward
    test -z "$(find /app/steward/profiles -maxdepth 1 -type f -name "*.json" -print 2>/dev/null)"
  '
  IMAGE_PROBE=$(docker run --rm --entrypoint /app/steward/DiscordSteward "$IMAGE_REF" --probe)
  if ! jq -e '.status == "ready" and .registeredToolCount > 0' <<< "$IMAGE_PROBE" >/dev/null; then
    echo "The containerized Steward executable failed its dark startup probe." >&2
    exit 1
  fi
  echo "Verified image contains the Steward executable and no private profile files"
fi

echo "Pushing image $IMAGE_REF"
docker push "$IMAGE_REF"

if [[ $SKIP_ROLLOUT -ne 0 ]]; then
  echo "Skipping rollout as requested."
  exit 0
fi

echo "Fetching AKS credentials for $AKS_CLUSTER"
az aks get-credentials --resource-group "$AKS_RESOURCE_GROUP" --name "$AKS_CLUSTER" --overwrite-existing
if [[ "$(kubectl config current-context)" != "$AKS_CLUSTER" ]]; then
  echo "kubectl context does not match the requested AKS cluster." >&2
  exit 1
fi

if [[ $PRESERVE_STEWARD_PROFILES -ne 0 ]]; then
  AUTONOMY_BINDINGS_JSON=$(kubectl get configmap discord-sky-autonomy-bindings -n discord-sky -o json 2>/dev/null \
    || printf '{"data":{}}')
  if ! jq -e '
      (.data // {}) | keys |
      all(.[]; test("^WorldAutonomy__EnabledGuilds__[1-9][0-9]*__ProfilePath$"))
    ' <<< "$AUTONOMY_BINDINGS_JSON" >/dev/null; then
    echo "The autonomy bindings ConfigMap contains an invalid key." >&2
    exit 1
  fi
  mapfile -t PRESERVED_STEWARD_GUILDS < <(jq -r '
    (.data // {}) | keys[] |
    capture("^WorldAutonomy__EnabledGuilds__(?<id>[1-9][0-9]*)__ProfilePath$").id
  ' <<< "$AUTONOMY_BINDINGS_JSON")
  BINDING_COUNT=$(jq '(.data // {}) | length' <<< "$AUTONOMY_BINDINGS_JSON")
  if [[ ${#PRESERVED_STEWARD_GUILDS[@]} -ne $BINDING_COUNT ]]; then
    echo "The autonomy bindings ConfigMap contains an invalid key." >&2
    exit 1
  fi

  if [[ $BINDING_COUNT -gt 0 ]]; then
    STEWARD_PROFILES_SECRET_JSON=$(kubectl get secret discord-sky-steward-profiles -n discord-sky -o json 2>/dev/null) || {
      echo "Autonomy bindings exist, but the private Steward profile Secret is missing." >&2
      exit 1
    }
    for guild_id in "${PRESERVED_STEWARD_GUILDS[@]}"; do
      binding_key="WorldAutonomy__EnabledGuilds__${guild_id}__ProfilePath"
      expected_path="/app/steward/profiles/${guild_id}.json"
      if ! jq -e --arg key "$binding_key" --arg expected "$expected_path" \
          '.data[$key] == $expected' <<< "$AUTONOMY_BINDINGS_JSON" >/dev/null; then
        echo "A configured autonomy guild has an invalid private profile path." >&2
        exit 1
      fi
      if ! jq -e --arg key "${guild_id}.json" \
          '.data[$key] != null and (.data[$key] | length > 0)' \
          <<< "$STEWARD_PROFILES_SECRET_JSON" >/dev/null; then
        echo "A configured autonomy guild has no matching private Steward profile key." >&2
        exit 1
      fi
    done
  fi
  echo "Verified $BINDING_COUNT preserved autonomy binding/profile pair(s)"
fi

RUNTIME_BINDINGS_JSON=$(kubectl get configmap discord-sky-runtime-bindings -n discord-sky -o json 2>/dev/null \
  || printf '{"data":{}}')
if ! jq -e '
    (.data // {}) | to_entries |
    all(.[];
      (.key | test("^ColdOpen__Channels__[0-9]+__(GuildId|ChannelId|Guild|Channel)$")) and
      (.value | type == "string" and length > 0))
  ' <<< "$RUNTIME_BINDINGS_JSON" >/dev/null; then
  echo "The private runtime bindings ConfigMap contains an invalid key or blank value." >&2
  exit 1
fi
mapfile -t RUNTIME_COLD_OPEN_INDICES < <(jq -r '
  (.data // {}) | keys[] |
  capture("^ColdOpen__Channels__(?<index>[0-9]+)__").index
  ' <<< "$RUNTIME_BINDINGS_JSON" | sort -nu)
for target_index in "${RUNTIME_COLD_OPEN_INDICES[@]}"; do
  guild_id_key="ColdOpen__Channels__${target_index}__GuildId"
  channel_id_key="ColdOpen__Channels__${target_index}__ChannelId"
  has_guild_id=$(jq -r --arg key "$guild_id_key" '(.data // {}) | has($key)' <<< "$RUNTIME_BINDINGS_JSON")
  has_channel_id=$(jq -r --arg key "$channel_id_key" '(.data // {}) | has($key)' <<< "$RUNTIME_BINDINGS_JSON")
  if [[ "$has_guild_id" != "$has_channel_id" ]]; then
    echo "Cold-open runtime target $target_index must provide GuildId and ChannelId together." >&2
    exit 1
  fi
  if [[ "$has_guild_id" == "false" ]] && ! jq -e \
      --arg key "ColdOpen__Channels__${target_index}__Channel" \
      '(.data // {}) | has($key)' <<< "$RUNTIME_BINDINGS_JSON" >/dev/null; then
    echo "Cold-open runtime target $target_index requires an exact ID pair or a channel-name fallback." >&2
    exit 1
  fi
  if [[ "$has_guild_id" == "true" ]] && ! jq -e \
      --arg guild_key "$guild_id_key" --arg channel_key "$channel_id_key" \
      '(.data[$guild_key] | test("^[1-9][0-9]*$")) and (.data[$channel_key] | test("^[1-9][0-9]*$"))' \
      <<< "$RUNTIME_BINDINGS_JSON" >/dev/null; then
    echo "Cold-open runtime target $target_index contains an invalid exact ID." >&2
    exit 1
  fi
done
echo "Verified ${#RUNTIME_COLD_OPEN_INDICES[@]} private cold-open runtime target(s)"

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

MANIFEST_WORKDIR="$TEMP_DIR/$(basename "$K8S_PATH")"
cp -R "$K8S_PATH" "$TEMP_DIR/"

INJECTED_STEWARD_GUILDS=()
if [[ ${#STEWARD_PROFILES[@]} -gt 0 ]]; then
  KUSTOMIZATION_FILE="$MANIFEST_WORKDIR/kustomization.yaml"
  if [[ ! -f "$KUSTOMIZATION_FILE" ]]; then
    echo "Kustomization file not found at $KUSTOMIZATION_FILE" >&2
    exit 1
  fi

  AUTONOMY_BINDINGS_FILE="$MANIFEST_WORKDIR/autonomy-bindings.yaml"
  cat > "$AUTONOMY_BINDINGS_FILE" <<'EOF'
apiVersion: v1
kind: ConfigMap
metadata:
  name: discord-sky-autonomy-bindings
  namespace: discord-sky
data:
EOF
  PROFILE_SECRET_ARGS=()
  for guild_id in "${STEWARD_PROFILE_GUILD_IDS[@]}"; do
    INJECTED_STEWARD_GUILDS+=("$guild_id")
    profile_path="${PROFILE_GUILDS[$guild_id]}"
    PROFILE_SECRET_ARGS+=(--from-file="${guild_id}.json=${profile_path}")
    cat >> "$AUTONOMY_BINDINGS_FILE" <<EOF
  WorldAutonomy__EnabledGuilds__${guild_id}__ProfilePath: "/app/steward/profiles/${guild_id}.json"
EOF
  done

  STEWARD_PROFILES_SECRET_FILE="$MANIFEST_WORKDIR/steward-profiles-secret.yaml"
  kubectl create secret generic discord-sky-steward-profiles \
    --namespace discord-sky \
    "${PROFILE_SECRET_ARGS[@]}" \
    --dry-run=client \
    -o yaml > "$STEWARD_PROFILES_SECRET_FILE"

  if [[ ${#INJECTED_STEWARD_GUILDS[@]} -ne ${#STEWARD_PROFILES[@]} ]]; then
    echo "Expected ${#STEWARD_PROFILES[@]} Steward profiles but found ${#INJECTED_STEWARD_GUILDS[@]}." >&2
    exit 1
  fi

  sed -i '/^resources:/a\  - autonomy-bindings.yaml' "$KUSTOMIZATION_FILE"
  sed -i '/^resources:/a\  - steward-profiles-secret.yaml' "$KUSTOMIZATION_FILE"
fi

DEPLOYMENT_FILE="$MANIFEST_WORKDIR/deployment.yaml"
if [[ ! -f "$DEPLOYMENT_FILE" ]]; then
  echo "Deployment file not found at $DEPLOYMENT_FILE" >&2
  exit 1
fi

sed -i "s|<ACR_LOGIN_SERVER>|$ACR_LOGIN_SERVER|g" "$DEPLOYMENT_FILE"
sed -i "s|:latest|:$IMAGE_TAG|g" "$DEPLOYMENT_FILE"

RENDERED_MANIFEST="$TEMP_DIR/rendered.yaml"
kubectl kustomize "$MANIFEST_WORKDIR" > "$RENDERED_MANIFEST"
for guild_id in "${INJECTED_STEWARD_GUILDS[@]}"; do
  if ! grep -q "WorldAutonomy__EnabledGuilds__${guild_id}__ProfilePath" "$RENDERED_MANIFEST"; then
    echo "Rendered manifest omitted autonomy binding for guild $guild_id." >&2
    exit 1
  fi
  if ! grep -Eq "^  \"?${guild_id}\.json\"?:" "$RENDERED_MANIFEST"; then
    echo "Rendered manifest omitted private Steward profile key for guild $guild_id." >&2
    exit 1
  fi
done
echo "Verified ${#INJECTED_STEWARD_GUILDS[@]} private autonomy binding(s) and profile key(s) in rendered manifest"

PREVIOUS_REVISION=$(kubectl get deployment discord-sky-bot -n discord-sky \
  -o jsonpath='{.metadata.annotations.deployment\.kubernetes\.io/revision}' 2>/dev/null || true)
BACKUP_CONFIG="$TEMP_DIR/discord-sky-config-before.json"
BACKUP_BINDINGS="$TEMP_DIR/discord-sky-autonomy-bindings-before.json"
BACKUP_PROFILES="$TEMP_DIR/discord-sky-steward-profiles-before.json"
BACKED_UP_CONFIG=0
BACKED_UP_BINDINGS=0
BACKED_UP_PROFILES=0
sanitize_resource='del(
  .metadata.annotations."kubectl.kubernetes.io/last-applied-configuration",
  .metadata.creationTimestamp,
  .metadata.generation,
  .metadata.managedFields,
  .metadata.resourceVersion,
  .metadata.uid
)'
if kubectl get configmap discord-sky-config -n discord-sky -o json 2>/dev/null \
    | jq "$sanitize_resource" > "$BACKUP_CONFIG"; then
  BACKED_UP_CONFIG=1
fi
if kubectl get configmap discord-sky-autonomy-bindings -n discord-sky -o json 2>/dev/null \
    | jq "$sanitize_resource" > "$BACKUP_BINDINGS"; then
  BACKED_UP_BINDINGS=1
fi
if kubectl get secret discord-sky-steward-profiles -n discord-sky -o json 2>/dev/null \
    | jq "$sanitize_resource" > "$BACKUP_PROFILES"; then
  BACKED_UP_PROFILES=1
fi

echo "Applying manifests"
DEPLOY_FAILED=0
kubectl apply -f "$RENDERED_MANIFEST" || DEPLOY_FAILED=1
if [[ $DEPLOY_FAILED -eq 0 && ${#INJECTED_STEWARD_GUILDS[@]} -eq 0 && $PRESERVE_STEWARD_PROFILES -eq 0 ]]; then
  kubectl delete configmap discord-sky-autonomy-bindings -n discord-sky --ignore-not-found
  kubectl delete secret discord-sky-steward-profiles -n discord-sky --ignore-not-found
fi

if [[ $DEPLOY_FAILED -eq 0 ]]; then
  echo "Waiting for rollout to complete"
  kubectl rollout status deployment/discord-sky-bot -n discord-sky --timeout=300s || DEPLOY_FAILED=1
fi

if [[ $DEPLOY_FAILED -ne 0 ]]; then
  echo "Deployment failed; restoring the previous runtime configuration and revision." >&2
  ROLLBACK_ERRORS=0
  if [[ $BACKED_UP_CONFIG -ne 0 ]] && ! kubectl apply -f "$BACKUP_CONFIG"; then
    echo "Failed to restore the previous Sky ConfigMap." >&2
    ROLLBACK_ERRORS=1
  fi
  if [[ $BACKED_UP_BINDINGS -ne 0 ]]; then
    if ! kubectl apply -f "$BACKUP_BINDINGS"; then
      echo "Failed to restore the previous autonomy bindings ConfigMap." >&2
      ROLLBACK_ERRORS=1
    fi
  else
    kubectl delete configmap discord-sky-autonomy-bindings -n discord-sky --ignore-not-found || ROLLBACK_ERRORS=1
  fi
  if [[ $BACKED_UP_PROFILES -ne 0 ]]; then
    if ! kubectl apply -f "$BACKUP_PROFILES"; then
      echo "Failed to restore the previous Steward profile Secret." >&2
      ROLLBACK_ERRORS=1
    fi
  else
    kubectl delete secret discord-sky-steward-profiles -n discord-sky --ignore-not-found || ROLLBACK_ERRORS=1
  fi
  if [[ -n "$PREVIOUS_REVISION" ]]; then
    if ! kubectl rollout undo deployment/discord-sky-bot -n discord-sky --to-revision="$PREVIOUS_REVISION"; then
      echo "Failed to restore deployment revision $PREVIOUS_REVISION; manual intervention is required." >&2
      ROLLBACK_ERRORS=1
    elif ! kubectl rollout status deployment/discord-sky-bot -n discord-sky --timeout=300s; then
      echo "The previous deployment revision did not become healthy; manual intervention is required." >&2
      ROLLBACK_ERRORS=1
    fi
  else
    echo "No previous deployment revision is available; manual intervention is required." >&2
    ROLLBACK_ERRORS=1
  fi
  if [[ $ROLLBACK_ERRORS -ne 0 ]]; then
    echo "Automatic rollback was incomplete." >&2
  fi
  exit 1
fi

echo "Deployment complete. Active image: $IMAGE_REF"
