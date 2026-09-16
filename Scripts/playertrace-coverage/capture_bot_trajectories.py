#!/usr/bin/env python3
"""
capture_bot_trajectories.py — Continuous Live Navigation & Telemetry Harvester

Connects to the AAEmu Game Server REST API (:1280) on .165, tracking active
player bots in real time. Logs position streams, movement vectors, elevation
gradients, speed profiles, and detects movement stalls/collisions.

Output datasets are saved to `traces/bot-trajectories/` in standard JSONL format
for navmesh tuning, topological road network refinement, and spatial analysis.
"""

import argparse
import datetime
import json
import math
import os
import sys
import time
import urllib.request as urlreq

def compute_dist2d(x1, y1, x2, y2):
    return math.hypot(x2 - x1, y2 - y1)

def compute_dist3d(x1, y1, z1, x2, y2, z2):
    return math.sqrt((x2 - x1) ** 2 + (y2 - y1) ** 2 + (z2 - z1) ** 2)

def main():
    parser = argparse.ArgumentParser(description="AAEmu Live Bot Navigation Data Harvester")
    parser.add_argument("--api-base", default=os.environ.get("AAEMU_GAME_API_BASE", "http://192.168.0.165:1280"),
                        help="AAEmu Game WebApi base URL")
    parser.add_argument("--token", default=os.environ.get("AAEMU_BOT_CTRL_TOKEN", "aaemu-tester-token"),
                        help="X-Auth-Token for BotControl API")
    parser.add_argument("--interval", type=float, default=1.0, help="Polling interval in seconds")
    parser.add_argument("--out-dir", default="traces/bot-trajectories", help="Output directory for JSONL traces")
    parser.add_argument("--stall-threshold", type=float, default=0.05, help="Max movement (meters) considered stalled")
    args = parser.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    session_id = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    out_file = os.path.join(args.out_dir, f"bot_nav_telemetry_{session_id}.jsonl")
    summary_file = os.path.join(args.out_dir, f"bot_nav_summary_{session_id}.json")

    print(f"============================================================")
    print(f" AAEmu Bot Navigation Telemetry Harvester")
    print(f" Target API: {args.api_base}")
    print(f" Trace Sink: {out_file}")
    print(f" Poll Rate:  {args.interval}s")
    print(f"============================================================")

    bot_histories = {} # id -> {last_x, last_y, last_z, last_time, total_distance, stall_count, samples}
    total_samples = 0
    start_time = time.time()

    with open(out_file, "a", encoding="utf-8") as f_out:
        try:
            while True:
                cycle_start = time.time()
                active_bots = []

                # Query bots API
                try:
                    req = urlreq.Request(
                        f"{args.api_base.rstrip('/')}/api/bots",
                        headers={"User-Agent": "AAEmu-NavHarvester", "X-Auth-Token": args.token}
                    )
                    with urlreq.urlopen(req, timeout=3.0) as resp:
                        if resp.status == 200:
                            data = json.loads(resp.read().decode("utf-8"))
                            bot_list = data.get("Bots") or data.get("Data") or data.get("data") or []
                            for b in bot_list:
                                state = b.get("State") or b.get("state")
                                if state == "Active":
                                    active_bots.append({
                                        "id": b.get("Id") or b.get("id") or b.get("CharacterId") or b.get("characterId"),
                                        "name": b.get("Name") or b.get("name"),
                                        "x": float(b.get("X") or b.get("x") or 0.0),
                                        "y": float(b.get("Y") or b.get("y") or 0.0),
                                        "z": float(b.get("Z") or b.get("z") or 0.0),
                                        "fidelity": b.get("Fidelity") or b.get("fidelity") or "Full"
                                    })
                except Exception as e:
                    # Fallback to logged-characters if bots API query times out
                    try:
                        req = urlreq.Request(
                            f"{args.api_base.rstrip('/')}/api/world/logged-characters",
                            headers={"User-Agent": "AAEmu-NavHarvester"}
                        )
                        with urlreq.urlopen(req, timeout=3.0) as resp:
                            if resp.status == 200:
                                chars = json.loads(resp.read().decode("utf-8"))
                                for c in chars:
                                    if c.get("IsOnline", False) or c.get("isOnline", False):
                                        active_bots.append({
                                            "id": c.get("Id") or c.get("id"),
                                            "name": c.get("Name") or c.get("name"),
                                            "x": float(c.get("X") or c.get("x") or 0.0),
                                            "y": float(c.get("Y") or c.get("y") or 0.0),
                                            "z": float(c.get("Z") or c.get("z") or 0.0),
                                            "fidelity": "Full"
                                        })
                    except Exception as ex2:
                        print(f"[{datetime.datetime.now().strftime('%H:%M:%S')}] Query error: {e} / {ex2}", file=sys.stderr)

                now = time.time()
                timestamp_iso = datetime.datetime.now(datetime.timezone.utc).isoformat()

                for b in active_bots:
                    bid = b["id"]
                    x, y, z = b["x"], b["y"], b["z"]

                    if bid not in bot_histories:
                        bot_histories[bid] = {
                            "name": b["name"],
                            "last_x": x, "last_y": y, "last_z": z,
                            "last_time": now,
                            "total_distance": 0.0,
                            "stall_count": 0,
                            "samples": 0
                        }

                    prev = bot_histories[bid]
                    dt = max(0.001, now - prev["last_time"])
                    d2 = compute_dist2d(prev["last_x"], prev["last_y"], x, y)
                    d3 = compute_dist3d(prev["last_x"], prev["last_y"], prev["last_z"], x, y, z)
                    speed2d = d2 / dt
                    dz = z - prev["last_z"]
                    grade_pct = (abs(dz) / max(0.01, d2)) * 100.0 if d2 > 0.01 else 0.0

                    is_stalled = False
                    if d2 < args.stall_threshold:
                        prev["stall_count"] += 1
                        if prev["stall_count"] >= 3:
                            is_stalled = True
                    else:
                        prev["stall_count"] = 0

                    prev["total_distance"] += d3
                    prev["samples"] += 1
                    prev["last_x"] = x
                    prev["last_y"] = y
                    prev["last_z"] = z
                    prev["last_time"] = now

                    record = {
                        "ts": timestamp_iso,
                        "bot_id": bid,
                        "name": b["name"],
                        "pos": {"x": round(x, 3), "y": round(y, 3), "z": round(z, 3)},
                        "d_xy": round(d2, 3),
                        "d_z": round(dz, 3),
                        "speed_mps": round(speed2d, 2),
                        "slope_pct": round(grade_pct, 1),
                        "stalled": is_stalled,
                        "total_dist_m": round(prev["total_distance"], 1)
                    }

                    f_out.write(json.dumps(record) + "\n")
                    total_samples += 1

                f_out.flush()

                # Print console update every ~5 seconds
                if int(now - start_time) % 5 == 0 and active_bots:
                    elapsed = now - start_time
                    fleet_dist = sum(h["total_distance"] for h in bot_histories.values())
                    avg_speed = (fleet_dist / max(1.0, elapsed * len(active_bots))) if active_bots else 0.0
                    print(f"[{datetime.datetime.now().strftime('%H:%M:%S')}] Active: {len(active_bots)} bots | Samples: {total_samples} | Fleet Dist: {fleet_dist:.1f}m | Avg Spd: {avg_speed:.2f}m/s")

                sleep_time = max(0.1, args.interval - (time.time() - cycle_start))
                time.sleep(sleep_time)

        except KeyboardInterrupt:
            print("\nHarvest stopped by operator.")

    # Write summary
    total_elapsed = time.time() - start_time
    summary = {
        "session_id": session_id,
        "duration_seconds": round(total_elapsed, 1),
        "total_samples": total_samples,
        "bots_tracked": len(bot_histories),
        "total_fleet_distance_m": round(sum(h["total_distance"] for h in bot_histories.values()), 1),
        "bot_statistics": {
            str(k): {
                "name": v["name"],
                "distance_m": round(v["total_distance"], 1),
                "samples": v["samples"]
            } for k, v in bot_histories.items()
        }
    }
    with open(summary_file, "w", encoding="utf-8") as f_sum:
        json.dump(summary, f_sum, indent=2)

    print(f"Summary written to {summary_file}")

if __name__ == "__main__":
    main()
