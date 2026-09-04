# Undefined World Mechanics Dossier (2026-08-31) — read-only census of data/code surfaces with no gameplay consumer

Durable exploration dossier. Source: the verified read-only archaeology report
`/tmp/undefined-mechanics-exploration.md`, re-checked against this repository
(data counts re-queried from canonical `compact.sqlite3`; code claims re-verified
by `grep`/`read`). **No gameplay/code/data change, no A5/E2E/soak root, no
commit.** Every finding here is a *discovery*, not a fix.

## Provenance

| Item | Value |
|---|---|
| Repo | `/root/aaemu-dev` (fork joshhmann/AAEmu, ArcheAge 1.2 emulator, .NET 10) |
| Branch | `develop` |
| **HEAD** | `0f8254dc3d914193d432fb842169e9bb07075508` (verified `git rev-parse HEAD`) |
| Canonical DB | `AAEmu.Game/Data/compact.sqlite3` |
| **DB md5** | `78b3bdbf038db3b927056106efdf91af` (before and after the source exploration; read-only invariant held; this dossier re-queries SELECT-only) |
| DB version | 1.2 `r208022`, 679 tables, 119,054,336 B |
| Evidence layers | **data** (canonical `compact.sqlite3` rows, re-verified via `sqlite3`) and **code** (repo C# source, verified via `grep`/`read`) only. |
| No live/client/H claims | **No game server launched, no client run, no authenticated run.** `game_pak` not opened (unconfigured). `H` (human/client feel) is **UNKNOWN** for every chain below. No physics/collision data exists in the canonical DB (only `physical_enchant_abilities` 86, `physical_explosion_effects` 58). |

## Method and honest limits

- All 679 table names were swept against `AAEmu.Game/` + `AAEmu.Commons/` C#
  source (word-boundary regex, build output excluded) to find tables with zero
  `.cs` references (211) and loaded-but-unconsumed tables; every headline
  candidate below was then traced through its actual runtime dispatch (loader,
  manager, packet handler, doodad-func `Use` path) before classification.
- **Classification ladder** (conservative, used throughout): **truly
  undefined** = dedicated data rows exist AND no loader/dispatch consumes them
  (or consumes them into no-ops) AND the surface is player-reachable in-world;
  **partial / undefined dispatch** = data + model loaded, hardcoded or absent
  runtime dispatch; **data-only** = canonical data exists, zero or no-op code
  consumption, no spawned/reachable object on the canonical data alone;
  **loaded, zero consumers** = loader present, nothing uses it;
  **unknown** = rows exist but semantics are opaque without client/reverse-
  engineering evidence. Already-tracked surfaces were explicitly rejected
  before proposing anything new.
- **Every count re-verified** against the canonical DB with `sqlite3` during
  this write-back; code anchors re-verified by `grep`/`read` at HEAD
  `0f8254dc3d914193d432fb842169e9bb07075508`. `truncated:false` held for all
  bounded queries in the source exploration. `git status --porcelain` shows
  only pre-existing modifications; no repo file was edited by the source
  exploration and none is edited by this dossier.
- **No claim of "missing" is made from absence of a live test** — every gap is
  grounded in a concrete data table a loader never reads or a dispatch path
  that ignores it (the "not data-driven" bar), or in a spawned world object
  whose funcs are no-ops.
- `search_everything`/`trace_references` were not relied on (documented scan
  caps); direct SQL + source reads only.
- **Bounds:** data+code evidence only. `H` UNKNOWN everywhere. The Whirlpool /
  sea-weather ship work (`StormShipLogic`, `SeaWeatherModel`, `PhysicsManager`
  sea-weather) IS implemented and explicitly out of scope; the Candidate-9
  climate/weather *state* below is the per-zone weather-state surface,
  distinct from that ship work.

---

## NEW-1 — `AGGRO-PACK-01`: NPC aggro-link packs — **TRULY UNDEFINED** (high confidence)

### Data (VERIFIED this pass)
- `aggro_links` = **130 rows** (`id, name, comment`): named packs — `10`
  "집단 생활 동물류" (group-living animals), `101` "인던_하디르의농장" (Hadir's
  farm), `102` "서방왕궁지하_어그로 링크" (west-palace aggro link), `27`
  "십자별 평원 - 일리온 원혼 무리" (Cross-star Plains Ilion Wraith pack),
  `7` "놀 류" (Troll family).
- `npc_aggro_links` = **643 rows**; **572 distinct NPCs**, **126 distinct
  links**, **111 links with >1 distinct NPC** (real shared-pull packs). Pack
  27 covers NPCs 2325–2329 + 2441; pack 7 covers 2024/2025/2098 (VERIFIED).

### Code (VERIFIED)
- `NpcManager.cs:547-560` loads the per-NPC *helper columns* into `NpcTemplate`
  (`accept_aggro_link`, `aggro_link_special_rule_id`, `aggro_link_help_dist`,
  `aggro_link_sight_check`, `aggro_link_special_guard`,
  `aggro_link_special_ignore_npc_attacker`).
- **Neither `aggro_links` nor `npc_aggro_links` is read anywhere in C#**
  (full-repo grep: zero hits). The runtime "help" path
  `Models/Game/AI/v2/Framework/Behavior.cs:375-418` uses ONLY a
  distance + faction-special-rule heuristic over
  `Template.AggroLinkHelpDist`/`AggroLinkSpecialRuleId`
  (`AggroLinkSpecialRuleKind` enum) — it **never consults which NPCs share an
  `aggro_link_id`**. Pack membership (the entire point of `npc_aggro_links`)
  is absent: two NPCs in the same canonical pack share aggro only if they
  happen to be within `help_dist` and pass the faction test.
- `aggro_link_special_rule_id` is used only inside that heuristic — it is not
  driven by pack membership.

### Classification & player visibility
**Truly undefined.** ~572 NPC templates in 126 packs (111 multi-member) will
not shared-pull as retail: a member pulled alone fights alone unless another
happens to fall within the generic help distance. Affects dungeon trash packs
and overworld herd/wraith groups. **Distinct from the `quest_act_obj_aggro`
objective system** (already catalogued under PB-002 — 37 rows/30 canonical
quests): those are quest objectives, not NPC-vs-NPC pack linkage.

---

## NEW-2 — `RESPAWN-LADDER-01`: death/resurrection wait-time ladder — **DATA-ONLY / hardcoded mismatch** (high confidence)

### Data (VERIFIED this pass)
- `resurrection_waiting_times` = **10 rows** `(id, penalty_duration,
  waiting_time, siege_waiting_time)`: id 1→(600, 0, 20); 2→(600,5,15);
  3→(600,45,10); 4→(600,90,5); 5..10→(600,180,0). Escalating respawn
  countdown, a **siege-specific** ladder, and a **600 s penalty window** per
  death, all in the canonical table.

### Code (VERIFIED)
- Table **never read** (zero `.cs` hits). Only the per-character record's
  persisted MySQL `rez_wait_duration`/`rez_penalty_duration` columns are read
  (`Character.cs`), not this template table.
- `CharacterCombat.cs:31-32` **hardcodes**
  `DeathWaitTimesSeconds = [15, 30, 60, 90, 120, 150, 180, 210, 240]` and
  `DeathCountResetMinutes = 5` — a ladder that **does not match** the
  canonical `waiting_time` (0,5,45,90,180,180,…). The canonical **siege**
  ladder (20,15,10,5,0) and the **penalty window** (600) are entirely ignored.
- Post-revive cooldown also hardcoded (`CharacterResurrection.
  RespawnCooldownDurationMs = 300_000`).

### Classification & player visibility
**Data-only / hardcoded mismatch.** The code path is demonstrably
non-data-driven (not "untested"). In-world respawn countdown
(`SCUnitDeathPacket.resurrectWaitingTime`) and post-revive penalty span
diverge from 1.2 retail data. Distinct from the implemented
CharacterCombat/CharacterResurrection death-watch plumbing — this is the
template-data gap beneath it. Sits under the existing **COMBAT-01** row
(PvE combat/death/resurrection; W=1), which this dossier refines.

---

## NEW-3 — `AUCTION-BANK-DOODAD-01`: Auction-House/Bank doodad funcs — **TRULY UNDEFINED** (high confidence, spawned object)

### Data (VERIFIED this pass)
- `doodad_func_auction_uis` = **2 rows**; `doodad_func_bank_uis` = **2 rows**
  (ids 1,2). `doodad_funcs` carries 2 `DoodadFuncAuctionUi` + 2
  `DoodadFuncBankUi` rows referencing doodad templates **7983** "무인 창고"
  (unattended warehouse) and 6669 "코끼리 시체_테스트" (elephant-corpse test).
- **7983 IS spawned in the world:** `AAEmu.Game/Data/Worlds/arche_mall_world/
  doodad_spawns.json:184` `"UnitId": 7983` ("Auctioneer/Warehouse") at
  (3452.5, 4289.4, 108.5), func-group 22065 carrying the two funcs.

### Code (VERIFIED)
- `DoodadFuncAuctionUi.cs` and `DoodadFuncBankUi.cs` exist and are
  **`Logger.Trace` no-ops** (both at `:11` of `Use`).
- The two tables are **never SELECTed** (zero hits in `DoodadManager`); the
  funcs are loaded only as template keys. `DoodadFunc.Use` (`Models/Game/
  DoodadObj/DoodadFunc.cs`) null-safe-dispatches via
  `GetFuncTemplate` (`DoodadManager.cs:2922`) — for an unloaded key it
  returns null and **nothing happens**.
- The real AH/bank *open* is hardcoded only for **NPC** right-click
  (`CSStartInteractionPacket.cs:36-56`: `Template.Banker→UseWarehouse`,
  `Template.Auctioneer→UseAuctioneer`, …). The fork currently has **zero**
  `npcs` with `banker=1` and **zero** with `auctioneer=1` (VERIFIED), so even
  the hardcoded NPC open path is unreachable via the current
  `CSStartInteraction` chain; the world's only auction/warehouse kiosk (7983)
  is a doodad whose two funcs are unloaded no-ops.

### Classification & player visibility
**Truly undefined.** Real data rows + a spawned, player-reachable world
object (Mirage Island "Auctioneer/Warehouse" kiosk) whose interaction funcs
are no-ops and unloaded — the kiosk is non-functional. Distinct from the
hardcoded NPC path (which is itself NPC-data-dead today). Adjacent to but
separate from the tracked **STORAGE-01** row (bank/coffer access), which this
finding does not promote.

---

## NEW-4 — `NPC-INTERACTION-01`: NPC interaction sets — **PARTIAL / undefined dispatch** (high confidence)

### Data (VERIFIED this pass)
- `npc_interaction_sets` = **111 rows** (e.g. "동대륙 여가정교사",
  "전승/공용어 배우기", "금평야 전쟁 승리 축하").
- `npc_interactions` = **114 rows** (`npc_interaction_set_id, skill_id`),
  **113 distinct skills** (~21335–25256: common-tongue learning 21366/21521/
  21520, "무혐의 입증" 21335, war-victory celebration 23146, "진실의 확인"
  21595).
- **142 NPCs** have `npc_interaction_set_id > 0`, referencing **107 distinct
  sets** (VERIFIED).

### Code (VERIFIED)
- `NpcTemplate.NpcInteractionSetId` is loaded (`NpcManager.cs:574`) into the
  model (`NpcTemplate.cs:75`) — **zero consumers** (full-repo grep: only the
  loader + the property). It never builds the interaction menu.
- `CSStartInteractionPacket.cs:20-58` sends the client right-click menu from
  a **hardcoded option chain** (`SkillsEnum` per
  `Template.Banker/Auctioneer/Priest/…`), and
  `SCNpcInteractionSkillListPacket` documents the client "will use the first
  one regardless" (`CSStartInteractionPacket.cs:28`) — the data-driven
  per-NPC skill sets are never surfaced.

### Classification & player visibility
**Partial / undefined dispatch** — the set id is loaded into the model but
the interaction-skill dispatch is hardcoded and never reads
`npc_interaction_sets`/`npc_interactions`. Per-NPC skill menus (language
learning, celebration skills) are not data-driven. (The sibling
`quest-reachability.md` correctly established these skills are not quest ids;
it did not examine their runtime dispatch.)

---

## NEW-5 — `BOOK-01` (formalized): readable books / open-paper items — **DATA-ONLY / unwired** (high confidence; existing row, evidence refreshed)

Prior `BOOK-01` row said "wiring unverified this pass" (mechanic-inventory
§3#26, SCORECARD 2026-08-25). This exploration **verifies it is NOT wired**.

### Data (VERIFIED this pass)
- `books` **72**, `book_pages` **1206**, `book_page_contents` **1873**,
  `book_elems` **846**. `book_page_contents.text` holds Korean lore text
  (e.g. page 700 "하늘에서 너를 보는 눈이 있으니…").
- `item_open_papers` = **551 rows** (`item_id, book_page_id, book_id`):
  **551 distinct items**, **550 resolving to an `items` row**,
  **541 links to a real `book_page_id`**; 491 in the quest ("퀘스트")
  category, 53 in "책" (book). Samples (VERIFIED): 29236 "용 사냥꾼
  민레이나의 일지" → book 10 (용 사냥꾼 민레이나의 일지), 29237 "마법사왕
  이야기" → book 11, 20031 "아이샤의 편지" → page 66.

### Code (VERIFIED)
- `ItemImplEnum.OpenPaper = 23` (`Models/StaticValues/ItemImplEnum.cs:28`) —
  **no item handler consumes it** (no `OpenPaper` case in the item-use path;
  grep over item handlers/effects for a book/open-paper consumer is empty).
- **No book-render packet exists** (grep over `Core/Packets` for
  BookPage/SCBook/CSReadBook/SCReadBook/SCOpenPaper; `SCOffsets.cs` has no
  such constant).
- `doodad_func_open_papers` + `DoodadFuncOpenPaper` exist but are a no-op
  (`DoodadFuncOpenPaper.cs:14` is `Logger.Trace`); it is the only
  `book_page_id` consumer and does nothing.

### Classification & player visibility
**Data-only / unwired.** The full readable-book content graph (72 books →
1206 pages → 1873 text rows) and the item→page link (551 items) are entirely
unreachable in-game; no in-world readable lore book or quest document opens
any content. This formally upgrades the "wiring unverified" wording of the
2026-08-25 row to verified-unwired.

---

## EXPLORATION-ONLY — `NPC-GROUP-01`: NPC group/member data — **LOADED, ZERO CONSUMERS** (lower priority; no ledger row)

- **Data (VERIFIED):** `npc_groups` = 158, `npc_group_members` = 571.
- **Code (VERIFIED):** `GameData/NpcGroupGameData.cs:44-89` loads both and
  exposes `GetNpcGroup`/`GetNpcGroupMembers` — **zero external consumers**
  (full-repo grep: only the loader file references it).
- **Classification:** partial (loaded) / undefined; distinct from
  AGGRO-PACK-01 (no aggro-link semantics). Player-visible only indirectly
  (grouped spawn/behavior data unused) and overlaps the "AI & spawn
  simulation" surface that mechanic-inventory §1d deliberately catalogues as
  **non-player-facing**. **Remains exploration-only — no SCORECARD row, no
  roadmap track.** If a future slice needs spawn/group composition, it is
  catalogued here.

---

## Medium / non-new signals (captured, NOT headline rows)

| Signal | Verified counts | Evidence | Status |
|---|---|---|---|
| `common_farms` (public-farm definitions) | 17 rows, `guard_time` 86400000 (24 h), named farm/stables/ranch/arboretum locations | `PublicFarmManager.cs` **hardcodes** subzone→type map `{998,966→Farm, 968→Nursery, 967→Ranch, 974→Stable}` (`Load()`) and never reads `common_farms`; `CommonFarmGameData` loads `farm_groups`/`farm_group_doodads`/`doodad_groups` only; guard time comes from `doodad_groups.guard_on_field_time` | data-only — the *data-table* gap behind the **tracked** PUBLICFARM-01 (dossier pending); no new row |
| `climates` / `zone_climates` | 6 / 10 rows | `zone_climate_elems` IS wired for farm-growth climate bonus (`ZoneManager.cs:204`, `DoodadFuncGrowth`/`DoodadFuncClimateReact`); `climates`/`zone_climates` never read; **no per-zone weather-state system** — only `SCOnOffSnowPacket` + `WorldManager.IsSnowing` driven by a GM `/snow` command (`WorldManager.cs:111,1306`) | partial/data-only — growth wired, weather-state absent; lower priority than NEW-1..5 |
| `merchant_packs` | 263 rows (`name` = `"npc.3939"` template labels; 154 owner NPCs) | stock loads from `merchant_goods` (`NpcManager.cs:870`), which works; the pack name/owner meta layer never read | data-only / label-only; same class as `auction_a/b/c_categories` and `specialty_bundles` (explicitly de-prioritized in partial-domains.md) |
| `content_configs` / `world_var_defaults` / `world_spec_configs` | 53 / 1 / 1 rows | never read; columns are opaque (`id, kind_id, value`); `world_var_defaults`' single row `var_delay_siege_x_days_after_dominion_declare=30` is a **data signal for the already-tracked dominion/siege gap**, not a new mechanic | data-only / **unknown semantics** — lowest confidence; flagged, not a roadmap item |

## Explicitly rejected / already-tracked (ruled out before proposing)

siege/dominion (DOMINION-01, M10 slices), ranks (RANKS-01), premium
(PREMIUM-01), moulds (MOULD-01, data-dead), race-tracks (RACETRACK-01),
music (MUSIC-01 — ~90% wired), fx-visuals (client-side by design), item
socketing/procs/recipe/accept-quest (ITEM-01 scope), mate-equip (MATE-01,
wired 0482ba3f0), housing (HOUSING-01/PROPERTY-01), models (client-side
labels), auction categories (AUCTION-01 — search works on `items` ids),
specialty-bundles (PACK-01 label), common-farm runtime (PUBLICFARM-01),
achievements (ACHIEVEMENT-01), appellations (APPELLATION-01), emotes
(EMOTE-01), tower-defense (TOWERDEF-01), navigation (PB-001), justice
(CRIME/TRIAL/PRISON-01), mail (MAIL-01), economy (MERCHANT/ECON-01),
indun (INDUN-01 — see formalization section), pvp (PVP-01/PB-007), physics/
collision (no canonical collision data exists). The Whirlpool / sea-weather
ship work is implemented and out of scope.

---

## INDUN-01 — formalization section (roadmap/scorecard audit support; NOT a new discovery)

Per the read-only roadmap mechanics-gaps audit (verified ledger census: 66
unique `*-01` rows; **INDUN-01 has ZERO ledger rows** — the mechanic-inventory
2026-08-25 row 22 claim "tracked / next action none" was contradicted by the
actual SCORECARD ledger; real ledger coverage is 63/65, not 64/65):

- **It is not a new discovery.** The existing `indun-domain.md` dossier
  (2026-08-24) documents structured implementation: `IndunManager` with
  entry requirements (level/party/ticket/visit count), per-party `Dungeon`
  objects on real isolated `WorldInstance`s, queue-during-load, solo→party
  conversion, kick-on-leave, 24 h solo expiry, access-flag reset, in-memory
  4 h cooldowns; `indun_zones`(20)/`indun_events`(70)/`indun_actions`(104)
  loaded via `IndunGameData`; portal doodad funcs
  (EnterInstance/EnterSysInstance/ExitIndun/RemoveInstance) from client data.
- **Dossier gaps re-affirmed as the formal scope:** low-level dungeons
  **45/46/47/50/51/52 have ZERO scripted completion events** (trash-pull-to-
  boss with no completion trigger); cooldowns are memory-only (lost on
  restart); `RestoreItemTime` dead code; `DungeonLoaderTask` blocking sleeps;
  channel-select TODO. Completion overlay patch
  `SQL/patches/compact/2026-08-25_indun_hadir_completion.sql` (events
  4601/4602) exists.
- **Exit-path evidence (PB-003, CLOSED):** the 2026-08-25 addendum + follow-up
  flipped PB-003 to FIXED with layer correction **DATA → E2E-coverage** —
  `IndunExitE2eTests` 11/11 on the isolated `pb003acc` stack: entry via skill
  17731 → bosses 10166+10167 dead → completion events 4601/4602 → exit via
  live portal doodad 4289 (skill 17733) → `SCLoadInstancePacket`(world 0,
  zone 179) → both members back at the pre-entry anchor, instance 0.
  Report: `/root/aaemu-e2e-pb003/logs/indun-exit-e2e-report.json`.
- **Mapping (per the audit; conservative, H unknown):** Lane D — 1.2 feature
  completeness, world-systems ordering (ROADMAP lane D order-2 already lists
  INDUN). Not a milestone, not M8.
  - **S1 (S):** bot-party clear-then-exit loop, Hadir Farm (46) — loop-closure
    DoD: entry via real portal doodad (`InteractWith` landed 13f502673) →
    final-NPC completion (events 4601/4602) → exit via portal 4289/skill
    17733 → `RequestLeaveInstance` restores `MainWorldPosition`; assert
    instance 0/zone 179/`SCLoadInstancePacket` for both members incl.
    kick-on-leave; no GM/DB/Transform/ZoneId; entry-count persistence
    documented if not persisted. Evidence boundary: C/W dossier + L exit-leg
    (IndunExitE2eTests 11/11); H UNKNOWN. Next action: add ledger row + Lane D
    card; run party-clear-then-exit via PartySpikeScenario/ProvisionBotParty
    seams.
  - **S2 (S-M):** low-level completion hook (45/46/47/50/51/52) — engine-
    composable final-kill→complete path or evidenced per-dungeon overlay
    proving completion through normal play; evidence boundary C dossier gap
    only; next action ruling engine fallback vs data overlay.
  - **S3 (S):** cooldown persistence + channel-select + non-blocking loader —
    restart-safe cooldown with explicit observable outcome; W=1 code gaps;
    card as deviations/fixes; do not block S1.
  - **S4 (L, explicitly deferred):** full phase scripting (Nachashgar 55 /
    easy 66 / Immortal Isle 62,64 / library wings 73-76) — three-scenario DoD;
    do not estimate into S1.
- **Rejected within INDUN formalization:** "full Indun missing" is NOT claimed
  — the dossier shows structured implementation with specific low-level
  completion-hook/cooldown gaps; only those are mapped (S1-S3) with H unknown.

---

## Scorecard row proposals (2026-08-31; see SCORECARD.md ledger additions)

| ID | Mechanic (one-line canonical description) | Classification | Evidence label |
|---|---|---|---|
| AGGRO-PACK-01 | NPC aggro-link packs: shared-pull via `aggro_links`/`npc_aggro_links` membership | truly undefined | data+code; high confidence; H UNKNOWN |
| RESPAWN-LADDER-01 | Data-driven death/resurrection wait ladder (incl. siege ladder + penalty window) | data-only / hardcoded mismatch | data+code; high confidence; H UNKNOWN |
| AUCTION-BANK-DOODAD-01 | Auction-House/Bank UI doodad funcs (spawned kiosk 7983) | truly undefined | data+code; high confidence; H UNKNOWN |
| NPC-INTERACTION-01 | Data-driven per-NPC interaction skill menus (`npc_interaction_sets`/`_interactions`) | partial / undefined dispatch | data+code; high confidence; H UNKNOWN |
| BOOK-01 (refresh) | Readable books / open-paper items — verified unwired | data-only / unwired | data+code; high confidence; H UNKNOWN |
| INDUN-01 (formalize) | Instance dungeons: entry/party/isolation/clear/exit, completion hooks, cooldown persistence | formalization of existing dossier (NOT new) | C/W dossier + L exit-leg (PB-003 11/11); H UNKNOWN |
| NPC-GROUP-01 | NPC group/member data loader | exploration-only (non-player-facing) | data+code; no row |

---

*Boundary notes: quest-objective aggro → PB-002 (quest_act_obj_aggros);
combat/death/resurrection → COMBAT-01 (RESPAWN-LADDER-01 refines it);
bank/coffer access → STORAGE-01; public farms → PUBLICFARM-01; dominion → *
DOMINION-01; all H stay UNKNOWN (hard rule). Cross-ref:
`scorecard-explorations/generated/mechanic-inventory-2026-08-25.md`,
`scorecard-explorations/zero-wired-domains.md`, `partial-domains.md`,
`mechanics/indun-domain.md`.*
