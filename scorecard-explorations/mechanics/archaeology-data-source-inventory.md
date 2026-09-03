# Archaeology Data-Source Inventory — Dossier (2026-08-30)

Read-only inventory. **No code changed, no commit, no build, no soak/E2E root touched.**
Branch `develop`, local HEAD `c6b5dd8f209e55d01026bbfec70dc2d3c34aa42f`.
Canonical data: `AAEmu.Game/Data/compact.sqlite3`, md5 `78b3bdbf038db3b927056106efdf91af` (unchanged).
This dossier is the repo's authoritative archaeology data-source inventory for the
current read-only archaeology MCP (`AAEmu.ArchaeologyMcp/`). It records what exists,
where, at what size, and what is safe to expose through an allowlisted MCP root.
Companion foundation report: `/tmp/archaeology-mcp-foundation.md` (MCP server
architecture, not data). Current acceptance evidence: the read-only acceptance run
[`archaeology-mcp-acceptance.md`](archaeology-mcp-acceptance.md) (2026-08-31, same
HEAD, 24-tool surface, canonical DB md5 unchanged).

**Path convention:** repo-relative paths (`AAEmu.Game/…`, `SQL/…`, `tools/…`) are
portable and live in the fork. Absolute paths under `/root/…` are **local-machine
only** (client assets, extracted client, E2E/soak roots) and must not be assumed on
any other host.

---

## 1. Canonical reference data (primary)

| Path | Source type | Logical domain | Size / count | Encoding / parseability | Version / provenance | Mutability | MCP-safe |
|---|---|---|---|---|---|---|---|
| `AAEmu.Game/Data/compact.sqlite3` | SQLite DB | **All game templates** (items, NPCs, skills, quests, doodads, buffs, loots, spawners, zones, localized text) | 119,054,336 B (113.5 MiB); **679 tables** | SQLite 3; fully parseable via `sqlite3` / `Microsoft.Data.Sqlite` | ArcheAge 1.2 r208022; md5 `78b3bdbf038db3b927056106efdf91af` (unchanged per STATUS/SCORECARD) | **Read-only** (canonical reference; never write) | **YES — primary root** |
| `AAEmu.Game/Data/Worlds/` | JSON world data | World spawns (npc/doodad/gimmick/slave/transfer), 18 world dirs + `world_spawns.json` | 42 JSON files, 2.8 MiB total; `main_world/npc_spawns.json` 813K, `doodad_spawns.json` 5.9M | JSON; parseable | 1.2 world layout | Read-only | **YES** |
| `AAEmu.Game/Data/Creatures.xml` | XML | Creature/NPC name table | 624,402 B | XML; parseable | 1.2 | Read-only | YES |
| `AAEmu.Game/Data/Doodads.xml` | XML | Doodad name table | 370,840 B | XML; parseable | 1.2 | Read-only | YES |
| `AAEmu.Game/Data/CharTemplates.json` | JSON | Character templates | 1,174 B | JSON | 1.2 | Read-only | YES |
| `AAEmu.Game/Data/battlefields.json`, `housing_bindings.json`, `slave_attach_points.json` | JSON | Battlefield / housing / slave attach config | 385 B / 17,801 B / 44,139 B | JSON | 1.2 | Read-only | YES |
| `AAEmu.Game/Data/Portal/` | JSON | recalls / respawns / worldgates | 3 files | JSON | 1.2 | Read-only | YES |
| `AAEmu.Game/Data/Path/` | `.path` | AI movement paths | 13 files | binary path format | 1.2 | Read-only | YES |
| `AAEmu.Game/Data/Bots/presence_manifest.json` | JSON | Bot presence manifest | 1 file | JSON | fork-local | Read-only | YES |

### 1.1 Key table counts in `compact.sqlite3` (verified read-only)

