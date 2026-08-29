# PB-005 Residual Decisions — Evidence Package (2026-08-29)

**Scope:** read-only evidence for PB-005 residual decisions (duplicate spawn rows;
cave/deck/submerged classification). No data or code changed.

**Repo:** `origin/develop` @ `22e02d3d2a98f81a02f15976bcc11b488b142abf` (clean worktree).
**Data sources (SELECT-only):**
- canonical `compact.sqlite3` — md5 `78b3bdbf038db3b927056106efdf91af`
  (`/root/aaemu-e2e/runtime/game-data/Data/compact.sqlite3`; same md5 cited in
  `scorecard-explorations/band-41-50-ltd-triage-wi6.md` and `m2b-e2e-metrics.md`)
- `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json` (md5 `5f6e57efcf5a0df46914f90de49c2d1a`,
  byte-identical to the file audited at `bfbea4093`)
- client `game_pak` heightmaps via the existing offline harness
  (`/root/npc-grounding-harness`, engine-identical Hmap parse + bilinear
  `WorldTemplate.GetHeight` math)
- prior audit: `scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md`

**Method:** re-ran the harness (25,118 spawn rows → per-row TSV), joined to
`npcs`/`zones`/`localized_texts`/`actor_models` in compact.sqlite3, and
re-derived every audit population count. All joins SELECT-only.

## 0. Population reconciliation vs 2026-08-25 audit

