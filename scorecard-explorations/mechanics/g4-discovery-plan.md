# G4 Discovery Plan — Quest 251 Objective → Valid Hostile Target (SYNTHESIS ONLY)

SYNTHESIS ONLY. Repo `/root/aaemu-dev`, branch `develop`. Nothing modified, built, or run. Sources only: `g4-objective-credit.md` (§§A–C + headline), `g4-perception-targets.md` (§§D–G), `g3-closeout-freeze.md` (freeze rule). Every code/data claim cites `file:line` or doc section; synthesis judgments are labeled `[JUDGMENT]`.

Freeze rule (restated, not altered — `g3-closeout-freeze.md` §d): G3 is FROZEN at PASS-BEHAVIOR. No new features, acceptance criteria, or journey stages on the G3 path. Regressions may REPAIR ONLY the frozen chain (3512-first → 251L2 → AcceptQuest/Completed → active); any widening is a new gate with its own fixture. This plan adds nothing to G3.

## A. Quest 251 Objective Map (chain verbatim from objective doc)

Template (`g4-objective-credit.md` §A.1; `compact.sqlite3:quest_contexts WHERE id=251`): 251 / `화난 멧돼지들`, LEVEL 2, zone 125 / category 4 / grade 1, non-repeatable/successive/selective f, milestone 5.

Component chain (`g4-objective-credit.md` §A.2; `quest_components WHERE quest_context_id=251`; `quest_acts WHERE quest_component_id IN (383,633,638,639)`; kind enum `QuestComponentKind.cs:3-14`):

| Component | Kind | Act / type / detail | Meaning |
|---|---|---|---|
| 383 | 2 Start | 333 / `QuestActConAcceptNpc` / 77 | accept at npc 3512 (`quest_act_con_accept_npcs:77`) |
| 633 | 4 Progress | 10473 / `QuestActObjItemGather` / 616 | gather item 4058 ×3 (`quest_act_obj_item_gathers:616`; highlight NULL, cleanup t, destroy_when_drop t) |
| 638 | 6 Ready | 457 / `QuestActConReportNpc` / 104 | report at npc 3512 (`quest_act_con_report_npcs:104`) |
| 639 | 8 Reward | 36911 / `QuestActSupplyItem` / 4075 | reward item 18791 ×1 (`quest_act_supply_items:4075`) |

Chain (verbatim, §A.2): Quest **251** → Progress component **633** → ObjItemGather act **10473** (detail 616) → item **4058 ×3** → NPC template **3475** (sole drop source, via loot pack 4530) → **≥3 kills + 3 successful loots**.

Step machine (§A.3): `Quest.GoToNextStep` order Start→None→Supply→Progress→Ready→Reward (`NewQuestCode.cs:95-160`); `AcceptQuest` drains Start→Supply→Progress automatically (`GameplayActor.cs:843-852`); `AdvanceQuest` = `RunCurrentStep()` only (`GameplayActor.cs:863-879`). From 251-ACTIVE the first progress step served is component 633. `QuestActObjItemGather.RunAct` credits from live inventory (`GetItemsCount(4058) >= Count`, `QuestActObjItemGather.cs:25-30`). Loader: `QuestManager.cs:1342-1368` (NULL highlight → 0 at `:1357`). Drop source (§A.4): `loots:10014` = pack 4530 → item 4058 ×1–1 (only `loots` row with 4058); `loot_pack_dropping_npcs:2093` = npc 3475 → pack 4530 default t; `npcs:3475` = Solzreed Boar, kind 2, LEVEL 2, faction 115, aggression f. Spawner ids 3372/3710/3729/8541 (`npc_spawner_npcs WHERE member_id=3475`); sqlite `npc_spawners` carries no coordinates; positions from `npc_spawns.json:183347-183386` (five UnitId-3475 entries near giver, nearest cited boar ~60 m [INFERENCE]).

## B. Quest Credit Authority (`OnItemGather` + kill-neutral + attribution)

