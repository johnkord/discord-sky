"""Inspect one Sky turn in an owner-provided local snapshot, without printing chat content."""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys
from uuid import UUID


EVENTS = {
    "world_autonomy_audience", "world_autonomy_budget", "world_autonomy_run", "world_autonomy_speech",
    "world_autonomy_visual", "world_autonomy_conversation", "world_autonomy_appeal", "reply_parent", "reaction_judged", "llm_call",
}
CONFIRMED = {"verified_desired_state", "recovered_verified_desired_state", "reconciled_verified_desired_state"}


def snowflake(value):
    text = str(value)
    return text if re.fullmatch(r"[1-9][0-9]{0,19}", text) and int(text) < 2**64 else None


def guid(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F-]{32,36}", value):
        return None
    try:
        return str(UUID(value))
    except ValueError:
        return None


def code(value):
    return value if isinstance(value, str) and re.fullmatch(r"[a-zA-Z][a-zA-Z0-9_.-]{0,63}", value) else "unknown"


def local(root, path):
    path = path.resolve()
    if not path.is_relative_to(root):
        raise ValueError("Snapshot path resolves outside the supplied directory.")
    return path


def journal(root, path):
    path = local(root, path)
    try:
        with path.open(encoding="utf-8") as source:
            data = json.load(source)
    except FileNotFoundError:
        return None
    if not isinstance(data, dict) or data.get("version") != 1:
        raise ValueError("Unrecognized journal format.")
    return data


def evidence_json(value):
    try:
        data = json.loads(value) if isinstance(value, str) else value
        return data if isinstance(data, dict) else {}
    except ValueError:
        return {}


def telemetry(root, gaps):
    rows = []
    files = sorted(local(root, root / "telemetry").glob("recall-????-??-??.jsonl"))
    if not files:
        gaps.add("No daily telemetry files in snapshot.")
    for path in files:
        with local(root, path).open(encoding="utf-8") as source:
            for line in source:
                row = evidence_json(line)
                if not row:
                    gaps.add("One or more telemetry rows could not be read.")
                if row.get("event") not in EVENTS:
                    continue
                rows.append({"event": row["event"], "message": snowflake(row.get("message_id")),
                             "referent": snowflake(row.get("referent_message_id")), "operation": guid(row.get("operation_id")),
                             "evaluation": guid(row.get("evaluation_id")), "kind": code(row.get("kind")),
                             "workload": code(row.get("workload") or row.get("kind")),
                             "outcome": code(row.get("outcome")), "stage": code(row.get("stage")),
                             "usage_reported": type(row.get("total_tokens")) is int,
                             "latency_ms": row.get("latency_ms") if type(row.get("latency_ms")) is int else None})
    return rows


def delivery(source, run, channel, messages, outcome, source_channel):
    channel = snowflake(channel)
    ids = [snowflake(item) for item in messages] if isinstance(messages, list) else []
    if not channel or not ids or not all(ids):
        return None
    location = "unrecorded" if not source_channel else ("source_channel" if source_channel == channel else "other_channel")
    return {"source": source, "run": run, "channel": channel, "messages": ids, "location": location, "outcome": outcome}


def native_receipt(call, runs):
    run = guid(call.get("runId"))
    tool, request = call.get("toolName"), guid(call.get("requestId"))
    if run not in runs or not request or tool not in {"send_message", "send_webhook_message"} or call.get("dispatchStatus") != "succeeded" or call.get("errorMessage"):
        return None
    outer = evidence_json(call.get("resultJson"))
    if outer.get("isError") or outer.get("IsError"):
        return None
    envelope = evidence_json(outer["Text"]) if "Text" in outer else outer
    data = evidence_json(envelope.get("data"))
    guild = snowflake(runs[run].get("guildId"))
    if (not guild or snowflake(evidence_json(envelope.get("guild")).get("id")) != guild or envelope.get("outcome") != "succeeded" or
            guid(envelope.get("requestId")) != request or guid(data.get("requestId")) != request or data.get("toolName") != tool or
            envelope.get("error") or data.get("errorCode") or data.get("verified") is not True or data.get("status") != "succeeded" or data.get("outcome") not in CONFIRMED):
        return None
    observed = evidence_json(data.get("observed"))
    return delivery("sky_copy_of_steward_receipt", run, observed.get("channelId"), [observed.get("messageId")], "verified", snowflake(runs[run].get("sourceChannelId")))


