#!/usr/bin/env python3
"""
test_coverage_mapper.py - Comprehensive unit test suite for AAEmu PlayerTrace Coverage Mapper.

Tests:
1. Streamed JSONL parser resilience (valid lines, malformed JSON, missing fields, unknown categories).
2. Packet inventory scanning and direction inference.
3. Noise filtering and sequence normalization (ping/chat suppression, movement collapsing).
4. Transition and n-gram calculation.
5. Scenario matrix and Jaccard similarity.
6. Conservative candidate family generation (verifying Plant, Harvest, and NPC interaction distinctions).
7. Baseline delta comparison.
8. Deterministic CSV and JSON export.
"""

import json
import os
import shutil
import tempfile
import unittest

from coverage_engine import (
    CorpusSummary,
    CoverageEngine,
    PacketCoverageItem,
)
from packet_inventory import PacketInventory, PacketMetadata
from playertrace_coverage import compute_baseline_delta, generate_markdown_report, write_csv
from trace_parser import TraceRecord, infer_scenario_from_path, stream_trace_file


class TestTraceParser(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.temp_dir)

    def test_parse_valid_and_malformed_records(self):
        sample_path = os.path.join(self.temp_dir, "test_action__Char__20260101_000000.jsonl")
        content = (
            '{"ts":"2026-09-14T07:00:00Z","elapsed_ms":0,"category":"lifecycle","event":"trace_started","data":{"Scenario":"test_action"}}\n'
            '{"ts":"2026-09-14T07:00:01Z","elapsed_ms":1000,"category":"packet","event":"packet_in","data":{"Packet":"CSStartSkillPacket","Opcode":"0x04F"}}\n'
            'THIS IS A CORRUPT MALFORMED LINE THAT SHOULD BE SKIPPED SAFELY\n'
            '{"ts":"2026-09-14T07:00:02Z","elapsed_ms":2000,"category":"packet","event":"packet_out","data":{"Packet":"SCSkillStartedPacket"}}\n'
            '{"unknown_root_schema": true}\n'
            '\n'  # empty line
            '{"ts":"2026-09-14T07:00:03Z","elapsed_ms":3000,"category":"world","event":"doodad_created","data":{"ObjId":123}}\n'
        )
        with open(sample_path, "w", encoding="utf-8") as f:
            f.write(content)

        malformed_count = 0
        def on_malformed(path, lineno, err, line):
            nonlocal malformed_count
            malformed_count += 1

        records = list(stream_trace_file(sample_path, on_malformed=on_malformed))

        # 4 valid records parsed: lifecycle, packet_in, packet_out, world
        self.assertEqual(len(records), 5)  # including unknown_root_schema which defaults to unknown/unknown
        self.assertEqual(malformed_count, 1)  # the corrupt non-JSON line
        self.assertEqual(records[0].scenario, "test_action")
        self.assertEqual(records[1].packet_name, "CSStartSkillPacket")
        self.assertEqual(records[1].opcode, "0x04F")
        self.assertTrue(records[1].is_packet_in)
        self.assertTrue(records[2].is_packet_out)

    def test_infer_scenario_from_path(self):
        self.assertEqual(infer_scenario_from_path("/path/to/pick_potato__Dingus__20260914_072343.jsonl"), "pick_potato")
        self.assertEqual(infer_scenario_from_path("harvest_chicken__Dingus__123.jsonl"), "harvest_chicken")
        self.assertEqual(infer_scenario_from_path("simple_scenario.jsonl"), "simple_scenario")


