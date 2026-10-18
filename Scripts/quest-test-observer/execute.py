#!/usr/bin/env python3
"""Run an existing owned test, judge its evidence and invoke Jev once.

This does not allocate a lane. The command must own its normal cleanup. A
cooperative stop breaks observation only; forced termination never proves cleanup.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
import uuid

from run import EXIT, process_start, utc, write_json


def native_identity_matches(native, manifest):
    if not isinstance(native, dict):
        return False
    expected = manifest.get('native_contract', {})
    provenance = native.get('provenance', {})
    if not isinstance(provenance, dict):
        return False
    source = provenance.get('source', {})
    if not isinstance(source, dict):
        return False
    return bool(expected and native.get('botName') == manifest['bot_name']
                and isinstance(native.get('runId'), str) and native['runId']
                and native.get('characterId') == manifest['bot_id']
                and native.get('scenario') == expected.get('scenario')
                and native.get('observerDatabaseId') == manifest['database_id']
                and source.get('Head') == manifest['source_sha']
                and source.get('Unavailable') is False
                and all(expected.get(key) and native.get(key) == expected[key]
                        for key in ('canonicalMd5', 'runtimeMd5'))
                and all(expected.get(key) and provenance.get(key) == expected[key]
                        for key in ('PublishSourceIdentity', 'testAssemblySha256', 'runtimeGameSha256')))


LOOP_SCHEMA = 'aaemu-loop-development/1'
LOOP_SOURCE_KEYS = {
    'head': 'source_sha', 'publishSourceIdentity': 'PublishSourceIdentity',
    'testAssemblySha256': 'testAssemblySha256', 'runtimeGameSha256': 'runtimeGameSha256',
    'canonicalMd5': 'canonicalMd5', 'runtimeMd5': 'runtimeMd5'}


def read_object(path, maximum=16 * 1024 ** 2):
    """Bound native evidence reads, including a file replaced during observation."""
    with path.open('rb') as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise ValueError(f'Evidence exceeds byte bound: {path.name}')
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError(f'Evidence must be an object: {path.name}')
    return value, hashlib.sha256(raw).hexdigest()


def loop_identity(manifest):
    return {'scenario': manifest['native_contract']['scenario'],
            'databaseId': manifest['database_id'], 'characterId': manifest['bot_id'],
            'botName': manifest['bot_name']}


def validate_loop_manifest(manifest):
    identity = loop_identity(manifest)
    contract = manifest['native_contract']
    if (not all(identity.values()) or not manifest.get('source_sha')
            or not all(contract.get(key) for key in LOOP_SOURCE_KEYS.values() if key != 'source_sha')
            or contract['canonicalMd5'] != contract['runtimeMd5']):
        raise ValueError('Loop manifest requires fixture, source, binary and matching data identities')
    portions = contract.get('portions')
    if portions is not None and not valid_portions(portions):
        raise ValueError('Loop manifest portions must be unique ordered names')


def valid_portions(portions):
    return (isinstance(portions, list) and bool(portions)
            and all(isinstance(name, str) and name.strip() for name in portions)
            and len(set(portions)) == len(portions))


def validate_loop_checkpoint(checkpoint, manifest, mode):
    """Validate native receipt structure and its binding to this invocation."""
    portions = checkpoint.get('portions')
    index = checkpoint.get('nextPortion')
    if (checkpoint.get('schema') != LOOP_SCHEMA or checkpoint.get('mode') != mode
            or not isinstance(checkpoint.get('runId'), str) or not checkpoint['runId']
            or checkpoint.get('identity') != loop_identity(manifest)
            or not valid_portions(portions) or type(index) is not int
            or not 0 <= index <= len(portions)
            or checkpoint.get('currentPortion') != (portions[index] if index < len(portions) else None)
            or not isinstance(checkpoint.get('initialState'), dict)):
        raise ValueError('Loop checkpoint identity, schema or portion boundary mismatch')
    expected_portions = manifest['native_contract'].get('portions')
    if expected_portions is not None and portions != expected_portions:
        raise ValueError('Loop portion contract changed')
    source = checkpoint.get('source')
    if not isinstance(source, dict) or any(
            source.get(key) != (manifest['source_sha'] if expected == 'source_sha'
                                else manifest['native_contract'][expected])
            for key, expected in LOOP_SOURCE_KEYS.items()):
        raise ValueError('Loop checkpoint source, binaries or reference data mismatch')
    if any(type(checkpoint.get(key)) is not bool for key in ('quiescent', 'durableVerified', 'resumable')):
        raise ValueError('Loop checkpoint cleanup facts are missing')
    status = checkpoint.get('status')
    failure = checkpoint.get('firstFailure')
    if status == 'FAIL':
        if (not isinstance(failure, dict) or not failure.get('reason')
                or failure.get('portion') != (checkpoint['currentPortion'] or 'finalization')):
            raise ValueError('Loop checkpoint lost its first failure')
    elif status == ('DEVELOPMENT_PASS' if mode == 'development' else 'PASS'):
        if (failure is not None or index != len(portions) or checkpoint['resumable']
                or not checkpoint['quiescent'] or not checkpoint['durableVerified']
                or not isinstance(checkpoint.get('durableState'), dict)):
            raise ValueError('Loop success requires complete portions and safe settlement')
    else:
        raise ValueError('Loop checkpoint is not a final result for this mode')
    if checkpoint['resumable'] and (
            mode != 'development' or status != 'FAIL' or index == len(portions)
            or not checkpoint['quiescent'] or not checkpoint['durableVerified']
            or not isinstance(checkpoint.get('durableState'), dict)
            or not isinstance(checkpoint.get('continuationData'), dict)):
        raise ValueError('Loop checkpoint is not safe to resume')


def validate_loop_report(native, checkpoint):
    # The loop receipt, not the legacy quest report envelope, owns identity/source.
    if (native.get('loopExecution') != checkpoint
            or type(native.get('passed')) is not bool
            or native['passed'] != (checkpoint['status'] != 'FAIL')):
        raise ValueError('Native report and checkpoint do not prove the same result')


def admit_loop_resume(previous_directory, manifest):
    """No game-state reads or mutations: native Begin must re-observe durable state."""
    previous = Path(previous_directory).resolve()
    execution, _ = read_object(previous / 'execution.json')
    old_manifest, _ = read_object(previous / 'manifest.json')
    validate_loop_manifest(old_manifest)
    path = previous / 'test/loop-checkpoint.json'
    checkpoint, digest = read_object(path, 1024 ** 2)
    if (execution.get('loop_mode') != 'development' or execution.get('status') != 'FAIL'
            or execution.get('forced_stop') is not False
            or execution.get('cleanup_state') != 'concluded'
            or execution.get('children_reaped') is not True or not execution.get('finished_utc')
            or execution.get('checkpoint_sha256') != digest
            or execution.get('source_sha') != old_manifest['source_sha']
            or execution.get('database_id') != old_manifest['database_id']
            or execution.get('run_id') != old_manifest['run_id']
            or execution.get('loop_run_id') != checkpoint.get('runId')
            or execution.get('expected_loop_run_id') != checkpoint.get('runId')
            or type(execution.get('test_exit_code')) is not int or execution['test_exit_code'] < 0
            or execution.get('native_test_passed') is not False
            or execution.get('native_identity_matches') is not True
            or execution.get('loop_evidence_valid') is not True):
        raise ValueError('Previous execution or immutable checkpoint does not permit resume')
    validate_loop_checkpoint(checkpoint, old_manifest, 'development')
    native, _ = read_object(previous / 'test/report.json')
    validate_loop_report(native, checkpoint)
    if execution.get('native_run_id') != native.get('runId'):
        raise ValueError('Previous native report does not belong to its execution')
    if not checkpoint['resumable']:
        raise ValueError('Previous loop checkpoint is not resumable')
    if (checkpoint['identity'] != loop_identity(manifest)
            or any(checkpoint['source'][key] != manifest['native_contract'][key]
                   for key in ('canonicalMd5', 'runtimeMd5'))
            or ('portions' in manifest['native_contract']
                and checkpoint['portions'] != manifest['native_contract']['portions'])):
        raise ValueError('Resume fixture, bot, portion contract or reference data changed')
    return checkpoint, {'path': str(path), 'sha256': digest, 'runId': checkpoint['runId']}


def conclude_loop(evidence, manifest, mode, receipt, predecessor, previous_checkpoint):
    receipt.update(observer_status='NOT_APPLICABLE', cleanup_state='unproved',
                   status='INCONCLUSIVE', loop_evidence_valid=False,
                   native_report_present=(evidence / 'report.json').is_file())
    try:
        checkpoint, digest = read_object(evidence / 'loop-checkpoint.json', 1024 ** 2)
        receipt['checkpoint_sha256'] = digest
        validate_loop_checkpoint(checkpoint, manifest, mode)
        if checkpoint['runId'] != receipt['expected_loop_run_id']:
            raise ValueError('Loop checkpoint is not from this supervisor invocation')
        if checkpoint.get('predecessor') != predecessor:
            raise ValueError('Loop predecessor does not match the explicitly admitted invocation')
        if previous_checkpoint is not None and (
                checkpoint['runId'] == previous_checkpoint['runId']
                or checkpoint['portions'] != previous_checkpoint['portions']
                or checkpoint['initialState'] != previous_checkpoint['durableState']
                or checkpoint['nextPortion'] < previous_checkpoint['nextPortion']):
            raise ValueError('Loop continuation lost its admitted state or portion boundary')
        native, _ = read_object(evidence / 'report.json')
        receipt.update(native_identity_matches=False,
                       native_run_id=native.get('runId'), native_test_passed=native.get('passed') is True)
        validate_loop_report(native, checkpoint)
        receipt['native_identity_matches'] = True
        if receipt.get('loop_progress_error'):
            raise ValueError('Loop progress evidence was invalid')
        if receipt.get('loop_progress_run_id', checkpoint['runId']) != checkpoint['runId']:
            raise ValueError('Loop final receipt belongs to another progress invocation')
        if receipt.get('first_failure') is not None and receipt['first_failure'] != checkpoint['firstFailure']:
            raise ValueError('Loop final receipt erased or changed the first observed failure')
        receipt.update(loop_evidence_valid=True, loop_run_id=checkpoint['runId'],
                       first_failure=checkpoint['firstFailure'], loop_status=checkpoint['status'])
        if not receipt['forced_stop'] and checkpoint['quiescent'] and checkpoint['durableVerified']:
            receipt['cleanup_state'] = 'concluded'
        if not receipt['forced_stop']:
            receipt['status'] = ('FAIL' if receipt['test_exit_code'] != 0 or checkpoint['status'] == 'FAIL'
                                 else checkpoint['status'] if receipt['cleanup_state'] == 'concluded'
                                 and not receipt['stop_requested']
                                 else 'INCONCLUSIVE')
    except (OSError, ValueError, KeyError, TypeError) as error:
        receipt['loop_evidence_error'] = str(error)
        if not receipt['forced_stop'] and receipt['test_exit_code'] != 0:
            receipt['status'] = 'FAIL'


def start_review(root, report):
    """Start one read-only reviewer; its availability never decides gameplay."""
    review_path = root / 'review-verdict.json'
    if len(json.dumps(report, indent=2).encode('utf-8')) + 1 > 1024 ** 2:
        raise ValueError('Reviewer input exceeds the separate 1MiB limit')
    write_json(review_path, report)
    with (root / 'jev.json').open('xb') as summary, (root / 'jev.stderr').open('xb') as errors:
        return subprocess.Popen(
            ['bun', str(Path(__file__).with_name('jev_review.ts')), str(review_path),
             '--events', str(root / 'observer/events.jsonl')],
            stdin=subprocess.DEVNULL, stdout=summary, stderr=errors, start_new_session=True)


def execute(args):
    os.umask(0o077)
    mode, resume = getattr(args, 'loop_mode', None), getattr(args, 'resume', None)
    if mode not in (None, 'development', 'acceptance') or (resume and mode != 'development'):
        raise ValueError('Resume requires explicit development mode; acceptance is always fresh')
    manifest = json.loads(Path(args.manifest).read_text())
    previous_checkpoint, predecessor = None, None
    if mode:
        validate_loop_manifest(manifest)
        if resume:
            previous_checkpoint, predecessor = admit_loop_resume(resume, manifest)
    root = Path(args.out).resolve()
    root.mkdir(parents=True, exist_ok=False)
    evidence = root / 'test'
    stop, done = root / 'request-stop.json', root / 'producer-ended.json'
    # Explicit CLI mode controls this invocation; inherited resume/output cannot leak.
    env = {key: value for key, value in os.environ.items() if not key.startswith('AAEMU_LOOP_')}
    env.update(E2E_QUEST_OBSERVER_DIRECTORY=str(evidence), E2E_QUEST_OBSERVER_STOP_FILE=str(stop))
    if mode:
        invocation_id = uuid.uuid4().hex
        env.update(AAEMU_LOOP_MODE=mode, AAEMU_LOOP_OUTPUT=str(evidence), AAEMU_LOOP_STOP_FILE=str(stop))
        env['AAEMU_LOOP_RUN_ID'] = invocation_id
        if predecessor:
            env.update(AAEMU_LOOP_RESUME=predecessor['path'], AAEMU_LOOP_RESUME_SHA256=predecessor['sha256'])
    if manifest.get('evidence_layer') != 'synthetic rig':
        if args.seconds < 1700 or args.cleanup_seconds < 500:
            raise ValueError('Native test needs fixture + 900s observation + 325s flush + margin: 1700s and 500s cleanup')
        if not mode and manifest.get('input_format') != 'bridge-quest-observe':
            raise ValueError('Native automatic test requires the full passive quest audit')
    # Live time is declared by the caller; synthetic replay is explicitly labelled.
    write_json(root / 'manifest.json', manifest)
    receipt = {'started_utc': utc(), 'command': args.command, 'source_sha': manifest['source_sha'],
               'database_id': manifest['database_id'], 'run_id': manifest['run_id'],
               'evidence_layer': manifest['evidence_layer'], 'owns': 'spawned test, observer and read-only reviewer only',
               'cleanup_seconds': args.cleanup_seconds, 'deadline_seconds': args.seconds,
               'cleanup_state': 'pending', 'review_status': 'not_started',
               'automatic_reruns': 0, 'forced_stop': False}
    if mode:
        receipt.update(loop_mode=mode, predecessor=predecessor, checkpoint_sha256=None,
                       expected_loop_run_id=invocation_id,
                       review_status='not_applicable',
                       review_reason='Quest CLI triage requires quest acceptance/events; native loop packets '
                                     'need the separate generic advisory reviewPacket seam')
    children = []
    started = time.monotonic()
    observer = None
    review, review_started = None, None

    def review_once(report, phase):
        nonlocal review, review_started
        review_input = dict(report)
        if phase == 'final':
            review_input['status'] = receipt['status']
        review_input['test_execution'] = {key: receipt.get(key) for key in
            ('test_exit_code', 'native_report_present', 'native_identity_matches',
             'native_test_passed', 'native_run_id', 'observer_status', 'forced_stop',
             'cleanup_state')}
        review_input['test_execution'].update(
            review_phase=phase, native_report=str(evidence / 'report.json'))
        try:
            review = start_review(root, review_input)
        except (OSError, ValueError) as error:
            receipt.update(review_status='unavailable', reviewer_error_type=type(error).__name__)
            return
        children.append((review, process_start(review.pid)))
        review_started = time.monotonic()
        receipt.update(review_status='running', review_phase=phase, reviewer_pid=review.pid,
                       review_started_utc=utc())
        write_json(root / 'execution.json', receipt)
        print(json.dumps({'code': 'JEV_REVIEW_STARTED', 'phase': phase,
                          'cleanup_state': receipt['cleanup_state']}), flush=True)

    def limit_review():
        if review is None:
            return
        running = review.poll() is None
        oversized = any(path.exists() and path.stat().st_size > 65536
                        for path in (root / 'jev.json', root / 'jev.stderr'))
        rss = 0
        if running:
            try:
                rss = int(Path(f'/proc/{review.pid}/statm').read_text().split()[1]) * os.sysconf('SC_PAGE_SIZE')
            except FileNotFoundError:
                pass
        limit = ('output_cap' if oversized else 'rss_cap' if rss > args.max_rss_mb * 1024 ** 2
                 else 'deadline' if running and time.monotonic() - review_started >= 20 else None)
        if limit:
            if running:
                terminate_owned(review, next(identity for child, identity in children if child is review))
            receipt.update(jev_exit_code=124, review_status='unavailable', reviewer_limit=limit)


    def record_review_result():
        code = review.wait()
        limit_review()
        if receipt.get('jev_exit_code') == 124:
            return
        receipt['jev_exit_code'] = code
        try:
            with (root / 'jev.json').open('rb') as stream:
                raw = stream.read(65537)
            if len(raw) > 65536:
                receipt.update(jev_exit_code=124, review_status='unavailable', reviewer_limit='output_cap')
                return
            summary = json.loads(raw)
            status = summary.get('review_status') if isinstance(summary, dict) else None
            receipt['review_status'] = status if status in (
                'completed', 'abstained', 'unavailable', 'invalid') else 'invalid'
        except (ValueError, OSError):
            receipt['review_status'] = 'unavailable' if code else 'invalid'
    try:
        with (root / 'test.log').open('xb') as log, (root / 'observer.log').open('xb') as observer_log:
            test = subprocess.Popen(args.command, env=env, stdin=subprocess.DEVNULL,
                                    stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            children.append((test, process_start(test.pid)))
            receipt['test_pid'] = test.pid
            write_json(root / 'execution.json', receipt)
            requested_at = None
            while test.poll() is None:
                elapsed = time.monotonic() - started
                raw = evidence / 'observations.jsonl'
                if not mode and observer is None and raw.is_file():
                    observer = subprocess.Popen([sys.executable, str(Path(__file__).with_name('run.py')),
                        'observe', '--manifest', str(root / 'manifest.json'), '--snapshots', str(raw),
                        '--out', str(root / 'observer'), '--follow', '--stop-file', str(done),
                        '--request-stop-file', str(stop), '--seconds', str(args.seconds)],
                        stdout=observer_log, stderr=subprocess.STDOUT, start_new_session=True)
                    children.append((observer, process_start(observer.pid)))
                    receipt['observer_pid'] = observer.pid
                    write_json(root / 'execution.json', receipt)
                if mode and 'first_failure' not in receipt and 'loop_progress_error' not in receipt:
                    progress_path = evidence / 'loop-progress.json'
                    if progress_path.is_file():
                        try:
                            progress, _ = read_object(progress_path, 1024 ** 2)
                            if progress.get('runId') != receipt['expected_loop_run_id']:
                                raise ValueError('Loop progress is not from this supervisor invocation')
                            failure = progress.get('firstFailure')
                            if failure is not None:
                                validate_loop_checkpoint(progress, manifest, mode)
                                receipt['loop_progress_run_id'] = progress['runId']
                                receipt['first_failure'] = failure
                                receipt['failed_portion'] = failure.get('portion')
                                if not stop.exists():
                                    write_json(stop, {'code': 'LOOP_FIRST_FAILURE', 'firstFailure': failure,
                                                      'timestamp': utc()})
                                write_json(root / 'execution.json', receipt)
                        except (OSError, ValueError, KeyError, TypeError) as error:
                            receipt['loop_progress_error'] = str(error)
                            if not stop.exists():
                                write_json(stop, {'code': 'LOOP_PROGRESS_INVALID', 'timestamp': utc()})
                if stop.exists() and requested_at is None:
                    requested_at = time.monotonic()
                if elapsed >= args.seconds and requested_at is None:
                    write_json(stop, {'code': 'TEST_DEADLINE', 'timestamp': utc()})
                    requested_at = time.monotonic()
                if observer is not None and observer.poll() is not None and not stop.exists():
                    verdict = root / 'observer/verdict.json'
                    if not verdict.is_file() or json.loads(verdict.read_text())['status'] != 'PASS':
                        write_json(stop, {'code': 'OBSERVER_CONCLUDED_WITHOUT_PASS', 'timestamp': utc()})
                        requested_at = time.monotonic()
                verdict_path = root / 'observer/verdict.json'
                if (receipt['review_status'] == 'not_started' and observer is not None
                        and observer.poll() is not None and verdict_path.is_file()):
                    provisional = json.loads(verdict_path.read_text())
                    receipt['observer_status'] = provisional['status']
                    review_once(provisional, 'provisional')
                limit_review()
                if review is not None and review.poll() is not None and receipt['review_status'] == 'running':
                    record_review_result()
                    write_json(root / 'execution.json', receipt)
                    print(json.dumps({'code': 'JEV_REVIEW_READY', 'review_status': receipt['review_status'],
                                      'cleanup_state': receipt['cleanup_state']}), flush=True)
                log_bytes = 0
                for path in root.rglob('*'):
                    try:
                        if path.is_file():
                            if path.parent == root and path.name in (
                                    'jev.json', 'jev.stderr', 'review-verdict.json', 'review-verdict.json.tmp'):
                                continue
                            log_bytes += path.stat().st_size
                    except FileNotFoundError:
                        pass  # Atomic receipt replacement removed its temporary name.
                rss_over = False
                for child, identity in children:
                    if child.poll() is None:
                        if child is review:
                            continue
                        try:
                            rss = int(Path(f'/proc/{child.pid}/statm').read_text().split()[1]) * os.sysconf('SC_PAGE_SIZE')
                            rss_over |= rss > args.max_rss_mb * 1024 ** 2
                        except FileNotFoundError:
                            pass  # Child exited between poll and stat.
                if log_bytes > args.max_bytes or rss_over:
                    if not stop.exists():
                        write_json(stop, {'code': 'OUTPUT_CAP', 'timestamp': utc()})
                    requested_at = requested_at or time.monotonic()
                    receipt['forced_stop'] = True
                    receipt['resource_fault'] = 'rss_cap' if rss_over else 'output_cap'
                    receipt['hard_stop_reason'] = 'emergency resource cap; native cleanup unproved'
                    break
                if requested_at is not None and time.monotonic() - requested_at >= args.cleanup_seconds:
                    receipt['forced_stop'] = True
                    receipt['hard_stop_reason'] = 'cooperative cleanup budget exhausted; native cleanup unproved'
                    break
                time.sleep(.1)
            if test.poll() is None:
                terminate_owned(test, children[0][1])
            receipt['test_exit_code'] = test.wait()
            if mode:
                receipt['stop_requested'] = stop.exists()
            write_json(done, {'finished_utc': utc(), 'exit_code': test.returncode})
            # A short test can finish before the supervisor's first poll.
            raw = evidence / 'observations.jsonl'
            if not mode and observer is None and raw.is_file():
                observer = subprocess.Popen([sys.executable, str(Path(__file__).with_name('run.py')),
                    'observe', '--manifest', str(root / 'manifest.json'), '--snapshots', str(raw),
                    '--out', str(root / 'observer'), '--seconds', str(args.seconds)],
                    stdout=observer_log, stderr=subprocess.STDOUT, start_new_session=True)
                children.append((observer, process_start(observer.pid)))
            if observer is not None:
                try:
                    receipt['observer_exit_code'] = observer.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    terminate_owned(observer, next(identity for child, identity in children if child is observer))
                    receipt['observer_exit_code'] = observer.wait()
            if mode:
                conclude_loop(evidence, manifest, mode, receipt, predecessor, previous_checkpoint)
            else:
                verdict_path = root / 'observer/verdict.json'
                verdict = json.loads(verdict_path.read_text()) if verdict_path.is_file() else None
                native_path = evidence / 'report.json'
                native = json.loads(native_path.read_text()) if native_path.is_file() else None
                receipt['native_report_present'] = native is not None
                receipt['native_identity_matches'] = native_identity_matches(native, manifest)
                receipt['native_run_id'] = native.get('runId') if native else None
                receipt['native_test_passed'] = native is not None and native.get('passed') is True
                receipt['observer_status'] = verdict['status'] if verdict else 'INCONCLUSIVE'
                receipt['cleanup_state'] = 'unproved' if receipt['forced_stop'] else 'concluded'
                receipt['status'] = ('PASS' if not receipt['forced_stop'] and test.returncode == 0
                    and receipt['native_test_passed'] and receipt['native_identity_matches']
                    and verdict and verdict['status'] == 'PASS'
                    else 'FAIL' if not receipt['forced_stop'] and (test.returncode != 0 or
                        (native is not None and native.get('passed') is False)) else 'INCONCLUSIVE')
                # Review can precede cleanup, but final native acceptance remains independent.
                if verdict is not None and receipt['review_status'] == 'not_started':
                    review_once(verdict, 'final')
            if review is not None:
                while review.poll() is None:
                    limit_review()
                    time.sleep(.1)
                record_review_result()
            if receipt['review_status'] == 'not_started':
                receipt['review_status'] = 'not_applicable'
            raw = evidence / 'observations.jsonl'
            if raw.is_file():
                with raw.open('rb') as source:
                    receipt['final_raw_sha256'] = hashlib.file_digest(source, 'sha256').hexdigest()
            else:
                receipt['final_raw_sha256'] = None
    finally:
        for child, identity in children:
            if child.poll() is None:
                terminate_owned(child, identity)
            child.wait()
        receipt.update(finished_utc=utc(), children_reaped=True,
                       wall_seconds=time.monotonic() - started)
        write_json(root / 'execution.json', receipt)
    print(json.dumps(receipt), flush=True)
    return 0 if receipt.get('status') == 'DEVELOPMENT_PASS' else EXIT.get(receipt.get('status'), 3)


def terminate_owned(child, identity):
    if child.poll() is not None:
        return
    if process_start(child.pid) != identity:
        raise RuntimeError('Owned test process identity changed; refuse signal')
    os.killpg(child.pid, signal.SIGTERM)
    try:
        child.wait(timeout=2)
    except subprocess.TimeoutExpired:
        if process_start(child.pid) != identity:
            raise RuntimeError('Owned test process identity changed; refuse kill')
        os.killpg(child.pid, signal.SIGKILL)
        child.wait(timeout=2)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--seconds', type=float, default=1700)
    parser.add_argument('--cleanup-seconds', type=float, default=500)
    parser.add_argument('--max-bytes', type=int, default=256 * 1024 ** 2)
    parser.add_argument('--max-rss-mb', type=int, default=2048)
    parser.add_argument('--loop-mode', choices=('development', 'acceptance'),
                        help='Native portion/checkpoint execution; no quest-specific evaluator')
    parser.add_argument('--resume', metavar='PREVIOUS_OUTPUT_DIRECTORY',
                        help='Explicitly resume one safely settled failed development attempt')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command[:1] == ['--']:
        args.command = args.command[1:]
    if not args.command or not 0 < args.seconds <= 1800 or not 0 < args.cleanup_seconds <= 500 or not 0 < args.max_bytes <= 512 * 1024 ** 2 or not 0 < args.max_rss_mb <= 4096:
        parser.error('Require existing test command and bounded duration/cleanup/output limits')
    if args.resume and args.loop_mode != 'development':
        parser.error('--resume requires --loop-mode development; acceptance must be fresh')
    try:
        raise SystemExit(execute(args))
    except (ValueError, KeyError, OSError) as error:
        print(f'Execution refused: {error}', file=sys.stderr)
        raise SystemExit(3)