Authoritative semantic for 251 (§B.1): `QuestActObjItemGather.OnItemGather` (`QuestActObjItemGather.cs:70-78`) matches `questAct.Id == ActId AND args.ItemId == 4058` → `SetObjective(owner.Inventory.GetItemsCount(4058))`. Identity matched = **item template 4058 + quest-act id**; no objId, NPC template, range, or party check. Armed per-quest-instance in `InitializeAction` (`:32-39`), removed in `FinalizeAction` (`:41-47`); event shape `OnItemGatherArgs{QuestId, ItemId, Count}` (`UnitEvents.cs:97-102`). Fired by `QuestManagerEvents.DoItemsAcquiredEvents` (`QuestManagerEvents.cs:86-103`); producer = `Inventory` acquire path (`Inventory.cs:920-924`).

Kill half is credit-neutral for 251 (§B.2): `Npc.DoDie` attribution (`Npc.cs:839-1032`) — tag-team in-range (`:843-861`, range = `LootingContainer.MaxLootingRange` 200f, `LootingContainer.cs:32`), tagger (`:863-872`), killer fallback (`:876-887`), eligible loop + XP scaling (`:890-982`, events at `:987`), TagShare fanout (`:999-1022`); pets/mates XP-only (`:975-981`); party only in-range members. `DoOnMonsterHuntEvents` (`QuestManagerEvents.cs:169-213`) fans out `OnMonsterHunt{NpcId=TemplateId}` + group + zone-kill + kill-accept — **progress-neutral for 251** (no hunt acts, not kill-accepted; act inventory §A.2). Kill-objective matching in general (§B.3): `QuestActObjMonsterHunt.OnMonsterHunt` (`QuestActObjMonsterHunt.cs:40-47`) matches NPC template id (`UnitEvents.cs:83-88`; group via `QuestManager.CheckGroupNpc`, `QuestManager.cs:136-139`). Corpse→objective chain (§B.4): `Unit.DoDie` → `GenerateLoot` (`Unit.cs:532-533`; pack rolls `LootingContainer.cs:81,204-208`) → `GameplayActor.Loot` (`GameplayActor.cs:1659-1688`) → acquire → `DoItemsAcquiredEvents` → recount → Progress→Ready.

G4 eligibility rule, derived (§B.5): quest-valid iff (1) `TemplateId == 3475`; (2) attackable via `CanAttack`; (3) kill yields loot rights under DoDie rules; (4) corpse lootable within loot range (`GameplayActor.cs:1668`). **Credit-time identity = item 4058; kill-time identity = template 3475; no method matches objId** — the selector must carry template→objId resolution explicitly.

## C. Existing PlayerBot Path (ownership condensed + D1–D4)

Condensed from §C.1 (owners only; code cites verbatim from source doc):

| Responsibility | Owner | Note |
|---|---|---|
| read-active-quests | `QuestBehavior.Run` | `ActiveQuestIds` via `BotObservedContext.Capture` (`QuestBehavior.cs:70-71`); seam `GameplayActor.Observe` (`GameplayActor.cs:222-288`) |
| resolve-objective | **NONE (production)**; scenario-only `LevelingLoopScenario.PursueObjectives` (`:813-819`, dispatch `:830-1070`) via `RunAsScenario` ← `BotScenarioRunner` (`:170-171`) ← `BotDriveBridge` (`:1187`) | production `AdvanceQuest` runs the step machine blind (`GameplayActor.cs:863-879`) |
| ItemGather→corpse-loot resolution | **NONE anywhere** | `GatherLeg` resolves doodads only (`:2259-2316`); 251 (highlight NULL) fails closed (`:2311-2315`); data exists (`ItemManager.cs:122-123`, `LootGameData.cs:175-178`) |
| find-hostiles (production) | `BotRoamStepExecutor` wildlife loop, 45 m, quest-blind (`:838-855`; gate `:2325-2348`; gated off while legs land work `:711`) | no template filter |
| find-hostiles (scenario) | `SelectHuntTarget` (`:2842-2893`); rig `AdventurerSpikeScenario` SelectHostile (`:761-790`) | quest-valid filter is scenario-only |
| assign-target | canonical `GameplayActor.SetTarget` (`GameplayActor.cs:538-545`); roam loop BYPASSES it (direct `CurrentTarget` writes, `:769-773,:865-866`) | — |
| combat initiation | two production shapes (roam `:804-831` vs HuntLeg `:2694-2821`); `CombatExecutor.cs:54` TEST-ONLY | HuntLeg is the only quest-credited shape |
| quest travel | `ArmQuestTravel`/`ResolveQuestTravelTarget` (`:1271-1358`): Ready-reporter + nothing-active only; 251-Progress → null (`:1281`) | no progress-objective travel |
| 251-specific production handling | NONE | 251 exists only in harness (`GameplayActorQuestActionsTests.cs:20-22,36-40`; `M1M2ReplayScenario.cs:93-96`; `PilotProbeTests.cs:58`; `E2eStack.cs:761-764`) |

