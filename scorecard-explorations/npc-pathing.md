# NPC Pathing Recon — "enemies walking INTO hills" (server-side)

**Task:** t_93ee94fb (Recon A, split from t_350d36b1)
**Branch:** npc-pathing-recon
**Date:** 2026-08-04
**Scope:** evidence only, no code changes. Ground truth: joshhmann fork `develop` @ 0267fc77; live 1.2 data on aaemu box (CT 133) `game_pak` (24.9 GB, Feb 2023 build).

---

## 1. TL;DR

NPC movement is **straight-line XY interpolation with Z snapped to a height query at each 100 ms tick** — there is **no obstacle avoidance, no slope limit, and no collision** during chase or roam. Whether that produces "walking into hills" depends entirely on two data layers the server loads from the 1.2 client pak:

- **Navmesh (`.bai` netmission files)** — the only thing that gives NPCs any obstacle-aware routing. In the 1.2 pak it covers **~37 % of main_world path blocks (7,937 / 21,280)** and **NONE of the Solzreed → Bluemist corridor** (path blocks X 48–63 × Y 45–60 = 0 blocks).
- **Heightmap (`heightmap.dat`)** — exists for **1,205 / 1,330 main_world cells (91 %)**, including every cell on the Solzreed → Bluemist route. Z-follows-ground therefore *works* there, but only at the tick endpoints; the client interpolates straight chords between them.

**Result on the reported route:** A* pathfinding finds nothing → degrades to a straight line toward the target; Z is re-snapped to ground each tick, so on steep slopes the NPC climbs cliff faces at full speed (model clips into the hill geometry), and between ticks the client's linear interpolation dips the model below the surface on concave slopes. The player sees enemies walking *into* hills.

---

## 2. Movement/pathing code — how it actually works

### 2.1 Where movement is driven

| Concern | Code |
|---|---|
| AI tick (chase/roam) | `AIManager.Initialize()` — `TickManager.OnTick.Subscribe(Tick, 100 ms)` — `AAEmu.Game/Core/Managers/AIManager.cs:23` |
| Combat chase | `BaseCombatBehavior.MoveInRange()` — `Models/Game/AI/v2/Behaviors/BaseCombatBehavior.cs:29` |
| Roaming (patrol) | `RoamingBehavior.Tick()` → `MoveTowards(_targetRoamPosition, ...)` — `Models/Game/AI/v2/Behaviors/Common/RoamingBehavior.cs:46` |
| Scripted routes | `AiPathHandler.RunCurrentPath()` — `Models/Game/AI/v2/Controls/AiPathHandler.cs:43` (also ends in `Npc.MoveTowards`) |
| The move primitive | `Npc.MoveTowards()` — `Models/Game/NPChar/Npc.cs:1157` |
| The Z query | `WorldManager.GetReferenceHeight()` — `Core/Managers/World/WorldManager.cs:828` |
| The pathfinder | `PathNode.FindPath()` (A\* over navmesh nodes) — `Models/Game/AI/AStar/PathNode.cs:74` |

### 2.2 The move primitive — `Npc.MoveTowards` (Npc.cs:1157)

```
speed *= Ai.Owner.MoveSpeedMul                 // buff-scaled
travelDist = Min(distanceToTarget, speed)      // step = speed × tick delta
(newX, newY, newZ) = AddDistanceToFront(travelDist, targetDist, cur, other)   // STRAIGHT LINE in XY (and Z if target Z passed)
targetZ = WorldManager.GetReferenceHeight(Ai, newX, newY, newZ, ZoneId)       // Z OVERRIDDEN
Transform.Local.SetPosition(newX, newY, targetZ)
BroadcastPacket(SCOneUnitMovementPacket(...))  // position + velocity to clients
```

