"""Capture discovery triage, not gameplay or autonomous capability verification."""

from dataclasses import asdict
import hashlib

from task_evaluator import evaluate_trace_file
from trace_parser import infer_scenario_from_path, stream_trace_file


def fingerprint(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def triage_tasks(tasks, trace_files, inventory, scenarios=None):
    """Keep exact captures and structural reuse leads separate; never merge files to pass."""
    captures = []
    for path in sorted(trace_files):
        declared, packets, events = set(), set(), set()
        for record in stream_trace_file(path):
            if record.category == "lifecycle" and record.event == "trace_started":
                declared.add(record.scenario)
            if record.is_packet:
                packets.add(record.packet_name)
            events.add(f"{record.category}:{record.event}")
        identity = declared or {infer_scenario_from_path(path)}
        captures.append({
            "path": str(path), "sha256": fingerprint(path),
            "scenarios": sorted(identity),
            "packets": packets,
            "events": events,
        })
    rows = []
    for task in tasks:
        scenario = task["scenario"]
        if scenarios and scenario not in scenarios:
            continue
        required = set(task.get("target_packets", []))
        required_events = set(task.get("target_events", []))
        unknown = sorted(p for p in required if inventory.get(p) is None)
        exact = []
        candidates = []
        for capture in captures:
            if scenarios and not set(capture["scenarios"]) <= scenarios:
                continue
            if capture["scenarios"] == [scenario]:
                report = asdict(evaluate_trace_file(capture["path"], task))
                # The existing evaluator reports event assertions but does not gate its verdict on them.
                missing_events = sorted(required_events - capture["events"])
                report["missing_required_events"] = missing_events
                if missing_events:
                    report["verdict"] = "FAIL"
                    report["failure_reasons"].append("Missing required events: " + ", ".join(missing_events))
                report["packet_assertions"].sort(key=lambda x: x["packet_name"])
                report["event_assertions"].sort(key=lambda x: x["event_key"])
                exact.append({"path": capture["path"], "sha256": capture["sha256"], "evaluation": report})
            elif (required or required_events) and required <= capture["packets"] and required_events <= capture["events"]:
                candidates.append({"path": capture["path"], "sha256": capture["sha256"],
                                   "scenarios": capture["scenarios"], "confidence": "structural-only"})
        if task.get("evaluation_kind") == "state_comparison":
            status = "STATE_EVIDENCE_REVIEW"
            action = "Locate before/after authoritative state and the restart boundary; packet capture cannot prove this task."
        elif task.get("evaluation_kind") == "multi_actor":
            status = "MULTI_ACTOR_REVIEW"
            action = "Correlate the named participants and authoritative consequences; never pool packet presence across files into a pass."
        elif unknown or not (required or required_events):
            status = "SPEC_REVIEW"
            action = "Resolve unknown packet names or empty assertions before requesting a capture."
        elif any(c["evaluation"]["verdict"] == "PASS" for c in exact):
            status = "CAPTURE_AVAILABLE"
            action = "Reuse the named capture within its assertion scope; gameplay and bot proof remain separate."
        elif exact:
            status = "CAPTURE_REVIEW"
            action = "Inspect existing capture failures/caveats before deciding whether to recapture."
        elif candidates:
            status = "REUSE_REVIEW"
            action = "Inspect candidate action windows and semantics; shared packets do not prove equivalence."
        elif str(task.get("status", "")).lower() == "complete":
            status = "EVIDENCE_LOCATION_REVIEW"
            action = "Locate the recorded evidence; no matching capture in the selected corpus."
        else:
            status = "CAPTURE_NEEDED"
            action = "No matching capture or complete structural candidate in the selected corpus; validate the task before capture."
        rows.append({"task_id": task["id"], "scenario": scenario, "title": task.get("title", scenario),
                     "priority": task.get("priority", "Medium").upper(), "category": task.get("category", "Unknown"),
                     "status": status, "catalog_status": task.get("status", "Unknown"),
                     "work_brief": task.get("notes", ""),
                     "target_packets": sorted(required), "unknown_packets": unknown,
                     "captures": exact, "reuse_candidates": candidates, "next_action": action,
                     "evidence_layer": "capture assertions / structural discovery only; producer build and gameplay proof UNKNOWN"})
    return rows
