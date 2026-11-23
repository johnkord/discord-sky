#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture"/{scripts,k8s,src/DiscordSky.Bot,steward}
cp "$root/scripts/deploy.sh" "$fixture/scripts/"
cp -R "$root/k8s/discord-sky" "$fixture/k8s/"
touch "$fixture/Dockerfile" "$fixture/src/DiscordSky.Bot/DiscordSky.Bot.csproj" "$fixture/steward/Steward.csproj"
export fixture

az() { if [[ $1 == acr && $2 == show ]]; then printf '%s\n' fixture.invalid; fi; }
docker() { if [[ "$*" == *'--entrypoint /app/steward/DiscordSteward'* ]]; then printf '%s\n' '{"status":"ready","registeredToolCount":173}'; fi; }
dotnet() {
  if [[ $1 == publish ]]; then
    while [[ $1 != -o ]]; do shift; done
    printf '#!/bin/sh\nexit 0\n' > "$2/DiscordSteward"
    chmod +x "$2/DiscordSteward"
  fi
}
kubectl() {
  case "$1" in
    config) printf '%s\n' fixture-cluster ;;
    kustomize) cat "$2/deployment.yaml" ;;
    create)
      printf '%s\n' 'deployment.apps/discord-sky-bot' 'configmap/discord-sky-config'
      if [[ $FAIL_READ == private-overlay ]]; then printf '%s\n' 'secret/discord-sky-steward-profiles'; fi ;;
    get)
      local kind=$2 name=$3 data='{}'
      if [[ $name == "$FAIL_READ" ]]; then return 1; fi
      if [[ $FAIL_READ == malformed && $name == discord-sky-config ]]; then printf '{}'; return; fi
      if [[ $PRIVATES == unpaired && $kind == secret ]]; then [[ "$*" == *--ignore-not-found* ]]; return; fi
      if [[ $name == discord-sky-autonomy-bindings || $name == discord-sky-steward-profiles ]]; then
        if [[ $PRIVATES == missing ]]; then [[ "$*" == *--ignore-not-found* ]]; return; fi
        if [[ $kind == secret ]]; then data='{"101.json":"Zml4dHVyZQ=="}'; else data='{"WorldAutonomy__EnabledGuilds__101__ProfilePath":"/app/steward/profiles/101.json"}'; fi
      fi
      if [[ $kind == deployment && "$*" == *jsonpath* ]]; then printf '12'; return; fi
      case "$kind" in configmap) kind=ConfigMap ;; secret) kind=Secret ;; deployment) kind=Deployment ;; *) return 2 ;; esac
      jq -nc --arg kind "$kind" --arg name "$name" --argjson data "$data" '{apiVersion:"v1",kind:$kind,metadata:{name:$name,namespace:"discord-sky",uid:"fixture-uid",resourceVersion:"1",annotations:{"deployment.kubernetes.io/revision":"12"}},data:$data}' ;;
    apply)
      if [[ $3 == */rendered.yaml ]]; then printf 'apply rendered\n' >> "$fixture/writes"; else
        printf 'restore %s\n' "$(jq -er '.metadata.name' "$3")" >> "$fixture/writes"
      fi ;;
    delete) printf 'delete %s\n' "$3" >> "$fixture/writes" ;;
    rollout)
      if [[ $2 == undo ]]; then printf 'undo\n' >> "$fixture/writes"; elif [[ $ROLLOUT_FAIL == yes ]] && ! grep -qx undo "$fixture/writes"; then return 1; fi ;;
    *) printf 'Unexpected kubectl command: %s\n' "$1" >&2; return 2 ;;
  esac
}
export -f az docker dotnet kubectl

check() {
  local label=$1 mode=$2 expected=$3 result=0
  export FAIL_READ=$4 PRIVATES=$5 ROLLOUT_FAIL=$6
  local -a args=(--aks-resource-group fixture --aks-cluster fixture-cluster --acr-name fixture --image-tag fixture --skip-build)
  if [[ $mode == preserve ]]; then args+=(--include-steward --steward-project steward/Steward.csproj --preserve-steward-profiles); fi
  : > "$fixture/writes"
  bash "$fixture/scripts/deploy.sh" "${args[@]}" > "$fixture/output" 2>&1 || result=$?
  if [[ $expected == preflight ]]; then
    [[ $result != 0 && ! -s $fixture/writes ]] || { printf '%s: expected refusal before apply\n' "$label" >&2; return 1; }
  elif [[ $expected == rollback ]]; then
    [[ $result != 0 ]] && grep -qx 'apply rendered' "$fixture/writes" && grep -qx 'restore discord-sky-config' "$fixture/writes" && grep -qx 'undo' "$fixture/writes" || { printf '%s: rollback incomplete\n' "$label" >&2; return 1; }
    if [[ $mode == preserve ]]; then
      ! grep -Eq '(autonomy-bindings|steward-profiles)' "$fixture/writes" || { printf '%s: preservation violated\n' "$label" >&2; return 1; }
    else
      for name in discord-sky-autonomy-bindings discord-sky-steward-profiles; do
        if [[ $PRIVATES == missing ]]; then grep -qx "delete $name" "$fixture/writes"; else grep -qx "restore $name" "$fixture/writes"; fi
      done
    fi
  else
    [[ $result == 0 ]] && grep -qx 'apply rendered' "$fixture/writes" || { printf '%s: expected successful rollout\n' "$label" >&2; return 1; }
  fi
  printf 'PASS: %s\n' "$label"
}

check 'Unreadable preserved bindings' preserve preflight discord-sky-autonomy-bindings present no
check 'Unreadable runtime exclusions' preserve preflight discord-sky-runtime-bindings present no
check 'Unreadable public backup' preserve preflight discord-sky-config present no
check 'Unreadable deployment revision' preserve preflight discord-sky-bot present no
check 'Malformed public resource is not absence' preserve preflight malformed present no
check 'Binding without its profile' preserve preflight none unpaired no
check 'Reject private objects in a preserve-mode manifest' preserve preflight private-overlay present no
check 'Unreadable owned profile backup' managed preflight discord-sky-steward-profiles present no
check 'Preserve existing private resources on failure' preserve rollback none present yes
check 'Preserve confirmed absence on failure' preserve rollback none missing yes
check 'Restore owned private resources on failure' managed rollback none present yes
check 'Restore confirmed absence of owned resources' managed rollback none missing yes
check 'Preserved binding and profile' preserve success none present no
check 'Deliberately dark installation' preserve success none missing no