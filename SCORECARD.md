# ArcheAge Slums — Data-Wiring and Evidence Scorecard (enriched)

Layers: (1) canonical 1.2 data surface (679 sqlite tables), (2) code wiring,
(3) upstream issue tracker (AAEmu/AAEmu open issues, 2026-08-03).

The table-wiring percentage is a discovery signal, not a feature-completeness
percentage. A referenced table can back broken behavior, while a complete
mechanic may not require every related table. Milestone readiness is decided
by the human, automated, restart-persistence, and soak evidence in ROADMAP.md;
never raise priority merely to improve a wiring percentage.

# ArcheAge 1.2 Data-Wiring Scorecard

Generated from: compact.sqlite3 r208022 (679 tables) vs AAEmu develop (95 managers).

## Legend
- **Tables**: canonical sqlite tables in the domain
- **Data-wired**: tables referenced by any .cs (server reads this data)
- **Managers**: game systems present in code

## Mechanic completion model

Track player-facing mechanics separately from table wiring and separately from
zone content. Each mechanic gets six evidence dimensions:

| Dimension | What it proves |
|---|---|
| **C — Canonical** | The intended ArcheAge 1.2 behavior and required compact/MySQL/client data are identified. |
| **W — Wired** | Normal runtime code paths exist end-to-end; a manager or referenced table alone is not enough. |
| **H — Human** | A player completes the curated scenario from a reproducible reset state without GM repair. |
| **A — Automated** | Behavior assertions exercise the mechanic, including negative/error paths. |
| **R — Restart** | Logout/restart/crash recovery preserves state without loss or duplication. |
| **S — Soak** | The mechanic survives its milestone load/duration and meets explicit performance/recovery budgets. |

Grades are `U` unassessed, `0` absent/broken, `1` partial or proxy evidence,
`2` verified for the named curated scope, and `N/A` only with a written reason.
Never average the grades. A mechanic is ready for a milestone only when every
dimension required by that milestone is `2`; the weakest required dimension
wins. Every non-`U` grade must link to a query/report, code path, test, human
run, restart run, or soak artifact.

### Global mechanic ledger

This is the prioritized inventory, not a claim that manager presence means the
feature works. The initial `W1` entries only record code surfaces found by
Graphify and must be promoted by an end-to-end exploration.

