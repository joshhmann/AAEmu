# Spawn-Z Data Defects — Classification + Correction (fix/npc-spawn-z)

**Author:** Tai (implementation: hx-coder, t_7abdc0ae) · **Date:** 2026-08-04
**Input:** Recon B (t_52ebb23f, `scorecard-explorations/npc-behavior.md`) — 99 anomalous
`npc_spawns` rows in Solzreed/Gweonid zones (142/129 + adjacent), terrain sampler
validated to 0.1–0.3 m against live ground truth.
**Deliverable:** corrected spawn-Z list — **15 rows snapped to terrain height** —
applied to `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json` via
`tools/data/npc-spawn-z-fix.py` (line-based, JSONC-safe, re-runnable, `--check` mode).
**Gate:** Rei (data semantics) — see §7. Fork branch only, no upstream PR.

---

## 1. Classification of the 99 rows (35 distinct NPCs)

Disposition key: **FIX** = snap Z to validated terrain height · **KEEP** = intentional, no change.

### FIX — true data-defect floaters (15 rows, 3 NPCs)

Non-flying (actor_models.movement_id=0 for all three models), open ground, elf-faction
(103), **no walkable navmesh surface at spawn Z** (cell 10_14 max node Z = 359.1 m),
no structure doodads at spawn height, 44.9–91.0 m above terrain.

| unit | name | rows | spawn Z range | terrain at pos | model | evidence |
|---|---|---|---|---|---|---|
| 569 | 에노이르 (elf) | 1 | 382.3 | 291.3 | 16 (movement_id=0) | +91.0 m over bare ground; single row |
| 3672 | 에오카드 (elf) | 13 | 379.9–382.3 | 292.5–328.4 | 16 (movement_id=0) | +51.5…+89.8 m, spread over 30×30 m; 4 rows carry stale `// солдат платформа` author comments (see §3) |
| 1904 | 아로라라 (daru) | 1 | 264.3 | 219.4 | 782 = `daru_pilot.chr` (movement_id=0, fly_mode=f) | +44.9 m; ONLY spawn row worldwide; 26 ground-level doodads around it, none at 264 m; recon misclassified as flyer |

### KEEP — flyers / sea (35 rows, 4 NPCs) — intentional

Server predicate `IsFlyOrSwim(modelId)` → `actor_models.movement_id == 2` (NpcManager.cs:135,
ModelManager.cs:58-65); flyers skip the spawn-Z snap entirely.

| unit | name | rows | diff | model | note |
|---|---|---|---|---|---|
| 1852 | 독수리 (eagle) | 22 | +4.0…+40.0 m | 412 (movement_id=2) | flying |
| 3451 | 육식성 말벌 (wasp) | 9 | +1.6…+12.5 m | 506 (movement_id=2) | flying |
| 8616 | 먼바다 가시부리새 (seabird) | 3 | +100.1 m | 416 (movement_id=2) | flying |
| 8563 | 큰 바다 벌레 (sea worm) | 1 | +69.2 m vs seabed | 404 (seabug.chr) | **at ocean level**: 62 rows worldwide ALL at Z=99.2; ocean_level=100.0 in all sampled cells — sea creature convention, keep |

### KEEP — cave / underground (12 rows, 5 NPCs) — intentional

Below-surface interiors; terrain sampler sees bare mountain surface only.

| unit | name | rows | diff |
|---|---|---|---|
| 1880 | 다후타 교단 신관 | 1 | −187.0 m |
| 1900 | 동굴 거미 | 4 | −88…−148 m |
| 5922 | 거미 군주 | 3 | −80…−117 m |
| 1881 | 미쳐버린 광부 | 3 | −113…−120 m |
| 6973 | 마법사 에르딜 | 1 | −120.4 m |

### KEEP — castle / structure floors (30 rows, 18 NPCs) — intentional

Consistent per-position offsets = structure floors/ramparts (terrain is bare ground;
it cannot see floors). Same pattern as recon class (a); includes 3 rows the recon
report omitted (3570, 8172×3 — same castle area).