Key facts:
- **XY moves in a straight line toward the target** — `AddDistanceToFront` is a plain 2-D interpolation; nothing samples the terrain between current and next position, nothing tests whether the segment crosses a slope/obstacle.
- **Z is *snapped* (not lerped) to `GetReferenceHeight`** at the destination of each tick. Vertical movement is therefore unlimited — a 45°+, 60°+ or vertical cliff face is climbed in 0.5 m horizontal steps with no slowdown.
- **No collision, no slope check, no "can I step here" test anywhere** in the chase/roam path. The physics heightmap (`Physics/HeightMaps`, Jitter) is used for ships/slaves and the `/height` GM command, not for NPC foot movement.
- Move speed × 100 ms tick = **0.35–0.6 m per step** for typical mobs (BaseMoveSpeed ~3.5–6). Overshoot is *not* the mechanism; absence of avoidance is.

### 2.3 The Z query — `GetReferenceHeight` (WorldManager.cs:828) and `GetHeight` (757)

Fallback chain, in order:

1. Flying NPCs → spawner Z (840).
2. `HoldPositionBehavior` / `IdleBehavior` → spawner Z (847–853).
3. `GetHeight(zoneKey, x, y, z)`:
   - `GeoDataMode` on → `AiGeoDataManager.GetHeight(pos)` (WorldManager.cs:763–767):
     - nearest **navmesh node** Z from the cell's `.bai` data (`AiGeodataManager.cs:259–333`). Note: returns the **nearest node's** Z even if the node is far away (no distance cutoff) — a latent bug where navmesh exists but is sparse.
     - no nodes at all → raw heightmap fallback `GetRawHeightMapHeight` (AiGeodataManager.cs:317–321).
   - height == 0 and `HeightMapsEnable` → `WorldTemplate.GetHeight(x, y)` bilinear over the cell heightmap (WorldManager.cs:769–783, WorldTemplate.cs:174).
4. Still 0 → **spawner Z** (GetReferenceHeight 862–863) — i.e. the NPC keeps its spawn altitude while XY keeps moving.

### 2.4 The pathfinder — `PathNode.FindPath` (A\* over navmesh)

- Entry: `Npc.FindPath(abuser)` (Npc.cs:1323) → `Ai.PathNode.FindPath(world, start, goal)`.
- Snaps start/goal to nearest navmesh nodes (`FindСlosestToTheCurrent`, AiGeodataManager.cs:191), expands neighbors via `GetNeighbours` (PathNode.cs:187) using `world.Template.GetBaiByPos(...)` and `FindClosestNetMissionNode`.
- **If no navmesh node exists near the position → `GetNeighbours` returns empty → `FindPath` returns `[]`** (PathNode.cs:153).
- `Npc.FindPath` then does `resList.Add(target)` → `ReducePath` → `FoundPath = [target]` — a one-point "path" that is just the target.
- `BaseCombatBehavior.MoveInRange` (BaseCombatBehavior.cs:146–199):
  - `GeoDataMode` on **and** `FoundPath.Count > 0` → follow path points;
  - `FoundPath` empty or path point reached → **`MoveTowards(target, ...)` straight line** (lines 188–199).
- Net effect: **in any area without navmesh data, GeoDataMode chase is functionally identical to straight-line chase.**

### 2.5 Heightmap load paths

- Boot: `WorldManager.LoadHeightmaps()` (WorldManager.cs:646) logs "Loading heightmap of {world}" for **every** world template; with `PreLoadTerrain: false` (fork default, `Configurations/World.json:20`) it only *marks* cells — real loading is on demand (`WorldCell.VerifyCellLoaded()`, WorldCell.cs:113).
- Cell load: reads `game/worlds/<world>/cells/<cell>/client/terrain/heightmap.dat` (WorldCell.cs:135–142). **Missing file → `return true` with an all-zero `HeightMap` array — silent success** (WorldCell.cs:139–142). Zero-height cells then feed the `0 → spawner Z` fallback.
- Navmesh load: `WorldTemplate.LoadZoneBaiFiles()` (WorldTemplate.cs:217) looks in `game/worlds/<world>/zone/<zoneKey>/*.bai`; per-cell path blocks in `WorldCell.LoadBaiFiles()` (WorldCell.cs:93–106) look in `game/worlds/<world>/paths/<block>/*.bai` (BaseBaiLoader.cs:223–233, block size 256 m).

---

## 3. Terrain data inventory (live 1.2 pak, aaemu box)

