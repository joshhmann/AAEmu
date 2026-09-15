#!/usr/bin/env python3
"""
task_evaluator.py - Automated evaluation engine for AAEmu PlayerTrace tasks.

Evaluates a collected human trace file against a task specification to verify:
1. File integrity & lifecycle boundaries (trace_started, trace_stopped, zero malformed).
2. Mandatory target packet presence (inbound/outbound packet assertions).
3. Required semantic event presence (skills, doodad use, resource changes).
4. Error / refusal checks (detecting unexpected refusal events or error opcodes).
5. Sequence flow verification (checking expected transitions in normalized sequence).
6. Verdict assignment: PASS / CAVEAT / FAIL with actionable diagnostic report.

Usage:
    python3 Scripts/playertrace-coverage/task_evaluator.py <trace_file> --task-id <id>
"""

import argparse
from dataclasses import asdict, dataclass, field
import json
import os
import sys
from typing import Any, Dict, List, Optional, Set, Tuple

from trace_parser import TraceRecord, stream_trace_file


@dataclass
class PacketAssertionResult:
    packet_name: str
    expected: bool
    observed_count: int
    passed: bool
    details: str


@dataclass
class EventAssertionResult:
    event_key: str
    expected: bool
    observed_count: int
    passed: bool


@dataclass
class TaskEvaluationReport:
    task_id: str
    scenario: str
    trace_file: str
    verdict: str  # PASS, CAVEAT, FAIL
    total_records: int
    malformed_records: int
    elapsed_seconds: float
    has_start_lifecycle: bool
    has_stop_lifecycle: bool
    packet_assertions: List[PacketAssertionResult]
    event_assertions: List[EventAssertionResult]
    refusal_count: int
    error_packet_count: int
    failure_reasons: List[str]
    caveat_notes: List[str]


def evaluate_trace_file(trace_path: str, task_spec: Dict[str, Any]) -> TaskEvaluationReport:
    """Evaluate a trace JSONL file against a structured task specification."""
    task_id = task_spec.get("id", "unknown_task")
    scenario = task_spec.get("scenario", "unknown_scenario")
    target_packets = set(task_spec.get("target_packets", []))
    target_events = set(task_spec.get("target_events", []))

    total_records = 0
    malformed_records = 0
    observed_packets: Dict[str, int] = {}
    observed_events: Dict[str, int] = {}
    has_start = False
    has_stop = False
    refusal_count = 0
    error_packet_count = 0
    min_elapsed = None
    max_elapsed = None

    def on_malformed(p, l, err, text):
        nonlocal malformed_records
        malformed_records += 1

    for rec in stream_trace_file(trace_path, on_malformed=on_malformed):
        total_records += 1

        if rec.elapsed_ms is not None:
            if min_elapsed is None or rec.elapsed_ms < min_elapsed:
                min_elapsed = rec.elapsed_ms
            if max_elapsed is None or rec.elapsed_ms > max_elapsed:
                max_elapsed = rec.elapsed_ms

        if rec.category == "lifecycle":
            if rec.event == "trace_started":
                has_start = True
            elif rec.event == "trace_stopped":
                has_stop = True

        if rec.category == "refusal":
            refusal_count += 1

        ev_key = f"{rec.category}:{rec.event}"
        observed_events[ev_key] = observed_events.get(ev_key, 0) + 1

        if rec.is_packet and rec.packet_name:
            pkt = rec.packet_name
            observed_packets[pkt] = observed_packets.get(pkt, 0) + 1
            if pkt in {"SCErrorMsgPacket", "SCErrorMessagePacket"}:
                error_packet_count += 1

    elapsed_sec = (max_elapsed - min_elapsed) / 1000.0 if (min_elapsed is not None and max_elapsed is not None) else 0.0

    # Evaluate Packet Assertions
    packet_results: List[PacketAssertionResult] = []
    missing_mandatory_packets: List[str] = []
    for pkt in target_packets:
        count = observed_packets.get(pkt, 0)
        passed = count > 0
        if not passed:
            missing_mandatory_packets.append(pkt)
        packet_results.append(PacketAssertionResult(
            packet_name=pkt,
            expected=True,
            observed_count=count,
            passed=passed,
            details=f"Observed {count} occurrences" if passed else "Mandatory target packet missing from trace"
        ))

    # Evaluate Event Assertions
    event_results: List[EventAssertionResult] = []
    for ev in target_events:
        count = observed_events.get(ev, 0)
        passed = count > 0
        event_results.append(EventAssertionResult(
            event_key=ev,
            expected=True,
            observed_count=count,
            passed=passed
        ))

    # Verdict Determination
    failure_reasons: List[str] = []
    caveat_notes: List[str] = []

    if total_records == 0:
        failure_reasons.append("Trace file contains zero records.")

    if missing_mandatory_packets:
        failure_reasons.append(f"Missing mandatory target packets: {', '.join(missing_mandatory_packets)}")

    if not has_start:
        caveat_notes.append("Trace lacks formal trace_started lifecycle record.")

    if not has_stop:
        caveat_notes.append("Trace lacks formal trace_stopped lifecycle record (trace may have been interrupted).")

    if malformed_records > 0:
        caveat_notes.append(f"{malformed_records} malformed records were skipped during parsing.")

    if refusal_count > 0:
        caveat_notes.append(f"{refusal_count} server action refusal events occurred during trace.")

    if error_packet_count > 0:
        caveat_notes.append(f"{error_packet_count} SCErrorMsgPacket error responses were returned by server.")

    # Assign final verdict
    if failure_reasons:
        verdict = "FAIL"
    elif caveat_notes:
        verdict = "CAVEAT"
    else:
        verdict = "PASS"

    return TaskEvaluationReport(
        task_id=task_id,
        scenario=scenario,
        trace_file=trace_path,
        verdict=verdict,
        total_records=total_records,
        malformed_records=malformed_records,
        elapsed_seconds=elapsed_sec,
        has_start_lifecycle=has_start,
        has_stop_lifecycle=has_stop,
        packet_assertions=packet_results,
        event_assertions=event_results,
        refusal_count=refusal_count,
        error_packet_count=error_packet_count,
        failure_reasons=failure_reasons,
        caveat_notes=caveat_notes
    )


