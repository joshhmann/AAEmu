"""Synthetic process/control evidence only: no AAEmu, gameplay, database or model calls.

The producer deliberately fabricates native-shaped receipts to exercise supervisor
custody and validation. Native admission/state reconciliation needs separate tests.
"""
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from execute import execute


PRODUCER = r'''
import hashlib,json,os,pathlib,sys,time
config=json.loads(pathlib.Path(sys.argv[1]).read_text())
root=pathlib.Path(os.environ['AAEMU_LOOP_OUTPUT']);root.mkdir()
def save(name, value):
    path=root/name
    temporary=path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value))
    temporary.replace(path)
save('synthetic-environment.json', {k:v for k,v in os.environ.items() if k.startswith('AAEMU_LOOP_')})
# Even raw files must not launch the quest evaluator for a nonquest loop.
(root/'observations.jsonl').write_text('synthetic nonquest data\n')
if config.get('replay'):
    previous=pathlib.Path(config['replay'])/'test'
    for name in ('loop-checkpoint.json','report.json'):
        (root/name).write_bytes((previous/name).read_bytes())
    sys.exit(0)
if config.get('no_tests'):
    print('Skipped: 1' if config['no_tests']=='skipped' else 'No test matches the given testcase filter')
    sys.exit(0)
if config.get('hang'):
    if config.get('output_cap'): print('x'*100000,flush=True)
    time.sleep(30)
m=config['manifest'];contract=m['native_contract'];mode=os.environ['AAEMU_LOOP_MODE']
old=None;predecessor=None
initial={'syntheticCounter':0}
if 'AAEMU_LOOP_RESUME' in os.environ:
    path=pathlib.Path(os.environ['AAEMU_LOOP_RESUME']);raw=path.read_bytes();old=json.loads(raw)
    assert hashlib.sha256(raw).hexdigest()==os.environ['AAEMU_LOOP_RESUME_SHA256']
    predecessor={'path':str(path),'sha256':hashlib.sha256(raw).hexdigest(),'runId':old['runId']}
    initial=old['durableState']
portions=contract['portions']
failing=config.get('fail',False)
index=(old['nextPortion'] if old else 1) if failing else len(portions)
failure={'portion':portions[index],'reason':'synthetic observed predicate absent','atUtc':'2026-10-15T00:00:00Z'} if failing else None
checkpoint={'schema':'aaemu-loop-development/1','mode':mode,'runId':os.environ['AAEMU_LOOP_RUN_ID'],
 'identity':{'scenario':contract['scenario'],'databaseId':m['database_id'],'characterId':m['bot_id'],'botName':m['bot_name']},
 'source':{'head':m['source_sha'],'publishSourceIdentity':contract['PublishSourceIdentity'],
           **{k:contract[k] for k in ('testAssemblySha256','runtimeGameSha256','canonicalMd5','runtimeMd5')}},
 'portions':portions,'nextPortion':index,'currentPortion':portions[index] if failing else None,
 'firstFailure':failure,'predecessor':predecessor,'initialState':initial,
 'durableState':{'syntheticCounter':1},'continuationData':{'syntheticReceipts':[]},
 'quiescent':True,'durableVerified':True,'resumable':failing and mode=='development',
 'status':'FAIL' if failing else 'DEVELOPMENT_PASS' if mode=='development' else 'PASS'}
save('synthetic-attempted-portions.json', portions[old['nextPortion'] if old else 0:index+1 if failing else len(portions)])
if failing:
    progress=dict(checkpoint,quiescent=False,durableVerified=False,resumable=False,durableState=None)
    if config.get('progress_run_id'):progress['runId']=config['progress_run_id']
    save('loop-progress.json',progress)
    deadline=time.monotonic()+4
    while not pathlib.Path(os.environ['AAEMU_LOOP_STOP_FILE']).exists() and time.monotonic()<deadline:time.sleep(.01)
    if not pathlib.Path(os.environ['AAEMU_LOOP_STOP_FILE']).exists():sys.exit(7)
    save('synthetic-stop-observed.json',{'observed':True})
    time.sleep(config.get('cleanup_delay',0))
if config.get('erase_failure'):
    checkpoint.update(firstFailure=None,status='DEVELOPMENT_PASS',nextPortion=len(portions),currentPortion=None,resumable=False)
checkpoint.update(config.get('checkpoint_patch',{}))
for key in config.get('checkpoint_remove',[]):checkpoint.pop(key,None)
if config.get('request_stop'):
    pathlib.Path(os.environ['AAEMU_LOOP_STOP_FILE']).write_text(json.dumps({'code':'SYNTHETIC_STOP'}))
save('synthetic-cleanup-completed.json',{'synthetic_cleanup':True})
if not config.get('missing_checkpoint'):save('loop-checkpoint.json',checkpoint)
save('loop-progress.json',checkpoint)
report={'evidence_layer':'synthetic rig','passed':checkpoint['status']!='FAIL','loopExecution':checkpoint}
report.update(config.get('report_patch',{}))
if not config.get('missing_report'):save('report.json',report)
sys.exit(config.get('exit_code',1 if failing else 0))
'''


