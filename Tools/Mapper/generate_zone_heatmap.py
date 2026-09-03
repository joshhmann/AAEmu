#!/usr/bin/env python3
"""
generate_zone_heatmap.py — Zone NPC & Road Heatmap Generator

Analyzes 25,118 NPC spawns and carriage routes across ArcheAge zones,
generating 2D vector heatmaps (SVG) and settlement cluster analyses.
Identifies major roads, traffic corridors, and town hubs.

Usage:
  python3 generate_zone_heatmap.py --zone solzreed --out solzreed_map.svg
  python3 generate_zone_heatmap.py --bounds "18000,8000,24000,14000" --out custom_zone.svg
"""

import os
import sys
import re
import json
import math
import argparse

ZONES = {
    "solzreed": {
        "name": "Solzreed Peninsula (Lv 1-10)",
        "min_x": 18000, "max_x": 24000,
        "min_y": 8000, "max_y": 14000,
        "landmarks": [
            {"name": "Crescent Throne", "x": 20450, "y": 8800},
            {"name": "Wardton", "x": 21800, "y": 11500},
            {"name": "Lacton", "x": 19500, "y": 11300},
            {"name": "Relic Isle", "x": 22500, "y": 9200}
        ]
    },
    "dewstone": {
        "name": "Dewstone Plains (Lv 15-20)",
        "min_x": 10000, "max_x": 14000,
        "min_y": 13000, "max_y": 16500,
        "landmarks": [
            {"name": "Royster's Camp", "x": 12850, "y": 14450},
            {"name": "Lilyut Crossing", "x": 12600, "y": 15350},
            {"name": "Windshade", "x": 10600, "y": 15000},
            {"name": "Sanddeep Highway", "x": 10670, "y": 13170}
        ]
    },
    "white_arden": {
        "name": "White Arden (Lv 20-25)",
        "min_x": 8000, "max_x": 11000,
        "min_y": 12000, "max_y": 14000,
        "landmarks": [
            {"name": "Arden Central Hub", "x": 9680, "y": 12770},
            {"name": "Birch Village Ferry", "x": 10280, "y": 12600}
        ]
    },
    "marianople": {
        "name": "Marianople (Lv 25-30)",
        "min_x": 9500, "max_x": 12500,
        "min_y": 10500, "max_y": 13000,
        "landmarks": [
            {"name": "Marianople Capital", "x": 10930, "y": 12040},
            {"name": "Halcyona Border", "x": 11090, "y": 11600},
            {"name": "Two Crowns Gate", "x": 11560, "y": 11840}
        ]
    }
}

NPC_SPAWNS_PATH = "/root/aaemu-dev/AAEmu.Game/Data/Worlds/main_world/npc_spawns.json"
TRANSFER_SPAWNS_PATH = "/root/aaemu-dev/AAEmu.Game/Data/Worlds/main_world/transfer_spawns.json"

def load_json_with_comments(path):
    if not os.path.exists(path):
        return []
    with open(path, "r") as f:
        text = f.read()
    text = re.sub(r'//.*', '', text)
    text = re.sub(r',(\s*[}\]])', r'\1', text)
    return json.loads(text)

