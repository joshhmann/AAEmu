#!/usr/bin/env python3
"""
test_infer_homestead_traces.py - Regression suite for the homestead trace evaluator.

Guards the evidence-honesty contract (AGENTS.md evidence-honesty gate, ROADMAP P1
"Correct synthetic evidence and evaluator verdicts"):

1. Synthetic detection reads the trace's real schema (`detail` and any other string
   field), not the removed capitalized `Payload`/`Target` keys whose absence made a
   unit-rig trace report `LIVE SERVER / NETWORK TRACE`.
2. A fully synthetic successful trace never earns a live verdict.
3. Failed/cancelled requests, duplicate events, reordered/interleaved actor records and
   success-sounding detail text without an authoritative completion never count as
   gameplay proof.
4. The report timestamp comes from the input artifact, not a hardcoded constant, and
   provenance (input path + sha256, producing command, evaluator revision, source HEAD +
   dirty state, evidence layer, is_synthetic) is recorded; unavailable provenance is
   UNKNOWN.

Run: python3 -m unittest discover -s Scripts/playertrace-coverage -p "test_*.py"
"""

import hashlib
import json
import os
import shutil
import tempfile
import unittest

from infer_homestead_traces import (
    LAYER_SYNTHETIC,
    LAYER_UNKNOWN,
    UNKNOWN,
    collect_provenance,
    detect_synthetic_markers,
    iter_strings,
    run_inference,
)

SYNTHETIC_KIT_DETAIL = (
    "[SYNTHETIC SEED] Starter homestead kit: Scarecrow Garden design (15596) "
    "and 10x Bound Tax Certificates (31892)"
)

# The canonical rig sequence: (action, target_id, detail).
SEQUENCE = [
    ("Observe", 0, "Spawned at Solzreed Peninsula (Wardton Hub) (14149.5, 14992.0, 133.4)"),
    ("Interact", 15596, SYNTHETIC_KIT_DETAIL),
    ("Move", 0, "Navigated 576.8m along roads to residential zone centroid"),
    ("Interact", 0, "Surveyed residential boundary and claimed 8x8 scarecrow garden plot (Plot ID 9000)"),
    ("Interact", 0, "Cultivated timber grove on private plot"),
    ("Interact", 2001, "Harvested timber from mature trees"),
    ("Move", 0, "Navigated 36.1m to crafting workbench"),
    ("Craft", 0, "Crafted 1x lumber pack (recipe 1001) at carpentry workbench"),
    ("Move", 0, "Transported lumber pack 36.1m back to plot frame"),
    ("Interact", 101, "Applied lumber pack to complete farm structure (House ID 9000)"),
]


class TraceBuilder:
    """Builds trace records in the real rig schema (lowercase snake_case keys)."""

    def __init__(self, actor_id: int = 0, start_index: int = 0):
        self.records = []
        self.actor_id = actor_id
        self.index = start_index

    def add(self, action, target_id, detail, *, result="Completed", terminal="Completed (completed)"):
        self.index += 1
        stamp = f"2026-09-17T07:55:{self.index:02d}.000000+00:00"
        record = {
            "trace_id": f"{self.index:032d}",
            "actor_id": self.actor_id,
            "action": action,
            "target_id": target_id,
            "requested_at": stamp,
            "started_at": stamp,
            "completed_at": stamp,
            "result": result,
            "failure": None if result == "Completed" else "StateTransition",
            "detail": detail,
            "state_changes": ["Requested", "Accepted (accepted)", f"Running ({detail})", terminal],
        }
        self.records.append(record)
        return record

    def full_run(self, overrides=None):
        """Adds the full 10-step sequence. `overrides` maps a step index to add() kwargs."""
        overrides = overrides or {}
        for step, (action, target_id, detail) in enumerate(SEQUENCE):
            override = dict(overrides.get(step, {}))
            self.add(
                override.pop("action", action),
                override.pop("target_id", target_id),
                override.pop("detail", detail),
                **override,
            )
        return self


