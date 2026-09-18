#!/usr/bin/env python3
"""
infer_homestead_traces.py — Automated Intent & Milestone Inference Engine for AAEmu PlayerBot Traces.

Blindly consumes ActorAuditRecord / PlayerTrace JSONL event streams and infers:
1. Racial Origin & Starting Hub (from spatial coordinates).
2. Autonomous GOAP Goal & Intent Hierarchy (GoalClaimHomestead -> GoalErectHome).
3. Land Tenure Claims: Plot locations, housing zone clusters, and assigned House IDs.
4. Economic Resource Flows: Tax certificates, sapling plantings, timber yields, workbench craft conversions.
5. Construction Milestones & Progression Ladder completions.
6. Behavioral Verdict & Confidence Scoring.

Usage:
    python3 Scripts/playertrace-coverage/infer_homestead_traces.py scorecard-explorations/generated/m7-homestead-10bot-spike.jsonl

Evidence-honesty contract (AGENTS.md, evidence-honesty gate):
- Milestone detection is text matching over the recorded fields: heuristic discovery only.
  It is neither resource conservation nor verified intent.
- This evaluator preserves the evidence layer of its input and can never upgrade it. A
  trace carrying a synthetic/fixture marker is reported as a synthetic orchestration
  contract; a trace with no declared layer is reported UNKNOWN, never assumed live.
- A milestone is only credited on an authoritative completion (result `Completed` and a
  terminal `Completed (...)` transition in `state_changes`). Failed/cancelled requests,
  duplicate events, reordered or interleaved actor records and success-sounding detail
  text without a completion postcondition never count toward a verdict.
- "SYNTHETIC PASS" (synthetic scenario sequence reproduced) is reported separately from
  "PASS" (same sequence on an input that declares a live layer).
"""

import argparse
import collections
import datetime
import hashlib
import json
import math
import os
import re
import shlex
import subprocess
import sys
from typing import Any, Dict, Iterable, List, Optional, Tuple

UNKNOWN = "UNKNOWN"

# The only synthetic/fixture marker this evaluator recognises, checked in *every* string
# field of a record (the trace schema is lowercase snake_case: `detail`, `target_id`,
# `state_changes`, ... — never `Payload`/`Target`).
SYNTHETIC_MARKER = "[SYNTHETIC"

LAYER_SYNTHETIC = "SYNTHETIC ORCHESTRATION CONTRACT (Unit Rig)"
LAYER_LIVE_DECLARED = "LIVE SERVER / NETWORK TRACE (declared by input)"
LAYER_UNKNOWN = "UNKNOWN — input declares no evidence layer"

SCOPE_SYNTHETIC = "synthetic scenario sequence only — NOT a gameplay-loop proof"
SCOPE_LIVE = "input-declared live layer; gameplay-loop proof still requires the loop-completeness checklist"

REQUIRED_MILESTONES = (
    "BORN_IN_WORLD", "STARTER_KIT_VERIFIED", "HOUSING_ZONE_REACHED",
    "PLOT_CLAIMED", "CROPS_CULTIVATED", "TIMBER_HARVESTED",
    "WORKBENCH_REACHED", "MATERIAL_PACK_CRAFTED",
    "PACK_DELIVERED_TO_SITE", "HOMESTEAD_CONSTRUCTED",
)

_LAYER_KEY_CANDIDATES = ("evidence_layer", "EvidenceLayer", "evidenceLayer")


def iter_strings(value: Any) -> Iterable[str]:
    """Yields every string inside arbitrarily nested record structures."""
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from iter_strings(item)
    elif isinstance(value, (list, tuple, set)):
        for item in value:
            yield from iter_strings(item)


def detect_synthetic_markers(records: List[Dict[str, Any]]) -> Dict[str, int]:
    """Counts explicit synthetic markers found in any string field of any record."""
    hits: collections.Counter = collections.Counter()
    for record in records:
        if not isinstance(record, dict):
            continue
        for text in iter_strings(record):
            index = text.find(SYNTHETIC_MARKER)
            if index >= 0:
                token = text[index:].split("]", 1)[0] + "]"
                hits[token] += 1
    return dict(hits)


def declared_evidence_layer(records: List[Dict[str, Any]]) -> Optional[str]:
    """Returns the evidence layer the input declares for itself, if any."""
    for record in records:
        if not isinstance(record, dict):
            continue
        for key in _LAYER_KEY_CANDIDATES:
            value = record.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()
    return None