| ID | Mechanic / scoped scenario | First gate | C | W | H | A | R | S | Evidence / next audit |
|---|---|---:|:---:|:---:|:---:|:---:|:---:|:---:|---|
| PROG-01 | Character creation, login, logout, re-entry | M2 | U | U | U | U | U | N/A | M2 baseline |
| QUEST-01 | Solzreed curated starter quest chain + rewards | M1 | 2 | 1 | U | 1 | 1 | N/A | `Golden-Route-Solzreed.md`; quest scenario harness; `next-missing-776-777.md` (330/776/777 COMPONENT_NEXT_MISSING → 0 via QuestDataOverlay, Rei gate PASS t_d8a8c798); `act-ref-2145-rig.md` (2145→2146 + sibling 1960→1961 ACT_REF_MISSING_QUEST → 0 via `2026-08-05-prune-act-ref-missing-2145.sql`, Rei gate PASS t_53baa876); `no-start-cluster-1533-1548-evidence.md` (QUEST_NO_START cluster 1533–1548 → 0 via `2026-08-05-drop-no-start-cluster.sql` — 23 contexts/25 components/42 acts dropped, Rei gate PASS t_f884383f); `no-components-1391-rig.md` (QUEST_NO_COMPONENTS 1391 empty template → dropped via `2026-08-05-drop-1391.sql` — 1 context dropped, drift −1, Rei gate PASS t_a56e8e2d); real restart still required |
| CTRL-01 | Movement, targeting, interaction, control-state recovery | M2/M5 | U | U | U | U | U | U | Actor-contract spike |
| COMBAT-01 | PvE combat, death, resurrection, loot | M2/M5 | U | 1 | U | U | U | U | `SkillManager`; combat audit |
| ABILITY-01 | Ability selection, skill use, progression | M2 | U | 1 | U | U | U | N/A | `SkillManager`; ability audit |
| ITEM-01 | Inventory, equipment, stacking, split/move, full-inventory errors | M2 | U | 1 | U | U | U | U | `ItemManager`; inventory conservation audit |
| LABOR-01 | Labor consume/regenerate/cap/persist | M3/M4 | U | U | U | U | U | U | Labor/ActAbility audit |
| MATE-01 | Obtain, summon, mount, dismount, persist a mount | M2 | U | 1 | U | U | U | N/A | `MateManager`; golden-route mount scenario |
| HOUSING-01 | Claim land, construct, own, permit, demolish | M3 | U | 1 | U | U | U | N/A | `HousingManager`; homestead audit |
| FARM-01 | Place, grow, harvest, and recover curated crops/livestock | M3 | U | 1 | U | U | U | U | `PublicFarmManager` + Doodad paths; farming audit |
| PROPERTY-01 | Furniture/storage/phase/attachment persistence | M3b | U | 1 | U | U | U | U | Housing/Doodad persistence audit |
| CRAFT-01 | Recipe prerequisites, labor/material consume, output | M4 | U | 1 | U | U | U | N/A | `CraftManager`; selected pack recipe |
| PACK-01 | Craft, carry, place, load, unload, sell trade pack | M4 | U | 1 | U | U | U | U | `SpecialtyManager`; pack audit |
| SLAVE-01 | Cart/ship summon, seats/cargo, cleanup, recovery | M4 | U | 1 | U | U | U | U | `SlaveManager`; vehicle/ship audit |
| TRADE-01 | Direct player-to-player item/currency trade | Later | U | 1 | U | U | U | N/A | `TradeManager`; separate from trade packs |
| MERCHANT-01 | NPC vendor buy/sell, price, stock, refund/error paths | M2/M4 | U | U | U | U | U | N/A | Merchant audit |
| AUCTION-01 | List, search, bid/buy, settle, cancel, expire | M8 | U | 1 | U | U | U | U | `AuctionManager`; market audit |
| ECON-01 | Currency/item/labor conservation across economy | M4/M8 | U | U | U | U | U | U | Cross-mechanic invariant audit |
| MAIL-01 | Send, receive, attach, return, expire, persist | Later | U | 1 | U | U | U | U | `MailManager`; mail audit |
| TRANSFER-01 | Fixed-route transport board/ride/disembark/recover | M4 | U | 1 | U | U | U | U | `TransferManager`; route audit |
| INDUN-01 | Instance entry, limits, party, completion, exit/recovery | M7+ | U | 1 | U | U | U | U | `IndunManager`; selected dungeon audit |
| FISH-01 | Fishing interaction, loot, labor, contest integration | M9.5 | U | U | U | U | U | U | Fishing audit |
| PVP-01 | Flagging, factions, damage, honor, death/recovery | Later | U | U | U | U | U | U | PvP audit |
| DUEL-01 | Invite, accept, bounds, result, cleanup | Later | U | 1 | U | U | U | N/A | `DuelManager`; duel audit |
| CRIME-01 | Crime evidence/points, reporting, persistence | M9 | U | 1 | U | U | U | U | `CrimeManager`; justice audit |
| TRIAL-01 | Arrest, jury selection, testimony, verdict, sentence | M9 | U | 1 | U | U | U | U | `TrialManager`; justice audit |
| PRISON-01 | Imprisonment, sentence time, labor/escape/release | M9 | U | U | U | U | U | U | No `PrisonManager` found; trace model/packet paths before scoping |
| PARTY-01 | Invite/join/leave, leader, follow/assist, recovery | M7 | U | U | U | U | U | U | Party audit |
| EXPEDITION-01 | Expedition membership, roles, persistence | M9/M10 | U | 1 | U | U | U | U | `ExpeditionManager`; organization audit |
| CHAT-01 | Local/zone/party/expedition chat, moderation, bot identity | M7/M8 | U | 1 | U | U | N/A | U | `ChatManager`; social audit |
| ZONE-01 | Peace/conflict/war state transitions and PvP rules | Later | U | 1 | U | U | U | U | `ZoneManager`; conflict-state audit |
| ACTOR-01 | Observe/action lifecycle, rejection, timeout, idempotency | M5 | U | 0 | U | 0 | 0 | U | New contract; architecture spike first |
| BOT-01 | Headless account/session/Character lifecycle | M6 | U | 0 | U | 0 | 0 | U | New fork capability |
| BOT-02 | Deterministic recovery + tick-budget compliance | M6 | U | 0 | U | 0 | 0 | 0 | Staged 30m/1h/6h soak |

Add mechanics as SQL/code/runtime exploration reveals them; use stable IDs so
bugs, cards, tests, and zone reports can refer to the same scope.

