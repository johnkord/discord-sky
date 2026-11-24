import json
from pathlib import Path
import tempfile
import unittest

from inspect_turn import inspect, render


class TurnInspectorTests(unittest.TestCase):
    run_id = "01234567-89ab-cdef-8123-456789abcdef"
    request = "01234567-89ab-cdef-8123-456789abcdea"
    trigger = 2**53 + 3
    private = "private room words, signed URL and credential-shaped fixture"

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.world = self.root / "world-autonomy"
        self.world.mkdir()
        (self.root / "telemetry").mkdir()
        self.sky = {"version": 1, "runs": {self.run_id: {"runId": self.run_id, "sourceMessageId": str(self.trigger), "guildId": 4001,
                    "sourceChannelId": 5001, "status": "succeeded", "finalText": self.private}}, "toolCalls": {}, "events": []}
        self.rows = []

    def trace(self, mode="message", key=None):
        (self.world / "world-autonomy.json").write_text(json.dumps(self.sky), encoding="utf-8")
        (self.root / "telemetry" / "recall-2026-11-23.jsonl").write_text("\n".join(json.dumps(row) for row in self.rows), encoding="utf-8")
        result = inspect(self.root, mode, str(self.trigger) if key is None else key)
        self.assertNotIn(self.private, json.dumps(result))
        self.assertNotIn(self.private, render(result))
        return result

    def write(self, status="unknown"):
        self.sky["toolCalls"]["one"] = {"runId": self.run_id, "requestId": self.request, "toolName": "send_message", "dispatchStatus": status, "argumentsJson": self.private}

    def test_chosen_silence_counts_its_judge_not_an_unobserved_reply(self):
        self.sky["runs"].clear()
        self.rows = [{"event": "world_autonomy_audience", "message_id": self.trigger, "outcome": "silence", "reason": self.private},
                     {"event": "llm_call", "message_id": self.trigger, "outcome": "ok", "total_tokens": 4},
                     {"event": "world_autonomy_audience", "message_id": self.trigger + 1, "outcome": "conversation"}]
        result = self.trace()
        self.assertEqual(["silence"], [row["outcome"] for row in result["steps"]]); self.assertEqual({"dispatched": 1}, result["provider"])
        self.assertFalse(result["deliveries"])

    def test_reply_parent_and_partial_speech_join_even_from_the_confirmed_chunk(self):
        self.rows = [{"event": "reply_parent", "message_id": self.trigger, "referent_message_id": self.trigger - 1,
                      "operation_id": self.run_id, "kind": "full", "outcome": "quoted", "note": self.private}]
        self.sky["events"] = [{"runId": self.run_id, "kind": "discord_delivery", "payloadJson": json.dumps(
            {"channelId": "5001", "messageIds": [str(self.trigger + 1)], "outcome": "partial_delivery"})}]
        result = self.trace(key=str(self.trigger + 1)); self.assertTrue(result["found"])
        self.assertEqual("partial_delivery", result["deliveries"][0]["outcome"])
        self.assertEqual(str(self.trigger - 1), result["steps"][0]["referent"])

    def test_cross_channel_send_requires_independent_matching_child_evidence(self):
        self.write("succeeded")
        folder = self.world / "4001" / "steward"; folder.mkdir(parents=True)
        child = {"requestId": self.request, "kind": "send_message", "guildId": "4001", "status": "succeeded", "outcome": "verified_desired_state",
                 "invocationJson": self.private, "observedStateJson": json.dumps({"channelId": "5002", "messageId": "7001", "content": self.private})}
        for guild, expected in [("4001", True), ("4002", False)]:
            child["guildId"] = guild
            (folder / "operations.json").write_text(json.dumps({"version": 1, "operations": {self.request: child}}), encoding="utf-8")
            result = self.trace("request", self.request)
            self.assertEqual(expected, bool(result["deliveries"]))
            if expected: self.assertEqual("other_channel", result["deliveries"][0]["location"])
            else: self.assertEqual("identity_mismatch", result["writes"][0]["steward"])

    def test_reported_refusal_carries_no_implied_discord_delivery(self):
        self.rows = [{"event": "world_autonomy_speech", "kind": "direct", "message_id": self.trigger, "outcome": "refused", "reason": self.private},
                     {"event": "llm_call", "message_id": self.trigger, "outcome": "error", "stage": "before_client_dispatch"}]
        result = self.trace(); self.assertEqual("refused", result["steps"][0]["outcome"])
        self.assertFalse(result["deliveries"]); self.assertEqual({"before_dispatch": 1}, result["provider"])

    def test_only_a_matching_ack_can_close_a_host_attempt_as_delivered(self):
        payload = {"attemptId": self.request, "medium": "speech", "channelId": "5001", "text": self.private}
        def event(kind, **extra):
            return {"runId": self.run_id, "kind": kind, "payloadJson": json.dumps({**payload, **extra})}
        self.rows = [{"event": "llm_call", "evaluation_id": self.run_id, "workload": "world_autonomy", "outcome": "ok"}]
        for suffix, outcome in [(None, "no_receipt"), ("_unknown", "unknown"), ("_not_sent", "not_sent"), ("", "partial_delivery")]:
            with self.subTest(suffix=suffix):
                self.sky["events"] = [event("discord_delivery_attempt"), event("discord_delivery_not_sent", channelId="5002")]
                if suffix is not None:
                    self.sky["events"].append(event("discord_delivery" + suffix, messageIds=["7001"], outcome="partial_delivery"))
                report = self.trace()
                self.assertEqual([outcome], [entry["outcome"] for entry in report["attempts"]])
                self.assertEqual(suffix == "", bool(report["deliveries"]))
                self.assertEqual("world_autonomy", report["calls"][0]["workload"])

    def test_native_post_id_resolves_to_source_turn_without_double_counting(self):
        self.write("succeeded")
        observed = {"channelId": "5002", "messageId": "7001", "content": self.private}
        envelope = {"guild": {"id": "4001"}, "requestId": self.request, "outcome": "succeeded", "data": {"requestId": self.request,
                    "toolName": "send_message", "status": "succeeded", "outcome": "verified_desired_state", "verified": True, "observed": observed}}
        self.sky["toolCalls"]["one"]["resultJson"] = json.dumps({"Text": json.dumps(envelope)})
        result = self.trace(key="7001")
        self.assertEqual(str(self.trigger), result["runs"][0]["trigger"])
        self.assertEqual("sky_copy_of_steward_receipt", result["deliveries"][0]["source"])
        folder = self.world / "4001" / "steward"
        folder.mkdir(parents=True)
        child = {"kind": "send_message", "requestId": self.request, "guildId": "4001", "status": "succeeded", "outcome": "verified_desired_state", "observedStateJson": json.dumps(observed)}
        (folder / "operations.json").write_text(json.dumps({"version": 1, "operations": {self.request: child}}), encoding="utf-8")
        self.assertEqual(["steward_observation"], [item["source"] for item in self.trace(key="7001")["deliveries"]])

    def test_first_confirmed_speech_id_survives_a_missing_journal_event(self):
        self.rows = [{"event": "world_autonomy_speech", "kind": "direct", "message_id": self.trigger + 1,
                      "operation_id": self.run_id, "count": 3, "outcome": "partial_delivery"}]
        sent = self.trace()["deliveries"]
        self.assertEqual([str(self.trigger + 1)], sent[0]["messages"])
        self.assertEqual("first_speech_ack", sent[0]["source"])
        self.assertEqual(1, len(sent))

    def test_missing_child_and_unknown_charge_remain_unknown(self):
        self.write(); self.rows = [{"event": "llm_call", "evaluation_id": self.run_id, "outcome": "cancelled"}]
        result = self.trace("request", self.request)
        self.assertEqual("not_exported", result["writes"][0]["steward"])
        self.assertEqual({"dispatch_unknown": 1, "without_reported_usage": 1}, result["provider"])
        self.assertFalse(self.trace(key=str(self.trigger + 1))["found"])


if __name__ == "__main__":
    unittest.main()