def parse_utc(value: Any) -> Optional[datetime.datetime]:
    if not isinstance(value, str) or not value:
        return None
    try:
        return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def is_authoritative_completion(record: Dict[str, Any]) -> bool:
    """A milestone counts only on a Completed request with a terminal Completed transition."""
    if not isinstance(record, dict) or record.get("result") != "Completed":
        return False
    changes = record.get("state_changes")
    if not isinstance(changes, list) or not changes:
        return False
    return str(changes[-1]).startswith("Completed")


def assess_run_integrity(trace: List[Dict[str, Any]]) -> List[str]:
    """Violations that make a run unusable as gameplay proof."""
    violations: List[str] = []

    seen: set = set()
    duplicates: List[str] = []
    for record in trace:
        if not isinstance(record, dict):
            continue
        trace_id = record.get("trace_id")
        if trace_id:
            if trace_id in seen and trace_id not in duplicates:
                duplicates.append(str(trace_id))
            seen.add(trace_id)
    if duplicates:
        violations.append(f"duplicate trace_id(s): {len(duplicates)} (e.g. {duplicates[0]})")

    non_terminal: collections.Counter = collections.Counter()
    for record in trace:
        if isinstance(record, dict) and not is_authoritative_completion(record):
            non_terminal[str(record.get("result"))] += 1
    for result, count in sorted(non_terminal.items()):
        violations.append(f"non-terminal/failed request result `{result}` x{count}")

    regressions = 0
    previous: Optional[datetime.datetime] = None
    for record in trace:
        if not isinstance(record, dict):
            continue
        stamp = parse_utc(record.get("requested_at"))
        if stamp is None:
            continue
        if previous is not None and stamp < previous:
            regressions += 1
        previous = stamp
    if regressions:
        violations.append(f"out-of-order/interleaved record timestamps x{regressions}")

    malformed = sum(1 for record in trace if not isinstance(record, dict))
    if malformed:
        violations.append(f"malformed (non-object) records x{malformed}")

    actor_ids = sorted({
        int(record["actor_id"]) for record in trace
        if isinstance(record, dict) and isinstance(record.get("actor_id"), int)
    })
    if len(actor_ids) > 1:
        violations.append(
            f"interleaved actors in the trace: {len(actor_ids)} distinct actor_id values "
            f"{actor_ids[:10]}"
        )

    return violations


def sha256_file(path: str) -> str:
    try:
        digest = hashlib.sha256()
        with open(path, "rb") as handle:
            for chunk in iter(lambda: handle.read(1 << 20), b""):
                digest.update(chunk)
        return digest.hexdigest()
    except OSError:
        return UNKNOWN


def git_capture(args: List[str], cwd: str) -> Optional[str]:
    try:
        proc = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True, timeout=15
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if proc.returncode != 0:
        return None
    return proc.stdout


def collect_provenance(
    trace_file: str,
    script_path: str,
    producing_command: str,
) -> Dict[str, Any]:
    """Provenance for the report; anything that cannot be established is UNKNOWN."""
    repo_dir = os.path.dirname(os.path.abspath(script_path))

    head_raw = git_capture(["rev-parse", "HEAD"], repo_dir)
    source_head = head_raw.strip() if head_raw and head_raw.strip() else UNKNOWN

    rel_script = os.path.relpath(os.path.abspath(script_path), repo_dir)
    revision_raw = git_capture(["log", "-1", "--format=%H", "--", rel_script], repo_dir)
    evaluator_revision = revision_raw.strip() if revision_raw and revision_raw.strip() else UNKNOWN

    status_raw = git_capture(["status", "--porcelain"], repo_dir)
    if status_raw is None:
        source_dirty: Any = UNKNOWN
        dirty_files: List[str] = []
    else:
        dirty_files = [line for line in status_raw.splitlines() if line.strip()]
        source_dirty = bool(dirty_files)

    try:
        mtime = os.path.getmtime(trace_file)
        input_mtime = datetime.datetime.fromtimestamp(
            mtime, tz=datetime.timezone.utc
        ).strftime("%Y-%m-%dT%H:%M:%SZ")
    except OSError:
        input_mtime = UNKNOWN

    input_hash = sha256_file(trace_file)
    if input_hash == UNKNOWN or input_mtime == UNKNOWN or source_head == UNKNOWN \
            or evaluator_revision == UNKNOWN or isinstance(source_dirty, str):
        provenance_status = "UNKNOWN — provenance could not be fully established"
    else:
        provenance_status = "RECORDED"

    return {
        "input_path": trace_file,
        "input_sha256": input_hash,
        "input_mtime_utc": input_mtime,
        "producing_command": producing_command,
        "evaluator_script": rel_script,
        "evaluator_revision_git_sha": evaluator_revision,
        "evaluator_sha256": sha256_file(script_path),
        "source_head": source_head,
        "source_dirty": source_dirty,
        "source_dirty_files": dirty_files if isinstance(source_dirty, bool) and source_dirty else [],
        "provenance_status": provenance_status,
    }