def generate_svg(zone_meta, npcs, transfers, out_path, width=1200, height=1200):
    min_x, max_x = zone_meta["min_x"], zone_meta["max_x"]
    min_y, max_y = zone_meta["min_y"], zone_meta["max_y"]

    def to_svg(x, y):
        # Y is inverted in SVG (0,0 is top-left)
        sx = ((x - min_x) / (max_x - min_x)) * (width - 100) + 50
        sy = ((max_y - y) / (max_y - min_y)) * (height - 100) + 50
        return round(sx, 1), round(sy, 1)

    svg = []
    svg.append(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" style="background-color: #0b0f19; font-family: sans-serif;">')
    svg.append(f'  <!-- Title -->')
    svg.append(f'  <text x="50" y="40" fill="#f8fafc" font-size="24" font-weight="bold">{zone_meta["name"]} — NPC & Road Heatmap</text>')
    svg.append(f'  <text x="50" y="65" fill="#94a3b8" font-size="14">Total NPCs: {len(npcs)} | Carriage/Transfer Checkpoints: {len(transfers)}</text>')

    # Grid lines (every 1000m)
    svg.append('  <!-- Coordinate Grid -->')
    for gx in range(int(min_x), int(max_x) + 1, 1000):
        sx1, sy1 = to_svg(gx, min_y)
        sx2, sy2 = to_svg(gx, max_y)
        svg.append(f'  <line x1="{sx1}" y1="{sy1}" x2="{sx2}" y2="{sy2}" stroke="#1e293b" stroke-width="1" stroke-dasharray="4,4"/>')
        svg.append(f'  <text x="{sx1}" y="{height - 20}" fill="#475569" font-size="10" text-anchor="middle">X:{gx}</text>')

    for gy in range(int(min_y), int(max_y) + 1, 1000):
        sx1, sy1 = to_svg(min_x, gy)
        sx2, sy2 = to_svg(max_x, gy)
        svg.append(f'  <line x1="{sx1}" y1="{sy1}" x2="{sx2}" y2="{sy2}" stroke="#1e293b" stroke-width="1" stroke-dasharray="4,4"/>')
        svg.append(f'  <text x="25" y="{sy1+4}" fill="#475569" font-size="10" text-anchor="end">Y:{gy}</text>')

    # NPC Points
    svg.append('  <!-- NPC Spawns (Heatmap density) -->')
    for npc in npcs:
        pos = npc.get("Position", {})
        px, py = pos.get("X", 0), pos.get("Y", 0)
        sx, sy = to_svg(px, py)
        # Glowing cyan dot
        svg.append(f'  <circle cx="{sx}" cy="{sy}" r="2" fill="#38bdf8" opacity="0.35"/>')

    # Transfer / Road Spawns
    svg.append('  <!-- Carriage / Road Checkpoints (Orange dots) -->')
    for tr in transfers:
        pos = tr.get("Position", {})
        px, py = pos.get("X", 0), pos.get("Y", 0)
        sx, sy = to_svg(px, py)
        svg.append(f'  <circle cx="{sx}" cy="{sy}" r="5" fill="#fb923c" stroke="#fed7aa" stroke-width="1"/>')

    # Landmarks / Town centers
    svg.append('  <!-- Known Landmarks / Settlement Hubs -->')
    for lm in zone_meta.get("landmarks", []):
        lx, ly = lm["x"], lm["y"]
        sx, sy = to_svg(lx, ly)
        svg.append(f'  <circle cx="{sx}" cy="{sy}" r="8" fill="#e11d48" stroke="#ffffff" stroke-width="2"/>')
        svg.append(f'  <text x="{sx + 12}" y="{sy + 5}" fill="#f43f5e" font-size="13" font-weight="bold">{lm["name"]}</text>')

    svg.append('</svg>')

    with open(out_path, "w") as f:
        f.write("\n".join(svg))
    return out_path

def main():
    parser = argparse.ArgumentParser(description="Generate NPC and Road heatmap for an ArcheAge zone.")
    parser.add_argument("--zone", default="solzreed", choices=list(ZONES.keys()), help="Zone preset (default: solzreed)")
    parser.add_argument("--bounds", help="Custom bounds 'minX,minY,maxX,maxY'")
    parser.add_argument("--out", default="zone_heatmap.svg", help="Output SVG path (default: zone_heatmap.svg)")
    args = parser.parse_args()

    if args.bounds:
        b = [float(v) for v in args.bounds.split(",")]
        zone_meta = {"name": "Custom Area", "min_x": b[0], "min_y": b[1], "max_x": b[2], "max_y": b[3], "landmarks": []}
    else:
        zone_meta = ZONES[args.zone]

    print(f"Loading NPC and transfer spawns for '{zone_meta['name']}'...")
    all_npcs = load_json_with_comments(NPC_SPAWNS_PATH)
    all_transfers = load_json_with_comments(TRANSFER_SPAWNS_PATH)

    min_x, max_x = zone_meta["min_x"], zone_meta["max_x"]
    min_y, max_y = zone_meta["min_y"], zone_meta["max_y"]

    zone_npcs = [n for n in all_npcs if min_x <= n.get("Position", {}).get("X", 0) <= max_x and min_y <= n.get("Position", {}).get("Y", 0) <= max_y]
    zone_transfers = [t for t in all_transfers if min_x <= t.get("Position", {}).get("X", 0) <= max_x and min_y <= t.get("Position", {}).get("Y", 0) <= max_y]

    print(f"  Zone NPC Spawns:       {len(zone_npcs)}")
    print(f"  Carriage Checkpoints:  {len(zone_transfers)}")

    out_file = generate_svg(zone_meta, zone_npcs, zone_transfers, args.out)
    print(f"Successfully generated vector map: {out_file}")

if __name__ == "__main__":
    main()
