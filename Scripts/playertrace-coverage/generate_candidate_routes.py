#!/usr/bin/env python3
"""
generate_candidate_routes.py - Synthesizes candidate-based routes from the ArcheAge atlas road network.
Uses A* / Dijkstra shortest-path navigation over digitized road junctions and edges in map_atlas_data.json.
Interpolates waypoints with realistic step distance, elevation, and yaw headings.
"""

import json
import math
import os
import sys
from typing import Dict, List, Optional, Tuple

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ATLAS_PATH = os.path.join(REPO_ROOT, "playertrace-coverage", "map_atlas_data.json")
ROUTES_DIR = os.path.join(REPO_ROOT, "AAEmu.Game", "Data", "Routes")


def load_atlas() -> Tuple[Dict[str, dict], List[dict]]:
    with open(ATLAS_PATH, "r", encoding="utf-8") as f:
        data = json.load(f)
    junctions = {j["id"]: j for j in data.get("junctions", [])}
    edges = data.get("edges", [])
    return junctions, edges


def build_graph(junctions: Dict[str, dict], edges: List[dict]):
    adj: Dict[str, List[Tuple[str, float]]] = {}
    for e in edges:
        u, v = e["from"], e["to"]
        if u in junctions and v in junctions:
            ju, jv = junctions[u], junctions[v]
            dz = (ju.get("z") or 0) - (jv.get("z") or 0)
            dist = math.hypot(ju["x"] - jv["x"], ju["y"] - jv["y"], dz)
            adj.setdefault(u, []).append((v, dist))
            adj.setdefault(v, []).append((u, dist))
    return adj


def find_shortest_path(start_id: str, end_id: str, junctions: Dict[str, dict], adj: dict) -> Optional[List[str]]:
    import heapq
    if start_id not in junctions or end_id not in junctions:
        return None

    queue = [(0.0, start_id, [start_id])]
    visited = {}

    while queue:
        cost, curr, path = heapq.heappop(queue)
        if curr == end_id:
            return path
        if curr in visited and visited[curr] <= cost:
            continue
        visited[curr] = cost

        for nxt, weight in adj.get(curr, []):
            if nxt not in visited or cost + weight < visited[nxt]:
                heapq.heappush(queue, (cost + weight, nxt, path + [nxt]))

    return None


def interpolate_route(junction_path: List[str], junctions: Dict[str, dict], step_meters: float = 25.0) -> dict:
    raw_points = []
    for jid in junction_path:
        j = junctions[jid]
        raw_points.append({
            "id": jid,
            "label": j.get("label", jid),
            "x": float(j["x"]),
            "y": float(j["y"]),
            "z": float(j.get("z", 0.0) or 0.0)
        })

    waypoints = []
    total_dist = 0.0

    for i in range(len(raw_points) - 1):
        p1 = raw_points[i]
        p2 = raw_points[i + 1]

        dx = p2["x"] - p1["x"]
        dy = p2["y"] - p1["y"]
        dz = p2["z"] - p1["z"]
        seg_dist = math.hypot(dx, dy, dz)

        # Yaw in radians (AAEmu standard yaw from atan2)
        yaw = math.atan2(dx, dy)
        if yaw < 0:
            yaw += 2 * math.pi
        yaw = round(yaw, 4)

        num_steps = max(1, int(math.ceil(seg_dist / step_meters)))

        for s in range(num_steps):
            t = s / float(num_steps)
            curr_x = round(p1["x"] + dx * t, 2)
            curr_y = round(p1["y"] + dy * t, 2)
            curr_z = round(p1["z"] + dz * t, 4)

            label = p1["label"] if s == 0 and i == 0 else (p1["label"] if s == 0 else None)
            waypoints.append({
                "ActionType": "Waypoint",
                "X": curr_x,
                "Y": curr_y,
                "Z": curr_z,
                "Yaw": yaw,
                "Label": label
            })

        total_dist += seg_dist

    # Final endpoint
    last = raw_points[-1]
    prev = waypoints[-1] if waypoints else last
    final_yaw = math.atan2(last["x"] - prev["X"], last["y"] - prev["Y"]) if prev != last else 0.0
    if final_yaw < 0:
        final_yaw += 2 * math.pi
    waypoints.append({
        "ActionType": "Waypoint",
        "X": round(last["x"], 2),
        "Y": round(last["y"], 2),
        "Z": round(last["z"], 4),
        "Yaw": round(final_yaw, 4),
        "Label": last["label"]
    })

    return {
        "TotalDistance": round(total_dist, 2),
        "WaypointCount": len(waypoints),
        "ActionCount": 0,
        "Actions": waypoints
    }


