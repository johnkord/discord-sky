#!/usr/bin/env bash
#
# snapshot.sh - capture a durable, read-only snapshot of Discord Sky bot state.
#
# Pulls (in order):
#   1. current + previous container stdout logs   (ephemeral - grab before any deploy)
#   2. all telemetry JSONL from the PVC           (durable)
#   3. every per-user memory JSON from the PVC    (durable)
#   4. pod describe + recent namespace events
# then prints quick aggregates (event counts, recall adoption, memory totals).
#
# Usage:
#   bash snapshot.sh [output-dir]
#
# Requires: kubectl (context already pointed at the cluster). jq is optional (summaries).
# Safety:   read-only. Does not modify the pod, the PVC, or any Azure resource.

set -euo pipefail

NS="discord-sky"
SELECTOR="app=discord-sky-bot"
CONTAINER="bot"
MEM_PATH="/app/data/user_memories"
OUT="${1:-./discord-sky-snapshot-$(date -u +%Y%m%dT%H%M%SZ)}"

command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl not found in PATH." >&2; exit 1; }

POD="$(kubectl get pod -n "$NS" -l "$SELECTOR" -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
if [[ -z "$POD" ]]; then
  echo "ERROR: no pod found in namespace '$NS' with selector '$SELECTOR'." >&2
  echo "Is kubectl pointed at the right cluster? Try: az aks get-credentials ..." >&2
  exit 1
fi

echo "Pod:    $POD"
echo "Output: $OUT"
mkdir -p "$OUT"

# --- 1. Ephemeral container logs (capture before they are lost) -----------------
echo "Capturing container logs..."
kubectl logs -n "$NS" "$POD" -c "$CONTAINER" --timestamps           > "$OUT/logs-current.txt"  2>/dev/null || true
kubectl logs -n "$NS" "$POD" -c "$CONTAINER" --previous --timestamps > "$OUT/logs-previous.txt" 2>/dev/null || true

# --- 2 + 3. Durable PVC data (memories + nested telemetry/ subdir) --------------
echo "Copying PVC data ($MEM_PATH)..."
if kubectl cp "$NS/$POD:$MEM_PATH" "$OUT/user_memories" -c "$CONTAINER" >/dev/null 2>&1 \
   && [[ -d "$OUT/user_memories" ]]; then
  echo "  copied via kubectl cp"
else
  echo "  kubectl cp failed (container may lack tar); falling back to exec+cat"
  mkdir -p "$OUT/user_memories/telemetry"
  kubectl exec -n "$NS" "$POD" -c "$CONTAINER" -- \
    sh -c "ls -1 $MEM_PATH/*.json 2>/dev/null" 2>/dev/null | while read -r f; do
      [[ -n "$f" ]] || continue
      kubectl exec -n "$NS" "$POD" -c "$CONTAINER" -- cat "$f" \
        > "$OUT/user_memories/$(basename "$f")" 2>/dev/null || true
    done
  kubectl exec -n "$NS" "$POD" -c "$CONTAINER" -- \
    sh -c "ls -1 $MEM_PATH/telemetry/*.jsonl 2>/dev/null" 2>/dev/null | while read -r f; do
      [[ -n "$f" ]] || continue
      kubectl exec -n "$NS" "$POD" -c "$CONTAINER" -- cat "$f" \
        > "$OUT/user_memories/telemetry/$(basename "$f")" 2>/dev/null || true
    done
fi

# --- 4. Pod + namespace metadata ------------------------------------------------
echo "Capturing pod metadata..."
kubectl describe pod -n "$NS" "$POD"                       > "$OUT/pod-describe.txt" 2>/dev/null || true
kubectl get events -n "$NS" --sort-by=.lastTimestamp        > "$OUT/events.txt"       2>/dev/null || true
kubectl get pod -n "$NS" "$POD" -o yaml                     > "$OUT/pod.yaml"         2>/dev/null || true

echo "Snapshot written to: $OUT"

# --- Aggregates (best-effort; needs jq) -----------------------------------------
if ! command -v jq >/dev/null 2>&1; then
  echo
  echo "(install jq to get automatic summaries)"
  exit 0
fi

TEL_DIR="$OUT/user_memories/telemetry"
echo
echo "== Telemetry event counts (all days) =="
if compgen -G "$TEL_DIR/recall-*.jsonl" >/dev/null 2>&1; then
  cat "$TEL_DIR"/recall-*.jsonl | jq -r '.event' 2>/dev/null | sort | uniq -c | sort -rn

  echo
  echo "== Recall adoption (recall_tool_ok / recall_hint_emitted) =="
  cat "$TEL_DIR"/recall-*.jsonl 2>/dev/null | jq -s '
    (map(select(.event=="recall_hint_emitted")) | length) as $hints
    | (map(select(.event=="recall_tool_ok"))    | length) as $ok
    | {hints:$hints, ok:$ok, adoption:(if $hints>0 then (($ok/$hints*1000|round)/1000) else null end)}' 2>/dev/null || true
else
  echo "(no telemetry files found - durable telemetry may predate this deploy, or the path differs)"
fi

echo
echo "== Memory store summary =="
MEM_FILES=$(find "$OUT/user_memories" -maxdepth 1 -name '*.json' 2>/dev/null | wc -l | tr -d ' ')
echo "users with memory files: $MEM_FILES"
if [[ "$MEM_FILES" != "0" ]]; then
  find "$OUT/user_memories" -maxdepth 1 -name '*.json' -exec cat {} + 2>/dev/null \
    | jq -s 'add | {total_notes: length, referenced: (map(select(.referenceCount>0))|length)}' 2>/dev/null || true
fi