| Measure | Audit (08-25) | This run | Delta |
|---|---|---|---|
| Rows in `npc_spawns.json` | 25 118 | 25 118 | 0 |
| Evaluated (has Z + heightmap) | 24 587 | 24 587 | 0 |
| Missing `Z` field | 327 | 327 | 0 |
| No heightmap cell (ground=0) | 204 | 530 | +326 (overlap with missing-Z; audit double-counted) |
| Flagged fly/swim (`movement_id=2`) | 410 | 410 | 0 |
| Water-surface signature (ground<80, z∈[85..130]) | 1 119 | 709 | −410 (= flyswim count; audit's 1 119 = 709 + 410, a double-subtract artifact) |
| Defect-audited population | 23 058 | 23 468 | +410 (consistent with ws-band correction) |
| ok (\|dz\|<0.5) | 20 646 | 20 645 | −1 (boundary row dz=−0.5 exactly) |
| minor float (0.5≤dz≤2) | 857 | 857 | 0 |
| severe float (dz>2) | 1 295 | 1 295 | 0 |
| submerged (dz<−0.5) | 670 | 670 | 0 |

The audit's own bucket rows sum to 23 468 — matching this run — so its stated
"23 058 / 1 119" figures are the artifact, not the buckets. Boundary row:
unit 2021 (w_white_forest_1), dz = −0.5 exactly, sits on the ok/submerged edge.

## 1. Exact duplicate-row analysis

**Definition reproduced from the audit:** same `unit_id` + `x` + `y` + `z`
rounded to 2 decimals (the audit's "same unit+x+y+z" key).

| Measure | Count |
|---|---|
| Duplicate positions | **526** |
| Duplicate rows (total) | **1 259** |
| Extra rows (n−1) | **733** |
| Multiplicity distribution | 2×: 416, 3×: 65, 4×: 24, 5×: 11, 8×: 9, 9×: 1 |

**Exact vs near:** 525 of 526 positions are **exact duplicates** — rows
byte-identical in full JSON precision (x, y, z, and yaw all equal; every
duplicate row has yaw=0). One position is a **near-duplicate** merged only by
2-decimal rounding: unit 8036 Bone Grub at (22129.8, 12518.811, 212.259) vs
(22129.8, 12518.8105, 212.259) — identical after float32 conversion, i.e. the
two rows are the same spawn under the engine's own float32 read. So: **525
exact + 1 float32-identical near-duplicate; 0 true near-duplicates.**

**Identity (top templates by rows):** Seabug Pupa 522 (261 positions ×2 — all
522 rows are duplicates), Ocean Razorbeak 60, Nymph 55, Undead Boatswain 50,
Banshee 46, Young Jellyfish 45, Dominated Firran 35, Roaming Wood Elemental 34,
Gazelle 32, Nightmare Infantry 32, Worker Ant 25, Deep Ocean Eel 21.

**Identity (top zones by rows):** s_silent_sea_1 128, s_lostway_sea 128,
w_solzreed_1 104, s_silent_sea_2 98, s_golden_sea_1 72, s_lost_island 72,
e_rainbow_field_2 57, s_silent_sea_6 54, w_solzreed_3 50, s_freedom_island 49.
328 of 526 positions (681 rows) are in `s_*` sea zones; 198 positions (578 rows)
on land.

**Layout evidence (append artifact, not authored content):**
- 488 of 526 groups are **adjacent** in the JSON (duplicate rows sit back-to-back).
- 38 groups are non-adjacent; 37 of those are short interleaved spreads (2–46
  rows apart) — e.g. the Ocean Razorbeak block at indices 14533–14577 where
  duplicate pairs alternate with unique rows (a systematic double-append).
- 1 group is split across the file: unit 4210 Schima Rogue at (23815.771,
  9363.847, 564.9305) appears at index 1020 **and** 3 more times at indices
  25109–25117 (the file tail) — the tail copy is a re-append of an earlier row.
- All 526 groups have identical zone_key within the group; no group mixes zones.

**Ownership options (nothing deleted; decision needed):**
- **O1 — Data de-dup (recommended):** remove the 733 extra rows from
  `npc_spawns.json` (keep one row per position). Precise, reviewable diff;
  must be re-applied on data re-import. Risk: none for exact dups; the 8036
  float32 pair must be collapsed to one row too.
- **O2 — Server-side dedup:** dedupe at `SpawnManager.AddNpcSpawner` load time
  (key on unit+position). Fixes every future import without data edits; adds
  load-time logic and hides the data defect.
- **O3 — Keep + document:** treat duplicates as intentional population
  multipliers (some spawners legitimately double-spawn). Evidence against:
  yaw=0 everywhere, adjacency pattern, and the file-tail re-append of unit 4210
  all indicate an extraction artifact, not authored intent. If kept, the
  Seabug Pupa 522-row block (a water mob, `movement_id=0`) still needs the
  intentional-floater whitelist from the audit's remedy C.

## 2. Cave / deck / submerged classification

Terrain-only sampling **can** classify water-dwellers and flat-z data errors,
**cannot** see cave interiors or structure decks (roads/bridges/building meshes
live in the client scene, not heightmap.dat). Per-row-class verdicts below;
per-row detail is in the tables at the end.

### 2a. Submerged (dz < −0.5, defect-audited population: 670 rows)

| Class | Rows | Verdict | Source evidence |
|---|---|---|---|
| Sea-zone water-dweller (zone `s_*`, z < ocean level 100) | 78 | **VERIFIED** | zone_group `fishing_sea_loot_pack_id` set (sea), z below OceanLevel=100, templates are Seabug/Jellyfish/Shark families; dz −33.5..−0.5 (median −1.08) |
| Deep non-sea (dz < −10) | 158 | **INFERRED cave/interior** | terrain-only cannot see cave meshes; templates (Cave Bat, Kobold Miner, Wisp, Bone Grub) and zones (e_tiger_spine_mountains_1 31, w_two_crowns_2 22, e_hasla_1 19, w_white_forest_1 7) are cave/interior-typical; top rows reproduce the audit's list exactly (dz −270.3 Striped Muzzle Kobold Miner …) |
| Non-sea, name-flagged water/aerial (Skyfin, Seabug, Jellyfish…) | 33 | **INFERRED** | name-based only; `movement_id=0` (not flagged fly/swim) |
| Shallow non-sea (−10 ≤ dz < −0.5) | 400 | **UNKNOWN** | mixed: terrain noise, water-edge, or interior floor; 349 of 400 are −2..−0.5 (sub-meter, plausibly noise); 51 are −10..−2. o_abyss_gate (41 rows, dz −8.3..−0.5, z 323–543) is a pit zone — likely interior, unverifiable offline |
| Sea-zone above ocean (z ≥ 100) | 1 | **UNKNOWN** | Gatekeeper Priest Marquad, s_silent_sea_7, z=193.5, dz=−0.63 — borderline |

### 2b. Severe float (dz > 2, defect-audited population: 1 295 rows)

| Class | Rows | Verdict | Source evidence |
|---|---|---|---|
| Flat-z data error (Hasla Citizens/Maids, z=538.x vs terrain 355–520) | 26 | **VERIFIED** | single z value copied across a slope; terrain range 355.1–520.7 at those coords; audit §5(a) CONFIRMED |
| Aerial/water by template name (Purple Falcon, Ocean Razorbeak, Jellyfish, sharks, hawks, harpies, Skyfin…) | 456 | **INFERRED** | name-based; `movement_id=0` so not flagged; includes 61 Purple Falcon, 56 Ocean Razorbeak, 44 Jellyfish |
| Structure-deck by template+zone (Guard/Sentry/Royal Guard 127, Two Crowns Noble/Townsperson 79 at z=242.8 vs terrain 232–239, Salphira Disciple 20 at z=242.2 vs terrain 228, Mahadevi city NPCs…) | 436 | **INFERRED** | city/harbor zones; deck height baked as terrain-like constant (Two Crowns z=242.8 ×74 rows); terrain-only cannot confirm |
| Remaining (Monstrous Mimic 27, ruins/harbor NPCs, misc) | 377 | **UNKNOWN** | needs in-game check or .bai GeoData; 217 of 377 are dz 2–10 (small), 100 are 10–30, 59 are 30–100, 1 >100 |

**Terrain-only sampling verdict:** it classifies water-dwellers (zone+ocean
level) and flat-z data errors (z-constancy across slope) with **VERIFIED**
confidence; cave/interior and structure-deck classes are **INFERRED** (strong
zone/template/z-constancy priors, no mesh visibility); 400 submerged + 377
severe rows (≈ 58 % of submerged, 29 % of severe) remain **UNKNOWN** offline and
need in-game verification or `.bai` navmesh sampling.

## 3. Decision input

### D1 — Duplicate rows (733 extra rows / 526 positions)
- **Option A (recommended):** data de-dup — remove 733 extra rows from
  `npc_spawns.json` (keep 1 per position; collapse the 8036 float32 pair).
  Precise diff; re-apply on re-import.
- **Option B:** server-side dedup in `SpawnManager.AddNpcSpawner` (unit+position
  key). No data edits; hides defect; load-time cost.
- **Option C:** keep as intentional multipliers. Weakest fit to evidence
  (yaw=0, adjacency, tail re-append of unit 4210).

### D2 — Cave/deck/submerged disposition
- **D2a (recommended):** adopt the audit's remedy C first — extend
  `ModelManager.IsFlyOrSwim` (or a template whitelist) to cover the 456
  name-flagged aerial/water severe rows + 33 submerged rows + the 78 VERIFIED
  water-dwellers; exempt from clamping and grounding telemetry.
- **D2b:** clamp-on-spawn (audit remedy A) with a conservative cap (e.g. reject
  corrections > 30 m) — safe for the 26 VERIFIED flat-z rows; must NOT touch
  INFERRED deck rows (Two Crowns/Guards) without a whitelist.
- **D2c:** data overlay patch (audit remedy B) for the VERIFIED Hasla cluster
  and the duplicate rows; INFERRED/UNKNOWN rows (836 of 1 965 defect rows)
  require in-game verification before any automated fix.

### D3 — Measurement truth
- Re-run grounding telemetry with the corrected ws-band (709, not 1 119) and
  the strict dz<−0.5 boundary (1 row at dz=−0.5 exactly, unit 2021).

## 4. Unclassifiable data (offline)

- 400 shallow non-sea submerged rows (51 of them dz −10..−2) — need in-game or
  `.bai` evidence.
- 377 severe-float rows (Monstrous Mimic 27, ruins/harbor NPCs, misc) — need
  in-game or `.bai` evidence.
- 1 sea-zone row above ocean level (Gatekeeper Priest Marquad).
- 327 rows with missing `Z` and 530 rows with no heightmap cell (204 of them
  also missing Z) were never evaluated — out of scope for grounding, but the
  missing-Z rows are a separate data-completeness question.

## 5. Reproduce

```bash
cd /root/npc-grounding-harness && dotnet run -c Release --no-build > /tmp/ng.tsv
# analysis: python3 pass joining /tmp/ng.tsv to compact.sqlite3
#   (npcs, zones, localized_texts tbl_name='npcs', actor_models, zone_groups)
```
compact.sqlite3 accessed SELECT-only; no game/server state touched; nothing pushed.
