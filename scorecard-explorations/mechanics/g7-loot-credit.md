# G7 Loot → Credit — Discovery (§E–G of G7 report, 2026-09-20, branch develop)

READ-ONLY discovery. Nothing modified, built, or run. G3–G6 frozen (untouched).
Mission: dead 3475 → canonical loot → 4058 acquired → `OnItemGather` → quest progress.
Canonical seam: `BotActionCommandQueue → GameplayActor → engine`.
Prior context: `g6-closeout-freeze.md` (end state: quest-owned AutoAttack loop live on a
damaging-but-alive 3475, priority 23); `capability-surface-matrix.md` (Loot verb row);
`g4-objective-credit.md` §B (credit authority, already verified — re-cited, not re-proven).

Headline: **the canonical dead-target→verb→handler→delta path exists end-to-end in
production code** (`GameplayActor.Loot` → `LootingContainer.OpenBag(lootAll:true)` →
`Bag.AcquireDefaultItem` → `OnAcquiredItem` → `DoItemsAcquiredEvents` → `OnItemGather`
→ inventory-recount `SetObjective`), **but no production bot owner drives it for quest
251**: the roam wildlife loop loots quest-blindly, `QuestBehavior` has zero loot/teardown
handling (explicit G7 gap), and the GOAP loot action is planner-registered yet never
scheduler-wired with its completion flag unwritten outside tests. Kill-3475 is
**credit-neutral for 251** (kill fanout carries NPC identity; the gather act matches
item identity only).

---

## §E. LOOT CAPABILITY — the canonical dead-target→verb→handler→delta path

### E.1 The verb: `GameplayActor.Loot` (production, canonical)

