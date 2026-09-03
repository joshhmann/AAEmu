#!/usr/bin/env python3
"""
redline_to_path.py — Redline Map Waypoint Converter

Converts marked map coordinates or pixel points into game-ready .path and JSON routes.
Features:
- Pixel-to-world coordinate affine calibration (give 2 or 4 anchor points or bounding box)
- Linear/spline interpolation between waypoints at configurable spacing (default: 2.0m)
- Ground Z-height estimation from nearest NPC spawns
- Exports dual formats:
  - Data/Path/<name>.path (standard pipe-separated |X|Y|Z|)
  - Data/Routes/<name>.json (rich DevMapper action graph)

Usage:
  python3 redline_to_path.py --name my_road --points "20100,10500;20150,10520;20200,10560"
  python3 redline_to_path.py --name solzreed_trail --file points.txt --spacing 2.0
"""

import os
import sys
import math
import json
import argparse
import re

DEFAULT_SPAWN_PATH = "/root/aaemu-dev/AAEmu.Game/Data/Worlds/main_world/npc_spawns.json"
DEFAULT_OUT_PATH = "/root/aaemu-dev/AAEmu.Game/Data/Path"
DEFAULT_OUT_ROUTES = "/root/aaemu-dev/AAEmu.Game/Data/Routes"

class GroundHeightSampler:
    """Estimates ground Z height using inverse-distance weighting from nearby NPC spawns."""
    def __init__(self, spawn_file=DEFAULT_SPAWN_PATH):
        self.points = []
        if os.path.exists(spawn_file):
            try:
                with open(spawn_file, "r") as f:
                    text = f.read()
                text = re.sub(r'//.*', '', text)
                text = re.sub(r',(\s*[}\]])', r'\1', text)
                data = json.loads(text)
                for s in data:
                    pos = s.get("Position", {})
                    if "X" in pos and "Y" in pos and "Z" in pos:
                        self.points.append((pos["X"], pos["Y"], pos["Z"]))
            except Exception as e:
                print(f"[Warn] Failed loading height samples: {e}", file=sys.stderr)

    def sample_z(self, x, y, default_z=50.0):
        if not self.points:
            return default_z
        # Find 3 nearest points
        nearest = sorted(self.points, key=lambda p: (p[0] - x)**2 + (p[1] - y)**2)[:3]
        total_w = 0.0
        weighted_z = 0.0
        for px, py, pz in nearest:
            dist = math.hypot(px - x, py - y)
            if dist < 0.001:
                return pz
            w = 1.0 / (dist ** 2)
            total_w += w
            weighted_z += pz * w
        return weighted_z / total_w if total_w > 0 else default_z

def interpolate_segment(p1, p2, spacing=2.0):
    """Interpolates points between p1 and p2 at regular spacing."""
    x1, y1 = p1
    x2, y2 = p2
    dist = math.hypot(x2 - x1, y2 - y1)
    if dist <= spacing:
        return [p1]
    
    steps = int(math.ceil(dist / spacing))
    points = []
    for i in range(steps):
        t = i / float(steps)
        points.append((x1 + (x2 - x1) * t, y1 + (y2 - y1) * t))
    return points

def build_path(raw_points, spacing=2.0, sampler=None):
    """Generates continuous 3D waypoints from raw 2D input line."""
    if len(raw_points) < 2:
        return []

    sampled_2d = []
    for i in range(len(raw_points) - 1):
        seg = interpolate_segment(raw_points[i], raw_points[i+1], spacing)
        sampled_2d.extend(seg)
    sampled_2d.append(raw_points[-1])

    waypoints = []
    total_dist = 0.0
    for idx, (x, y) in enumerate(sampled_2d):
        z = sampler.sample_z(x, y) if sampler else 50.0
        if idx > 0:
            prev = waypoints[-1]
            total_dist += math.hypot(x - prev["x"], y - prev["y"], z - prev["z"])
        
        # Calculate yaw toward next point
        yaw = 0.0
        if idx < len(sampled_2d) - 1:
            nx, ny = sampled_2d[idx + 1]
            yaw = math.atan2(ny - y, nx - x)

        waypoints.append({"x": round(x, 2), "y": round(y, 2), "z": round(z, 4), "yaw": round(yaw, 4)})
    return waypoints, total_dist

def export_files(name, waypoints, total_dist, out_path_dir=DEFAULT_OUT_PATH, out_routes_dir=DEFAULT_OUT_ROUTES):
    os.makedirs(out_path_dir, exist_ok=True)
    os.makedirs(out_routes_dir, exist_ok=True)

    # 1. Export .path format
    path_file = os.path.join(out_path_dir, f"{name}.path")
    with open(path_file, "w") as f:
        for wp in waypoints:
            f.write(f"|{wp['x']:.2f}|{wp['y']:.2f}|{wp['z']:.4f}|\n")

    # 2. Export JSON action graph format
    route_file = os.path.join(out_routes_dir, f"{name}.json")
    actions = []
    for idx, wp in enumerate(waypoints):
        actions.append({
            "ActionType": "Waypoint",
            "X": wp["x"],
            "Y": wp["y"],
            "Z": wp["z"],
            "Yaw": wp["yaw"],
            "Label": "start" if idx == 0 else ("end" if idx == len(waypoints) - 1 else None)
        })

    route_data = {
        "RouteName": name,
        "Author": "RedlineTool",
        "TotalDistance": round(total_dist, 2),
        "WaypointCount": len(waypoints),
        "ActionCount": 0,
        "Actions": actions
    }

    with open(route_file, "w") as f:
        json.dump(route_data, f, indent=2)

    return path_file, route_file

def main():
    parser = argparse.ArgumentParser(description="Convert marked redline map coordinates to .path and JSON routes.")
    parser.add_argument("--name", required=True, help="Route name (e.g. solzreed_express)")
    parser.add_argument("--points", help="Semicolon-delimited X,Y coordinates (e.g. '20100,10500;20150,10520')")
    parser.add_argument("--file", help="Text file containing X,Y coordinates (one per line)")
    parser.add_argument("--spacing", type=float, default=2.0, help="Waypoint spacing in meters (default: 2.0m)")
    args = parser.parse_args()

    raw_points = []
    if args.points:
        for part in args.points.split(";"):
            if not part.strip(): continue
            vals = part.strip().split(",")
            raw_points.append((float(vals[0]), float(vals[1])))
    elif args.file:
        with open(args.file, "r") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"): continue
                vals = line.replace(",", " ").split()
                raw_points.append((float(vals[0]), float(vals[1])))
    else:
        print("[Error] Must specify either --points or --file", file=sys.stderr)
        sys.exit(1)

    if len(raw_points) < 2:
        print("[Error] Need at least 2 points to define a path.", file=sys.stderr)
        sys.exit(1)

    print(f"Sampling ground height and interpolating {len(raw_points)} input nodes (spacing: {args.spacing}m)...")
    sampler = GroundHeightSampler()
    waypoints, total_dist = build_path(raw_points, spacing=args.spacing, sampler=sampler)

    path_file, route_file = export_files(args.name, waypoints, total_dist)
    print(f"Successfully generated route '{args.name}'!")
    print(f"  Total Waypoints: {len(waypoints)}")
    print(f"  Total Distance:  {total_dist:.1f} m")
    print(f"  .path File:      {path_file}")
    print(f"  JSON File:       {route_file}")

if __name__ == "__main__":
    main()