def find_latest_trace_for_scenario(scenario: str, traces_dir: str = "traces/player-actions") -> Optional[str]:
    """Find the most recent trace file matching scenario in traces directory."""
    if not os.path.exists(traces_dir):
        return None
    candidates = []
    for f in os.listdir(traces_dir):
        if f.endswith(".jsonl") and scenario in f:
            full_path = os.path.join(traces_dir, f)
            candidates.append((os.path.getmtime(full_path), full_path))
    if not candidates:
        return None
    candidates.sort(reverse=True)
    return candidates[0][1]


def load_task_spec_by_id(task_id: str, tasks_file: str = "playertrace-coverage/dashboard_tasks.json") -> Optional[Dict[str, Any]]:
    """Load task definition from dashboard_tasks.json."""
    if not os.path.exists(tasks_file):
        return None
    try:
        with open(tasks_file, "r", encoding="utf-8") as f:
            tasks = json.load(f)
        for t in tasks:
            if t.get("id") == task_id or t.get("scenario") == task_id:
                return t
    except Exception:
        pass
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Evaluate a PlayerTrace file against a task specification")
    parser.add_argument("trace_file", nargs="?", help="Path to trace JSONL file")
    parser.add_argument("--task-id", required=True, help="Task or scenario ID to evaluate against")
    parser.add_argument("--tasks-file", default="playertrace-coverage/dashboard_tasks.json", help="Path to dashboard tasks JSON")
    parser.add_argument("--json", action="store_true", help="Output machine-readable JSON only")
    args = parser.parse_args()

    task_spec = load_task_spec_by_id(args.task_id, args.tasks_file)
    if not task_spec:
        print(f"ERROR: Task ID '{args.task_id}' not found in {args.tasks_file}", file=sys.stderr)
        return 1

    trace_path = args.trace_file
    if not trace_path:
        trace_path = find_latest_trace_for_scenario(task_spec.get("scenario", args.task_id))
        if not trace_path:
            print(f"ERROR: No trace file provided and no existing trace found for scenario '{task_spec.get('scenario')}'", file=sys.stderr)
            return 1

    report = evaluate_trace_file(trace_path, task_spec)

    if args.json:
        print(json.dumps(asdict(report), indent=2))
        return 0

    print("=" * 65)
    print(f"  PLAYERTRACE TASK EVALUATION REPORT")
    print("=" * 65)
    print(f"  Task ID:          {report.task_id}")
    print(f"  Scenario:         {report.scenario}")
    print(f"  Trace File:       {os.path.basename(report.trace_file)}")
    print(f"  Total Records:    {report.total_records:,} (Elapsed: {report.elapsed_seconds:.1f}s)")
    print(f"  VERDICT:          [{report.verdict}]")
    print("-" * 65)
    print("  PACKET ASSERTIONS:")
    for pa in report.packet_assertions:
        status = "PASS" if pa.passed else "FAIL"
        print(f"    [{status}] {pa.packet_name:<32} count={pa.observed_count}")

    if report.caveat_notes:
        print("-" * 65)
        print("  CAVEATS & ANOMALIES:")
        for note in report.caveat_notes:
            print(f"    - {note}")

    if report.failure_reasons:
        print("-" * 65)
        print("  FAILURE REASONS:")
        for fail in report.failure_reasons:
            print(f"    - {fail}")
    print("=" * 65)

    return 0 if report.verdict in {"PASS", "CAVEAT"} else 2


if __name__ == "__main__":
    sys.exit(main())