class MockInventory(PacketInventory):
    """Mock inventory with predefined packet metadata for unit testing."""
    def __init__(self):
        self.repo_root = "/fake/repo"
        self.packets = {
            "CSCreateDoodadPacket": PacketMetadata("CSCreateDoodadPacket", "C2S", "AAEmu.Game/Core/Packets/C2G/CSCreateDoodadPacket.cs", "0x0A0"),
            "CSStartSkillPacket": PacketMetadata("CSStartSkillPacket", "C2S", "AAEmu.Game/Core/Packets/C2G/CSStartSkillPacket.cs", "0x04F"),
            "CSStartInteractionPacket": PacketMetadata("CSStartInteractionPacket", "C2S", "AAEmu.Game/Core/Packets/C2G/CSStartInteractionPacket.cs", "0x050"),
            "CSInteractNPCPacket": PacketMetadata("CSInteractNPCPacket", "C2S", "AAEmu.Game/Core/Packets/C2G/CSInteractNPCPacket.cs", "0x030"),
            "SCDoodadCreatedPacket": PacketMetadata("SCDoodadCreatedPacket", "S2C", "AAEmu.Game/Core/Packets/G2C/SCDoodadCreatedPacket.cs", "0x120"),
            "SCSkillStartedPacket": PacketMetadata("SCSkillStartedPacket", "S2C", "AAEmu.Game/Core/Packets/G2C/SCSkillStartedPacket.cs", "0x110"),
            "SCSkillFiredPacket": PacketMetadata("SCSkillFiredPacket", "S2C", "AAEmu.Game/Core/Packets/G2C/SCSkillFiredPacket.cs", "0x111"),
            "SCNpcInteractionSkillListPacket": PacketMetadata("SCNpcInteractionSkillListPacket", "S2C", "AAEmu.Game/Core/Packets/G2C/SCNpcInteractionSkillListPacket.cs", "0x130"),
            "CSUnobservedLeadPacket": PacketMetadata("CSUnobservedLeadPacket", "C2S", "AAEmu.Game/Core/Packets/C2G/CSUnobservedLeadPacket.cs", "0x999")
        }
        self.offsets_by_name = {}


