import json
from pathlib import Path
import tempfile
import unittest

from coverage_engine import CoverageEngine
from task_triage import triage_tasks


class Inventory:
    def get(self, name):
        return object() if name in {'ActionPacket', 'ResultPacket'} else None


class TaskTriageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.files = []
        self.task = {'id': 'action', 'scenario': 'action', 'title': 'Action',
                     'target_packets': ['ActionPacket', 'ResultPacket'], 'priority': 'High'}

    def capture(self, scenario, packets, stop=True, refusal=False):
        path = Path(self.temp.name) / f'{scenario}__{len(self.files)}.jsonl'
        records = [{'category': 'lifecycle', 'event': 'trace_started', 'data': {'Scenario': scenario}}]
        records += [{'category': 'packet', 'event': 'packet_in', 'data': {'Packet': p}} for p in packets]
        if refusal:
            records.append({'category': 'refusal', 'event': 'skill_refused'})
        if stop:
            records.append({'category': 'lifecycle', 'event': 'trace_stopped'})
        path.write_text('\n'.join(json.dumps(r) for r in records))
        self.files.append(str(path))
        return path

    def row(self):
        return triage_tasks([self.task], self.files, Inventory())[0]

    def test_existing_capture_is_not_recommended(self):
        self.capture('action', self.task['target_packets'])
        row = self.row()
        self.assertEqual('CAPTURE_AVAILABLE', row['status'])
        engine = CoverageEngine(Inventory())
        engine.task_findings = [row]
        self.assertEqual([], engine.compute_recommendations())
        self.assertEqual(64, len(row['captures'][0]['sha256']))

    def test_unknown_packet_blocks_capture_request(self):
        self.task['target_packets'].append('UnknownPacket')
        self.assertEqual('SPEC_REVIEW', self.row()['status'])

    def test_caveat_is_review_not_new_capture(self):
        self.capture('action', self.task['target_packets'], refusal=True)
        self.assertEqual('CAPTURE_REVIEW', self.row()['status'])

    def test_other_scenario_is_candidate_not_pass(self):
        self.capture('action_extended', self.task['target_packets'])
        row = self.row()
        self.assertEqual('REUSE_REVIEW', row['status'])
        self.assertEqual([], row['captures'])
        self.assertEqual(1, len(row['reuse_candidates']))

    def test_separate_files_cannot_combine_to_pass(self):
        self.capture('action', ['ActionPacket'])
        self.capture('action', ['ResultPacket'])
        self.assertEqual('CAPTURE_REVIEW', self.row()['status'])
        self.assertTrue(all(c['evaluation']['verdict'] == 'FAIL' for c in self.row()['captures']))

    def test_missing_required_event_blocks_capture_available(self):
        self.task['target_events'] = ['resource:money_changed']
        self.capture('action', self.task['target_packets'])
        self.assertEqual('CAPTURE_REVIEW', self.row()['status'])

    def test_missing_complete_evidence_is_location_review(self):
        self.task['status'] = 'Complete'
        self.assertEqual('EVIDENCE_LOCATION_REVIEW', self.row()['status'])

    def test_persistence_requires_state_evidence_not_packet_capture(self):
        self.task['evaluation_kind'] = 'state_comparison'
        self.task['target_packets'] = ['UnknownLegacyPacket']
        row = self.row()
        self.assertEqual('STATE_EVIDENCE_REVIEW', row['status'])
        engine = CoverageEngine(Inventory())
        engine.task_findings = [row]
        self.assertEqual([], engine.compute_recommendations())

    def test_multi_actor_task_cannot_pass_from_one_complete_packet_set(self):
        self.task['evaluation_kind'] = 'multi_actor'
        self.capture('action', self.task['target_packets'])
        row = self.row()
        self.assertEqual('MULTI_ACTOR_REVIEW', row['status'])
        engine = CoverageEngine(Inventory())
        engine.task_findings = [row]
        self.assertEqual([], engine.compute_recommendations())

    def test_no_evidence_recommends_capture(self):
        engine = CoverageEngine(Inventory())
        engine.task_findings = [self.row()]
        self.assertEqual('action', engine.compute_recommendations()[0].scenario_name)

    def test_mixed_scenarios_do_not_count_as_exact_capture(self):
        path = self.capture('action', ['ActionPacket'])
        with path.open('a') as stream:
            stream.write('\n' + json.dumps({'category': 'lifecycle', 'event': 'trace_started', 'data': {'Scenario': 'other'}}))
            stream.write('\n' + json.dumps({'category': 'packet', 'event': 'packet_in', 'data': {'Packet': 'ResultPacket'}}))
        self.assertEqual('REUSE_REVIEW', self.row()['status'])

    def test_declared_scenario_overrides_filename(self):
        path = self.capture('action', self.task['target_packets'])
        renamed = path.with_name('unrelated.jsonl')
        path.rename(renamed)
        self.files = [str(renamed)]
        self.assertEqual('CAPTURE_AVAILABLE', self.row()['status'])

    def test_filtered_analysis_excludes_other_tasks_and_candidates(self):
        self.capture('other', self.task['target_packets'])
        rows = triage_tasks([self.task], self.files, Inventory(), {'action'})
        self.assertEqual('CAPTURE_NEEDED', rows[0]['status'])
        self.assertEqual([], triage_tasks([self.task], self.files, Inventory(), {'absent'}))

    def test_noise_is_retained_in_raw_counts(self):
        from trace_parser import TraceRecord
        engine = CoverageEngine(Inventory())
        for name in ['FastPingPacket', 'FastPongPacket']:
            engine.process_record(TraceRecord('f', 1, 'action', None, None, None, None,
                                              'packet', 'packet_in', packet_name=name))
            self.assertEqual(1, engine.packet_counts[name])
        self.assertEqual([], engine.scenario_normalized_sequences['action'])


if __name__ == '__main__':
    unittest.main()
