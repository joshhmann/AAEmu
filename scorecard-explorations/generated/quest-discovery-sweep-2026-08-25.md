# Quest-Discovery Zone Sweep — PB-002 second half (2026-08-25)

Workstream: OFFLINE data analysis · repo `joshhmann/AAEmu` develop @ `41ddb889a` (verified).
Question answered: **"for a bot of level N standing in zone Z, what could it DISCOVER within walking distance?"**
Method: read-only `sqlite3` SELECTs against `AAEmu.Game/Data/compact.sqlite3` (canonical 1.2, md5-checked by the census) + static spawn placements from `AAEmu.Game/Data/Worlds/main_world/{npc,doodad}_spawns.json`. No engine changes; scripts in `/tmp` (`zone_sweep.py`, `sweep2.py`, `sweep3.py`). All numbers below are VERIFIED unless graded otherwise.

## TL;DR headline numbers

| Metric | Value | Grade |
|---|---|---|
| Distinct quests offered through the two v1 discovery channels | **3,000** (2,797 NPC-only + 203 board-only; zero overlap) | VERIFIED |
| Offering NPC templates / board doodad templates | 1,750 / 183 | VERIFIED |
| Offer edges (quest × acceptor-template × Start component) | 3,078 NPC + 203 doodad | VERIFIED |
| Discoverable quests banded by availability level | 1–9: **477** · 10–19: **555** · 20–29: **744** · 30+: **1,216** | VERIFIED |
| Zone groups with ≥1 discoverable quest | 57 of 78 | VERIFIED |
| Dead zone groups (zero discoverable content) | **23** total — 8 land/sea playfields + 15 instances/temp groups | VERIFIED |
| Deepest continuous ladder for an autonomous loop | West arc Solzreed→Lilyut→Gweonid (L1→50, max stall 3); East arc Tiger Spine→Mahadevi→Sunrise Peninsula (single prereq chain of 45 quests L2→20) | VERIFIED |

Canonical cross-check: the earlier lane's "~2,797 quests / ~1,805 NPCs / 203 doodad-offered" reproduces exactly for quests and boards; I count 1,750 distinct offering NPC templates vs the canonical ~1,805 [INFERRED difference: canonical likely counted distinct npc ids across ALL ConAcceptNpc acts incl. non-Start components; the offer index itself filters to Start only].

## Methodology

### 1. Offer index (mirrors `QuestManager.GetQuestsOfferedByNpc/Doodad`)

```sql
-- NPC offers: Start components carrying ConAcceptNpc acts
SELECT qa.act_detail_id, qc.quest_context_id
FROM quest_acts qa
JOIN quest_components qc ON qc.id = qa.quest_component_id
WHERE qc.component_kind_id = 2                      -- QuestComponentKind.Start
  AND qa.act_detail_type = 'QuestActConAcceptNpc';
-- act_detail_id joins quest_act_con_accept_npcs.id -> npc_id (= npcs.id template)

-- Board offers: same shape with 'QuestActConAcceptDoodad'
--   -> quest_act_con_accept_doodads.id -> doodad_id (= Doodads.xml Creature Id)
```

