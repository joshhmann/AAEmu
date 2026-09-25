#!/usr/bin/env python3
"""
export_world_snapshot.py — Offline bbox -> JSON world snapshot exporter.

Planning-only export: bounding-box filter over npc_spawns.json +
doodad_spawns.json, joined against compact.sqlite3 (npc names/levels,
doodad names, actor radii), plus Data/Routes paths clipped to the bbox.

Usage:
  python3 export_world_snapshot.py --zone solzreed --out /tmp/snap.json
  python3 export_world_snapshot.py --bounds "22000,11000,23000,12000" --out /tmp/snap.json
  python3 export_world_snapshot.py --zone solzreed --max-entities 500 --out /tmp/snap.json

Schema: aaemu.world-snapshot/v1. Output is cached static JSON for planning
visualization only (planning_only:true) — NEVER decision input, NEVER raw
.bai/navmesh bytes, NEVER per-request regeneration.

Conventions follow Tools/Mapper/extract_doodad_obstacles.py
(ZONE_PRESETS, comment-stripped spawner JSON, world-meters x=east y=north).
"""

import argparse
import datetime
import glob
import hashlib
import json
import os
import re
import sqlite3
import sys

BASE = "/root/aaemu-dev"
WORLDS_DIR = os.path.join(BASE, "AAEmu.Game/Data/Worlds")
ROUTES_DIR = os.path.join(BASE, "AAEmu.Game/Data/Routes")
DEFAULT_SQLITE = os.path.join(BASE, "AAEmu.Game/Data/compact.sqlite3")

SCHEMA = "aaemu.world-snapshot/v1"
TOOL = "Tools/Mapper/export_world_snapshot.py"

ZONE_PRESETS = {
    "solzreed": (18000, 8000, 24000, 14000),
    "wardton": (21400, 11200, 22200, 12000),
    "crescent": (20000, 8400, 21000, 9200),
    "dewstone": (10000, 13000, 14000, 16500),
    "white_arden": (8000, 12000, 11000, 14000),
    "marianople": (9500, 10500, 12500, 13000),
    "sharpwind": (0, 0, 1000, 1000),
    "cuttingwind": (0, 0, 1000, 1000),
}

NPC_RADIUS_DEFAULT = 0.5
DOODAD_RADIUS_DEFAULT = 1.5

# Keyword radius fallback, same values as the obstacle extractor's table.
try:
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from extract_doodad_obstacles import OBSTACLE_KEYWORDS as _KW
    KEYWORD_RADIUS = {k: v["radius"] for k, v in _KW.items()}
except Exception:
    KEYWORD_RADIUS = {
        "fence": 2.0, "wall": 3.0, "gate": 3.5, "house": 10.0,
        "building": 12.0, "tower": 6.0, "bridge": 5.0, "boulder": 3.5,
        "rock": 3.0, "statue": 3.0, "barricade": 3.0,
    }


def md5_of(path, limit=256 * 1024 * 1024):
    h = hashlib.md5()
    with open(path, "rb") as f:
        while True:
            chunk = f.read(1 << 20)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def load_spawns(path):
    if not os.path.exists(path):
        print(f"[Error] Spawner JSON not found: {path}", file=sys.stderr)
        sys.exit(2)
    with open(path, "r") as f:
        text = f.read()
    text = re.sub(r"//.*", "", text)
    text = re.sub(r",(\s*[}\]])", r"\1", text)
    return json.loads(text)


def in_bbox(pos, box):
    x1, y1, x2, y2 = box
    return x1 <= pos.get("X", 0) <= x2 and y1 <= pos.get("Y", 0) <= y2


def keyword_radius(name):
    low = (name or "").lower()
    for kw, r in KEYWORD_RADIUS.items():
        if kw in low:
            return r
    return None