def trace_time_range(records: List[Dict[str, Any]]) -> Dict[str, str]:
    stamps = [
        stamp
        for record in records
        if isinstance(record, dict)
        for stamp in (
            parse_utc(record.get("requested_at")),
            parse_utc(record.get("completed_at")),
        )
        if stamp is not None
    ]
    if not stamps:
        return {"start": UNKNOWN, "end": UNKNOWN}
    return {
        "start": min(stamps).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "end": max(stamps).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }


def infer_race_and_zone(x: float, y: float) -> Tuple[str, str]:
    """Infers racial identity and starting zone based on continental ArcheAge coordinates."""
    if 13000 <= x <= 15500 and 14000 <= y <= 16000:
        return "Nuian", "Solzreed Peninsula (Nuia)"
    elif 16000 <= x <= 18000 and 12500 <= y <= 14500:
        return "Elf", "Gweonid Forest (Nuia)"
    elif 20500 <= x <= 22500 and 6500 <= y <= 8000:
        return "Harani (Hariharan)", "Arcum Iris (Haranya)"
    elif 22500 <= x <= 24500 and 8000 <= y <= 9500:
        return "Firran (Ferre)", "Falcon Plateau (Haranya)"
    else:
        return "Unknown", f"Unmapped Zone ({x:.0f}, {y:.0f})"


def parse_coords(text: str) -> Optional[Tuple[float, float, float]]:
    m = re.search(r'\(([\d\.\-]+),\s*([\d\.\-]+),\s*([\d\.\-]+)\)', text)
    if m:
        return float(m.group(1)), float(m.group(2)), float(m.group(3))
    return None


