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

**H dimension rule (reconciled 2026-08-12, bot-backtrack program):** `H`
means an ACTUAL PLAYER completing the curated scenario — never a bot or
scripted actor. Scripted-actor/bot evidence is recorded under `A`
(functional) with an explicit "proxy/bot-functional" label in the evidence
cell; it is NEVER recorded as `H=2`. `H` stays `U` (UNKNOWN) until Josh runs
the curated scenario. (ROADMAP M5-stand-in rule: bots prove function, never
feel.)

**Proxy vs authentic replay (2026-08-13, canonical sync t_c9f0d7f6):** the
M3a/M4 `A`-dimension grades above rest on scripted-actor PROXY evidence
(M3a: in-memory rig, reflection, GM inventory, direct service calls; M4
integrated rig: direct zone/transform assignment, manual cargo attach). Those
rigs are **superseded for authentic acceptance — NOT erased** (historical
evidence preserved in ROADMAP's M4 EXIT RECORD + deferred-gate table) — by
the bot-backtrack Phase-2 replay (root t_b4f455b0, scope t_2625be99) through
M5.1 contract actions on a real server: no direct Transform/ZoneId/GM/
reflection/DB shortcuts; labor (−60/pack) + mail payout (124540/pack,
SpecialtyManager) conservation required; process-level restart suites re-run
as-is. **Grades are not inflated:** proxy evidence stays labeled, and `H`
stays `U` (UNKNOWN) until Josh tests feel (human packet t_2b654349). Scope
lock 2026-08-14 (marker t_2625be99): replay route = **Housing.Build (M5.2,
merged 3396d9ef1) → farm/storage → craft → pack → load/drive vehicle →
unload → sell → reward**, all prerequisites merged (LoadPackOntoVehicle
6c2429ae0, DriveVehicle 6edbf0cbb, full M5.1 surface on develop).

### Global mechanic ledger

This is the prioritized inventory, not a claim that manager presence means the
feature works. The initial `W1` entries only record code surfaces found by
Graphify and must be promoted by an end-to-end exploration.

| ID | Mechanic / scoped scenario | First gate | C | W | H | A | R | S | Evidence / next audit |
|---|---|---:|:---:|:---:|:---:|:---:|:---:|:---:|---|
| PROG-01 | Character creation, login, logout, re-entry | M2 | U | U | U | U | U | N/A | M2 baseline legs executed: automated t_c6eb12ec (Rei gate t_1998cfd8 PASS) + restart t_cca63225 (Rei gate t_c069bacd PASS) + live probe t_92a41fe6 2/2 — grades stay U: no human evidence yet (human baseline deferred to M4, t_46bf9b84); save path: SaveManager dirty-tracking merged 5ed5d6493 (fork fix, SaveManagerTests 10/10) |
| QUEST-01 | Solzreed curated starter quest chain + rewards | M1 | 2 | 1 | U | 1 | 2 | N/A | `Golden-Route-Solzreed.md`; quest scenario harness; `next-missing-776-777.md` (330/776/777 COMPONENT_NEXT_MISSING → 0 via QuestDataOverlay, Rei gate PASS t_d8a8c798); `act-ref-2145-rig.md` (2145→2146 + sibling 1960→1961 ACT_REF_MISSING_QUEST → 0 via `2026-08-05-prune-act-ref-missing-2145.sql`, Rei gate PASS t_53baa876); `no-start-cluster-1533-1548-evidence.md` (QUEST_NO_START cluster 1533–1548 → 0 via `2026-08-05-drop-no-start-cluster.sql` — 23 contexts/25 components/42 acts dropped, Rei gate PASS t_f884383f); `no-components-1391-rig.md` (QUEST_NO_COMPONENTS 1391 empty template → dropped via `2026-08-05-drop-1391.sql` — 1 context dropped, drift −1, Rei gate PASS t_a56e8e2d); real restart verified 2026-08-10 (t_cca63225/t_92a41fe6): M2 restart baseline control run A `E2e_RestartPersistence_TwoCheckpoints_FullStateMatch` PASS 4m01s — real process-level SIGKILL + cold boot, quests 254/266 active after restart, Step/Status/Objectives byte-equal, resume-through-turn-in completes; live probe 2/2 PASS (`restart-baseline-probe-20260810-195332.md`, q266 retained across real restart, still Ready after re-entry, completed_quests 3==3==3==3); M3b exit E2E restart cycles (f5b00c686); 2026-08-23 (6dbd41b64): QuestActEtcItemObtain credit path implemented — the long-standing census watch-item family closes, ~51 live quests fixed (QUEST-01 grades unchanged — the Solzreed curated scope was already graded) |
| CTRL-01 | Movement, targeting, interaction, control-state recovery | M2/M5 | U | U | U | U | U | U | Actor-contract spike |
| COMBAT-01 | PvE combat, death, resurrection, loot | M2/M5 | U | 1 | U | U | U | U | `SkillManager`; combat audit |
| ABILITY-01 | Ability selection, skill use, progression | M2 | U | 1 | U | U | U | N/A | `SkillManager`; ability audit |
| ITEM-01 | Inventory, equipment, stacking, split/move, full-inventory errors | M2 | U | 1 | U | U | U | U | `ItemManager`; inventory conservation audit. **2026-08-24 (0482ba3f0):** item_proc_bindings loaded + GetItemProcBindings; UnitProcs factory seam — items can carry procs. Evidence note only — grades stay conservative pending the inventory conservation audit |
| LABOR-01 | Labor consume/regenerate/cap/persist | M3/M4 | U | U | U | U | U | U | Labor/ActAbility audit |
| MATE-01 | Obtain, summon, mount, dismount, persist a mount | M2 | U | 2 | U | 1 | U | N/A | `MateManager`; golden-route mount scenario. **2026-08-24 (0482ba3f0):** mate_equip_packs/pack_groups/pack_items/slot_packs loaded in MateGameData (the zero-data-wired mates domain is now wired); fail-closed IsMateEquipAllowed legality at MateEquipmentContainer.CanAccept; latent EquipmentContainer null-Owner level-gate bypass fixed for mates. W=2 (real load + enforcement paths end-to-end); A=1 rig-level legality only — no live equip E2E yet (kept honest); H=UNKNOWN |
| HOUSING-01 | Claim land, construct, own, permit, demolish | M3 | 2 | 2 | U | 2 | U | N/A | M3a: `HousingPlacementValidator` (zone/category/overlap/ownership, 1.2 housing_groups/areas/group_categories) + `HousingManager.Build` wiring + `CraftEffect` construction + `DecorationLimitEvaluator` (canonical deco limits: housing_deco_limits 12 groups / 23 elems, deco_limit 40, absolute 60/208 — M3a resolved the partial-domains "dead weight" finding); harnesses `HomesteadPlacementScenarioTests` 29/29 + `HousingM3aConstructionTests` 18/18 + M3a exit scenario `M3aExitScenarioTests` (2 scripted actors = M5-stand-in, adjacent homesteads, one session) — Rei gate t_72c787c8; M3 canonical circle-back audit `mechanics/m3-canonical-audit.md` (2026-08-11): dossier verified fully covered; placement is zone-level not polygon-level, terrain 115/116 + unit 114 + max_construct_count unenforced → FIX-2 (Tai). **H=U (reconciled 2026-08-12): exit driven by scripted actors — proxy/bot-functional, H UNKNOWN until Josh runs it; M3a contract replay = deferred gate**. **2026-08-24 (0482ba3f0):** FIX-2 closed — terrain/overlap/cap/race checks verified as already landed; two-thread build-race regression added via real HousingManager.Build. Grades unchanged |
| FARM-01 | Place, grow, harvest, and recover curated crops/livestock | M3 | 2 | 2 | U | 2 | U | U | M3a: canonical potato loop (seed 15659 → doodad 2259 → loot pack 6452) on real Doodad paths (`DoodadFuncCropHarvest`/`DoodadFuncFruitPick` → loot phase); `CropHarvestLoopTests` 6/6 + M3a exit scenario (plant→grow→harvest in the same one-session flow) — Rei gate t_72c787c8; M3 canonical circle-back audit `mechanics/m3-canonical-audit.md` (2026-08-11): growth timers data-verified (60 s + 9 min, codex-confirmed "matures in ~10m"; climate 0.73 = upstream PR #744 research-derived), rot timer 48.33 h + watering path pinned (FIX-4 t_254eafc7); **livestock: growth chain data-verified (calf 2672: 3.43 h + 30.87 h) and restart-recovery pinned (M3b-2 8/8), interactions implemented (FIX-1 t_afbf7cb7, REI GATE t_3afe9f5b): `DoodadFuncFeed` consumes the feed item (error `not_enough_item`), `DoodadFuncDairyCollect`/`DoodadFuncShear`/`DoodadFuncButcher` advance their canonical chains (shear publishes the 60 s regrow term; loot from the milk/butcher phases: pack 6392 → milk 8055, pack 6390 → beef 8048); `LivestockInteractionTests` 9/9 on the real calf 2672 / sheep 518 chains**. **H=U (reconciled 2026-08-12): M3a exit driven by scripted actors — proxy/bot-functional, H UNKNOWN until Josh runs it; M3a contract replay = deferred gate** |
| PROPERTY-01 | Furniture/storage/phase/attachment persistence | M3b | 2 | 2 | U | U | 2 | U | M3 canonical circle-back audit `mechanics/m3-canonical-audit.md` (2026-08-11): canonical 1.2 persistence contract identified — MySQL `housings` (owner/co-owner, template, transform, build step, permission, dates, sell state) + `doodads` (phase/plant/growth/phase times, house-relative local transform, attach_point, owner_type, item/container links); 1.2 drops nothing on death, persists everything on restart, returns furniture+design by mail on demolish (wiki S4/S9/S10 + code `ReturnHouseItemsToOwner`); fork deliberately disables unpaid-tax demolish (documented deviation, NOTE to Josh); M3b exit gate (t_accb1c63): merged M3b-1..4; full lifecycle E2E `M3bExitPersistenceE2eTests` — place→decorate→plant→harvest, N=3 crash cycles (restart, kill -9 mid-save via INNODB_TRX-observed autosave, MySQL container kill during harvest save) + final re-entry, two homesteads (900002/900003), 16 rows asserted intact per boot with transforms/phases/owners/attachment, no loss/dup — PASS 7m08s; W=2: exit E2E exercises the real save/load runtime paths end-to-end (16 rows per boot × 3 crash cycles; kill -9 mid-save + container kill); M3b-1 E2E `M3bFurniturePersistenceE2eTests` 7/7 rows ×2 SIGKILL restarts; recovery/load seams: `ShouldLoadHouseRow`/`ShouldLoadPersistentDoodad` (M3b-3 11/11), `ApplyLoadedState` read-only phase restore (M3b-2 8/8), SpawnPersistentDoodads save-after-restore (M3b-1 4/4) — full unit gate 1691/0/1 on merged tree; autosave p95 1301ms < 2000ms at 25 bots + 2 homesteads |
| CRAFT-01 | Recipe prerequisites, labor/material consume, output | M4 | 2 | 2 | U | 2 | 2 | N/A | M4-A (fix/m4a-crafting-integrity, t_d957e80d): workstation req_doodad_id/range/ownership enforcement, bag-scope materials, stack-aware fit, rate rolls, labor-cost parity, queue guards — merged into M4 EXIT (t_97e59ffc, release/m4-exit); `M4ExitIntegratedSessionTests` drives craft 5404 (3× golden potato 19887 → pack 26489) through the REAL CharacterCraft.Craft → CraftEffect.Apply → EndCraft chain in one 4-scripted-actor session (M5-stand-in): level-9 negative (LevelLowToUse), materials consumed before grant, pack lands in Backpack slot (full gate 1778/0/1 on the merged tree). **H=U (reconciled 2026-08-12): exit driven by scripted actors — proxy/bot-functional, H UNKNOWN until Josh runs it; M4 economic replay = deferred gate** |
| PACK-01 | Craft, carry, place, load, unload, sell trade pack | M4 | 2 | 2 | U | 2 | 2 | U | M4-2 (fix/m4-2-trade-packs, t_449d0c41): level-10 craft/sell gates, origin-zone StoreCantSellSameZone, 6-day placed-pack expiry, 22 h mail delay; `SpecialtyManagerTests` 21/21 (sale math, 80/20, coin routes, gates); `M4_2TradePackRestartE2eTests` — plant_time + made_unit_id survive kill -9 (PASS on merged tree 2m12s); `M4ExitIntegratedSessionTests` — full craft→load→travel→unload→sell→reward loop, reward math 91238 base / 124540 payout verified, 2× repeat (4 scripted actors = M5-stand-in). **H=U (reconciled 2026-08-12): exit driven by scripted actors — proxy/bot-functional, H UNKNOWN until Josh runs it; M4 economic/navigation replay = deferred gate** |
| SLAVE-01 | Cart/ship summon, seats/cargo, cleanup, recovery | M4 | 2 | 2 | U | 2 | 2 | U | M4-3 (fix/m4-3-vehicle-lifecycle, t_4a91a4f5, Rei gate t_5019f7b1 PASS): despawn gates owner/312/288/801, BindSlave 324, RidersEscape 640; `SlaveLifecycleTests` 29/29; `M4VehiclesE2eTests` — two kill -9 restarts, row intact, exactly 1 row (PASS on merged tree 3m09s); `M4ExitIntegratedSessionTests` — pack loaded on slave, 801 despawn refusal, unload → despawn allowed (4 scripted actors = M5-stand-in). **H=U (reconciled 2026-08-12): exit driven by scripted actors — proxy/bot-functional, H UNKNOWN until Josh runs it; M4 navigation replay = deferred gate** |
| TRADE-01 | Direct player-to-player item/currency trade | Later | U | 2 | U | 1 | U | N/A | `TradeManager`; separate from trade packs. **2026-08-23 (d4b5e524c): trade FUNCTIONAL** — OkTrade cancel-then-finish KeyNotFoundException fixed; both-locked AND both-ok gate (was !a && !b single-side exploit); TradeOffer/TradePutup/TradeLockOk contract actions; `TradeHandshakeScenarioRigTests` 5/5. W=2 (real engine path end-to-end); **A=1 rig-level only — no live/restart evidence yet (kept honest)**; H=UNKNOWN |
| MERCHANT-01 | NPC vendor buy/sell, price, stock, refund/error paths | M2/M4 | U | U | U | U | U | N/A | Merchant audit |
| AUCTION-01 | List, search, bid/buy, settle, cancel, expire | M8 | U | 2 | U | 2 | 2 | U | `AuctionManager`; market audit. **2026-08-23 (f3bb787ce) — strongest promotion of the sweep: W/A/R = 2** — expiry sweep hardened (per-lot isolation, null-safe missing-item expiry, mail-fail no longer wedges lots, `_auctionTaskScheduled` guard); `AuctionHouseRestartE2eTests` live E2E PASS 3m26s — post → buy → settle → expiry-mail across kill -9 (E2eStack.RestartGameServer(afterStop) seam). C stays U pending the canonical market audit; H=UNKNOWN |
| ECON-01 | Currency/item/labor conservation across economy | M4/M8 | U | U | U | U | U | U | Cross-mechanic invariant audit |
| MAIL-01 | Send, receive, attach, return, expire, persist | Later | U | 1 | U | 1 | U | U | `MailManager`; mail audit. **2026-08-23 (6b2f15a6d):** ReturnMail implemented + expiry bounce/destruction semantics rig-tested → A=1 (rig level). ⚠️ CSReturnMailPacket opcode confirmed a 0xfff placeholder — NOT registered — so the client-facing return path is still unwired; W stays 1. H=UNKNOWN |
| TRANSFER-01 | Fixed-route transport board/ride/disembark/recover | M4 | U | 2 | U | 1 | U | U | `TransferManager`; route audit. **2026-08-24 (3a534b539): transfer FUNCTIONAL + LIVE PROVEN** — CSBoardingTransferPacket TlId shadowing FIXED (multi-part transfers share the master's TlId but seats exist only on child parts; FirstOrDefault always resolved the seatless master, so boarding could never bond); read-only `transfers` bridge dump command; TransferRideE2eTests LIVE PASS (board Marianople Gondola tlId=1 ap=2 BondChairDouble → ride route samples → disembark at current position). W=2 (real engine path end-to-end); A=1 live board/ride/disembark E2E — recover/restart legs still open; H=UNKNOWN |
| INDUN-01 | Instance entry, limits, party, completion, exit/recovery | M7+ | U | 1 | U | U | U | U | `IndunManager`; selected dungeon audit |
| FISH-01 | Fishing interaction, loot, labor, contest integration | M9.5 | U | U | U | U | U | U | Fishing audit |
| PVP-01 | Flagging, factions, damage, honor, death/recovery | Later | U | U | U | U | U | U | PvP audit |
| DUEL-01 | Invite, accept, bounds, result, cleanup | Later | U | 1 | U | U | U | N/A | `DuelManager`; duel audit |
| CRIME-01 | Crime evidence/points, reporting, persistence | M9 | U | 1 | U | U | U | U | `CrimeManager`; justice audit |
| TRIAL-01 | Arrest, jury selection, testimony, verdict, sentence | M9 | U | 1 | U | U | U | U | `TrialManager`; justice audit |
| PRISON-01 | Imprisonment, sentence time, labor/escape/release | M9 | U | U | U | U | U | U | No `PrisonManager` found; trace model/packet paths before scoping |
| PARTY-01 | Invite/join/leave, leader, follow/assist, recovery | M7 | U | 2 | U | 1 | U | U | Party audit. **2026-08-21→23:** PartyInvite/PartyAccept contract actions through the real TeamManager engine paths (rig GameplayActorPartyTests 6/6); `PartyFollowAssistScenario` (rig 4/4); **party spike LIVE E2E PASS 2026-08-23 (c98da8a53)** — `PartySpikeScenario` m7-party-spike: 3-bot rally → assist → kill elite NPC 1870 inside the leash window over the N-actor bridge seam, with causal cast-effect traces (ActorAuditRecord v2: target_hp_before/after, effect_observed, effect_wait_ms). W=2 (invite/join/follow/assist/rally live-proven through real paths); **A=1 — partial scenario coverage** (roles, avoid-extra-pulls, resurrect, mount+travel, lifecycle fault matrix still open); H=UNKNOWN |
| EXPEDITION-01 | Expedition membership, roles, persistence | M9/M10 | U | 1 | U | U | U | U | `ExpeditionManager`; organization audit |
| CHAT-01 | Local/zone/party/expedition chat, moderation, bot identity | M7/M8 | U | 1 | U | U | N/A | U | `ChatManager`; social audit |
| ZONE-01 | Peace/conflict/war state transitions and PvP rules | Later | U | 2 | U | 1 | U | U | `ZoneManager`; conflict-state audit. **2026-08-24 (0482ba3f0): zone state machine data-wired + enforced** — hard-coded Conflict boot state removed → data-driven Peace default (legacy World.ConflictZonesStartAtConflict flag kept for tests); Peace-state PvP protection at the BaseUnit.CanAttack chokepoint (fail-open when no conflict entry; Hostile stays attackable). W=2 (real engine path end-to-end); A=1 rig-level state machine + enforcement tests — no live PvP scenario yet (kept honest); H=UNKNOWN |
| ACTOR-01 | Observe/action lifecycle, rejection, timeout, idempotency | M5 | U | 0 | U | 0 | 0 | U | New contract; architecture spike first |
| BOT-01 | Headless account/session/Character lifecycle | M6 | U | 0 | U | 0 | 0 | U | New fork capability |
| BOT-02 | Deterministic recovery + tick-budget compliance | M6 | U | 0 | U | 0 | 0 | 0 | Staged 30m/1h/6h soak. **2026-08-24 (4e460305b):** scheduler soak STAGE 1 executed — SchedulerSoakStage1Tests, 10 manifest citizens × 30min through real IPlayerBotScheduler wakes; two valid runs ~90k steps, 0 failed/timed-out, wake avg ~99ms, DB writes 14–19/min/citizen, tick+region budgets PASS; staged ladder continues (1h/6h rungs + physics-recalibration decision open). Adjacent G3-B3 arbitration landed same sweep (0482ba3f0): IBotActivityModule + BotGoalArbiter — priority-based single-active activity per bot per wake. Notes only — grades unchanged (kept honest) |

Add mechanics as SQL/code/runtime exploration reveals them; use stable IDs so
bugs, cards, tests, and zone reports can refer to the same scope.

> **2026-08-24 scorecard update (through develop @ 3a534b539):** ZONE-01
> promoted W=2/A=1 — data-driven Peace boot state + Peace PvP protection at the
> BaseUnit.CanAttack chokepoint; state machine + enforcement tested rig-level,
> no live PvP scenario yet · MATE-01 promoted W=2/A=1 — mate equip-pack data
> loaded + fail-closed IsMateEquipAllowed legality rig-tested, no live equip
> E2E · TRANSFER-01 promoted W=2/A=1 — CSBoardingTransferPacket TlId shadowing
> fixed; TransferRideE2eTests live board/ride/disembark PASS · ITEM-01 evidence
> note — item_proc_bindings loader + UnitProcs factory seam (grades
> conservative) · HOUSING-01 evidence note — two-thread build-race regression
> added (grades unchanged) · BOT-02/scheduler-soak note — STAGE 1 executed
> (~90k steps, 0 failures; staged ladder continues). H stays UNKNOWN everywhere
> (hard rule — never recorded as H=2).

> **2026-08-23 scorecard update (through develop @ f3bb787ce):** AUCTION-01
> promoted W/A/R = 2 — live E2E incl. kill -9 restart pin, strongest of the
> sweep · TRADE-01 W=2/A=1 via trade handshake rig + engine fixes (A stays
> honest: rig-level) · PARTY-01 W=2/A=1 via the party spike live E2E
> (c98da8a53) · MAIL-01 A=1 — return + expiry implemented and rig-tested
> (CSReturnMailPacket opcode still an unregistered 0xfff placeholder) ·
> QUEST-01 evidence note: EtcItemObtain credit path closed (~51 live quests).
> H stays UNKNOWN everywhere (hard rule — never recorded as H=2).

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

> Quest-runnability (M2a/M2c + WI-2 + WI-3 + WI-4 + WI-5 + WI-7 + WI-8 + WI-9 + WI-11b + WI-12, band 0-55 + lvl-99 straggler census 2026-08-10, FINAL G1 CENSUS on merged develop): **4573/4573 quests runnable, 0 FAIL, 14 documented SKIP** across the
> 4587-quest scenario-harness census (297 dropped quest_contexts rows excluded — 305 registered drops, 8 of which are orphaned contexts without rows; register is the source of truth). Denominator reconciliation (WI-12): 4,876 quest_contexts rows − 297 registered drops = **4,579 live contexts** = 4,573 PASS + 6 kept-by-ruling doc-SKIP (3419/4967 ltd, 315/1576/1728/2046 no-components), 0 unexplained; the G1 audit estimate 4,735 (4,876 − 141) shrinks by the WI-6 drop (6069, −1) and WI-11a drop (−155) to the final 4,579 — every live context is PASS or registered-drop or documented-SKIP, zero unexplained FAIL/SKIP. **Band 1-10: 560 PASS / 0 SKIP / 0 FAIL = 100.0% PASS-or-doc-SKIP** of
> 560 non-dropped (668 − 108 dropped). **Band 11-20: 609 PASS / 0 SKIP / 0 FAIL = 100.0%** of 609 non-dropped (626 − 17 dropped).
> **Band 21-30: 847 PASS / 0 SKIP / 0 FAIL = 100.0%** of 847 non-dropped (0 dropped).
> **Band 31-40: 643 PASS / 0 SKIP / 0 FAIL = 100.0%** of 643 non-dropped (0 dropped) — the WI-7 T13 sweep drove all 643 contexts (631 in t13, 12 sampled in earlier tiers), zero harness SKIPs as predicted.
> **Band 41-50: 1589 PASS / 2 SKIP / 0 FAIL = 100.0% PASS-or-doc-SKIP** of 1591 non-dropped (1592 − 1 dropped, 6069 WI-6) — the WI-8 T14 sweep drove all 1591 contexts (1528 in t14, 63 sampled in earlier tiers); the 2 SKIPs are 3419/4967 (let-it-done without report act — Josh NO-GO keep ruling, WI-6 register §8).
> **Band 51-55: 268 PASS / 0 SKIP / 0 FAIL = 100.0% PASS-or-doc-SKIP** of 268 non-dropped (0 dropped) — the WI-9 T15 sweep drove all 268 contexts (264 in t15, 4 sampled in earlier tiers: 6095 t3 / 6578 t2 / 6600 t2 / 6615 t2).
> **Lvl-99 straggler 3465: 1 PASS / 0 SKIP / 0 FAIL = 100.0%** (1 non-dropped) — top-level quest outside every banded tier; a no-acts shell (4 components, 0 acts) that the harness walks Start→Progress→Ready→Reward→Persist via the empty-comp auto-pass path, verdict PASS.
> **Band 0/null (WI-11b + WI-12, 2026-08-10, t_8ec705f0): 59/60 contexts driven in the new t16 tier (56-context sweep handoff D2/D3/D4/D5 + the 4 A2 keeps; 6250 rides T3 = 60/60 accounted) — 56 PASS / 0 FAIL / 4 SKIP = 100.0% PASS-or-doc-SKIP.** D2 old Sunny (13/13 PASS — act-less superseded line, vacuous shells); D3 tutorial sphere steps (12/12 PASS — 33 accept-sphere acts reference spheres 1982-2014, ALL present in the spheres table; 2617-2619 are single empty Start comps the engine completes via the kind-chain walk; 11 of the 33 sphere_quests rows (1098-1130) reference sphere ids missing from the spheres table — inert data, the engine consumes sphere ids from the accept acts, not sphere_quests); D4 real content (22/22 PASS — Cradle chain 1394-1485, Blue Salt 5307-5314, dailies 5459/6222/6223/8000004, event/title/library quests all drive full lifecycle; 6250 already PASS in T3; **8000004 flipped FAIL→PASS after BUG-014 fix merged @ 4b73b63ac**); D5 test/dummy (9/9 PASS); A2 unit-req keeps (4 SKIP "no components" — zero-component shells kept by Josh's Q2 NO-GO ruling, documented in the triage doc §3 A2, not dropped). The WI-11b first-sweep FAIL was quest 8000004's RESET fidelity stage — **BUG-014 (REAL engine defect)**: the completed-block id is a ushort key and `(ushort)(8000004/64)` wraps 125000 → 59464, so ResetDailyQuests recomputes questId 3,805,700 and can never clear the completed bit for quest ids ≥ 4,194,304 — 8000004 is permanently daily-locked after first completion (bugs/014-quest-completed-block-ushort-wrap.md; repro probe + evidence on the card). **FIXED 2026-08-10 (t_8b47a3bf, fix/bug-014-quest-completed-block-uint @ 4b73b63ac): completed-block key ushort→uint, Rei gate PASS (t_5c09fdf9, isolated clone, 5/5 rig incl. HighIdDailyQuest pin, full gate 1494/0/1), merged to origin/develop.** Two harness/generator calibrations were required to sweep band 0: (1) NULL LEVEL (quest 1576) now normalizes to 0 exactly like the engine (GetByte("level", 0), QuestManager.cs:565) — a null template.level crashed the C# byte deserializer; (2) the rig character's level is clamped to ≥1 (max(1, template.Level)) because the engine's exp curve explicitly rejects level-0 units (GetLevelFromExp ThrowIfZero, ExperienceManager.cs:76) — a level-0 character completing a SupplyExp reward (6229/6314/6355) threw; the template keeps the data-true level 0. All 56 sweep contexts accounted: 55 driven in t16 (all PASS post-fix) + 6250 in t3 (PASS). No drops decided or executed in this card.
> All SKIPs documented-SKIP with reason (14): 8 orphaned contexts (no quest_contexts row; 6069 was the
> let-it-done-without-report-act SKIP but is now DROPPED — WI-6 triage ruling 2026-08-09, register §8,
> drop execution t_6810ebd4, merged t_ec1a3326) + 2 kept-by-ruling ltd quests (3419/4967, WI-8 census) + 4
> kept-by-ruling no-components (315/1576/1728/2046, WI-11b A2 keeps — zero-component shells, do-not-delete
> labels, triage doc §3 A2) — the WI-2
> CrimePoint closure closed the last 2 census SKIPs (2916/2926) and added the t9 tier so the five level-41-50
> carriers (2935/2936/5197/5198/5494) are sampled and PASS — 7/7 CrimePoint contexts driven; the WI-3 AbilityLevel
> closure closed the AbilityLevel objective family and added the t10 tier so the nine level-50 single-ability
> carriers (6070/6075-6082) are sampled and PASS — 10/10 AbilityLevel contexts driven (6069 dropped, not counted),
> 5967 (all-abilities branch) flipped SKIP→PASS; the WI-4 MateLevel closure added the t11 tier so the four level-50
> carriers (5465/5466/5812/5813) are sampled and PASS — 6/6 MateLevel contexts driven (5430/5464 flipped
> SKIP→PASS in T3; 6015 is an orphaned context, excluded from t11 so the census gains no new orphan SKIP); the
> WI-5 CompleteQuest closure added the t12 tier so the nine level-50 carriers (5816-5821/5862/5868/5911) are
> sampled and PASS — 11/11 CompleteQuest contexts driven (5814/5815 flipped SKIP→PASS in T3). Band-21-30 sweep
> calibration: kind_id-1 None components (legacy task-board
> step, engine walks Start→None→Supply) now emitted as "None" — 5 quests flipped FAIL→PASS (275/281/305/371/604).
> Wave-1+2 closures flipped 73 SKIP→PASS cumulative (36 wave-1 + 37 merged-line incl. 1702 multi-gap); wave-3
> ZoneKill closure flipped 23 more (73 + 23 = 96 cumulative); WI-2 CrimePoint closure flipped 2 more census SKIP→PASS
> (2916/2926) + 5 unsampled carriers → 7/7 driven (98 cumulative); WI-3 AbilityLevel closure flipped 5967 + 9
> unsampled carriers → 10/10 driven (108 cumulative, 6069 dropped not counted); WI-4 MateLevel closure flipped 5430/5464 + 4 unsampled
> carriers → 6/6 driven (114 cumulative); WI-5 CompleteQuest closure flipped 5814/5815 + 9 unsampled carriers →
> 11/11 driven (125 cumulative); WI-7 T13 sweep drove band 31-40 643/643 PASS (631 new t13 manifests, 12 sampled
> earlier) with zero harness SKIPs as predicted; WI-8 T14 sweep drove band 41-50 1591/1591 PASS-or-doc-SKIP
> (1528 new t14 manifests, 63 sampled earlier, 6069 dropped excluded) — 1589 PASS / 2 SKIP (3419/4967 kept-by-ruling) /
> 0 FAIL; WI-9 T15 sweep drove band 51-55 268/268 + the lvl-99 straggler 3465 = 269/269 PASS-or-doc-SKIP
> (264 new t15 manifests + 3465, 4 band contexts sampled earlier: 6095/6578/6600/6615) — 265 PASS / 0 SKIP / 0 FAIL,
> zero first-sweep FAILs and zero new SKIPs (3465 is a no-acts shell; the band's 6 CheckTimer carriers 6108/6131/6154/
> 6162-6164 PASS via the auto-pass kind — WI-10 owns driver-fidelity for that family); the 10 first-sweep FAILs were TWO harness expectation-model gaps + ONE real engine bug, all fixed with
> engine-source evidence: (1) score-quest under-credit — generator fired count events but the engine score branch
> needs Σ Count×Objective ≥ Score (MaxObjective = Score/Count+1 proves the data intends objectives beyond the
> displayed count); now fires scaled events (7 quests: 3076/3089/3625/4343/5062/5063/5064); (2) Ready-step OR
> semantics — QuestComponent.RunComponent ORs Start/Ready acts, so an always-true act (SupplyRemoveItem) advances
> past Ready without the report event (5174/5722); (3) QuestActConReportJournal subscribed in InitializeAction
> (step-entry) instead of InitializeQuest (ctor) like its ReportNpc/ReportDoodad siblings — ltd quests stuck at
> Progress never enter Ready, so the journal report could never fire (3630); subscription moved to ctor, sibling
> pattern. zero PASS→SKIP regressions across every sweep. WI-10 (2026-08-10, t_abafd918) added driver-fidelity
> probe stages to the census manifests: TIMEOUT (37 CheckTimer quests — driver fires the timeout task's exact
> body QuestManager.OnTimerExpired on a fresh probe and asserts FailQuest; 7 ineligible engine-grounded: 6 rest
> at Ready so the timer is already removed at end-state entry, 1897 never enters the CheckTimer comp), RESET
> (602 daily/repeatable quests — engine ResetDailyQuests + AddQuest re-accept, QuestDailyLimit gate; detail
> Daily 7/DailyHunt 10/DailyLivelihood 11/DailyGroup 12 = the exact set ResetDailyQuests clears), GUARD_DIED
> (3 escorts — dead-guard probe per BUG-008 semantics: RunAct false ⇒ stall at the guard kind; 1313 skipped
> engine-grounded: Start comp ORs its acts so the always-true CheckTimer carries the step). 642 probe stages
> all PASS on the merged census — 4518/4518 runnable, 0 FAIL, 10 documented SKIPs unchanged. New rig pins:
> QuestCheckTimerRigTests 2/2 (timer registered at accept → expiry fires FailQuest), QuestDailyResetRigTests
> 4/4 (completed daily refused → ResetDailyQuests clears → re-accept; repeatable re-accepts immediately),
> QuestCheckGuardRigTests 3/3 (alive → true; dead → false + stall; npcId-0 unresolvable → false + stall).
> Census regen deterministic (byte-identical ×2); band denominators + zone coverage (Gweonid/Lilyut/Mahadevi/
> Tiger Spine/Falcony/Sunny Wilderness/Ancient Forest/Marionople/Two Crowns/White Forest/Singing Land/Sunrise
> Peninsula) in runnability.md (census-meta.json-driven). Fail-before states on the
> wave-1/wave-2 rig commits (2283c0df/7a1145be). Watch items: EtcItemObtain engine no-op, cinema zero-wired,
> honor zero-wired (zero-wired-domains.md), ZoneKill ZoneId unenforced (§2.4, zero-wired-domains.md §9).
> See scorecard-explorations/runnability.md + zero-wired-domains.md §8/§9.

## Zero-data-wired domains (data exists, server ignores it)

- **fx-visuals** (15 tables): fx_cam_fovs, fx_cgas, fx_cgfs, fx_chrs, fx_decals, fx_group_fx_items...
- **siege** (5 tables): siege_items, siege_plans, siege_settings, siege_ticket_offense_prices, siege_zones
- **mates** (4 tables): mate_equip_pack_groups, mate_equip_pack_items, mate_equip_packs, mate_equip_slot_packs — **WIRED 2026-08-24 (0482ba3f0):** all four loaded in MateGameData; fail-closed equip legality at MateEquipmentContainer.CanAccept (MATE-01 W=2)
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

- **A4 save-path fix — SaveManager dirty-tracking (MERGED to fork develop @ 5ed5d6493, 2026-08-10 — t_8c18eb1c, Rei gate ACCEPT t_53025996).**
  Kimi audit finding (t_0fda3cd3, ROADMAP G2-A4): `SaveManager.DoSave` ran a full
  REPLACE for every in-world character every cycle (SaveManager.cs:94) — at 1,000 bots
  the periodic save rewrote ~1,000 full character rows + sub-collections (options,
  abilities, skills, quests, mates, …) each cycle even when nothing changed. Fix
  (a0277ad07, 20 files +404/−15): per-character dirty tracking — `Character.IsDirty`
  (default true: first cycle persists everyone, then settles into dirty-only) +
  `MarkDirty()` at every save-relevant chokepoint (SetPosition + bot movement paths,
  Hp/Mp value-compare props, Money/Money2/Experience, options/action slots/quests/
  skills/abilities/actability/appellations/portals/friends/blocked/mates, persistable
  buffs); `Save()` clears IsDirty on success; `SaveDirectlyToDatabase` stays
  unconditional (disconnect path unchanged — E2E restart-persistence contract intact).
  `DoSave(bool saveAllCharacters = false)` force-all seam — shutdown (StopAsync /
  ShutdownTask) and `/save` + `/shutdown` persist everything; `GetCharactersToSave()`
  extracted as a testable seam. Evidence: SaveManagerTests 10/10 (incl. 1,000-character
  simulated load: all-clean → 0, touched-subset → dirty-only, force-all → all); branch
  gate 1481/0/1; merged-tree gate 1575/0/1; M2bE2e restart-persistence 5/5
  (t_2ee39438). A4 acceptance (autosave p95 < 2s at 250 chars, zero `_isSaving` skips)
  remains a milestone-gate measurement.

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
