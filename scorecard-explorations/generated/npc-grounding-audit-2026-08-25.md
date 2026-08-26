# NPC Grounding Audit — main_world (2026-08-25)

Measurement workstream answer to prod observation: *"a lot of the NPCs are not
really grounded — some floating, some under roads and clipping."* READ-ONLY
diagnostics; nothing behavioral was changed. Repo state audited:
`develop` @ `bfbea4093` (== `origin/develop`; note: session brief said
`cb460f8e3+`, tree had moved on — all code citations below verified against
`bfbea4093`).

## 1. Method

**Offline harness** (no live stack needed): `/root/npc-grounding-harness/`
(uncommitted scratch, references `AAEmu.Game.csproj`).

1. Opens the runtime client data pak
   (`/root/aaemu-e2e/runtime/game-data/ClientData/game_pak`) via the engine's own
   `ClientFileManager`/AAPak reader.
2. Builds `main_world` `WorldTemplate` from `game/worlds/main_world/world.xml`
   exactly like `WorldManager.CreateWorldTemplate`
   (`AAEmu.Game/Core/Managers/World/WorldManager.cs:613-635`): 35×38 cells,
   MaxHeight 4096, OceanLevel 100, HeightMaxCoefficient 63.999.
3. For each cell, parses `game/worlds/main_world/cells/NNN_MMM/client/terrain/heightmap.dat`
   with the engine's own `Hmap`/`NodeCell` binary parser and replicates
   `WorldCell.LoadCellHeightMapFromClientData` node ordering + quantization
   (`AAEmu.Game/Models/Game/World/WorldCell.cs:135-190`, byte-identical math;
   the engine's copy of that method additionally touches DI singletons
   `WorldManager.Instance` at `WorldCell.cs:218`, which is why the harness
   re-implements the loop instead of calling it).
4. Ground height per spawn = `WorldTemplate.GetRawHeightMapHeight` /
   bilinear `GetHeight` (`WorldTemplate.cs:118-127,174-192`) — the same
   heightmap path `WorldManager.GetReferenceHeight` falls through to
   (`WorldManager.cs:825-847`).
5. `dz = z_spawn − z_ground` for every row of
   `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json`.

**Classification flags**: flyers/swimmers identified via the engine's own rule,
`ModelManager.IsFlyOrSwim` = `ActorModel.MovementId == 2`
(`AAEmu.Game/Core/Managers/ModelManager.cs:58-65`, `actor_models.movement_id=2`,
58 models), applied through `npcs.model_id` in compact.sqlite3 (SELECT-only).
Zone names from compact.sqlite3 `zones`; English names from
`localized_texts.tbl_name='npcs'`.

**Reference-model limitation (important)**: the harness measures against raw
*terrain* only. It cannot see roads/bridges/building meshes (those live in the
client scene, not in heightmap.dat) nor `.bai` navmesh geometry. Consequences:

- An NPC standing *on* a road mesh gets `dz≈0` here even though the server's
  GeoData path may disagree — "clipping into roads" is therefore **under-**
  counted by this audit, not over-counted.
- Cave/interior dwellers legitimately *below* the outdoor heightmap surface show
  up in the "submerged" bucket and must be treated as **suspect, not proven
  bugs** (see §5 classification).

## 2. Population audited

| Measure | Count |
|---|---|
| Rows in `npc_spawns.json` | **25 118** |
| Evaluated (has Z, heightmap cell present) | **24 587** |
| Skipped: missing `Z` field in JSON | 327 |
| Skipped: no heightmap cell / out of bounds (ground=0) | 204 |
| Flagged fly/swim via `movement_id=2` | 410 |
| Water-surface signature (ground < 80, z ∈ [85..130]) | 1 119 |
| **Defect-audited population** | **23 058** |
| Exact duplicate spawn rows (same unit+x+y+z) | **733 rows / 526 positions** |

## 3. dz distribution (defect-audited population)

| Bucket | Count | % |
|---|---|---|
| ok (\|dz\| < 0.5 m) | 20 646 | **89.54 %** |
| minor float (0.5 ≤ dz ≤ 2 m) | 857 | 3.72 % |
| **severe float (dz > 2 m)** | **1 295** | **5.62 %** |
| submerged/under-mesh (dz < −0.5 m) | 670 | 2.91 % |

Median |dz| is tiny (median dz = +0.056 over the full set) — the bulk of
placements are well-grounded. The defect mass is concentrated, not diffuse:
~8.5 % of audited spawns sit >2 m off the terrain or visibly below it.

