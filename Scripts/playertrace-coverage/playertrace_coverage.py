#!/usr/bin/env python3
"""
playertrace_coverage.py - AAEmu PlayerTrace Coverage Mapper CLI.

Analyzes human action trace files against known AAEmu packet classes, computes
coverage metrics, derives normalized action sequences, generates candidate action
families with conservative bounds, and recommends next human traces.

Usage:
    python3 Scripts/playertrace-coverage/playertrace_coverage.py [trace_dir_or_files...] [options]
"""

import argparse
import csv
from dataclasses import asdict
import datetime
import json
import os
import subprocess
import sys
from typing import Any, Dict, List, Optional, Set, Tuple

from coverage_engine import (
    CandidateFamily,
    CorpusSummary,
    CoverageEngine,
    CoverageGap,
    EventCoverageItem,
    NextTraceRecommendation,
    PacketCoverageItem,
    ScenarioSimilarityItem,
)
from packet_inventory import PacketInventory
from trace_parser import discover_trace_files, stream_trace_file


def get_git_info(repo_root: str) -> Tuple[Optional[str], Optional[str]]:
    """Query git branch and commit SHA if available."""
    try:
        sha = subprocess.check_output(
            ["git", "rev-parse", "HEAD"],
            cwd=repo_root,
            stderr=subprocess.DEVNULL,
            text=True
        ).strip()
        branch = subprocess.check_output(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=repo_root,
            stderr=subprocess.DEVNULL,
            text=True
        ).strip()
        return branch, sha
    except Exception:
        return None, None


def write_csv(file_path: str, fieldnames: List[str], rows: List[Dict[str, Any]]) -> None:
    """Write rows to a CSV file deterministically."""
    os.makedirs(os.path.dirname(os.path.abspath(file_path)), exist_ok=True)
    with open(file_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for r in rows:
            writer.writerow(r)


def write_scenario_matrix_csv(
    file_path: str,
    scenarios: List[str],
    features: List[str],
    matrix: List[List[int]]
) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(file_path)), exist_ok=True)
    with open(file_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["scenario"] + features)
        for sc, row in zip(scenarios, matrix):
            writer.writerow([sc] + row)


def compute_baseline_delta(
    current_summary: CorpusSummary,
    current_packets: List[PacketCoverageItem],
    baseline_path: str
) -> Dict[str, Any]:
    """Compare current coverage results against a baseline summary.json."""
    if not os.path.exists(baseline_path):
        return {"error": f"Baseline file not found: {baseline_path}"}

    try:
        with open(baseline_path, "r", encoding="utf-8") as f:
            base_data = json.load(f)
    except Exception as ex:
        return {"error": f"Failed to parse baseline: {ex}"}

    base_corpus = base_data.get("corpus_summary", {})
    base_scenarios = set(base_corpus.get("scenarios", []))
    curr_scenarios = set(current_summary.scenarios)
    new_scenarios = sorted(curr_scenarios - base_scenarios)

    base_pkts = set()
    for p in base_data.get("packet_coverage", {}).get("items", []):
        if p.get("observed"):
            base_pkts.add(p.get("packet"))

    curr_observed_pkts = {p.packet for p in current_packets if p.observed}
    newly_observed_pkts = sorted(curr_observed_pkts - base_pkts)

    records_delta = current_summary.total_records - base_corpus.get("total_records", 0)

    return {
        "baseline_file": baseline_path,
        "new_scenarios": new_scenarios,
        "newly_observed_packets": newly_observed_pkts,
        "record_count_delta": records_delta,
        "scenarios_count_delta": len(curr_scenarios) - len(base_scenarios),
    }


