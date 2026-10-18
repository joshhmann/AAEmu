"""Exercise automatic execution and cleanup using a synthetic test process."""
import json
from pathlib import Path
import sys
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from execute import execute, native_identity_matches
import test_evaluate as fixtures
from test_evaluate import manifest, stamp, event, terminal


PRODUCER = '''import json,os,pathlib,time,sys
root=pathlib.Path(os.environ['E2E_QUEST_OBSERVER_DIRECTORY']);root.mkdir()
rows=json.load(open(sys.argv[1]))
manifest=json.load(open(sys.argv[4]))
contract=manifest['native_contract']
with (root/'observations.jsonl').open('w') as out:
 for row in rows:
  out.write(json.dumps(row)+'\\n');out.flush();time.sleep(.04)
 if sys.argv[2]=='wait':
  deadline=time.monotonic()+3
  while not pathlib.Path(os.environ['E2E_QUEST_OBSERVER_STOP_FILE']).exists() and time.monotonic()<deadline: time.sleep(.02)
time.sleep(float(sys.argv[5]))
(root/'cleanup-completed.json').write_text(json.dumps({'synthetic_cleanup':True}))
(root/'report.json').write_text(json.dumps({'passed':sys.argv[3]=='true','runId':'synthetic-owned-test','evidence_layer':'synthetic rig', 'botName':manifest['bot_name'], 'characterId':manifest['bot_id'], 'scenario':contract['scenario'], 'observerDatabaseId':manifest['database_id'], 'canonicalMd5':contract['canonicalMd5'],'runtimeMd5':contract['runtimeMd5'], 'provenance':{'source':{'Head':manifest['source_sha'],'Unavailable':False},**{key:contract[key] for key in ['PublishSourceIdentity','testAssemblySha256','runtimeGameSha256']}}}))
sys.exit(0 if sys.argv[3]=='true' else 1)
'''