Verified counts: 3,519 ConAcceptNpc acts total, 3,078 on Start components → 2,797 distinct quests, 131–1,750 templates depending on channel scope. `quest_components.npc_id` was NOT used (nearly always empty, per the landed primitive's finding).

### 2. Level gates (mirror of `UnitRequirementsGameData.CanComponentRun` / `UnitReqs` kind Level)

```sql
SELECT owner_id, kind_id, value1, value2 FROM unit_reqs WHERE owner_type = 'QuestComponent';
```

Kind 1 (Level) semantics: passes when `Level >= value1 && (value2 == 0 || Level <= value2)`; AND-composed across reqs unless `quest_components.or_unit_reqs` (then OR). Per offer edge the Start component's window `[lo, hi]` gives the earliest level the quest becomes discoverable; a quest is available from `min(lo)` over its Start components. Quests with no level gate fall back to `quest_contexts.LEVEL` (display level). Bands: 1–9 / 10–19 / 20–29 / 30+.

Non-level gates present on Start components (caveat flags, not modeled): kind 31 `CompleteQuestContext` (prereq chains — used in §4), kind 3 Race, 10 OwnItem, 36 ExceptCompleteQuestContext, 42 MotherFaction.

### 3. Zoning model (data-driven, NOT rect geometry)

Two candidate mappings were tested:

- **Rejected:** spatial containment of spawn coords in `zone_groups.x/y/w/h` rectangles — only 46.7% agreement with `quest_contexts.zone_id → zones.group_id` over 3,865 checkable offer edges (rects overlap heavily; sea catch-all rects swallow land). The exact runtime path (`WorldManager.GetZoneId` → `WorldTemplate.ZoneKeyByRegions`) needs client `game/worlds/*/world.xml` from a game_pak, which does not exist on this machine (searched).
- **Used (authoritative):** quest → zone via `quest_contexts.zone_id` (only 4/4,876 missing); acceptor template → zone via **modal zone of the quests it offers**. Purity: 1,664/1,750 offering NPCs map to a single non-zero zone; only 86 multi-zone (mode taken); 7 zero-only. This *is* the game's own notion of "quests of this zone", so it directly answers the discovery question.

Zone rollup to the 78 `zone_groups` via `zones.group_id`.

### 4. Distance metrics (walking feasibility)

Per zone group: all static spawn points (`main_world/npc_spawns.json`, `doodad_spawns.json`; comment-tolerant JSON parse) of its offering NPC/board templates, deduplicated per template. Reported:
- `NNmed` — median nearest-neighbour distance between offerer spawns (hub tightness);
- `NNmin` — closest pair (clustered hub exists);
- `maxNN` — worst isolated offerer's NN distance (isolation signal).
Units are world units (~meters). Caveat: straight-line Euclidean; no terrain/navmesh.

## Per-zone-group table (groups with ≥1 quest, sorted by size)

Bands = distinct quests by availability level. `NPC/p` = offering NPC templates with ≥1 main_world spawn / total. Boards likewise.

| gid | group | Q | 1–9 | 10–19 | 20–29 | 30+ | NPC/p | BRD/p | NNmed | NNmin | maxNN |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | w_gweonid_forest | 439 | 83 | 48 | 84 | 224 | 147/185 | 6/16 | 31.3 | 1.5 | 2471 |
| 17 | e_ynystere | 138 | 2 | 0 | 93 | 43 | 87/90 | 5/6 | 7.9 | 2.1 | 6861 |
| 9 | e_mahadevi | 122 | 0 | 114 | 1 | 7 | 84/84 | 4/6 | 20.8 | 2.0 | 1298 |
| 6 | w_lilyut_meadow | 108 | 41 | 29 | 33 | 5 | 52/52 | 5/5 | 11.4 | 1.6 | 959 |
| 4 | e_sunrise_peninsula | 106 | 0 | 83 | 15 | 8 | 64/64 | 9/9 | 15.6 | 2.0 | 533 |
| 27 | w_long_sand | 105 | 0 | 0 | 0 | 105 | 67/67 | 5/5 | 5.3 | 0.1 | 14162 |
| 2 | w_marianople | 101 | 2 | 0 | 90 | 9 | 57/57 | 7/11 | 12.5 | 1.7 | 193 |
| 18 | w_white_forest | 101 | 0 | 86 | 15 | 0 | 67/67 | 5/5 | 4.6 | 1.5 | 158 |
| 8 | w_two_crowns | 97 | 0 | 0 | 97 | 0 | 77/77 | 3/3 | 9.6 | 1.9 | 298 |
| 3 | w_garangdol_plains | 96 | 0 | 89 | 1 | 6 | 60/60 | 3/3 | 10.3 | 2.0 | 430 |
| 20 | w_cross_plains | 96 | 0 | 2 | 90 | 4 | 51/51 | 3/3 | 9.1 | 1.8 | 2910 |
| 25 | e_ancient_forest | 95 | 0 | 0 | 94 | 1 | 58/58 | 3/3 | 32.5 | 3.5 | 312 |
| 12 | e_singing_land | 94 | 0 | 0 | 91 | 3 | 66/66 | 2/2 | 5.9 | 1.4 | 459 |
| 24 | e_tiger_spine_mountains | 93 | 45 | 46 | 1 | 1 | 63/63 | 6/6 | 26.2 | 2.0 | 350 |
| 14 | e_steppe_belt | 90 | 0 | 0 | 0 | 90 | 50/50 | 5/6 | 5.2 | 2.0 | 2785 |
| 23 | e_hasla | 87 | 0 | 0 | 0 | 87 | 58/58 | 7/7 | 12.5 | 2.5 | 522 |
| 5 | w_solzreed | 80 | 66 | 1 | 0 | 13 | 54/54 | 6/8 | 18.2 | 1.7 | 1478 |
| 19 | w_the_carcass | 79 | 0 | 0 | 0 | 79 | 46/46 | 5/5 | 12.9 | 2.1 | 316 |
| 61 | o_shining_shore | 79 | 0 | 0 | 0 | 79 | 29/32 | 1/3 | 2.2 | 0.0 | 5746 |
| 26 | w_hell_swamp | 77 | 0 | 0 | 0 | 77 | 33/33 | 6/6 | 16.9 | 1.3 | 1102 |
| 7 | e_rainbow_field | 71 | 65 | 0 | 2 | 4 | 46/46 | 9/10 | 12.7 | 1.7 | 314 |
| 15 | e_ruins_of_hariharalaya | 71 | 0 | 0 | 0 | 71 | 48/48 | 4/4 | 18.8 | 2.7 | 304 |
| 22 | w_golden_plains | 71 | 0 | 0 | 30 | 41 | 38/38 | 8/8 | 29.2 | 3.4 | 492 |
| 11 | e_falcony_plateau | 69 | 68 | 0 | 0 | 1 | 42/43 | 4/6 | 13.4 | 2.2 | 204 |
| 16 | e_lokas_checkers | 68 | 0 | 0 | 0 | 68 | 48/48 | 3/3 | 22.5 | 3.6 | 395 |
| 10 | w_bronze_rock | 57 | 57 | 0 | 0 | 0 | **0**/47 | 0/1 | – | – | – |
| 21 | w_cradle_of_genesis | 56 | 9 | 47 | 0 | 0 | 35/39 | 1/1 | 14.2 | 2.9 | 519 |
| 49 | arche_mall | 45 | 16 | 6 | 4 | 19 | 1/34 | 0/0 | 7075 | 7075 | 7075 |
| 54 | o_abyss_gate | 27 | 0 | 0 | 0 | 27 | 7/8 | 0/7 | 296 | 3.3 | 5742 |
| 13 | e_sunny_wilderness | 24 | 23 | 1 | 0 | 0 | **0**/11 | 0/9 | – | – | – |

(remaining 28 groups have <25 quests each — instances, ocean groups, Aurumora fields; full data in `/tmp/sweep_final.json`.)

Notable anomalies: **w_bronze_rock** and **e_sunny_wilderness** have 57/24 quests whose offering templates carry ZERO main_world spawns in this data (unreachable offers — likely instance-world or event NPCs); **arche_mall** has 34 offering templates but only 1 placed. These count as content-black-holes despite non-zero quest numbers.

## Dead zones (progression dead-ends)

Zero discoverable content (23 groups): land/sea playfields **w_barren_land, s_lost_island, w_dark_side_of_the_moon, e_una_basin, s_nightmare_coast, s_golden_sea, s_crescent_sea**, placeholder **locked_sea_temp, locked_land_temp**, plus 13 instance/temp groups (hadir_farm, prologue, nachashgar×2, howling_abyss_2, immortal_isle, violent_maelstrom, library bosses/TD, training_camp, e_white_island).

Interpretation [INFERRED]: none of these are low-level leveling zones — a bot following either recommended arc below never enters them; the dead groups are endgame/PvP sea areas and instances where questing isn't the intended loop.

## Recommendations — autonomous-leveling bot loops

### Loop A (best overall): West Nuia starter arc — Solzreed → Lilyut Meadow → Gweonid Forest
- Coverage: L1→~35 continuous from normal quests (Solzreed 66 quests @1–9; Lilyut 41+29+33 across 1–29; Gweonid 83+48+84 up to ~35 plus repeatables beyond); Gweonid alone has the deepest bench in the game (439 quests, all bands, longest stall between new-content levels = 3).
- Verified chain seeds (ids from `unit_reqs` kind-31 prereq graph):
  - Solzreed golden route (already census-PASS, M2b pilot): `254→255→256→257→259→260→261` @L2–3, then `266→354→4292→4294→4295`.
  - Lilyut chains: `1600→1604→1608` @L7–9; `1595→1598→1599→4121` @L7–8.
  - Gweonid chains: `4415→4417→4424→4438/4439→5309→4779` @L7–10; `84→96→94→88→80` @L14–19.
- Hub density: NNmed ≤31 u in all three; boards present (6 in Gweonid).
- This extends the existing curated slice into a self-sustaining loop using only the landed DiscoverQuests surface.

### Loop B: East Haranya story spine — Tiger Spine Mountains → Mahadevi → Sunrise Peninsula
- A single verified prereq chain of **45 quests running L2→L20**: `1574→1222→2300→3435→3436→3437→2309→2315→3438→3439→3440→1366→6352→2326→2328→3442→3443→3444→3445→1254→1257→1572→3446→3447→3448→3449→2423→2424→2425→3451→3452→3453→3454→3455→3456→3491→3457→3459→3460→3461→3462→3463→3464→3466→(3450, 3467)`, crossing zone names 호랑이 등뼈 산맥 (Tiger Spine) → 마하데비 (Mahadevi) → 동틀녘 반도 (Sunrise Peninsula) in `quest_names`.
- Band support: Tiger Spine 91 quests @1–19; Mahadevi 114 @10–19; Sunrise 83 @10–19 + 15 @20–29. Every offerer template has spawns (63/63, 84/84, 64/64).
- Best choice if the bot starts on the eastern continent.

### Loop C (mid-game continuation): Two Crowns → Cross Plains → Ynystrye
- w_two_crowns: 97 quests ALL @20–29 (stall=1 — densest mid-band block in the data);
  chains `597→555→646` and `597→555→649` @L28–29.
- w_cross_plains adds 90 @20–29 + 4 @30+, e_ynystere 93 @20–29 + 43 @30+ (NNmed 7.9, tightest large hub).
- Feeds naturally out of Loop B at ~L20 or Loop A at ~L25.

## Graded caveats — what static-spawn analysis cannot see

1. **Runtime phasing / spawn-time state** (UNKNOWN by construction): indun phasing, quest-stage-gated NPC visibility, `game_schedule_quests` time windows, and weather/climate gating are invisible to static JSON+SQL. A bot may find fewer live offers at runtime than this table promises — the reverse (more) cannot happen through these channels since DiscoverQuests re-filters through the real AddQuest gate.
2. **Repeatables/dailies**: only 88 quests are flagged REPEATABLE in `quest_contexts` — but daily-reset hubs (the big same-level clusters like Gweonid@50 ×89, Mahadevi chains) suggest many "once-per-reset" offers that static rows don't distinguish. UNKNOWN until runtime observation.
3. **Offer channels outside the v1 discovery surface** (VERIFIED counts, Start-component acts not covered by DiscoverQuests today): `QuestActConAcceptSphere` 455 acts/431 quests (sphere-entry offers), `QuestActConAcceptItem` 342 quests (item-gated), `QuestActConAcceptComponent` 191, `QuestActConAcceptItemGain` 72 acts/25 quests, `QuestActConAcceptLevelUp` 3. AddQuest accepts these; DiscoverQuests currently surfaces only NPC/doodad carriers. That is ~900+ quests invisible to the bot's perception that the engine would still accept.
4. **No client world data offline** (VERIFIED absence): `game_pak` / `game/worlds/*/world.xml` not present on this host, so exact `GetZoneId` region resolution could not be replicated; `zone_groups` rects proven unreliable (46.7% agreement). If a client install lands, re-running the sweep with true region zoning would tighten zone attribution — the data-driven modal-zone model should track it closely (it IS the quest table's own zoning).
5. **Names sparse**: `quest_names` covers only 379/4,876 quests (KR 1.2 client strings). Chains above cite raw ids; human-readable naming must come from client localization files later.
6. **Objective-side reachability not swept**: this half answers "can I discover/accept"; whether the objective targets (hunt NPCs, gather doodads) sit within walking distance is the natural next sweep (tables `quest_act_obj_monster_hunts`, `quest_act_obj_item_gathers` + spawn join already identified).
7. M1 census runnable predicate reuse: the census harness (`QuestDataCensusTests`, runnability.md) proves structural quest runnability (component chains), not spatial discoverability; only its Start-component gate semantics were reusable here [INFERRED]. No census code was modified.

## Reproducibility

Scripts: `/tmp/zone_sweep.py` (offer index + placement prototype), `/tmp/sweep2.py` (rect-model rejection + validation), `/tmp/sweep3.py` (final model; emits `/tmp/sweep_final.json` consumed for every table above). DB opened `mode=ro` throughout; compact.sqlite3 untouched.