def inspect(root, mode, key):
    root = Path(root).resolve(strict=True)
    if not root.is_dir():
        raise ValueError("Expected a snapshot directory.")
    key = snowflake(key) if mode == "message" else guid(key)
    if not key or mode not in {"message", "run", "request"}:
        raise ValueError("Expected a Discord snowflake or a valid run/request UUID.")
    gaps = set()
    world = root / "world-autonomy"
    sky = journal(root, world / "world-autonomy.json")
    if sky is None:
        gaps.add("Sky journal not exported.")
    sky = sky or {"runs": {}, "toolCalls": {}, "events": []}
    runs = {guid(name): row for name, row in sky["runs"].items() if guid(name) and guid(row.get("runId")) == guid(name)}
    calls = list(sky["toolCalls"].values())
    receipts = [receipt for call in calls if (receipt := native_receipt(call, runs)) is not None]
    attempts = {}
    for event in sky["events"]:
        run = guid(event.get("runId"))
        kind = event.get("kind")
        if run not in runs or kind not in {"discord_delivery", "discord_delivery_attempt", "discord_delivery_unknown", "discord_delivery_not_sent"}:
            continue
        payload = evidence_json(event.get("payloadJson"))
        attempt = guid(payload.get("attemptId"))
        channel = snowflake(payload.get("channelId"))
        if kind == "discord_delivery_attempt" and attempt and channel:
            attempts[(run, attempt)] = {"run": run, "attempt": attempt, "channel": channel,
                                        "medium": code(payload.get("medium")), "outcome": "no_receipt"}
        entry = attempts.get((run, attempt))
        if kind in {"discord_delivery_unknown", "discord_delivery_not_sent"} and entry and channel == entry["channel"]:
            entry["outcome"] = "unknown" if kind.endswith("_unknown") else "not_sent"
        if kind != "discord_delivery":
            continue
        if payload.get("outcome") not in (None, "delivered", "partial_delivery"):
            continue
        receipt = delivery("sky_ack", run, payload.get("channelId"), payload.get("messageIds"),
                           payload.get("outcome") or "acknowledged", snowflake(runs[run].get("sourceChannelId")))
        if receipt:
            receipts.append(receipt)
            if entry and channel == entry["channel"]:
                entry["outcome"] = receipt["outcome"]

    rows = telemetry(root, gaps)
    messages = {key} if mode == "message" else set()
    operations = {key} if mode == "run" else set()
    if mode == "request":
        operations.update(guid(call.get("runId")) for call in calls if guid(call.get("requestId")) == key)
    operations.update(item["run"] for item in receipts if messages.intersection(item["messages"]))
    operations.update(run for run, row in runs.items() if snowflake(row.get("sourceMessageId")) in messages)
    for row in rows:
        if row["message"] in messages:
            operations.update([row["operation"], row["evaluation"]])
    operations.discard(None)
    selected_runs = {run: row for run, row in runs.items() if run in operations}
    messages.update(snowflake(row.get("sourceMessageId")) for row in selected_runs.values())
    messages.discard(None)
    matched = [row for row in rows if row["message"] in messages or row["operation"] in operations or row["evaluation"] in operations]
    steps = [{name: row[name] for name in ("event", "kind", "outcome", "referent", "latency_ms") if row[name] is not None}
             for row in matched if row["event"] != "llm_call"]
    provider = Counter()
    provider_calls = []
    for row in (row for row in matched if row["event"] == "llm_call"):
        stage = row["stage"]
        dispatch = ("before_dispatch" if stage == "before_client_dispatch" else "dispatched" if row["outcome"] == "ok" or
                    stage.startswith(("client_dispatched", "stream_ended")) else "dispatch_unknown")
        provider[dispatch] += 1
        provider_calls.append({"workload": row["workload"], "dispatch": dispatch, "outcome": row["outcome"],
                       "latency_ms": row["latency_ms"], "usage_reported": row["usage_reported"]})
        if dispatch != "before_dispatch" and not row["usage_reported"]:
            provider["without_reported_usage"] += 1

    writes = []
    deliveries = [item for item in receipts if item["run"] in selected_runs]
    confirmed_ids = {message for item in deliveries for message in item["messages"]}
    for row in matched:
        if (row["event"] != "world_autonomy_speech" or row["outcome"] not in {"delivered", "partial_delivery"} or
                not row["message"] or row["message"] in confirmed_ids):
            continue
        channel = snowflake(selected_runs.get(row["operation"], {}).get("sourceChannelId"))
        deliveries.append({"source": "first_speech_ack", "run": row["operation"], "channel": channel,
                           "messages": [row["message"]], "location": "source_channel" if channel else "unrecorded", "outcome": row["outcome"]})
        confirmed_ids.add(row["message"])
    children = {}
    for call in calls:
        run = guid(call.get("runId"))
        if run not in selected_runs:
            continue
        guild = snowflake(selected_runs[run].get("guildId"))
        request = guid(call.get("requestId"))
        tool = code(call.get("toolName"))
        write = {"tool": tool, "request": request, "run": run, "sky": code(call.get("dispatchStatus")), "steward": "not_exported"}
        writes.append(write)
        if not guild or not request:
            write["steward"] = "no_join_identity"
            continue
        if guild not in children:
            children[guild] = journal(root, world / guild / "steward" / "operations.json")
        child = children[guild]
        if child is None:
            gaps.add("A matching guild's Steward journal is not in the snapshot.")
            continue
        record = child["operations"].get(request)
        if record is None:
            write["steward"] = "no_receipt"
            continue
        if guid(record.get("requestId")) != request or snowflake(record.get("guildId")) != guild or record.get("kind") != tool:
            write["steward"] = "identity_mismatch"
            continue
        status, outcome = code(record.get("status")), code(record.get("outcome"))
        write.update(steward=status, outcome=outcome)
        if tool in {"send_message", "send_webhook_message"} and status == "succeeded" and outcome in CONFIRMED and not record.get("errorCode"):
            observed = evidence_json(record.get("observedStateJson"))
            sent = delivery("steward_observation", run, observed.get("channelId"), [observed.get("messageId")], "verified", snowflake(selected_runs[run].get("sourceChannelId")))
            if sent:
                deliveries = [item for item in deliveries if not (item["source"] == "sky_copy_of_steward_receipt" and item["run"] == run and
                              item["channel"] == sent["channel"] and item["messages"] == sent["messages"])]
                deliveries.append(sent)
    found = bool(selected_runs or matched or writes)
    selected_attempts = [entry for entry in attempts.values() if entry["run"] in selected_runs]
    if any(entry["outcome"] in {"no_receipt", "unknown"} for entry in selected_attempts):
        gaps.add("A host send has no conclusive receipt. Inspect Discord read-only; do not replay it.")
    if not deliveries:
        gaps.add("No confirmed Discord message ID in the joined records; this does not establish silence.")
    if not provider:
        gaps.add("No joined provider-call rows; actual dispatch is not established by this view.")
    return {"found": found, "query": {mode: key}, "runs": [{"id": run, "trigger": snowflake(row.get("sourceMessageId")),
            "status": code(row.get("status"))} for run, row in selected_runs.items()], "steps": steps,
            "provider": dict(provider), "calls": provider_calls, "attempts": selected_attempts, "deliveries": deliveries, "writes": writes, "gaps": sorted(gaps)}