def generate_candidate_route(name: str, start_id: str, end_id: str, junctions: dict, adj: dict, step_meters: float = 25.0) -> Optional[dict]:
    path = find_shortest_path(start_id, end_id, junctions, adj)
    if not path:
        print(f"[-] No road connection between {start_id} and {end_id}")
        return None

    route_data = interpolate_route(path, junctions, step_meters)
    route_data["RouteName"] = name
    route_data["Author"] = "CandidateAtlasGenerator"
    route_data["JunctionPath"] = path
    return route_data


# Well-known candidate routes for major thoroughfares across Haranya and Nuia
CANDIDATE_DEFINITIONS = [
    {
        "name": "highway_arcum_iris_to_tigerspine",
        "start": "rb-junction-arcum-iris-J1",
        "end": "rb-junction-tigerspine-J1",
        "desc": "Arcum Iris Serpent's Pass across the northern border pass to Tigerspine Mountains (Anvilton)"
    },
    {
        "name": "highway_falcorth_to_tigerspine",
        "start": "rb-junction-falcorth-J1",
        "end": "rb-junction-tigerspine-J1",
        "desc": "Falcorth Plains Cloudborne down the mountain road to Tigerspine Mountains"
    },
    {
        "name": "highway_tigerspine_to_mahadevi",
        "start": "rb-junction-tigerspine-J1",
        "end": "rb-junction-tigerspine-J18",
        "desc": "Tigerspine Anvilton south past Tiger's Eye towards Mahadevi City of Towers"
    },
    {
        "name": "highway_arcum_iris_thoroughfare",
        "start": "rb-junction-arcum-iris-J14",
        "end": "rb-junction-arcum-iris-J1",
        "desc": "Arcum Iris southern trading post (Hatora) through Widesleeves to Serpent's Pass"
    },
    {
        "name": "highway_falcorth_thoroughfare",
        "start": "rb-junction-falcorth-J14",
        "end": "rb-junction-falcorth-J1",
        "desc": "Falcorth Plains southern plateau past Oxion Clan to Cloudborne"
    },
    {
        "name": "highway_solzreed_crescent_to_wardton",
        "start": "rb-junction-solzreed-J5",
        "end": "rb-junction-solzreed-J14",
        "desc": "Solzreed Peninsula Crescent Throne royal gate past Wardton to Lacton crossroads"
    },
    {
        "name": "highway_solzreed_to_lilyut",
        "start": "rb-junction-solzreed-J14",
        "end": "rb-junction-lilyut-J26",
        "desc": "Solzreed Peninsula (Lacton) eastern road into Lilyut Hills (Windshade)"
    },
    {
        "name": "highway_lilyut_to_dewstone",
        "start": "rb-junction-lilyut-J22",
        "end": "rb-junction-dewstone-J1",
        "desc": "Lilyut Hills Windshade southwest across Riverspan to Dewstone Plains Roadwatch"
    }
]


def main():
    os.makedirs(ROUTES_DIR, exist_ok=True)
    junctions, edges = load_atlas()
    adj = build_graph(junctions, edges)

    print(f"Loaded {len(junctions)} junctions, {len(edges)} edges from {ATLAS_PATH}")
    generated_count = 0

    for defn in CANDIDATE_DEFINITIONS:
        name = defn["name"]
        print(f"\n[+] Generating candidate route: {name} ...")
        route = generate_candidate_route(name, defn["start"], defn["end"], junctions, adj, step_meters=25.0)
        if not route:
            continue

        out_path = os.path.join(ROUTES_DIR, f"{name}.json")
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(route, f, indent=2)

        print(f"    Saved: {out_path}")
        print(f"    Total distance: {route['TotalDistance']} m | Waypoints: {route['WaypointCount']}")
        print(f"    Description: {defn['desc']}")
        generated_count += 1

    print(f"\nSuccessfully generated {generated_count} candidate routes in {ROUTES_DIR}")


if __name__ == "__main__":
    main()