## Zone and instance content coverage

Global mechanics and local content are orthogonal. A zone report references
the global mechanic IDs it exercises, then grades its own content surfaces:
quest chains; NPC spawns; Doodad spawns/interactions; merchants/services;
spheres/triggers; transfers/portals; terrain/navigation/spawn-Z; human route;
and restart behavior. Main-world coverage uses the canonical zone-group ID when
known plus every member zone key; instances use the exact
`Data/Worlds/instance_*` key. Do not use a free-form zone name as the only
identifier.

| Zone key | Scope | Quests | NPCs | Doodads | Services | Triggers/transfers | Nav/terrain | Human | Restart | Evidence |
|---|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|---|
| Solzreed / zone keys `9,124,125` (group ID to verify) | M1-M4 golden route | 1 | U | U | U | U | 1 | U | U | `Golden-Route-Solzreed.md`; `runnability.md`; `spawn-z-fix.md` |

Create one row/report per audited zone or instance. Do not expand every map
area up front: prioritize the golden route, adjacent travel/trade zones, then
zones required by a milestone or an observed defect.

## Domain scorecard

| Domain | Tables | Data-wired | % | Managers |
|--------|--------|-----------|------|----------|
| misc | 206 | 114 | 55% | — |
| doodads | 135 | 123 | 91% | DoodadIdManager, DoodadManager |
| quests | 85 | 70 | 82% | QuestIdManager, QuestManager |
| items | 53 | 31 | 58% | — |
| npcs | 18 | 12 | 67% | NpcManager |
| fx-visuals | 15 | 0 | 0% | — |
| slaves | 15 | 9 | 60% | SlaveManager |
| instances | 14 | 14 | 100% | — |
| buffs | 10 | 7 | 70% | — |
| skills | 10 | 5 | 50% | SkillManager, SkillTlIdManager |
| spheres | 10 | 9 | 90% | SphereQuestManager |
| equipment | 9 | 6 | 67% | — |
| housing | 8 | 3 | 38% | HousingIdManager, HousingManager, HousingTldManager |
| plots | 6 | 6 | 100% | PlotManager |
| characters | 5 | 3 | 60% | CharacterIdManager, CharacterManager |
| crafting | 5 | 4 | 80% | — |
| siege | 5 | 0 | 0% | — |
| loot | 4 | 4 | 100% | — |
| mates | 4 | 0 | 0% | MateIdManager, MateManager |
| merchants | 4 | 2 | 50% | — |
| premium | 4 | 0 | 0% | — |
| ranks | 4 | 0 | 0% | — |
| specialty-trade | 4 | 3 | 75% | — |
| towerdefense | 4 | 4 | 100% | — |
| transfers | 4 | 4 | 100% | TransferManager |
| zones | 4 | 3 | 75% | ZoneManager |
| auction | 3 | 0 | 0% | AuctionIdManager, AuctionManager |
| bubbles | 3 | 1 | 33% | — |
| models | 3 | 0 | 0% | ModelManager |
| moulds | 3 | 0 | 0% | — |
| shipyards | 3 | 2 | 67% | ShipyardIdManager, ShipyardManager |
| world | 3 | 0 | 0% | WorldIdManager, WorldManager |
| achievements | 2 | 2 | 100% | — |
| battlefields | 2 | 1 | 50% | — |
| combat | 2 | 1 | 50% | — |
| race-tracks | 2 | 0 | 0% | — |
| sounds | 2 | 0 | 0% | — |
| appellations | 1 | 1 | 100% | — |
| express-text | 1 | 1 | 100% | — |
| fishing | 1 | 1 | 100% | — |
| gimmicks | 1 | 1 | 100% | GimmickIdManager, GimmickManager |
| music | 1 | 0 | 0% | MusicIdManager, MusicManager |
| taxation | 1 | 1 | 100% | TaxationsManager |