def main():
    ap = argparse.ArgumentParser(description="Export offline bbox world snapshot (aaemu.world-snapshot/v1).")
    ap.add_argument("--zone", default="solzreed", choices=list(ZONE_PRESETS.keys()))
    ap.add_argument("--bounds", help="Custom bbox 'x1,y1,x2,y2' (overrides --zone)")
    ap.add_argument("--out", required=True, help="Output JSON path")
    ap.add_argument("--world", default="main_world", help="World dir under AAEmu.Game/Data/Worlds")
    ap.add_argument("--sqlite", default=DEFAULT_SQLITE, help="compact.sqlite3 path")
    ap.add_argument("--max-entities", type=int, default=67034)
    args = ap.parse_args()

    box = (
        [float(v) for v in args.bounds.split(",")]
        if args.bounds
        else list(ZONE_PRESETS[args.zone])
    )
    x1, y1, x2, y2 = box
    label = "custom" if args.bounds else args.zone

    if not os.path.exists(args.sqlite):
        print(f"[Error] compact.sqlite3 not found: {args.sqlite} — failing closed.", file=sys.stderr)
        sys.exit(2)

    npc_path = os.path.join(WORLDS_DIR, args.world, "npc_spawns.json")
    doodad_path = os.path.join(WORLDS_DIR, args.world, "doodad_spawns.json")
    npc_spawns = load_spawns(npc_path)
    doodad_spawns = load_spawns(doodad_path)
    print(f"Loaded {len(npc_spawns)} npc + {len(doodad_spawns)} doodad spawns ({args.world}).")

    con = sqlite3.connect(args.sqlite)
    con.row_factory = sqlite3.Row
    npc_rows = {r["id"]: r for r in con.execute("SELECT id, name, LEVEL, model_id, comment2 FROM npcs")}
    alm_rows = {r["id"]: r for r in con.execute(
        "SELECT id, name, group_id, sim_radius, min_time FROM doodad_almighties")}
    actor_radius = {}
    for r in con.execute("SELECT id, radius FROM actor_models"):
        if r["radius"]:
            actor_radius[r["id"]] = float(r["radius"])
    actor_by_model = {}
    for r in con.execute("SELECT model_file, radius FROM actor_models"):
        if r["model_file"] and r["radius"]:
            actor_by_model.setdefault(r["model_file"].lower(), float(r["radius"]))
    spawner_delay = {}
    for r in con.execute("SELECT id, spawn_delay_min FROM npc_spawners"):
        if r["spawn_delay_min"]:
            spawner_delay[r["id"]] = float(r["spawn_delay_min"])
    con.close()
    print(f"Join tables: npcs={len(npc_rows)} doodad_almighties={len(alm_rows)} actor_models={len(actor_radius)}.")

    misses = {"npc_template": 0, "npc_radius": 0, "doodad_template": 0, "doodad_radius": 0}
    entities = []

    npc_in = doodad_in = 0
    for s in npc_spawns:
        pos = s.get("Position", {})
        if not in_bbox(pos, box):
            continue
        npc_in += 1
        tid = s.get("UnitId", 0)
        row = npc_rows.get(tid)
        spawn_ids = s.get("NpcSpawnerIds") or []
        spawn_id = spawn_ids[0] if spawn_ids else tid
        if row is None:
            misses["npc_template"] += 1
            name, level, zone, radius = f"unknown_{tid}", None, "", NPC_RADIUS_DEFAULT
            misses["npc_radius"] += 1
        else:
            name = row["name"]
            level = row["LEVEL"]
            zone = row["comment2"] or ""
            radius = actor_radius.get(row["model_id"])
            if radius is None:
                misses["npc_radius"] += 1
                radius = NPC_RADIUS_DEFAULT
        delay = spawner_delay.get(spawn_id, spawner_delay.get(tid))
        x, y, z = pos.get("X", 0), pos.get("Y", 0), pos.get("Z", 0)
        entities.append({
            "kind": "npc", "template_id": tid, "name": name, "level": level,
            "zone": zone, "x": x, "y": y, "z": z, "radius": radius,
            "spawn_id": spawn_id, "respawn_s": delay, "pos": [x, y, z],
        })

    for s in doodad_spawns:
        pos = s.get("Position", {})
        if not in_bbox(pos, box):
            continue
        doodad_in += 1
        tid = s.get("UnitId", 0)
        row = alm_rows.get(tid)
        spawn_id = s.get("Id", tid)
        if row is None:
            misses["doodad_template"] += 1
            name, group = s.get("Title") or f"unknown_{tid}", None
            radius = keyword_radius(name) or DOODAD_RADIUS_DEFAULT
            misses["doodad_radius"] += 1
            respawn = None
        else:
            name = row["name"]
            group = row["group_id"]
            respawn = row["min_time"] / 1000.0 if row["min_time"] else None
            sim_r = row["sim_radius"] or 0
            radius = float(sim_r) if sim_r > 0 else None
            if radius is None:
                radius = keyword_radius(name)
            if radius is None:
                misses["doodad_radius"] += 1
                radius = DOODAD_RADIUS_DEFAULT
        x, y, z = pos.get("X", 0), pos.get("Y", 0), pos.get("Z", 0)
        entities.append({
            "kind": "doodad", "template_id": tid, "name": name, "level": None,
            "zone": "", "x": x, "y": y, "z": z, "radius": radius,
            "spawn_id": spawn_id, "respawn_s": respawn, "pos": [x, y, z],
            "group_id": group,
        })

    total_in_bbox = len(entities)
    entities.sort(key=lambda e: (e["kind"], str(e["spawn_id"])))
    truncated = total_in_bbox > args.max_entities
    entities = entities[:args.max_entities]

    paths = []
    for rp in sorted(glob.glob(os.path.join(ROUTES_DIR, "*.json"))):
        try:
            with open(rp) as f:
                route = json.load(f)
        except Exception:
            continue
        pts = [
            [a.get("X", 0), a.get("Y", 0), a.get("Z", 0)]
            for a in route.get("Actions", [])
            if a.get("ActionType") == "Waypoint"
            and x1 <= a.get("X", 0) <= x2 and y1 <= a.get("Y", 0) <= y2
        ]
        if pts:
            paths.append({
                "name": route.get("RouteName") or os.path.splitext(os.path.basename(rp))[0],
                "points": pts,
            })
    print(f"In bbox: npc={npc_in} doodad={doodad_in} total={total_in_bbox} "
          f"-> entities={len(entities)} truncated={truncated}; paths={len(paths)}.")

    # Stream-write: header + entities/paths item-by-item (no big in-memory dump).
    captured = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    header = {
        "schema": SCHEMA,
        "planning_only": True,
        "bbox": {"x1": x1, "y1": y1, "x2": x2, "y2": y2},
        "crs": "world-meters,x=east,y=north",
        "source": {
            "world": args.world,
            "md5": {
                "npc_spawns": md5_of(npc_path),
                "doodad_spawns": md5_of(doodad_path),
                "sqlite": md5_of(args.sqlite),
            },
            "captured_utc": captured,
            "tool": TOOL,
        },
        "stats": {
            "zone": label,
            "npc_in_bbox": npc_in,
            "doodad_in_bbox": doodad_in,
            "total_in_bbox": total_in_bbox,
            "entities": len(entities),
            "truncated": truncated,
            "max_entities": args.max_entities,
            "misses": misses,
            "path_count": len(paths),
        },
    }

    # Stream-write: header, then entities/paths item-by-item, no big DOM dump.
    with open(args.out, "w") as f:
        f.write("{\n")
        f.write(f'"schema": {json.dumps(header["schema"])},\n')
        f.write('"planning_only": true,\n')
        f.write(f'"bbox": {json.dumps(header["bbox"])},\n')
        f.write(f'"crs": {json.dumps(header["crs"])},\n')
        f.write(f'"source": {json.dumps(header["source"], ensure_ascii=False)},\n')
        f.write(f'"stats": {json.dumps(header["stats"], ensure_ascii=False)},\n')
        f.write('"entities": [\n')
        for i, e in enumerate(entities):
            f.write(json.dumps(e, ensure_ascii=False))
            f.write(",\n" if i < len(entities) - 1 else "\n")
        f.write("],\n")
        f.write('"paths": [\n')
        for i, p in enumerate(paths):
            f.write(json.dumps(p, ensure_ascii=False))
            f.write(",\n" if i < len(paths) - 1 else "\n")
        f.write("]\n}\n")

    size = os.path.getsize(args.out)
    print(f"Saved world snapshot: {args.out} ({size} bytes, {len(entities)} entities)")
    print(f"  misses={misses}")


if __name__ == "__main__":
    main()
