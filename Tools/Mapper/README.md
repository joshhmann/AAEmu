# Dev Mapper & Navigation Toolchain

This toolkit provides an end-to-end mapping pipeline for ArcheAge: from in-game manual walk recording to offline map redlining, zone heatmaps, and doodad obstacle extraction.

---

## 1. In-Game Manual Walk Mode (`/mapper`)

The in-game dev mapper lets you walk through the game world naturally on a player or GM character. It automatically traces your steps, compacts straight lines, captures interactions, and saves game-ready routes.

### Commands
* **`/mapper walk <route_name>`**
  * Starts **Manual Walk Mode**.
  * Samples your position every $1.5\text{–}2.0\text{m}$ (or on turns $> 20^\circ$).
  * Automatically intercepts:
    * Doodad interactions (doors, workbenches, wells, harvestables)
    * NPC dialogs (talk, quest accept, turn-in)
    * Skill casts & combat pulls
* **`/mapper mark <label>`**
  * Drops a custom tagged landmark at your current position (e.g. `fence_corner`, `gate_entrance`, `ferry_wait`).
* **`/mapper stop`**
  * Finalizes the session and saves:
    * `Data/Routes/<name>.json` — Rich action graph with waypoints and interactions.
    * `Data/Path/<name>.path` — Standard pipe-delimited `|X|Y|Z|` route.
* **`/mapper list`**
  * Lists all saved routes with waypoint counts, action counts, and total distance.
* **`/mapper play <bot_name> <route_name>`**
  * Commands an active player bot to replay the exact path and actions sequentially.

---

## 2. Redline Map Tool (`redline_to_path.py`)

Converts a series of 2D coordinates (or redline waypoints drawn on a zone map) into continuous 3D game routes with ground $Z$ height estimation.

```bash
# Convert a sequence of coordinate waypoints into a .path and .json route:
python3 Tools/Mapper/redline_to_path.py \
  --name solzreed_highway \
  --points "20100,10500;20150,10520;20200,10560" \
  --spacing 2.0

# Or read coordinates from a text file:
python3 Tools/Mapper/redline_to_path.py \
  --name lakeside_trail \
  --file points.txt \
  --spacing 2.0
```

---

## 3. Zone NPC & Road Heatmap Generator (`generate_zone_heatmap.py`)

Plots 25,118 world NPC spawns and canonical carriage/transfer routes into a high-resolution 2D vector map (`.svg`). 
* Shows where natural roads, gates, and settlements (Crescent Throne, Wardton, Lacton) sit.
* Cyan clusters indicate NPC settlements; orange dots indicate carriage road checkpoints.

```bash
# Generate Solzreed Peninsula heatmap:
python3 Tools/Mapper/generate_zone_heatmap.py --zone solzreed --out Tools/Mapper/solzreed_heatmap.svg

# Or generate for any custom bounding box:
python3 Tools/Mapper/generate_zone_heatmap.py \
  --bounds "18000,8000,24000,14000" \
  --out custom_zone.svg
```

---

## 4. Doodad Obstacle Extractor (`extract_doodad_obstacles.py`)

Dissects `doodad_spawns.json` and `Doodads.xml` to identify physical obstacles and placed structures in any region:
* Fences, stone walls, and gates
* Doors, ladders, and stairs
* Workbenches, forges, and crafting stations
* Wells, signposts, and chests

```bash
# Extract all obstacles across Solzreed Peninsula:
python3 Tools/Mapper/extract_doodad_obstacles.py \
  --zone solzreed \
  --out Tools/Mapper/solzreed_obstacles.json

# Extract obstacles within a tight town bounding box (e.g. Wardton):
python3 Tools/Mapper/extract_doodad_obstacles.py \
  --zone wardton \
  --out Tools/Mapper/wardton_obstacles.json
```