def generate_markdown_report(
    summary: CorpusSummary,
    packet_items: List[PacketCoverageItem],
    event_items: List[EventCoverageItem],
    transitions: List[Dict[str, Any]],
    patterns: List[Dict[str, Any]],
    similarities: List[ScenarioSimilarityItem],
    families: List[CandidateFamily],
    gaps: List[CoverageGap],
    recommendations: List[NextTraceRecommendation],
    delta: Optional[Dict[str, Any]] = None,
    git_branch: Optional[str] = None,
    git_sha: Optional[str] = None
) -> str:
    lines = []
    lines.append("# AAEmu PlayerTrace Coverage Report")
    lines.append("")
    lines.append(f"> **Generated:** {datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')}")
    if git_branch and git_sha:
        lines.append(f"> **Repository State:** `{git_branch}` @ `{git_sha[:10]}`")
    lines.append("")

    # 1. Corpus Summary
    lines.append("## 1. Corpus Summary")
    lines.append("")
    lines.append("| Metric | Count |")
    lines.append("| --- | --- |")
    lines.append(f"| **Trace Files Analyzed** | {summary.total_files} |")
    lines.append(f"| **Total Records Parsed** | {summary.total_records:,} |")
    lines.append(f"| **Malformed Records Skipped** | {summary.malformed_records} |")
    lines.append(f"| **Distinct Scenarios Observed** | {len(summary.scenarios)} |")
    lines.append(f"| **Packet Records (in + out)** | {summary.packet_records:,} |")
    lines.append(f"| **Movement Records** | {summary.movement_records:,} |")
    lines.append(f"| **Semantic Event Records** | {summary.event_records:,} |")
    lines.append(f"| **Marker Records** | {summary.marker_records} |")
    lines.append(f"| **Unique Observed Packets** | {summary.unique_packets} (C2S: {summary.unique_c2s_packets}, S2C: {summary.unique_s2c_packets}) |")
    lines.append(f"| **Unique Trace Event Types** | {summary.unique_events} |")
    if summary.time_span_seconds:
        lines.append(f"| **Corpus Time Span** | {summary.time_span_seconds:.1f} seconds |")
    lines.append("")
    lines.append(f"**Scenarios in Corpus:** `{', '.join(summary.scenarios)}`")
    lines.append("")

    # 2. Packet Coverage
    observed_pkts = [p for p in packet_items if p.observed]
    leads = [p for p in packet_items if not p.observed and p.status == "DISCOVERY_LEAD"]
    unmapped = [p for p in packet_items if p.status == "OBSERVED_UNMAPPED"]

    lines.append("## 2. Packet Coverage Overview")
    lines.append("")
    lines.append(f"- **Observed & Mapped to AAEmu Source:** {len(observed_pkts) - len(unmapped)} packet classes")
    lines.append(f"- **Observed Unmapped Packets (Schema Drift/Debug):** {len(unmapped)}")
    lines.append(f"- **Known AAEmu Packets Unobserved (Discovery Leads):** {len(leads)} classes")
    lines.append("")
    lines.append("### Top Observed Packets")
    lines.append("")
    lines.append("| Packet | Direction | Opcode | Count | Scenarios | Source Path |")
    lines.append("| --- | --- | --- | --- | --- | --- |")
    for p in observed_pkts[:15]:
        op = p.opcode or "-"
        lines.append(f"| `{p.packet}` | {p.direction} | `{op}` | {p.observed_count:,} | {p.scenarios_seen} | `{p.source_path}` |")
    lines.append("")

    # 3. Event Coverage
    lines.append("## 3. Semantic Event Coverage")
    lines.append("")
    lines.append("| Event Category & Name | Category | Count | Scenarios |")
    lines.append("| --- | --- | --- | --- |")
    for ev in event_items[:15]:
        lines.append(f"| `{ev.event_key}` | {ev.category} | {ev.count:,} | {ev.scenarios_seen} |")
    lines.append("")

    # 4. Action Sequences & Transitions
    lines.append("## 4. Most Common Action Sequences & Skeletons")
    lines.append("")
    lines.append("*(High-frequency movement updates and keepalive pings normalized/collapsed)*")
    lines.append("")
    lines.append("### Top 2-Gram Transitions")
    lines.append("")
    lines.append("| Transition | Count | Scenarios Seen |")
    lines.append("| --- | --- | --- |")
    for t in transitions[:10]:
        lines.append(f"| `{t['from_token']}` &rarr; `{t['to_token']}` | {t['count']} | `{t['scenarios']}` |")
    lines.append("")
    lines.append("### Top 3-Gram Action Skeletons")
    lines.append("")
    lines.append("| Sequence Pattern | Count | Scenarios Seen |")
    lines.append("| --- | --- | --- |")
    for pat in [p for p in patterns if p['pattern_type'] == '3-gram'][:10]:
        lines.append(f"| `{pat['sequence']}` | {pat['count']} | `{pat['scenarios']}` |")
    lines.append("")

    # 5. Candidate Action Families
    lines.append("## 5. Candidate Action Families")
    lines.append("")
    lines.append("> [!CAUTION]")
    lines.append("> **CORRECTNESS RULE**: Packet and event similarity provides structural evidence, NOT proof of identical server semantics.")
    lines.append("> Candidate families are labeled **POSSIBLE RELATED ACTIONS**. Inspect server code before making architectural assumptions.")
    lines.append("")
    for fam in families:
        lines.append(f"### Family: {fam.family_name} `[Confidence: {fam.confidence}]`")
        lines.append(f"- **Rationale:** {fam.rationale}")
        lines.append(f"- **Member Scenarios:** `{', '.join(fam.member_scenarios)}`")
        lines.append(f"- **Common Packets:** `{', '.join(fam.common_packets) if fam.common_packets else 'None'}`")
        lines.append(f"- **Common Events:** `{', '.join(fam.common_events) if fam.common_events else 'None'}`")
        lines.append("- **Key Differences Among Members:**")
        for sc, diffs in fam.key_differences.items():
            diff_str = ", ".join(diffs[:6]) if diffs else "None (Exact Match)"
            if len(diffs) > 6:
                diff_str += f" (+{len(diffs) - 6} more)"
            lines.append(f"  - `{sc}`: `{diff_str}`")
        lines.append("")

    # 6. Scenario Similarity Matrix
    lines.append("## 6. Scenario Similarity Pairs")
    lines.append("")
    lines.append("| Scenario A | Scenario B | Jaccard Sim | Transition Overlap | Classification |")
    lines.append("| --- | --- | --- | --- | --- |")
    for s in similarities[:15]:
        lines.append(f"| `{s.scenario_a}` | `{s.scenario_b}` | {s.jaccard_similarity:.2f} | {s.transition_overlap:.2f} | {s.label} |")
    lines.append("")

    # 7. Coverage Gaps
    lines.append("## 7. Coverage Gaps & Blind Spots")
    lines.append("")
    for g in gaps:
        lines.append(f"### `[{g.gap_type}]` {g.title}")
        lines.append(f"- **Description:** {g.description}")
        if g.affected_packets:
            lines.append(f"- **Target AAEmu Packets:** `{', '.join(g.affected_packets)}`")
        lines.append(f"- **Suggested Remediation:** {g.suggested_action}")
        lines.append("")

    # 8. Recommended Next Traces
    lines.append("## 8. Recommended Next Human Traces (Ranked)")
    lines.append("")
    lines.append("| Rank | Suggested Scenario | Priority | Target Family | Rationale |")
    lines.append("| --- | --- | --- | --- | --- |")
    for r in recommendations:
        lines.append(f"| **#{r.rank}** | `{r.scenario_name}` | **{r.priority}** | {r.expected_action_family} | {r.rationale} |")
    lines.append("")

    # 9. Baseline Delta if present
    if delta and "error" not in delta:
        lines.append("## 9. Coverage Delta (vs Baseline)")
        lines.append("")
        lines.append(f"- **Baseline Source:** `{delta.get('baseline_file')}`")
        lines.append(f"- **New Scenarios Added:** `{', '.join(delta.get('new_scenarios', [])) or 'None'}`")
        lines.append(f"- **Newly Observed Packets:** `{', '.join(delta.get('newly_observed_packets', [])) or 'None'}`")
        lines.append(f"- **Net Record Count Delta:** `+{delta.get('record_count_delta', 0):,}`")
        lines.append("")

    lines.append("---")
    lines.append("*Report generated by AAEmu PlayerTrace Coverage Mapper v1.0*")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="AAEmu PlayerTrace Coverage Mapper - Analyze human trace corpora against AAEmu packet inventory."
    )
    parser.add_argument(
        "inputs",
        nargs="*",
        default=["traces/player-actions"],
        help="Trace JSONL files or directories containing traces (default: traces/player-actions)"
    )
    parser.add_argument(
        "--repo-root",
        default=".",
        help="Root path of AAEmu repository (default: .)"
    )
    parser.add_argument(
        "--out",
        default="playertrace-coverage",
        help="Output directory for generated reports and CSVs (default: playertrace-coverage)"
    )
    parser.add_argument(
        "--scenario",
        nargs="*",
        help="Filter analysis to specific scenario names"
    )
    parser.add_argument(
        "--similarity-threshold",
        type=float,
        default=0.35,
        help="Jaccard similarity threshold for candidate action families (default: 0.35)"
    )
    parser.add_argument(
        "--min-count",
        type=int,
        default=1,
        help="Minimum occurrence count for transitions and patterns (default: 1)"
    )
    parser.add_argument(
        "--baseline",
        help="Path to previous summary.json for delta comparison"
    )
    parser.add_argument(
        "--no-markdown",
        action="store_true",
        help="Skip markdown summary generation"
    )
    parser.add_argument(
        "--no-json",
        action="store_true",
        help="Skip JSON summary generation"
    )

    args = parser.parse_args()

    repo_root = os.path.abspath(args.repo_root)
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)

    print(f"[PlayerTrace Coverage Mapper] Initializing...")
    print(f"  Repo root: {repo_root}")
    print(f"  Output directory: {out_dir}")

    # 1. Discover packet inventory
    print(f"[1/5] Scanning AAEmu packet classes...")
    inventory = PacketInventory(repo_root)
    print(f"      Discovered {len(inventory.packets)} packet classes.")

    # 2. Discover trace files
    print(f"[2/5] Discovering trace files in: {args.inputs}...")
    trace_files = discover_trace_files(args.inputs)
    if not trace_files:
        print(f"ERROR: No .jsonl trace files found in specified inputs: {args.inputs}", file=sys.stderr)
        return 1
    print(f"      Discovered {len(trace_files)} trace files.")

    # 3. Stream & analyze
    print(f"[3/5] Ingesting and streaming trace records...")
    engine = CoverageEngine(
        inventory=inventory,
        similarity_threshold=args.similarity_threshold,
        min_ngram_count=args.min_count
    )

    engine.total_files = len(trace_files)
    scenario_filters = set(args.scenario) if args.scenario else None

    for tf in trace_files:
        for rec in stream_trace_file(tf, on_malformed=engine.record_malformed):
            if scenario_filters and rec.scenario not in scenario_filters:
                continue
            engine.process_record(rec)

    print(f"      Processed {engine.total_records:,} records ({engine.malformed_records} malformed skipped).")

    # 4. Compute analytics
    print(f"[4/5] Computing coverage matrices, sequences, similarities, and gaps...")
    summary = engine.compute_summary()
    packet_items = engine.compute_packet_coverage()
    event_items = engine.compute_event_coverage()
    scenarios, features, matrix = engine.compute_scenario_matrix()
    transitions, patterns = engine.compute_transitions_and_ngrams()
    cooccurrences = engine.compute_cooccurrence()
    similarities = engine.compute_scenario_similarity()
    families = engine.compute_candidate_families()
    gaps = engine.compute_coverage_gaps()
    recommendations = engine.compute_recommendations()

    delta = None
    if args.baseline:
        print(f"      Comparing against baseline: {args.baseline}...")
        delta = compute_baseline_delta(summary, packet_items, args.baseline)

    # 5. Write outputs
    print(f"[5/5] Writing reports and CSV artifacts to: {out_dir}...")

    # CSV: packet-coverage.csv
    write_csv(
        os.path.join(out_dir, "packet-coverage.csv"),
        fieldnames=["packet", "direction", "observed_count", "scenarios_seen", "observed", "known_to_repo", "source_path", "opcode", "status"],
        rows=[asdict(p) for p in packet_items]
    )

    # CSV: event-coverage.csv
    write_csv(
        os.path.join(out_dir, "event-coverage.csv"),
        fieldnames=["event_key", "category", "event", "count", "scenarios_seen", "first_seen", "last_seen"],
        rows=[asdict(e) for e in event_items]
    )

    # CSV: scenario-matrix.csv
    write_scenario_matrix_csv(
        os.path.join(out_dir, "scenario-matrix.csv"),
        scenarios=scenarios,
        features=features,
        matrix=matrix
    )

    # CSV: packet-transitions.csv
    write_csv(
        os.path.join(out_dir, "packet-transitions.csv"),
        fieldnames=["from_token", "to_token", "count", "scenarios_count", "scenarios"],
        rows=transitions
    )

    # CSV: sequence-patterns.csv
    write_csv(
        os.path.join(out_dir, "sequence-patterns.csv"),
        fieldnames=["pattern_type", "sequence", "count", "scenarios_count", "scenarios"],
        rows=patterns
    )

    # CSV: scenario-similarity.csv
    write_csv(
        os.path.join(out_dir, "scenario-similarity.csv"),
        fieldnames=["scenario_a", "scenario_b", "jaccard_similarity", "transition_overlap", "shared_items_count", "distinct_items_count", "label"],
        rows=[asdict(s) for s in similarities]
    )

    # CSV: cooccurrence.csv
    write_csv(
        os.path.join(out_dir, "cooccurrence.csv"),
        fieldnames=["event_a", "event_b", "shared_scenarios_count", "shared_scenarios", "jaccard_similarity", "total_scenarios_a", "total_scenarios_b"],
        rows=cooccurrences
    )

    # Git metadata
    git_branch, git_sha = get_git_info(repo_root)

    # JSON: summary.json
    if not args.no_json:
        summary_data = {
            "schema_version": 1,
            "generated_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "git": {
                "branch": git_branch,
                "commit": git_sha
            },
            "corpus_summary": asdict(summary),
            "packet_coverage": {
                "total_known_packets": len(inventory.packets),
                "total_observed_packets": summary.unique_packets,
                "items": [asdict(p) for p in packet_items]
            },
            "event_coverage": {
                "total_events": summary.unique_events,
                "items": [asdict(e) for e in event_items]
            },
            "candidate_families": [asdict(f) for f in families],
            "similarities": [asdict(s) for s in similarities],
            "gaps": [asdict(g) for g in gaps],
            "recommendations": [asdict(r) for r in recommendations]
        }
        if delta:
            summary_data["baseline_delta"] = delta

        json_path = os.path.join(out_dir, "summary.json")
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump(summary_data, f, indent=2)

    # Markdown: summary.md
    if not args.no_markdown:
        md_text = generate_markdown_report(
            summary=summary,
            packet_items=packet_items,
            event_items=event_items,
            transitions=transitions,
            patterns=patterns,
            similarities=similarities,
            families=families,
            gaps=gaps,
            recommendations=recommendations,
            delta=delta,
            git_branch=git_branch,
            git_sha=git_sha
        )
        md_path = os.path.join(out_dir, "summary.md")
        with open(md_path, "w", encoding="utf-8") as f:
            f.write(md_text)

    print(f"\n[PlayerTrace Coverage Mapper] Analysis complete!")
    print(f"  Summary Report: {os.path.join(out_dir, 'summary.md')}")
    print(f"  Machine JSON:   {os.path.join(out_dir, 'summary.json')}")
    print(f"  CSV Outputs:    {out_dir}/*.csv")
    return 0


if __name__ == "__main__":
    sys.exit(main())
