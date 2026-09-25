# G7 Discovery Plan — 251 kill → loot → 4058 → credit (2026-09-20, branch develop)

READ-ONLY synthesis. Nothing modified, built, or run. G3–G6 frozen (untouched).
Sources: `g7-combat-death.md` (§§A–D), `g7-loot-credit.md` (§§E–G),
`g7-decomposition.md` (§§H–K), `g7-human-trace-251.md` (trace verdict),
`g6-closeout-freeze.md` (freeze rule, restated below — not altered).
G6 end state: quest 251 ACTIVE, damaging-but-alive 3475 under a live
quest-owned AutoAttack loop, terminal Completed("loop started")
(`g6-closeout-freeze.md:9-20`).
Freeze rule (restated): G6 is FROZEN at PASS-BEHAVIOR — regressions may REPAIR
ONLY the frozen chain (23-proposal → AutoAttack → Completed-started → loop live
+ pinned → trailing HP fall, zeros held); any widening (rotation, kill, loot,
credit, turn-in) is a new gate (G7) with its own fixture
(`g6-closeout-freeze.md:110-115`).

## A. Combat Continuation Path (loop owner, cadence, death detection, request terminal)

- Loop owner is the engine, nobody bot-side (`g7-combat-death.md:§A.1`):
  dispatch `QuestBehavior.Dispatch` goal-guarded arm
  (`AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:821`) →
  `GameplayActor.AutoAttack` (`GameplayActor.cs:779-848`) returns terminal
  `Completed` at dispatch (`:847`); loop = `Character.IsAutoAttack=true` +
  `StartAutoSkill` (`:844-845`) creating `UseAutoAttackSkillTask` on
  `TaskManager` with weapon attack-delay as initial+repeat interval, repeat
  forever (`Unit.cs:601-613`). `GameplayActor.Tick` does NOT pump combat
  (cast effects + request timeouts only, `GameplayActor.cs:4161-4184`); damage
  arrives from scheduler `Execute()` ticks. Queue carries no AutoAttack kind
  (`BotActionCommandQueue.cs` zero matches) — not on this path.
- Cadence (`g7-combat-death.md:§A.2`): first strike synchronous in dispatch
  (`GameplayActor.cs:841`, respects attack timing); subsequent strikes per
  `Execute()` tick, re-reading attack speed each tick
  (`UseAutoAttackSkillTask.cs:77-80`). Skill chosen at dispatch only: melee 2
  vs ranged 4 by role/distance/weapon (`GameplayActor.cs:800-808`,
  `CombatDecisionTree.cs:111-112`); same-skill re-dispatch → "already active"
  (`:811-815`), different skill cancels first (`:817-823`).
- Range violation = pause, never cancel (`g7-combat-death.md:§A.3`):
  per-tick skips on skill-casting (`:44-45`), GCD (`:46-47`),
  `!CanAttack` (`:49-50`), over-range (`:53-56`); max range = weapon else
  skill-template else 3 m fallback (`:124-136`).
- Death detection is HP-zero, no flag/event (`g7-combat-death.md:§A.4`):
  `ReduceCurrentHp` clamps + `SCUnitPointsPacket`, then `PostUpdateCurrentHp`
  (`Unit.cs:373-395`); early-return if `Hp>0` else unconditional `DoDie`
  (`:439-459`); `IsDead` derived `Hp<=0` (`:946-952`). Loop stop conditions
  (caster Hp≤0, target null/Hp≤0, self-target, Cancelled → `StopAutoAttack`,
  `UseAutoAttackSkillTask.cs:36-40,:116-122`); reliable stop is the task's own
  next-tick self-termination — engine `DoDie→StopAutoSkill` coverage is partial
  (only `CurrentTarget!=null` Character combos, sets `Cancelled` only,
  `Unit.cs:536-568,:583-599`).
- Request terminals (`g7-combat-death.md:§A.4`):
  `Requested→Accepted→Running→Completed|Rejected|Interrupted|TimedOut`
  (`ActorRequest.cs:9-13`), `IsTerminal` (`:184-185`); AutoAttack outcomes
  `Completed` (started/already-active) vs `Rejected` (null/dead target at
  dispatch `:789-791`; unknown skill `:825-827`). Death never mutates the
  already-terminal request. Full chain + killer corroboration (target-clear +
  `SCTargetChangedPacket`, `Unit.cs:551-567`) (`g7-combat-death.md:§A.5`).

## B. Death Authority (engine semantics)

- Authority: `Unit.ReduceCurrentHp → PostUpdateCurrentHp → Unit.DoDie`
  (`Unit.cs:373-395,:404-459,:484-569`); `Npc` overrides `DoDie`, not the HP
  path (`Npc.cs:839-1032`) (`g7-combat-death.md:§B.1`).
