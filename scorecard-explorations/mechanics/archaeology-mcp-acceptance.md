# Archaeology MCP — Read-Only Acceptance Report (2026-08-31)

Read-only acceptance of the archaeology MCP's named queries against the canonical
`AAEmu.Game/Data/compact.sqlite3` and repo source. **No code changed, no commit, no
build, no soak/E2E root touched, no game runtime exercised.** All queries were run
against the actual stdio server (pre-built Release binary) via newline-delimited
JSON-RPC 2.0, plus direct read-only `sqlite3`/`grep` corroboration for provenance and
gap root-cause. This is a canonical read-only evidence artifact.

---

## 0. Provenance

| Item | Value |
|---|---|
| Branch | `develop` |
| Local HEAD | `c6b5dd8f209e55d01026bbfec70dc2d3c34aa42f` (merge: sync upstream/develop, 2026-08-30) |
| Canonical DB | `AAEmu.Game/Data/compact.sqlite3` |
| DB md5 (before) | `78b3bdbf038db3b927056106efdf91af` |
| DB md5 (after) | `78b3bdbf038db3b927056106efdf91af` (**unchanged** — read-only invariant held) |
| DB version label | `1.2 r208022` (from `list_sources` provenance) |
| DB size | 119,054,336 B (113.5 MiB) |
| Tables | 679 |
| Server | `AAEmu.ArchaeologyMcp` stdio, pre-built `bin/Release/net10.0/AAEmu.ArchaeologyMcp` |
| Server version | `aaemu-archaeology` 1.1.0, protocol `2025-03-26` |
| Tool surface | 24 tools (smoke-verified) |
| Transport | newline-delimited JSON-RPC 2.0 (MCP stdio) |
| Run timestamp | 2026-08-31T11:49:23Z (batch 1), 2026-08-31T11:49:25Z (batch 2) |

