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

---

## 5. Beyond Solzreed Inter-Zone Highways (Levels 15–30)

Arterial highways connecting the Western Continent zones for bot travel and autonomous leveling progression:

* **Solzreed $\rightarrow$ Dewstone Plains** (`Data/Path/highway_solzreed_to_dewstone.path`):
  * **Distance:** 10.2 km, 402 waypoints (25m spacing).
  * **Path:** Wardton (`21800, 11500`) $\rightarrow$ Lilyut Crossing (`12600, 15350`).
* **Dewstone $\rightarrow$ Marianople** (`Data/Path/highway_dewstone_to_marianople.path`):
  * **Distance:** 4.0 km, 163 waypoints (25m spacing).
  * **Path:** Dewstone $\rightarrow$ Royster's Camp (`12850, 14450`) $\rightarrow$ Marianople City Gate (`10930, 12040`).

---

## 6. Runtime Obstacle Avoidance (`ObstacleManager`)

Static doodad obstacles (fences, stone walls, closed gates, towers, buildings) are loaded at server startup into `ObstacleManager`:
* **Spatial Hash Grid:** 100m cells for sub-microsecond point and line queries.
* **A\* Pathfinding Integration:** Hooked into `AiGeodataManager.CheckImpossibleWalk(Vector3 point)`. Any navmesh node falling inside an obstacle's keep-out cylinder is treated as impassable, forcing A* to route around it.
* **Catalogs Loaded:** `Data/Navigation/*_obstacles.json` (1,395 total placed obstacles across Solzreed, Dewstone, White Arden, and Marianople).

---

## 7. Generated Vector Heatmaps & Catalogs

* **Heatmaps (`.svg`):**
  * `solzreed_heatmap.svg` (4,570 NPCs, 15 checkpoints)
  * `dewstone_heatmap.svg` (2,745 NPCs, 7 checkpoints)
  * `white_arden_heatmap.svg` (948 NPCs, 13 checkpoints)
  * `marianople_heatmap.svg` (1,692 NPCs, 17 checkpoints)
* **Obstacle Catalogs (`.json`):**
  * `solzreed_obstacles.json` (709 obstacles)
  * `dewstone_obstacles.json` (327 obstacles)
  * `white_arden_obstacles.json` (107 obstacles)
  * `marianople_obstacles.json` (252 obstacles)