- Nobody "marks" dead — death = executing `DoDie`; `Unit.DoDie`
  (`:484-533`): `InterruptSkills`, `IsInBattle=false`, fires `Events.OnDeath`
  + `ParentWorld.Events.OnUnitKilled` + killer `Events.OnKill` (`:493-495`;
  shapes `UnitEvents.cs:311-314,:322-325`), `Buffs.RemoveEffectsOnDeath`,
  `SCUnitDeathPacket` (`:517`; NPC wait = `Spawner.RespawnTime`, `:511-514`).
- Loot fixed synchronously at death: `Unit.DoDie` calls
  `LootingContainer.GenerateLoot(killer)` (`Unit.cs:532-533`) before
  aggro/target cleanup; `Npc.DoDie` then clears aggro/taggers/`CurrentAggroTarget`,
  `Spawner.DoDespawn` + `Ai.GoToDead()` (`Npc.cs:1024-1031`)
  (`g7-combat-death.md:§B.4`).
- Attribution (`g7-combat-death.md:§B.5`, `Npc.cs:843-887`): TagTeam in
  `MaxLootingRange` (200 f, `LootingContainer.cs:32`) eligible; else `Tagger`;
  else killer-fallback (`DoOnMonsterHuntEvents` + full XP). Eligible loop grants
  level-scaled XP (`:949-982`) + hunt events each (`:987`); TagShare is
  quest-events-only (`:991-1022`). For 251 the hunt fanout is progress-neutral
  (only objective act is the ItemGather — see §G); attribution still matters
  for XP, container `EligiblePlayers`/loot rights (§C), recorded `Killer`
  (`LootingContainer.cs:101`).

## C. Corpse/Loot State (creation, representation, expiry)

- No corpse entity: dead Npc IS the corpse (no `Corpse` type; only
  `Ai.GoToDead()`=`BehaviorKind.Dead`, `NpcAi.cs:282-284`, from
  `Npc.cs:1030-1031`); same object, `Hp==0`, `DeadTime` set (`Npc.cs:841`),
  loot container attached, awaiting despawn (`g7-combat-death.md:§B.2`).
- Container exists on the unit, filled once at death
  (`g7-combat-death.md:§C.1-C.2`): `LootingContainer(owner)` bound
  (`LootingContainer.cs:22-75`); `AlreadyGenerated` once-only guard (`:85-92`);
  `GenerateLoot` rolls every pack from
  `GetLootPackIdByNpcId(templateId)` (`:203-211`; lookup returns full list,
  `ItemManager.cs:122-123`); `UpdateLootState` broadcasts `SCLootableStatePacket`
  (`:294-303`).