class ExecutionTests(unittest.TestCase):
    def run_case(self, rows, wait=False, native_pass=True, cleanup_delay=0, review_status='completed', tight_native_budget=False, review_output=None):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            (base/'producer.py').write_text(PRODUCER)
            (base/'events.json').write_text(json.dumps(rows))
            m=manifest();m.update(started_utc=stamp(0),collection_mode='historical-replay',input_format='normalized-events')
            m['native_contract']={'scenario':'synthetic-test','canonicalMd5':'synthetic-db','runtimeMd5':'synthetic-db','PublishSourceIdentity':'synthetic-publish','testAssemblySha256':'synthetic-assembly','runtimeGameSha256':'synthetic-runtime'}
            (base/'manifest.json').write_text(json.dumps(m))
            args=SimpleNamespace(out=str(base/'result'),manifest=str(base/'manifest.json'),seconds=4,
                cleanup_seconds=2,max_bytes=1024**2,max_rss_mb=512,command=[sys.executable,str(base/'producer.py'),
                str(base/'events.json'),'wait' if wait else 'end','true' if native_pass else 'false',str(base/'manifest.json'),str(cleanup_delay)])
            # A real isolated process replaces only the external reviewer. It
            # records whether ordinary cleanup has finished when review starts.
            def synthetic_review(root, report):
                (root/'review-verdict.json').write_text(json.dumps(report))
                before = (root/'test/cleanup-completed.json').exists()
                (root/'review-start-state.json').write_text(json.dumps({'cleanup_completed': before}))
                if tight_native_budget:
                    reviewer_names={'review-verdict.json','review-verdict.json.tmp','jev.json','jev.stderr'}
                    args.max_bytes=sum(p.stat().st_size for p in root.rglob('*')
                                       if p.is_file() and not (p.parent==root and p.name in reviewer_names)) + 1024
                with (root/'jev.json').open('wb') as output, (root/'jev.stderr').open('wb') as errors:
                    return subprocess.Popen([sys.executable, '-c',
                        'import json,sys,time; raw=json.loads(sys.argv[2]); payload={"review_status":"completed","padding":"x"*70000} if sys.argv[1]=="fast_oversized" else {"review_status":sys.argv[1]}; print(raw if raw is not None else "x"*70000 if sys.argv[1]=="oversized" else json.dumps(payload),flush=True); time.sleep(10 if sys.argv[1]=="oversized" else 0); sys.exit(3 if sys.argv[1]=="unavailable" else 0)',
                        review_status, json.dumps(review_output)], stdout=output, stderr=errors, start_new_session=True)
            with patch('execute.start_review',side_effect=synthetic_review):
                code=execute(args)
            receipt=json.loads((base/'result/execution.json').read_text())
            receipt['review_started_after_cleanup'] = json.loads(
                (base/'result/review-start-state.json').read_text())['cleanup_completed']
            cleanup=(base/'result/test/cleanup-completed.json').is_file()
            stopped=(base/'result/request-stop.json').is_file()
            return code,receipt,cleanup,stopped

    def test_success_automatically_runs_and_reaps(self):
        code,r,clean,_=self.run_case(fixtures.EvaluatorTests().success_events())
        self.assertEqual(code,0);self.assertTrue(clean);self.assertTrue(r['children_reaped'])
        self.assertEqual(r['observer_status'],'PASS');self.assertFalse(r['forced_stop'])

    def test_invalid_action_requests_cleanup_and_fails(self):
        rows=[event(1,0,'heartbeat',wake_sequence=1,active=True,scheduler_running=True)]
        rows += [terminal(i+2,i+1,str(i),'Loot',251,result='Rejected',reason='permission',failure_class='invalid') for i in range(3)]
        code,r,clean,stopped=self.run_case(rows,wait=True,native_pass=False)
        self.assertEqual(code,1);self.assertTrue(stopped);self.assertTrue(clean)
        self.assertEqual(r['observer_status'],'FAIL');self.assertFalse(r['forced_stop'])

    def test_observer_pass_cannot_override_native_failure(self):
        code,r,clean,_=self.run_case(fixtures.EvaluatorTests().success_events(),native_pass=False)
        self.assertEqual(r['observer_status'],'PASS');self.assertEqual(code,1);self.assertTrue(clean)

    def test_reviewer_unavailable_cannot_downgrade_native_success(self):
        code,r,clean,_=self.run_case(fixtures.EvaluatorTests().success_events(),review_status='unavailable')
        self.assertEqual(code,0);self.assertTrue(clean)
        self.assertEqual(r['status'],'PASS');self.assertEqual(r['review_status'],'unavailable')

    def test_non_object_reviewer_output_cannot_interrupt_native_success(self):
        code,r,clean,_=self.run_case(fixtures.EvaluatorTests().success_events(),review_output='[]')
        self.assertEqual(code,0);self.assertTrue(clean)
        self.assertEqual(r['status'],'PASS');self.assertEqual(r['review_status'],'invalid')
        self.assertFalse(r['forced_stop']);self.assertEqual(r['cleanup_state'],'concluded')
        self.assertTrue(r['children_reaped'])

    def test_reviewer_output_cap_does_not_interrupt_native_cleanup_or_success(self):
        code,r,clean,_=self.run_case(fixtures.EvaluatorTests().success_events(),review_status='oversized')
        self.assertEqual(code,0);self.assertTrue(clean)
        self.assertEqual(r['review_status'],'unavailable')
        self.assertEqual(r['reviewer_limit'],'output_cap')
        self.assertFalse(r['forced_stop']);self.assertEqual(r['cleanup_state'],'concluded')
        self.assertTrue(r['children_reaped'])

    def test_quickly_exiting_oversized_reviewer_cannot_bypass_output_limit(self):
        code,r,clean,_=self.run_case(fixtures.EvaluatorTests().success_events(),review_status='fast_oversized')
        self.assertEqual(code,0);self.assertTrue(clean)
        self.assertEqual(r['review_status'],'unavailable')
        self.assertEqual(r['reviewer_limit'],'output_cap')
        self.assertFalse(r['forced_stop']);self.assertTrue(r['children_reaped'])

    def test_provisional_review_input_does_not_consume_native_cleanup_budget(self):
        rows=[event(1,0,'heartbeat',wake_sequence=1,active=True,scheduler_running=True)]
        rows += [terminal(i+2,i+1,str(i),'Loot',251,result='Rejected',reason='permission',failure_class='invalid') for i in range(3)]
        code,r,clean,_=self.run_case(rows,wait=True,native_pass=False,cleanup_delay=.7,tight_native_budget=True)
        self.assertEqual(code,1);self.assertTrue(clean)
        self.assertEqual(r['review_phase'],'provisional')
        self.assertFalse(r['forced_stop']);self.assertNotIn('resource_fault',r)
        self.assertEqual(r['cleanup_state'],'concluded')

    def test_failure_triage_runs_while_ordinary_cleanup_is_pending(self):
        rows=[event(1,0,'heartbeat',wake_sequence=1,active=True,scheduler_running=True)]
        rows += [terminal(i+2,i+1,str(i),'Loot',251,result='Rejected',reason='permission',failure_class='invalid') for i in range(3)]
        code,r,clean,_=self.run_case(rows,wait=True,native_pass=False,cleanup_delay=.7)
        self.assertEqual(code,1);self.assertTrue(clean)
        self.assertEqual(r['review_phase'],'provisional')
        self.assertFalse(r['review_started_after_cleanup'])
        self.assertEqual(r['cleanup_state'],'concluded')
        self.assertTrue(r['children_reaped'])

    def test_missing_telemetry_is_never_success(self):
        code,r,_,_=self.run_case([event(1,0,'telemetry_gap',reason='missing native state')])
        self.assertEqual(code,3);self.assertEqual(r['status'],'INCONCLUSIVE')

    def test_report_from_another_bot_or_build_cannot_join(self):
        m=manifest();m['native_contract']={'scenario':'synthetic-test','canonicalMd5':'db','runtimeMd5':'db',
            'PublishSourceIdentity':'publish','testAssemblySha256':'test','runtimeGameSha256':'game'}
        n={'runId':'owned','botName':m['bot_name'],'characterId':m['bot_id'],'scenario':'synthetic-test',
           'observerDatabaseId':m['database_id'],'canonicalMd5':'db','runtimeMd5':'db',
           'provenance':{'source':{'Head':m['source_sha'],'Unavailable':False},
                         'PublishSourceIdentity':'publish','testAssemblySha256':'test','runtimeGameSha256':'game'}}
        self.assertTrue(native_identity_matches(n,m))
        for key in ('characterId','botName','scenario','observerDatabaseId','runId','canonicalMd5'):
            wrong=json.loads(json.dumps(n));wrong[key]=None
            self.assertFalse(native_identity_matches(wrong,m),key)
        wrong=json.loads(json.dumps(n));wrong['provenance']['runtimeGameSha256']='other'
        self.assertFalse(native_identity_matches(wrong,m))

    def test_unresponsive_test_is_reaped_without_claiming_cleanup(self):
        with tempfile.TemporaryDirectory() as temp:
            base=Path(temp);m=manifest();m.update(started_utc=stamp(0),collection_mode='historical-replay')
            (base/'manifest.json').write_text(json.dumps(m))
            args=SimpleNamespace(out=str(base/'result'),manifest=str(base/'manifest.json'),seconds=.1,
                cleanup_seconds=.1,max_bytes=1024**2,max_rss_mb=512,
                command=[sys.executable,'-c','import time;time.sleep(10)'])
            self.assertEqual(execute(args),3)
            receipt=json.loads((base/'result/execution.json').read_text())
            self.assertTrue(receipt['forced_stop']);self.assertTrue(receipt['children_reaped'])
            self.assertEqual(receipt['cleanup_state'],'unproved')

    def test_emergency_output_cap_never_claims_success(self):
        with tempfile.TemporaryDirectory() as temp:
            base=Path(temp);m=manifest();m.update(started_utc=stamp(0),collection_mode='historical-replay')
            (base/'manifest.json').write_text(json.dumps(m))
            args=SimpleNamespace(out=str(base/'result'),manifest=str(base/'manifest.json'),seconds=2,
                cleanup_seconds=.1,max_bytes=20000,max_rss_mb=512,
                command=[sys.executable,'-c','import time;print("x"*50000,flush=True);time.sleep(10)'])
            self.assertEqual(execute(args),3)
            receipt=json.loads((base/'result/execution.json').read_text())
            self.assertEqual(receipt['resource_fault'],'output_cap');self.assertTrue(receipt['children_reaped'])
            self.assertEqual(receipt['status'],'INCONCLUSIVE')


if __name__=='__main__':unittest.main()