Census done with a read-only pak FAT lister (AAPak TypeA, AES-128-CBC per-entry, key from `AAEmu.Commons/Utils/AAPak/AAPak.cs:41`; script `paklist.py` in the recon workspace).

| Layer | Files in pak (main_world) | Coverage |
|---|---|---|
| `heightmap.dat` | **1,205 cells** | 1,205 / 1,330 cells = **91 %** |
| `netmission*.bai` (navmesh graph) | **7,937 blocks** | 7,937 / 21,280 path blocks = **37 %** |
| `areasmission*.bai` (forbidden areas) | 7,937 blocks | same 37 % |
| `vertsmission*.bai` (vertex obstacles) | 7,937 blocks | same 37 % |
| `hidemission*.bai` | 7,184 blocks | ~34 % |
| `zone/<zoneKey>/*.bai` (zone-level navmesh) | **0 files** | none for main_world (instances *do* have them, e.g. boot log `LoadBaiFilesFromFolder 240/264/...`) |

Solzreed → Bluemist specifics (world.xml zone origins; `ToPathsIndex = floor(x/256)`):

- `w_solzreed_1` id=142 origin cell (12,13); `w_solzreed_2` id=178 and `w_solzreed_3` id=179 origin cell (14,13) — Bluemist strait area.
- Heightmap cells 012_011 … 016_015: **all present** (25/25 in the band; 42/42 in X12–17 × Y10–16).
- Navmesh path blocks X 48–63 × Y 45–60 (covers Solzreed village → Bluemist and the whole western approach): **0 of ~288 blocks present**. Nearest navmesh to the route: blocks 052_027/052_032/055_045/055_050 (≈ 4–7 km away).

Deployed server confirms the same picture at runtime:

```
WorldManager - Loading heightmap of main_world
WorldManager - PreLoadTerrain disabled, heightmaps for main_world will get loaded on demand.
BaseBaiLoader - LoadBaiFilesFromFolder 198   <- instances only (numeric zone folders)
BaseBaiLoader - LoadBaiFilesFromFolder 240
...                                            <- NO zone-level BAI line for main_world
```

(Deployed config: `HeightMapsEnable: true`, `World.GeoDataMode: true`, `PreLoadTerrain: false` — fork defaults.)

---

## 4. Hypotheses — verdicts with evidence

### (a) Chase movement ignores terrain — straight line, Y = spawn height — **PARTLY TRUE, refined**

XY is a straight line in all cases (Npc.cs:1209). Z is *not* ignored: it is re-snapped to `GetReferenceHeight` every tick (Npc.cs:1210–1211) — **but only at the tick endpoint**:
- where heightmap data exists (91 % of cells incl. the route): Z follows ground at endpoints → the NPC still walks *through* cliffs/steep slopes because nothing gates the step on slope or obstacles;
- where heightmap is missing (125 cells) or returns 0: `GetReferenceHeight` falls back to **spawner Z** (WorldManager.cs:862) → constant-altitude flight through hills — exactly "Y = spawn height".

### (b) Heightmap not loaded for world zones outside main_world — **FALSE as stated, TRUE as refined**

Heightmaps are loaded for **all** world templates at boot (WorldManager.cs:653) and on demand per cell. The real gaps: (i) 125 main_world cells lack `heightmap.dat` and load silently as all-zero (WorldCell.cs:139–142 → spawner-Z fallback); (ii) **navmesh** coverage is the actual desert — 0 % of the Solzreed corridor, 37 % of the map overall — which is what turns GeoDataMode chase into un-routed straight-line chase there. `GetBaiByPos` also has a `TODO: Pick the actually correct zone` (WorldTemplate.cs:238) that returns the *first* zone's loader whenever any zone BAI exists (instance worlds).

### (c) NPC move speed + tick interval overshoots obstacles — **NOT the mechanism**

Step length (speed × 100 ms tick ≈ 0.35–0.6 m) is far smaller than any hill; there is no obstacle *hit-test* at all, so "overshoot" never occurs — the step is simply never blocked. The `maxLoopsLeft = rawDistance*10 + 50` guard in A\* (PathNode.cs:102) also caps routing work, not movement.

### Root-cause statement

