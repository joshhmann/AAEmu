# Spawn-Reachability Census — 2026-08-29

- **Date:** 2026-08-29
- **HEAD:** `1a84df839ddcf50e1ee7ebc9ec7e95d64f0e2e06` (branch `develop`)
- **Data sources (all READ-ONLY):**
  - `AAEmu.Game/Data/compact.sqlite3` — quest graph (`quest_components`/`quest_acts`/act-detail tables, `quest_contexts`, `zones`)
  - `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json` (7,023 distinct `UnitId` templates), `doodad_spawns.json` (2,298 distinct `UnitId` templates)
  - 17 instance world dirs under `AAEmu.Game/Data/Worlds/` (union: 560 NPC, 632 doodad templates); all-worlds union: 7,567 NPC / 2,909 doodad
- **Method:** perceivable quest set re-derived exactly as the discovery-channel census (Start components `component_kind_id=2` ⋈ `quest_acts` ⋈ act-detail tables, 7 accept channels incl. the new kill-accept channel), then each quest's objective-side references (Progress NPC/doodad refs + Report/Accept NPCs) intersected with the spawn template sets. Spawn JSONs are JSONC (comments + trailing commas) — ids extracted by regex on `"UnitId"`/`"DoodadId"` fields after comment stripping.
- **Related code:** `GameplayActor.DiscoverQuests` / `DiscoverSelfQuests`, `QuestManager.GetQuestsOfferedBy*`, `SphereQuestManager.GetQuestStartingSpheres`, `DoOnMonsterHuntEvents` (kill-accept).

## 1. Perceivable set (re-derived)

| Channel | Distinct quests |
|---|---|
| `QuestActConAcceptNpc` | 2,797 |
| `QuestActConAcceptDoodad` | 203 |
| `QuestActConAcceptItem` | 342 |
| `QuestActConAcceptItemGain` | 25 |
| `QuestActConAcceptSphere` | 431 |
| `QuestActConAcceptLevelUp` | 3 |
| `QuestActConAcceptNpcKill` | 381 |
| **Union (7 channels, detail-joined)** | **4,181** |

3,801 wired union + 380 kill-only (quest 3845 carries both a wired and a kill channel) — exact match with the discovery census. Query shape (per channel):

```sql
SELECT DISTINCT qc.quest_context_id
FROM quest_components qc
JOIN quest_acts qa ON qa.quest_component_id = qc.id
JOIN <act_detail_table> d ON d.id = qa.act_detail_id
WHERE qc.component_kind_id = 2 AND qa.act_detail_type = '<ActType>';
```

## 2. Reachability classification (main_world spawns)

Reference extraction (all Progress components `component_kind_id=4`):

- **Progress NPC refs:** `QuestActObjMonsterHunt.npc_id`, `QuestActObjTalk.npc_id`, `QuestActObjMonsterGroupHunt → quest_monster_npcs.npc_id`, `QuestActObjTalkNpcGroup → npc_group_members.npc_id`, `QuestActObjZoneNpcTalk.npc_id`
- **Progress doodad refs:** `QuestActObjItemGather.highlight_doodad_id`, `QuestActObjInteraction.doodad_id` + `highlight_doodad_id`
- **Report/accept NPCs:** `QuestActConReportNpc.npc_id`, `QuestActConAcceptNpc.npc_id` (any component kind)

| Class | Definition | All 4,181 | Wired 3,801 | Kill-only 380 |
|---|---|---|---|---|
| **a — fully reachable** | every referenced NPC/doodad template present in main_world spawns | **1,309** | 1,046 | 263 |
| **b — partially reachable** | some references spawned, some not | **202** | 160 | 42 |
| **c — unreachable** | no referenced NPC/doodad spawned | **187** | 121 | 66 |
| **d — no objective refs** | delivery/turn-in-only (no Progress NPC/doodad refs) | **2,483** | 2,474 | 9 |

Class-d sub-split (report/accept NPCs, the only spawn check that applies):

| Class-d sub | All 4,181 | Wired 3,801 |
|---|---|---|
| report/accept NPCs all spawned | 1,657 | 1,650 |
| report/accept NPCs partially spawned | 18 | 18 |
| report/accept NPCs none spawned | 158 | 158 |
| no report/accept NPC at all (item/doodad/sphere/level accept, auto-complete turn-in) | 650 | 648 |

Of the 650 no-report-NPC quests, **574 carry a `QuestActConAutoComplete` act** (they complete on objective fulfillment without a turn-in NPC — fully engine-completable once the objective is met); the remaining 76 are sphere-accepted quests (e.g. 2585/2587/2588/2607/2608/2610/2611/2613 in w_gweonid_forest_1) with no NPC turn-in — these complete via `LetItDone`/score or auto-complete paths.

## 3. Delivery-only vs progress-act split

| Metric | All 4,181 | Wired 3,801 | Kill-only 380 |
|---|---|---|---|
| Quests with ≥1 Progress act (`component_kind_id=4` with any act) | 3,075 | 2,703 | 372 |
| **Delivery-only** (no Progress act) | **1,106** | 1,098 | 8 |
| Progress quests with NPC/doodad refs | 1,698 | — | — |
| — of which **fully reachable (class a)** | **1,309** | — | — |
| Progress act but no spawn-checkable refs (item gather/use, craft, aggro, zone-kill, sphere, etc.) | 1,272 | — | — |
| — of which zone-act only (`QuestActObjZoneKill`/`QuestActObjZoneMonsterHunt`, 105 pure) | 106 | — | — |

