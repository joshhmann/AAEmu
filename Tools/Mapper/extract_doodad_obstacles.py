#!/usr/bin/env python3
"""
extract_doodad_obstacles.py — Doodad Obstacle Extractor

Dissects the gameworld geometry from doodad_spawns.json and Doodads.xml.
Extracts all physical obstacles (fences, stone walls, gates, houses, buildings, towers)
within any target zone or bounding box, calculating bounding radiuses and coordinates.

Outputs a clean obstacle catalog usable by navigation engines, bot pathfinders, and mappers.

Usage:
  python3 extract_doodad_obstacles.py --zone solzreed --out solzreed_obstacles.json
  python3 extract_doodad_obstacles.py --bounds "21500,11000,22500,12000" --out wardton_obstacles.json
"""

import os
import sys
import re
import json
import math
import argparse
import xml.etree.ElementTree as ET

DOODAD_XML_PATH = "/root/aaemu-dev/AAEmu.Game/Data/Doodads.xml"
DOODAD_SPAWNS_PATH = "/root/aaemu-dev/AAEmu.Game/Data/Worlds/main_world/doodad_spawns.json"

OBSTACLE_KEYWORDS = {
    "fence": {"category": "fence", "radius": 2.0},
    "wall": {"category": "wall", "radius": 3.0},
    "gate": {"category": "gate", "radius": 3.5},
    "door": {"category": "door", "radius": 2.0},
    "house": {"category": "building", "radius": 10.0},
    "building": {"category": "building", "radius": 12.0},
    "tower": {"category": "building", "radius": 6.0},
    "cabin": {"category": "building", "radius": 7.0},
    "tent": {"category": "structure", "radius": 4.0},
    "bridge": {"category": "structure", "radius": 5.0},
    "stairs": {"category": "structure", "radius": 3.0},
    "pillar": {"category": "pillar", "radius": 1.5},
    "ladder": {"category": "ladder", "radius": 1.0},
    "wagon": {"category": "vehicle_static", "radius": 3.0},
    "cart": {"category": "vehicle_static", "radius": 2.0},
    "well": {"category": "structure", "radius": 2.0},
    "workbench": {"category": "crafting", "radius": 2.0},
    "forge": {"category": "crafting", "radius": 2.5},
    "chest": {"category": "prop", "radius": 1.5},
    "signpost": {"category": "prop", "radius": 1.0},
    "explosive": {"category": "prop", "radius": 2.0},
    "powder": {"category": "prop", "radius": 2.0},
    "geyser": {"category": "hazard", "radius": 3.0},
    "statue": {"category": "structure", "radius": 3.0},
    "boulder": {"category": "structure", "radius": 3.5},
    "rock": {"category": "structure", "radius": 3.0},
    "barricade": {"category": "structure", "radius": 3.0}
}

ZONE_PRESETS = {
    "solzreed": (18000, 8000, 24000, 14000),
    "wardton": (21400, 11200, 22200, 12000),
    "crescent": (20000, 8400, 21000, 9200),
    "dewstone": (10000, 13000, 14000, 16500),
    "white_arden": (8000, 12000, 11000, 14000),
    "marianople": (9500, 10500, 12500, 13000),
    "sharpwind": (0, 0, 1000, 1000),
    "cuttingwind": (0, 0, 1000, 1000)
}

def load_obstacle_templates(xml_path=DOODAD_XML_PATH):
    templates = {}
    if not os.path.exists(xml_path):
        print(f"[Error] Doodads.xml not found at {xml_path}", file=sys.stderr)
        return templates

    tree = ET.parse(xml_path)
    root = tree.getroot()
    for elem in root.findall("Creature"):
        cid = int(elem.get("Id", 0))
        name = elem.get("Name", "").strip()
        name_lower = name.lower()

        for kw, meta in OBSTACLE_KEYWORDS.items():
            if kw in name_lower:
                templates[cid] = {
                    "templateId": cid,
                    "name": name,
                    "category": meta["category"],
                    "defaultRadius": meta["radius"]
                }
                break
    return templates

def load_doodad_spawns(spawns_path=DOODAD_SPAWNS_PATH):
    if not os.path.exists(spawns_path):
        return []
    with open(spawns_path, "r") as f:
        text = f.read()
    text = re.sub(r'//.*', '', text)
    text = re.sub(r',(\s*[}\]])', r'\1', text)
    return json.loads(text)

def main():
    parser = argparse.ArgumentParser(description="Extract doodad obstacles (fences, walls, buildings) from game world data.")
    parser.add_argument("--zone", default="wardton", choices=list(ZONE_PRESETS.keys()), help="Zone preset (default: wardton)")
    parser.add_argument("--bounds", help="Custom bounding box 'minX,minY,maxX,maxY'")
    parser.add_argument("--spawns", default=DOODAD_SPAWNS_PATH, help="Doodad spawns JSON path (default: main_world/doodad_spawns.json)")
    parser.add_argument("--out", default="obstacles.json", help="Output JSON path (default: obstacles.json)")
    args = parser.parse_args()

    if args.bounds:
        b = [float(v) for v in args.bounds.split(",")]
        min_x, min_y, max_x, max_y = b[0], b[1], b[2], b[3]
    else:
        min_x, min_y, max_x, max_y = ZONE_PRESETS[args.zone]

    print(f"Scanning Doodads.xml for obstacle categories...")
    templates = load_obstacle_templates()
    print(f"  Identified {len(templates)} obstacle templates (fences, walls, gates, houses).")

    print(f"Filtering doodad spawns in bounding box [{min_x}, {min_y}] -> [{max_x}, {max_y}] from {args.spawns}...")
    all_spawns = load_doodad_spawns(args.spawns)
    print(f"  Total world doodads: {len(all_spawns)}")

    obstacles = []
    category_counts = {}

    for s in all_spawns:
        unit_id = s.get("UnitId", 0)
        if unit_id not in templates:
            continue

        pos = s.get("Position", {})
        px = pos.get("X", 0)
        py = pos.get("Y", 0)
        pz = pos.get("Z", 0)
        yaw = pos.get("Yaw", 0)

        if min_x <= px <= max_x and min_y <= py <= max_y:
            t = templates[unit_id]
            cat = t["category"]
            category_counts[cat] = category_counts.get(cat, 0) + 1

            obstacles.append({
                "templateId": unit_id,
                "name": t["name"],
                "category": cat,
                "x": round(px, 2),
                "y": round(py, 2),
                "z": round(pz, 2),
                "yaw": round(yaw, 2),
                "keepOutRadius": t["defaultRadius"]
            })

    output_data = {
        "zone": args.zone if not args.bounds else "custom",
        "bounds": {"minX": min_x, "minY": min_y, "maxX": max_x, "maxY": max_y},
        "totalObstacles": len(obstacles),
        "categoryCounts": category_counts,
        "obstacles": obstacles
    }

    with open(args.out, "w") as f:
        json.dump(output_data, f, indent=2)

    print(f"Extraction complete!")
    print(f"  Found {len(obstacles)} placed obstacles:")
    for cat, count in sorted(category_counts.items(), key=lambda x: -x[1]):
        print(f"    - {cat}: {count}")
    print(f"Saved obstacle catalog: {args.out}")

if __name__ == "__main__":
    main()