**No terrain-aware routing exists where navmesh data is absent (63 % of main_world incl. the reported route), and no slope/obstacle gate exists on the straight-line fallback — so NPCs chase through terrain geometry at full speed, with Z snapped to ground at tick endpoints (and to spawner Z where heightmap data is missing).**

---

## 5. Recommended fix shapes (design only — NOT implemented)

Ranked by value/effort. All server-side.

1. **Slope/step gate in `Npc.MoveTowards` (highest value, smallest change).** Before committing `(newX, newY)`, sample `GetReferenceHeight` at the destination (already done) *and* compare ΔZ vs ΔXY against a walkable gradient (e.g. max ~45° or configurable). If too steep: shorten the step (slide along the slope contour) or pick a perpendicular offset direction (local contour-follow), falling back to stopping. This kills "climbing cliff faces / clipping into hills" everywhere, with or without navmesh. Cost: one helper + one config knob; touches the single choke point all NPC movement flows through.
2. **Fix the silent-zero heightmap cell** (WorldCell.cs:139–142): log a warning and let the cell report "no data" so callers can distinguish *sea level / missing* — stops the spawner-Z flight mode for the 125 missing cells. Trivial.
3. **Nearest-node distance cutoff in `AiGeoDataManager.GetHeight`** (AiGeodataManager.cs:259–333): ignore navmesh nodes farther than ~a few meters and fall back to the raw heightmap, so Z queries in sparse-navmesh areas use real terrain. Small.
4. **Medium: heightmap-based local avoidance fallback.** When A\* returns empty (`PathNode.FindPath → []`), instead of pure straight line, sample a small ring of candidate next points on the heightmap and pick the one with (a) acceptable gradient and (b) minimum heading change toward target. Keeps NPCs off cliffs in the 63 % no-navmesh world without a full navmesh.
5. **Large / offline: generate navmesh for the missing blocks** from the existing 91 % heightmap (walkability mask = gradient + heightmap) so GeoDataMode A\* works map-wide. This is the "real" fix AAEmu upstream eventually needs, but it is a tooling + data pipeline effort, not a hotfix.
6. **Note — client interpolation is not server-fixable:** the chord-dip between 100 ms ticks is a client render artifact; 1–3 reduce how often it is visible, they cannot remove it.

---

## 6. Evidence appendix

- Code refs: `Models/Game/NPChar/Npc.cs:1157–1242` (MoveTowards), `Core/Managers/World/WorldManager.cs:757–864` (GetHeight/GetReferenceHeight), `Core/Managers/World/WorldManager.cs:646–663` (LoadHeightmaps), `Models/Game/AI/AStar/PathNode.cs:74–154` (A\*), `Models/Game/AI/v2/Behaviors/BaseCombatBehavior.cs:146–199` (chase routing), `Models/Game/World/WorldTemplate.cs:217–247` (BAI load + GetBaiByPos), `Models/Game/World/WorldCell.cs:93–142` (cell/path load), `Models/CryEngine/Loaders/BaseBaiLoader.cs:27–240` (BAI folder layout), `Models/Game/World/Xml/XmlWorld.cs:63` (HeightMaxCoefficient = 65535/(MaxTerrainHeight/4)), `Models/ClientData/NodeCell.cs:67–70` (height decode, 5 cm units — decode path is sound).
- Config: `AAEmu.Game/Configurations/World.json:19–20` — `GeoDataMode: true`, `PreLoadTerrain: false`; box `Config.json` `HeightMapsEnable: true`.
- Pak census: `/tmp/paklist.py` on aaemu box (read-only FAT lister); results: 1,205 heightmap cells, 7,937 netmission blocks, 0 zone-level `.bai` for main_world, Solzreed corridor 0 blocks.
- world.xml (extracted from pak): `main_world` 35×38 cells, zones w_solzreed_1/2/3 at cells (12,13)/(14,13).
- Runtime: `docker exec aaemu-game-1 grep ... /app/Logs/Server.log` — heightmap on-demand + instance-only zone BAI loads.
- Playtest report: Josh, 2026-08-04, Solzreed→Bluemist route (parent card t_350d36b1).

*Recon only — no code changed on this branch.*