- Ownership/rights (`g7-combat-death.md:§C.3`): eligibility mirrors kill side
  (`:123-196`; solo covered by tagger-or-killer fallback; `lootDropRate` = max
  eligible `DropRateMul` or killer's, `:170-196`); per-entry
  `TryTakeLootLocked` (`:393-512`): `HighestRoller`, team `LootingRule`
  branches (solo FreeForAll immediate; RotateWinner/LootMaster/Public,
  `:441-483`), roll pooling (`:490-507`); quest-item gate
  (`Template.LootQuestId>0` requires `HasQuest`, `:414-422`). Granted entries
  removed (`TryReserveLootItem`, `:709-722`); bag-full restores (`:615-622`,
  `:724-730`). Public fallback after `MakeLootPublicTime` 180 s (`:37`):
  `CanMakePublic` (`:789-799`) → `MakeLootPublic` (`:771-787`).
- ObjId stability (`g7-combat-death.md:§B.3`): corpse keeps ObjId while
  lootable; released at despawn (`SpawnManager.cs:1136-1160`); [INFERENCE]
  respawn is a fresh `Npc` (`NpcSpawnerNpc.cs:130-133`) with a new ObjId; grant
  entry ids derive from corpse ObjId (`:270`).
- Respawn/despawn scheduling (`g7-combat-death.md:§B.4`):
  `NpcSpawner.DoDespawn` (`NpcSpawner.cs:742-777`): respawn at
  `DeadTime+RespawnTime` (rolled `SpawnDelayMin..Max`,
  `NpcSpawnerNpc.cs:130-133,:160-186`); despawn at `DespawnTime` **plus
  `LootDespawnExtensionTime` 300 s** (`LootingContainer.cs:42`) when non-empty
  (`:768-773`). Base `DespawnTime` value source UNKNOWN. Once emptied,
  despawn collapses to spawner time or `now+2 s` (`PostLootMinimumDespawnTime`,
  `LootingContainer.cs:47,:304-323`) (`g7-combat-death.md:§C.4`).

## D. Loot Pack 4530 / Item 4058 (drop-chain details)

All data facts from read-only `compact.sqlite3` selectors
(`g7-combat-death.md:§D`).

- Pack 4530 = exactly one row: id 10014, group 0, item 4058,
  `drop_rate` 10000000, min 1, max 1, grade 0, `always_drop` f; only `loots`
  row with `item_id=4058` → sole source.
- Chance: conditional-guaranteed, not probabilistic — group 0 with zero
  `loot_groups` rows for 4530 takes the per-item path in `GeneratePackNewV2`
  (`LootPack.cs:190-213`): `requiresDice=floor(10M×lootDropRate×LootRate)`;
  grant iff `dice<requiresDice || AlwaysDrop` (`:204-211`). At base rates
  (10M rate, `lootDropRate`=1, `LootRate`=1) always granted when the roll runs;
  un-guaranteed only by quest filter (§D.3), negative `DropRateMul`, or server
  `LootRate`<1. `always_drop`=f irrelevant (10M saturates the roll alone).
- Quantity: exactly ×1 per kill (`Random.Next(1,1+1)`, `LootPack.cs:308`).
- Dropping NPCs: only 3475 drops 4530 (link id 2093, default t); 3475 drops 4
  packs (2093→4530 t; 5163,6851→1616 f; 79421→8055 f), all roll per kill.
  Pack 1616 = zero rows (dead link); pack 8055 = item 29203 @ rate 1/10M
  (~never). Net per 3475 kill (251 holder, base rates): **4058 ×1** + ~never
  29203 ×1.
- Quest gates (effectively quest-only): item 4058 row `loot_quest_id`=251
  (name `솔즈리드 멧돼지 고기`), `max_stack_size`=10, `bind_id`=2. Gate 1
  (generation): quest items skipped unless killer `HasQuest(LootQuestId)`
  (`LootPack.cs:194-201`; `player`=`killer as Character`,
  `LootingContainer.cs:208`). Gate 2 (taking): requires `HasQuest` else fail
  packet (`:414-422`). No level/explicit quest-state check in generation
  (XP level-scaling `Npc.cs:951-982` does not touch loot); 10M grant survives
  any non-negative `DropRateMul` [INFERENCE from `:204-206`].
- Kills-per-4058: exactly **1** (holder, base rates, corpse looted); quest
  needs 4058 ×3 (`quest_act_obj_item_gathers` 616) → **≥3 kills + 3 loots**,
  no variance at base rates; variance only via sub-unit `LootRate`, negative
  `DropRateMul`, missed loot window, bag-full restores, contested rolls.

## E. Canonical Loot Capability (verb/handler/delta)

- Verb: `IGameplayActor.Loot(uint lootOwnerObjId, …)`
  (`IGameplayActor.cs:196-202`); `ActorActionType.Loot`=11 (`:947-948`);
  `BotActionKind.Loot`=7 dispatches straight through
  (`BotActionCommandQueue.cs:581-582`), mapped Loot→Loot (`:900`)
  (`g7-loot-credit.md:§E.1`).
- Implementation (`GameplayActor.cs:1733-1762`): preconditions fail-closed —
  owner resolves (`:1739-1741`), flat distance ≤ `MaxLootingRange` 200 f
  (`:1742-1743`; effectively never binding next to a corpse; `MaxInteractRange`
  25 m at `:1535` does NOT apply), non-empty (`:1746-1748`); handler
  `OpenBag(Character, owner, lootAll:true)` (`:1756`, the exact
  `CSLootOpenBagPacket` call, `:1753-1755`); `granted = before-after`,
  `Complete` (`:1758-1761`) — **Result counts entries removed, not item
  units** (callers diff the bag; `GameplayActorLootGrantTests.cs:60-62`).
  Unkeyed retry after success → `Rejected` (already looted); same-key retry
  rejects pre-flight (`GameplayActorInteractLootTests.cs:179-210`).
- End-to-end handler chain (all production, `g7-loot-credit.md:§E.2`):
  `Unit.DoDie→GenerateLoot` (`Unit.cs:532-533`) → `GameplayActor.Loot`
  → `OpenBag(lootAll:true)` (snapshots, `TryTakeLoot` each,
  `LootingContainer.cs:332-375`) → `TryReserveLootItem` atomic remove
  (`:709-721`) → `Bag.AcquireDefaultItem(Loot/LootAll,…)` (`:670`) →
  `Inventory.OnAcquiredItem` (`ItemContainer.cs:493-494,:736,:500`) →
  `DoItemsAcquiredEvents` (`Inventory.cs:924`) → `OnItemGather` → recount
  `SetObjective` (§G). Loot-all leaves failures via `RestoreLootItem`
  (`:724-730`) — partial grants possible, entries conserved
  (`GameplayActorLootGrantTests.cs:227-231`). Corpse lifecycle
  (`Npc.cs:1024-1031`; empty → `SCLootableStatePacket(hasItems:false)` +
  shortened timer, `:294-303`, `PostLootMinimumDespawnTime`=2 f, `:47`).
- NOT loot paths (`g7-loot-credit.md:§E.2`): `Interact`/`InteractWith` are
  doodad-only (`:1540-1592`; corpse objId → `Rejected`); corpses never become
  doodads (`BotRoamStepExecutor.cs:908-911`); `InteractNpc` is dialogue+talk
  credit (`:1154-1257`); `PackPickup` is trade-pack-only (skill 11361,
  `IGameplayActor.cs:507-512`).

## F. Existing Post-Kill Logic (owners + classes)

Scale: PRODUCTION = scheduler-wired; REUSABLE = production-grade, unwired;
SCENARIO-ONLY / FIXTURE-ONLY / DUPLICATE / UNKNOWN
(`g7-loot-credit.md:§F`).

- `BotRoamStepExecutor` wildlife loop — PRODUCTION (quest-blind)
  (`BotRoamStepExecutor.cs:745-790`): detection per-wake
  (`targetUnit==null || Hp<=0 || !IsAttackableWildlife` + 30 s cap, `:745-748`);
  `Hp<=0` → `actor.Loot(objId)` with (Completed,granted>0)/(Completed,0)/
  non-Completed triage (`:754-769`); `StopAutoAttack` (`:772-775`); direct
  `CurrentTarget=null`+packet (`:777-781`); clear + preempt stale Move
  (`:786-790`). Follow-up re-scans nearest attackable 45 m, no template/quest/
  inventory gate (`:862-901`; no `GetItemsCount(4058)` in file). Second teardown
  at `:2472-2476` (PvP leg) → PRODUCTION + DUPLICATE.
- `QuestBehavior` quest leg — PRODUCTION with explicit loot gap
  (`QuestBehavior.cs:677-748`; priority 23, `QuestDecisionScenario.cs:65`;
  AutoAttack-only dispatch `:816-821`): grep for
  `Loot|corpse|StopAutoAttack|TargetDead|HasLooted|Teardown` hits only the
  `target-dead` withdrawal (`:570-571,:707-711`) + "no Loot" comments
  (`:540,:675,:819-820`) — zero post-kill handling/teardown/corpse
  detection/selection/inventory checks. `StepQuestLeg` (`:1257-1311`) adds
  none. `OutOfCombatRecoveryModule` priority 85
  (`OutOfCombatRecoveryModule.cs:61`) may preempt post-fight wakes.
- GOAP — REUSABLE (registered, planner-covered, never scheduled):
  `LootCorpseAction` (`CombatActions.cs:130-165`, pre `TargetDead`+
  `HasCorpseToLoot`, →`Loot(CurrentTarget.ObjId)` `:144-149`);
  `ExecuteCombatComboAction` (`:83-125`); state
  (`BotWorldStateProvider.cs:216-225`); `HasLooted` (`BotMemory.cs:43`) has no
  production writer (only test manual-set,
  `GoapScenarioIntegrationTests.cs:344`); zero production
  `new GoapBotStepExecutor`; binding is
  `BotGoalArbiterStepExecutor→BotRoamStepExecutor` (`Program.cs:324-332`).
- Scenario/fixture — SCENARIO-ONLY / FIXTURE-ONLY:
  `LevelingLoopScenario.HuntLeg` (`LevelingLoopScenario.cs:2694-2821`,
  corpse=`Hp<=0` `:2755,:2767`, reselect `:2814-2815`, via
  `RunAsScenario←BotScenarioRunner←BotDriveBridge`); `AdventurerSpikeScenario`
  (`:561-580`, loot optional) + `PartySpikeScenario.AttemptLoot=false`
  (`:138-141,:461-467`); seeded-container proofs + `QuestObjectiveCombatTests`
  (`:232-334`); `QuestObjectiveTargetSelector` PRODUCTION relevance (251 active
  + 4058<3, `:101-108`; constants `:37-53`).

## G. ItemGather Credit Authority (event → progress chain + kill-neutral verdict)

- Exact 4058→progress chain, all production (`g7-loot-credit.md:§G.1`):
  1. `Inventory.OnAcquiredItem` fires iff `count>0 && item!=null`
     (`Inventory.cs:915-925`) → `DoItemsAcquiredEvents(Owner, TemplateId,
     Count)` (`:924`); both container paths funnel here (new-item
     `ItemContainer.cs:493-494` incl. mail take `:500`; stack top-up `:736`).
  2. Fanout `DoItemsAcquiredEvents` fires `OnItemGather{ItemId,Count}` +
     `OnItemGroupGather` unconditionally, no party/range/quest check
     (`QuestManagerEvents.cs:86-103`; `QuestId` unset by this producer).
  3. Match `QuestActObjItemGather.OnItemGather`
     (`QuestActObjItemGather.cs:70-78`): `questAct.Id==ActId &&
     args.ItemId==4058` → `SetObjective(bag count)` (`:77`) — act id + item
     identity only; no objId/NPC/range/party/killer check; no `Count>0` guard
     (contrast `QuestActEtcItemObtain.cs:46`).
  4. Recount-never-increment, capped: `SetObjective` clamps to `MaxObjective`
     (`QuestActTemplate.cs:119-131`; 251 Count=3, no score → max 3, `:105-112`);
     change → `RequestEvaluation()` (`:129`; `Quest.cs:480-485`); Progress→Ready
     when `RunAct` reads ≥3 (`:25-30`).
  5. Subscription = active-quest requirement: armed `InitializeAction`
     (`:32-39`, seeded from live bag) removed `FinalizeAction` (`:41-47`); no
     active 251 Progress step → no subscriber → no progress (pre-held 4058
     counts at accept-time via recount).
- Edge semantics (`g7-loot-credit.md:§G.2`): ANY acquisition path credits
  (loot, `Buy` via `AcquireDefaultItem(StoreBuy)` at `GameplayActor.cs:2890`,
  stocked/preseeded `PlayerBotController.cs:150-155` /
  `M1M2ReplayScenario.cs:31-35`, mail takes `ItemContainer.cs:498-501`); the
  G7 proof MUST use the loot path. Consumes (`UseItem`→`OnConsumedItem`→
  negative-count acquire, `QuestManagerEvents.cs:78`) recount harmlessly.
  Raw field/DB writes bypass events (never credit). Synthetic
  `controller.GatherItem` (`PlayerBotController.cs:76-77`) is engine-injection,
  never loot credit. Coins/pack auto-equip (`LootingContainer.cs:625-664`)
  emit no event (irrelevant for bag-item 4058). Recount is batch-independent
  (3×1 walks 0→1→2→3; ×3 jumps 0→3); 1:1 capped at 3; cleanup consumes
  `min(objective,max)` (`:56,:66`); no party fanout on the item path (loot-rule
  branches `:442-483` gate who receives; each bag fires its own credit).
- KILL-NEUTRAL verdict (`g7-loot-credit.md:§G.3`): kill fanout
  (`DoOnMonsterHuntEvents`, `QuestManagerEvents.cs:169-213`) emits
  `OnMonsterHunt{NpcId=3475}` (`:177-182`) + group/zone/kill-accept fanouts,
  but 251 subscribes to NONE (only Progress act is ItemGather 10473/detail
  616; no hunt acts; 3475 not a kill-acceptor — 251 accepts from NPC 3512,
  `:207-212`). Gather keys (act id + item 4058) are disjoint from kill keys
  (NpcId 3475) — no handler maps one to the other. Corroboration: G6 held meat
  0→0 with a damaging loop, quest still Progress. **Death≠progress unless the
  loot→acquire→`OnItemGather` chain completes.**

## H. Recommended G7 Decomposition (one gate vs G7a-d)

- Recommend **four gates G7a (kill) / G7b (corpse observed) / G7c (loot→grant)
  / G7d (acquisition→Ready)** (`g7-decomposition.md:§H`): four phases, four
  owners, four disjoint failure vocabularies — one end-to-end gate conflates
  combat-stall vs death-missed vs unlootable vs dry-roll (one `Starvation` or
  meat-delta-zero says nothing). Continues the G3→G6 one-predicate-family-per-
  gate doctrine (G4 `G4QuestObjectiveTargetTests.cs:30-37`; G5 stops at range
  `G5QuestPursuitGateTests.cs:46-48`; G6 stops at first damage
  `G6QuestCombatGateTests.cs:52-54`).
- Least-harness cost: all four reuse G4–G6 staging (fresh bot, level 10,
  vitals refill, 251 pre-hold at giver 3512, `teleportToNpc(3475)`+
  `PollNpcObjId` proof, START snapshot, observe-only;
  `G4QuestObjectiveTargetTests.cs:24-28`, `G6QuestCombatGateTests.cs:23-28`) —
  marginal cost is one START + one PASS predicate per gate. Chaining allowed
  (G7b on G7a's corpse, G7c on G7b's) with re-taken START, so chained despawn
  reads as `HARNESS/corpse-gone`, never a loot failure.
- Single-gate variant REJECTED except as post-pass sweep: cheaper wall-clock
  but nil fault isolation; acceptable ONLY as final integration sweep with no
  new claims. No new owners/subsystems/queue kinds anywhere.

## I. Recommended Ownership (existing components only, no new owners)

| Phase | Behavior owner | Capability / engine truth |
|---|---|---|
| Combat continuation → death | `QuestBehavior` — `CombatProposal` (`:677-755`) + `CombatGoal` (`:80-81`) + priority 23 < pursuit 24 (`QuestDecisionScenario.cs:60-65`); quest-side teardown is G7's by debt assignment (`g6-closeout-freeze.md:132`) | `GameplayActor.AutoAttack` (`:779-848`) / `StopAutoAttack` (`:850-869`); engine owns damage+death (`Npc.DoDie`, `Npc.cs:839-842`) |
| Corpse detection | `QuestBehavior` — promote `target-dead` withdrawal (`:707-711`, diag `validate=target-dead`) to detection event; loot-once memory beside `LastStopHold`/`LastPursuitIssue` (`:89-96`) or `ActorEffectLedger` (`ActorIdempotency.cs:52-60`); NOT `BotMemory.HasLootedCurrentTarget` (`BotMemory.cs:43`, GOAP-context) | Engine death truth (`Unit.DoDie→GenerateLoot`, `Unit.cs:532-533`); killer target-clear corroboration (roam `:777-781`) |
| Loot execution | `QuestBehavior` dispatch — new goal-guarded arm beside AutoAttack (`:816-822`) | `GameplayActor.Loot` (`:1733-1762`, lootAll `:1753-1761`); queue kind exists (`BotActionKind.Loot`, `:30`, dispatch `:581-582`) but stay on the direct live-actor route (G6 AutoAttack precedent, no queue kind "by design") |
| Credit observation | `QuestBehavior` / `QuestObjectiveTargetSelector.IsRelevant` (4058<3, `:101-108`; funnel `:122-125`) + Ready-guard (`:131-135`, Ready→turn-in only) | Engine credit: `TryTakeLoot`→bag→`DoItemsAcquiredEvents` (`QuestManagerEvents.cs:86-103`)→`OnItemGather`→`SetObjective` from bag (`QuestActObjItemGather.cs:70-78`); `RunAct` bag-evaluated (`:25-29`) |

- Non-owners: roam hunt leg (`BotRoamStepExecutor.cs:734-860`) owns
  opportunistic-wildlife loot+teardown (`:750-790`) but is suppressed while
  `QuestLegActive` (`:737`) — never owns the 251 corpse; GOAP owns nothing in
  production (unwired); bridge `loot` / `BotDriveBridge` are fixture-only (Q4
  `:121-122`).
- Teardown ordering (roam precedent `:750-790`): loot first, then
  `StopAutoAttack`, then target-clear+reset — stopping before looting risks
  the despawn race; never stopping leaves `IsAutoAttack` live (`CombatProposal`
  withdraws `already-live` while running, `:723-727`) [INFERENCE flagged]
  (`g7-decomposition.md:§I`).

## J. Proposed E2E Gates (fixture/START/PASS/first-zero per slice)

- Shared staging (all gates, `g7-decomposition.md:§J.4`): fresh bot, level 10,
  vitals ≥0.95, 251 pre-held at giver 3512, staged at live 3475 spawner
  (`teleportToNpc(3475)`+`PollNpcObjId`), ONE authoritative START snapshot
  (charId, quest state, target objId/template/HP, snap↔actor identity, flat
  distance, activity, CycleId, meat 4058, money, turnIns —
  `G6QuestCombatGateTests.cs:324-359`), then observe-only (exclusion scan for
  `Cast`/`Loot` hits like `G6QuestCombatGateTests.cs:549-551`).
- Perception: NO snapshot extension for G7a–c
  (`g7-decomposition.md:§J.1`): snapshot today is ids-only
  (`ActorObservation.cs:22-91` → `BotObservedContext`,
  `BotDecisionProposal.cs:67-103`); target alive/dead, corpse/lootable,
  template/distance, quest Status, despawn timers are MISSING from snapshot —
  use the blessed live-recheck pattern (resolve `GetNpc` at proposal time,
  `QuestBehavior.cs:697-703`, selector `:169-174`; contract
  `QuestObjectiveTargetSelector.cs:14-19`); bag 4058 EXISTS (`:123-125`,
  funnel `:222-224`); quest Status live-read (`QuestBehavior.cs:131`,
  precedent `QuestDecisionScenario.cs:113`); corpse expiry = spawner
  `DespawnTime` + 2 s post-loot (`LootingContainer.cs:305-317`; doodad guard
  precedent `GameplayActor.cs:1580-1584`). Additive `PinnedTargetSnapshot`+
  `Quest251Progress` fields deferred until G7d shows measured wake cost
  (S2/S9 pressure, `gap-observe-scale.md` §1.7; v2 precedent
  `ActorObservation.cs:15-20`).
- Randomness: data (§D) says 4058 ×1 guaranteed per holder-kill at base rates,
  so the decomposition doc's worst-case-probabilistic posture
  (`g7-decomposition.md:§J.2` — `LootGameData.cs:40-55`, `LootingContainer.cs:
  118-208`, Q4 `meatDelta>=1` at `Q4LiveHuntLootE2eTests.cs:147-155`) is
  superseded on rates; KEEP its gate strategy anyway: G7c proves
  loot→grant mechanics (Q4 conservation: granted==before−after, remaining==0,
  all bag deltas gains, op↔test double-read, `:145-161`), G7d proves
  acquisition→Ready over as many kills as demanded; bounded repeats (Q4
  `maxCasts`=60 at `:217`; spike `SustainMaxRounds`=30 at
  `AdventurerSpikeScenario.cs:181`; roam 30 s cap at `:748`;
  `NoProgressSkipRounds`=3 at `:152-156`); deterministic fixture (drop-rate
  override/seeded pack/synthetic events) REJECTED — `IKillCreditSeam` stays
  library-only (`LevelingLoopScenario.cs:429-435`).
- Diagnostics/taxonomy (`g7-decomposition.md:§J.3`): extend
  `QuestObjectiveTargetDiag` (`death=`+`loot=`+`credit=`) and
  `QuestObjectiveCombatDiag` (`:885-897`; add loopLive/pinned/dead/corpseId/
  lootable/containerBefore/granted/itemBefore-After/progressBefore-After +
  trace/CycleId), Info-level, `TruncateSweep` (`:921-922`); failures
  `COMBAT/*` (diag verbs `:685-735`; dispatch `:790-791,:827`), `DEATH/*`
  (target-dead-withdrawn expected / kill-timeout / loop-still-live-after-death),
  `LOOT/*` (owner-not-found / out-of-loot-range 200 f `:32` / empty-or-looted
  `:1741-1748` / grant-zero), `ITEM/*` (caller-delta-mismatch Q4 `:148-155` /
  foreign-take), `CREDIT/*` (objective-unchanged `:70-78` / not-ready-after-3
  `:25-29` / advance-refused `:670-673`), `HARNESS/*` (SETUP accept-251 /
  boar-unresolved / stage-range / start-snapshot, corpse-gone, CONFIG/
  bootstrap-off, leash-reset).

| Gate | START | PASS (all must hold) | First-zero |
|---|---|---|---|
| G7a kill-only | G6 end state (251 ACTIVE, live 3475, loop live, meat<3) | prey dead (`Hp<=0`/`GetNpc` null + `PollNpcObjId` gone); loop torn down (`IsAutoAttack` false, task null, target cleared — roam `:772-781` shape); funnel shows `dead` + Completed trace | meatΔ 0, moneyΔ 0, quest still Progress, turnIns 0 (anti-leak, `G6QuestCombatGateTests.cs:632-634`) |
| G7b corpse observed | Fresh unlooted corpse (G7a-chained or re-staged; START re-taken; `HARNESS/corpse-gone` if despawned) | quest-side detection names corpse (diag `corpse={objId} lootable=true`, test-side `containerBefore>=1` like Q4 `:148-150`); no loot dispatched yet; quest Progress | grant zeros, meat unchanged |
| G7c loot→4058 | Unlooted corpse + 251 ACTIVE + loot range | `Loot` Completed; Q4 conservation (`:147-161`); meat after ≥ before (dry `meatΔ==0` only with container proof, counted as G7d iteration); retry-after-success grants nothing (Q4 `:178-184`); status read-not-asserted | conservation holds (no `ITEM/*` violation) |
| G7d acquisition→credit | Bag 4058<3, 251 ACTIVE, live 3475 ground | bounded repeats until bag ≥3; objective==3 (`:77`); quest **Ready** (`:25-29`); relevance flips false (`:101-108`, combat withdraws) | turnIns 0 (turn-in is G8) |

## K. Risks/Unknowns (+ trace verdict)

- DPS/leash cap (G6 debt `g6-closeout-freeze.md:131`): boar leash-reset vs
  level-10 DPS may cap G7a at sustained-damage; spike-to-50 concession changes
  fixture not gate; trip `HARNESS/leash-reset` with HP-sawtooth evidence
  (`g7-decomposition.md:§K.1`).
- Corpse-despawn race: floor = spawner `DespawnTime` (value UNKNOWN, not read)
  + 300 s if non-empty / 2 s post-loot (`LootingContainer.cs:42,:47,:305-317`,
  `NpcSpawner.cs:742-777`); G7b/c budgets inside the observed window, first
  observation calibrates (`g7-decomposition.md:§K.2`; `g7-combat-death.md:
  §D.6` UNKNOWN).
- `OutOfCombatRecoveryModule` priority 85 preempting post-fight wakes — observe,
  never disable; diagnose via activity field (`g7-decomposition.md:§K.3`;
  `g6-closeout-freeze.md:131`).
- Tag/aggro shape: `Npc.DoDie` eligible-tag-team/in-range else killer-fallback
  (`Npc.cs:843-879,:1013-1021`); solo lane = fallback path (safe); second damage
  dealer risks `ITEM/foreign-take` — single-bot lane only
  (`g7-decomposition.md:§K.4`).
- Busy-reject (not loot-range): 200 f never binds at 3 m hold; live risk is
  single-writer `Rejected(StateTransition,busy)` (`GameplayActor.cs:46-48`,
  queue `:446-448`) if teardown collides with a Running leg — one dispatch per
  wake (`g7-decomposition.md:§K.5`).
- Audit retention under kill-loop volume: sink 10 k drop-oldest / 1 k flush
  (`PlayerBotAuditSink.cs:36-39,:71-98`), queue history 1024, trace API 100 —
  bounded repeats stay far below; per-tick full-observation logging prohibited
  (`g7-decomposition.md:§K.6`).
- Wrong-act trap: 251 is ItemGather not MonsterHunt — kill credit
  (`QuestManagerEvents.cs:169-203`) is IRRELEVANT; only bag count via
  `OnItemGather` (`:70-78`) moves it; never assert hunt counters for 251
  (`g7-decomposition.md:§K.7`).
- Data-vs-inference ledger (`g7-combat-death.md:§D.6`): [INFERENCE] respawned
  Npc gets new ObjId; 10M scale = guaranteed-at-base (loader normalizes
  `drop_rate<=1→10M`, `LootGameData.cs:40`); null-player (pet-kill) quest-filter
  edge. UNKNOWN: `DespawnTime` source, 3475 spawner coords, live server
  `LootRate`/`GoldLootMultiplier`, `CanAttack` faction detail.
- Human-trace verdict (`g7-human-trace-251.md:§1`): **NO human trace covering
  quest 251 exists in the repo** — every `251` hit outside code/docs is an
  elapsed-ms false positive; genuine refs are code/docs only
  (`Golden-Route-Solzreed.md` step 2, G3/G4 docs, m2b notes, Q4 tests).
  Structural cause: trace schema carries no quest/item/NPC-template IDs
  (`trace_parser.py`: only `{Packet,Opcode}` + `{SkillId,TargetObjId,X/Y/Z/
  Yaw/Range}`), so `quest_accept_and_turnin` cannot bind to 251/3512/4058/3475;
  corpus gap self-reported (`playertrace-coverage/summary.md` §7–8: quest and
  basic-combat families `[UNTRACED]`; `packet-coverage.csv` turn-in rows are
  stale — zero such records under `traces/`).
- Substitutes (`g7-human-trace-251.md:§§2-3`): ordering/timing grounding from
  human `quest_accept_and_turnin__Dingus__20260914_194206.jsonl` (22690
  records, ~157 s: accept bundle Started→Updated+echo same-ms→grant ≤70 ms;
  death burst BuffRemoved→`SCUnitDeathPacket`→Aggro→CombatCleared→
  TargetChanged same-ms; kill #3 death+`SCQuestContextUpdatedPacket` same tick
  vs non-objective kills death-alone; NO loot/turn-in packets anywhere) +
  combat_melee/ranged casts; plus [SUBSTITUTE]-only spike quest-250
  Target→Cast→Loot→Advance ×3 (`m7-adventurer-spike.jsonl`) and bot Q4 251
  hunt-kill→grant→retry-empty (`Q4LiveHuntLootE2eTests.cs:110-184`). Server
  code stays authoritative for legality/credit
  (`g7-human-trace-251.md:§§4-5`).

## L. Next Action (exactly one bounded implementation step)

- Implement the G7a kill-only gate fixture+test first (fresh bot, level 10,
  vitals ≥0.95, 251 pre-held at giver 3512, `teleportToNpc(3475)`+
  `PollNpcObjId`, START snapshot per §J, observe-only sustain of the G6 live
  loop to prey-dead with loop-teardown assertion and meat/money/credit/turn-in
  zeros held), and nothing else — no loot/credit/turn-in wiring, no snapshot
  extension, no drop-rate override.

G7 DISCOVERY COMPLETE
Combat-to-death path: engine-owned AutoAttack loop (TaskManager ticks) → HP-zero DoDie; request terminal means loop-started, death stops the loop next tick
Corpse/loot path: dead Npc IS the corpse (ObjId held to despawn); GenerateLoot at death; GameplayActor.Loot → OpenBag lootAll; expiry DespawnTime+300 s non-empty, +2 s once emptied
4058 acquisition path: 3475 → pack 4530 (sole source, row 10014) → 4058 ×1 guaranteed per holder-kill at base rates (10M/10M); 3 kills + 3 loots for the quest
Quest-credit path: bag acquire → OnItemGather (act id + item 4058) → recount SetObjective → Ready at ≥3; kill-3475 is credit-neutral (no hunt act in 251)
Recommended G7 split: four gates G7a kill / G7b corpse-observed / G7c loot→grant / G7d acquisition→Ready (ownership boundaries; single gate rejected except post-pass sweep)
First implementation step: G7a kill-only gate fixture+test (START snapshot, observe-only sustain to dead, teardown + zeros), nothing else
Trace verdict: no human 251 trace in repo (schema ID gap is structural); ordering/timing grounding from quest_accept_and_turnin + substitutes (spike-250, bot-Q4); server code authoritative for legality/credit