| Table | Rows | | Table | Rows |
|---|---|---|---|---|
| `quest_contexts` | 4,876 | | `skills` | 15,126 |
| `quest_components` | 17,851 | | `items` | 21,482 |
| `quest_acts` | 26,886 | | `npcs` | 13,382 |
| `quest_act_obj_mate_levels` | 10 | | `buffs` | 9,816 |
| `quest_act_obj_ability_levels` | 15 | | `doodad_funcs` | 13,974 |
| `quest_act_obj_levels` | 17 | | `npc_spawners` | 15,275 |
| `localized_texts` | 263,635 | | `npc_spawner_npcs` | 15,967 |
| `unit_reqs` | 13,354 | | `crafts` | 7,010 |
| `zones` | 218 | | `loots` | 13,823 |
| `world_groups` | 6 | | `ui_texts` | 6,349 |

### 1.2 Target-query data verified present

- `skill 23085` = "소환수 성장의 파트라슈 빵" (Patrashu bread, +50,000 mate XP);
  `skill_effects` row 25546 → effect 32617.
- `item 29040` = "파트라슈 빵" (Patrashu bread), `use_skill` 23085.
- `quest_act_obj_mate_levels`: 10 rows; 6 live carrier quests (5430/5464/5465/5466/5812/5813),
  4 orphaned detail rows (3/4/5/11). See sibling dossier
  `scorecard-explorations/mechanics/mate-level-objective-research.md`.

---

## 2. AAEmu source (packet/protocol + engine)

| Path | Source type | Logical domain | Size / count | Encoding | Version | Mutability | MCP-safe |
|---|---|---|---|---|---|---|---|
| `AAEmu.Game/Core/Packets/` | C# source | Wire protocol: C2G 259, G2C 401, C2S 18, S2C 16, G2L 6, L2G 5, Proxy 23 = **728 packet files**; `*Offsets.cs` (CSOffsets, SCOffsets, CTOffsets, TCOffsets, GLOffsets, LGOffsets, PPOffsets) | 728 .cs | UTF-8 C# | fork `develop` | Read-only (source) | YES (read) |
| `AAEmu.Game/Core/Managers/` | C# source | Runtime managers (SkillManager, ItemManager, QuestManager, MateManager, NpcManager, SpawnManager, etc.) | 125 entries | UTF-8 C# | fork `develop` | Read-only | YES (read) |
| `AAEmu.Game/GameData/` | C# source | Typed loaders from `compact.sqlite3` (NpcGameData, ItemGameData, QuestGameData, MateGameData, SkillGameData, etc.) + `Framework/GameDataManager` | ~24 loaders | UTF-8 C# | fork `develop` | Read-only | YES (read) |
| `AAEmu.Game/Models/Game/` | C# source | Domain models (Skills, Items, Quests, Mate, NPChar, World, etc.) | dir tree | UTF-8 C# | fork `develop` | Read-only | YES (read) |
| `AAEmu.Game/Scripts/Commands/` | C# source | In-game GM/admin commands | 94 files | UTF-8 C# | fork `develop` | Read-only | YES (read) |
| `AAEmu.Game/Utils/DB/SQLite.cs` | C# source | **Canonical read-only SQLite accessor** (`SQLite.CreateConnection`, `Mode=ReadOnly`) | 1 file | UTF-8 C# | fork `develop` | Read-only | YES (reuse) |
| `AAEmu.Game/Utils/DB/SQLiteWrapperReader.cs` | C# source | Typed SQLite reader | 1 file | UTF-8 C# | fork `develop` | Read-only | YES (reuse) |
| `AAEmu.Game/IO/` | C# source | Client file access (`ClientFileManager`, `ClientSource`) | 4 files | UTF-8 C# | fork `develop` | Read-only | YES (read) |
| `AAEmu.Commons/IO/FileManager.cs` | C# source | AAPak / app-path helper | 1 file | UTF-8 C# | fork `develop` | Read-only | YES (read) |
| `AAEmu.Game` total | C# source | Full game server | **2,550 .cs** | UTF-8 C# | fork `develop` | Read-only | YES (read) |