Duplications flagged (§C.2): **D1** — two nearest-hostile selections (roam `:842-855` vs `SelectHuntTarget` `:2846-2890`; different filters/radii/homes). **D2** — target-assignment bypass (`SetTarget` vs roam direct writes + manual packet). **D3** — two combat-initiation shapes (roam inline vs HuntLeg SetTarget→Evaluate→burst→Loot); `CombatExecutor` unused in production. **D4** — four "hostile?" spellings (`IsAttackableWildlife` `:2325-2348`; `SelectHuntTarget` CanAttack-only `:2868`; `IsHostileTarget` `:73+`; AdventurerSpike `:786`) — agree on 3475 today, can disagree elsewhere. (D5 radii/perception gap belongs to §D.)

## D. Perception Sufficiency (gaps + helper shape + relevance-derivable)

Snapshot carries objIds only (§D.1): `ActorObservation` (`ActorObservation.cs:22-91`) — `NearbyNpcObjIds` bare uints from 25 m `GetAround` (`GameplayActor.cs:262-264`), `ActiveQuestIds` (`:49`), `BagItemCounts` (`:52-67`). **No template/alive/hostile/distance/relevance/zone/skill field per entity**; `BotObservedContext` is a 1:1 copy (`BotDecisionProposal.cs:16-110`). Per-entity truth lives live: template via `ParentWorld.GetNpc(objId).TemplateId`, alive via `Hp <= 0`, hostility via `IsHostileTarget` (`CombatDecisionTree.cs:73-102`) / `CanAttack` (`BaseUnit.cs:54-138`), distance derived, stealth via `CanSeeTarget` (only `BotSurveySenses.ResolveOccupants` applies it, `BotSurveySenses.cs:247-269`).

All five sites re-resolve live (§D.2): AdventurerSpike `SelectHostile` (`:767-796`); `SelectHuntTarget` (`:2842-2893`, +group `CheckGroupNpc` + zone-group gate `:2877-2882`); roam 45 m scan (`:835-875`, radius `:108-110`, revalidation `:715-722`, cap `:722`); survey resolver (`:242-271`); `QuestBehavior` provider sweep ignoring the snapshot (`QuestBehavior.cs:112-157`, take-3 after nearest sort `:146-156`, 25 m gate `GameplayActor.cs:1216-1222`, range `MaxQuestDiscoverRange = MaxInteractRange = 25f` `:1196,1461`).