Worst zones by severe-float count: `w_two_crowns_2` (159), `e_hasla_2` (95),
`e_mahadevi_2` (94), `e_lokas_checkers_2` (68), `e_ynystere_1` (65),
`w_long_sand_2` (56), `s_freedom_island` (55). Submerged concentrates in
`e_tiger_spine_mountains_1` (42), `o_abyss_gate` (41), `w_two_crowns_2` (32),
`w_lilyut_meadow_2` (30) — i.e. mountainous/cave terrain, consistent with
interior meshes under the heightmap.

## 4. Worst offenders (in-game check list)

Severe floaters, fly/swim-excluded (world coords = AAEmu world units):

```
dz=183.6  unit=12335 Citizen                    e_hasla_2        x=30011.74 y= 8709.93 z=538.70 ground=355.15
dz=183.5  unit=12336 Citizen                    e_hasla_2        x=30010.92 y= 8711.24 z=538.70 ground=355.25
dz=172.5  unit=12339 Maid                       e_hasla_2        x=29966.42 y= 8730.85 z=538.80 ground=366.28
dz=142.1  unit= 9631 Ravra                      e_hasla_1        x=28904.67 y= 7679.68 z=614.70 ground=472.59
dz=119.6  unit= 1243 Purple Falcon              e_lokas_checkers_2 x=24837.33 y=10710.22 z=788.87 ground=669.24
dz=116.3  unit= 1243 Purple Falcon              e_lokas_checkers_2 x=24763.62 y=10774.42 z=771.24 ground=654.93
dz=108.7  unit=12339 Maid                       e_hasla_2        x=29969.51 y= 8733.03 z=538.70 ground=429.97
dz=108.6  unit=12337 Citizen                    e_hasla_2        x=30071.53 y= 8779.54 z=538.60 ground=429.99
dz=108.4  unit= 1243 Purple Falcon              e_lokas_checkers_2 x=24839.68 y=10739.68 z=785.49 ground=677.12
dz=106.5  unit= 1243 Purple Falcon              e_lokas_checkers_2 x=24731.31 y=10848.79 z=787.99 ground=681.48
dz=100.5  unit= 8616 Ocean Razorbeak            s_freedom_island x=21726.39 y=17789.80 z=131.42 ground=30.88
dz=100.4  unit= 8616 Ocean Razorbeak            s_freedom_island x=19919.79 y=17763.24 z=142.30 ground=41.87
dz=100.3  unit= 8616 Ocean Razorbeak            s_silent_sea_1   x=14217.03 y=20343.00 z=164.99 ground=64.64
dz=100.3  unit= 1243 Purple Falcon              e_lokas_checkers_2 x=24943.17 y=10741.51 z=782.62 ground=682.30
dz=100.3  unit= 8616 Ocean Razorbeak            s_silent_sea_7   x=17021.00 y=13921.00 z=145.11 ground=44.82
```

Top submerged (cave/interior suspects):

```
dz=-270.3 unit= 2020 Striped Muzzle Kobold Miner w_white_forest_1 x= 8854.91 y=13568.44 z=320.76 ground=591.01
dz=-213.8 unit= 1880 Dahuta Cult Priestess       w_lilyut_meadow_2 x=11909.49 y=16410.58 z=292.90 ground=506.69
dz=-213.0 unit= 5754 Sayen                       e_falcony_plateau_1 x=22921.63 y=9498.92 z=527.20 ground=740.24
dz=-199.7 unit= 6991 Lord Colin's Henchman       w_lilyut_meadow_2 x=11921.64 y=16407.35 z=295.18 ground=494.84
dz=-186.4 unit=10537 Apprentice Wizard Valaren   w_white_forest_1 x= 8899.97 y=13533.10 z=317.66 ground=504.11
dz=-176.5 unit= 2504 Whitewing Astra             e_mahadevi_1     x=17749.13 y= 8757.61 z=253.44 ground=429.97
dz=-163.4 unit= 2023 Deshak the Cave Troll       w_white_forest_1 x= 8881.33 y=13523.22 z=319.48 ground=482.90
```

Recurring floater templates: Purple Falcon (61 spawns — an aerial mob whose
model is NOT flagged `movement_id=2`), Guard/Royal Guard/Sentry (city guards on
walls/gates), Two Crowns Noble/Townsperson (57 combined — the Two Crowns
harbor/town structures), Seabug/Jellyfish/Shark families (water mobs outside my
surface band). Full machine-readable top lists: `/tmp/ng_top.json`,
raw matrix `/tmp/ng.tsv`.