---

## 3. SQL base / update / patch files

| Path | Source type | Logical domain | Size / count | Encoding | Version | Mutability | MCP-safe |
|---|---|---|---|---|---|---|---|
| `SQL/aaemu_game.sql` | MySQL DDL | Mutable game-state schema (characters, items, quests, housing, mails, auction) | 688 lines | SQL (MySQL 8) | fork `develop` | Read-only (schema source) | YES (read) |
| `SQL/aaemu_login.sql` | MySQL DDL | Login/account schema | 47 lines | SQL | fork `develop` | Read-only | YES (read) |
| `SQL/updates/` | MySQL DDL/DML | Incremental schema updates (2019–2026) | **69 files** | SQL | fork `develop` | Read-only | YES (read) |
| `SQL/patches/compact/` | SQLite DML | Intentional `compact.sqlite3` fixups (quest drops, data defects) | **15 files** | SQL | fork `develop` | Read-only (patch source; applied to E2E stacks only) | YES (read) |
| `SQL/examples/` | MySQL DML | Example seed data (ICS, test user) | 2 files | SQL | fork `develop` | Read-only | YES (read) |

---

## 4. Client files / game_pak / extracted client (local-machine paths)

| Path (local) | Source type | Logical domain | Size / count | Encoding / parseability | Version / provenance | Mutability | MCP-safe |
|---|---|---|---|---|---|---|---|
| `game_pak` (24.8 GB) — hardlinked at `/root/aaemu-e2e/runtime/game-data/ClientData/`, `/root/aaemu-e2e-mcp/runtime/game/ClientData/`, `/root/aaemu-e2e-mcp/runtime/game-data/ClientData/`, `/root/perf-hardlink-lane/ClientData/` (inode 1173359, 4 links); separate copy `/root/hl-cp-test/ClientData/` (inode 6326327) | AAPak archive | Full client asset pack (models, geodata, UI scripts, strings) | 24,885,257,728 B (24.8 GB); **218,068 entries** | AAPak (header `primary\nnormal_mode\nbuiltin\nescape…`); entries listable/extractable via `paktool`; not directly text-parseable | ArcheAge 1.2 (Feb 2023) | Read-only | **YES (list/extract via tool; do NOT stream whole file)** — when `ARCHEAGE_PAK_PATH` is configured, the MCP exposes the archive read-only via `list_pak_entries`/`read_pak_entry` (bounded listing, 1 MiB reads, never streamed wholesale); otherwise both tools return a deterministic `not configured` error. See [`archaeology-mcp-acceptance.md`](archaeology-mcp-acceptance.md) |
| `/root/aaemu-pak-lua/dec/` | Decompiled Lua | Client UI scripts (x2ui: questcontext, inventory, mailbox, expedition, dominion, justice, etc.) + gamerules | **731 `.lua` files, 4.5 MiB** | Plaintext Lua 5.1 (decompiled from `.alb` bytecode, zero failures) | 1.2 client | Read-only | **YES — high value** |
| `/root/aaemu-pak-lua/ext/game/scriptsbin/` | Raw Lua bytecode | `.alb` UI scripts (x2ui + gamerules) | **732 files** | Lua 5.1 bytecode (`\27LuaQ` v0x51), plaintext string constants | 1.2 client | Read-only | YES (via decompiler) |
| `/root/aaemu-pak-lua/ext-bin/bin32/` | Client binaries | `archeage.exe(.nohs/.original)`, `x2game.dll`, `x2ui.dll`, `x2common.dll`, `xlcommon.dll`, `crygame.dll` | 8 files | PE binaries; x2ui.dll string-obfuscated (not text-parseable) | 1.2 client | Read-only | YES (metadata only) |
| `/root/aaemu-pak-lua/all-list.txt` | Text index | Full pak enumeration (path<TAB>size) | **218,068 lines** | Plaintext | 1.2 client | Read-only | YES |
| `/root/aaemu-pak-lua/lua-list.txt` | Text index | Lua script entries | 234 lines | Plaintext | 1.2 client | Read-only | YES |
| `/root/aaemu-pak-lua/x2ui-list.txt` | Text index | UI script entries | 783 lines | Plaintext | 1.2 client | Read-only | YES |
| `/root/aaemu-nav-paktool/pak-bai-listing.txt` | Text index | BAI (navmesh) entries | 9,460 lines | Plaintext | 1.2 client | Read-only | YES |
| `/root/aaemu-nav-paktool/` | Tool + probes | Navmesh/geodata probes (`navprobe`, `areacheck`, `famlist`, `corridor-probe.txt`) | dir tree | C#/text | fork-local | Read-only | YES (read) |
| `/root/aaemu-pak-lua/paktool/` | Tool | AAPak list/extract/strings (.NET 10, refs AAEmu.Commons) | 1 csproj + Program.cs | C# | fork-local | Read-only | YES (reuse) |