Full-loop check: of the 1,309 class-a quests, **386** have both Progress NPC refs *and* a spawned `QuestActConReportNpc` — the complete accept → kill/talk → report cycle with every NPC on the ground. The rest of class-a either auto-complete, report to a doodad, or are item/sphere-objective quests whose NPC refs are all spawned.

## 4. Indun / instance caveat

Instance-zone quests (zone_id in the 23 instance/arche_mall zones: 108, 144, 168–170, 183–186, 189, 192, 194, 199, 202, 204, 207, 209–216): **206 perceivable quests** (157 wired, 49 kill-only).

| View | a | b | c | d |
|---|---|---|---|---|
| main_world spawns only | 2 | 37 | 96 | 71 |
| **any-world spawns** (main + 17 instance dirs) | **139** | 51 | 9 | 7 |

The main_world-only view badly understates instance quests: 96 of 206 look unreachable, but **139 are fully reachable once instance spawn files are counted** (e.g. library instances 209/210/211 = 111 quests, arche_mall = 46). The 9 still-unreachable even in the any-world view are genuine data gaps (referenced NPCs exist in no spawn file). Caveat: instance spawn files exist for all 17 instance dirs, but instance *availability* (whether the bot can enter/complete those instances) is a separate axis not assessed here.

## 5. Data-gap evidence

- **Class-c blockers:** 231 distinct NPC templates referenced by unreachable quests are absent from main_world spawns; 124 of those appear in instance spawn files (instance quests), **107 appear in no spawn file at all** (true data gap). Top missing: 8962 (3 quests), 9798 (3), 11195/12508/12509/13712/12962/14388/14375/14370/14376/14364 (2 each).
- **Class-c by zone:** w_bronze_rock_1 35, instance_library_2 33, instance_library_1 24, instance_library_3 24, o_shining_shore_1 17, e_sunny_wilderness_1 16, w_marianople_2 10, instance_training_camp 9.
- **Class-d none-spawned (158):** w_gweonid_forest_1 50, arche_mall 40, w_bronze_rock_1 29, e_sunny_wilderness_1 10 — these are quests whose accept/report NPCs are simply not placed in main_world.
- **Class-a by accept channel:** Npc 932, NpcKill 263, Doodad 82, Sphere 15, Item 15, ItemGain 2.
- **Top zones by fully-reachable count:** e_ynystere_1 78/151, w_long_sand_1 75/138, w_white_forest_1 66/125, w_the_carcass_1 62/114, e_sunrise_peninsula_1 60/118. (w_gweonid_forest_1 has 847 quests but only 65 fully reachable — the starter zone's quest graph is dense but its spawn coverage is the thinnest.)

## 6. Recommendations

1. **Bot content today — kill-accept + monster-hunt quests in main_world zones (263 fully reachable, engine-live).** The kill-accept channel (380 quests, 263 class-a) is the newest and best bot surface: `DoOnMonsterHuntEvents` auto-starts and credits kills with zero accept dispatch, and 372/380 have Progress acts. Pair with the 386 full-loop class-a quests (spawned accept NPC → spawned hunt/talk NPC → spawned report NPC) for a complete accept→progress→turn-in cycle. Best zones: e_ynystere_1, w_long_sand_1, w_white_forest_1, w_the_carcass_1, e_sunrise_peninsula_1 (60–78 fully-reachable quests each).
2. **Delivery-only quests are the largest completable pool (1,098 wired, 1,650 with all report/accept NPCs spawned) — but low-value bot content.** They are turn-in-only (no Progress act), mostly auto-complete (716 wired class-d carry `QuestActConAutoComplete`); they exercise the accept/report loop but not objective play. Good for soak coverage of the accept/turn-in path, not for leveling content.
3. **Instance quests are a data-complete but engine-gated frontier (139 fully reachable in any-world view, 96 look dead in main_world-only).** The spawn data exists; the blocker is instance availability/entry for the bot (library 111 quests, arche_mall 46, burntcastle_armory 9, training_camp 9). If instance entry is wired, this is the second-largest completable family.
4. **Data gap (not code gap): 107 NPC templates referenced by 187 class-c quests exist in no spawn file.** Fix = add spawns (e.g. w_bronze_rock_1 35 quests, o_shining_shore_1 17, e_sunny_wilderness_1 16, w_marianople_2 10). No engine change needed — the quests are otherwise wired.
5. **Class-b (202) is a mixed bag — 55 have all NPC refs spawned but some doodad refs missing, 9 the reverse.** These are one-spawn-row away from class-a; triage by adding the missing doodad/NPC spawns rather than treating them as blocked.
6. **Zone-act progress (106 quests, 105 pure) is not spawn-checkable by this method** — `QuestActObjZoneKill`/`QuestActObjZoneMonsterHunt` reference zones, not templates. They need a separate engine/zone-coverage audit before being claimed completable.

## Caveats

- Spawn presence ≠ spawn reachability: a template may be spawned only in a far corner, behind a gate, or in a zone the bot never visits. This census is template-set membership only.
- `QuestActObjAggro` progress (37 acts) is engine-broken regardless of spawns (see discovery census §5: `OnKill` never sets `Target` to the victim) — those quests are unreachable by code, not data.
- Doodad refs use `highlight_doodad_id`/`doodad_id` from the act tables; `QuestActObjItemGather` without a highlight doodad (gather-from-node) is not spawn-checkable here.
- No data files were modified; analysis was query-only against `compact.sqlite3` and read-only parses of the spawn JSONs.