> Quest-runnability (M2a/M2c + WI-2 + WI-3, band 1-30 census 2026-08-09, post-drop on merged develop): **2079/2079 driven quests runnable, 0 FAIL** across the
> 2092-quest scenario-harness census (117 M2a-dropped quests excluded — 26 engine-stuck + 91 shells). **Band 1-10: 560 PASS / 0 SKIP / 0 FAIL = 100.0% PASS-or-doc-SKIP** of
> 560 non-dropped (668 − 108 dropped). **Band 11-20: 609 PASS / 0 SKIP / 0 FAIL = 100.0%** of 609 non-dropped (626 − 17 dropped).
> **Band 21-30: 847 PASS / 0 SKIP / 0 FAIL = 100.0%** of 847 non-dropped (0 dropped).
> All SKIPs documented-SKIP with reason (13): 8 orphaned contexts (no quest_contexts row),
> 4 unsupported-act-type (MateLevel 2 / CompleteQuest 2 — the WI-2
> CrimePoint closure closed the last 2 census SKIPs (2916/2926) and added the t9 tier so the five level-41-50
> carriers (2935/2936/5197/5198/5494) are sampled and PASS — 7/7 CrimePoint contexts driven; the WI-3 AbilityLevel
> closure closed the last unsupported-objective family and added the t10 tier so the nine level-50 single-ability
> carriers (6070/6075-6082) are sampled and PASS — 10/11 AbilityLevel contexts driven, 5967 (all-abilities branch)
> flipped SKIP→PASS, 6069 remains SKIP for let-it-done-without-report-act, an engine completion-path class), plus 1 let-it-done-without-report-act (6069). Band-21-30 sweep calibration: kind_id-1 None components (legacy task-board
> step, engine walks Start→None→Supply) now emitted as "None" — 5 quests flipped FAIL→PASS (275/281/305/371/604).
> Wave-1+2 closures flipped 73 SKIP→PASS cumulative (36 wave-1 + 37 merged-line incl. 1702 multi-gap); wave-3
> ZoneKill closure flipped 23 more (73 + 23 = 96 cumulative); WI-2 CrimePoint closure flipped 2 more census SKIP→PASS
> (2916/2926) + 5 unsampled carriers → 7/7 driven (98 cumulative); WI-3 AbilityLevel closure flipped 5967 + 9
> unsampled carriers → 10/11 driven (108 cumulative); zero PASS→SKIP regressions.
> Census regen deterministic (byte-identical); band denominators + zone coverage (Gweonid/Lilyut/Mahadevi/
> Tiger Spine/Falcony/Sunny Wilderness/Ancient Forest/Marionople/Two Crowns/White Forest/Singing Land/Sunrise
> Peninsula) in runnability.md (census-meta.json-driven). Fail-before states on the
> wave-1/wave-2 rig commits (2283c0df/7a1145be). Watch items: EtcItemObtain engine no-op, cinema zero-wired,
> honor zero-wired (zero-wired-domains.md), ZoneKill ZoneId unenforced (§2.4, zero-wired-domains.md §9).
> See scorecard-explorations/runnability.md + zero-wired-domains.md §8/§9.

## Zero-data-wired domains (data exists, server ignores it)

- **fx-visuals** (15 tables): fx_cam_fovs, fx_cgas, fx_cgfs, fx_chrs, fx_decals, fx_group_fx_items...
- **siege** (5 tables): siege_items, siege_plans, siege_settings, siege_ticket_offense_prices, siege_zones
- **mates** (4 tables): mate_equip_pack_groups, mate_equip_pack_items, mate_equip_packs, mate_equip_slot_packs
- **premium** (4 tables): premium_benefits, premium_configs, premium_grades, premium_points
- **ranks** (4 tables): rank_reward_links, rank_rewards, rank_scope_links, rank_scopes
- **auction** (3 tables): auction_a_categories, auction_b_categories, auction_c_categories
- **models** (3 tables): model_attach_point_strings, model_bindings, model_quest_cameras
- **moulds** (3 tables): mould_pack_items, mould_packs, moulds
- **world** (3 tables): world_groups, world_spec_configs, world_var_defaults

## Upstream issue tracker — known gaps

- 67 open issues: 50 bug · 30 quest · 9 missing-data · 4 enhancement · 3 skill

### Quests reported broken (playtest targets, by ID)

1208, 1255, 1256, 1257, 1258, 1259, 1260, 1261, 1262, 1263, 1264, 1265, 1266, 1267, 1268, 1269, 1270, 1271, 1272, 1274, 1275, 1276, 1277, 1278, 1279, 1280, 1281, 1282, 1329, 1450

## Fork fixes (our lane, no upstream PR)