---

## 5. World spawn data, scripts, tools

| Path | Source type | Logical domain | Size / count | Encoding | Version | Mutability | MCP-safe |
|---|---|---|---|---|---|---|---|
| `tools/quest-graph/` | Python tool | Builds graphify-format quest knowledge graph from `compact.sqlite3` (`build-quest-graph.py`, `report-zone.py`) | 2 py + README | Python 3 | fork-local | Read-only | YES (reuse) |
| `tools/gamedata-graph/` | Python tool | Builds graphify-format all-content graph (`build-gamedata-graph.py`) | 1 py | Python 3 | fork-local | Read-only | YES (reuse) |
| `tools/quest-scenario/gen-manifests.py` | Python tool | Quest scenario manifest generation | 86,216 B | Python 3 | fork-local | Read-only | YES (reuse) |
| `tools/scorecard/` | Python tool | Scorecard table dump / enrich / fetch-issues | 4 py | Python 3 | fork-local | Read-only | YES (reuse) |
| `tools/data/npc-spawn-z-fix.py` | Python tool | NPC spawn Z-fix | 8,374 B | Python 3 | fork-local | Read-only | YES (reuse) |
| `Scripts/*census.sh` | Shell | Read-only `sqlite3` census queries against `compact.sqlite3` (quest_no_start, quest_act_ref_missing, quest_next_missing, unit_reqs_missing_context) | 4 sh | Shell | fork-local | Read-only | YES (reuse) |
| `Tools/WorldConverter/`, `Tools/UpdatesForTransform/`, `Tools/NavGCostProbe/`, `Tools/sit-pose-census/` | C#/py tools | World conversion, transform updates, nav cost probe, sit-pose census | dir trees | C#/py | fork-local | Read-only | YES (read) |

---

## 6. Existing graphs / document maps / indexes

| Path | Source type | Logical domain | Size / count | Encoding | Version | Mutability | MCP-safe |
|---|---|---|---|---|---|---|---|
| `tools/quest-graph/` + `tools/gamedata-graph/` | **Builders only** | Graphify-format knowledge graphs (quest + all-content) | 3 py | Python 3 | fork-local | Read-only | YES (reuse) |
| `graphify-out/` (repo) | **ABSENT** | Output graphs (`quest-graph.json`, `gamedata-graph.json`, `merged-graph.json`) | **0 files** (gitignored; not generated) | — | — | — | N/A |
| `/root/graphify-out/` | Graphify output | **Hermes** code graph (`.hermes` sources), NOT AAEmu | graph.json 1.7M + manifest 486K | JSON | hermes | Read-only | NO (wrong project) |
| `scorecard-explorations/mechanics/` | Markdown dossiers | **Pre-digested canonical archaeology**: `mate-level-objective-research.md`, `ability-level-objective-research.md`, quest-implementation-guide, navigation/justice/pvp/ships/mail/dominion/economy/indun/fishing domains | 18 files | Markdown | fork-local | Read-only | **YES — high value** |
| `scorecard-explorations/generated/` | Markdown + JSON/JSONL | Dated evidence reports + trace samples (`b1-trace-samples.json`, `leveling-loop-2026-08-25.jsonl`, `m5.3-core-surface-exit.jsonl`, `m7-adventurer-spike.jsonl`) | 28 md + 4 json/jsonl | Markdown/JSON/JSONL | fork-local | Read-only | YES (read) |

