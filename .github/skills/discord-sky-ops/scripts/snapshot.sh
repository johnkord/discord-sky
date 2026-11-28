#!/usr/bin/env bash
#
# snapshot.sh - capture a durable, read-only snapshot of Discord Sky bot state.
#
# Pulls (in order):
#   1. the container stdout log                    (ephemeral - grab before any release)
#   2. the whole data directory: telemetry JSONL, transcripts, memories and the rest (durable)
#   3. container state and the release record
# then prints quick aggregates (event counts, recall adoption, memory totals).
#
# Usage:
#   SKY_HOST=<ssh-alias> bash snapshot.sh [output-dir]
#
# Requires: ssh access to the production host (alias from the operator's private SSH config).
#           jq is optional (summaries).
# Safety:   read-only. Does not stop the bot or modify the host.

set -euo pipefail

HOST="${SKY_HOST:?Set SKY_HOST to the production host SSH alias (see docs/env-inventory.md).}"
CONTAINER="sky-bot-1"
DATA_DIR="/srv/sky/data"
OUT="${1:-./discord-sky-snapshot-$(date -u +%Y%m%dT%H%M%SZ)}"

command -v ssh >/dev/null 2>&1 || { echo "ERROR: ssh not found in PATH." >&2; exit 1; }
remote() { ssh -o BatchMode=yes "$HOST" "$@" </dev/null; }
remote "sudo docker inspect $CONTAINER >/dev/null" || { echo "ERROR: cannot reach $CONTAINER on $HOST." >&2; exit 1; }

echo "Host:   $HOST"
echo "Output: $OUT"
mkdir -p "$OUT/user_memories"

# --- 1. Ephemeral container log (capture before it is lost) ----------------------
echo "Capturing container log..."
remote "sudo docker logs --timestamps $CONTAINER" > "$OUT/logs-current.txt" 2>&1 || true

# --- 2. Durable data directory (memories + nested telemetry/ and transcripts/) ----
echo "Copying data directory ($DATA_DIR)..."
remote "sudo tar -C $DATA_DIR -cf - ." | tar -x -C "$OUT/user_memories" --no-same-owner

# --- 3. Container state and release record ---------------------------------------
echo "Capturing container state..."
remote "sudo docker inspect $CONTAINER" > "$OUT/container-inspect.json" 2>/dev/null || true
remote "sudo docker compose -f /srv/sky/compose.yaml ps" > "$OUT/compose-ps.txt" 2>/dev/null || true
remote "sudo cat /srv/sky/release.json" > "$OUT/release.json" 2>/dev/null || true

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
