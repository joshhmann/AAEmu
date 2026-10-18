import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from observe import BridgeQuestAdapter, SnapshotAdapter, active_quests, completed_flags
from test_evaluate import manifest, stamp


def snapshot(second=0):
    m = manifest()
    return {"time": stamp(second), "source": m["source_sha"],
            "container": {"id": "fixture-runtime", "running": True, "oom": False, "restart_count": 0},
            "status": {"Success": True, "Bots": [{"Id": m["bot_id"], "Name": m["bot_name"], "State": "Active"}]},
            "brains": {m["bot_name"]: {"CharacterId": m["bot_id"], "Name": m["bot_name"],
                                        "History": [], "WakeSequence": 1}},
            "persisted_db": {"completed": "", "quests": "", "items": ""},
            "world_characters": [{"Id": m["bot_id"], "X": 10, "Y": 20, "Z": 30}], "critical": []}


def terminal(trace="trace", reason="ordinary result"):
    return {"TraceId": trace, "Action": "Observe", "Result": "Completed", "Reason": reason,
            "Goal": None, "Policy": None, "DecisionQuestId": None, "WakeSequence": None}


class AdapterTests(unittest.TestCase):
    def test_native_completed_bit_order(self):
        body = (1 << (251 % 64)).to_bytes(8, "little").hex()
        self.assertEqual({251}, completed_flags(f"1\t3\t{body}", 1))
        self.assertEqual(set(), completed_flags(f"2\t3\t{body}", 1))

    def test_failed_dropped_quests_are_not_new_eligible_acceptances(self):
        self.assertEqual({251}, active_quests("1\t251\t1\t00\n1\t324\t2\t00\n1\t330\t4\t00", 1))

    def test_initial_completed_flag_is_baseline_not_new_completion(self):
        row = snapshot()
        body = (1 << (251 % 64)).to_bytes(8, "little").hex()
        row["persisted_db"]["completed"] = f"1\t3\t{body}"
        adapter = SnapshotAdapter(manifest())
        self.assertFalse(any(e["kind"] == "quest_completed" for e in adapter.consume(row, "raw", 1)))
        row["time"] = stamp(1)
        self.assertFalse(any(e["kind"] == "quest_completed" for e in adapter.consume(row, "raw", 2)))

    def test_missing_db_and_wrong_actor_are_gaps(self):
        for corrupt in ("missing-db", "wrong-brain", "restarted-runtime"):
            with self.subTest(corrupt=corrupt):
                row = snapshot()
                if corrupt == "missing-db": del row["persisted_db"]["completed"]
                elif corrupt == "wrong-brain": row["brains"][manifest()["bot_name"]]["CharacterId"] += 1
                else: row["container"]["restart_count"] = 1
                events = SnapshotAdapter(manifest()).consume(row, "raw", 1)
                self.assertEqual("telemetry_gap", events[-1]["kind"])

    def test_lost_rolling_history_refuses_complete_capture(self):
        adapter = SnapshotAdapter(manifest())
        first = snapshot()
        first["brains"][manifest()["bot_name"]]["History"] = [terminal("old")]
        adapter.consume(first, "raw", 1)
        second = snapshot(1)
        second["brains"][manifest()["bot_name"]]["History"] = [terminal(f"new{i}") for i in range(20)]
        self.assertEqual("telemetry_gap", adapter.consume(second, "raw", 2)[-1]["kind"])

    def test_terminal_duplicates_are_deduplicated_and_conflicts_refused(self):
        adapter = SnapshotAdapter(manifest())
        row = snapshot()
        row["brains"][manifest()["bot_name"]]["History"] = [terminal()]
        first = adapter.consume(row, "raw", 1)
        self.assertEqual(1, sum(e["kind"] == "action_result" for e in first))
        row["time"] = stamp(1)
        self.assertFalse(any(e["kind"] == "action_result" for e in adapter.consume(row, "raw", 2)))
        row["time"] = stamp(2)
        row["brains"][manifest()["bot_name"]]["History"][0]["Reason"] = "changed terminal"
        self.assertEqual("telemetry_gap", adapter.consume(row, "raw", 3)[-1]["kind"])

    def test_bridge_receipt_is_not_a_snapshot_and_real_missing_snapshot_is_gap(self):
        adapter = BridgeQuestAdapter(manifest())
        receipt = {"phase": "OBSERVE", "request": {"method": "POST", "path": "/api/actors/interrupt"}, "output": {"success": True}}
        self.assertEqual([], adapter.consume(receipt, "raw", 1))
        broken = {"phase": "OBSERVE", "request": {"cmd": "quest", "sub": "observe"}, "output": {"error": "missing"}}
        self.assertEqual("telemetry_gap", adapter.consume(broken, "raw", 2)[-1]["kind"])


