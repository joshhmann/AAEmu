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
"""

import argparse
import collections
import json
import math
import os
import re
import sys
from typing import Any, Dict, List, Optional, Tuple


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


def run_inference(trace_file: str, out_dir: str = "playertrace-coverage") -> Dict[str, Any]:
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
        # A new bot run starts with an Observe spawn record
        if r.get("action") == "Observe" and "Spawned at" in (r.get("detail") or ""):
            if current_run:
                bot_traces.append(current_run)
            current_run = [r]
        else:
            current_run.append(r)
    if current_run:
        bot_traces.append(current_run)

    print(f"Identified {len(bot_traces)} distinct bot progression runs.\n")

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
            "verdict": "UNKNOWN"
        }

        spawn_rec = trace[0]
        spawn_detail = spawn_rec.get("detail", "")
        start_coords = parse_coords(spawn_detail)
        if start_coords:
            race, zone = infer_race_and_zone(start_coords[0], start_coords[1])
            bot_summary["inferred_race"] = race
            bot_summary["inferred_starting_zone"] = zone
            bot_summary["locations"]["start"] = start_coords
            bot_summary["milestones_detected"].append("BORN_IN_WORLD")

        # Analyze progression records
        for r in trace:
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

        # Goal inference from observed milestone chain
        if "PLOT_CLAIMED" in bot_summary["milestones_detected"]:
            bot_summary["inferred_goals"].append({
                "goal": "GoalClaimHomestead",
                "confidence": 1.0,
                "evidence": "Observed Scarecrow Garden design usage and land plot boundary registration"
            })
        if "HOMESTEAD_CONSTRUCTED" in bot_summary["milestones_detected"]:
            bot_summary["inferred_goals"].append({
                "goal": "GoalErectHome",
                "confidence": 1.0,
                "evidence": "Observed lumber pack craft at workbench and application to home frame"
            })

        required_milestones = {
            "BORN_IN_WORLD", "STARTER_KIT_VERIFIED", "HOUSING_ZONE_REACHED",
            "PLOT_CLAIMED", "CROPS_CULTIVATED", "TIMBER_HARVESTED",
            "WORKBENCH_REACHED", "MATERIAL_PACK_CRAFTED",
            "PACK_DELIVERED_TO_SITE", "HOMESTEAD_CONSTRUCTED"
        }
        observed_set = set(bot_summary["milestones_detected"])
        missing = required_milestones - observed_set

        if not missing:
            bot_summary["verdict"] = "PASS"
        elif "PLOT_CLAIMED" in observed_set:
            bot_summary["verdict"] = "PARTIAL_PROGRESSION"
            bot_summary["confidence_score"] = len(observed_set) / len(required_milestones)
        else:
            bot_summary["verdict"] = "FAIL"
            bot_summary["confidence_score"] = 0.0

        inferred_bots.append(bot_summary)

    # Compile executive report
    os.makedirs(out_dir, exist_ok=True)
    json_path = os.path.join(out_dir, "homestead_10bot_inference_report.json")
    md_path = os.path.join(out_dir, "homestead_10bot_inference_report.md")

    is_synthetic = any("[SYNTHETIC" in (r.get("Payload") or "") or "[SYNTHETIC" in (r.get("Target") or "") for r in records)
    evidence_layer = "SYNTHETIC ORCHESTRATION CONTRACT (Unit Rig)" if is_synthetic else "LIVE SERVER / NETWORK TRACE"
    
    passed_count = sum(1 for b in inferred_bots if b["verdict"] == "PASS")
    overall_verdict = ("SYNTHETIC PASS" if is_synthetic else "PASS") if passed_count == len(inferred_bots) else "FAIL"

    summary_data = {
        "timestamp": "2026-09-16T13:05:00Z",
        "source_trace": trace_file,
        "evidence_layer": evidence_layer,
        "is_synthetic": is_synthetic,
        "total_bots_evaluated": len(inferred_bots),
        "overall_verdict": overall_verdict,
        "metrics": {
            "passed_bots": passed_count,
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
    md.append(f"> **Evaluator:** `infer_homestead_traces.py`")
    md.append(f"> **Input Trace:** `{os.path.basename(trace_file)}` ({len(records)} audit records)")
    md.append(f"> **Overall Inference Verdict:** **{overall_verdict}** ({passed_count}/{len(inferred_bots)} bots verified)")
    if is_synthetic:
        md.append("> **Honesty Notice (AGENTS.md):** Trace contains synthetic harness events. Validates GOAP planning search & actor contract, NOT live player gameplay loop (Live=UNKNOWN, H=UNKNOWN).")
    md.append("")
    md.append("## Macro Telemetry Summary")
    md.append("")
    md.append(f"- **Total Starter Bots Evaluated:** {len(inferred_bots)}")
    md.append(f"- **Residential Plots Claimed:** {total_plots_claimed}")
    md.append(f"- **Farmsteads Constructed:** {total_homes_built}")
    md.append(f"- **Material Packs Crafted:** {total_lumber_crafted}")
    md.append(f"- **Cumulative Transit Distance:** {total_transit_meters:.1f} meters")
    md.append("")
    md.append("## Inferred Bot Progression Matrix")
    md.append("")
    md.append("| Bot # | Inferred Race | Starting Hub | Plot ID | Milestones | Inferred Goals | Verdict |")
    md.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for b in inferred_bots:
        goals_str = ", ".join(g["goal"] for g in b["inferred_goals"])
        plot_id = b["locations"].get("plot_id", "N/A")
        m_count = f"{len(b['milestones_detected'])}/10"
        md.append(f"| **Bot {b['bot_index']:02d}** | {b.get('inferred_race', 'Unknown')} | {b.get('inferred_starting_zone', 'Unknown')} | `{plot_id}` | {m_count} | {goals_str} | **{b['verdict']}** |")

    md.append("")
    md.append("## Detailed Milestone Inference Breakdown")
    md.append("")

    for b in inferred_bots:
        md.append(f"### Bot #{b['bot_index']:02d} ({b.get('inferred_race', 'Unknown')})")
        md.append(f"- **Starting Origin:** {b.get('inferred_starting_zone', 'Unknown')}")
        md.append(f"- **Plot Claimed:** House ID `{b['locations'].get('plot_id')}`")
        md.append("- **Resource Conversions Verified:**")
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
    print(f"\nInference Verdict: {overall_verdict} ({passed_count}/{len(inferred_bots)} passed)")
    return summary_data


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Infer player bot goals and milestones from traces.")
    parser.add_argument("trace_file", help="Path to audit record JSONL file")
    parser.add_argument("--out-dir", default="playertrace-coverage", help="Output directory for reports")
    args = parser.parse_args()

    run_inference(args.trace_file, args.out_dir)
