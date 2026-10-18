"""Consumer-visible packet behavior; fixtures never invoke gameplay or models."""
import copy
import hashlib
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

import triage
from evaluate import Evaluator, SCHEMA


START = datetime(2026, 10, 12, tzinfo=timezone.utc)


def stamp(seconds):
    return (START + timedelta(seconds=seconds)).isoformat()


def manifest():
    return {"schema": SCHEMA, "run_id": "preserved-run", "bot_id": 297,
            "bot_name": "Fixture", "source_sha": "exact-source-sha", "database_id": "fixture-db",
            "evidence_layer": "preserved replay", "acceptance": {"quest_id": 251,
            "item_id": 4058, "item_count": 3, "require_followup": True,
            "independent_selection": True}, "thresholds": {"inactivity_seconds": 30,
            "repeated_failures": 3, "scheduler_stall_seconds": 20, "max_gap_seconds": 15}}


def event(number, seconds, kind, **data):
    m = manifest()
    return {"schema": SCHEMA, **{key: m[key] for key in triage.IDENTITY},
            "event_id": f"e{number}", "timestamp": stamp(seconds), "kind": kind,
            "source": {"artifact": "preserved/raw exact.jsonl", "line": number,
                       "field": kind, "layer": m["evidence_layer"],
                       "timestamp_kind": "first_observed_at"}, "data": data}


def heartbeat(number, seconds, wake=1):
    return event(number, seconds, "heartbeat", wake_sequence=wake, active=True, scheduler_running=True)


def terminal(number, seconds, trace="trace-exact", action="Move", result="Rejected", **extra):
    return event(number, seconds, "action_result", trace_id=trace, action=action,
                 result=result, quest_id=251, reason="invalid request", **extra)


def report(rows):
    evaluator = Evaluator(manifest())
    for row in rows:
        evaluator.consume(row)
    return evaluator.finish("bounded fixture end", rows[-1]["timestamp"])


def success_rows():
    return [heartbeat(1, 0),
            event(2, 1, "inventory_change", quest_id=251, item_id=4058, before=0, after=3),
            event(3, 2, "action_attempt", trace_id="turn", action="TurnInQuest", quest_id=251),
            terminal(4, 3, "turn", "TurnInQuest", "Completed"),
            event(5, 4, "quest_completed", quest_id=251, before=False, after=True,
                  authority="saved completed_quests bit"),
            event(6, 5, "goal_selected", quest_id=324, trace_id="follow", independent=True,
                  policy="quest-v1", reason="eligible offer"),
            event(7, 6, "action_result", quest_id=324, trace_id="follow", action="AcceptQuest", result="Completed"),
            event(8, 7, "quest_accepted", quest_id=324, before=False, after=True, authority="saved quests row"),
            heartbeat(9, 8, 2)]


class PacketTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.events = self.root / "events.jsonl"
        self.verdict = self.root / "verdict.json"

    def write_rows(self, rows):
        self.events.write_bytes(b"".join(json.dumps(row).encode() + b"\n" for row in rows))

    def packet(self, rows, supplied_report=None):
        self.write_rows(rows)
        supplied_report = report(rows) if supplied_report is None else supplied_report
        self.verdict.write_text(json.dumps(supplied_report))
        return triage.build_packet(supplied_report, self.events, self.verdict)

    def assert_refs(self, packet):
        included = {e["event_id"] for e in packet["events"]}
        self.assertEqual(len(included), len(packet["events"]))
        if packet["first_discrepancy"]:
            self.assertIn(packet["first_discrepancy"]["event_id"], included)
        for candidate in packet["hypotheses"]:
            self.assertTrue(set(candidate["supporting_event_ids"]) <= included)
        self.assertLessEqual(len(triage.encode(packet)), 16384)
        coverage = packet["coverage"]
        self.assertEqual(len(packet["events"]), coverage["included_events"])
        self.assertEqual(coverage["total_events"], coverage["included_events"] + coverage["omitted_events"])

    def assert_unknown(self, packet):
        self.assertTrue(packet["abstention_reason"])
        self.assertEqual(["unknown"], [h["id"] for h in packet["hypotheses"]])
        self.assert_refs(packet)

    def test_earliest_failure_survives_last_heartbeat_verdict_detail_loss(self):
        rows = [heartbeat(1, 0), event(2, 1, "action_attempt", trace_id="early", action="Move", quest_id=251),
                terminal(3, 2, "early")] + [heartbeat(n, n - 1) for n in range(4, 101)]
        verdict = report(rows)
        verdict["failure_or_stall_evidence"] = [rows[-1]["event_id"]]
        packet = self.packet(rows, verdict)
        self.assertIsNone(packet["abstention_reason"])
        self.assertEqual("e3", packet["first_discrepancy"]["event_id"])
        self.assertTrue({"e2", "e3", "e100"} <= {e["event_id"] for e in packet["events"]})
        self.assertGreater(packet["coverage"]["omitted_events"], 0)
        self.assertIn("binding_context", {h["id"] for h in packet["hypotheses"]})
        self.assert_refs(packet)

    def test_movement_stall_retains_first_threshold_and_competing_arrival_checks(self):
        rows = [heartbeat(1, 0), event(2, 5, "movement", distance_m=3)]
        rows += [heartbeat(n, (n - 1) * 10, n) for n in range(3, 6)]
        verdict = report(rows)
        threshold = next(n for n in verdict["notifications"] if n["code"] == "STALL_THRESHOLD")
        packet = self.packet(rows, verdict)
        self.assertIsNone(packet["abstention_reason"])
        self.assertEqual(threshold["event_id"], packet["first_discrepancy"]["event_id"])
        candidates = {h["id"]: h for h in packet["hypotheses"]}
        self.assertTrue({"travel_pending", "arrival_action_context", "unknown"} <= candidates.keys())
        self.assertEqual(["e2"], candidates["travel_pending"]["supporting_event_ids"])
        self.assertNotIn("no_issue", candidates)
        self.assert_refs(packet)

    def test_exact_trace_and_extended_source_are_not_shortened_or_rewritten(self):
        trace = "trace-" + "x" * 700
        rows = [heartbeat(1, 0), terminal(2, 1, trace)]
        rows[1]["source"].update(artifact="/exact/path with spaces/" + "a" * 500,
                                  field="output.actorAudit[17].detail", native_timestamp=stamp(1),
                                  raw_sha256="b" * 64, actor_id=990, cycle_id="native-cycle")
        packet = self.packet(rows)
        selected = next(e for e in packet["events"] if e["event_id"] == "e2")
        self.assertEqual(rows[1], selected)
        self.assertEqual("preserved replay", packet["evidence_layer"])
        self.assert_refs(packet)

    def test_identity_duplicates_chronology_and_terminal_contradictions_abstain(self):
        base = [heartbeat(1, 0), terminal(2, 1), heartbeat(3, 2)]
        cases = []
        mismatch = copy.deepcopy(base)
        mismatch[-1]["bot_id"] = 165
        cases.append(mismatch)
        duplicate = copy.deepcopy(base)
        duplicate[-1]["event_id"] = "e2"
        cases.append(duplicate)
        backwards = copy.deepcopy(base)
        backwards[-1]["timestamp"] = stamp(0)
        cases.append(backwards)
        cases.append(base + [terminal(4, 3, result="Completed")])
        cases.append([heartbeat(1, 0), event(2, 1, "action_attempt", trace_id="trace-exact", action="TurnInQuest", quest_id=251), terminal(3, 2)])
        for rows in cases:
            with self.subTest(rows=rows):
                packet = self.packet(rows)
                self.assert_unknown(packet)
                self.assertEqual(report(rows)["status"], packet["deterministic_status"])

    def test_missing_telemetry_and_unsupported_authority_abstain(self):
        cases = [[terminal(1, 0)],
                 [heartbeat(1, 0), event(2, 1, "telemetry_gap", reason="missing rolling history")],
                 [heartbeat(1, 0), event(2, 1, "quest_completed", quest_id=251, before=False, after=True, authority="actor Completed")],
                 [heartbeat(1, 0), event(2, 1, "objective_progress", quest_id=251, act_id=42, before=0, after=1, authority="damage observed")]]
        for rows in cases:
            with self.subTest(rows=rows):
                self.assert_unknown(self.packet(rows))

    def test_dangling_and_contradictory_report_evidence_abstain(self):
        rows = success_rows()
        for mutate in (lambda r: r["criterion_evidence"].update(authoritative_completion="missing"),
                       lambda r: r["criterion_evidence"].update(authoritative_completion="e4"),
                       lambda r: r["confirmed_actions"][0].update(result="Failed"),
                       lambda r: r["criteria"].update(authoritative_completion=False),
                       lambda r: r["criteria"].pop("independent_followup")):
            verdict = report(rows)
            mutate(verdict)
            self.assert_unknown(self.packet(rows, verdict))

    def test_completed_action_never_promotes_to_objective_effect(self):
        rows = [heartbeat(1, 0), terminal(2, 1, action="TurnInQuest", result="Completed"), heartbeat(3, 2)]
        packet = self.packet(rows)
        self.assertIsNone(packet["abstention_reason"])
        self.assertFalse(packet["acceptance"]["observed"]["authoritative_completion"])
        self.assertIsNone(packet["first_discrepancy"])
        candidates = {h["id"]: h for h in packet["hypotheses"]}
        self.assertIn("terminal_without_effect", candidates)
        self.assertIn("observation_window", candidates)
        self.assertNotIn("no_issue", candidates)
        self.assert_refs(packet)

    def test_success_and_native_failure_remain_separate(self):
        rows = success_rows()
        good = self.packet(rows)
        self.assertIsNone(good["abstention_reason"])
        self.assertIn("no_issue", {h["id"] for h in good["hypotheses"]})
        verdict = report(rows)
        verdict["status"] = "FAIL"
        verdict["test_execution"] = {"observer_status": "PASS", "native_test_passed": False,
                                     "test_exit_code": 1, "native_end_reason": "assertion failure",
                                     "native_report": "/preserved/native.json", "cleanup_status": "running"}
        packet = self.packet(rows, verdict)
        self.assertIsNone(packet["abstention_reason"])
        self.assertEqual("FAIL", packet["deterministic_status"])
        self.assertEqual(verdict["test_execution"], packet["test_execution"])
        self.assertIn("native_execution_failure", {h["id"] for h in packet["hypotheses"]})
        self.assertNotIn("no_issue", {h["id"] for h in packet["hypotheses"]})
        self.assert_refs(packet)
    def test_provisional_observer_pass_is_not_aggregate_acceptance(self):
        rows = success_rows()
        verdict = report(rows)
        verdict["test_execution"] = {"observer_status": "PASS", "review_phase": "provisional",
                                     "cleanup_state": "pending", "native_test_passed": None,
                                     "test_exit_code": None}
        packet = self.packet(rows, verdict)
        self.assertIsNone(packet["abstention_reason"])
        self.assertEqual("PASS", packet["deterministic_status"])
        self.assertNotIn("no_issue", {h["id"] for h in packet["hypotheses"]})
        self.assertIn("native_acceptance_pending", {h["id"] for h in packet["hypotheses"]})
        self.assertEqual(verdict["test_execution"], packet["test_execution"])
        self.assert_refs(packet)

    def test_qualifying_middle_inventory_event_and_correlated_attempt_are_included(self):
        rows = success_rows()
        rows.insert(1, event(10, 0, "inventory_change", quest_id=251, item_id=4058, before=1, after=0))
        rows.insert(3, event(11, 1, "inventory_change", quest_id=251, item_id=4058, before=3, after=2))
        packet = self.packet(rows)
        self.assertIsNone(packet["abstention_reason"])
        self.assertTrue({"e2", "e3", "e4"} <= {e["event_id"] for e in packet["events"]})
        self.assert_refs(packet)

    def test_followup_completed_saved_state_is_valid_existing_proof(self):
        rows = success_rows()
        rows[7]["kind"] = "quest_completed"
        rows[7]["data"]["authority"] = "saved completed_quests bit"
        packet = self.packet(rows)
        self.assertIsNone(packet["abstention_reason"])
        self.assertTrue(packet["acceptance"]["observed"]["independent_followup"])
        self.assert_refs(packet)

    def test_native_hunt_progress_retains_exact_counter_source_and_rejects_future_credit(self):
        rows = success_rows()
        rows[1] = event(2, 1, "objective_progress", quest_id=251, act_id=42,
                        before=0, after=3, authority="native quest progress")
        rows[1]["source"].update(native_timestamp=stamp(1), raw_sha256="d" * 64,
                                  actor_id=990, cycle_id="counter-cycle", field="log_lines[17]")
        hunt_manifest = manifest()
        hunt_manifest["acceptance"]["objective"] = {"mode": "native_hunt", "act_id": 42, "required_progress": 3}
        evaluator = Evaluator(hunt_manifest)
        for row in rows:
            evaluator.consume(row)
        verdict = evaluator.finish("bounded native-counter fixture", rows[-1]["timestamp"])
        packet = self.packet(rows, verdict)
        self.assertIsNone(packet["abstention_reason"])
        self.assertTrue(packet["acceptance"]["observed"]["hunt_progress"])
        self.assertEqual(rows[1], next(e for e in packet["events"] if e["event_id"] == "e2"))
        self.assert_refs(packet)
        rows[1]["source"]["native_timestamp"] = stamp(9)
        self.assert_unknown(self.packet(rows, verdict))


    def test_malformed_middle_still_hashes_and_validates_entire_bounded_input(self):
        rows = [heartbeat(1, 0), heartbeat(2, 1), heartbeat(3, 2)]
        self.write_rows(rows)
        lines = self.events.read_bytes().splitlines(keepends=True)
        raw = lines[0] + b"{not-json}\n" + b"".join(lines[1:])
        self.events.write_bytes(raw)
        packet = triage.build_packet(report(rows), self.events)
        self.assert_unknown(packet)
        self.assertTrue(packet["coverage"]["input_complete"])
        self.assertEqual(4, packet["coverage"]["total_events"])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), packet["provenance"]["events_sha256"])

    def test_declared_stream_bounds_disclose_incomplete_hash(self):
        rows = [heartbeat(1, 0), heartbeat(2, 1), heartbeat(3, 2)]
        for name, limit in (("MAX_EVENTS", 2), ("MAX_EVENT_BYTES", 100), ("MAX_LINE_BYTES", 100)):
            with self.subTest(bound=name), mock.patch.object(triage, name, limit):
                packet = self.packet(rows)
                self.assert_unknown(packet)
                self.assertTrue(packet["coverage"]["bounds_hit"])
                self.assertFalse(packet["coverage"]["input_complete"])
                self.assertIsNone(packet["provenance"]["events_sha256"])

    def test_oversized_essential_source_abstains_instead_of_truncating_refs(self):
        rows = [heartbeat(1, 0), terminal(2, 1, "x" * 20000)]
        packet = self.packet(rows)
        self.assert_unknown(packet)
        self.assertIn("packet_bytes", packet["coverage"]["bounds_hit"])
        self.assertEqual([], packet["events"])
        self.assertIsNone(packet["first_discrepancy"])
        self.assertEqual(2, packet["coverage"]["omitted_events"])

    def test_event_final_raw_prefix_and_verdict_hashes_are_distinct_read_only(self):
        rows = [heartbeat(1, 0), terminal(2, 1)]
        verdict = report(rows)
        verdict["collection"] = {"raw_path": "/preserved/raw.jsonl", "raw_sha256": "a" * 64,
                                  "consumed_prefix_sha256": "b" * 64, "final_raw_sha256": "c" * 64, "raw_preserved": True}
        packet = self.packet(rows, verdict)
        raw_events, raw_verdict = self.events.read_bytes(), self.verdict.read_bytes()
        packet_again = triage.build_packet(verdict, self.events, self.verdict)
        self.assertEqual(packet, packet_again)
        self.assertEqual(raw_events, self.events.read_bytes())
        self.assertEqual(raw_verdict, self.verdict.read_bytes())
        provenance = packet["provenance"]
        self.assertEqual(hashlib.sha256(raw_events).hexdigest(), provenance["events_sha256"])
        self.assertEqual(hashlib.sha256(raw_verdict).hexdigest(), provenance["verdict_sha256"])
        self.assertEqual("a" * 64, provenance["raw_sha256"])
        self.assertEqual("b" * 64, provenance["consumed_prefix_sha256"])
        self.assertEqual("c" * 64, provenance["final_raw_sha256"])
        self.assert_refs(packet)

    def test_cli_emits_one_bounded_packet_and_preserves_report_status(self):
        rows = success_rows()
        self.packet(rows)
        result = subprocess.run([sys.executable, str(Path(triage.__file__)), str(self.verdict), "--events", str(self.events)],
                                check=True, capture_output=True, timeout=10)
        self.assertLessEqual(len(result.stdout), 16384)
        packet = json.loads(result.stdout)
        self.assertEqual("PASS", packet["deterministic_status"])
        self.assertEqual(triage.SCHEMA, packet["schema"])
        self.assertIsNone(packet["abstention_reason"])
        self.assert_refs(packet)

    def test_missing_files_and_duplicate_json_keys_abstain(self):
        rows = [heartbeat(1, 0)]
        self.assert_unknown(triage.build_packet(report(rows), self.events))
        self.write_rows(rows)
        self.events.write_bytes(self.events.read_bytes().replace(b'"active": true', b'"active": true, "active": false'))
        self.assert_unknown(triage.build_packet(report(rows), self.events))


if __name__ == "__main__":
    unittest.main()