| unit | name | rows | diff | note |
|---|---|---|---|---|
| 10320 | 정예 근위병 | 8 | +3.8…+4.7 m | castle (12720,16250) |
| 10636 | 공작 성 하인 엠마 | 1 | +4.7 m | castle |
| 12037 | 콜린 | 1 | +4.5 m | castle |
| 12038 | 방탕한 음유시인 | 1 | +4.7 m | castle |
| 12039 | 거리의 장미 | 2 | +4.2…+4.7 m | castle |
| 12040 | 밤의 요정 | 2 | +4.2…+4.6 m | castle |
| 5925 | 그라일렌트 | 1 | +4.8 m | castle |
| 5926 | 집사 이언스 | 1 | +4.6 m | castle |
| 6990 | 공작 부인 에스텔 | 1 | +4.2 m | castle |
| 6992 | 조이스 | 1 | +4.3 m | castle |
| 3615 | 론반 공작 | 1 | +4.1 m | castle (15560,13780) |
| 3616 | 엘렌 공주 | 1 | +3.5 m | castle |
| 3617 | 헤라온 | 1 | +3.9 m | castle |
| 3560 | 경비병 | 1 | +10.4 m | rampart |
| 8176 | 감시병 | 2 | +4.2…+10.3 m | rampart |
| 8771 | 초승달 왕좌 근위병 | 1 | +9.8 m | rampart |
| 3570 | 경매장 직원 | 1 | +6.2 m | castle auction house (recon omitted) |
| 8172 | 경비병 | 3 | +1.5…+3.1 m | castle steps (recon omitted) |

### KEEP — village structures, in-game flag (7 rows, 5 NPCs) — probable structure

Consistent small offsets at fixed positions in the Gweonid elf village; 1.5–3.6 m
matches building-floor offsets (castle precedent). Terrain/heightmap cannot see
porches; **Rei/Josh: confirm in-game** — if any of these visibly float, a follow-up
card snaps them.

| unit | name | rows | diff |
|---|---|---|---|
| 658 | 경매장 직원 (elf) | 3 | +3.2…+3.6 m |
| 576 | 네서틴 | 1 | +1.8 m |
| 3688 | 알리아 | 1 | +1.8 m |
| 6541 | 헤이스 | 1 | +1.7 m |
| 7733 | 티티라라 | 1 | +1.8 m |

---

## 2. The correction (15 rows, old → new Z)

All new Z = server-exact heightmap terrain at the row's (X, Y); matches the data
convention (control census: 2,169 normal rows sit at median **+0.04 m** vs terrain,
61.6 % within 0.05 m — the data convention IS terrain height, no offset).

| unit | X | Y | old Z | new Z | delta |
|---|---|---|---|---|---|
| 569 | 10570.89 | 14718.339 | 382.272 | 291.25 | −91.0 |
| 1904 | 10469.5 | 15106.7 | 264.3 | 219.40 | −44.9 |
| 3672 | 10576.83 | 14732.125 | 379.9 | 312.60 | −67.3 |
| 3672 | 10569.16 | 14719.196 | 382.3 | 294.10 | −88.2 |
| 3672 | 10572.4 | 14719.1 | 382.3 | 292.50 | −89.8 |
| 3672 | 10584.4 | 14714.0 | 379.9 | 314.75 | −65.1 |
| 3672 | 10556.1 | 14715.3 | 379.9 | 315.55 | −64.3 |
| 3672 | 10556.9 | 14722.3 | 379.9 | 315.20 | −64.7 |
| 3672 | 10558.9 | 14709.8 | 379.9 | 328.40 | −51.5 |
| 3672 | 10566.5 | 14712.2 | 382.272 | 298.50 | −83.8 |
| 3672 | 10578.2 | 14704.9 | 379.9 | 319.30 | −60.6 |
| 3672 | 10583.8 | 14726.2 | 379.9 | 314.85 | −65.0 |
| 3672 | 10569.84 | 14733.19 | 379.9 | 314.95 | −64.9 |
| 3672 | 10572.9 | 14712.1 | 382.272 | 293.75 | −88.5 |
| 3672 | 10560.7 | 14728.6 | 379.9 | 315.20 | −64.7 |

---

## 3. The `// солдат платформа` comments — why FIX anyway

4 of the 13 에오카드 rows carry the original author's comment `// солдат платформа`
("soldier platform"). Investigated as a possible intentional sky platform:

- **Doodads** at the float area: only a quest woodbox (`ndeco_quest_woodbox`, 2× at
  Z=381.8), a light (`gw_lighting_a_on`, Z=411.9), a crate (Z=321.5). No platform mesh.