def strip_synthetic_markers(records):
    """Returns copies with the fixture marker removed from every string field."""
    def clean(value):
        if isinstance(value, str):
            return value.replace("[SYNTHETIC SEED] ", "")
        if isinstance(value, dict):
            return {k: clean(v) for k, v in value.items()}
        if isinstance(value, list):
            return [clean(v) for v in value]
        return value

    return [clean(record) for record in records]


def write_trace(path, records):
    with open(path, "w", encoding="utf-8") as handle:
        for record in records:
            handle.write(json.dumps(record) + "\n")


class EvaluatorTestCase(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp()
        self.trace_path = os.path.join(self.temp_dir, "trace.jsonl")
        self.out_dir = os.path.join(self.temp_dir, "out")

    def tearDown(self):
        shutil.rmtree(self.temp_dir)

    def evaluate(self, records, out_dir=None, trace_path=None):
        trace_path = trace_path or self.trace_path
        out_dir = out_dir or self.out_dir
        write_trace(trace_path, records)
        summary = run_inference(trace_path, out_dir=out_dir)
        with open(os.path.join(out_dir, "homestead_10bot_inference_report.md"),
                  encoding="utf-8") as handle:
            summary["_md"] = handle.read()
        return summary


class TestSyntheticDetection(EvaluatorTestCase):
    def test_marker_in_lowercase_detail_is_detected(self):
        """The defect: capitalized Payload/Target were read, so `detail` markers were missed."""
        records = TraceBuilder().full_run().records
        self.assertFalse(any("Payload" in r or "Target" in r for r in records))
        markers = detect_synthetic_markers(records)
        self.assertTrue(markers)

    def test_rig_trace_reports_synthetic_layer_not_live(self):
        summary = self.evaluate(TraceBuilder().full_run().records)
        self.assertTrue(summary["is_synthetic"])
        self.assertEqual(summary["evidence_layer"], LAYER_SYNTHETIC)
        self.assertNotIn("LIVE SERVER", summary["evidence_layer"])
        # The report's own layer line must be synthetic; LIVE SERVER may only appear in the
        # withdrawal note that supersedes the old live claim.
        self.assertIn("**Evidence Layer:** `SYNTHETIC ORCHESTRATION CONTRACT (Unit Rig)`", summary["_md"])
        self.assertNotIn("`LIVE SERVER / NETWORK TRACE (declared by input)`", summary["_md"])
        self.assertNotIn("Overall Inference Verdict", summary["_md"])
        self.assertIn("withdrawn", summary["_md"])

    def test_fully_synthetic_success_never_earns_live_verdict(self):
        summary = self.evaluate(TraceBuilder().full_run().records)
        self.assertEqual(summary["overall_verdict"], "SYNTHETIC PASS")
        self.assertEqual(summary["synthetic_verdict"], "SYNTHETIC PASS")
        self.assertIn("never earn a live verdict", summary["live_verdict"])
        self.assertEqual(summary["metrics"]["live_pass_bots"], 0)
        self.assertIn("UNPROVED", summary["gameplay_loop_proof"])
        self.assertTrue(all(b["verdict"] == "SYNTHETIC PASS" for b in summary["bots"]))
        self.assertIn("Honesty Notice", summary["_md"])
        self.assertIn("Superseded claim", summary["_md"])
        self.assertNotIn("bots verified", summary["_md"])

    def test_undeclared_layer_is_unknown_never_live_pass(self):
        summary = self.evaluate(strip_synthetic_markers(TraceBuilder().full_run().records))
        self.assertFalse(summary["is_synthetic"])
        self.assertEqual(summary["evidence_layer"], LAYER_UNKNOWN)
        self.assertIn("UNKNOWN", summary["overall_verdict"])
        self.assertNotIn("PASS (INPUT-DECLARED LIVE LAYER)", summary["overall_verdict"])
        self.assertEqual(summary["metrics"]["live_pass_bots"], 0)
        self.assertIn("UNKNOWN", summary["live_verdict"])

    def test_declared_live_layer_is_labelled_as_declared(self):
        records = strip_synthetic_markers(TraceBuilder().full_run().records)
        for record in records:
            record["evidence_layer"] = "LIVE SERVER / NETWORK TRACE"
        summary = self.evaluate(records)
        self.assertFalse(summary["is_synthetic"])
        self.assertEqual(summary["overall_verdict"], "PASS (INPUT-DECLARED LIVE LAYER)")
        self.assertEqual(summary["metrics"]["live_pass_bots"], 1)
        self.assertNotIn("Honesty Notice", summary["_md"])


class TestNegativePathRejection(EvaluatorTestCase):
    def test_interrupted_request_never_credits_a_milestone(self):
        records = TraceBuilder().full_run(
            {7: {"result": "Interrupted", "terminal": "Interrupted (Interrupted)"}}
        ).records
        summary = self.evaluate(records)
        bot = summary["bots"][0]
        self.assertNotIn("MATERIAL_PACK_CRAFTED", bot["milestones_detected"])
        self.assertNotIn("SYNTHETIC PASS", bot["verdict"])
        self.assertEqual(bot["verdict"], "PARTIAL_PROGRESSION")
        self.assertNotEqual(summary["overall_verdict"], "SYNTHETIC PASS")
        self.assertTrue(any("Interrupted" in v for v in summary["run_integrity_violations"]))
        self.assertTrue(any("Interrupted" in v for v in bot["integrity_violations"]))

    def test_rejected_request_never_credits_a_milestone(self):
        records = TraceBuilder().full_run(
            {9: {"result": "Rejected", "terminal": "Rejected (Rejected)"}}
        ).records
        summary = self.evaluate(records)
        bot = summary["bots"][0]
        self.assertNotIn("HOMESTEAD_CONSTRUCTED", bot["milestones_detected"])
        self.assertEqual(bot["verdict"], "PARTIAL_PROGRESSION")
        self.assertLess(bot["confidence_score"], 1.0)
        self.assertIn("Rejected", summary["_md"])

    def test_success_text_without_postcondition_never_completes(self):
        """Success-sounding detail text with a non-terminal result is not evidence."""
        records = TraceBuilder().full_run(
            {9: {"result": "Running", "terminal": "Running (still placing frame)"}}
        ).records
        summary = self.evaluate(records)
        bot = summary["bots"][0]
        self.assertIn("complete farm structure", records[-1]["detail"])
        self.assertNotIn("HOMESTEAD_CONSTRUCTED", bot["milestones_detected"])
        self.assertNotIn("SYNTHETIC PASS", bot["verdict"])
        self.assertIn("non-terminal/failed request", summary["_md"])

    def test_duplicate_events_are_flagged_and_block_clean_pass(self):
        builder = TraceBuilder().full_run()
        builder.records.insert(1, dict(builder.records[1]))
        summary = self.evaluate(builder.records)
        self.assertTrue(any("duplicate" in v for v in summary["run_integrity_violations"]))
        self.assertTrue(summary["bots"][0]["integrity_violations"])
        self.assertIn("INTEGRITY VIOLATIONS", summary["overall_verdict"])
        self.assertNotEqual(summary["overall_verdict"], "SYNTHETIC PASS")

    def test_reordered_records_are_flagged(self):
        builder = TraceBuilder().full_run()
        builder.records[5], builder.records[6] = builder.records[6], builder.records[5]
        summary = self.evaluate(builder.records)
        self.assertTrue(any("out-of-order" in v for v in summary["run_integrity_violations"]))
        self.assertIn("INTEGRITY VIOLATIONS", summary["overall_verdict"])

    def test_interleaved_actors_never_earn_clean_pass(self):
        """Two actors whose records arrive interleaved cannot silently form one passing run."""
        first = TraceBuilder(actor_id=0, start_index=0).full_run()
        second = TraceBuilder(actor_id=1, start_index=100).full_run()

        # One spawn record, then both actors' remaining requests interleaved into one run.
        interleaved = [first.records[0]]
        for left, right in zip(first.records[1:], second.records[1:]):
            interleaved.append(left)
            interleaved.append(right)

        summary = self.evaluate(interleaved)
        # Both actors' complete sequences are present, so the ONLY thing standing between
        # this trace and a clean verdict is the interleaved-actor detection.
        self.assertEqual(len(summary["bots"][0]["milestones_detected"]),
                         len(set(summary["bots"][0]["milestones_detected"])) and 10 or 0)
        self.assertTrue(any("interleaved actors" in v for v in summary["run_integrity_violations"]))
        self.assertIn("interleaved actors", summary["_md"])
        self.assertNotEqual(summary["overall_verdict"], "SYNTHETIC PASS")
        self.assertNotEqual(summary["overall_verdict"], "PASS (INPUT-DECLARED LIVE LAYER)")
        self.assertIn("INTEGRITY VIOLATIONS", summary["overall_verdict"])


class TestDegenerateScaleClaim(EvaluatorTestCase):
    def test_single_actor_id_cannot_support_a_multi_bot_claim(self):
        summary = self.evaluate(TraceBuilder(actor_id=0).full_run().records)
        self.assertIn("UNSUPPORTED", summary["bots"][0]["distinct_actors_claim"])
        self.assertIn("Distinct Actors In Run", summary["_md"])


class TestProvenance(EvaluatorTestCase):
    def test_timestamp_derives_from_input_mtime_not_a_constant(self):
        records = TraceBuilder().full_run().records
        write_trace(self.trace_path, records)
        os.utime(self.trace_path, (1_700_000_000, 1_700_000_000))
        summary = run_inference(self.trace_path, out_dir=self.out_dir)
        self.assertEqual(summary["timestamp"], "2023-11-14T22:13:20Z")
        self.assertNotEqual(summary["timestamp"], "2026-09-16T13:05:00Z")
        self.assertEqual(summary["timestamp_basis"], "input artifact mtime (UTC)")

    def test_report_records_input_hash_command_revision_and_dirty_state(self):
        summary = self.evaluate(TraceBuilder().full_run().records)
        provenance = summary["provenance"]
        self.assertEqual(provenance["input_path"], self.trace_path)
        with open(self.trace_path, "rb") as handle:
            expected_hash = hashlib.sha256(handle.read()).hexdigest()
        self.assertEqual(provenance["input_sha256"], expected_hash)
        self.assertIn("infer_homestead_traces.py", provenance["producing_command"])
        self.assertIn("--out-dir", provenance["producing_command"])
        self.assertEqual(len(provenance["evaluator_revision_git_sha"]), 40)
        self.assertEqual(len(provenance["evaluator_sha256"]), 64)
        self.assertEqual(len(provenance["source_head"]), 40)
        self.assertIsInstance(provenance["source_dirty"], bool)
        self.assertEqual(provenance["provenance_status"], "RECORDED")
        self.assertEqual(summary["provenance_status"], "RECORDED")

    def test_missing_input_provenance_is_unknown(self):
        provenance = collect_provenance(
            os.path.join(self.temp_dir, "does-not-exist.jsonl"), __file__, "python3 test"
        )
        self.assertEqual(provenance["input_sha256"], UNKNOWN)
        self.assertEqual(provenance["input_mtime_utc"], UNKNOWN)
        self.assertIn("UNKNOWN", provenance["provenance_status"])

    def test_machine_report_carries_layer_and_synthetic_flag(self):
        self.evaluate(TraceBuilder().full_run().records)
        with open(os.path.join(self.out_dir, "homestead_10bot_inference_report.json"),
                  encoding="utf-8") as handle:
            payload = json.load(handle)
        self.assertEqual(payload["is_synthetic"], True)
        self.assertEqual(payload["evidence_layer"], LAYER_SYNTHETIC)
        self.assertEqual(payload["synthetic_verdict"], "SYNTHETIC PASS")
        self.assertIn("provenance", payload)
        self.assertIn("evaluator_revision_git_sha", payload["provenance"])
        self.assertNotEqual(payload["overall_verdict"], "PASS")

    def test_iter_strings_reaches_nested_record_fields(self):
        record = {"detail": "plain", "state_changes": ["Requested", "[SYNTHETIC SEED] kit"]}
        self.assertIn("[SYNTHETIC SEED] kit", list(iter_strings(record)))


if __name__ == "__main__":
    unittest.main()