def render(report):
    mode, key = next(iter(report["query"].items()))
    lines = [f"{mode.title()} {key}"]
    if not report["found"]:
        lines.append("No matching turn in this snapshot.")
    for run in report["runs"]:
        lines.append(f"  Run {run['id']}: {run['status']} (trigger {run['trigger'] or 'unknown'})")
    for step in report["steps"]:
        detail = f"; cites {step['referent']}" if step.get("referent") else ""
        if step.get("latency_ms") is not None:
            detail += f"; {step['latency_ms']} ms"
        lines.append(f"  {step['event']}: {step['kind']} / {step['outcome']}{detail}")
    if report["provider"]:
        lines.append("  Provider: " + ", ".join(f"{name}={value}" for name, value in report["provider"].items()))
    for call in report["calls"]:
        elapsed = f"; {call['latency_ms']} ms" if call["latency_ms"] is not None else ""
        lines.append(f"    {call['workload']}: {call['dispatch']} / {call['outcome']}{elapsed}")
    for item in report["writes"]:
        lines.append(f"  {item['tool']} ({item['request'] or 'no request ID'}): Sky {item['sky']}; Steward {item['steward']} / {item.get('outcome', 'unconfirmed')}")
    for attempt in report["attempts"]:
        lines.append(f"  Host {attempt['medium']} attempt {attempt['attempt']}: {attempt['outcome']} (channel {attempt['channel']})")
    for sent in report["deliveries"]:
        lines.append(f"  {sent['source']}: {sent['outcome']}; {sent['location']} {sent['channel']}; message {', '.join(sent['messages'])}")
    lines.extend(f"  Limit: {gap}" for gap in report["gaps"])
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path, help="Local snapshot root containing telemetry/ and/or world-autonomy/")
    selectors = parser.add_mutually_exclusive_group(required=True)
    for name in ("message", "run", "request"):
        selectors.add_argument("--" + name, metavar="ID")
    parser.add_argument("--json", action="store_true", help="Emit structured, metadata-only output")
    args = parser.parse_args()
    mode = next(name for name in ("message", "run", "request") if getattr(args, name) is not None)
    try:
        report = inspect(args.snapshot, mode, getattr(args, mode))
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        print(f"Cannot inspect snapshot or identifier ({type(error).__name__}). Check its layout, schema and permissions.", file=sys.stderr)
        return 2
    print(json.dumps(report, indent=2) if args.json else render(report))
    return 0 if report["found"] else 1


if __name__ == "__main__":
    sys.exit(main())