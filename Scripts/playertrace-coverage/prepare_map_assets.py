#!/usr/bin/env python3
"""Decode extracted 1.2 client maps and attach canonical world-meter bounds.

Pillow is needed only for this offline preparation step. Output contains client
artwork and belongs in .client_files, never in version control.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3

from PIL import Image


WORLD_IMAGES = {
    "main_world": ("main_world", "World", "world"),
    "west": ("land_west", "Nuia", "continent"),
    "east": ("land_east", "Haranya", "continent"),
    "origin": ("land_origin", "Auroria", "continent"),
    "north_sea": ("land_silent_sea", "Arcadian Sea", "continent"),
}
ZONE_IMAGES = {
    "s_lostway_sea": "s_lost_road_sea",
    "s_golden_sea": "s_gold_sea",
    "s_crescent_sea": "s_crescent_moon_sea",
}
ZONE_TITLES = {
    "w_solzreed": "Solzreed Peninsula", "w_garangdol_plains": "Dewstone Plains",
    "w_gweonid_forest": "Gweonid Forest", "w_lilyut_meadow": "Lilyut Hills",
    "w_marianople": "Marianople", "w_white_forest": "White Arden",
    "w_two_crowns": "Two Crowns", "w_cross_plains": "Cinderstone Moor",
    "w_golden_plains": "Halcyona", "w_hell_swamp": "Hellswamp",
    "w_long_sand": "Sanddeep", "w_the_carcass": "Karkasse Ridgelands",
    "e_rainbow_field": "Arcum Iris", "e_falcony_plateau": "Falcorth Plains",
    "e_tiger_spine_mountains": "Tigerspine Mountains", "e_mahadevi": "Mahadevi",
    "e_sunrise_peninsula": "Solis Headlands", "e_singing_land": "Villanelle",
    "e_ancient_forest": "Silent Forest", "e_ynystere": "Ynystere",
    "e_lokas_checkers": "Rookborne Basin", "e_steppe_belt": "Windscour Savannah",
    "e_ruins_of_hariharalaya": "Perinoor Ruins", "e_hasla": "Hasla",
}


def prepare(client_root: Path, database: Path, output: Path):
    texture_root = client_root / "game/ui/map/world/en_us"
    output.mkdir(parents=True, exist_ok=True)
    maps = []
    missing = []
    digest = hashlib.md5(database.read_bytes()).hexdigest()
    with sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True) as db:
        db.row_factory = sqlite3.Row
        for table in ("world_groups", "zone_groups"):
            for row in db.execute(f"SELECT id, name, x, y, w, h FROM {table} ORDER BY id"):
                name = row["name"]
                if row["w"] <= 0 or row["h"] <= 0:
                    continue
                if table == "world_groups":
                    if name not in WORLD_IMAGES:
                        continue
                    image_name, title, kind = WORLD_IMAGES[name]
                    key = "world" if kind == "world" else name
                else:
                    # Instance/Mirage/library maps occupy different runtime worlds.
                    if not name.startswith(("w_", "e_", "o_", "s_")) or name.startswith("o_library_"):
                        continue
                    image_name = ZONE_IMAGES.get(name, name)
                    title = ZONE_TITLES.get(name, name[2:].replace("_", " ").title())
                    kind, key = "zone", "zone-" + str(row["id"])
                source = texture_root / (image_name + ".dds")
                if not source.is_file():
                    missing.append(name)
                    continue
                with Image.open(source) as image:
                    # DDS header carries the real dimensions/stride. Do not force
                    # a square texture or reinterpret its compressed scanlines.
                    if image.size != (1856, 1112):
                        raise ValueError(f"Unexpected map dimensions: {source}: {image.size}")
                    image.convert("RGB").save(output / (key + ".jpg"), quality=95)
                maps.append({
                    "key": key, "title": title, "kind": kind, "world_id": 0,
                    "group_id": row["id"], "name": name,
                    "bounds": {"min_x": row["x"], "min_y": row["y"],
                               "max_x": row["x"] + row["w"], "max_y": row["y"] + row["h"]},
                    "image": key + ".jpg", "pixel_width": 1856, "pixel_height": 1112,
                    "source": "game/ui/map/world/en_us/" + image_name + ".dds",
                    "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                    "bounds_source": f"compact.sqlite3:{table}:{row['id']}",
                })
    if not any(m["key"] == "world" for m in maps):
        raise ValueError("The extracted main_world.dds is required")
    manifest = {"version": 1, "client": "1.2 r208022", "database_md5": digest,
                "projection": "x=east, y=north; image top-left=(min_x,max_y)",
                "maps": maps, "missing": missing}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {len(maps)} calibrated maps; missing artwork: {', '.join(missing) or 'none'}")
    print(f"Read-only compact.sqlite3 md5: {digest}; local output: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-root", type=Path, required=True, help="Extracted client root containing game/ui/map/world/en_us")
    parser.add_argument("--database", type=Path, default=Path("AAEmu.Game/Data/compact.sqlite3"))
    parser.add_argument("--output", type=Path, default=Path(".client_files/dashboard-map"))
    args = parser.parse_args()
    prepare(args.client_root, args.database, args.output)