## 5. Root-cause classification

**(a) Source-data z error — CONFIRMED for specific clusters.**
Hasla Citizens/Maid all carry `z=538.x` while local terrain is 355–430 — a
single flat value copied across a slope, i.e. the canonical extraction froze an
elevation that matches no ground there. Same signature for Two Crowns nobles
(structure deck heights baked as if they were terrain). These are DATA-layer
errors in `npc_spawns.json`.

**(b) Structure-height mismatch — likely dominant for the remaining floaters.**
Guards/Sentries/Nobles cluster around towns/harbors where the true stand surface
is a mesh above terrain. The offline reference can't measure these; in-game
verification of §4 coordinates will split them cleanly (if the NPC renders
correctly in-game on a deck, it is a false positive of terrain-only auditing).

**(c) Missing/ineffective spawn-time clamp — SERVER defect, cited.**
`NpcSpawnerNpc.SpawnNpc` (`AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs:97-104`)
is the only spawn-time correction: it queries `GeoData.GetHeight` (nearest .bai
navmesh node/vertex — a sparse sample, not a true floor) and applies it **only
if `Math.Abs(spawnerZ − newZ) < 1f`**. Any error ≥ 1 m is deliberately kept.
Then `WorldManager.GetReferenceHeight`
(`AAEmu.Game/Core/Managers/World/WorldManager.cs:907-921`) returns the spawner Z
verbatim for flyers **and for Idle/HoldPosition AI** — the majority of placed
NPCs — so the data error is never corrected at runtime, and
`SCUnitStatePacket.cs:137-140` pushes exactly that height to clients. Net
effect: whatever z is in `npc_spawns.json` reaches the player unmodified unless
it happens to be within 1 m of a navmesh node.

**(d) Intentional floaters/swimmers — misclassified by current flags.**
The only flag is `movement_id=2` (58 models, 410 spawns). Aerial/water mobs not
covered by it (Purple Falcon, Ocean Razorbeak, Seabug/Jellyfish/shark families,
Skyfin appearing in the submerged list because they hover over valleys) account
for a large share of both extreme buckets. They need a whitelist, not a clamp.

**(e) Duplicate rows.** 733 byte-identical duplicate spawn rows (526 distinct
positions) double-spawn NPCs at the same spot — separate small DATA defect.

## 6. Remedy options (NOT implemented)

| Option | What | Tradeoffs |
|---|---|---|
| **A. Clamp-on-spawn (SERVER)** | In `SpawnNpc`, replace the `<1f` guard with an unconditional snap to `GetReferenceHeight` for non-flyer/non-swimmer templates, plus a max-step sanity cap (e.g. reject corrections > ~30 m and log). Also fix `GetReferenceHeight` branch 2 to clamp idle NPCs to terrain when |spawnerZ−terrain| is small. | Fixes every future/current spawn uniformly; no data edits. Risks teleporting legit structure-deck NPCs down to terrain (the Two Crowns/Guard cases) unless structure-aware sources (.bai GeoData loaded per cell) are used; needs a whitelist escape hatch. |
| **B. Data overlay patch (DATA)** | Regenerate/patch `npc_spawns.json` z values for the confirmed clusters (Hasla set, duplicated rows) from heightmap+bai re-extraction; ship as a patch step in the worlds data pipeline. | Precise, reviewable diffs; keeps server code honest. But must be re-done whenever canonical data is re-imported, and cannot know about road/deck meshes either — same blind spots as this audit. |
| **C. Intentional-floater whitelist** | Extend `ModelManager.IsFlyOrSwim` coverage (or add a template flag/table) covering known aerial/water models (Purple Falcon, Ocean Razorbeak, Seabug/Jelly/Shark, Skyfin…); exempt whitelisted NPCs from clamping and from grounding telemetry. | Cheap, removes most false positives from any automated fix; maintenance burden is model-list curation. |
| Recommended sequencing | C first (cheap, unblocks measurement truth), then A with conservative caps + logging, then B for the named data clusters (incl. de-duplicating the 733 rows). | — |

## 7. Reproduce

```bash
cd /root/npc-grounding-harness && dotnet run -c Release   # writes TSV to stdout
# analysis: python3 pass over the TSV joining compact.sqlite3 npcs/zones/localized_texts
```
Artifacts: `/tmp/ng.tsv` (per-spawn matrix), `/tmp/ng_top.json` (top offender
JSON), `/tmp/npc_grounding.tsv` (first full run). compact.sqlite3 accessed
SELECT-only; no game/server state touched; nothing pushed.