class SupervisorTests(unittest.TestCase):
    def run_observer(self, directory, raw_content, *options):
        root = Path(directory)
        m = manifest()
        m.update(started_utc=stamp(0), collection_mode="historical-replay")
        (root / "manifest.json").write_text(json.dumps(m))
        (root / "raw.jsonl").write_text(raw_content)
        command = [sys.executable, str(Path(__file__).with_name("run.py")), "supervise",
                   "--manifest", str(root / "manifest.json"), "--snapshots", str(root / "raw.jsonl"),
                   "--out", str(root / "result"), "--seconds", "1", *options]
        return subprocess.run(command, capture_output=True, text=True, timeout=8), command

    def test_incomplete_input_reports_inconclusive_and_reaps_only_owned_child(self):
        with tempfile.TemporaryDirectory() as directory:
            result, _ = self.run_observer(directory, '{"partial":')
            self.assertEqual(3, result.returncode)
            receipt = json.loads((Path(directory) / "result.supervision.json").read_text())
            self.assertTrue(receipt["child_reaped"])
            self.assertFalse(receipt["forced_stop"])
            self.assertIn("file observer only", receipt["owns"])
            report = json.loads((Path(directory) / "result/verdict.json").read_text())
            self.assertEqual("INCONCLUSIVE", report["status"])
            self.assertEqual('{"partial":', (Path(directory) / "raw.jsonl").read_text())

    def test_existing_results_refused_without_overwriting_custody(self):
        with tempfile.TemporaryDirectory() as directory:
            _, command = self.run_observer(directory, json.dumps(snapshot()) + "\n")
            receipt_path = Path(directory) / "result.supervision.json"
            before = receipt_path.read_bytes()
            again = subprocess.run(command, capture_output=True, timeout=8)
            self.assertEqual(3, again.returncode)
            self.assertEqual(before, receipt_path.read_bytes())

    def test_empty_follow_is_bounded_and_does_not_invent_success(self):
        with tempfile.TemporaryDirectory() as directory:
            result, _ = self.run_observer(directory, "", "--follow")
            self.assertEqual(3, result.returncode)
            report = json.loads((Path(directory) / "result/verdict.json").read_text())
            self.assertEqual("INCONCLUSIVE", report["status"])
            self.assertEqual("observer_deadline", report["finish_reason"])
            self.assertEqual(0, report["collection"]["controls_sent"])


if __name__ == "__main__":
    unittest.main()