- **BUG-006 — kill-acceptor quests can never start (FIXED on `fix/quest-kill-acceptor`, 2026-08-03).**
  `QuestActConAcceptNpcKill.RunAct` was a copy-paste of the Npc accept check, and no code
  path ever set a Kill acceptor. Added `QuestAcceptorType.Kill`, wired
  `DoOnMonsterHuntEvents` (the Npc.cs death path funnel, Npc.cs:877/986/1019) to start
  matching quests with the Kill acceptor, and fixed `RunAct` to match. Live data check:
  **380 quests** had ALL Start acts as kill-accepts (e.g. 182, 205, 556, 913, 1057, 1208)
  — all now startable on kill. Quest 1119 (upstream #1208) is actually a plain
  Npc-accept quest (Npc 2237), not part of this family.
- **BUG-008 — QuestActCheckGuard silently auto-completes (FIXED on `fix/quest-check-guard`, 2026-08-04).**
  `RunAct` returned true unconditionally, so 6 escort/protect quests' guard objectives
  always passed (silent false positive). Now resolves the guard NPC in the owner's world
  (`ParentWorld.GetNpcByTemplateId`) and returns true only when present and alive; dead,
  despawned, or unresolvable → false. 3 new `QuestActCheckGuardTests` (dead/missing
  cases failed before the fix). Full gate 1085/1085. Catalog: bugs/008-check-guard.md.

- **BUG-009 — item-group gather/use objectives stall (FIXED on `fix/quest-item-group-objectives`, 2026-08-04).**
  `QuestActObjItemGroupGather`/`QuestActObjItemGroupUse` RunAct fell through to the base
  stub ("not implemented", returns false) — 9 act rows stall: 4 live gather quests
  (5490 신기루 섬을 깨끗하게, 6578 이이제이, 6600 보다 더 강력한 힘, 6615 신의 방패
  정식 대원이 되다!) + test quest 5489 (use), 4 orphaned contexts (1955/1957/2140/1958).
  Both acts now implement real objective counting following their single-item siblings,
  group-expanded via `QuestManager.GetGroupItems`/`CheckGroupItem` (any group item
  counts). Gather also mirrors cleanup/destroy-on-drop item removal. 14 new unit tests.

- **BUG-010 — Helpers.UnixTime(long) clamps every timestamp > 59s to DateTime.MaxValue (FIXED/CLOSED on `fix/bug-010-unix-time`, 2026-08-04 — Rei attestation comment 2532, PASS).**
  The range check compared `time > DateTime.MaxValue.Second` — and `DateTime.MaxValue.Second == 59` —
  so any unix-seconds value > 59 decoded to `DateTime.MaxValue`. Every CheckTimer quest restored
  via `Quest.ReadData` got `Time = DateTime.MaxValue` (byte-diff proof: time field 1785894127s →
  253402300800s for census quests 350/4292/1313), `LeftTime` int-overflowed, timer never expired.
  Now clamps against the exact max representable unix-seconds value (integer ticks math,
  253402300799; `(long)TotalSeconds` double-rounds to 253402300800 which AddSeconds can't hold).
  8 new `HelpersTests` incl. 2026-timestamp round-trip; census regen flipped 350/4292/1313
  PERSIST:Fail → PERSIST:Pass with zero other verdict changes. Gate 1129/1129. Catalog: bugs/010-unix-time-maxvalue.md.
  **Census headline after closeout: T1 88/97 PASS (9 FAIL / 0 SKIP), T2 22/35 PASS (7 FAIL / 6 SKIP).**
  Remaining FAILs were harness-gap (1897 SUPPLY/PROGRESS/REWARD etc., tracked under t_71ac7013) and
  have since been calibrated away — runnability line is GREEN (M1-5 entry below).

- **BUG-007 — quest data defects fail silently (FIXED on `feat/quest-sanity-verifier`, 2026-08-04).**
  New `QuestSanityVerifier` (startup cross-check at end of `QuestManager.Load`): collects
  unknown/uninstantiated/detached act types, broken component/quest/item-group refs,
  M1-2 known stubs, orphaned rows and the alias-dormancy verdict — logged loudly
  (Error/Warn/Info), never throws (matches loader behavior). 14 unit tests cover every
  finding class. Full defect catalog: bugs/007-quest-sanity-verifier.md.

- **BUG-011 — QuestActCheckSphere can never pass + sphere entry crashes (FIXED on `fix/quest-check-sphere`, 2026-08-04).**
  CheckSphere is a check act (no objective counter — loader assigns
  `ThisComponentObjectiveIndex = 0xFF`), but `RunAct` read the counter (always 0 →
  never passes) and `OnEnterSphere`/`OnExitSphere` wrote it (Objectives[0xFF] →
  IndexOutOfRangeException). Now `RunAct` evaluates the owner's LIVE position against
  the component's quest spheres (mirrors QuestActCheckGuard), and sphere events only
  request re-evaluation. Live data: exactly 1 quest_context (1033, Progress component
  5065 → sphere 945); the other 10 act rows are orphans. 8 new `QuestActCheckSphereTests`
  (fail-before: 1 assertion + 3 IndexOutOfRangeException). Catalog: bugs/011-check-sphere-0xff.md.
- **BUG-012 — CharacterAbilities KeyNotFoundException 'General' on quest exp rewards (FIXED on `fix/char-abilities-general`, 2026-08-04).**
  `CharacterAbilities` ctor seeds `Fight(1)`..`Love(10)` only, but `AbilityType.General == 0`
  (Ability.cs:5) is never seeded and ability1/2/3 come from the client create packet / DB
  with no server-side validation — `AddActiveExp` indexed `Abilities[Ability1]` and threw
  `KeyNotFoundException 'General'` (census REWARD:Fail 250/6578/6600/6615 via
  QuestActSupplyExp → Character.AddExp). Both exp paths (`AddActiveExp`, `AddExp`) now
  guard with `Abilities.TryGetValue` — unseeded abilities are skipped, character exp
  intact (granted before the call), no bogus General row persisted. 6 new
  `CharacterAbilitiesTests` (3 General-slot no-throw + None + seeded-ability controls).
  Full gate 1127/1127. Catalog: bugs/012-abilities-general-key.md.

- **M1-5 — quest scenario harness census COMPLETE (feat/quest-scenario-harness, 2026-08-04).**
  Harness (driver + manifest loader + tier runner) drives every manifest quest through
  START→PROGRESS→READY→REWARD→PERSIST with per-stage verdicts. Census over 3 tiers: T1 Solzreed
  97/97 PASS, T2 families 29/29 PASS (+6 SKIP orphaned contexts), T3 stratified act-family census
  27/27 PASS (+27 SKIP: unsupported act families + orphaned data). **Headline: 153/153 quests
  runnable** across 186 sampled quests (0 FAIL — every driven quest completes its lifecycle).
  Remaining SKIPs: 8 orphaned quest_contexts (data) + 25 harness gaps (14 unsupported act
  families — ObjZoneKill, ObjAggro, ObjCompleteQuest, EtcItemObtain, …; queue in
  scorecard-explorations/runnability.md). Gate 1148/1148.
  **Post-fix census regen (2026-08-05, fix/next-missing-776-777 @ aa35a503 merged, Rei gate PASS
  t_d8a8c798):** verdicts byte-stable — 153/153 runnable, 0 FAIL, 33 SKIP (census FAIL/SKIP deltas:
  none; the defect was a load-time verifier finding, never a scenario stage). 330/776/777
  COMPONENT_NEXT_MISSING → 0 via QuestDataOverlay (1520→1521, 3480→3482, 3488→11591): real-load
  census 0 ERR / 0 COMPONENT_NEXT_MISSING over 4775 quests. Gate 1216/0/0. Evidence:
  scorecard-explorations/next-missing-776-777.md.
  **Post-fix census regen (2026-08-05, fix/act-ref-2145 @ 82834da7 merged, Rei gate PASS
  t_53baa876):** verdicts byte-stable again — 153/153 runnable, 0 FAIL, 33 SKIP (census
  FAIL/SKIP deltas: none; the 2 pruned dangling ConAcceptComponent acts — 2145→2146 + sibling
  1960→1961 — sit on quests outside the 186-quest census sample, dead cat-34 crafting chain).
  ACT_REF_MISSING_QUEST real-data census: fail-before 2 rows → pass-after 0 (drift accept_comps
  384→382, quest_acts 26886→26884, −2/−2 exactly). Gate 1216/0/0 (incl. rig class 2/2).
  Evidence: scorecard-explorations/act-ref-2145-rig.md.
  **Post-fix census regen (2026-08-05, fix/no-start-1533 @ 74fb7762 merged, Rei gate PASS
  t_f884383f):** verdicts byte-stable — 153/153 runnable, 0 FAIL, 33 SKIP (census FAIL/SKIP
  deltas: none; the DROPPED QUEST_NO_START cluster 1533–1548 — 23 legacy tutorial shells, zero
  Start components + zero accept surfaces, provably never acceptable — sat outside the 186-quest
  census sample). QUEST_NO_START real-data census: fail-before 23 quests/exit 1 → pass-after 0
  (drift quest_contexts 4876→4853, quest_components 17851→17826, quest_acts 26886→26844 —
  −23/−25/−42 exactly; 9/9 unit_reqs collision rows untouched). Gate 1223/0/0 (fast +
  CI-parity coverage, Login.IntegrationTests 6/6; 1 pre-existing %db_port% env artifact).
  Evidence: scorecard-explorations/no-start-cluster-1533-1548-evidence.md +
  fix-no-start-1533-passafter.md.
  **Post-fix census regen (2026-08-06, fix/no-components-1391 @ b1e2231c merged, Rei gate PASS
  t_a56e8e2d):** verdicts byte-stable — 153/153 runnable, 0 FAIL, 33 SKIP (census FAIL/SKIP
  deltas: none; quest 1391 — an empty-template dummy shell with 0 components/0 acts, provably
  never acceptable — sat outside the 186-quest census sample). QUEST_NO_COMPONENTS real-data
  census: 1391 dropped via SQL/patches/compact/2026-08-05-drop-1391.sql (drift quest_contexts
  4876→4875, −1 exactly; nothing to cascade in components/acts; the only unit_reqs touch, id
  33609, is a Skill-owned sphere ref — left in place). Allowlist 132→131 ids (1391 removed so a
  regression re-reports at WARN, was allowlist-masked INFO). Rig flipped pass-after 2/2 (template
  absent + regression guard); verifier 27/27. Gate 1210/0/0 (fast + CI-parity coverage,
  Login.IntegrationTests 6/6; 1 pre-existing %db_port% env artifact). Evidence:
  scorecard-explorations/no-components-1391-rig.md (§7 pass-after).

### System-level bugs (non-quest)

- #696 [skill] [BUG] Tree thinning of old trees doesn't work as intended
- #920 [bug] [BUG] Ezna misses a lot of things
- #949 [bug] [BUG]  Pack that disappeared
- #972 [bug] [BUG] Red farm hauler
- #973 [bug] [BUG] Letter box
- #974 [bug] [BUG] Letter box  Shipping problem
- #978 [skill] [BUG] [Skill] (24353) Fortuna Die does not function correctly
- #985 [question] [BUG] Save character database
- #1011 [bug] [BUG] Harani main quest 3509 invisible quest npc
- #1033 [missing functionality] [BUG] Issue with NavMesh on the main_world, NPCs and monsters not being on the correct "height"
- #1046 [question] [BUG] with item
- #1047 [bug] [BUG] Quest NPC Nubo spawned inside mountain
- #1091 [bug] [BUG] <with farm hauler>
- #1152 [feedback to address] [BUG]  Treasure map
- #1168 [missing functionality] [BUG]  the packs in the merchant ship disappear
- #1170 [bug] [BUG]  Sometimes a player character looses its model and items (It happens randomly)
- #1175 [bug] [BUG] Return to email sender
- #1323 [bug] [BUG] PvP Arena scoring not working
- #1425 [bug] [BUG] NPCs and monsters floating above ground on develop
- #1491 [bug] [BUG] ActiveRegionTick is subscribed synchronously and starves the 100 ms AI tick

## Canonical resources — how 1.2 mechanics actually worked

| Resource | Use | URL |
|----------|-----|-----|
| ArcheAge Fandom Wiki | feature/mechanic reference (trade packs, labor, housing) | https://archeage.fandom.com/wiki/ArcheAge_Wiki |
| Ten Ton Hammer 1.2-era guides | trade pack economy, shipbuilding (2014 era = 1.2) | https://www.tentonhammer.com/guides/archeage-trade-pack-guide |
| AAEmu wiki (in-repo) | project's own component/architecture docs | Docs/wiki/ |
| AAEmu GitHub issues | known-broken list (50 bug / 30 quest / 9 data) | https://github.com/AAEmu/AAEmu/issues |
| compact.sqlite3 | the canonical 1.2 data surface (679 tables) | server: .server_files/AAEmu.Game/Data/ |
| game_pak (r208022) | client-side assets the server references | server: .server_files/AAEmu.Game/ClientData/ |
| ArcheAge Classic / ArcheRage | private-server behavior reference (what "working" looks like) | https://aa-classic.com |