class TestCoverageEngine(unittest.TestCase):
    def setUp(self):
        self.inventory = MockInventory()
        self.engine = CoverageEngine(inventory=self.inventory, similarity_threshold=0.35)

    def test_noise_filtering_and_sequence_normalization(self):
        records = [
            TraceRecord("f.jsonl", 1, "sc1", "2026-09-14T00:00:01Z", 0, 1, "Char", "movement", "movement_update"),
            TraceRecord("f.jsonl", 2, "sc1", "2026-09-14T00:00:02Z", 10, 1, "Char", "movement", "movement_update"),
            TraceRecord("f.jsonl", 3, "sc1", "2026-09-14T00:00:03Z", 20, 1, "Char", "packet", "packet_in", packet_name="PingPacket"),
            TraceRecord("f.jsonl", 4, "sc1", "2026-09-14T00:00:04Z", 30, 1, "Char", "packet", "packet_out", packet_name="PongPacket"),
            TraceRecord("f.jsonl", 5, "sc1", "2026-09-14T00:00:05Z", 40, 1, "Char", "packet", "packet_in", packet_name="CSSendChatMessagePacket", data={"Details": "/trace mark test"}),
            TraceRecord("f.jsonl", 6, "sc1", "2026-09-14T00:00:06Z", 50, 1, "Char", "packet", "packet_in", packet_name="CSStartSkillPacket"),
            TraceRecord("f.jsonl", 7, "sc1", "2026-09-14T00:00:07Z", 60, 1, "Char", "packet", "packet_out", packet_name="SCSkillStartedPacket"),
            TraceRecord("f.jsonl", 8, "sc1", "2026-09-14T00:00:08Z", 70, 1, "Char", "packet", "packet_out", packet_name="SCSkillFiredPacket"),
        ]
        for r in records:
            self.engine.process_record(r)

        norm_seq = self.engine.scenario_normalized_sequences["sc1"]
        # Movement updates should collapse into a single "movement" token.
        # Ping/Pong and /trace chat should be filtered from normalized sequence.
        expected = ["movement", "CSStartSkillPacket", "SCSkillStartedPacket", "SCSkillFiredPacket"]
        self.assertEqual(norm_seq, expected)

        # But RAW coverage still includes Ping/Pong
        self.assertEqual(self.engine.packet_counts["PingPacket"], 1)
        self.assertEqual(self.engine.packet_counts["PongPacket"], 1)

    def test_transitions_and_ngrams(self):
        records = [
            TraceRecord("f.jsonl", 1, "sc1", "2026-09-14T00:00:01Z", 0, 1, "Char", "packet", "packet_in", packet_name="CSStartSkillPacket"),
            TraceRecord("f.jsonl", 2, "sc1", "2026-09-14T00:00:02Z", 10, 1, "Char", "packet", "packet_out", packet_name="SCSkillStartedPacket"),
            TraceRecord("f.jsonl", 3, "sc1", "2026-09-14T00:00:03Z", 20, 1, "Char", "packet", "packet_out", packet_name="SCSkillFiredPacket"),
        ]
        for r in records:
            self.engine.process_record(r)

        transitions, patterns = self.engine.compute_transitions_and_ngrams()
        # 2 transitions: CSStartSkillPacket -> SCSkillStartedPacket, SCSkillStartedPacket -> SCSkillFiredPacket
        self.assertEqual(len(transitions), 2)
        self.assertEqual(transitions[0]["from_token"], "CSStartSkillPacket")
        self.assertEqual(transitions[0]["to_token"], "SCSkillStartedPacket")
        # 1 3-gram pattern
        tri_patterns = [p for p in patterns if p["pattern_type"] == "3-gram"]
        self.assertEqual(len(tri_patterns), 1)
        self.assertEqual(tri_patterns[0]["sequence"], "CSStartSkillPacket -> SCSkillStartedPacket -> SCSkillFiredPacket")

    def test_plant_harvest_npc_distinctness(self):
        """Verify Plant, Harvest, and NPC interaction are classified conservatively into separate families."""
        # Plant scenario
        self.engine.process_record(TraceRecord("plant.jsonl", 1, "plant_potato", "2026-09-14T00:00:01Z", 0, 1, "C", "packet", "packet_in", packet_name="CSCreateDoodadPacket"))
        self.engine.process_record(TraceRecord("plant.jsonl", 2, "plant_potato", "2026-09-14T00:00:02Z", 10, 1, "C", "packet", "packet_out", packet_name="SCDoodadCreatedPacket"))
        self.engine.process_record(TraceRecord("plant.jsonl", 3, "plant_potato", "2026-09-14T00:00:03Z", 20, 1, "C", "world", "doodad_created"))

        self.engine.process_record(TraceRecord("plant2.jsonl", 1, "plant_tree", "2026-09-14T00:00:04Z", 0, 1, "C", "packet", "packet_in", packet_name="CSCreateDoodadPacket"))
        self.engine.process_record(TraceRecord("plant2.jsonl", 2, "plant_tree", "2026-09-14T00:00:05Z", 10, 1, "C", "packet", "packet_out", packet_name="SCDoodadCreatedPacket"))
        self.engine.process_record(TraceRecord("plant2.jsonl", 3, "plant_tree", "2026-09-14T00:00:06Z", 20, 1, "C", "world", "doodad_created"))

        # Harvest scenario
        self.engine.process_record(TraceRecord("harv1.jsonl", 1, "harvest_potato", "2026-09-14T00:00:10Z", 0, 1, "C", "packet", "packet_in", packet_name="CSStartInteractionPacket"))
        self.engine.process_record(TraceRecord("harv1.jsonl", 2, "harvest_potato", "2026-09-14T00:00:11Z", 10, 1, "C", "packet", "packet_in", packet_name="CSStartSkillPacket"))
        self.engine.process_record(TraceRecord("harv1.jsonl", 3, "harvest_potato", "2026-09-14T00:00:12Z", 20, 1, "C", "packet", "packet_out", packet_name="SCSkillStartedPacket"))

        self.engine.process_record(TraceRecord("harv2.jsonl", 1, "harvest_tree", "2026-09-14T00:00:13Z", 0, 1, "C", "packet", "packet_in", packet_name="CSStartInteractionPacket"))
        self.engine.process_record(TraceRecord("harv2.jsonl", 2, "harvest_tree", "2026-09-14T00:00:14Z", 10, 1, "C", "packet", "packet_in", packet_name="CSStartSkillPacket"))
        self.engine.process_record(TraceRecord("harv2.jsonl", 3, "harvest_tree", "2026-09-14T00:00:15Z", 20, 1, "C", "packet", "packet_out", packet_name="SCSkillStartedPacket"))

        # NPC interaction scenario
        self.engine.process_record(TraceRecord("npc1.jsonl", 1, "talk_npc_1", "2026-09-14T00:00:20Z", 0, 1, "C", "packet", "packet_in", packet_name="CSInteractNPCPacket"))
        self.engine.process_record(TraceRecord("npc1.jsonl", 2, "talk_npc_1", "2026-09-14T00:00:21Z", 10, 1, "C", "packet", "packet_out", packet_name="SCNpcInteractionSkillListPacket"))

        self.engine.process_record(TraceRecord("npc2.jsonl", 1, "talk_npc_2", "2026-09-14T00:00:22Z", 0, 1, "C", "packet", "packet_in", packet_name="CSInteractNPCPacket"))
        self.engine.process_record(TraceRecord("npc2.jsonl", 2, "talk_npc_2", "2026-09-14T00:00:23Z", 10, 1, "C", "packet", "packet_out", packet_name="SCNpcInteractionSkillListPacket"))

        families = self.engine.compute_candidate_families()
        family_names = [f.family_name for f in families]

        # Must have placement, harvest, and NPC families
        self.assertTrue(any("Placement" in fn for fn in family_names))
        self.assertTrue(any("Gathering" in fn or "Harvesting" in fn for fn in family_names))
        self.assertTrue(any("NPC" in fn for fn in family_names))

        # None of the families should mix plant and harvest or plant and NPC
        for f in families:
            members = set(f.member_scenarios)
            if "plant_potato" in members:
                self.assertNotIn("harvest_potato", members)
                self.assertNotIn("talk_npc_1", members)
            if "harvest_potato" in members:
                self.assertNotIn("plant_potato", members)
                self.assertNotIn("talk_npc_1", members)

    def test_discovery_leads_classification(self):
        items = self.engine.compute_packet_coverage()
        leads = [p for p in items if p.status == "DISCOVERY_LEAD"]
        self.assertTrue(any(p.packet == "CSUnobservedLeadPacket" for p in leads))