No-schema-change helper shape (§D.3): add ONE pure helper shaped like `SelectHuntTarget` for 251 — candidates from `observation.NearbyNpcObjIds`, resolve via `ParentWorld.GetNpc`, filter `TemplateId == 3475` + `Hp > 0` + `CanAttack` + `CanSeeTarget`, nearest-first. Derived projection (`brain-boundaries.md` §2.2), pure function, never stored (`nearby-quest-contracts.md` §2.3 item 4). Relevance-derivable finding (§D.3): quest-relevance needs NO new field — `ActiveQuestIds.Contains(251)` + `BagItemCounts[4058] < 3` (both already in snapshot) × static loot link 3475→4058 (pack 4530; `LootPack.cs:195-201`; Q4 constants `Q4LiveHuntLootE2eTests.cs:37-39`). Only the static NPC→item link must live beside the selector as a named constant, NOT a snapshot column. Explicitly NOT needed: skills/cooldowns, doodad phase, merchant/party/housing/vehicle/nav/zone state. Top-3 gaps: (1) bare objIds + live re-resolution (TOCTOU; dispatch fail-closed via `ResolveUnit`, `GameplayActor.cs:4753-4760`); (2) no stealth filter in `Observe` — G4 applies `CanSeeTarget` itself; (3) no quest↔prey surface — relevance is a derived loot-chain predicate.

## E. Target Eligibility (relevance × legality; owners)

Hard separation (§E.4): quest-relevance and combat-legality share nothing but the objId; merging into one stored flag is PROHIBITED.

Quest-relevance (owner = quest state + static loot chain; decided WITHOUT combat state): `ActiveQuestIds ∋ 251` AND `BagItemCounts(4058) < 3` AND `npc.TemplateId == 3475` (loot link pack 4530; §B.5/§D.3). Combat-legality (owner = engine; queried live per candidate, never cached): `BaseUnit.CanAttack` (`BaseUnit.cs:54-138` — null-faction `:56-57`, self `:58-59`, relation `:60`, mother-zone `:76-80`, PvP/team `:82-104`, NPC-safety TODO open `:109-112`, peace-zone `:130-133`) via the canonical wrapper `CombatDecisionTree.IsHostileTarget` (`CombatDecisionTree.cs:73-102`; doc comment `:64-72`) + alive (`Hp > 0`, revalidate; roam 30 s-cap precedent `:722`; GOAP `TargetDead` `:216-221`) + visible (`CanSeeTarget`; §D.1). Selectable iff relevant AND legal.

Wrappers: REUSE `IsHostileTarget`; REUSE-WITH-CLEANUP roam `IsAttackableWildlife` (`:2325-2348`) — MUST NOT copy a third version (§E.2). `IsSkillInRangeAndReady` (`:233-274`) is ADVISORY ONLY (ordering at most; `Cast` gates template/learned/resolve `GameplayActor.cs:576-594`, `UseSkill` refuses post-Start `:609-622`). Inherited dispatch gates (fail-closed, cite-not-copy): `SetTarget` unresolvable-reject (`:548-561`); `Cast` unknown/unlearned/unresolved (`:576-594`); `AutoAttack` null-or-dead (`:715-717`); `Talk` live-NPC + 25 m (`:987-994`); `Interact`/`Harvest` doodads-only (`:1472,:1532`). [JUDGMENT]: naming `IsHostileTarget` as the single legality spelling is the cheapest drift-proof choice because it is public and already wraps `CanAttack`.

## F. Travel/Target Reuse (null-for-Progress verdict + reuse order)