- Contract: `IGameplayActor.Loot(uint lootOwnerObjId, …)` —
  `AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs:196-202` ("Loots a corpse/bag owner
  through the real engine path (`LootingContainer.OpenBag` with lootAll — the exact call
  `CSLootOpenBagPacket` makes)… retries cannot duplicate loot"). Action enum
  `ActorActionType.Loot = 11` (`IGameplayActor.cs:947-948`).
- Queue seam: `BotActionKind.Loot = 7` dispatches straight through —
  `AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs:581-582`
  (`case BotActionKind.Loot: return (actor.Loot(spec.TargetId, key), null)`), mapped
  `BotActionKind.Loot → ActorActionType.Loot` (`:900`). Externally reachable:
  `POST /api/actors/loot` → queue → actor (matrix §A1 Loot row; MCP `loot` tool,
  bridge `drive loot` is actor-traversing-but-scripted, matrix §A3).
- Implementation — `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:1733-1762`:
  1. Preconditions (all `RejectedAction` refusals, fail-closed):
     - owner resolves via `Character.ParentWorld?.GetBaseUnit(lootOwnerObjId)` (`:1739`);
       unresolvable → `Rejected("loot owner … not found in world")` (`:1740-1741`).
     - loot-range gate: flat distance ≤ `LootingContainer.MaxLootingRange`
       (`:1742-1743`). **Note the scale: `MaxLootingRange = 200f`**
       (`AAEmu.Game/Models/Game/Items/Containers/LootingContainer.cs:32`) — loot range
       is effectively never the binding constraint next to a corpse; the 25 m
       `MaxInteractRange` (`GameplayActor.cs:1535`) does NOT apply to loot.
     - non-empty gate: `container.Items.Count <= 0` → `Rejected("nothing to loot …
       (empty or already looted)")` (`:1746-1748`).
  2. Engine handler: `container.OpenBag(Character, owner, lootAll: true)` (`:1756`) —
     "the exact call CSLootOpenBagPacket makes with lootAll=true" (`:1753-1755`).
  3. Inventory delta: `granted = before - container.Items.Count`; `Complete(request,
     granted, …)` (`:1758-1761`). **Result counts container ENTRIES removed, not item
     units** (a stacked entry of 2 grants 2 units with Result == 1 — callers must diff
     the bag; rig convention `GameplayActorLootGrantTests.cs:60-62`).
  4. No idempotency-by-content beyond the engine: unkeyed retry after success sees an
     empty container → `Rejected` ("already looted"); same-key retry rejects pre-flight
     via the shared ledger (`GameplayActorInteractLootTests.cs:179-210`).

### E.2 Corpse Interact / pickup variants (do NOT conflate)

- `Interact` / `InteractWith` are **doodad-only**: `Interact` resolves
  `ParentWorld?.GetDoodad(doodadObjId)` (house edge-case aside) and drives
  `doodad.Use(Character, skillId)` (`GameplayActor.cs:1540-1592`); a corpse objId is not
  a doodad → `Rejected("doodad … not found in world")` (`:1574`). NPC corpses NEVER
  become doodads (roam butcher-leg comment, `BotRoamStepExecutor.cs:908-911`).
- `InteractNpc` is ambient dialogue + opportunistic talk credit (`GameplayActor.cs:1154-1257`;
  skill-list packet + `DoTalkMadeEvents` fanout) — never a loot path. Actor-only (no
  queue kind), like `AutoAttack`/`StopAutoAttack`.
- `PackPickup` is trade-pack-only (`RecoverItem.Execute` with recover skill 11361;
  `IGameplayActor.cs:507-512`) — not a corpse path.
- **Canonical dead-target→verb→handler→delta path** (all production):
  `Unit.DoDie` → `LootingContainer.GenerateLoot(killer)` (`Unit.cs:532-533`) →
  `GameplayActor.Loot(objId)` (`GameplayActor.cs:1733-1762`, range + non-empty gates) →
  `LootingContainer.OpenBag(lootAll:true)` (`LootingContainer.cs:332-375`, snapshots
  entries, `TryTakeLoot` each) → per-entry `TryReserveLootItem` (atomic remove,
  `:709-721`) → `Bag.AcquireDefaultItem(Loot/LootAll, templateId, count, grade)`
  (`:670`) → `AddOrMoveExistingItem` → `Inventory.OnAcquiredItem`
  (`ItemContainer.cs:493-494` new-item; `:736` stack-top-up; mail-attachment take
  `:500`) → `QuestManager.DoItemsAcquiredEvents` (`Inventory.cs:924`) →
  `QuestActObjItemGather.OnItemGather` → recount-`SetObjective` (§G).

### E.3 Engine handler semantics (loot-all, resolution, corpse lifecycle)

- Loot-all: `lootAll:true` snapshots `Items.ToArray()` and takes every entry
  (`LootingContainer.cs:339-351`); per-entry failure (roll pending, bag full,
  quest-gated) leaves that entry via `RestoreLootItem` (`:724-730`) — partial grants
  possible, entries conserved not destroyed (full-bag test:
  `GameplayActorLootGrantTests.cs:227-231`).
- Corpse resolution: `GenerateLoot` is once-only (`AlreadyGenerated` guard, `:89-91`),
  rolls `ItemManager.GetLootPackIdByNpcId(templateId)` packs
  (`:112`, `:203-211`) — for 3475 this includes pack 4530 → 4058 (G4 §A.4). Loot rights:
  tag-team in-range members / tagger / killer-fallback (`:130-196`); quest-item gate
  `LootQuestId` requires the looter to hold that quest (`:415-422`) — 4058's template
  `LootQuestId` was NOT re-verified this pass (see §G.6 UNKNOWN); the seeded-mirror test
  documents the refusal shape (`GameplayActorLootGrantTests.cs:282-288`).
- Corpse lifecycle: death clears aggro/targets, `Spawner?.DoDespawn(this)`,
  `Ai?.GoToDead()` (`Npc.cs:1024-1031`); empty-container → `UpdateLootState` broadcasts
  `SCLootableStatePacket(hasItems:false)` and shortens the despawn timer
  (`LootingContainer.cs:294-303`, `PostLootMinimumDespawnTime = 2f`, `:47`).
  Loot-then-despawn ordering is engine-owned; the bot must loot while the corpse
  resolves (no production quest-side deadline exists — G7 gap).

---

## §F. BOT POST-KILL LOGIC — owners and classification

Scale: `PRODUCTION` = scheduler-wired live-actor logic; `REUSABLE` = production-grade
library with no production scheduler wiring; `SCENARIO-ONLY` = reachable only via
`RunAsScenario ← BotScenarioRunner ← BotDriveBridge`; `FIXTURE-ONLY` = test rigs;
`DUPLICATE` = second spelling of an owned behavior; `UNKNOWN` = not found / unverified.

### F.1 `BotRoamStepExecutor` wildlife loop — PRODUCTION (quest-blind)

- Post-kill detection: per-wake `targetUnit == null || Hp <= 0 ||
  !IsAttackableWildlife || 30 s engagement cap` (`BotRoamStepExecutor.cs:745-748`).
- Post-kill handling (`:750-790`): if `Hp <= 0` → `actor.Loot(objId)` on the SAME live
  actor, three terminal outcomes distinguished by `(Completed, granted>0)` /
  `(Completed, 0)` / non-Completed (`:754-769`, log-only); then `StopAutoAttack`
  (`:772-775`), direct `CurrentTarget = null` + `SCTargetChangedPacket` (`:777-781`,
  the G4-D2 seam bypass — writes `Character.CurrentTarget` instead of
  `actor.SetTarget(0)`), clear `TargetNpcObjId`, preempt stale Move leg.
- Hunt follow-up: target cleared → next wake re-scans (`NearbyNpcProvider ??
  WorldManager.GetAround<Npc>` @ 45 m, `HuntPerceptionRadius`, `:864-867`) and engages
  the nearest attackable (`:862-901`) — nearest-first, **no template filter, no quest
  check, no inventory check** (no `GetItemsCount(4058)` anywhere in this file —
  verified by grep). Combat teardown shape (30 s cap + `StopAutoAttack` + target-clear)
  is the production precedent G6 §(g) names.
- Second combat-teardown site at `:2472-2476` (PvP leg: `StopAutoAttack` + target
  clear) — same shape, different trigger. Classification of the pair: PRODUCTION +
  DUPLICATE (two teardown spellings; quest-side teardown still missing).

### F.2 `QuestBehavior` (production quest leg) — PRODUCTION with an explicit loot gap

- Owns Target (G4), pursuit Move/Stop (G5), combat AutoAttack proposal at priority 23
  (`QuestBehavior.cs:677-748`; `ObjectiveCombatPriority = 23`,
  `QuestDecisionScenario.cs:65`). Dispatch arms AutoAttack ONLY
  (`QuestBehavior.cs:816-821`, "No Cast, no rotation, no Loot, no credit — those are
  G7").
- Grep over `QuestBehavior.cs` for `Loot|corpse|StopAutoAttack|TargetDead|HasLooted|
  Teardown` hits ONLY `target-dead` as a validation *withdrawal* reason
  (`:570-571`, `:707-711`: dead target → proposal null, "verb=none") plus the "no Loot"
  comments (`:540`, `:675`, `:819-820`). **Zero post-kill handling, zero combat
  teardown, zero corpse detection/selection, zero inventory checks.** The quest leg's
  post-kill behavior today is: combat proposal withdraws on death; the roam loop's
  quest-blind loot (F.1) is the only production loot that can fire.
- `StepQuestLeg` (roam→quest bridge, `:1257-1311`) ticks the quest runtime on the live
  actor and records `QuestLegActive`; it adds no loot/teardown of its own.
- `OutOfCombatRecoveryModule` (priority 85, `OutOfCombatRecoveryModule.cs:61`) may
  preempt post-fight wakes (G6 §(g) observation, not re-verified beyond file presence).

### F.3 GOAP combat/loot — REUSABLE (registered, planner-covered, never scheduled)

- `LootCorpseAction` (`Goap/Actions/CombatActions.cs:130-165`): preconditions
  `TargetDead + HasCorpseToLoot`, effects clear corpse/in-combat/target + set
  `HasLooted`; `CreateActorRequest` → `effectiveActor.Loot(CurrentTarget.ObjId)`
  (`:144-149`) — the canonical verb. `ExecuteCombatComboAction` (`:83-125`, Cast +
  `TargetDead` effect) is its combat half.
- State: `BotWorldStateProvider` sets `TargetDead` from `CurrentTarget is Unit,
  IsDead` and `HasCorpseToLoot` iff `!Memory.HasLootedCurrentTarget`
  (`BotWorldStateProvider.cs:216-221`); `HasLooted` iff the memory flag
  (`:224-225`).
- **The memory flag has no production writer**: repo-wide grep for `HasLooted`
  finds the declaration (`BotMemory.cs:43`), provider reads, action effects/status,
  goal conditions (`GoalArbitrator.cs:23,32`; `GoapActionLibraryTests.cs:80`) — the
  ONLY write is a test manual-set (`GoapScenarioIntegrationTests.cs:344`). So a live
  GOAP loot can never raise `HasLooted`; `EvaluateStatus` still terminates via the
  `!HasCorpseToLoot` clause once the target clears (`CombatActions.cs:157`) — [INFERENCE]
  graceful by accident, not by design.
- Scheduling: `GoapBotStepExecutor` implements `IBotStepExecutor` but has ZERO
  production construction sites (no `new GoapBotStepExecutor` repo-wide); production
  binding is `BotGoalArbiterStepExecutor → BotRoamStepExecutor` (`Program.cs:324-332`).
  The only production-constructed runner is the homestead-domain runner inside
  `StepHomesteadLeg` (`BotRoamStepExecutor.cs:1501-1504`). Combat-domain actions are
  registered (`GoapActionRegistry.cs:52-55`) and planner-tested, but no production wake
  ever plans them. Classification: REUSABLE, not PRODUCTION. (G6 freeze's "GOAP acquire
  stays a dead stub, the tree advises but never dispatches" corroborates.)

### F.4 Scenario / fixture loot — SCENARIO-ONLY / FIXTURE-ONLY

- `LevelingLoopScenario.HuntLeg` — SCENARIO-ONLY: `SetTarget → Evaluate → AutoAttack →
  cast rotation → StopAutoAttack → actor.Loot (Rejected tolerated) → EquipUpgrades`
  (`LevelingLoopScenario.cs:2694-2821`); corpse detection = `target.Hp <= 0` mid-chain
  (`:2755`, `:2767`); follow-up = exclude + reselect nearest matching
  (`:2814-2815`). Only reachable via `RunAsScenario ← BotScenarioRunner ←
  BotDriveBridge` (G4 §C.1). Level/ability legs share the shape (`:1541`, `:1735`).
- `AdventurerSpikeScenario` — SCENARIO-ONLY: kill ×N → `Loot` each corpse (optional
  via `LootOptional`, `:561-580`); `PrepareLootCorpse` is a live no-op, rig-seeded in
  fixtures (`:280-284`, `:1104-1107`). `PartySpikeScenario.AttemptLoot` default false
  (`PartySpikeScenario.cs:138-141`, `:461-467`) — SCENARIO-ONLY.
- `GameplayActor*Loot*Tests`, `GameplayActorTestRig` (seeded `LootingContainer`,
  deterministic pack mirrors incl. 4058/pack-4530 mirror,
  `GameplayActorLootGrantTests.cs:27-30`, `:153-157`), `QuestObjectiveCombatTests`
  (re-seeds 251 gather act + 4530→4058 link when absent, `:232-334`) — FIXTURE-ONLY.
- `QuestObjectiveTargetSelector` — PRODUCTION (G4): relevance = 251-active + 4058<3
  (`:101-108`); constants `Quest251/PreyTemplate3475/PreyPack4530/PreyItem4058/
  PreyItemRequired=3/ExpectedGatherActId=10473` (`:37-53`); stops at selection.
- DUPLICATE inventory: nearest-hostile selection exists 3× (roam `:842-855`,
  `SelectHuntTarget`, selector funnel); teardown 2× (F.1); combat initiation 2× + test
  `CombatExecutor` (G4 §C.2 D1–D4, still current — re-grepped, no consolidation found).
- UNKNOWN: any production 4058-inventory-gated hunt follow-up (none found); production
  corpse-selection policy for 251 (none — relevance ends at selection, loot ends at
  the roam loop).

### F.5 Ownership table (post-kill slice)

| Responsibility | Owner | Class |
|---|---|---|
| corpse detection (Hp≤0 + validity + 30 s cap) | `BotRoamStepExecutor` `:745-748` | PRODUCTION |
| post-kill loot dispatch (live actor) | `BotRoamStepExecutor` `:754` | PRODUCTION (quest-blind) |
| combat teardown (StopAutoAttack + target-clear) | `BotRoamStepExecutor` `:772-784`, `:2472-2476` | PRODUCTION + DUPLICATE |
| hunt follow-up (re-scan nearest attackable) | `BotRoamStepExecutor` `:862-901` | PRODUCTION (no quest/inventory gate) |
| quest combat proposal + dead-target withdrawal | `QuestBehavior` `:677-748`, `:707-711` | PRODUCTION (loot gap explicit) |
| quest-side loot / teardown / corpse selection | none | UNKNOWN (G7 work) |
| GOAP TargetDead/HasCorpseToLoot/HasLooted state | `BotWorldStateProvider` `:216-225` | REUSABLE |
| GOAP corpse loot action | `LootCorpseAction` | REUSABLE (unwired; flag never set live) |
| scenario hunt loot + equip upgrades | `LevelingLoopScenario` `:2811-2821` | SCENARIO-ONLY |
| spike/party loot | `AdventurerSpikeScenario`, `PartySpikeScenario` | SCENARIO-ONLY |
| seeded-container loot/credit proofs | `GameplayActor*Tests`, rigs | FIXTURE-ONLY |

---

## §G. CREDIT AUTHORITY + KILL-vs-CREDIT VERDICT

### G.1 The exact 4058-enters-inventory → progress chain (the credit proof path)

Every link is production engine code; the proof MUST exercise exactly this:

1. **Triggering event**: `Inventory.OnAcquiredItem(item, count)` fires ONLY when
   `count > 0 && item != null` (`Inventory.cs:915-925`) → unconditional
   `QuestManager.DoItemsAcquiredEvents(Owner, item.TemplateId, item.Count)` (`:924`).
   Both container paths funnel here: new-item `AddOrMoveExistingItem`
   (`ItemContainer.cs:493-494`, incl. mail-attachment take `:500`) and stack top-up
   in `AcquireDefaultItemEx` (`:736`). Loot's `Bag.AcquireDefaultItem(Loot/LootAll,…)`
   (`LootingContainer.cs:670`) therefore always emits per unit batch.
2. **Fanout**: `DoItemsAcquiredEvents` fires `OnItemGather{ItemId=templateId,
   Count=count}` unconditionally + `OnItemGroupGather` per containing group
   (`QuestManagerEvents.cs:86-103`). No party loop, no range check, no quest check —
   per-owner broadcast. (`QuestId` field on the args is NOT set by this producer;
   matching ignores it — see 3.)
3. **Matching** (`QuestActObjItemGather.OnItemGather`,
   `QuestActObjItemGather.cs:70-78`): `questAct.Id == ActId && args.ItemId == ItemId
   (4058)` → `SetObjective(bag count of 4058)`. Identity = **act id + item template
   only**. No objId/NPC/range/party/killer check. No `Count > 0` guard on this act
   (contrast `QuestActEtcItemObtain`, which requires `e.Count > 0` —
   `QuestActEtcItemObtain.cs:46`); negative-count consume events also hit the handler
   but harmlessly — see G.2.
4. **Progress mutation**: recount, never increment —
   `SetObjective(owner.Inventory.GetItemsCount(4058))` (`:77`); same at
   `InitializeAction` (`:35`) and `RunAct` (`:28`). Capped:
   `SetObjective` clamps to `MaxObjective()` (`QuestActTemplate.cs:119-131`; 251:
   `Count=3`, no score → max 3, `:105-112`). On change → `quest.RequestEvaluation()`
   (`:129`; flag + `MarkDirty`, `Quest.cs:480-485`); step machine advances
   Progress→Ready when `RunAct` reads ≥3 (`QuestActObjItemGather.cs:25-30`, G4 §A.3).
5. **Subscription = active-quest requirement**: handler armed in `InitializeAction`
   (`:38`, seeded from live bag) and removed in `FinalizeAction` (`:46`) — per
   quest-instance event subscription. Credit lands only while 251's Progress step is
   the live step; no active quest → no subscriber → no progress. (Initialize also
   recounts, so pre-held 4058 counts at accept-time — no loot required if the bag
   already holds 3.)

### G.2 Bypass / edge semantics (all verified in code)

- **Normal-acquisition-only? NO — any acquisition path credits.** The handler matches
  the broadcast, not the source: loot, `Buy` (`GameplayActor.cs:2890`,
  `AcquireDefaultItem(StoreBuy,…)`), stocked/preseeded items (`StockInventory →
  AcquireDefaultItem`, `PlayerBotController.cs:150-155`; M1M2 preseed doc
  `M1M2ReplayScenario.cs:31-35`), mail-attachment takes (`ItemContainer.cs:498-501`),
  traded items — all funnel through `OnAcquiredItem`. The G7 credit proof MUST use the
  loot path (E.2 chain), not a stock-then-advance shortcut.
- **Mail/use/direct-mutation bypass?** Mail *take* credits (fires `OnAcquiredItem`);
  mail *deposit* consumes (`OnConsumedItem`, `:503-507`). `UseItem` (SkillItem branch)
  consumes charges → `OnConsumedItem` → `DoItemsConsumedEvents` → `DoItemsAcquiredEvents`
  with negative count (`QuestManagerEvents.cs:78`) → handler recounts (decrement
  reflected, never a false increment — recount shape is self-healing). Raw field/DB
  writes (`drive setLevel`-style setup, `EnsureFreshBotRow` deletes) bypass events
  entirely — never credit, never part of a proof. Synthetic `controller.GatherItem`
  event-fire (`PlayerBotController.cs:76-77`) mimics credit without acquisition —
  ENGINE-injection, must never be cited as loot credit (matrix §A3/B).
- **Loot triggers? YES** — via `AcquireDefaultItem` (G.1.1). Coins (`Item.Coins`) and
  trade-pack auto-equip branches do NOT touch the bag (`LootingContainer.cs:625-664`)
  and emit no acquire event — irrelevant for 4058 (a bag item).
- **Stack increments (one/many)? Recount — batch-size independent.** Each
  `OnAcquiredItem` call sets the objective to the live bag total. Looting 3×1 across
  three corpses (the 3475 case: pack row 10014 is ×1–1, G4 §A.4) walks 0→1→2→3; a
  single ×3 grant would jump 0→3. No under/over-count from batching.
- **1:1 quantity? YES for 251**: one 4058 unit ≡ +1 progress, capped at 3. (General
  `MaxObjective` overachieve/`LetItDone ×1.5` math at `QuestActTemplate.cs:105-112`
  does not widen 251: `Score` 0 → max = `Count` = 3.)
- **Caps**: objective clamped at 3 even if the bag holds more (`:125`); cleanup at
  turn-in/drop consumes `min(objective, max)` (`QuestActObjItemGather.cs:56,66`).
- **Active-quest requirement**: §G.1.5. **Party effects**: none on the item path —
  no TeamShare/fanout in `DoItemsAcquiredEvents` (contrast the kill path's TagShare,
  `Npc.cs:991-1022`, which fans `OnMonsterHunt` only). Each member credits from its
  own bag via its own loot takes (party loot-rule branches — FreeForAll/RotateWinner/
  LootMaster/Public — gate *who receives the item*, `LootingContainer.cs:442-483`;
  whoever's bag gains 4058 fires their own credit).

### G.3 KILL-vs-CREDIT VERDICT: kill-3475 is credit-neutral for 251

- Kill fanout (`DoOnMonsterHuntEvents`, `QuestManagerEvents.cs:169-213`) emits
  `OnMonsterHunt{NpcId=template 3475, Count=1}` (`:177-182`) + group/zone/kill-accept
  fanouts. 251 subscribes to NONE of these: its only Progress act is the ItemGather
  (act 10473/detail 616, G4 §A.2); no hunt/group-hunt/zone-kill/aggro acts; 3475 is not
  a kill-acceptor for 251 (`GetQuestIdsFromKillAcceptNpc`, `:207-212` — 251 accepts
  from NPC 3512, G4 §A.2).
- The gather handler's match keys (`questAct.Id`, `args.ItemId==4058`) are disjoint
  from the kill event's keys (`NpcId==3475`, `ZoneGroupId`) — no handler exists that
  maps one to the other. Death produces the *opportunity* (corpse + `GenerateLoot`)
  and nothing else.
- Corroboration: G6 gate held meat 0→0 with a damaging loop and quest still Progress
  (`g6-closeout-freeze.md` §(a)); `QuestActObjMonsterHunt.OnMonsterHunt` (the
  template-matching sibling) is a different act class 251 does not instantiate.
- **Verdict: KILL-NEUTRAL confirmed.** Death≠progress unless the loot→acquire→
  `OnItemGather` chain completes. A G7 proof must therefore gate kill, loot, and
  credit as three separate bounded gates (G6 handoff), and the credit gate MUST show
  the E.2→G.1 chain (bag delta + `OnItemGather` + objective 0→…→3 → Ready), never
  kill-count-as-progress.

### G.4 UNKNOWNs (honest, do not block G7 gates)

- 4058 template `LootQuestId` gating for the live (non-seeded) corpse
  (`LootingContainer.cs:415-422` gate exists; template row not re-queried this pass).
- `drop_rate 10000000` = guaranteed roll (scale convention, G4 §A.5 inference).
- Live 3475 pack contents beyond row 10014 (other packs 1616/8055 content unexamined).
- Whether `Spawner.DoDespawn` can yank a 3475 corpse before a 200 m-range loot
  arrives (no quest-side deadline handling exists either way).
- Ranged-role `TooCloseRange` / leash / DPS fixture bounds (G6 §(g) debt, untouched).

## G7c release-wake follow-up — 2026-09-23 (Codex takeover)

**STATUS UPDATE (same day, later):** both bounded changes below landed and the
gate was run twice. The wake-issuing change works (a release wake is issued and
logged), but the quest leg produced no `validate=ok` arm afterwards
(`FAIL-BEHAVIOR/CORPSE-UNOBSERVED`, corpse=7445,
`g7c-loot-report.20260923T133239002Z.json`); the newest attempt failed earlier in
staging (`UNKNOWN / HARNESS/fixture-overshoot`, `g7c-loot-report.json`). The
gate's failure arm was also relaxed to CHAINING-INTENDED (per-ObjId
`CorpseLootedTwice`, quest held-not-past-Ready) and its START block now tolerates
a `validate=loop-live` provisional arm while it waits bounded for the release.
**G7c is therefore still the one unproven leg of the quest-251 chain.** Read
`arc-utilization-index-2026-09-23.md` §c.10 + §d items 1–2, plus the session
notes `MUSE-g7c-start.md` / `g7c-intent.md` / `g7c-relax.md` / `g7c-run2.md`,
before touching this gate.

User confirmed Muse finished and authorized shared-file changes. Parent: existing
G7c quest-owned loot-grant gate, not a new quest or milestone. Baseline HEAD
`6c3efe25ecb68998544eb3f75699bc1674c7eef1` + existing dirty actor/quest/gate work.
Latest prior report `/root/aaemu-e2e-q0/logs/g7c-loot-report.20260923T085743685Z.json`
failed CORPSE/unobserved after authoritative death; provisional loop-live evidence
was present but the START release wait only slept.

Bounded change: issue exactly one existing `quest wake` inside the existing 30-second
START release window, then scan the same corpse's validate=ok evidence. No direct
loot/cast/kill or injected credit. Transport failure becomes HARNESS/release-wake,
not a behavior failure. Also replace the gate's foreign-WebApi-process termination
with refusal: a test cannot stop another lane to obtain exclusive routing.

Acceptance: same frozen corpse recognition → one ordinary Loot → conservation
contract. Reuse the adopted q0 lane, fresh test-owned character; no rebuild/reset.
Observed listening game/login processes belong to q0. Owner/verifier: Codex / pending
independent review. Fixture/bypass scope remains the existing gate's disclosed setup.
No gameplay implementation, deployment or H claim. Stop after the named gate verdict;
Wave A Buy/Harvest is the next proof-design review, not automatically proved by G7c.

Build: `dotnet build AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj -c Release --no-restore`
succeeded, 458 warnings / 0 errors. Initial tree-filter invocation was rejected by
the xUnit runner: 0 tests; not a gate result. Correct filter is
`--filter-method AAEmu.IntegrationTests.E2e.G7cLootGrantGateTests.QuestLegLootsCorpseOnceWithConservingGrant`.
Run log: `/tmp/aaemu-g7c-wake-test-filtered.log`; result pending at this entry.
Environment: Linux x64/.NET 10, `E2E_ROOT=/root/aaemu-e2e-q0`, host 127.0.0.1,
login/game/stream/bridge/internal/WebApi/DB ports 2237/2239/2250/2260/2234/1280/3311.
Command: `dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --configuration Release --no-build --filter-method AAEmu.IntegrationTests.E2e.G7cLootGrantGateTests.QuestLegLootsCorpseOnceWithConservingGrant`
with those E2E environment overrides. Unit suite/ScriptCompiler/downstream MCP
smokes are not part of this targeted command; no new full-gate green claimed.