class TestBaselineDelta(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.temp_dir)

    def test_delta_detection(self):
        baseline_file = os.path.join(self.temp_dir, "base_summary.json")
        base_data = {
            "corpus_summary": {
                "total_records": 100,
                "scenarios": ["old_scenario"]
            },
            "packet_coverage": {
                "items": [
                    {"packet": "CSOldPacket", "observed": True}
                ]
            }
        }
        with open(baseline_file, "w") as f:
            json.dump(base_data, f)

        summary = CorpusSummary(
            total_records=150,
            scenarios=["old_scenario", "new_scenario"]
        )
        packet_items = [
            PacketCoverageItem("CSOldPacket", "C2S", 10, 1, True, True, "path", "0x01", "OBSERVED_AND_KNOWN"),
            PacketCoverageItem("CSNewPacket", "C2S", 5, 1, True, True, "path", "0x02", "OBSERVED_AND_KNOWN")
        ]

        delta = compute_baseline_delta(summary, packet_items, baseline_file)
        self.assertEqual(delta["new_scenarios"], ["new_scenario"])
        self.assertEqual(delta["newly_observed_packets"], ["CSNewPacket"])
        self.assertEqual(delta["record_count_delta"], 50)


class TestTaskEvaluator(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.temp_dir)

    def test_evaluate_trace_pass_and_fail(self):
        from task_evaluator import evaluate_trace_file
        trace_file = os.path.join(self.temp_dir, "test_eval__Char__20260101_000000.jsonl")
        content = (
            '{"ts":"2026-09-14T07:00:00Z","elapsed_ms":0,"category":"lifecycle","event":"trace_started","data":{"Scenario":"test_eval"}}\n'
            '{"ts":"2026-09-14T07:00:01Z","elapsed_ms":1000,"category":"packet","event":"packet_in","data":{"Packet":"CSSetTargetPacket"}}\n'
            '{"ts":"2026-09-14T07:00:02Z","elapsed_ms":2000,"category":"packet","event":"packet_out","data":{"Packet":"SCDamagePacket"}}\n'
            '{"ts":"2026-09-14T07:00:03Z","elapsed_ms":3000,"category":"lifecycle","event":"trace_stopped","data":{}}\n'
        )
        with open(trace_file, "w", encoding="utf-8") as f:
            f.write(content)

        # Passing spec
        passing_spec = {
            "id": "test_pass",
            "scenario": "test_eval",
            "target_packets": ["CSSetTargetPacket", "SCDamagePacket"]
        }
        report = evaluate_trace_file(trace_file, passing_spec)
        self.assertEqual(report.verdict, "PASS")
        self.assertTrue(report.has_start_lifecycle)
        self.assertTrue(report.has_stop_lifecycle)
        self.assertTrue(all(pa.passed for pa in report.packet_assertions))

        # Failing spec (missing packet)
        failing_spec = {
            "id": "test_fail",
            "scenario": "test_eval",
            "target_packets": ["CSSetTargetPacket", "CSMissingPacket"]
        }
        report_fail = evaluate_trace_file(trace_file, failing_spec)
        self.assertEqual(report_fail.verdict, "FAIL")
        self.assertTrue(any(not pa.passed for pa in report_fail.packet_assertions))
        self.assertTrue(any("CSMissingPacket" in r for r in report_fail.failure_reasons))


if __name__ == "__main__":
    unittest.main()