def run_inference(
    trace_file: str,
    out_dir: str = "playertrace-coverage",
    producing_command: Optional[str] = None,
) -> Dict[str, Any]:
    if producing_command is None:
        producing_command = " ".join(
            shlex.quote(part) for part in [sys.executable or "python3", os.path.abspath(__file__),
                                           os.path.abspath(trace_file), "--out-dir", os.path.abspath(out_dir)]
        )
    print(f"============================================================")
    print(f" AAEmu PlayerTrace Autonomous Intent Inference Engine")
    print(f" Ingesting Trace: {trace_file}")
    print(f"============================================================")

    if not os.path.exists(trace_file):
        print(f"Error: Trace file '{trace_file}' not found.")
        sys.exit(1)

    records = []
    with open(trace_file, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                records.append(json.loads(line))

    print(f"Loaded {len(records)} audit records.")

    # Group records into bot runs by detecting spawn/observe events
    bot_traces: List[List[Dict[str, Any]]] = []
    current_run: List[Dict[str, Any]] = []

    for r in records:
        if not isinstance(r, dict):
            current_run.append(r)
            continue
        # A new bot run starts with an Observe spawn record
        if r.get("action") == "Observe" and "Spawned at" in (r.get("detail") or ""):
            if current_run:
                bot_traces.append(current_run)
            current_run = [r]
        else:
            current_run.append(r)
    if current_run:
        bot_traces.append(current_run)

    # Provenance / evidence layer: the input declares its own layer; the evaluator
    # never upgrades it. No marker and no declared layer => UNKNOWN, not "live".
    synthetic_markers = detect_synthetic_markers(records)
    is_synthetic = bool(synthetic_markers)
    declared_layer = declared_evidence_layer(records)
    if is_synthetic:
        evidence_layer = LAYER_SYNTHETIC
    elif declared_layer:
        evidence_layer = declared_layer
    else:
        evidence_layer = LAYER_UNKNOWN
        declared_layer = UNKNOWN
    evidence_scope = SCOPE_SYNTHETIC if is_synthetic else SCOPE_LIVE
    trace_range = trace_time_range(records)

    run_integrity = assess_run_integrity(records)
    if run_integrity:
        print("Run integrity violations (gameplay-proof blocking):")
        for violation in run_integrity:
            print(f"  - {violation}")

    print(f"Identified {len(bot_traces)} distinct bot progression runs.")
    print(f"Evidence layer: {evidence_layer}\n")

    inferred_bots = []
    total_plots_claimed = 0
    total_homes_built = 0
    total_lumber_crafted = 0
    total_transit_meters = 0.0

    for idx, trace in enumerate(bot_traces, 1):
        bot_summary: Dict[str, Any] = {
            "bot_index": idx,
            "record_count": len(trace),
            "inferred_goals": [],
            "milestones_detected": [],
            "resource_ledger": {
                "scarecrow_designs": 0,
                "tax_certificates": 0,
                "saplings_planted": 0,
                "timber_harvested": 0,
                "packs_crafted": 0,
                "homes_constructed": 0
            },
            "locations": {},
            "confidence_score": 1.0,
            "verdict": "UNKNOWN",
            "actor_ids": [],
            "integrity": {"duplicate_trace_ids": [], "non_terminal_results": {}, "timestamp_regressions": 0},
            "integrity_violations": [],
            "distinct_actors_claim": UNKNOWN,
        }

        spawn_rec = trace[0]
        spawn_detail = spawn_rec.get("detail", "") if isinstance(spawn_rec, dict) else ""
        start_coords = parse_coords(spawn_detail)
        if start_coords:
            race, zone = infer_race_and_zone(start_coords[0], start_coords[1])
            bot_summary["inferred_race"] = race
            bot_summary["inferred_starting_zone"] = zone
            bot_summary["locations"]["start"] = start_coords
            bot_summary["milestones_detected"].append("BORN_IN_WORLD")

        # A milestone is credited only on an authoritative completion of a request whose
        # recorded detail matches. Failure text, running/interrupted states and duplicate
        # ids never contribute.
        for r in trace:
            if not is_authoritative_completion(r):
                continue
            act = r.get("action")
            target_id = r.get("target_id")
            detail = r.get("detail", "")

            # Starter kit seeding / acquisition
            if "starter homestead kit" in detail or target_id == 15596:
                bot_summary["resource_ledger"]["scarecrow_designs"] += 1
                bot_summary["resource_ledger"]["tax_certificates"] += 10
                bot_summary["milestones_detected"].append("STARTER_KIT_VERIFIED")

            # Transit to residential zone
            if act == "Move" and "residential zone" in detail:
                m = re.search(r'Navigated ([\d\.]+)m', detail)
                if m:
                    dist = float(m.group(1))
                    total_transit_meters += dist
                    bot_summary["locations"]["transit_to_zone_dist_m"] = dist
                bot_summary["milestones_detected"].append("HOUSING_ZONE_REACHED")

            # Plot claim
            if "claimed 8x8 scarecrow garden plot" in detail or "Erected Straw Hat Scarecrow Garden" in detail:
                house_match = re.search(r'Plot ID (\d+)', detail)
                house_id = int(house_match.group(1)) if house_match else None
                bot_summary["locations"]["plot_id"] = house_id
                total_plots_claimed += 1
                bot_summary["milestones_detected"].append("PLOT_CLAIMED")

            # Cultivation
            if "Cultivated" in detail or "Planted" in detail:
                bot_summary["resource_ledger"]["saplings_planted"] += 1
                bot_summary["milestones_detected"].append("CROPS_CULTIVATED")

            # Harvest
            if "Harvested timber" in detail or "Chopped mature trees" in detail:
                bot_summary["resource_ledger"]["timber_harvested"] += 1
                bot_summary["milestones_detected"].append("TIMBER_HARVESTED")

            # Workbench transit
            if act == "Move" and "workbench" in detail:
                m = re.search(r'Navigated ([\d\.]+)m', detail)
                if m:
                    dist = float(m.group(1))
                    total_transit_meters += dist
                bot_summary["milestones_detected"].append("WORKBENCH_REACHED")

            # Craft pack
            if act == "Craft" or "Crafted" in detail and "lumber pack" in detail:
                bot_summary["resource_ledger"]["packs_crafted"] += 1
                total_lumber_crafted += 1
                bot_summary["milestones_detected"].append("MATERIAL_PACK_CRAFTED")

            # Pack transport
            if act == "Move" and "Transported lumber pack" in detail:
                m = re.search(r'Transported lumber pack ([\d\.]+)m', detail)
                if m:
                    dist = float(m.group(1))
                    total_transit_meters += dist
                bot_summary["milestones_detected"].append("PACK_DELIVERED_TO_SITE")

            # Construction completion
            if "complete farm structure" in detail or "Applied lumber pack" in detail:
                bot_summary["resource_ledger"]["homes_constructed"] += 1
                total_homes_built += 1
                bot_summary["milestones_detected"].append("HOMESTEAD_CONSTRUCTED")

        # Actor identity: the trace schema carries actor_id; a ten-bot scale claim is only
        # supported when the records actually identify ten distinct actors.
        # The milestone log is de-duplicated (order preserved) so a repeated event can never
        # inflate a bot's milestone count past the requirement total.
        bot_summary["milestones_detected"] = list(dict.fromkeys(bot_summary["milestones_detected"]))

        actor_ids = sorted({
            int(r["actor_id"]) for r in trace
            if isinstance(r, dict) and isinstance(r.get("actor_id"), int)
        })
        bot_summary["actor_ids"] = actor_ids
        bot_summary["distinct_actors_claim"] = (
            "supported" if len(actor_ids) > 1 else f"UNSUPPORTED — all records carry actor_id {actor_ids or UNKNOWN}"
        )
        # Per-run integrity: duplicates, non-terminal results, timestamp order.
        duplicates: List[str] = []
        seen_ids: set = set()
        non_terminal: collections.Counter = collections.Counter()
        for r in trace:
            if not isinstance(r, dict):
                non_terminal["malformed-record"] += 1
                continue
            trace_id = r.get("trace_id")
            if trace_id:
                if trace_id in seen_ids and trace_id not in duplicates:
                    duplicates.append(str(trace_id))
                seen_ids.add(trace_id)
            if not is_authoritative_completion(r):
                non_terminal[str(r.get("result"))] += 1
        regressions = 0
        previous: Optional[datetime.datetime] = None
        for r in trace:
            if not isinstance(r, dict):
                continue
            stamp = parse_utc(r.get("requested_at"))
            if stamp is None:
                continue
            if previous is not None and stamp < previous:
                regressions += 1
            previous = stamp
        bot_summary["integrity"] = {
            "duplicate_trace_ids": duplicates,
            "non_terminal_results": dict(non_terminal),
            "timestamp_regressions": regressions,
        }
        violations: List[str] = []
        if len(actor_ids) > 1:
            violations.append(
                f"interleaved actors in one run: {len(actor_ids)} distinct actor_id values "
                f"{actor_ids} — a progression run must belong to a single actor"
            )
        if duplicates:
            violations.append(f"duplicate trace_id(s): {len(duplicates)}")
        if non_terminal:
            violations.append("non-terminal/failed request(s): " + ", ".join(
                f"{k} x{v}" for k, v in sorted(non_terminal.items())))
        if regressions:
            violations.append(f"out-of-order/interleaved record timestamps x{regressions}")
        bot_summary["integrity_violations"] = violations

        # Goal inference from observed milestone chain
        if "PLOT_CLAIMED" in bot_summary["milestones_detected"]:
            bot_summary["inferred_goals"].append({
                "goal": "GoalClaimHomestead",
                "confidence": 1.0,
                "evidence": "Observed Scarecrow Garden design usage and land plot boundary registration (heuristic text match)"
            })
        if "HOMESTEAD_CONSTRUCTED" in bot_summary["milestones_detected"]:
            bot_summary["inferred_goals"].append({
                "goal": "GoalErectHome",
                "confidence": 1.0,
                "evidence": "Observed lumber pack craft at workbench and application to home frame (heuristic text match)"
            })

        observed_set = set(bot_summary["milestones_detected"])
        missing = set(REQUIRED_MILESTONES) - observed_set

        if missing:
            if "PLOT_CLAIMED" in observed_set:
                bot_summary["verdict"] = "PARTIAL_PROGRESSION"
                bot_summary["confidence_score"] = len(observed_set) / len(REQUIRED_MILESTONES)
            else:
                bot_summary["verdict"] = "FAIL"
                bot_summary["confidence_score"] = 0.0
        elif violations:
            # Full sequence, but the run cannot be consumed as gameplay proof.
            bot_summary["verdict"] = "SYNTHETIC PASS (INTEGRITY VIOLATIONS)" if is_synthetic else "INTEGRITY VIOLATIONS"
            bot_summary["confidence_score"] = len(observed_set) / len(REQUIRED_MILESTONES)
        elif is_synthetic:
            bot_summary["verdict"] = "SYNTHETIC PASS"
        elif evidence_layer == LAYER_UNKNOWN:
            # No marker and no declared layer: the sequence was reproduced, but the
            # evaluator refuses to emit a live verdict for an undeclared input.
            bot_summary["verdict"] = "SEQUENCE REPRODUCED (LAYER UNKNOWN)"
        else:
            # The input declares a live/server layer. Gameplay-loop closure is still a
            # separate question (loop-completeness checklist), so the verdict names the
            # declared layer instead of a bare, unqualified "PASS".
            bot_summary["verdict"] = "PASS (INPUT-DECLARED LIVE LAYER)"

        inferred_bots.append(bot_summary)

    # Compile executive report
    os.makedirs(out_dir, exist_ok=True)
    json_path = os.path.join(out_dir, "homestead_10bot_inference_report.json")
    md_path = os.path.join(out_dir, "homestead_10bot_inference_report.md")

    provenance = collect_provenance(trace_file, __file__, producing_command)
    provenance["evidence_layer"] = evidence_layer
    provenance["declared_evidence_layer"] = declared_layer if declared_layer else UNKNOWN
    provenance["is_synthetic"] = is_synthetic
    provenance["synthetic_markers"] = synthetic_markers
    provenance["trace_time_range_utc"] = trace_range

    synthetic_pass_count = sum(1 for b in inferred_bots if b["verdict"] == "SYNTHETIC PASS")
    live_pass_count = sum(1 for b in inferred_bots if b["verdict"] == "PASS (INPUT-DECLARED LIVE LAYER)")
    partial_count = sum(1 for b in inferred_bots if b["verdict"] == "PARTIAL_PROGRESSION")
    fail_count = sum(1 for b in inferred_bots if b["verdict"] == "FAIL")
    integrity_flagged = sum(1 for b in inferred_bots if b["integrity_violations"])
    fully_sequence_complete = sum(
        1 for b in inferred_bots if not (set(REQUIRED_MILESTONES) - set(b["milestones_detected"]))
    )

    if fully_sequence_complete != len(inferred_bots):
        overall_verdict = "FAIL"
    elif is_synthetic:
        overall_verdict = "SYNTHETIC PASS" if not run_integrity else "SYNTHETIC PASS (INTEGRITY VIOLATIONS)"
    elif evidence_layer == LAYER_UNKNOWN:
        # No declared layer: the sequence was reproduced, but the input's evidence layer
        # is unknown, so the evaluator refuses to emit a live verdict.
        overall_verdict = "UNKNOWN — input declares no evidence layer; sequence reproduced only"
    else:
        overall_verdict = "PASS (INPUT-DECLARED LIVE LAYER)"

    synthetic_verdict = overall_verdict if is_synthetic else "N/A — input declares no synthetic marker"
    if is_synthetic:
        live_verdict = "N/A — synthetic input can never earn a live verdict"
    elif evidence_layer == LAYER_UNKNOWN:
        live_verdict = "UNKNOWN — input declares no evidence layer"
    else:
        live_verdict = overall_verdict
    gameplay_loop_proof = "UNPROVED (synthetic orchestration contract only)" if is_synthetic else (
        "UNKNOWN — evaluator text matching is heuristic discovery, not a loop-completeness proof"
    )

    sorted_actor_ids = sorted({
        int(r["actor_id"]) for r in records
        if isinstance(r, dict) and isinstance(r.get("actor_id"), int)
    })
    if len(sorted_actor_ids) <= 1:
        multi_bot_scale_claim = (
            f"UNSUPPORTED — trace carries {len(sorted_actor_ids)} distinct actor_id value(s); "
            "run grouping is by spawn events, not actor identity"
        )
    else:
        multi_bot_scale_claim = (
            f"supported — records carry {len(sorted_actor_ids)} distinct actor_id values"
        )

    summary_data = {
        "timestamp": provenance["input_mtime_utc"],
        "timestamp_basis": "input artifact mtime (UTC)" if provenance["input_mtime_utc"] != UNKNOWN
        else "UNKNOWN — input mtime unavailable",
        "source_trace": trace_file,
        "evidence_layer": evidence_layer,
        "evidence_scope": evidence_scope,
        "is_synthetic": is_synthetic,
        "synthetic_markers": synthetic_markers,
        "declared_evidence_layer": declared_layer if declared_layer else UNKNOWN,
        "provenance": provenance,
        "provenance_status": provenance["provenance_status"],
        "overall_verdict": overall_verdict,
        "synthetic_verdict": synthetic_verdict,
        "live_verdict": live_verdict,
        "gameplay_loop_proof": gameplay_loop_proof,
        "run_integrity_violations": run_integrity,
        "integrity_flagged_bots": integrity_flagged,
        "evaluation_method": "heuristic text matching over recorded audit fields; not resource conservation and not verified intent",
        "total_bots_evaluated": len(inferred_bots),
        "distinct_actors_in_trace": sorted_actor_ids,
        "multi_bot_scale_claim": multi_bot_scale_claim,
        "metrics": {
            "sequence_complete_bots": fully_sequence_complete,
            "synthetic_pass_bots": synthetic_pass_count,
            "live_pass_bots": live_pass_count,
            "partial_progression_bots": partial_count,
            "failed_bots": fail_count,
            "total_plots_claimed": total_plots_claimed,
            "total_homes_constructed": total_homes_built,
            "total_lumber_packs_crafted": total_lumber_crafted,
            "total_transit_distance_meters": round(total_transit_meters, 1)
        },
        "bots": inferred_bots
    }

    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(summary_data, f, indent=2)

    # Generate Markdown Report
    md = []
    md.append("# AAEmu PlayerTrace Homestead Intent & Milestone Inference Report")
    md.append("")
    md.append(f"> **Evidence Layer:** `{evidence_layer}`")
    md.append(f"> **Synthetic Verdict:** **{synthetic_verdict}** (synthetic scenario sequence only)")
    md.append(f"> **Live Verdict:** **{live_verdict}**")
    md.append(f"> **Gameplay Loop Proof:** **{gameplay_loop_proof}**")
    md.append(f"> **Evaluator:** `{provenance['evaluator_script']}` @ `{provenance['evaluator_revision_git_sha']}` (sha256 `{provenance['evaluator_sha256']}`)")
    md.append(f"> **Input Trace:** `{trace_file}` ({len(records)} audit records)")
    md.append(f"> **Input SHA-256:** `{provenance['input_sha256']}`")
    md.append(f"> **Input Modified (UTC):** `{provenance['input_mtime_utc']}`")
    md.append(f"> **Trace Window (UTC):** `{trace_range['start']}` → `{trace_range['end']}`")
    md.append(f"> **Producing Command:** `{provenance['producing_command']}`")
    md.append(f"> **Source HEAD:** `{provenance['source_head']}` · dirty: {provenance['source_dirty']}"
              + (f" ({len(provenance['source_dirty_files'])} files)" if provenance["source_dirty_files"] else ""))
    md.append(f"> **Provenance Status:** {provenance['provenance_status']}")
    md.append(f"> **Evaluation Method:** {summary_data['evaluation_method']}.")
    md.append(f"> **Multi-Bot Scale Claim:** {multi_bot_scale_claim}.")
    if is_synthetic:
        md.append("> **Honesty Notice (AGENTS.md):** Trace contains explicit synthetic harness/fixture "
                  "markers. This report validates the rig's synthetic orchestration sequence and actor "
                  "contract ONLY. It does NOT prove live server, network, or human client gameplay "
                  "(Live=UNKNOWN, H=UNKNOWN). A fully synthetic successful trace can never earn a live verdict.")
        md.append("> **Seeded / bypassed steps:** fake actor, manual position changes, manual request "
                  "completion, state/material/ownership/construction overrides, and injected starter kit.")
        md.append("> **Superseded claim (2026-09-17):** an earlier revision of this report and of its "
                  "generator labelled this same synthetic input `LIVE SERVER / NETWORK TRACE` and a bare "
                  "`PASS (10/10 bots fully verified)` because the detector read non-existent "
                  "`Payload`/`Target` fields instead of the trace's real `detail` field. That claim is "
                  "withdrawn here; SCORECARD.md's 2026-09-16 evidence correction notice already rejects "
                  "it. The historical text remains in git history. No gameplay or milestone evidence is "
                  "promoted by this report.")
    if run_integrity:
        md.append("> **Run Integrity Violations (block gameplay-proof use):**")
        for violation in run_integrity:
            md.append(f">   - {violation}")
    md.append("")
    md.append("## Macro Telemetry Summary")
    md.append("")
    md.append(f"- **Total Starter Bots Evaluated:** {len(inferred_bots)}")
    md.append(f"- **Sequence-Complete Bots:** {fully_sequence_complete}")
    md.append(f"- **Synthetic-Pass Bots:** {synthetic_pass_count}")
    md.append(f"- **Live-Pass Bots:** {live_pass_count}")
    md.append(f"- **Residential Plots Claimed:** {total_plots_claimed}")
    md.append(f"- **Farmsteads Constructed:** {total_homes_built}")
    md.append(f"- **Material Packs Crafted:** {total_lumber_crafted}")
    md.append(f"- **Cumulative Transit Distance:** {total_transit_meters:.1f} meters")
    md.append("")
    md.append("## Inferred Bot Progression Matrix")
    md.append("")
    md.append(f"| Bot # | Inferred Race | Starting Hub | Plot ID | Milestones | Distinct Actors | Inferred Goals | Verdict |")
    md.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for b in inferred_bots:
        goals_str = ", ".join(g["goal"] for g in b["inferred_goals"])
        plot_id = b["locations"].get("plot_id", "N/A")
        m_count = f"{len(b['milestones_detected'])}/{len(REQUIRED_MILESTONES)}"
        md.append(f"| **Bot {b['bot_index']:02d}** | {b.get('inferred_race', 'Unknown')} | {b.get('inferred_starting_zone', 'Unknown')} | `{plot_id}` | {m_count} | {len(b['actor_ids'])} | {goals_str} | **{b['verdict']}** |")

    md.append("")
    md.append("## Detailed Milestone Inference Breakdown")
    md.append("")

    for b in inferred_bots:
        md.append(f"### Bot #{b['bot_index']:02d} ({b.get('inferred_race', 'Unknown')})")
        md.append(f"- **Starting Origin:** {b.get('inferred_starting_zone', 'Unknown')}")
        md.append(f"- **Plot Claimed:** House ID `{b['locations'].get('plot_id')}`")
        md.append(f"- **Distinct Actors In Run:** {len(b['actor_ids'])} (actor_id {b['actor_ids'] or UNKNOWN}) — distinct-actor claim: {b['distinct_actors_claim']}")
        md.append(f"- **Run Integrity:** {'; '.join(b['integrity_violations']) if b['integrity_violations'] else 'no duplicate ids, no non-terminal requests, timestamps ordered'}")
        md.append(f"- **Verdict:** **{b['verdict']}**")
        md.append("- **Resource Conversions (heuristic text matches, not conservation):**")
        for k, v in b["resource_ledger"].items():
            md.append(f"  - {k.replace('_', ' ').title()}: **{v}**")
        md.append("- **Detected Sequence Flow:**")
        for m in b["milestones_detected"]:
            md.append(f"  - `[OK]` {m}")
        md.append("")

    with open(md_path, "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")

    print(f"[Done] Emitted inference results:")
    print(f"  - JSON Report: {json_path}")
    print(f"  - Markdown Report: {md_path}")
    print(f"\nInference Verdict: {overall_verdict} ({fully_sequence_complete}/{len(inferred_bots)} sequence-complete)")
    return summary_data


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Infer player bot goals and milestones from traces.")
    parser.add_argument("trace_file", help="Path to audit record JSONL file")
    parser.add_argument("--out-dir", default="playertrace-coverage", help="Output directory for reports")
    args = parser.parse_args()

    # Record exactly how this report was produced (provenance field).
    producing_command = " ".join(shlex.quote(part) for part in ["python3", *sys.argv])
    run_inference(args.trace_file, args.out_dir, producing_command=producing_command)