---

## 7. Mutable state (MySQL — NOT canonical reference)

| Path | Source type | Logical domain | Size / count | Encoding | Version | Mutability | MCP-safe |
|---|---|---|---|---|---|---|---|
| MySQL `aaemu_game` (e2e-mcp, 127.0.0.1:3310) | MySQL 8 | Mutable game state: characters, items, quests, completed_quests, mates, skills, slaves, housings, mails, auction, expeditions, dominions | **35 tables** | MySQL 8 | fork `develop` | **Read/write** | **NO — do not expose** (mutable, per-project rule) |
| MySQL `aaemu_login` (e2e-mcp, 127.0.0.1:3310) | MySQL 8 | Accounts, 2FA, bans | small | MySQL 8 | fork `develop` | Read/write | **NO** |

---

## 8. E2E / soak roots (DO NOT TOUCH — excluded from MCP allow-list)

| Path (local) | Contents | compact.sqlite3 variant |
|---|---|---|
| `/root/aaemu-e2e/` | soak logs, runtime/game + runtime/game-data | game/Data = **719b** (indun-patched); game-data/Data = **78b3** canonical |
| `/root/aaemu-e2e-a5/`, `aaemu-e2e-a5-tier3-sixhour/`, `aaemu-e2e-mcp/`, `aaemu-e2e-pb003/`, `aaemu-e2e-soak2/`, `aaemu-e2e-t3/`, `aaemu-e2e-perf/` | E2E/soak runtime stacks | all **78b3** canonical |
| `/root/aaemu-soak/`, `/root/aaemu-botctrl/`, `/root/aaemu-dev-b1um/`, `/root/aaemu-wip-backup-20260812/` | soak / botctrl / backup clones | — |

**Variant note:** the only difference between the two compact variants is the
`indun_*` tables (e2e/runtime/game/Data has the `2026-08-25_indun_hadir_completion.sql`
patch applied: `indun_actions` 105 vs 104, `indun_events` 72 vs 70, etc.). All other
679 tables are row-identical. The canonical `78b3bdbf…` is the reference for the
archaeology server.

---

## 9. Missing sources (gaps)

1. **No full extracted client directory** — only `game_pak` (archive) + partial `bin32`
   binaries + decompiled UI Lua subset. No unpacked models/geodata tree.
2. **No AAEmu graph.json outputs** — `tools/quest-graph`/`gamedata-graph` builders exist
   but `graphify-out/` is empty (gitignored, never generated). Would need to be built.
3. **No CSV/TSV files** anywhere in the repo.
4. **No packet capture (`.packet`) files** — gitignored; none present.
5. **No heightmaps** (`hmap.dat` gitignored; not present).
6. **No existing archaeology MCP project** — was greenfield at inventory time
   (2026-08-30, confirmed by the companion foundation report); the read-only
   archaeology MCP (`AAEmu.ArchaeologyMcp/`) has since been built and accepted
   (see [`archaeology-mcp-acceptance.md`](archaeology-mcp-acceptance.md)).
7. **No localized-text graph nodes** — `localized_texts` (263,635 rows) is not graphed
   by the graph builders (documented scope boundary).
8. **No live client** — no running ArcheAge client; only server-side data.

---

## 10. Security recommendations (for allowlisted MCP root)

1. **Primary allow-list root:** `AAEmu.Game/Data/` (compact.sqlite3 + Worlds + XML/JSON +
   Portal/Path/Bots) — read-only, canonical, safe.