def manifest():
    return {'run_id': 'synthetic-supervisor', 'source_sha': 'synthetic-source',
            'database_id': 'synthetic-fixture', 'bot_id': 17, 'bot_name': 'SyntheticBot',
            'evidence_layer': 'synthetic rig',
            'native_contract': {'scenario': 'synthetic-nonquest-loop',
                                'PublishSourceIdentity': 'synthetic-publish',
                                'testAssemblySha256': 'synthetic-test-binary',
                                'runtimeGameSha256': 'synthetic-game-binary',
                                'canonicalMd5': 'synthetic-data', 'runtimeMd5': 'synthetic-data',
                                'portions': ['acquire', 'process', 'settle']}}


class LoopExecutionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.producer = self.base / 'synthetic-producer.py'
        self.producer.write_text(PRODUCER)
        self.sequence = 0

    def args(self, mode='development', resume=None, m=None, **config):
        self.sequence += 1
        prefix = self.base / str(self.sequence)
        m = copy.deepcopy(m or manifest())
        config['manifest'] = m
        config_path = prefix.with_suffix('.config.json')
        manifest_path = prefix.with_suffix('.manifest.json')
        config_path.write_text(json.dumps(config))
        manifest_path.write_text(json.dumps(m))
        return SimpleNamespace(out=str(prefix), manifest=str(manifest_path), loop_mode=mode,
                               resume=str(resume) if resume else None, seconds=3, cleanup_seconds=1,
                               max_bytes=1024**2, max_rss_mb=512,
                               command=[sys.executable, str(self.producer), str(config_path)])

    def run_case(self, **kwargs):
        args = self.args(**kwargs)
        with patch('execute.start_review', side_effect=AssertionError('No quest review for a loop')):
            code = execute(args)
        root = Path(args.out)
        receipt = json.loads((root / 'execution.json').read_text())
        self.assertTrue(receipt['children_reaped'])
        self.assertEqual(receipt['automatic_reruns'], 0)
        self.assertNotIn('observer_pid', receipt)
        self.assertFalse((root / 'observer').exists())
        return code, receipt, root

    def rewrite(self, path, change):
        value = json.loads(path.read_text())
        change(value)
        path.write_text(json.dumps(value))

    def assert_refused_before_launch(self, args):
        with patch('execute.subprocess.Popen') as spawn:
            with self.assertRaises((ValueError, KeyError, OSError)):
                execute(args)
            spawn.assert_not_called()
        self.assertFalse(Path(args.out).exists())

    def test_development_success_is_not_acceptance(self):
        code, receipt, root = self.run_case()
        self.assertEqual(code, 0)
        self.assertEqual(receipt['status'], 'DEVELOPMENT_PASS')
        self.assertEqual(receipt['cleanup_state'], 'concluded')
        self.assertTrue(receipt['loop_evidence_valid'])
        self.assertEqual(receipt['checkpoint_sha256'], hashlib.sha256(
            (root / 'test/loop-checkpoint.json').read_bytes()).hexdigest())
        self.assertEqual(receipt['review_status'], 'not_applicable')
        self.assertIsNone(receipt['native_run_id'])
        self.assertEqual(receipt['loop_run_id'], receipt['expected_loop_run_id'])
        self.assertRegex(receipt['expected_loop_run_id'], r'^[0-9a-f]{32}$')

    def test_optional_native_report_run_id_is_preserved(self):
        code, receipt, _ = self.run_case(report_patch={'runId': 'synthetic-native-report'})
        self.assertEqual(code, 0)
        self.assertEqual(receipt['native_run_id'], 'synthetic-native-report')

    def test_fresh_acceptance_is_pass_and_environment_is_explicit(self):
        with patch.dict(os.environ, {'AAEMU_LOOP_RESUME': '/stale', 'AAEMU_LOOP_RESUME_SHA256': 'stale',
                                     'AAEMU_LOOP_MODE': 'development', 'AAEMU_LOOP_RUN_ID': 'stale'}):
            code, receipt, root = self.run_case(mode='acceptance')
        self.assertEqual(code, 0)
        self.assertEqual(receipt['status'], 'PASS')
        env = json.loads((root / 'test/synthetic-environment.json').read_text())
        self.assertEqual(env['AAEMU_LOOP_MODE'], 'acceptance')
        self.assertEqual(env['AAEMU_LOOP_RUN_ID'], receipt['expected_loop_run_id'])
        self.assertNotEqual(env['AAEMU_LOOP_RUN_ID'], 'stale')
        self.assertNotIn('AAEMU_LOOP_RESUME', env)
        self.assertNotIn('AAEMU_LOOP_RESUME_SHA256', env)
        self.assertIsNone(receipt['predecessor'])

    def test_first_failure_requests_cleanup_without_automatic_retry(self):
        code, receipt, root = self.run_case(fail=True, cleanup_delay=.15)
        self.assertEqual(code, 1)
        self.assertEqual(receipt['first_failure']['portion'], 'process')
        self.assertEqual(receipt['failed_portion'], 'process')
        self.assertFalse(receipt['forced_stop'])
        self.assertEqual(receipt['cleanup_state'], 'concluded')
        self.assertTrue((root / 'test/synthetic-stop-observed.json').is_file())
        self.assertEqual(json.loads((root / 'request-stop.json').read_text())['code'], 'LOOP_FIRST_FAILURE')
        self.assertEqual(json.loads((root / 'test/synthetic-attempted-portions.json').read_text()), ['acquire', 'process'])

    def test_explicit_resume_allows_repaired_source_and_keeps_old_failure(self):
        _, _, previous = self.run_case(fail=True)
        checkpoint = previous / 'test/loop-checkpoint.json'
        old_bytes = checkpoint.read_bytes()
        m = manifest()
        m['source_sha'] = 'synthetic-repaired-source'
        m['native_contract'].update(PublishSourceIdentity='synthetic-repaired-publish',
                                    testAssemblySha256='synthetic-repaired-test',
                                    runtimeGameSha256='synthetic-repaired-game')
        code, receipt, root = self.run_case(resume=previous, m=m)
        self.assertEqual(code, 0)
        self.assertEqual(receipt['status'], 'DEVELOPMENT_PASS')
        self.assertEqual(checkpoint.read_bytes(), old_bytes)
        self.assertEqual(json.loads((previous / 'execution.json').read_text())['status'], 'FAIL')
        self.assertEqual(receipt['predecessor']['sha256'], hashlib.sha256(old_bytes).hexdigest())
        env = json.loads((root / 'test/synthetic-environment.json').read_text())
        self.assertEqual(env['AAEMU_LOOP_RESUME'], str(checkpoint))
        self.assertEqual(env['AAEMU_LOOP_RESUME_SHA256'], receipt['predecessor']['sha256'])
        self.assertEqual(json.loads((root / 'test/synthetic-attempted-portions.json').read_text()), ['process', 'settle'])

    def test_acceptance_and_implicit_mode_refuse_resume(self):
        for mode in ('acceptance', None):
            with self.subTest(mode=mode):
                self.assert_refused_before_launch(self.args(mode=mode, resume=self.base / 'missing'))

    def test_resume_refuses_changed_hash_before_launch(self):
        _, _, previous = self.run_case(fail=True)
        path = previous / 'test/loop-checkpoint.json'
        path.write_bytes(path.read_bytes() + b' ')
        self.assert_refused_before_launch(self.args(resume=previous))

    def test_resume_refuses_unsafe_execution_before_launch(self):
        _, _, previous = self.run_case(fail=True)
        path = previous / 'execution.json'
        original = path.read_text()
        for key, value in (('forced_stop', True), ('children_reaped', False), ('cleanup_state', 'unproved'),
                           ('status', 'PASS'), ('checkpoint_sha256', None), ('loop_run_id', 'stale'),
                           ('source_sha', 'stale'), ('loop_evidence_valid', False),
                           ('native_run_id', 'stale'), ('test_exit_code', -9),
                           ('native_test_passed', True), ('native_identity_matches', False),
                           ('expected_loop_run_id', 'stale')):
            with self.subTest(key=key):
                path.write_text(original)
                self.rewrite(path, lambda row: row.update({key: value}))
                self.assert_refused_before_launch(self.args(resume=previous))

    def test_resume_refuses_fixture_bot_data_and_portion_changes(self):
        _, _, previous = self.run_case(fail=True)
        changes = [('database_id', 'other-fixture'), ('bot_id', 18), ('bot_name', 'OtherBot'),
                   ('scenario', 'other-loop'), ('portions', ['process', 'acquire', 'settle']),
                   ('data', 'other-data')]
        for key, value in changes:
            with self.subTest(key=key):
                m = manifest()
                if key == 'data':
                    m['native_contract'].update(canonicalMd5=value, runtimeMd5=value)
                elif key in ('scenario', 'portions'):
                    m['native_contract'][key] = value
                else:
                    m[key] = value
                self.assert_refused_before_launch(self.args(resume=previous, m=m))

    def test_resume_refuses_checkpoint_boundary_or_settlement_even_with_updated_hash(self):
        _, _, previous = self.run_case(fail=True)
        path = previous / 'test/loop-checkpoint.json'
        original = path.read_text()
        for key, value in (('quiescent', False), ('durableVerified', False), ('resumable', False),
                           ('currentPortion', 'settle'), ('nextPortion', -1), ('durableState', None),
                           ('durableState', {'syntheticCounter': 999}),
                           ('continuationData', None), ('mode', 'acceptance')):
            with self.subTest(key=key):
                path.write_text(original)
                self.rewrite(path, lambda row: row.update({key: value}))
                self.rewrite(previous / 'execution.json', lambda row: row.update(
                    checkpoint_sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
                self.assert_refused_before_launch(self.args(resume=previous))

    def test_zero_skipped_missing_and_malformed_native_results_never_pass(self):
        cases = [{'no_tests': 'zero'}, {'no_tests': 'skipped'}, {'missing_report': True},
                 {'missing_checkpoint': True}, {'report_patch': {'loopExecution': {}}},
                 {'report_patch': {'passed': False}}, {'report_patch': {'passed': 'true'}},
                 {'checkpoint_patch': {'portions': [], 'nextPortion': 0}},
                 {'checkpoint_patch': {'nextPortion': 1, 'currentPortion': 'process'}},
                 {'checkpoint_patch': {'status': 'PASS'}},
                 {'checkpoint_patch': {'quiescent': False}},
                 {'checkpoint_patch': {'durableVerified': False}}]
        for config in cases:
            with self.subTest(config=config):
                code, receipt, _ = self.run_case(**config)
                self.assertNotEqual(code, 0)
                self.assertFalse(receipt['loop_evidence_valid'])
                self.assertEqual(receipt['cleanup_state'], 'unproved')

    def test_identity_source_binary_and_data_mismatch_never_pass(self):
        for key in ('botName', 'characterId', 'scenario', 'databaseId'):
            with self.subTest(key=key):
                identity = {'scenario': 'synthetic-nonquest-loop', 'databaseId': 'synthetic-fixture',
                            'characterId': 17, 'botName': 'SyntheticBot'}
                identity[key] = 'foreign'
                code, _, _ = self.run_case(checkpoint_patch={'identity': identity})
                self.assertNotEqual(code, 0)
        for key in ('head', 'publishSourceIdentity', 'testAssemblySha256', 'runtimeGameSha256', 'canonicalMd5', 'runtimeMd5'):
            with self.subTest(source=key):
                source = {'head': 'synthetic-source', 'publishSourceIdentity': 'synthetic-publish',
                          'testAssemblySha256': 'synthetic-test-binary', 'runtimeGameSha256': 'synthetic-game-binary',
                          'canonicalMd5': 'synthetic-data', 'runtimeMd5': 'synthetic-data'}
                source[key] = 'foreign'
                code, _, _ = self.run_case(checkpoint_patch={'source': source})
                self.assertNotEqual(code, 0)

    def test_observed_failure_cannot_be_erased_by_final_success(self):
        code, receipt, _ = self.run_case(fail=True, erase_failure=True, exit_code=0)
        self.assertNotEqual(code, 0)
        self.assertEqual(receipt['first_failure']['portion'], 'process')
        self.assertIn('first observed failure', receipt['loop_evidence_error'])

    def test_resume_final_receipt_cannot_lie_about_initial_state(self):
        _, _, previous = self.run_case(fail=True)
        code, receipt, _ = self.run_case(resume=previous, checkpoint_patch={'initialState': {'syntheticCounter':999}})
        self.assertNotEqual(code, 0)
        self.assertIn('admitted state', receipt['loop_evidence_error'])

    def test_stale_matching_acceptance_report_and_checkpoint_are_rejected(self):
        code, previous_receipt, previous = self.run_case(mode='acceptance')
        self.assertEqual(code, 0)
        code, receipt, root = self.run_case(mode='acceptance', replay=str(previous))
        self.assertEqual(receipt['test_exit_code'], 0)
        self.assertNotEqual(code, 0)
        self.assertEqual(receipt['status'], 'INCONCLUSIVE')
        self.assertNotEqual(receipt['expected_loop_run_id'], previous_receipt['expected_loop_run_id'])
        self.assertIn('not from this supervisor invocation', receipt['loop_evidence_error'])
        self.assertEqual((root / 'test/loop-checkpoint.json').read_bytes(),
                         (previous / 'test/loop-checkpoint.json').read_bytes())
        self.assertEqual((root / 'test/report.json').read_bytes(), (previous / 'test/report.json').read_bytes())

    def test_foreign_progress_run_id_requests_stop_and_refuses_final_receipt(self):
        code, receipt, root = self.run_case(fail=True, progress_run_id='stale-progress')
        self.assertNotEqual(code, 0)
        self.assertIn('not from this supervisor invocation', receipt['loop_progress_error'])
        self.assertEqual(receipt['cleanup_state'], 'unproved')
        self.assertEqual(json.loads((root / 'request-stop.json').read_text())['code'], 'LOOP_PROGRESS_INVALID')

    def test_success_requires_object_durable_state_in_both_modes(self):
        cases = [{'checkpoint_remove': ['durableState']},
                 *({'checkpoint_patch': {'durableState': value}} for value in (None, [], 'stale', 0))]
        for mode in ('development', 'acceptance'):
            for config in cases:
                with self.subTest(mode=mode, config=config):
                    code, receipt, _ = self.run_case(mode=mode, **config)
                    self.assertNotEqual(code, 0)
                    self.assertFalse(receipt['loop_evidence_valid'])
                    self.assertEqual(receipt['cleanup_state'], 'unproved')
                    self.assertIn('safe settlement', receipt['loop_evidence_error'])

    def test_interrupted_acceptance_cannot_become_pass(self):
        code, receipt, _ = self.run_case(mode='acceptance', request_stop=True)
        self.assertNotEqual(code, 0)
        self.assertEqual(receipt['status'], 'INCONCLUSIVE')
        self.assertTrue(receipt['stop_requested'])
        self.assertEqual(receipt['cleanup_state'], 'concluded')

    def test_child_failure_cannot_be_overridden_by_native_pass(self):
        code, receipt, _ = self.run_case(exit_code=5)
        self.assertEqual(code, 1)
        self.assertEqual(receipt['test_exit_code'], 5)
        self.assertEqual(receipt['status'], 'FAIL')

    def test_cleanup_timeout_and_resource_cap_never_claim_settlement(self):
        for cap in (False, True):
            with self.subTest(output_cap=cap):
                args = self.args(hang=True, output_cap=cap)
                args.seconds = .2
                args.cleanup_seconds = .1
                if cap:
                    args.max_bytes = 20000
                self.assertEqual(execute(args), 3)
                receipt = json.loads((Path(args.out) / 'execution.json').read_text())
                self.assertTrue(receipt['forced_stop'])
                self.assertTrue(receipt['children_reaped'])
                self.assertEqual(receipt['cleanup_state'], 'unproved')

    def test_actual_cli_pass_and_resume_mode_rejection(self):
        args = self.args(mode='acceptance')
        cli = [sys.executable, str(Path(__file__).with_name('execute.py')), '--manifest', args.manifest,
               '--out', args.out, '--seconds', '3', '--cleanup-seconds', '1', '--loop-mode', 'acceptance']
        result = subprocess.run(cli + ['--'] + args.command, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        receipt = json.loads((Path(args.out) / 'execution.json').read_text())
        self.assertEqual(receipt['status'], 'PASS')
        rejected = subprocess.run(cli + ['--resume', '/not-read', '--'] + args.command,
                                  capture_output=True, text=True, timeout=10)
        self.assertEqual(rejected.returncode, 2)
        self.assertIn('acceptance must be fresh', rejected.stderr)


if __name__ == '__main__':
    unittest.main()
