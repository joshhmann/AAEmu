from copy import deepcopy
import builtins
import hashlib
import json
import multiprocessing
import os
import stat
from pathlib import Path
import runpy
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch

from work_dashboard import MAX_HISTORY, MAX_PREVIEW_BYTES, MAX_SOURCE_BYTES, WorkDashboard, WorkError


def race_save(root, data_file, barrier, results, suffix):
    service = WorkDashboard(root, data_file=data_file)
    board = service.snapshot()
    feature = deepcopy(board['features'][0])
    feature['title'] = 'Concurrent title ' + suffix
    barrier.wait(timeout=10)
    try:
        results.put(('saved', service.mutate({'revision': board['revision'], 'action': 'feature_save', 'feature': feature})['revision']))
    except WorkError as error:
        results.put(('error', error.status))


class WorkDashboardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'repo'
        self.root.mkdir()
        self.research = Path(self.temp.name) / 'research'
        self.research.mkdir()
        self.data_file = self.root / 'playertrace-coverage/work_board.json'
        self.catalog_file = self.root / 'Scripts/playertrace-coverage/work_catalog.json'
        self.catalog_file.parent.mkdir(parents=True)
        self.write('PROJECT-CONTROL.md', '# Scope\nBounded engineering only.\n')
        self.write('ROADMAP.md', '# Roadmap\nNext milestone requires evidence.\n')
        self.write('.omp/state/CURRENT.md', '# CURRENT\n## Objective\nImplement the bounded fixture outcome.\n')
        self.write('.omp/state/DECISIONS.md', '# Decisions\n## Decision\nNo gameplay authorization.\n')
        self.write('.omp/state/EVIDENCE.md', '# Evidence\n## Historical proof\nRecorded scenario, not new verification.\n### Limits\nControl layer only.\n## Other proof\nUnrelated result.\n')
        self.write('AAEmu.Game/Fixture/A/source.py', 'answer = 42\n')
        self.git('init', '-b', 'main')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.git('config', 'user.name', 'Fixture')
        self.git('add', '.')
        self.git('commit', '-m', 'Fixture baseline')
        self.sha = self.git('rev-parse', 'HEAD').strip()
        self.feature = {
            'id': 'bounded-feature', 'title': 'Bounded outcome', 'track': 'Delivery',
            'summary': 'Existing source has a bounded implementation.', 'gaps': ['Independent scenario remains unproved'],
            'references': [self.ref('implementation', 'AAEmu.Game/Fixture/A/source.py'),
                           self.ref('proof', '.omp/state/EVIDENCE.md', kind='evidence', anchor='historical-proof')],
        }
        self.card = self.make_card('first')
        self.catalog_file.write_text(json.dumps({'features': [self.feature], 'cards': [self.card]}), encoding='utf-8')
        self.service = WorkDashboard(self.root, data_file=self.data_file, research_root=self.research)

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], stderr=subprocess.STDOUT, text=True)

    def write(self, path, content):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding='utf-8')
        return target

    def ref(self, rid, path='', source='repo', kind='implementation', **extra):
        value = {'id': rid, 'label': rid.title(), 'kind': kind, 'source': source,
                 'path': path, 'url': '', 'revision': 'historic-fixture', 'layer': 'control',
                 'confidence': 'source-inspected', 'limits': 'Not gameplay or live acceptance'}
        value.update(extra)
        return value

    def make_card(self, cid, **extra):
        value = {
            'id': cid, 'feature_id': 'bounded-feature', 'title': 'Observe bounded result ' + cid,
            'parent': 'Fixture milestone', 'outcome': 'Saved result survives a new service instance',
            'mode': 'Implement', 'slot': 'primary', 'status': 'Ready', 'priority': 'Normal',
            'owner': 'Owner ' + cid, 'verifier': 'Independent reviewer', 'scope': ['Owned/A'],
            'resources': ['fixture-runner'], 'non_goals': ['No gameplay launch'],
            'acceptance': ['A fresh reader obtains the committed result'], 'evidence_layer': 'control',
            'reference_ids': ['implementation', 'proof'], 'dependencies': [], 'baseline_sha': self.sha,
            'next_action': 'Record the first bounded observation', 'unknowns': ['Acceptance is not yet exercised'],
            'evidence_refs': [], 'blocker': '', 'acceptance_note': '',
        }
        value.update(extra)
        return value

    def mutate(self, action, **fields):
        return self.service.mutate(dict(revision=self.service.snapshot()['revision'], action=action, **fields))

    def get_card(self, cid='first'):
        return next(card for card in self.service.snapshot()['cards'] if card['id'] == cid)

    def get_session(self, sid):
        return next(session for session in self.service.snapshot()['sessions'] if session['id'] == sid)

    def start(self, cid='first', strategy='continue', **extra):
        board = self.mutate('session_start', card_id=cid, name='Operator record ' + cid, strategy=strategy, **extra)
        return board['sessions'][-1]['id']

    def checkpoint(self, sid, status='paused', **extra):
        checkpoint = {'confirmed': ['A consumer-visible checkpoint was recorded'], 'unknowns': ['Live behavior remains unverified'],
                      'next_action': 'Review the saved bounded observation', 'evidence_refs': ['fixture:receipt'], 'blocker': ''}
        return self.mutate('session_checkpoint', session_id=sid, checkpoint=checkpoint, status=status, **extra)

    def assert_error(self, expected_status, action, **fields):
        before = self.data_file.read_bytes() if self.data_file.exists() else None
        with self.assertRaises(WorkError) as caught:
            self.mutate(action, **fields)
        self.assertEqual(expected_status, caught.exception.status, caught.exception.message)
        self.assertEqual(before, self.data_file.read_bytes() if self.data_file.exists() else None)

    def test_seed_get_and_reference_are_read_only_and_return_detached_values(self):
        first = self.service.snapshot()
        first['cards'][0]['scope'].append('Unowned')
        self.assertEqual(['Owned/A'], self.get_card()['scope'])
        self.service.reference('bounded-feature', 'proof')
        self.assertFalse(self.data_file.exists())
        self.assertFalse(self.data_file.parent.exists())
        self.assertEqual([], self.service.snapshot()['sessions'])

    def test_persistence_and_stale_save_do_not_overwrite_a_newer_record(self):
        initial = self.service.snapshot()
        card = deepcopy(initial['cards'][0])
        card['next_action'] = 'Read persisted checkpoint from a fresh reader'
        saved = self.service.mutate({'revision': initial['revision'], 'action': 'card_save', 'card': card})
        fresh = WorkDashboard(self.root, self.data_file, self.research)
        self.assertEqual(saved['cards'], fresh.snapshot()['cards'])
        before = self.data_file.read_bytes()
        with self.assertRaises(WorkError) as caught:
            self.service.mutate({'revision': initial['revision'], 'action': 'card_save', 'card': initial['cards'][0]})
        self.assertEqual(409, caught.exception.status)
        self.assertEqual(before, self.data_file.read_bytes())

    def test_two_service_instances_serialize_the_same_revision_across_threads(self):
        barrier = threading.Barrier(2)
        results = []
        def save(suffix):
            service = WorkDashboard(self.root, self.data_file, self.research)
            feature = deepcopy(self.feature)
            feature['title'] += suffix
            barrier.wait(timeout=10)
            try:
                results.append(service.mutate({'revision': 0, 'action': 'feature_save', 'feature': feature})['revision'])
            except WorkError as error:
                results.append(error.status)
        threads = [threading.Thread(target=save, args=(str(i),)) for i in range(2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=15)
            self.assertFalse(thread.is_alive())
        self.assertCountEqual([1, 409], results)
        self.assertEqual(1, self.service.snapshot()['revision'])

    def test_cross_process_revision_race_has_one_winner(self):
        context = multiprocessing.get_context('fork')
        barrier, results = context.Barrier(2), context.Queue()
        processes = [context.Process(target=race_save, args=(str(self.root), str(self.data_file), barrier, results, str(i))) for i in range(2)]
        for process in processes:
            process.start()
            self.addCleanup(lambda p=process: p.kill() if p.is_alive() else None)
        observed = [results.get(timeout=15) for _ in processes]
        for process in processes:
            process.join(timeout=15)
            self.assertEqual(0, process.exitcode)
        self.assertCountEqual([('saved', 1), ('error', 409)], observed)
        self.assertEqual(1, self.service.snapshot()['revision'])

    def test_ready_requires_complete_contract_and_registered_references(self):
        for field in ('owner', 'verifier', 'scope', 'outcome', 'parent', 'non_goals', 'acceptance', 'baseline_sha', 'next_action', 'reference_ids'):
            with self.subTest(field=field):
                card = deepcopy(self.card)
                card[field] = [] if isinstance(card[field], list) else ''
                self.assert_error(400, 'card_save', card=card)
        card = deepcopy(self.card)
        card['reference_ids'] = ['invented-proof']
        self.assert_error(400, 'card_save', card=card)

    def test_backlog_can_be_incomplete_but_cannot_start(self):
        card = self.make_card('incomplete', status='Backlog', outcome='', owner='', scope=[], acceptance=[], non_goals=[], reference_ids=[], baseline_sha='', next_action='')
        saved = self.mutate('card_save', card=card)
        row = next(c for c in saved['cards'] if c['id'] == 'incomplete')
        self.assertFalse(row['readiness']['ready'])
        self.assert_error(409, 'session_start', card_id='incomplete', strategy='continue', name='Not runnable')

    def test_dependencies_need_real_accepted_record_and_cycle_is_rejected(self):
        child = self.make_card('child', status='Backlog', dependencies=['first'], scope=['Owned/B'])
        self.mutate('card_save', card=child)
        child['status'] = 'Ready'
        self.assert_error(400, 'card_save', card=child)
        parent = self.get_card()
        parent['status'], parent['dependencies'] = 'Backlog', ['child']
        self.assert_error(400, 'card_save', card=parent)
        parent = self.get_card()
        parent.update(status='Accepted', acceptance_note='Fresh-instance fixture read produced expected saved result; control only', evidence_refs=['fixture:result.json'])
        self.mutate('card_save', card=parent)
        self.mutate('card_save', card=child)
        self.assertTrue(self.get_card('child')['readiness']['ready'])
        parent['status'] = 'Backlog'
        self.assert_error(400, 'card_save', card=parent)

    def test_unknown_self_dependencies_and_missing_blocker_fail_closed(self):
        for dependencies in (['missing'], ['first']):
            card = deepcopy(self.card)
            card['dependencies'] = dependencies
            self.assert_error(400, 'card_save', card=card)
        card = deepcopy(self.card)
        card['status'] = 'Blocked'
        self.assert_error(400, 'card_save', card=card)

    def test_status_alone_cannot_claim_acceptance_and_canonical_bytes_do_not_change(self):
        canonical = {path: (self.root / path).read_bytes() for path in ('PROJECT-CONTROL.md', 'ROADMAP.md', '.omp/state/CURRENT.md', '.omp/state/EVIDENCE.md')}
        card = deepcopy(self.card)
        card['status'] = 'Accepted'
        self.assert_error(400, 'card_save', card=card)
        card['acceptance_note'] = 'Operator reports the named control scenario; live outcome still unproved'
        self.assert_error(400, 'card_save', card=card)
        card['evidence_refs'] = ['fixture:reported-result']
        board = self.mutate('card_save', card=card)
        self.assertEqual('Accepted', board['cards'][0]['status'])
        for path, content in canonical.items():
            self.assertEqual(content, (self.root / path).read_bytes())

    def test_active_card_contract_and_acceptance_are_locked(self):
        sid = self.start()
        card = self.get_card()
        card['scope'] = ['Owned/B']
        self.assert_error(409, 'card_save', card=card)
        card = self.get_card()
        card.update(status='Accepted', acceptance_note='Reported fixture', evidence_refs=['fixture:reported'])
        self.assert_error(409, 'card_save', card=card)
        card = self.get_card()
        card['status'] = 'Verifying'
        self.mutate('card_save', card=card)
        self.assertEqual('active', self.get_session(sid)['status'])

    def test_wip_owner_scope_and_resource_conflicts_are_exclusive(self):
        self.start()
        variants = (
            {'slot': 'primary', 'scope': ['Owned/B'], 'resources': []},
            {'slot': 'secondary', 'owner': 'owner FIRST', 'scope': ['Owned/B'], 'resources': []},
            {'slot': 'secondary', 'scope': ['Owned/A/child.py'], 'resources': []},
            {'slot': 'secondary', 'scope': ['Owned/B'], 'resources': ['FIXTURE-RUNNER']},
        )
        for index, variant in enumerate(variants):
            with self.subTest(variant=variant):
                cid = 'conflict-' + str(index)
                self.mutate('card_save', card=self.make_card(cid, **variant))
                self.assert_error(409, 'session_start', card_id=cid, name='Conflicting record', strategy='continue')
        self.assert_error(409, 'session_start', card_id='first', name='Duplicate active', strategy='fresh', reason='Cannot override owner')

    def test_component_prefix_not_lexical_prefix_and_both_slots_can_work(self):
        self.start()
        self.mutate('card_save', card=self.make_card('second', slot='secondary', scope=['Owned/AB'], resources=['another-runner']))
        sid = self.start('second')
        self.assertEqual('active', self.get_session(sid)['status'])

    def test_pause_releases_ownership_but_resume_rechecks_it(self):
        first = self.start()
        self.checkpoint(first)
        second_card = self.make_card('second')
        self.mutate('card_save', card=second_card)
        second = self.start('second')
        self.assert_error(409, 'session_resume', session_id=first)
        self.checkpoint(second)
        self.mutate('session_resume', session_id=first)
        self.assertEqual('active', self.get_session(first)['status'])
        self.assertEqual(3, len(self.get_session(first)['history']))

    def test_resume_rechecks_dependency_status(self):
        accepted = self.make_card('dependency', status='Accepted', scope=['Owned/B'], acceptance_note='Reported fixture only', evidence_refs=['fixture:result'])
        self.mutate('card_save', card=accepted)
        card = self.get_card()
        card['dependencies'] = ['dependency']
        self.mutate('card_save', card=card)
        sid = self.start()
        self.checkpoint(sid)
        # Removing acceptance from a dependency of a runnable card is rejected
        # at save time, rather than producing a misleading resume success.
        accepted['status'] = 'Backlog'
        self.assert_error(400, 'card_save', card=accepted)
        self.assertEqual('paused', self.get_session(sid)['status'])

    def test_fresh_requires_reason_and_preserves_predecessor_checkpoint_history(self):
        old = self.start()
        self.checkpoint(old)
        previous = deepcopy(self.get_session(old))
        self.assert_error(400, 'session_start', card_id='first', name='Restart', strategy='fresh', reason='')
        self.assert_error(409, 'session_start', card_id='first', name='Wrong continuation', strategy='continue')
        new = self.start(strategy='fresh', reason='Different bounded approach after reviewing the checkpoint')
        predecessor, successor = self.get_session(old), self.get_session(new)
        self.assertNotEqual(old, new)
        self.assertEqual('closed', predecessor['status'])
        self.assertEqual(previous['history'], predecessor['history'][:len(previous['history'])])
        self.assertEqual(previous['checkpoint'], predecessor['checkpoint'])
        self.assertEqual(old, successor['predecessor_id'])
        self.assertEqual(new, predecessor['history'][-1]['successor_id'])
        self.assertEqual(successor, WorkDashboard(self.root, self.data_file, self.research).snapshot()['sessions'][-1])

    def test_checkpoint_cannot_bypass_resume_and_closed_history_is_immutable(self):
        sid = self.start()
        self.assert_error(400, 'session_checkpoint', session_id=sid, status='paused', checkpoint={'next_action': ''})
        self.checkpoint(sid)
        self.assert_error(409, 'session_checkpoint', session_id=sid, status='active', checkpoint={'next_action': 'Bypass WIP checks'})
        self.checkpoint(sid, status='closed')
        self.assert_error(409, 'session_resume', session_id=sid)
        self.assert_error(409, 'session_checkpoint', session_id=sid, status='closed', checkpoint={'next_action': 'Rewrite history'})

    def test_changed_current_requires_explicit_nonstale_context_on_resume(self):
        sid = self.start()
        self.checkpoint(sid)
        old_context = self.service.snapshot()['context']
        self.write('.omp/state/CURRENT.md', '# CURRENT\n## Objective\nNew bounded canonical objective.\n')
        self.assertTrue(self.get_session(sid)['drift'])
        self.assert_error(409, 'session_resume', session_id=sid)
        self.assert_error(409, 'session_resume', session_id=sid, reviewed_context=old_context)
        self.assert_error(400, 'session_resume', session_id=sid, reviewed_context=True)
        self.assert_error(400, 'session_resume', session_id=sid, reviewed_context={})
        self.assert_error(400, 'session_resume', session_id=sid, reviewed_context=False)
        new_context = self.service.snapshot()['context']
        board = self.mutate('session_resume', session_id=sid, reviewed_context=new_context)
        self.assertEqual([], board['sessions'][0]['drift'])
        self.assertEqual(old_context, board['sessions'][0]['history'][0]['context'])
        self.assertTrue(board['sessions'][0]['history'][-1]['reviewed_context'])

    def test_source_change_blocks_new_start_and_needs_ack_for_continuation(self):
        sid = self.start()
        self.write('AAEmu.Game/Fixture/A/source.py', 'answer = 43\n')
        self.git('add', 'AAEmu.Game/Fixture/A/source.py')
        self.git('commit', '-m', 'Changed actual source')
        self.assert_error(409, 'session_checkpoint', session_id=sid, status='active', checkpoint={'next_action': 'Continue without review'})
        self.checkpoint(sid)
        self.mutate('session_resume', session_id=sid, reviewed_context=self.service.snapshot()['context'])
        self.checkpoint(sid, status='closed')
        self.assert_error(409, 'session_start', card_id='first', name='Wrong baseline', strategy='fresh', reason='Reviewed restart')
        card = self.get_card()
        card['baseline_sha'] = self.git('rev-parse', 'HEAD').strip()
        self.mutate('card_save', card=card)
        fresh = self.start(strategy='fresh', reason='Explicitly reviewed new actual source baseline')
        self.assertEqual(card['baseline_sha'], self.get_session(fresh)['context']['source_sha'])

    def test_feature_reference_drift_requires_fresh_and_never_rebinds_saved_contract(self):
        sid = self.start()
        saved_contract = deepcopy(self.get_session(sid)['contract'])
        feature = deepcopy(self.feature)
        feature['references'][1]['revision'] = 'new-reported-historical-revision'
        self.mutate('feature_save', feature=feature)
        self.assertEqual(saved_contract, self.get_session(sid)['contract'])
        self.assertTrue(self.get_session(sid)['drift'])
        self.checkpoint(sid)
        self.assert_error(409, 'session_resume', session_id=sid, reviewed_context=self.service.snapshot()['context'])
        fresh = self.start(strategy='fresh', reason='Selected bibliography contract changed')
        self.assertNotEqual(saved_contract, self.get_session(fresh)['contract'])
        self.assertEqual(saved_contract, self.get_session(sid)['contract'])

    def test_unselected_reference_edit_does_not_rebind_or_invent_drift(self):
        sid = self.start()
        feature = deepcopy(self.feature)
        feature['references'].append(self.ref('additional', 'ROADMAP.md', kind='policy'))
        self.mutate('feature_save', feature=feature)
        self.assertEqual([], self.get_session(sid)['drift'])
        self.assertEqual(2, len(self.get_session(sid)['contract']['references']))

    def test_paused_card_contract_change_requires_fresh_even_with_ack(self):
        sid = self.start()
        self.checkpoint(sid)
        card = self.get_card()
        card['acceptance'] = ['A different explicitly bounded criterion']
        self.mutate('card_save', card=card)
        self.assert_error(409, 'session_resume', session_id=sid, reviewed_context=self.service.snapshot()['context'])
        fresh = self.start(strategy='fresh', reason='Acceptance contract changed after review')
        self.assertEqual(card['acceptance'], self.get_session(fresh)['contract']['acceptance'])

    def test_brief_is_reproducible_and_uses_saved_contract_and_current_pointers(self):
        sid = self.start()
        self.checkpoint(sid)
        first = self.service.brief(sid)
        self.assertEqual(first, self.service.brief(sid))
        self.assertIn('fixture:receipt', first['text'])
        self.assertIn('No gameplay launch', first['text'])
        self.assertIn('.omp/state/EVIDENCE.md#historical-proof', first['text'])
        saved = self.get_session(sid)['contract']['outcome']
        card = self.get_card()
        card['outcome'] = 'A different current outcome'
        self.mutate('card_save', card=card)
        changed = self.service.brief(sid)
        self.assertIn(saved, changed['text'])
        self.assertNotIn('Outcome: A different current outcome', changed['text'])
        self.assertTrue(changed['drift'])

    def test_registered_anchor_and_line_selection_have_current_digest_and_real_bounds(self):
        result = self.service.reference('bounded-feature', 'proof')
        self.assertIn('Recorded scenario', result['text'])
        self.assertIn('Control layer only', result['text'])
        self.assertNotIn('Unrelated result', result['text'])
        self.assertEqual((2, 5), (result['line_start'], result['line_end']))
        self.assertEqual(hashlib.sha256((self.root / '.omp/state/EVIDENCE.md').read_bytes()).hexdigest(), result['sha256'])
        feature = deepcopy(self.feature)
        feature['references'].append(self.ref('lines', '.omp/state/EVIDENCE.md', kind='evidence', line_start=3, line_end=4))
        self.mutate('feature_save', feature=feature)
        result = self.service.reference('bounded-feature', 'lines')
        self.assertEqual((3, 4), (result['line_start'], result['line_end']))
        self.assertNotIn('Historical proof', result['text'])

    def test_missing_anchor_and_source_are_explicitly_unavailable_not_substituted(self):
        feature = deepcopy(self.feature)
        feature['references'].append(self.ref('missing', 'docs/absent.md'))
        feature['references'].append(self.ref('anchor', '.omp/state/EVIDENCE.md', anchor='missing-heading'))
        self.mutate('feature_save', feature=feature)
        for rid in ('missing', 'anchor'):
            result = self.service.reference('bounded-feature', rid)
            self.assertTrue(result['unavailable'])
            self.assertEqual('', result['text'])
        with self.assertRaises(WorkError) as caught:
            self.service.reference('bounded-feature', '../CURRENT.md')
        self.assertEqual(404, caught.exception.status)

    def test_external_reference_is_never_fetched_and_research_root_is_explicit(self):
        feature = deepcopy(self.feature)
        feature['references'].append(self.ref('external', source='external', kind='research', url='https://example.invalid/study'))
        feature['references'].append(self.ref('research', 'study.md', source='research', kind='research'))
        (self.research / 'study.md').write_text('Independent comparison, not ArcheAge authority.\n', encoding='utf-8')
        self.mutate('feature_save', feature=feature)
        external = self.service.reference('bounded-feature', 'external')
        self.assertEqual('', external['text'])
        self.assertIsNone(external['sha256'])
        self.assertFalse(external['unavailable'])
        self.assertIn('Independent comparison', self.service.reference('bounded-feature', 'research')['text'])
        with patch.dict(os.environ, {}, clear=True):
            unavailable = WorkDashboard(self.root, self.data_file).reference('bounded-feature', 'research')
        self.assertTrue(unavailable['unavailable'])

    def test_unsafe_reference_paths_urls_and_selectors_cannot_be_registered(self):
        invalid = (
            {'path': '../outside.md'}, {'path': '/etc/passwd'}, {'path': 'docs/../CURRENT.md'},
            {'path': 'docs\\secret.md'}, {'path': '.git/config'}, {'path': 'Config.Local.json'},
            {'path': 'Data/compact.sqlite3'}, {'path': 'Data/live.db'}, {'path': '.env.local'},
            {'path': 'private/receipt.md'}, {'path': 'docs/api-token.txt'}, {'path': 'captures/auth.jsonl'},
            {'path': 'docs/observation.jsonl'}, {'path': 'docs/runtime.log'},
            {'path': '.omp/agents/operator/notes.md'}, {'path': '.omp/state/sessions/notes.md'},
            {'path': '.client_files/survey/innocent.json'}, {'path': '.server_files/innocent.md'},
            {'path': 'traces/innocent.json'}, {'path': 'AAEmu.Game/Data/innocent.json'},
            {'path': 'AAEmu.Game/cache/innocent.md'}, {'path': 'Scripts/sessions/innocent.md'},
            {'path': 'Docs/.cache/innocent.md'}, {'path': 'Scripts/agent-records/innocent.md'},
            {'path': 'Docs/trace-records/innocent.md'}, {'path': 'Docs/.notes.md'},
            {'path': '.omp/state/artifacts/unreviewed-runtime/innocent.json'},
            {'source': 'external', 'path': '', 'url': 'http://example.invalid/study'},
            {'source': 'external', 'path': '', 'url': 'https://user:password@example.invalid/study'},
            {'path': 'ROADMAP.md', 'line_start': 0, 'line_end': 5},
            {'path': 'ROADMAP.md', 'line_start': 1, 'line_end': 2001},
            {'path': 'ROADMAP.md', 'anchor': 'roadmap', 'line_start': 1, 'line_end': 2},
        )
        for changes in invalid:
            with self.subTest(changes=changes):
                feature = deepcopy(self.feature)
                feature['references'].append(self.ref('unsafe', **changes))
                self.assert_error(400, 'feature_save', feature=feature)

    def test_symlink_and_hardlink_reference_escape_and_owned_scope_fail_closed(self):
        outside = Path(self.temp.name) / 'outside.md'
        outside.write_text('Must never be exposed', encoding='utf-8')
        (self.root / 'Linked').symlink_to(outside.parent, target_is_directory=True)
        (self.root / 'Docs').mkdir()
        (self.root / 'Docs/Linked').symlink_to(outside.parent, target_is_directory=True)
        (self.root / 'AAEmu.Game/Fixture/A/linked.md').symlink_to(outside)
        os.link(outside, self.root / 'AAEmu.Game/Fixture/A/hardlinked.md')
        feature = deepcopy(self.feature)
        feature['references'].extend([self.ref('parent-link', 'Docs/Linked/outside.md'),
                                      self.ref('final-link', 'AAEmu.Game/Fixture/A/linked.md'),
                                      self.ref('hard-link', 'AAEmu.Game/Fixture/A/hardlinked.md')])
        self.mutate('feature_save', feature=feature)
        for rid in ('parent-link', 'final-link', 'hard-link'):
            with self.assertRaises(WorkError) as caught:
                self.service.reference('bounded-feature', rid)
            self.assertEqual(400, caught.exception.status)
        card = self.make_card('escape', scope=['Linked/outside.md'])
        self.assert_error(400, 'card_save', card=card)
        card['scope'] = ['../outside.md']
        self.assert_error(400, 'card_save', card=card)

    def test_preview_and_source_byte_limits_and_nonregular_binary_boundaries(self):
        self.write('docs/large.md', 'bounded line\n' * (MAX_PREVIEW_BYTES // 5))
        (self.root / 'docs/oversized.md').write_bytes(b'x' * (MAX_SOURCE_BYTES + 1))
        (self.root / 'docs/binary.md').write_bytes(b'visible\x00secret')
        os.mkfifo(self.root / 'docs/fifo.md')
        feature = deepcopy(self.feature)
        feature['references'].extend(self.ref(name, 'docs/' + name + '.md') for name in ('large', 'oversized', 'binary', 'fifo'))
        self.mutate('feature_save', feature=feature)
        preview = self.service.reference('bounded-feature', 'large')
        self.assertTrue(preview['truncated'])
        self.assertLessEqual(len(preview['text'].encode('utf-8')), MAX_PREVIEW_BYTES)
        for rid in ('oversized', 'binary'):
            result = self.service.reference('bounded-feature', rid)
            self.assertTrue(result['unavailable'])
            self.assertEqual('', result['text'])
            self.assertIsNone(result['sha256'])
        with self.assertRaises(WorkError) as caught:
            self.service.reference('bounded-feature', 'fifo')
        self.assertEqual(400, caught.exception.status)

    def test_history_bound_preserves_events_and_reserves_close(self):
        sid = self.start()
        # Shrink the runtime bound for a deterministic short behavioral test.
        with patch('work_dashboard.MAX_HISTORY', 4):
            self.checkpoint(sid, status='active')
            self.checkpoint(sid, status='active')
            self.assert_error(409, 'session_checkpoint', session_id=sid, status='active', checkpoint={'next_action': 'Would discard history'})
            self.checkpoint(sid, status='closed')
            self.assertEqual(4, len(self.get_session(sid)['history']))
        self.assertLessEqual(len(self.get_session(sid)['history']), MAX_HISTORY)

    def test_atomic_replace_failure_preserves_previous_committed_bytes(self):
        self.mutate('card_save', card=self.card)
        before = self.data_file.read_bytes()
        feature = deepcopy(self.feature)
        feature['title'] = 'Failed write candidate'
        with patch('work_dashboard.os.replace', side_effect=OSError('Injected filesystem refusal')):
            self.assert_error(500, 'feature_save', feature=feature)
        self.assertEqual(before, self.data_file.read_bytes())
        self.assertEqual([], list(self.data_file.parent.glob('*.tmp')))

    def test_corrupt_existing_stores_are_never_reseeded_or_replaced(self):
        self.data_file.parent.mkdir(parents=True)
        invalid_stores = (b'{broken', b'{"revision":0,"revision":1}',
                          json.dumps({'schema_version': 1, 'revision': 0, 'features': [], 'cards': [self.card], 'sessions': []}).encode())
        for raw in invalid_stores:
            with self.subTest(raw=raw):
                self.data_file.write_bytes(raw)
                with self.assertRaises(WorkError) as caught:
                    WorkDashboard(self.root, self.data_file, self.research).snapshot()
                self.assertEqual(500, caught.exception.status)
                self.assertEqual(raw, self.data_file.read_bytes())
                with self.assertRaises(WorkError):
                    self.service.mutate({'revision': 0, 'action': 'card_save', 'card': self.card})
                self.assertEqual(raw, self.data_file.read_bytes())

    def test_inconsistent_history_and_conflicting_persisted_owners_are_corruption(self):
        sid = self.start()
        good = json.loads(self.data_file.read_text(encoding='utf-8'))
        corrupt = deepcopy(good)
        corrupt['sessions'][0]['checkpoint']['next_action'] = 'Edited outside append-only history'
        self.data_file.write_text(json.dumps(corrupt), encoding='utf-8')
        with self.assertRaises(WorkError) as caught:
            self.service.snapshot()
        self.assertEqual(500, caught.exception.status)
        corrupt = deepcopy(good)
        other = deepcopy(corrupt['sessions'][0])
        other['id'] = 'work-conflicting-record'
        corrupt['sessions'].append(other)
        self.data_file.write_text(json.dumps(corrupt), encoding='utf-8')
        with self.assertRaises(WorkError) as caught:
            self.service.snapshot()
        self.assertEqual(500, caught.exception.status)
        self.assertEqual(sid, good['sessions'][0]['id'])

    def test_forged_history_context_refresh_and_predecessor_links_are_corruption(self):
        sid = self.start()
        self.checkpoint(sid)
        good = json.loads(self.data_file.read_text(encoding='utf-8'))
        changed = deepcopy(good)
        changed['sessions'][0]['context']['branch'] = 'Unreviewed branch'
        changed['sessions'][0]['history'][-1]['context']['branch'] = 'Unreviewed branch'
        bad_predecessor = deepcopy(good)
        bad_predecessor['sessions'][0]['predecessor_id'] = 'work-missing-record'
        bad_start = deepcopy(good)
        bad_start['sessions'][0]['history'][0]['action'] = 'checkpoint'
        for corrupt in (changed, bad_predecessor, bad_start):
            with self.subTest(corrupt=corrupt):
                raw = json.dumps(corrupt).encode('utf-8')
                self.data_file.write_bytes(raw)
                with self.assertRaises(WorkError) as caught:
                    self.service.snapshot()
                self.assertEqual(500, caught.exception.status)
                self.assertEqual(raw, self.data_file.read_bytes())
        self.assertEqual(sid, good['sessions'][0]['id'])

    def test_malformed_mutation_fields_are_useful_errors_without_writes(self):
        for payload in ({'revision': True, 'action': 'feature_save', 'feature': self.feature},
                        {'revision': 0, 'action': []},
                        {'revision': 0, 'action': 'feature_save', 'feature': dict(self.feature, title='\ud800')},
                        {'revision': 0, 'action': 'feature_save', 7: 'non-string field name'},
                        {'revision': 0, 'action': 'session_start', 'card_id': 'first', 'name': 'Malformed', 'strategy': []},
                        {'revision': 0, 'action': 'card_save', 'card': self.card, 'data_file': '/tmp/unowned.json'}):
            with self.subTest(payload=payload):
                with self.assertRaises(WorkError) as caught:
                    self.service.mutate(payload)
                self.assertEqual(400, caught.exception.status)
                self.assertFalse(self.data_file.exists())

    def test_missing_posix_locking_keeps_import_constructor_safe_but_routes_fail_closed(self):
        original_import = builtins.__import__
        def without_fcntl(name, *args, **kwargs):
            if name == 'fcntl':
                raise ImportError('Fixture unsupported locking platform')
            return original_import(name, *args, **kwargs)
        with patch('builtins.__import__', side_effect=without_fcntl):
            module = runpy.run_path(str(Path(__file__).with_name('work_dashboard.py')))
        service = module['WorkDashboard'](self.root, self.data_file, self.research)
        calls = (lambda: service.snapshot(),
                 lambda: service.mutate({'revision': 0, 'action': 'card_save', 'card': self.card}),
                 lambda: service.reference('bounded-feature', 'implementation'),
                 lambda: service.brief('work-record'))
        for call in calls:
            with self.assertRaises(module['WorkError']) as caught:
                call()
            self.assertEqual(503, caught.exception.status)
        self.assertFalse(self.data_file.exists())
        self.assertFalse(self.data_file.parent.exists())

    def test_missing_catalog_is_an_error_not_an_empty_fabricated_board(self):
        self.catalog_file.unlink()
        with self.assertRaises(WorkError) as caught:
            self.service.snapshot()
        self.assertEqual(500, caught.exception.status)
        self.assertFalse(self.data_file.exists())

    def test_symlink_store_is_not_followed_or_replaced(self):
        outside = Path(self.temp.name) / 'outside-store.json'
        outside.write_text('{"private":true}', encoding='utf-8')
        self.data_file.parent.mkdir(parents=True)
        self.data_file.symlink_to(outside)
        with self.assertRaises(WorkError) as caught:
            self.service.snapshot()
        self.assertEqual(500, caught.exception.status)
        self.assertEqual('{"private":true}', outside.read_text(encoding='utf-8'))
        self.assertTrue(self.data_file.is_symlink())

    def test_dirty_only_source_change_rejects_the_frozen_reviewed_context(self):
        sid = self.start()
        self.checkpoint(sid)
        displayed = self.service.snapshot()['context']
        self.write('AAEmu.Game/Fixture/A/source.py', 'answer = 43\n')
        current = self.service.snapshot()['context']
        self.assertEqual(displayed['source_sha'], current['source_sha'])
        self.assertEqual(displayed['canonical_links'], current['canonical_links'])
        self.assertNotEqual(displayed['dirty_state'], current['dirty_state'])
        self.assert_error(409, 'session_resume', session_id=sid, reviewed_context=displayed)
        resumed = self.mutate('session_resume', session_id=sid, reviewed_context=current)
        self.assertEqual('active', resumed['sessions'][0]['status'])
        self.assertEqual(current, resumed['sessions'][0]['context'])

    def test_altered_displayed_excerpt_cannot_be_recorded_as_current_review(self):
        sid = self.start()
        self.checkpoint(sid)
        altered = deepcopy(self.service.snapshot()['context'])
        altered['current_excerpt'] = '# Different objective not displayed by the server\n'
        self.assert_error(409, 'session_resume', session_id=sid, reviewed_context=altered)

    def test_directory_sync_failure_returns_the_committed_state_with_a_warning(self):
        sid = self.start()
        previous = self.service.snapshot()['revision']
        real_sync = os.fsync
        def fail_directory(fd):
            if stat.S_ISDIR(os.fstat(fd).st_mode):
                raise OSError('Fixture post-commit directory sync failure')
            return real_sync(fd)
        with patch('work_dashboard.os.fsync', side_effect=fail_directory):
            result = self.checkpoint(sid)
        self.assertEqual(previous + 1, result['revision'])
        self.assertTrue(result['storage_warning'])
        fresh = WorkDashboard(self.root, self.data_file, self.research).snapshot()
        self.assertEqual(result['revision'], fresh['revision'])
        self.assertEqual('paused', fresh['sessions'][0]['status'])
        self.assertEqual(result['sessions'][0]['checkpoint'], fresh['sessions'][0]['checkpoint'])

    def test_file_sync_failure_preserves_the_previous_checkpoint(self):
        sid = self.start()
        before = self.data_file.read_bytes()
        with patch('work_dashboard.os.fsync', side_effect=OSError('Fixture pre-commit file sync failure')):
            with self.assertRaises(WorkError) as caught:
                self.checkpoint(sid)
        self.assertEqual(500, caught.exception.status)
        self.assertEqual(before, self.data_file.read_bytes())
        fresh = WorkDashboard(self.root, self.data_file, self.research).snapshot()
        self.assertEqual('active', fresh['sessions'][0]['status'])

    def test_registered_gamedata_loader_source_is_previewable_without_runtime_data(self):
        code = 'public sealed class FixtureLootGameData { }\n'
        self.write('AAEmu.Game/GameData/FixtureLootGameData.cs', code)
        feature = deepcopy(self.feature)
        feature['references'].append(self.ref('loader', 'AAEmu.Game/GameData/FixtureLootGameData.cs'))
        self.mutate('feature_save', feature=feature)
        preview = self.service.reference(feature['id'], 'loader')
        self.assertEqual(code, preview['text'])
        self.assertEqual(hashlib.sha256(code.encode()).hexdigest(), preview['sha256'])
        for path in ('AAEmu.Game/Data/runtime.json', 'AAEmu.Game/GameData/runtime.json',
                     'AAEmu.Game/GameData/compact.sqlite3'):
            bad = deepcopy(feature)
            bad['references'].append(self.ref('runtime-data', path))
            self.assert_error(400, 'feature_save', feature=bad)

    def test_current_context_does_not_smuggle_preserved_allocations_into_a_brief(self):
        self.write('.omp/state/CURRENT.md',
                   '# CURRENT\n## Objective\nCurrent bounded control outcome.\n'
                   '## Next action\nReview this scope only.\n\n'
                   '## Preserved prior allocation\nUnrelated gameplay campaign was once active.\n')
        sid = self.start()
        brief = self.service.brief(sid)
        self.assertIn('Current bounded control outcome.', brief['context']['current_excerpt'])
        self.assertNotIn('Unrelated gameplay campaign', brief['context']['current_excerpt'])
        full_current = (self.root / '.omp/state/CURRENT.md').read_bytes()
        self.assertEqual(hashlib.sha256(full_current).hexdigest(), brief['context']['current_sha256'])

    def test_fresh_brief_retains_prior_context_without_promoting_its_facts(self):
        old = self.start()
        checkpoint = {'confirmed': ['Prior progress was observed in the fixture'],
                      'unknowns': ['Prior recovery is unproved'], 'next_action': 'Continue at the remaining boundary',
                      'evidence_refs': ['fixture:prior-receipt'], 'blocker': ''}
        self.mutate('session_checkpoint', session_id=old, checkpoint=checkpoint, status='paused')
        fresh = self.start(strategy='fresh', reason='Move the same bounded work to a clean context')
        brief = self.service.brief(fresh)
        self.assertEqual(old, brief['predecessor']['id'])
        self.assertTrue(brief['predecessor']['contract_compatible'])
        self.assertEqual('Continue at the remaining boundary', self.get_session(fresh)['checkpoint']['next_action'])
        self.assertEqual([], self.get_session(fresh)['checkpoint']['confirmed'])
        self.assertEqual(['fixture:prior-receipt'], brief['predecessor']['checkpoint']['evidence_refs'])
        self.checkpoint(fresh)
        changed = deepcopy(self.get_card())
        changed['outcome'] = 'A newly bounded changed outcome'
        changed['next_action'] = 'Review the changed scope before proceeding'
        self.mutate('card_save', card=changed)
        replacement = self.start(strategy='fresh', reason='Changed contract requires a new bounded record')
        self.assertFalse(self.service.brief(replacement)['predecessor']['contract_compatible'])
        self.assertEqual('Review the changed scope before proceeding', self.get_session(replacement)['checkpoint']['next_action'])
        self.assertEqual([], self.get_session(replacement)['checkpoint']['confirmed'])


if __name__ == '__main__':
    unittest.main()