`ArmQuestTravel`/`ResolveQuestTravelTarget` (`BotRoamStepExecutor.cs:1271-1358`) destination families (§F.1): Ready-reporter spawner (`:1312-1327,1385-1393`) — REUSABLE-WITH-CLEANUP for giver/report staging only, not prey; nearest in-band offerer when ZERO active quests (`:1335-1355`, via `OffersWalkableQuest` `:1370-1382`) — discovery staging only; kill-objective/prey-spawner/POI/template-position — NEVER produced; **active-but-not-Ready (251 in Progress) falls through both branches → null → `"no walkable quest target"` (`:1281) — GAP, the exact 251-ACTIVE state arms no travel today**. `OffersWalkableQuest` mirrors band + `IsDiscoverable` (`:1372-1379`; `:1422-1423`; codes `:1434-1454`) — third "should we walk here" copy; G4 prey-travel must not inherit offer-band logic.

Hunt resolution (§F.2): NO production path resolves a quest-valid kill target (roam = quest-blind FIXTURE-ONLY; GOAP `AcquireHostileTargetAction` = `SetTarget(0)` stub always `Rejected`, `CombatActions.cs:19-23`, FIXTURE-ONLY; scenarios = rig-driven). Extractable yeses: `AdventurerSpikeScenario.SelectHostile` (`:767-796`) and `LevelingLoopScenario.SelectHuntTarget` (`:2842-2893`) — REUSABLE-WITH-CLEANUP; G4 needs the template branch only (no group/zone act on 251). Reuse order (cheapest first, §G): `IsHostileTarget` → extract `SelectHuntTarget`-shaped selector (3475 + `CanSeeTarget`) → snapshot relevance predicate (§E.4) → nearest-legal ordering → never touch travel, GOAP acquire, or skill-readiness. Geography note (§4.2): five near-giver 3475 spawners (`npc_spawns.json:183347-183386`; flat ~89/60/62/164/256 m from giver 3512 at `main_world/npc_spawns.json:184418-184424`) + nine far 3475 (~1.4 km WSW, `npc_spawns_solzreed_wildlife.json:981002-981034`; loader globs `npc_spawns*.json`, `SpawnManager.cs:331-333`); template match alone is insufficient — nearest-legal ordering disambiguates when staged at 3512. [JUDGMENT]: nearest-legal ordering is load-bearing (G.10), not cosmetic, because all 14 spawners share template 3475.

## G. Duplication/Architecture Risks (G.1–G.10 condensed + three projection surfaces)

Condensed from §G (each: location → risk):

- **G.1** `IsAttackableWildlife` (`:2325-2348`, private) vs `IsHostileTarget` (`:73-102`, public) — same rule twice → third copy drifts; G4 calls the public one.
- **G.2** Hunt acquisition ×4 (roam scan; `SelectHostile`; `SelectHuntTarget`; GOAP stub) — G4 must extract ONE selector, delete nothing (G3 frozen, scenarios rig-owned).
- **G.3** Sweep ×3 (`QuestBehavior` `:112-157` vs ignored `NearbyNpcObjIds` vs `QuestDecisionScenario` `:125-132`) — G4 consumes `observation.NearbyNpcObjIds`, adds no fourth scan.
- **G.4** `IsSkillInRangeAndReady` (`:233-274`) vs engine `UseSkill` — advisory copy; order at most, never reject.
- **G.5** `OffersWalkableQuest` vs `IsDiscoverable` vs scenario band filter — offer-shaped ×3; prey-travel must not inherit it (§F.1 gap).
- **G.6** GOAP threat flags (`BotWorldStateProvider.cs:200-222`) vs roam `isDeadOrInvalid` (`:719-722`) — validity projected twice; follow the roam predicate.
- **G.7** GOAP acquire stub (`CombatActions.cs:19-23` → always `Rejected` via `GameplayActor.cs:4753-4760`) — never cite as resolution or wire through it.
- **G.8** `QuestBehavior.cs:26-28` header claim (pursuit "rides hunt/interact branches") — behind it sits quest-BLIND roam hunt; trusting it kills foxes (3492) as happily as boars.
- **G.9** GOAP live scans bypassing `ActorObservation` (`BotWorldStateProvider.cs:94-101,152-177,201-222`) — G4 reads snapshot fields, not side channels.
- **G.10** Template world-wide first-match (`QuestBehavior.cs:210`; `:1323`; `:1387-1389`) — no range check; 1.4 km boar can win by enumeration luck; nearest-ordering is load-bearing.

Three projection surfaces that must stay separate (E.4 + D.3 + G.6/G.9): (1) snapshot quest state (`ActiveQuestIds`, `BagItemCounts`); (2) live combat legality (`CanAttack`/`IsHostileTarget` + alive + visible, queried, never stored); (3) static loot chain (3475→4530→4058, named constant). [JUDGMENT]: keeping these three unmerged is the single highest-leverage anti-duplication decision in G4.

## H. Recommended G4 Contract

- **INPUT:** 251-ACTIVE quest state (G3 freeze end state: `g3-closeout-freeze.md` §g) + immutable `ActorObservation` snapshot (`ActiveQuestIds`, `NearbyNpcObjIds`, `BagItemCounts`; §D.1) + live world for per-candidate resolution only (§D.2).
- **PROCESS:** (1) resolve-objective — read Progress acts of 251 via `QuestManager.GetTemplate(251).GetComponents(Progress)` → act 10473 = ItemGather(4058×3) (§C.3); (2) resolve loot source — 4058 → pack 4530 → template 3475 (§A.4) via existing data helpers (`ItemManager.GetLootPackIdByNpcId`, `LootGameData.GetPack`; §C.1); (3) enumerate — candidates from `observation.NearbyNpcObjIds` (§D.3); (4) quest-filter — relevance predicate (§E.4); (5) combat-filter — `IsHostileTarget` + alive + `CanSeeTarget` (§E.3); (6) deterministic rank — nearest-first + ObjId tiebreak (G3 doctrine: `g3-closeout-freeze.md` §e.7; precedent `QuestBehavior.cs:150-156`); (7) select — canonical `GameplayActor.SetTarget` (§C.1), never the D2 bypass.
- **OUTPUT:** selected ObjId + objective/reason metadata (questId 251, objective act 10473 / detail 616, requiredTemplate 3475, requiredItem 4058, remaining count, per-candidate questRelevant/combatLegal flags, reject tallies, distance, CycleId join key per G3 doctrine `g3-closeout-freeze.md` §e.2/§e.5).
- **STOP BOUNDARY:** ends at identified + selected (G3 handoff: `g3-closeout-freeze.md` §g). No travel, no combat execution, no credit, no turn-in. `ArmQuestTravel` prey routing and HuntLeg/D3 machinery stay out of scope (§F.1–F.2).

## I. Implementation Shape (expected files/classes; NO implementation now)

Expected to change (read-out from §§C.3/G; nothing touched in this pass):

1. **New quest-objective selector helper** (production, `SelectHuntTarget`-shaped per §D.3/G.2): 251-relevance wrapper (template 3475 + item 4058×3 constants beside it) + `IsHostileTarget` legality + `CanSeeTarget` + nearest+ObjId ordering; consumes `NearbyNpcObjIds`.
2. **`QuestBehavior` integration point** (production wake leg; `QuestBehavior.cs:70-88`): per-ActiveQuest proposal path that today only advances/turns-in — the objective→selector call site lives here or in a owned helper it calls ([JUDGMENT]: `QuestBehavior` is the natural owner because it already owns read-active-quests, and the scenario `PursueObjectives` shape must be extracted, not called).
3. **Funnel diagnostics** (G3 doctrine `g3-closeout-freeze.md` §e.5–§e.7): per-predicate tallies + parser-safe DECIDE detail + CycleId join, first-zero rule.

Explicit non-goals (NOT G4): snapshot schema change (§D.3); travel/prey routing via `ArmQuestTravel` (§F.1); combat execution (HuntLeg/D3, `CombatExecutor`); credit/turn-in (`OnItemGather` path already authoritative, §B.1); GOAP acquire stub wiring (§G.7); skill-readiness gating (§G.4); offer-band logic reuse (§G.5); any G3-file behavior change (frozen; `g3-closeout-freeze.md` §d); resolving the 3512-position discrepancy (§4.2: spawner file vs `SolzreedStarterCorridorProfile.cs:30-31`) or respawn timing beyond `npc_spawns_solzreed_wildlife.json:29` (`RespawnTime: 15`).

## J. Proposed E2E Gate (fixture + START rules + evidence + verdicts)

- **Fixture:** bot with 251 ACTIVE (G3 end state), staged near the 3475 near-cluster (giver-3512 staging puts nearest boar ~60 m SE — outside 25 m Observe and 45 m hunt radius, §4.2 — so stage/close-in to ≤45 m before START or the gate measures travel-confounded emptiness [JUDGMENT]); vitals refilled (fractions ≥ 0.95, doctrine §e.4); single-`Character` identity proof (`snap==actor==spawner`, `botSameChar`, §e.1).
- **START observe-only rules:** settle live routes first (stop-seam attempt + standstill poll, §e.3); START gated on measured range + zero drift with the live-resolved target objId; per-bot wake proof on fresh signals (detail overwrite + CycleId + audit growth + leg-lit + server tick; §e.2); deterministic ordering before any cut (§e.7); first-zero rule — name the earliest failing predicate and stop (§e.6).
- **Funnel evidence fields:** `questId` (251), `objectiveId` (act 10473 / detail 616), `requiredTemplate` (3475), `requiredItem` (4058 ×3), `raw` (enumerated candidate objIds), `questRelevant` / `combatLegal` per candidate, `selected` (ObjId or null), `distance` (flat m), `rejects` (per-predicate tallies incl. stealth/range/dead/template), `CycleId`.
- **PASS:** from 251-ACTIVE, the gate observes a deterministic selected ObjId whose template is 3475, relevance predicate true (bag 4058 < 3), legality true (`IsHostileTarget` + alive + visible), assigned via `SetTarget`, with full funnel evidence.
- **Predicate-named FAILs:** `FAIL-OBJECTIVE` (Progress acts unreadable / act 10473 not resolved); `FAIL-SOURCE` (4058→3475 link unresolvable); `FAIL-SWEEP-EMPTY` (no candidates in snapshot); `FAIL-ALL-FILTERED` (candidates exist, none relevant+legal — tallies say which half); `FAIL-ORDERING` (selection non-deterministic across ticks); `FAIL-ASSIGN` (`SetTarget` rejected); `FAIL-STALE` (identity/wake proof void).

## K. Next Action

Exactly one bounded step: implement the production quest-objective selector helper (single pure function, `SelectHuntTarget`-shaped, 251 ItemGather→3475 relevance + `IsHostileTarget`/`CanSeeTarget`/nearest+ObjId, `SetTarget` assignment, funnel tallies) behind the `QuestBehavior` proposal path with the §J evidence fields, in its own gate with its own fixture — no travel, no combat, no G3-file behavior change.

---

## G4 DISCOVERY VERDICT

- G4 DISCOVERY COMPLETE
- Current authoritative path: `QuestBehavior.Run` reads active quests (`QuestBehavior.cs:70-88`) and `GameplayActor.Observe` exposes bare objIds + `ActiveQuestIds` + `BagItemCounts` (`GameplayActor.cs:222-288`); credit for 251 is `QuestActObjItemGather.OnItemGather` on item 4058 (`QuestActObjItemGather.cs:70-78`); no production owner resolves objective→target or item→NPC, and `ArmQuestTravel` returns null for in-Progress 251 (`BotRoamStepExecutor.cs:1281,1302-1358`).
- Missing seam: the item→drop-source-NPC resolver (4058 → pack 4530 → template 3475) plus the quest-valid selector (template + alive + `CanAttack`/`IsHostileTarget` + visible, nearest-ordered, `SetTarget`-assigned) — scenario-only `SelectHuntTarget`/`SelectHostile` prove the shape; production has no copy.
- Recommended G4 implementation: pure `SelectHuntTarget`-shaped selector with the 251-relevance wrapper, integrated at the `QuestBehavior` proposal path, with funnel diagnostics and CycleId join; reuse `IsHostileTarget`, never duplicate it.
- G4 success contract: from 251-ACTIVE, deterministically identify + select a live 3475 (relevant AND legal) via `SetTarget` with full funnel evidence — and stop there (no travel/combat/credit/turn-in).
- Next action: build the selector helper + `QuestBehavior` integration point under a dedicated E2E gate (§J); see §K.
- Key reframing: 251's first step is LOOT-driven gather, so G4 target = loot-source NPC 3475 and credit key = item 4058, not a kill-match.