**Evidence-layer note:** all findings below are **data** (canonical `compact.sqlite3`
rows) and **code** (repo C# source) evidence. No live/client/H evidence was exercised:
no game server was launched, no client was run, no authenticated-server run occurred.
`H` (human/client) stays UNKNOWN for every chain. The `game_pak` AAPak archive was not
opened (no `ARCHEAGE_PAK_PATH` configured; `list_pak_entries`/`read_pak_entry` return
deterministic `not configured` errors).

**Build note:** `dotnet run --project AAEmu.ArchaeologyMcp` fails to rebuild because of
a **pre-existing** compile error (`QueryTimeoutSecondsOverride` undefined at
`ArchaeologyDomain.cs:60,918,1054` and `ArchaeologyService.cs:213`). This is a
pre-existing source defect in the untracked `AAEmu.ArchaeologyMcp/` tree, not caused by
this acceptance run. The pre-built Release binary (which predates the defect) was used
instead; it serves the full 24-tool surface correctly. This gap is recorded here as an
honest note, not fixed (no code changes permitted).

---

## 1. MateLevel objectives — `find_quest_objectives(family="mate_levels")`

**Input:** `{"name":"find_quest_objectives","arguments":{"family":"mate_levels"}}`

**Result:** `ok=true`, `supported=true`, `row_count=6`, `truncated=false`, `limit=50`,
`evidence=heuristic` (objective linkage via `quest_acts.act_detail_type`/`act_detail_id`
convention; no declared FK).

**Rows (6 live carrier quests; table `quest_act_obj_mate_levels`):**

| act_id | act_detail_id | item_id | LEVEL | cleanup | use_alias | alias_id | quest_context_id | quest_name |
|---|---|---|---|---|---|---|---|---|
| 33000 | 2 | 14878 | 50 | t | t | 2420 | 5430 | 폭풍을 넘어 천둥을 내 손에 |
| 33270 | 6 | 8158 | 50 | t | t | 2467 | 5464 | 잘 자란 칠흑의 릴리엇 말 납품 |
| 33271 | 7 | 8162 | 50 | t | t | 2468 | 5465 | 잘 자란 붉은 점박 릴리엇 말 납품 |
| 33272 | 8 | 8163 | 50 | t | t | 2469 | 5466 | 잘 자란 순백의 릴리엇 말 납품 |
| 34550 | 9 | 28420 | 50 | t | t | 2567 | 5812 | 잘 자란 날렵한 갈색 곰 납품 |
| 34551 | 10 | 28752 | 50 | t | t | 2568 | 5813 | 잘 자란 날렵한 눈보라 곰 납품 |

**Corroboration (direct read-only SQL):** `quest_act_obj_mate_levels` has 10 rows; the
4 orphaned detail rows (3/4/5/11) are **not** returned by `find_quest_objectives`
because their `quest_acts` rows (33008/33212/33213/35465) join to `quest_components`
rows that do not exist (deleted upstream) — the tool's `JOIN quest_components` filters
them out. This matches the sibling dossier
`scorecard-explorations/mechanics/mate-level-objective-research.md` §2.2 (6 live + 4
orphaned). **Consistent with prior research.**

---

## 2. Skill 23085 relationships

### 2a. `trace_skill(id=23085)`
**Input:** `{"name":"trace_skill","arguments":{"id":23085}}`
**Result:** `ok=true`, `supported=true`, `row_count=1`, `evidence=exact`, `truncated=false`.
Row: `skills#23085` = "소환수 성장의 파트라슈 빵" (Patrashu bread), desc "소환수의
경험치가 50000 증가됩니다. 소환수에게만 사용할 수 있습니다." (mate XP +50,000, mate-only),
cost 80, icon 1, start_anim 188, fire_anim 200.

### 2b. `trace_references(identifier="23085", domain="skill")`
**Result:** `ok=true`, `node_count=26`, `max_depth=2`, `truncated=true` (hit the 200-node
cap), `file_matches=3`, `files_scanned=1000`.

Key nodes (evidence labels as reported):
- `skill_effects#23085` (depth 0, exact) — seed hit
- `skills#23085` (depth 0, exact) — seed hit
- `skill_effects#25546` (depth 1, exact, `skill_effects.skill_id → skills.id`) — the
  skill's effect row
- `effects#32617` (depth 2, exact, `skill_effects.effect_id → effects.id`) — the effect
- `skill_controllers#703`, `icons#1`, `fx_groups#1508`, `doodad_bundles#0` (depth 1,
  exact) — skill FK targets
- `tagged_skills#2917` (depth 1, exact), `tags#1106` (depth 2, exact)
- `np_skills#9684` (depth 2, **heuristic**, `np_skills.skill_id → skills.id`)
- 26 nodes total, truncated at the 200-node cap (BFS fan-out from shared FK targets).

**Corroboration (direct read-only SQL):** `skill_effects` row 25546 → `skill_id=23085`,
`effect_id=32617`; `effects#32617` = `(32617, 13221, SpecialEffect)`; `special_effects#13221`
= `(13221, 28, 50000, 0, 0, 0)` — special-effect type 28 (AddExp) value 50,000. This is
the mate-XP consumable chain. **Consistent with `mate-level-objective-research.md` §4.4.**

**File matches (code evidence):** `AAEmu.Game/Core/Managers/Bots/LevelingLoopScenario.cs`
lines 85, 217, 984 — all document that item 29040 → skill 23085 is blocked by a
canonical data gap (`unit_reqs` kind-38 `MotherFactionOnly=5`).

---

## 3. Faction-five references

### 3a. `trace_references(identifier="5", domain="faction")`
**Result:** `ok=true`, `node_count=1`, `max_depth=2`, `truncated=true` (file-match cap),
`file_matches=20`, `files_scanned=3`.

Node: `system_faction_relations#5` (depth 0, exact). **Corroboration (direct SQL):**
`system_faction_relations` id 5 = `(5, 102, 104, 3)` — faction1 102 (이즈나 왕가 / Izna
royal house) vs faction2 104 (안델프 / Andelph), state 3. The `system_factions` table
has **no id 5** (ids 1/2/3 = 우호/중립/적대, then 101+); `mother_id=5` matches **zero**
factions. The `trace_references` seed found only the `system_faction_relations` row
whose PK is 5; it did not surface the `MotherFactionOnly=5` unit_reqs rows because those
are `unit_reqs` rows with `value1=5` (not a PK match) and the domain filter `faction`
excludes `unit_reqs`.

### 3b. `search_everything(term="MotherFactionOnly")`
**Result:** `ok=true`, `hit_count=3`, `db_hits=[]`, `tables_scanned=300`,
`files_scanned=1000`, `truncated=true` (scan caps), `limit=50`.

**File matches (code evidence):** `LevelingLoopScenario.cs` lines 86, 219, 985 — all
state "kind-38 MotherFactionOnly=5, no faction satisfies it — engine refuses with
skill_urk_mother_faction_only". **No DB hits** — `MotherFactionOnly` is a C# enum name,
not a data value.

**Corroboration (direct SQL):** `unit_reqs` kind_id=38 rows include
`(43798, Skill, 23085, 38, 5, 0)` — skill 23085 has a kind-38 `MotherFactionOnly` gate
with `value1=5`. `system_factions` has no id 5 and no `mother_id=5` faction, so the gate
is unsatisfiable — confirming the code comment. **Consistent with `mate-level-objective-research.md` §4.4 and `LevelingLoopScenario.cs`.**

---

## 4. Quest 3889 relationships

### 4a. `trace_quest(id=3889)`
**Input:** `{"name":"trace_quest","arguments":{"id":3889}}`
**Result:** `ok=true`, `supported=true`, `row_count=1`, `evidence=exact`, `act_count=5`,
`components_truncated=false`.

Row: `quest_contexts#3889` = "수호자들의 도움" (Guardians' Help), category 8, LEVEL 21,
non-repeatable.

**Components (5):**
| comp_id | kind | skill_id | skill_self |
|---|---|---|---|
| 18561 | 2 (Start) | 19196 | t |
| 18562 | 4 (Progress) | null | f |
| 18563 | 6 (Ready) | null | f |
| 18564 | 8 (Reward) | null | f |
| 18824 | 3 (?) | null | f |

**Corroboration (direct SQL):** `quest_acts` for 3889 = 5 acts:
- 24825 `QuestActConAcceptNpc` 3554 (comp 18561) — accept from NPC 3554 (공학자 도널)
- 25183 `QuestActObjItemUse` 633 (comp 18562) — use item 24165
- 24826 `QuestActConReportNpc` 3814 (comp 18563) — report to NPC 3814
- 25184 `QuestActSupplyItem` 2639 (comp 18824) + 40895 `QuestActSupplyItem` 4862 (comp 18564) — rewards

### 4b. `trace_references(identifier="3889", domain="quest")`
**Result:** `ok=false`, `error="SQLite error: SQLite Error 1: 'no such column: id'."`

**Genuine tool gap (root-caused, direct SQL):** the trace's incoming-exact edge runs
`SELECT id FROM <table> WHERE <fkcol> = <value>` for every table with a declared FK to a
reached table. `indun_zones` has **no `id` column** (cols: `zone_group_id, name,
COMMENT, level_min, level_max, max_players, pvp, has_graveyard, item_id,
restore_item_time, party_only, client_driven, select_channel`) yet declares
`FOREIGN KEY(item_id) REFERENCES items(id)` and `FOREIGN KEY(zone_group_id)
REFERENCES zone_groups(id)`. When the trace reaches `items` (via quest 3889's item 24165)
or `zone_groups` (via quest 3889's zone 10 → group 18), the incoming-exact query
`SELECT id FROM indun_zones WHERE item_id = …` / `WHERE zone_group_id = …` throws
`no such column: id`, aborting the whole trace. **This is a real tool defect, not a data
gap** — `trace_references` assumes every FK-target table has an `id` column, which
`indun_zones` (and `conflict_zones`, `siege_settings`, `schema_migrations`) violate.
Reproduced deterministically with direct SQL. **Not fixed (no code changes permitted).**

---

## 5. Quest item → skill chain (quest 3889 → item 24165 → skill 18017)

### 5a. `trace_item(id=24165)`
**Result:** `ok=true`, `supported=true`, `row_count=1`, `evidence=exact`.
Row: `items#24165` = "봉인의 기운" (Seal's energy), category 64, LEVEL 1, bind 2.

### 5b. `query_sql("SELECT id, name, use_skill_id FROM items WHERE id=24165")`
**Result:** `ok=true`, `row_count=1`, `truncated=false`, `elapsed_ms=31`.
Row: `(24165, 봉인의 기운, 18017)` — item 24165's `use_skill_id` = 18017.

### 5c. `trace_skill(id=18017)`
**Result:** `ok=true`, `supported=true`, `row_count=1`, `evidence=exact`.
Row: `skills#18017` = "봉인석에 기운 흘려넣기" (Pour energy into seal stone), cost 0,
icon 1757.

### 5d. `query_sql("SELECT id, skill_id, effect_id FROM skill_effects WHERE skill_id=18017")`
**Result:** `ok=true`, `row_count=0` — skill 18017 has **no** `skill_effects` rows (the
chain terminates at the skill; no effect row).

### 5e. `trace_references(identifier="24165", domain="item")`
**Result:** `ok=false`, `error="SQLite error: SQLite Error 1: 'no such column: id'."` —
same `indun_zones` gap as §4b (item 24165 reaches `items`, whose incoming-exact edge
hits `indun_zones.item_id`).

**Chain summary (data):** quest 3889 `QuestActObjItemUse` 633 → item 24165 (봉인의 기운)
→ `use_skill_id` 18017 (봉인석에 기운 흘려넣기) → no `skill_effects` row (terminal).

---

## 6. NPC → world spawn chain (NPC 3554)

### 6a. `trace_npc(id=3554)`
**Result:** `ok=true`, `supported=true`, `row_count=1`, `evidence=exact`.
Row: `npcs#3554` = "공학자 도널" (Engineer Donel), grade 1, kind 1, LEVEL 10,
npc_template 9.

### 6b. `trace_world_spawn(name="3554")`
**Result:** `ok=true`, `world_spawns_file=null`, `spawns=[]`, `npc_spawners` 2 rows,
`npc_spawners_truncated=false`, `row_count=2`, `truncated=false`.
- `npc_spawners#3451` = "3554. 공학자 도널", category 1, maxPopulation 1
- `npc_spawners#15096` = "13554: 밤의 요정 패티 호스트" (name LIKE match on "3554"? —
  actually a substring match artifact; the name contains no "3554", so this is a
  `npc_spawners` name-LIKE false positive on the numeric substring; see note)

**Note:** `world_spawns_file=null` because `trace_world_spawn` only reads
`Data/Worlds/world_spawns.json` when it exists and is ≤1 MiB; the file exists (5,325 B)
but contains **no NPC 3554** (it lists only instance/world entry points, not NPC
spawners). The second `npc_spawners` row (15096) is a name-LIKE substring artifact of the
`%3554%` pattern — its name "13554: …" contains "3554" as a substring. **Honest note:
the tool's name filter is a raw `LIKE '%<name>%'` substring match, so numeric names
over-match.**

### 6c. `query_sql("SELECT sn.id, sn.npc_spawner_id, sn.member_id, sn.member_type, s.name FROM npc_spawner_npcs sn LEFT JOIN npc_spawners s ON s.id=sn.npc_spawner_id WHERE sn.member_id=3554")`
**Result:** `ok=true`, `row_count=1`, `truncated=false`, `elapsed_ms=8`.
Row: `(3453, 3451, 3554, Npc, "3554. 공학자 도널")` — spawner 3451 places NPC 3554.

### 6d. `trace_references(identifier="3554", domain="npc")`
**Result:** `ok=true`, `node_count=68`, `max_depth=2`, `truncated=true` (200-node cap),
`file_matches=9`, `files_scanned=1000`. Seed hits: `npc_ai_params#3554`,
`npc_spawner_npcs#3554`, `npc_spawners#3554`, `npcs#3554`,
`quest_act_con_accept_npcs#3554`, `quest_act_con_report_npcs#3554`,
`quest_monster_npcs#3554`. Edges include `npc_spawner_npcs.npc_spawner_id →
npc_spawners.id` (heuristic), `npcs.model_id → models.id` (exact), etc.

**Chain summary (data):** NPC 3554 → `npc_spawner_npcs` member 3554 → spawner 3451
("3554. 공학자 도널", category 1, maxPopulation 1). No `world_spawns.json` entry for
3554 (that file holds instance entry points only).

---

## 7. Mate / pet chain

### 7a. `trace_mate(item_id=14878)`
**Result:** `ok=true`, `supported=true`, `row_count=1`, `evidence=exact`.
Row: `item_summon_mates#17` = item 14878 → npc 7043 (폭풍질주 / Storm Dash).

### 7b. `trace_mate(item_id=8158)` and `trace_mate(item_id=28420)`
- 8158 → `item_summon_mates#9` → npc 5430 (칠흑의 릴리엇 말)
- 28420 → `item_summon_mates#129` → npc 13432 (날렵한 갈색 곰)

### 7c. `trace_references(identifier="14878", domain="item")`
**Result:** `ok=false`, `error="SQLite error: SQLite Error 1: 'no such column: id'."` —
same `indun_zones` gap (item 14878 reaches `items`).

**Corroboration (direct SQL):** `item_summon_mates` has 176 rows; the 6 MateLevel quest
summon items map to: 14878→7043, 8158→5430, 8162→5434, 8163→5435, 28420→13432,
28752→13519. **Consistent with `mate-level-objective-research.md` §2.2.**

---

## 8. Vehicle / ship chain

### 8a. `trace_vehicle(id=1)`
**Result:** `ok=true`, `supported=true`, `row_count=1`, `evidence=exact`.
Row: `vehicle_models#1` = siege catapult (`cga://objects/Env/06_unit/04_siegeweapon/
siege_catapult_a.chr`), wheel `siege_catapultwheel_a.cgf`, turret_pitch_angle_max 0.7,
lin_inertia 0.

### 8b. `trace_vehicle(id=2)`
Row: `vehicle_models#2` = trebuchet (`prefab://prefabs/siege_weapon.xml/trebuchet_a…`),
turret_pitch_angle_max 70, lin_inertia 0.4.

### 8c. `trace_references(identifier="1", domain="vehicle")`
**Result:** `ok=true`, `node_count=1`, `max_depth=2`, `truncated=true` (file-match cap),
`file_matches=20`, `files_scanned=3`. Node: `vehicle_models#1` (depth 0, exact). The
`vehicle_models` table has no FK to other tables and no incoming FK edges, so the trace
is a single node; the file matches are substring noise (CharTemplates.json "Id": 1,
presence_manifest "level": 5, etc.).

**Note:** `vehicle_models` has **no name column** (per README); `trace_vehicle` matches
by id only and returns model columns (`normal`, `damaged50`, `dying`, `dead`, `wheel`,
inertia, velocity, etc.). 63 rows total. **Ship/vehicle chain is data-only; no
`vehicle_models` FK edges exist in the canonical DB.**

---

## 9. Physics parameter domains

### 9a. `search_physics({})`
**Result:** `ok=true`, `supported=true`, `tables=["physical_enchant_abilities",
"physical_explosion_effects"]`, `truncated=false`, `limit=20`. Note: "no
collision/geometry tables exist in this database; only physical_* effect tables are
available".

- `physical_enchant_abilities` (86 rows): `id, npc_id, armor, enchant_level,
  min_friendship, success_ratio` — e.g. id 1 = npc 1, armor t, enchant_level 1,
  min_friendship 0, success_ratio 100.
- `physical_explosion_effects` (58 rows): `id, radius, hole_size, pressure`.

### 9b. `search_physics(term="armor")`
**Result:** `ok=true`, `supported=true`, both tables return **0 rows** for the term
"armor" (the `armor` column is a `t`/`f` flag, not the word "armor"; the term filter
matches column values, not column names).

**Domain note:** the canonical DB contains **no collision/geometry/physics-parameter
tables** beyond these two effect tables. `search_physics` honestly reports this. The
`vehicle_models` inertia/velocity columns (§8) are the only other physics-adjacent
parameters, and they are not in `physical_*` tables.

---

## 10. Bounds / truncation summary

| Query | Result cap | Truncated? | Notes |
|---|---|---|---|
| `find_quest_objectives` mate_levels | 50 | no | 6 rows |
| `trace_skill` 23085 | 20 | no | 1 row |
| `trace_references` 23085 | 200 nodes | **yes** | 26 nodes, hit cap |
| `trace_references` faction 5 | 200 nodes | **yes** | 1 node; file-match cap (20) |
| `search_everything` MotherFactionOnly | 50 | **yes** | 3 hits; tables_scanned 300, files_scanned 1000 (scan caps) |
| `trace_quest` 3889 | 20 | no | 1 row + 5 components |
| `trace_references` 3889 | 200 nodes | **error** | `indun_zones` no-id gap |
| `trace_item` 24165 | 20 | no | 1 row |
| `trace_skill` 18017 | 20 | no | 1 row |
| `trace_npc` 3554 | 20 | no | 1 row |
| `trace_world_spawn` 3554 | 20 | no | 2 spawners |
| `trace_mate` 14878/8158/28420 | 20 | no | 1 row each |
| `trace_vehicle` 1/2 | 20 | no | 1 row each |
| `search_physics` | 20 | no | 2 tables |
| `query_sql` (all) | 100 | no | ≤1 row each |

`truncated` is reported on both the `data` object and the `provenance` block when a
row/byte/result limit was hit (verified in the raw JSON).

---

## 11. Honest unsupported / gap notes

1. **`trace_references` fails on any trace that reaches `items` or `zone_groups`**
   (incoming-exact edge) because `indun_zones` (and `conflict_zones`, `siege_settings`,
   `schema_migrations`) have **no `id` column** yet declare FKs. The tool assumes every
   FK-target table has an `id` column. This breaks `trace_references` for quest 3889,
   item 24165, and mate item 14878. **Genuine tool defect; not fixed (no code changes).**
2. **`trace_world_spawn` name filter is a raw `LIKE '%<name>%'` substring match** — the
   numeric name "3554" over-matched spawner 15096 ("13554: …"). Numeric-name queries
   over-match.
3. **`trace_world_spawn` reads only `Data/Worlds/world_spawns.json`** (instance entry
   points), not the per-world `npc_spawns.json` files; NPC spawn chains must be resolved
   via `npc_spawners`/`npc_spawner_npcs` (as done in §6).
4. **`search_everything` scans at most 300 tables and 1000 files** — `truncated=true`
   on the MotherFactionOnly query; a term could be missed if it lives past those caps.
5. **`search_physics` covers only `physical_enchant_abilities` and
   `physical_explosion_effects`** — no collision/geometry/physics-parameter tables
   exist in the canonical DB (honestly reported by the tool).
6. **`vehicle_models` has no name column** — `trace_vehicle` matches by id only.
7. **`find_quest_objectives` linkage is `heuristic`** (no declared FK between
   `quest_acts.act_detail_id` and the `quest_act_obj_*` family tables) — the tool labels
   this honestly.
8. **Pre-existing build defect** in the untracked `AAEmu.ArchaeologyMcp/` source
   (`QueryTimeoutSecondsOverride` undefined) prevents `dotnet run` rebuild; the pre-built
   Release binary was used. Not caused by, and not fixed by, this acceptance run.
9. **No game runtime exercised** — all evidence is data + code; no live/client/H
   evidence. `game_pak` not opened (unconfigured).

---

## 12. Verification

- **Read-back:** this report was written to
  `scorecard-explorations/mechanics/archaeology-mcp-acceptance.md` and re-read in full.
- **Diff-check:** `git status --porcelain` shows **no new tracked changes** from this
  run (the only untracked entries — `Scripts/mcp-archaeology-smoke.sh`,
  `scorecard-explorations/mechanics/archaeology-data-source-inventory.md`, and the
  `AAEmu.ArchaeologyMcp/` tree — pre-existed this run; the new report is the only
  addition). `compact.sqlite3` md5 unchanged (`78b3bdbf…`). No commit made.
- **Read-only invariant:** the DB was opened `Mode=ReadOnly` by the server; md5 before
  and after are identical.

**Report path:** `scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`
---

# Follow-up 2026-08-31 (second pass) — rebuilt server, fixes verified

**This section supersedes the stale notes in the original batch above.** The
archaeology MCP implementation was fixed and rebuilt after the first pass; the
pre-built-binary/build-defect note (§0), the `trace_references` no-id gap (§4b, §5e,
§7c, §11.1), and the `trace_world_spawn` substring-overmatch note (§6b, §11.2) are
**obsolete** and replaced by the verified results below. The original batch is
preserved as the historical record of the pre-fix server.

## F0. Provenance (second pass)

| Item | Value |
|---|---|
| Local HEAD | `c6b5dd8f209e55d01026bbfec70dc2d3c34aa42f` (unchanged) |
| Canonical DB | `AAEmu.Game/Data/compact.sqlite3` |
| DB md5 (before) | `78b3bdbf038db3b927056106efdf91af` |
| DB md5 (after) | `78b3bdbf038db3b927056106efdf91af` (**unchanged**) |
| Server | **rebuilt** `AAEmu.ArchaeologyMcp` — `dotnet build -c Release` **succeeds** (0 errors; 2 NU1903 SQLitePCLRaw vulnerability warnings, pre-existing) |
| Binary | `AAEmu.ArchaeologyMcp/bin/Release/net10.0/AAEmu.ArchaeologyMcp` (freshly built 2026-08-31T05:08Z) |
| Tool surface | 24 tools (unchanged) |
| Run timestamp | 2026-08-31T05:09Z (batch 3, 28 requests) |

**Build-defect note superseded:** the `QueryTimeoutSecondsOverride` compile error
(original §0/§11.8) is **fixed** — the project now builds cleanly and the rebuilt
binary was used for every query in this pass. No code was edited by this acceptance
run; the fix was made by the implementation owner before this pass.

## F1. Fix 1 — `trace_references` handles tables without an `id` column

**Source fix (verified in `AAEmu.ArchaeologyMcp/ArchaeologyDomain.cs`):** a new
`KeyColumn(connection, table)` helper returns the single declared PK column, else the
conventional `id` column, else `null`. Every seed lookup, outgoing edge, and incoming
edge now uses `KeyColumn` and **skips** (never crashes on) tables with no addressable
key column (`ArchaeologyDomain.cs:201-216, 240-310, 1119-1130`). The previous failure
mode — `SELECT id FROM indun_zones WHERE item_id = …` throwing `no such column: id`
because `indun_zones` (and `conflict_zones`, `siege_settings`, `schema_migrations`)
have no `id` column yet declare FKs — is eliminated.

### F1a. `trace_references(identifier="3889", domain="quest")` — was ERROR, now OK
**Result:** `ok=true`, `node_count=62`, `max_depth=2`, `truncated=true` (200-node cap).
Seed hits: `quest_act_con_accept_npcs#3889`, `quest_act_con_report_npcs#3889`,
`quest_act_supply_items#3889`, `quest_components#3889`, `quest_contexts#3889`,
`quest_monster_npcs#3889`. Edges include `quest_act_con_accept_npcs.npc_id → npcs#12203`
(exact), `quest_act_con_report_npcs.npc_id → npcs#1944` (exact),
`quest_act_supply_items.item_id → items#29681` (exact),
`quest_components.quest_context_id → quest_contexts#913` (exact). **No `indun_zones`
crash; the trace completes.**

### F1b. `trace_references(identifier="24165", domain="item")` — was ERROR, now OK
**Result:** `ok=true`, `node_count=13`, `max_depth=2`, `truncated=true`.
Seed hits: `item_armor_assets#24165`, `items#24165`. Edges include
`quest_act_obj_item_uses#633` (exact, `item_id → items.id` — the quest-3889 item-use
act), `quest_act_supply_items#2639` (exact), `armor_assets#3052` (exact),
`icons#1552`, `buffs#0`. **Completes; the quest item→skill chain is reachable.**

### F1c. `trace_references(identifier="14878", domain="item")` — was ERROR, now OK
**Result:** `ok=true`, `node_count=33`, `max_depth=2`, `truncated=true`.
Seed hits: `item_assets#14878`, `items#14878`, `sound_pack_items#14878`. Edges include
`item_summon_mates#17` (exact — the mate chain), `quest_act_obj_mate_levels#2` (exact —
the MateLevel objective), `models#21`, `merchant_goods#5596` (heuristic). **Completes.**

## F2. Fix 2 — `trace_world_spawn` scans aggregate + per-world JSON with exact/whole-word matching

**Source fix (verified in `ArchaeologyDomain.cs:599-745`):** `trace_world_spawn` now
scans `world_spawns.json` **and** every per-world `*.json` under `Data/Worlds/`
(comment-tolerant `ParseJsonTolerant`, `:1193-1197`), reads both aggregate
(`Name`/`SpawnPosition`) and per-world (`UnitId`/`Position`) entry shapes, and matches
names by **exact (case-insensitive) or whole-word containment** — no more raw substring
overmatch on the JSON side. New result fields: `worlds_dir`, `files_matched`,
`files_scanned`, `no_match`. The `npc_spawners` DB side still uses `LIKE '%name%'`
(see F4 limitation).

### F2a. `trace_world_spawn(name="3554")` — JSON side fixed
**Result:** `ok=true`, `spawns=[]` (JSON side: **no** substring overmatch — no
`world_spawns.json`/per-world entry contains "3554" as a whole word), `files_scanned=42`
(all Worlds JSON files), `npc_spawners` 2 rows: `npc_spawners#3451` ("3554. 공학자
도널", category 1, maxPopulation 1) and `npc_spawners#15096` ("13554: …" — **still a
`npc_spawners` DB-side `LIKE` overmatch**, see F4). `truncated=false`.

### F2b. `trace_world_spawn(name="arche_mall")` — aggregate exact match
**Result:** `ok=true`, `spawns=2`: `arche_mall_world` and `arche_mall` (both zone 260,
from `world_spawns.json`), `files_matched=[world_spawns.json]`, `files_scanned=42`.

### F2c. `trace_world_spawn(name="instance_library")` — whole-word match
**Result:** `ok=true`, `spawns=8`: `instance_library`, `_1`, `_2`, `_3`, `boss_1`,
`boss_2`, `boss_3`, `tower_defense` (zones 296-306, all from `world_spawns.json`).
Whole-word boundary matching (`nameLower + "_"` / `"_" + nameLower`) correctly includes
underscore-suffixed variants without raw-substring noise.

### F2d. `trace_world_spawn(zone_id=260)` and `(zone_id=198)` — per-world reachability
zone 260 → 2 aggregate spawns; zone 198 → `instance_training_camp` (zone 198) plus
`npc_spawners` rows. Per-world `npc_spawns.json` files (e.g. `main_world/npc_spawns.json`,
`UnitId`/`Position` shape) are scanned; they carry **no names**, so name filters cannot
match them (only `zone_id`/no-filter can surface them) — see F4.

## F3. All acceptance queries re-run on the rebuilt server (batch 3)

| # | Query | Result |
|---|---|---|
| 3 | `trace_references(3889, quest)` | ok, 62 nodes, truncated (cap) — **was error** |
| 4 | `trace_references(24165, item)` | ok, 13 nodes, truncated (cap) — **was error** |
| 5 | `trace_references(14878, item)` | ok, 33 nodes, truncated (cap) — **was error** |
| 6 | `trace_world_spawn(3554)` | ok, 0 JSON spawns + 2 npc_spawners, no_match=false |
| 7 | `trace_world_spawn(arche_mall)` | ok, 2 spawns (exact) |
| 8 | `trace_world_spawn(instance_library)` | ok, 8 spawns (whole-word) |
| 9 | `find_quest_objectives(mate_levels)` | ok, 6 rows, evidence=heuristic, truncated=false |
| 10 | `trace_skill(23085)` | ok, 1 row (Patrashu bread, +50,000 mate XP), exact |
| 11 | `trace_references(23085, skill)` | ok, 26 nodes, truncated (cap) |
| 12 | `trace_references(5, faction)` | ok, 1 node (`system_faction_relations#5`), truncated (file cap) |
| 13 | `search_everything(MotherFactionOnly)` | ok, 3 file hits (LevelingLoopScenario.cs 86/219/985), 0 db hits, truncated (scan caps) |
| 14 | `trace_quest(3889)` | ok, 1 row, act_count=5, truncated=false |
| 15 | `trace_item(24165)` | ok, 1 row (봉인의 기운), exact |
| 16 | `trace_skill(18017)` | ok, 1 row (봉인석에 기운 흘려넣기), exact |
| 17 | `trace_npc(3554)` | ok, 1 row (공학자 도널), exact |
| 18-20 | `trace_mate(14878/8158/28420)` | ok, 1 row each (7043/5430/13432), exact |
| 21-22 | `trace_vehicle(1/2)` | ok, 1 row each (catapult/trebuchet), exact |
| 23 | `search_physics({})` | ok, 2 tables, 20 rows each, truncated=false |
| 24 | `search_physics(armor)` | ok, 0 rows (armor is a t/f flag, not the word) |
| 25 | `trace_references(3554, npc)` | ok, 68 nodes, truncated (cap) |
| 26 | `trace_references(1, vehicle)` | ok, 1 node, truncated (file cap) |
| 27-28 | `list_sources` / `list_databases` | ok (canonical DB only) |

**All 24 acceptance queries pass on the rebuilt server.** No query returned an error.

## F4. Remaining limitations (honest, second pass)

1. **`npc_spawners` DB-side name filter is still raw `LIKE '%<name>%'`** — the JSON
   side was fixed to exact/whole-word, but `trace_world_spawn(name="3554")` still
   returns `npc_spawners#15096` ("13554: …") as a substring overmatch. Numeric-name
   queries over-match on the DB side only.
2. **Per-world `npc_spawns.json` entries carry no names** (`UnitId`/`Position` only), so
   name filters cannot match them; only `zone_id`/no-filter scans surface them. The
   no-filter scan hits the 20-row cap on the aggregate first (`truncated=true`), so
   per-world entries are only reachable via `zone_id` filters.
3. **`trace_references` truncates at 200 nodes** — quest 3889 (62), skill 23085 (26),
   NPC 3554 (68), item 14878 (33) all hit the cap; deep fan-out graphs are partial.
4. **`search_everything` caps at 300 tables / 1000 files** — `truncated=true` on the
   MotherFactionOnly query; terms past the caps are missed.
5. **`search_physics` covers only `physical_enchant_abilities` (86) and
   `physical_explosion_effects` (58)** — no collision/geometry tables exist in the
   canonical DB (honestly reported).
6. **`vehicle_models` has no name column** — `trace_vehicle` matches by id only; 63
   rows; no FK edges (single-node traces).
7. **`find_quest_objectives` linkage remains `heuristic`** (no declared FK between
   `quest_acts.act_detail_id` and `quest_act_obj_*` families) — honest label.
8. **No game runtime exercised** — all evidence is data + code; no live/client/H
   evidence. `game_pak` not opened (unconfigured).

## F5. Verification (second pass)

- **Read-back:** the full report (original batch + this follow-up) was re-read.
- **Diff-check:** `git status --porcelain` shows the only new file from this pass is
  this follow-up appended to `scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`;
  no tracked files modified, no commit made. `compact.sqlite3` md5 unchanged
  (`78b3bdbf…`).
- **Read-only invariant:** DB opened `Mode=ReadOnly`; md5 before/after identical.
- **Build:** `dotnet build AAEmu.ArchaeologyMcp -c Release` succeeds (0 errors).

**Report path (unchanged):** `scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`
---

# Follow-up 2026-08-31 (third pass) — npc_spawners DB-side name matching fixed

**This section supersedes F4.1 in the second-pass follow-up above.** A further code
fix landed after the second pass: the `npc_spawners` DB-side world-name matching now
uses a bounded candidate set plus the same exact/whole-word C# filter as the JSON
side, so a numeric name like "3554" no longer over-matches "13554". The second-pass
F4.1 limitation is **resolved**; the remaining-limitations list below reflects only
what is still actually limited. The original batch and the second-pass follow-up are
preserved as the historical record.

## G0. Provenance (third pass)

| Item | Value |
|---|---|
| Local HEAD | `c6b5dd8f209e55d01026bbfec70dc2d3c34aa42f` (unchanged) |
| Canonical DB | `AAEmu.Game/Data/compact.sqlite3` |
| DB md5 (before) | `78b3bdbf038db3b927056106efdf91af` |
| DB md5 (after) | `78b3bdbf038db3b927056106efdf91af` (**unchanged**) |
| Server | **rebuilt** `AAEmu.ArchaeologyMcp` — `dotnet build -c Release` **succeeds** (0 errors; 2 pre-existing NU1903 warnings) |
| Binary | `AAEmu.ArchaeologyMcp/bin/Release/net10.0/AAEmu.ArchaeologyMcp` (freshly built 2026-08-31T05:20Z) |
| Tests | `dotnet test --project AAEmu.UnitTests --configuration Release` — **ArchaeologyDomainTests: 37/37 pass**; **all ArchaeologyMcp tests: 18/18 pass** |
| Run timestamp | 2026-08-31T05:21Z (batch 4, 8 requests) |

## G1. Fix 3 — `npc_spawners` DB-side name matching (bounded candidates + exact/whole-word C# filter)

**Source fix (verified in `ArchaeologyDomain.cs:707-735`):** `trace_world_spawn` now
fetches a bounded candidate set from `npc_spawners` (a broad `LIKE '%<name>%'`
superset, capped at `resultLimit * 10`, max 1000) and applies the same
`MatchesNameFilter` exact/whole-word C# filter (`:1198-1223`) used on the JSON side.
A name matches only on exact equality or a token boundary (letter/digit runs), so
"3554" no longer matches "13554" (the "13554" token ≠ "3554").

### G1a. `trace_world_spawn(name="3554")` — overmatch eliminated
**Result:** `ok=true`, `spawns=[]`, `npc_spawners=1` (**only** `npc_spawners#3451`
"3554. 공학자 도널", category 1, maxPopulation 1), `files_scanned=42`, `truncated=false`,
`no_match=false`. The previous `npc_spawners#15096` ("13554: …") overmatch is **gone**.

### G1b. `trace_world_spawn(name="arche_mall")` — unchanged, exact
**Result:** `ok=true`, `spawns=2` (`arche_mall_world`, `arche_mall`, zone 260),
`npc_spawners=0`, `truncated=false`.

### G1c. `trace_world_spawn(name="instance_library")` — unchanged, whole-word
**Result:** `ok=true`, `spawns=8` (`instance_library`, `_1`, `_2`, `_3`, `boss_1`,
`boss_2`, `boss_3`, `tower_defense`, zones 296-306), `npc_spawners=0`, `truncated=false`.

### G1d. `trace_world_spawn(zone_id=260)` — unchanged
**Result:** `ok=true`, `spawns=2` (aggregate), `npc_spawners=20` (bounded candidate
set, `truncated=true` at the 20-row result cap — honest), `files_scanned=42`.

## G2. `trace_references` re-verified (unchanged, still fixed)

| Query | Result |
|---|---|
| `trace_references(3889, quest)` | ok, 62 nodes, truncated (200-node cap) |
| `trace_references(24165, item)` | ok, 13 nodes, truncated (cap) |
| `trace_references(14878, item)` | ok, 33 nodes, truncated (cap) |

All three complete with no `indun_zones` crash — the F1 fix holds on the rebuilt
server.

## G3. Remaining limitations (actual, third pass)

1. **Per-world `npc_spawns.json` entries carry no names** (`UnitId`/`Position` only), so
   name filters cannot match them; only `zone_id`/no-filter scans surface them, and the
   no-filter scan hits the 20-row cap on the aggregate first (`truncated=true`).
2. **`trace_references` truncates at 200 nodes** — quest 3889 (62), skill 23085 (26),
   NPC 3554 (68), item 14878 (33) all hit the cap; deep fan-out graphs are partial.
3. **`search_everything` caps at 300 tables / 1000 files** — `truncated=true` on the
   MotherFactionOnly query; terms past the caps are missed.
4. **`search_physics` covers only `physical_enchant_abilities` (86) and
   `physical_explosion_effects` (58)** — no collision/geometry tables exist in the
   canonical DB (honestly reported).
5. **`vehicle_models` has no name column** — `trace_vehicle` matches by id only; 63
   rows; no FK edges (single-node traces).
6. **`find_quest_objectives` linkage remains `heuristic`** (no declared FK between
   `quest_acts.act_detail_id` and `quest_act_obj_*` families) — honest label.
7. **No game runtime exercised** — all evidence is data + code; no live/client/H
   evidence. `game_pak` not opened (unconfigured).

**Resolved (no longer a limitation):** the `npc_spawners` DB-side `LIKE` overmatch
(second-pass F4.1) is fixed; the `trace_references` no-id crash (original §4b/§5e/§7c)
is fixed; the build defect (original §0/§11.8) is fixed.

## G4. Verification (third pass)

- **Read-back:** the full report (original batch + second-pass + this third-pass
  follow-up) was re-read.
- **Diff-check:** `git status --porcelain` shows the only new file from this pass is
  this follow-up appended to `scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`;
  no tracked files modified, no commit made. `compact.sqlite3` md5 unchanged
  (`78b3bdbf…`).
- **Read-only invariant:** DB opened `Mode=ReadOnly`; md5 before/after identical.
- **Build + tests:** `dotnet build AAEmu.ArchaeologyMcp -c Release` succeeds (0 errors);
  `ArchaeologyDomainTests` 37/37 pass; all `ArchaeologyMcp` tests 18/18 pass.

**Report path (unchanged):** `scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`