class HuntSnapshotTests(unittest.TestCase):
    def config(self):
        m = manifest()
        m['acceptance'].pop('item_id')
        m['acceptance'].pop('item_count')
        m['acceptance']['objective'] = {'mode': 'native_hunt', 'act_id': 26549, 'required_progress': 1}
        return m

    def row(self, second, count, actor=700, quest=251, act=26549):
        row = snapshot(second)
        row['log_lines'] = [f'{stamp(second)} [INFO] QuestObjectiveTargetDiag cycle=c{second} char=297 actor={actor} quest={quest} objective=[ok:act{act}:huntx20] source=[ok:npc] relevance=true have={count} need=5 raw=0']
        row['log_tail_limit'] = 5000
        row['sampled_log_lines'] = 1
        return row

    def test_baseline_then_exact_native_credit_has_raw_provenance(self):
        adapter = SnapshotAdapter(self.config())
        self.assertFalse(any(e['kind'] == 'objective_progress' for e in adapter.consume(self.row(0, 4), 'raw.jsonl', 1)))
        events = adapter.consume(self.row(1, 5), 'raw.jsonl', 2)
        credit = next(e for e in events if e['kind'] == 'objective_progress')
        self.assertEqual({'quest_id': 251, 'act_id': 26549, 'before': 4, 'after': 5, 'authority': 'native quest progress'}, credit['data'])
        self.assertEqual('log_lines[0]', credit['source']['field'])
        self.assertEqual(64, len(credit['source']['raw_sha256']))
        self.assertEqual(700, credit['source']['actor_id'])

    def test_wrong_act_or_quest_and_motion_never_supply_credit(self):
        for change in ({'act': 26550}, {'quest': 999}):
            adapter = SnapshotAdapter(self.config())
            adapter.consume(self.row(0, 0), 'raw', 1)
            row = self.row(1, 5, **change)
            row['world_characters'][0]['X'] += 5
            events = adapter.consume(row, 'raw', 2)
            self.assertTrue(any(e['kind'] == 'movement' for e in events))
            self.assertFalse(any(e['kind'] == 'objective_progress' for e in events))

    def test_regression_actor_change_missing_logs_and_truncation_are_gaps(self):
        for failure in ('regress', 'actor', 'missing', 'truncated', 'future', 'malformed', 'no-timezone'):
            with self.subTest(failure=failure):
                adapter = SnapshotAdapter(self.config())
                adapter.consume(self.row(0, 2), 'raw', 1)
                row = self.row(1, 1 if failure == 'regress' else 3, actor=701 if failure == 'actor' else 700)
                if failure == 'missing': del row['log_lines']
                if failure == 'truncated': row['log_tail_limit'] = 1
                if failure == 'future': row['log_lines'][0] = row['log_lines'][0].replace(stamp(1), stamp(2))
                if failure == 'malformed': row['log_lines'][0] = row['log_lines'][0].replace('have=3', 'have=NA')
                if failure == 'no-timezone': row['log_lines'][0] = row['log_lines'][0].replace('+00:00', '')
                events = adapter.consume(row, 'raw', 2)
                self.assertEqual('telemetry_gap', events[-1]['kind'])

    def test_duplicate_native_line_does_not_double_credit(self):
        adapter = SnapshotAdapter(self.config())
        adapter.consume(self.row(0, 0), 'raw', 1)
        row = self.row(1, 1)
        row['log_lines'] *= 2
        row['sampled_log_lines'] = 2
        events = adapter.consume(row, 'raw', 2)
        self.assertEqual(1, sum(e['kind'] == 'objective_progress' for e in events))
        row['time'] = stamp(2)
        self.assertFalse(any(e['kind'] == 'objective_progress' for e in adapter.consume(row, 'raw', 3)))

    def test_snapshot_credit_completion_and_independent_followup_pass_without_items(self):
        from evaluate import Evaluator
        config = self.config()
        adapter, evaluator = SnapshotAdapter(config), Evaluator(config)
        first = self.row(0, 0)
        second = self.row(1, 1)
        third = self.row(2, 1)
        third['brains'][config['bot_name']]['History'] = [dict(terminal('turn'), Action='TurnInQuest', DecisionQuestId=251)]
        third['persisted_db']['completed'] = '297\t3\t' + (1 << (251 % 64)).to_bytes(8, 'little').hex()
        fourth = self.row(3, 1)
        fourth['brains'][config['bot_name']]['History'] = third['brains'][config['bot_name']]['History'] + [dict(terminal('accept'), Action='AcceptQuest', DecisionQuestId=324, Policy='quest-v1', Reason='eligible offer')]
        fourth['persisted_db']['completed'] = third['persisted_db']['completed']
        fourth['persisted_db']['quests'] = '297\t324\t1\t00'
        config['choices_unassigned'] = True
        for index, row in enumerate((first, second, third, fourth), 1):
            for event in adapter.consume(row, 'snapshots.jsonl', index):
                evaluator.consume(event)
        report = evaluator.finish('bounded synthetic snapshot proof', stamp(4))
        self.assertEqual('PASS', report['status'])
        self.assertTrue(all(report['criteria'].values()))
        self.assertNotIn('item_progress', report['criteria'])

    def test_native_audit_without_act_counters_cannot_grade_hunts(self):
        row = {'phase': 'OBSERVE', 'request': {'cmd': 'quest', 'sub': 'observe'}, 'recordedAtUtc': stamp(0), 'output': {}}
        events = BridgeQuestAdapter(self.config()).consume(row, 'native.jsonl', 1)
        self.assertEqual('telemetry_gap', events[-1]['kind'])
        self.assertIn('act-specific', events[-1]['data']['reason'])