2. **Secondary allow-list roots:** `AAEmu.Game/` source (packets/managers/GameData/Models/
   Scripts), `SQL/`, `tools/`, `Scripts/*census.sh`, `scorecard-explorations/` (dossiers),
   and the extracted client roots `/root/aaemu-pak-lua/dec/` + `/root/aaemu-pak-lua/*.txt`
   indexes.
3. **`query_sql` guard:** open `compact.sqlite3` with `Mode=ReadOnly` (reuse
   `SQLite.CreateConnection`); reject any non-`SELECT`/`PRAGMA`/`EXPLAIN` statement;
   reject `;`-separated multi-statements and
   `INSERT/UPDATE/DELETE/DROP/ALTER/ATTACH/DETACH/VACUUM/REINDEX/CREATE`; cap rows
   (e.g. 1000) and columns (e.g. 50).
4. **`search_files` guard:** restrict to the allow-list roots; reject `..` and absolute
   paths outside the allow-list; cap result count.
5. **`game_pak`:** do NOT stream the 24.8 GB file through MCP. The archive is exposed
   read-only through the configured AAPak catalog/tool surface
   (`list_pak_entries`/`read_pak_entry` when `ARCHEAGE_PAK_PATH` is set — bounded
   listing, 1 MiB reads, never streamed wholesale), plus the entry indexes
   (`all-list.txt` etc.) and tool-based extraction (`paktool`); never raw file reads.
6. **Exclude all E2E/soak roots** (`/root/aaemu-e2e*`, `/root/aaemu-soak*`,
   `/root/aaemu-botctrl*`) and **all MySQL** (mutable state) from the MCP allow-list.
7. **No mutation surface:** the archaeology server must expose no POST/mutation tools —
   read-only by construction.
8. **`compact.sqlite3` read-only invariant:** after any tool run, md5 must remain
   `78b3bdbf038db3b927056106efdf91af` (the read-only connection guarantees this).

---

## 11. Top 5 sources required for the first useful queries

For `search_everything(23085)`, `trace_skill(23085)`, `find_quest_objectives(MateLevel)`:

1. **`AAEmu.Game/Data/compact.sqlite3`** (canonical 679-table reference DB) — the single
   source for all three: `skills`/`skill_effects`/`skill_reqs` (23085), `items` (29040),
   `quest_act_obj_mate_levels`/`quest_acts`/`quest_components`/`quest_contexts` (MateLevel),
   `npcs`/`npc_spawners`/`npc_spawner_npcs` (spawn context), `localized_texts` (names).
2. **`AAEmu.Game/` source** (packets `Core/Packets/*Offsets.cs` + `Core/Managers/`
   SkillManager/ItemManager/QuestManager/MateManager + `GameData/` loaders) — for
   `trace_skill(23085)`: the effect 32617 → `AddExp` code path, and
   `find_quest_objectives` engine semantics.
3. **`AAEmu.Game/Data/Worlds/`** (world spawn JSON) — for `search_everything(23085)` /
   `find_quest_objectives`: which spawners place the relevant NPCs (e.g. mate summon
   NPCs 7043/5430/5434/5435/13432/13519) and where.
4. **`/root/aaemu-pak-lua/dec/`** (decompiled client UI Lua, esp. `x2ui/questcontext/`,
   `x2ui/inventory/`, `x2ui/logic/item.lua`) — client-side behavior for
   `trace_skill(23085)` (item-use UI) and `find_quest_objectives` (quest journal UI).
5. **`scorecard-explorations/mechanics/mate-level-objective-research.md`** (+
   `ability-level-objective-research.md`) — pre-digested canonical archaeology: exact
   `quest_act_obj_mate_levels` rows, 6 live carrier quests, orphaned rows, and the
   23085/29040 `MotherFactionOnly=5` blocker — the fastest path to correct answers for
   all three queries.