- **Navmesh** (server's walkable-surface source, `.bai`): cell 10_14 max node Z =
  **359.1 m** (netmission 347.8) — no walkable surface exists at 380 m. If a walkable
  platform had existed in the original data, navmesh nodes would exist there.
- **Ground truth**: these are the NPCs Josh reported floating in-game.
- Conclusion: the comment refers to a sky prop cluster (or an intent that never
  materialized in the shipped navmesh/client statics); the rows float over bare
  terrain. FIX. (If Rei's in-game check ever shows a visible platform there, the
  rows can be reverted from this branch's diff — the old Z values are in §2.)

---

## 4. Verification — before / after

| metric | before | after |
|---|---|---|
| npc_spawns.json rows | 25,118 | 25,118 (unchanged) |
| spawn rows with \|diff\| ≥ 1.5 m vs terrain (target zones) | 99 | **84** (all KEEP-class: flyers/cave/castle/village-flag) |
| fixed rows vs terrain | +44.9…+91.0 m | **0.000 m (15/15)** — re-sampled on the box from the corrected file |
| file diff | — | byte-identical except the 15 `"Z"` lines (JSONC comments/trailing commas preserved) |
| md5 | 995e98274c54eb4d5db393f78f3de5b4 | 5f6e57efcf5a0df46914f90de49c2d1a |

Sampled rows after fix (box re-sampler, postfix_verify.py — live heightmaps):

```
unit 569  (10570.89, 14718.34) Z=291.25 terrain=291.25 diff=0.000
unit 3672 (10572.40, 14719.10) Z=292.50 terrain=292.50 diff=0.000
unit 3672 (10558.90, 14709.80) Z=328.40 terrain=328.40 diff=0.000
unit 1904 (10469.50, 15106.70) Z=219.40 terrain=219.40 diff=0.000
```

Terrain heights double-sourced: two independent box runs (recon census
`/tmp/npc_behavior_recon.json` + control run `/tmp/spawn_z_fix_control.json`) agree
on all 15 values; the sampler itself was validated to 0.1–0.3 m against 3 live
ground-truth points (recon B §6).

---

## 5. Deliverable-format note — why not `SQL/updates/`

The card asked for a SQL/updates script run against a copy of compact.sqlite3.
Investigation shows that form is N/A for this data:

- **compact.sqlite3 has NO spawn-position table.** All 679 tables checked; the only
  coord-bearing spawn-adjacent tables are `npc_spawners`/`npc_spawner_npcs` (spawner
  *groupings* — id/name/timers/weights, no X/Y/Z). Position columns exist only in
  `demo_locs`, `doodad_func_navi_mark_pos_to_maps`, `sub_zones`, `world_groups`,
  `zone_groups`.
- **Runtime spawn positions load from world JSON** (`SpawnManager.cs:328`:
  `Directory.GetFiles(worldPath, "npc_spawns*.json")`); rows are `{UnitId, Position}`.
- **`SQL/updates/` is the MySQL runtime-DB migration channel** (readme.txt: applied
  once at server start, logged as installed) — a spawn-Z file there would be a no-op
  at best and misleading at worst.
- Therefore the fix lands in the actual source of truth, `npc_spawns.json`
  (md5-identical between repo and box), and takes effect at the next data sync.
  `tools/data/npc-spawn-z-fix.py` is the re-runnable correction script with
  `--check` mode for the gate.

The npcs-table joins for classification (names/factions/models/actor_models) were
run against a **copy** of the box's compact.sqlite3 (119 MB, pulled to the card
workspace). The repo reference file (`AAEmu.Game/Data/compact.sqlite3`, a 0-byte
placeholder) was never modified.

---

## 6. Files changed (branch fix/npc-spawn-z)

- `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json` — 15 Z corrections
- `tools/data/npc-spawn-z-fix.py` — correction tool (FIX list embedded, line-based,
  `--check` verifies the committed file)
- `scorecard-explorations/spawn-z-fix.md` — this report

## 7. Rei gate (data semantics) + in-game confirm (Josh)

1. **Floaters fixed**: teleport to (10570.9, 14718.3) — 에노이르 and the 에오카드
   cluster should now stand on the ground in the elf village; 아로라라 at
   (10469.5, 15106.7) likewise.
2. **Village-flag KEEPs** (네서틴/알리아/헤이스/티티라라 +1.7…1.8 m; 경매장 직원 +3.2…
   3.6 m): porch vs float — if visibly floating, snap them in a follow-up card.
3. **Castle KEEPs** (공작/공주/근위병 +3.5…10.4 m): confirm they stand on floors/
   ramparts, not in air.
4. **Sky-prop question** (confirms the §3 call): does anything render at ~380 m
   above (10570,14718) in the client (woodbox prop at 381.8 is expected)?
5. Sea worm (8563) at Z=99.2 = ocean level (100.0) — confirm it swims at surface,
   not above it.
