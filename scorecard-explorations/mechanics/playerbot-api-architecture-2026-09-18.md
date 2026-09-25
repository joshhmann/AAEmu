> Provenance: workstream API-AUDIT investigation-only, 2026-09-18, HEAD d0bea9e6a011d97d60157718996cfea7013ea7af.
> Tracked copy: scorecard-explorations/mechanics/playerbot-api-architecture-2026-09-18.md (authoritative; /tmp original ephemeral).
> Body below is byte-identical to the /tmp source.

# PlayerBot API / E2E Control Layer — Architecture Audit and Design

Workstream **API-AUDIT**. Status: **INVESTIGATION / DESIGN ONLY.** No production code was
modified, no refactor performed, no Needle integration proposed, no commit, no push.
Date: 2026-09-18. Repo: `/root/aaemu-dev` @ `d0bea9e6a` (branch `develop`, dirty: 14 tracked
modifications + 2 untracked wave-1 docs — all record/dashboard surfaces, none bot code).

Method: every claim below is traced to `path:line` at that revision from direct source
reading. Where a claim is an inference rather than an observation it is marked
`[INFERENCE]`. Where evidence could not be established it is marked `UNKNOWN` with what
was tried. Prior artifacts (`/tmp/actor_audit.md`, `/tmp/scenario_conversion.md`,
`/tmp/e1_inventory.md`, `/tmp/step3..step8_inventory.md`, the two tracked
`scorecard-explorations/mechanics/wave1-*.md` dossiers) were read first and **verified**
against source rather than re-derived; every one of their claims reused here was
re-checked, and the checks that failed are recorded in §5 (Problems).

---

## 1. Executive summary

AAEmu already has a **three-layer player-bot API**, but only two of the three layers
exist as *designed* layers, and the boundary between them is not drawn where the code
actually lives.

1. **A gameplay capability surface already exists and is real.**
   `IGameplayActor` (1104 lines, **55 action kinds**, `IGameplayActor.cs:889-1062`)
   implemented once by `GameplayActor` (4708 lines). Its 63 members are validated
   requests that walk `Requested → Accepted → Running → Completed | Rejected |
   Interrupted | TimedOut` (`ActorRequest.cs:123-205`) and call **ordinary engine
   services** (`Character.UseSkill`, `QuestManager.DoReportEvents`,
   `HousingManager.Build`, `SpecialtyManager.SellSpecialty`, `TeamManager.AskToJoin`,
   `SlaveManager.BindSlave`, `Doodad.Use`, `Character.Craft.Craft`, …). It is
   single-writer, idempotency-keyed, execution-boundary-pinned and trace-emitting.

2. **A test/control surface already exists too, and it is scattered across three
   transports.** ~90 op labels on the loopback JSON/TCP `BotDriveBridge`
   (`BotDriveBridge.cs:255-297`), 44 REST routes on `/api/actors/*`
   (`BotActionController.cs`), 5 on `/api/bots*` (`BotControlController.cs`), plus 11
   `/bot` console subcommand files. These are **not** a designed layer: they are three
   frontends over three different cores (`PlayerBotController`, `BotActionCommandQueue`,
   `BotAdminService`) with overlapping, non-equivalent semantics.

3. **The actual problem is not "no layers" — it is that the *test* layer can drive
   gameplay, and the *gameplay* layer has no notion of intent.** `BotDriveBridge` op
   `drive/kill` fires `Character.Events.OnMonsterHunt` directly
   (`BotDriveBridge.cs:727-729` → `PlayerBotController.cs:67-68` → `UnitEvents`), i.e.
   the harness *performs the mob kill* and then asserts the quest advanced. Twenty such
   bridge ops call `PlayerBotController` event injection
   (`BotDriveBridge.cs` — 20 call sites of `controller.{KillNpc,KillNpcGroup,GatherItem,
   UseItem,TalkToNpc,InteractWithDoodad,EnterSphere,ExpressEmotion,LevelUp,AggroNpc,
   ZoneKill,Cinema*,StockInventory,Advance,AcceptQuest}`). Meanwhile eleven real engine
   events are also *reachable* through the capability layer's own verbs, so the two paths
   coexist and produce indistinguishable-looking green evidence.

**Recommended boundary (Phase 4 answer, detailed in §6/§7):** **keep them separate, but
invert the current relationship.** Define a **Gameplay Capability API** = the existing
`IGameplayActor` surface (minus three test-only/synthetic members) and a **Test/Control
API** = spawn/remove/assign/observe/wait/collect-trace + explicitly-labelled privileged
fixture ops, and make the Test/Control API **unable to perform a gameplay step that the
bot's own behaviour cannot perform**. Concretely: no Test API verb may inject a
`UnitEvents` progress event, no Test API verb may synthesise a success, and every
"setup" verb must be marked as setup in the artifact. The 14 privileged fixture ops and
the 20 event-injection ops are the concrete violations.

**What must NOT belong to the Gameplay Capability API** (headline answer, full list §6.4):
fixture grants (`drive/stock`, `drive/setLevel`, `auction/rig`, `mail/rig`,
`homestead/kit`, `farm/rig`, `housing/rig`), positional staging (`drive/teleportToNpc`,
`homestead/move`, `farm/place`), event injection (`drive/kill`, `drive/gather`,
`drive/talk`, `drive/interact`, `drive/enterSphere`, `drive/cinemaStarted/Ended`,
`drive/aggro`, `drive/zoneKill`, `drive/levelUp`, `drive/express`), the `*Override`
memory layer (`BotMemory.cs:84-136`), and the test-only route seam
`GameplayActor.NavigateRoutedForTest` (`GameplayActor.cs:429`).

---

## 2. Current map — who owns / ticks / decides / executes a bot

### 2.1 The runtime object

| Concern | Owner | Evidence |
|---|---|---|
| Bot identity + lifecycle state | `PlayerBotRuntime` (Character + `State` + `Owner`) | `IPlayerBotManager.cs:36-101` |
| Registry / spawn / activate / deactivate / diagnostics | `IPlayerBotManager` / `PlayerBotManager` | `IPlayerBotManager.cs:20-56`; `PlayerBotManager.cs:16-85` |
| Embodiment into the world | `IPlayerBotLifecycleService` → `PlayerBotLifecycleAdapter` → `ICharacterLifecycleService.ActivateHeadless` + `character.Spawn()` | `IPlayerBotLifecycleService.cs:24-35`; `PlayerBotLifecycleAdapter.cs:47-70` (Spawn at `:60`) |
| Fidelity authority (Dormant/Reduced/Full) | `IPopulationDirector` | `IPopulationDirector.cs:6-27` (not the scheduler, per `IPlayerBotManager.cs:9-12`) |
| Due-time scheduling / wake | `IPlayerBotScheduler` / `PlayerBotScheduler` | `IPlayerBotScheduler.cs:27-70`; `PlayerBotScheduler.cs:38-564` |

A bot is **an ordinary `Character` row** driven headlessly — not a parallel entity
(`PlayerBotRuntime.cs:26-33`; `AGENTS.md` rule #9 referenced in-file). Two creation paths
exist and are *not* equivalent:

- **Production:** `HeadlessSession.Provision(...)` — real managed account + real character
  rows, embodied through the lifecycle service (`HeadlessSession.cs:145-147`, `:377`,
  doc `:90-94`).
- **Fixture only:** `HeadlessSession.Create(...)` — DB-row-less synthetic world, used by
  the unit rig (`HeadlessSession.cs:492-516`, doc `:33-36` explicitly forbids it as the
  citizen path).
- **E2E networked:** `BotNetworkSession.ConnectAsync(...)` — full real login
  (auth → world list → cookie → select → spawn → in-game), "NO auth bypass"
  (`BotNetworkSession.cs:29-33`, `:66-79`, select/spawn `:160-192`).

### 2.2 Who ticks

```
TickManager.OnTick (game-loop thread)
   └─ PlayerBotScheduler.TickDrain            (PlayerBotScheduler.cs:134-136, useAsync:false)
        └─ DrainTickQueue → ExecuteStepOnExecutionBoundary   (:362-457)
             ├─ ExecutionBoundary.AssertOnExecutionThread    (:96-114)
             ├─ registry consumption: runtime must be Active  (:379-385)
             ├─ death watch (resurrect)                       (:386-392)
             └─ _executor.StepAsync(runtime).GetResult()      (:394-408)
                  └─ BotGoalArbiterStepExecutor.StepAsync     (BotGoalArbiterStepExecutor.cs:31-40)
                       ├─ arbiter.Arbitrate(bot, gameHour)    (one pass per wake)
                       └─ inner.StepAsync → BotRoamStepExecutor.StepAsync (BotRoamStepExecutor.cs:484)
                            └─ actor.Tick(elapsed)            (BotRoamStepExecutor.cs:974 → GameplayActor.Tick :4100+)
```

Key facts verified:

- Workers **do not execute** steps; they only marshal (`WorkerLoopAsync`,
  `PlayerBotScheduler.cs:344-352`; the class doc `:19-30` states the rule).
- The scheduler guarantees **at most one in-flight step per bot** (lease,
  `IPlayerBotScheduler.cs:48-49`; `_leases` `PlayerBotScheduler.cs:52`).
- The actor's `Tick` is driven by the **step executor**, not by the scheduler
  (`BotRoamStepExecutor.cs:974`). There are exactly two production actor tickers:
  `BotRoamStepExecutor` (DI-wired, `Program.cs:269-288`) and the unwired
  `GameplayActorStepExecutor` (`GameplayActorStepExecutor.cs:30-80`, no DI reference —
  only its unit test `GameplayActorStepExecutorTests.cs:17`).
- `GoapBotStepExecutor` (`Goap/GoapBotStepExecutor.cs:15`) has **zero callers** in
  `AAEmu.Game`, `AAEmu.UnitTests`, `AAEmu.IntegrationTests`.
- `UnwiredBotStepExecutor` (`UnwiredBotStepExecutor.cs:15`) and
  `UnwiredPlayerBotLifecycleService.cs:17` are historical fail-closed defaults, no longer
  DI-wired (`IBotStepExecutor.cs:10-11`; `IPlayerBotLifecycleService.cs:18-21`).
- The scheduler is **registered but not auto-started** (`Program.cs:342-346`); `Start()`
  is called by `BotPresenceCoordinator` (`:357`, `:452`), `BotAdminService`
  (`:461`, `:475`, `:488`, `:687`), `PopulationDirectorBootstrap` (`:980`) and the E2E
  bridge's `ArmSoakScheduler` (`BotDriveBridge.cs:130`).

### 2.3 Who decides

Two decision systems coexist:

| System | Shape | Entry | Arbitration |
|---|---|---|---|
| **Activity arbitration** (G3-B3) | module list → ONE activity name per bot per wake | `BotGoalArbiterStepExecutor.StepAsync` → `IBotGoalArbiter.Arbitrate` | `BotGoalArbiter.cs:128` (`Arbitrate(PlayerBotRuntime, float gameHour)`); modules implement `IBotActivityModule.CanActivate/Activate` (`IBotActivityModule.cs:30-53`); priority wins, ties by registration order (`:24-28`) |
| **GOAP planning** (Wave One 4/5) | observe → goal → A* plan → action-by-action execution | `GoapPlanRunner.Tick(bot, actor, context)` | `GoapPlanRunner.cs:86-183` (sense `:95`, arbitrate `:98`, interrupt watchdog, action evaluation `:170+`); `GoapPlanner`, `PlanTemplateCache`, `GoalArbitrator` |

Leg selection inside the roam executor is a **hard-coded if-chain**, not the arbiter:

- party auto-accept + follow/assist — `BotRoamStepExecutor.cs:506-596` (unconditional, no
  config gate; an idle bot follows/assists its leader on its own wake)
- conflict/PvP — `:598-608`, gated on activity prefix `conflict.` (`:606`)
- `needs.*` — `:610-637` → `StepNeedsFarmLeg` (`:1221`)
- `quest.*` — `:639-655` → `StepQuestLeg` (`:1159`) → `QuestDecisionScenario.Run`
- `homestead.*` — `:657-666` → `StepHomesteadLeg` (`:1188`) → `GoapPlanRunner.Tick`
- wildlife hunt / butcher / route legs — `:668-973`

The arbiter holds **one** activity per wake (`IBotActivityModule.cs:24-28`), so at most
one of `needs.`/`quest.`/`homestead.` fires.

Nine `IBotActivityModule` implementations are registered (`Program.cs:304-320`):
`SchedulePhaseActivityModule`, `OutOfCombatRecoveryModule`, `PresenceRoamActivityModule`,
`ConflictJoinActivityModule`, `FishingContestActivityModule`, `HomesteadActivityModule`,
`QuestBootstrapActivityModule`, `NeedsFarmActivityModule`, `IdleActivityModule`.

### 2.4 Who executes

`GameplayActor` — one instance per bot character, **reused, never a second instance**:

- created lazily by the executor's `ActorFactory` (`BotRoamStepExecutor.cs:90` default;
  `BotRoamStepExecutor.cs:358-370 GetOrCreateActor`), which is also what the control plane
  uses (`BotActionCommandQueue.cs:430`) — the doc at `BotRoamStepExecutor.cs:352-357`
  states the "SAME actor the scheduler ticks" contract explicitly.
- every action begins `ExecutionBoundary.AssertOnExecutionThread(...)`
  (e.g. `GameplayActor.cs:299`, `:314`, `:507`, `:532`).
- every terminal transition goes through one place: `GameplayActor.Finish`
  (`GameplayActor.cs:4640-4660`) which writes the effect ledger and appends one immutable
  `ActorAuditRecord`.

**Anti-pattern found:** `BotDriveBridge` constructs **19 throwaway `new GameplayActor(...)`**
instances (`BotDriveBridge.cs:900, 930, 1292, 1295, 1315, 1619, 1622, 1903-1904, 2101-2102,
2875, 2901, 3251, 3290, 3415, 4028, 4104, 4246`). Those actors are *not* the scheduler's
actor, so the bridge's single-writer gate, idempotency ledger and audit trace are a
**different ledger** from the one the bot's own behaviour uses. The bridge's throwaway
actors also run **off the execution boundary** when the bridge serves a request from its
socket worker (`BotDriveBridge.cs:233` `Task.Run(() => ServeClientAsync(...))`,
`HandleCommand` called from `ServeClientAsync` at `:243`), so the
`AssertOnExecutionThread` guards on those calls either no-op (boundary not pinned in that
context) or fire a violation log. `[INFERENCE]` on which of the two happens depends on
whether a prior drain pinned `AsyncLocal` in this execution context; the guard returns
early when `boundary == 0` (`ExecutionBoundary.cs:101-103`).

### 2.5 Command / control pathways (three, non-equivalent)

| # | Transport | Entry point | Gate | Core | Cardinality |
|---|---|---|---|---|---|
| 1 | loopback JSON/TCP | `BotDriveBridge.HandleCommand` | `Bots.EnableE2EBridge` / `E2E_BRIDGE_ENABLED`, 127.0.0.1 only (`BotDriveBridge.cs:22-33`, `:96-119`) | `PlayerBotController` + ad-hoc engine calls + throwaway `GameplayActor` | 16 top-level cmds; ~90 op labels |
| 2 | REST `/api/actors/*` | `BotActionController` | `BotControlSettings.IsEnabled()` + `X-Auth-Token` fixed-time compare (`BotControlSettings.cs:18-25`) | `BotActionCommandQueue` (enqueue-only, drain on boundary) | 44 routes |
| 3 | REST `/api/bots*` | `BotControlController` | same gate | `BotAdminService` | 5 routes (list/status/add/remove/relocate) |
| 4 | console `/bot …` | 11 subcommand files under `AAEmu.Game/Scripts/SubCommands/Bots/` | GM command layer | `BotAdminService` + own executors | add/remove/restore/list/here/go/to/lab/party/home/wild |
| 5 | MCP stdio sidecar | `AAEmu.BotControlMcp/ActionMcpServer.cs` | 1:1 proxy of #2 | HTTP → `/api/actors/*` | **44 tools** = 44 routes, verified 1:1 (`ToolsList` `:193-366` vs `name switch` `:87-190`, zero drift) |

Only layer #2 has the designed properties (enqueue-only, single lifecycle, execution
boundary, audit trail). Layer #1 is the broadest and the least disciplined. Layers #1
and #2 are **not equivalent**: e.g. `advance_quest` over REST lands on
`GameplayActor.AdvanceQuest` (`BotActionCommandQueue.cs:530`), while `drive/advance` on
the bridge lands on `PlayerBotController.Advance` (`BotDriveBridge.cs:724-726`) — the
same engine call but with **no audit record, no lifecycle, no idempotency key**.

**Notable and relevant:** the MCP sidecar is already **gameplay-scoped** — all 44 tools map
to `/api/actors/*` and it contains **zero** references to `/api/bots` (verified: 0 matches
for `api/bots` in `ActionMcpServer.cs`), and its own doc states that "contract actions
without an authenticated actor endpoint remain deferred rather than being faked here"
(`ActionMcpServer.cs:9-13`). That is the two-layer split already expressed correctly at
the outermost edge — the design in §6/§7 generalises the property the MCP layer already
chose, rather than inventing it.

### 2.6 World entry

- Production: `PlayerBotLifecycleAdapter.ActivateHeadless` → shared
  `ICharacterLifecycleService.ActivateHeadless(character, ctx)` (`:57`) then
  `character.IsOnline = true; character.Spawn()` (`:60-61`). The doc at `:19-28` states
  why: a headless bot sends no `CSMoveUnitPacket`, so without the explicit placement it
  exists in `WorldManager._characters` but in **no region** and is invisible to clients.
- Networked: `BotNetworkSession.ConnectAsync` drives the real login/enter-world flow
  (`BotNetworkSession.cs:66-236`).
- Fixture: `HeadlessSession.Create` (`:492-516`).

---

## 3. Capability audit matrix

Verdict vocabulary: **AUTHORITATIVE** (real engine path a human client also uses) ·
**PARTIAL** (real path but with a stand-in target, fallback branch, or missing engine
gate) · **DUPLICATED** (parallel re-implementation of engine/validated logic) ·
**SYNTHETIC** (rig/fixture-only) · **MISSING** · **UNKNOWN**.

### 3.1 Movement

| Capability | `IGameplayActor` | Impl | Engine chain | Success / failure | Verdict |
|---|---|---|---|---|---|
| Walk to position | `MoveTo` `:81` | `:292-311` → `StartMove` `:399-418` | `Tick` leg `:4116-4179` → `ApplyCharacterMove` `:4246-4284` → `VehicleMovementModel.ApplyUnitMove` | `Complete("arrived")` `:4177` / `Reject(speed\|finite)` `:303-307`; `Expire(Navigation)` `:4037`; stuck → `Expire(Navigation)` `:4372` | AUTHORITATIVE |
| Routed walk | `NavigateTo` `:89` | `:312-321` → `NavigateToInternal` `:334-392` | `PathNode.FindPath` `:358` → waypoint queue `:362`; else `ObstacleManager.IntersectsObstacle/FindDetour` `:377-386`; **else straight line** `:390` | `Start("navigating route (N waypoints)")` `:366` | **PARTIAL** (3 fallback tiers; navmesh absence silently degrades) |
| Walk to unit | `NavigateToUnit` `:97` / `MoveToUnit` `:100` | `:323-332` / `:462-476` | `ResolveUnit` then **position snapshot at request time** | `Reject("target unit not found")` | **PARTIAL** (target not re-tracked; a moving target is missed) |
| Stop | `Stop` `:106` | `:477-500` | `BroadcastStop` `:4415-4417` only when interrupting a Move | `Complete("stopped")` | AUTHORITATIVE |
| Drive vehicle | `DriveVehicle` `:473` | `:2098` + Tick `:4183-4232` | `ApplyVehicleMove` `:4427` → vehicle movement model | arrival → `Complete` | AUTHORITATIVE |
| Test-only route seam | — | `NavigateRoutedForTest` `:429` | same as NavigateTo | — | **SYNTHETIC** (test seam in production assembly) |

### 3.2 Targeting / auto-attack / skills

| Capability | Member | Impl | Engine chain | Verdict |
|---|---|---|---|---|
| Set target | `SetTarget` `:113` | `:502-528` | `Character.CurrentTarget = unit` `:516` + `SCTargetChangedPacket` `:523-525` (mirrors `CSChangeTargetPacket.Read`) | AUTHORITATIVE |
| Cast at unit | `Cast` `:121` | `:530-587` | `Character.UseSkill(skillId, target)` `:573` | AUTHORITATIVE |
| Cast at position | `CastAt` `:137` | `:589-667` | `new Skill` → `skill.Use(..., bypassGcd: **true**, out _)` `:655` | **PARTIAL** (hardcoded bypass; fixed `PosRot = 0f` `:650-652`; reagent pre-flight duplicates `ApplyReagents` `:622-629`) |
| Auto-attack | `AutoAttack` `:145` | `:669-738` | `skill.Use(..., bypassGcd: false)` `:731` + `IsAutoAttack = true` `:734` + `StartAutoSkill` `:735` | **PARTIAL** (skill choice 2 vs 4 re-derived from role + distance + ranged-weapon presence `:691-698` — a client-side heuristic, DUPLICATED) |
| Stop auto-attack | `StopAutoAttack` `:150` | `:740-760` | `AutoAttackTask.Cancel()` + `IsAutoAttack = false` | AUTHORITATIVE |
| Learned-skill gate | — (inside Cast/CastAt) | `:548-551`, `:611-614` | mirrors `CSStartSkillPacket` learned branch | **DUPLICATED** |
| GCD ownership | — | `CombatExecutor.cs:230-243` | real `Skill.Use` gate fields | **SYNTHETIC** — executor has **zero production callers** (only `CombatExecutor.cs` itself + `CombatExecutorTests.cs`); the live cast path bypasses GCD (verified: `GameplayActor.cs:571-574` deferral comment) |

### 3.3 Interaction / loot / item / equip

| Capability | Member | Impl | Engine chain | Verdict |
|---|---|---|---|---|
| Doodad interact | `Interact` `:162` | `:1401-1455` | `doodad.Use(Character, skillId)` `:1452`; **house branch** `Character.UseSkill` `:1428` | AUTHORITATIVE |
| Doodad interact (derived skill) | `InteractWith` `:709` | `:1459-1557` | `SkillManager` + `skill.Use(..., false)` `:1512`; skill-less `doodad.Use` `:1524`; fails closed on no observable delta `:1553` | **PARTIAL** (re-implements `DoodadManager.GetFunc` matching in `ResolveInteractionSkill` `:1559-1578`; authors `SCSkill*` packets headless `:1519-1526`) |
| Loot | `Loot` `:171` | `:1594-1623` | `owner.LootingContainer` + `container.OpenBag(..., lootAll:true)` `:1617` | AUTHORITATIVE (grant count computed locally before/after `:1619` — DUPLICATED accounting) |
| Use item | `UseItem` `:182` | `:1625-1707` | `new SkillItem(...)` `:1677` + `skill.Use(..., false)` `:1680` | AUTHORITATIVE |
| Equip | `Equip` `:199` | `:1709-1758` | `Inventory.SplitOrMoveItem(SwapItems → Equipment)` `:1752-1753` | **PARTIAL** (slot pick is first-empty-else-first allowed `:1737-1743`; no level gate `:1751`) |

### 3.4 Quests

| Capability | Member | Impl | Engine chain | Verdict |
|---|---|---|---|---|
| Accept | `AcceptQuest` `:635` | `:783-825` | `PlayerBotController.AcceptQuest` → `CharacterQuests.AddQuest` (`CharacterQuests.cs:82`) + `RunCurrentStep` (`NewQuestCode.cs:62`) | AUTHORITATIVE (effect-key dedupe `:797-800`, `:820`) |
| Advance | `AdvanceQuest` `:643` | `:827-843` | `Quest.RunCurrentStep` `NewQuestCode.cs:62` | AUTHORITATIVE |
| Turn in (NPC/doodad/auto) | `TurnInQuest` `:652`, `TurnInAtDoodad` `:714`, `AutoTurnInQuest` `:720` | `:861-919` | `PlayerBotController.Report*` → `QuestManager.DoReportEvents` (`QuestManagerEvents.cs:22`) — the exact `CSCompleteQuestContextPacket` path | AUTHORITATIVE |
| Talk credit | `Talk` `:741` | `:941-1041` | `QuestManager.DoTalkMadeEvents` `:993` → `UnitEvents.OnTalkMade/OnTalkNpcGroupMade` (`UnitEvents.cs:45-46`) | AUTHORITATIVE (Completed only on an observed quest delta) |
| Ambient NPC talk | `InteractNpc` `:747` | `:1044-1140` | same, plus `SCNpcInteractionSkillListPacket` | **PARTIAL** (completes even with zero delta) |
| Cinema | `PlayCinema` `:788` | `:1341-1363` | `Character.Events.OnCinemaStarted/Ended` `:1359-1360` | **PARTIAL** (fixture-shaped: Started+Ended back-to-back, no client round trip `:1353-1362`) |
| Discovery (target) | `DiscoverQuests` `:681` | `:1162-1235` | `QuestManager.GetQuestsOfferedBy*` + an `IsDiscoverable` mirror | AUTHORITATIVE (mirror is DUPLICATED) |
| Discovery (self) | `DiscoverSelfQuests` `:780` | `:1250-1335` | item/sphere/level offer channels | AUTHORITATIVE |
| Quest progress **push** | — | — | `Npc.DoDie → DoOnMonsterHuntEvents` (`QuestManagerEvents.cs:169`); `AddExp → DoOnLevelUpEvents` (`Character.cs:1550`); `SphereTick` | **UNKNOWN** as a subscription — the bot **polls** (`Observe.ActiveQuestIds`), it does not subscribe |

### 3.5 Inventory / economy

| Capability | Member | Impl | Engine chain | Verdict |
|---|---|---|---|---|
| Deposit / withdraw money | `:591` / `:599` | `:3285-3328` / `:3330-3362` | `Character.ChangeMoney(Inventory,Bank,…)` (`Character.cs:1584`) | AUTHORITATIVE |
| Deposit / withdraw item | `:611` / `:620` | `:3364-3450` | `Inventory.SplitOrMoveItem` (`Inventory.cs:274`) | AUTHORITATIVE |
| Buy from vendor | `Buy` `:805` | `:2699-2764` | `NpcManager.GetGoods` (`NpcManager.cs:111`) + `AcquireDefaultItem` (`ItemContainer.cs:662`) + `ChangeMoney` (`Character.cs:1582`) | **PARTIAL** (grant-then-charge split at `:2749-2752`: no atomic rollback, no honor/vocation) |
| Sell to vendor | `Sell` `:818` | `:2766-2818` | `BuyBackItems.AddOrMoveExistingItem` + `MarkItemForDbDeletion` + `ChangeMoney` | AUTHORITATIVE |
| Sell specialty pack | `SellSpecialty` `:827` | `:2820-2873` | `SpecialtyManager.SellSpecialty` (`SpecialtyManager.cs:247`) | AUTHORITATIVE |
| Repair | `Repair` `:834` | — | blacksmith path | AUTHORITATIVE (per `EquipmentRepairE2eTests`) |
| Auction post / buy | `PostAuction` `:847`, `BuyAuction` `:861` | `:2922`, `:2975` | `AuctionManager.PostLotOnAuction` (`:649`) / `BidOnAuctionLot` (`:162`) | AUTHORITATIVE |
| Auction cancel / search / non-buyout bid | **MISSING** | — | `AuctionManager.CancelAuctionLot` (`:115`), `SearchAuctionLots` (`:575`) | MISSING |
| Mail send / list / take attachment | **MISSING** | — | `CSSendMailPacket` → `CharacterMails.SendMailToPlayer` (`:85`); `CSTakeAttachmentItemPacket` → `MailManager` (`:20`) | MISSING |

### 3.6 Crafting / harvesting / planting / housing

| Capability | Member | Impl | Engine chain | Verdict |
|---|---|---|---|---|
| Craft one step | `Craft` `:448` | `:3676-3774` + Tick drain `:4049-4096` | `CraftManager.GetCraftById` (`:121`) + `Character.Craft.Craft` (`CharacterCraft.cs:44`) | AUTHORITATIVE |
| Harvest crop | `Harvest` `:428` | `:3497-3621` | `TryResolveHarvestSkill` (data-driven phase funcs) + `doodad.Use(Character, skill)` `:3581` | **PARTIAL** — authors `SCSkillStartedPacket` `:3543`, `SCSkillFiredPacket` `:3580`, manual `ChangeLabor` `:3589`, `SCSkillEndedPacket` `:3596`; the in-file Q-harvest-seam note `:3559-3569` says these must converge onto `Skill.Use(bypassGcd:false)` |
| Plant | `Plant` `:555` | `:3030-3164` | `ItemManager.GetDoodadIdFromItem` (`:271`) + `DoodadManager.CreatePlayerDoodad` (`DoodadManager.cs:3037`) | AUTHORITATIVE |
| Build house | `BuildHouse` `:582` | `:3167` | `HousingManager.Build` (`HousingManager.cs:612`) | AUTHORITATIVE |
| Housing demolish / tax / decorate / permissions | **MISSING** | — | `HousingManager.Demolish` (`:910`), `DecorateHouse` | MISSING |
| Farm create / grow | **MISSING** (only `Plant`) | — | `DoodadManager.CreatePlayerDoodad` | MISSING |

### 3.7 Party / expedition / trade / mounts / vehicles / packs

| Capability | Member | Impl | Engine chain | Verdict |
|---|---|---|---|---|
| Party invite / accept | `:218` / `:234` | `:1761` / `:1805` | `TeamManager.AskToJoin` (`:122`) / `ReplyToJoinTeam` (`:173`) | AUTHORITATIVE |
| Party leave / kick | **MISSING** | — | `CSLeaveTeamPacket` / `CSKickTeamMemberPacket` are **log-only stubs** (no manager call) | MISSING (no working engine path either) |
| Expedition create/invite/accept/leave | `:251`/`:264`/`:280`/`:289` | `:3832`/`:3867`/`:3905`/`:3943` | `ExpeditionManager.CreateExpedition` (`:171`), `Invite` (`:298`), `ReplyInvite` (`:316`), `Leave` (`:361`) | AUTHORITATIVE (invite = PARTIAL: no server-side invitation record to post-check) |
| Player trade offer/putup/lock | `:310`/`:326`/`:343` | `:1838`/`:1880`/`:1919` | `TradeManager.TryStartTrade` (`:164`), `AddItem` (`:222`), `LockTrade`/`ConfirmTrade` (`:329`/`:378`) | AUTHORITATIVE |
| Trade cancel | **MISSING** | — | `CSCancelTradePacket` → `TradeManager.CancelTrade` (`:198`) | MISSING |
| Mount / dismount / dismiss | `:354`/`:362`/`:369` | `:1958`/`:2007`/`:2045` | `MateManager.MountMate` (`:154`) / `UnMountMate` (`:206`); `Character.Mates.DespawnMate` | AUTHORITATIVE |
| Board / unboard vehicle | `:390`/`:401` | `:2433` (slave/transfer-seat/glider branches) / `:2601` | `SlaveManager.BindSlave` (`:167`) / `UnbindSlave` (`:135`); `DoodadFuncAttachment.Use`; `TakeoffBackpack` | AUTHORITATIVE |
| Pack pickup / putdown | `:490`/`:507` | `:2150`/`:2222` | `new RecoverItem().Execute` (`:2198`) ← `CSLootOpenBagPacket`; `Skill.Use` SkillItem caster | AUTHORITATIVE |
| Load pack onto vehicle | `:532` | `:2324` | `PackVehicleService.TryLoadCarriedPack/TryLoadPlacedPack` (`PackVehicleService.cs:45`) | AUTHORITATIVE |
| Unload pack | **MISSING** | — | — | MISSING |
| Slave summon / despawn | **MISSING** | — | `CSSpawnSlavePacket` (`:8`) → SlaveManager spawn | MISSING |

### 3.8 Actor-vs-control-plane coverage (verified count)

- `ActorActionType`: **55** values (`IGameplayActor.cs:889-1062`, extracted verbatim).
- `BotActionKind`: **44** values (`BotActionCommandQueue.cs:16-96`).
- **16 actor actions are unreachable through the control plane:**
  `AuctionPost, AuctionBuy, HouseBuild, Drive, PartyInvite, PartyAccept, CastAt,
  ExpeditionCreate, ExpeditionInvite, ExpeditionAccept, ExpeditionLeave, PlayCinema,
  DismissMate, InteractNpc, AutoAttack, StopAutoAttack`
  (`ToActorAction` map `BotActionCommandQueue.cs:868-915` has no case for them, and
  `ExecuteKind` `:516-700` has no dispatch case either).
- Conversely `MoveToUnit`, `Navigate`, `NavigateToUnit`, `DriveVehicle`, `Interrupt` are
  control-plane-only kinds that map onto actor actions (`ToActorAction` `:870`, `:911`).
- **Consequence:** the most capability-relevant verbs — party formation, expedition,
  auto-attack, position casts, house build, auction — exist on the actor but only the
  *bridge* (un-audited, throwaway-actor) path exposes them, via scenario composers.

---

## 4. Ownership map — authoritative engine owner per responsibility

| Responsibility | Engine owner | Real-player path | Bot path | Divergence |
|---|---|---|---|---|
| Movement (on foot) | `VehicleMovementModel.ApplyUnitMove` | `CSMoveUnitPacket.Execute` (`CSMoveUnitPacket.cs`) parses a client-authoritative `MoveType` | actor `Tick` computes the step itself and calls `ApplyUnitMove` (`GameplayActor.cs:4283`) | **Movement is bot-authored, not client-authored.** The bot's step integrates position locally; the client sends its own position. Geometry/fidelity is therefore bot-side, not engine-side. `[INFERENCE]` the engine path (broadcast + finalize) is shared. |
| Movement (throttled broadcast) | `WorldManager.AddVisibleObject` / around-broadcast | every client frame | `BotRoamStepExecutor` 15 Hz throttle (`:63-73`, broadcast at ~4-6 Hz in the class doc) with `BroadcastMovement=false` set by the executor (`:504-506`) | bot suppresses the actor's own per-apply broadcast (`GameplayActor.cs:4273-4281`) → silent `Transform` write. This is exactly the divergence the wave-1 conversion names as a movement-fidelity gap. |
| Targeting | `Character.CurrentTarget` + `SCTargetChangedPacket` | `CSChangeTargetPacket.Read` | `GameplayActor.SetTarget` (`:516-525`) | none observed |
| Skill legality | `SkillManager` template/learned/common gates + `Skill.Use` GCD/cooldown/reagent/range | `CSStartSkillPacket.Read` → `Character.UseSkill` | `Cast` `:542-563` (mirror gates) + `Character.UseSkill` | **GCD arm never fires on the bot path**: `Unit.UseSkill` hardcodes `bypassGcd: true` (`Unit.cs:1069-1081`); `CastAt` does the same (`GameplayActor.cs:655`). Only `AutoAttack` passes `false` (`:731`). |
| Skill execution effects | `Skill.Use` → `ApplySkillTask` / plot runtime | same | same | none |
| Interaction effects | `Doodad.Use` → func phase machine | `CSStartSkillPacket` interaction branch / `CSLootOpenBagPacket` | `Interact` `:1452`, `InteractWith` `:1512/:1524` | `InteractWith` fails closed on no delta (`:1553`) — stricter than the client, which cannot observe a silent refusal |
| Quest state | `CharacterQuests` + `Quest.RunCurrentStep` + `QuestManager.Do*Events` | `CSAcceptQuestPacket` / `CSCompleteQuestContextPacket` / `CSStartSkillPacket` talk | `AcceptQuest`/`AdvanceQuest`/`TurnIn*`/`Talk` | none for accept/advance/turn-in; **progress events are injected by the harness** through `UnitEvents` (`BotDriveBridge.cs:727-765`) — the same surface, but the human reaches it only by *playing* |
| Inventory | `Inventory` / `ItemContainer` | `CSSwapItemsPacket` etc. | `SplitOrMoveItem`, `AcquireDefaultItem` | none, except `Equip`'s slot heuristic (`:1737-1743`) |
| Loot | `LootingContainer.OpenBag` | `CSLootOpenBagPacket` | `Loot` `:1617` | none |
| Economy (vendor) | `NpcManager.GetGoods` + `ChangeMoney` | `CSBuyGoodsPacket`/`CSSellGoodsPacket` | `Buy`/`Sell` | grant-then-charge non-atomic (`:2749-2752`) |
| Economy (specialty) | `SpecialtyManager.SellSpecialty` | `CSSellBackpackGoodsPacket` | `SellSpecialty` | none |
| Doodad lifecycle | `DoodadManager` | packet-driven `CSCreateDoodadPacket` etc. | `CreatePlayerDoodad` (`:3037`) | none observed |
| Mount / vehicle | `MateManager` / `SlaveManager` / `TransferManager` | `CSMountMatePacket`, `CSBindSlavePacket`, `CSDiscardSlavePacket` | `Mount`/`BoardVehicle`/`UnboardVehicle` | **Board transfer-seat path calls the doodad func directly**, bypassing the `CSStartInteractionPacket`→`Doodad.Use` outer skill pipeline `[INFERENCE]` (the audit noted `seat.Use(Character, attachmentSkillId)`; whether that is the same seam the packet uses is UNKNOWN). |
| Party | `TeamManager` | `CSInviteToTeamPacket`, `CSReplyToJoinTeamPacket` | `PartyInvite`/`PartyAccept` | leave/kick have **no engine path at all** (stub handlers) |
| Trade | `TradeManager` | `CSCanStartTradePacket`→`CSStartTradePacket`, `CSPutupTradeItemPacket`, `CSTradeLockPacket`, `CSTradeOkPacket` | `TradeOffer`/`TradePutup`/`TradeLockOk` | cancel missing |
| Crafting | `CraftManager` + `Character.Craft` | `CSCraftPacket` | `Craft` | none |
| Housing | `HousingManager.Build` | `CSCreateHousePacket` | `BuildHouse` | bridge attaches an `E2eNullSession` `GameConnection` when headless (`BotDriveBridge.cs:3400-3406`) — a session stand-in |
| Auction | `AuctionManager` | `CSAuctionPostPacket`, `CSBidAuctionPacket` | `PostAuction`/`BuyAuction` | none |

---

## 5. Problems (verified, with disposition)

P1 — **The harness can perform gameplay.** 20 bridge ops inject `UnitEvents` progress
(`BotDriveBridge.cs:727-765`, `:766-786`, `:825-833`). A scenario that fires
`drive/kill` and then asserts `questState.completed` cannot distinguish "the bot hunted
and killed" from "the harness told the engine a kill happened".
*Disposition:* the boundary must forbid it (§6/§7).

P2 — **Three control planes, no single semantics.** Bridge / `/api/actors` /
`/api/bots`+console. `drive/advance` has no audit record; `advance_quest` does.
*Disposition:* converge on the `/api/actors` model; bridge becomes a debug alias or is
retired (§11).

P3 — **19 throwaway actors in the bridge** (`BotDriveBridge.cs` sites listed §2.4)
defeat the single-writer/ledger/audit contract and can run off the execution boundary.
*Disposition:* bridge gameplay ops must route through `BotActionCommandQueue`
(`GetOrCreateActor` `BotRoamStepExecutor.cs:358`).

P4 — **16 actor capabilities are unreachable from the audited control plane** (§3.8).
*Disposition:* add kinds + routes for the ones that are gameplay; classify the rest.

P5 — **Fixture overrides leak into acceptance paths.** `BotMemory` `*Override` fields are
gated behind `EnableFixtureOverrides()` (`BotMemory.cs:74-81`) and **no production code
calls it** (verified: callers are `AAEmu.UnitTests` only — `GoapRuntimeTests.cs:302`,
`GoapScenarioIntegrationTests.cs:213/297/418`, `HomesteadRuntimeTests.cs:387/445`,
`HomesteadTenBotScenarioRigTests.cs:386`). That is correct **today**. However the
provider reads them unconditionally through the `??` chain
(`BotWorldStateProvider.cs:93, 98, 140, 145, 152, 157, 164, 169, 174`) and
`HomesteadActions.cs`/`GoalArbitrator.cs` read the same fields by the same pattern
(`HomesteadActions.cs:49-53`, `:192-195`, `:277`, `:313-316`, `:391-394`, `:430-433`,
`:489-492`, `:550-553`; `GoalArbitrator.cs:108-113`). One `EnableFixtureOverrides()` call
from production flips every acceptance path to synthetic.
*Disposition:* keep the gate, and make the read sites structurally incapable of seeing a
set override outside a fixture scope (e.g. the flags become `internal` to the fixture
assembly).

P6 — **`AcquireScarecrowAction` can never complete in production.**
`HomesteadActions.cs:49-53` returns `Succeeded` only when
`HasScarecrowDesignOverride && HasTaxCertificatesOverride`, else `Running` forever; yet
`GoalArbitrator.cs:108-113` arbitrates `GoalClaimHomestead` for **any** bot without a plot,
with no design/kit precondition. Combined with `HomesteadActivityModule.Enabled = false`
default and **zero** production setters (`HomesteadActivityModule.cs:27`, verified
repo-wide), the homestead leg is doubly unreachable and, if enabled, unfinishable.
*Disposition:* correctness fix, not API shape — flagged here because it is the clearest
example of "wiring claimed, behaviour unreachable".

P7 — **Stand-in targets remain in the GOAP actions.** `FarmingActions.cs:247`
`Harvest(0)`, `HomesteadActions.cs:421` `Interact(2001)`, `HomesteadActions.cs:535`
`Interact(101)` — fixed ids where the action should resolve a live object. The step-8
dossier records the bare-`Interact(skill)` fallback as deleted for the *plot* variant
only (`step8-bot-loop-dossier-2026-09-17.md` §4).
*Disposition:* remove or prove unreachable; these are exactly "test-only powers" smuggled
into production actions.

P8 — **No wait/poll primitive; every scenario invents its own.** E2E files define
≈20 ad-hoc `Poll*/WaitFor*` helpers (`B6MerchantConservationE2eTests.cs`,
`BotControlApiE2eTests.cs`, `MovementStopInterruptE2eTests.cs`, …). The bridge has one
blocking server-side wait (`farm/needs` `waitMs` poll, `BotDriveBridge.cs:4151-4154`).
*Disposition:* a real Test/Control API owns `wait` (§7).

P9 — **Traceability is split across four sinks with different schemas.**
`ActorAuditRecord.ToJson` snake_case (`ActorAuditRecord.cs:86-111`) → `playerbot_audit`
table (via `PlayerBotAuditSink.Flush`); in-memory `BotActionCommandQueue.TraceFor`
(`:341`); bridge `traceRecords` inline in scenario responses; `PlayerTraceService` JSONL
(`:223-249`) which carries `ts/character_id/event` and is **armed only by a `/trace`
GM subcommand** (`PlayerTraceStartSubCommand.cs:79`) — no test arms it.
*Disposition:* one trace contract, one arming path (§8/§10).

P10 — **Movement fidelity is structurally unobservable today.** The bot writes position
with `BroadcastMovement=false` (`BotRoamStepExecutor.cs:504-506` →
`GameplayActor.cs:4273-4281`), so the `SCOneUnitMovementPacket` the wave-1 conversion asks
to capture is *suppressed* on the roam path. Only wire-tap tests using the real client
`CSMoveUnitPacket` (`E2E_WIRE_DUMP`, 1 test file) observe movement.
*Disposition:* the observation API must expose a first-class per-frame motion series
(§8).

P11 — **`GameplayActorStepExecutor` and `GoapBotStepExecutor` are dead code.**
Verified: no DI reference, no production caller; only unit tests and docs reference them.
*Disposition:* remove or document as archival (§11 REMOVE).

P12 — **`PlanTemplateCache` keys on flags only, ignoring Labor/Gold**
(`PlanTemplateCache.cs:14`, loads at `GoapPlanRunner.cs:309`, stores `:344`). Latent
today because a hit only emits telemetry and never executes
(`GoapRunner.cs:309-314`), but it is a live correctness bug the moment cache-hit execution
is wired.
*Disposition:* out of API scope; recorded because the capability API's determinism
depends on it.

---

## 6. Gameplay Capability API

### 6.1 Should the two layers be separate? — Yes, with a sharper reason than "cleanliness"

The question in Phase 4 is whether a **Gameplay Capability API** (NavigateTo / Target /
Attack / Cast / Interact / AcceptQuest / TurnInQuest / Loot / UseItem / Equip / Buy /
Plant / Harvest / Mount — no test-only powers) and a **Test/Control API** (spawn / remove
/ assign / observe / wait / collect-trace + privileged fixture ops, clearly marked)
should be separate layers.

**Decision: separate — because they have different *truth obligations*, not different
convenience.** A gameplay capability must be *reproducible by a bot's own behaviour*; a
test control op must be *impossible to mistake for behaviour*. The current code violates
this in exactly two directions (§5 P1, P5), and both violations are structural, so no
amount of documentation fixes them. Separating them makes the violation a compile-time
impossibility rather than a review habit.

Additionally, the two layers already have **different natural lifetimes and blast radii**:
capabilities are shipped gameplay (`AAEmu.Game`, prod-reachable via bots); fixture ops are
E2E-only and gated off in prod (`Bots.EnableE2EBridge`, `BotControlSettings`). Putting
them in one interface forces every capability consumer (GOAP actions, scenario composers,
the production scheduler) to be able to name a fixture op.

### 6.2 Proposed Gameplay Capability API — shape

**Keep the existing `IGameplayActor`, with these changes:**

1. **Split the interface by capability family** (facets over one implementation, never
   parallel implementations):
   `IMovementCapability`, `ITargetingCapability`, `ICombatCapability`,
   `IInteractionCapability`, `INpcInteractionCapability`, `IQuestCapability`,
   `IInventoryCapability`, `IEconomyCapability`, `IProductionCapability`
   (craft/plant/harvest/build), `ISocialCapability` (party/expedition/trade),
   `ITransitCapability` (mount/vehicle/pack), `IObservationCapability`.
   `IGameplayActor` stays as the composition of all facets, so no caller breaks.

2. **Delete the test-only members** from the interface: `NavigateRoutedForTest`
   (`GameplayActor.cs:429`) — keep the seam but move it behind the fixture boundary
   (§7.3), not on the capability interface.

3. **Make `Result` a typed, discriminated action result** rather than `object?`
   (today: `SkillResult`, `int`, `bool`, `TalkResult`, `InteractWithResult`,
   `CraftResult`, `QuestDiscoveryResult` — `ActorRequest.cs:71-72` + `:214-311`).

4. **One completion contract** (§7 of the phase list — see §9 below), instead of the
   current mixture of "Completed the instant the engine accepted it" (Cast, `:583`),
   "Completed after a poll" (Craft, PutDown), and "Completed on an observed delta"
   (`InteractWith` `:1553`, `Talk`).

5. **No new powers.** Every capability member must be invocable by a bot whose only
   inputs are observation + the capability API. Concretely: any member that today relies
   on a fixture override (`Has*Override`) must either gain a real acquisition path or be
   removed from the capability layer.

### 6.3 Proposed Gameplay Capability API — the intent layer

The API is a *capability* API, not an *intent* API, and that is correct: intent
(what to do next) belongs to the behaviour layer (activity modules + GOAP), which already
exists. To keep that split honest, the behaviour layer must be the only caller in
production. Today it is not: `BotDriveBridge` and the scenario composers call the
capability API directly. That is acceptable **only** when the caller is a Test/Control
client and the artifact says so — which is the separation this design asks for.

### 6.4 What must NOT belong to the Gameplay Capability API

| Must not belong | Where it is today | Why |
|---|---|---|
| Fixture stock/level/money grants | `drive/stock` `BotDriveBridge.cs:828-830`; `drive/setLevel` `:831-833`; `auction/rig` `:2850-2858`; `mail/rig` `:3093-3142`; `housing/rig` `:3377-3392`; `farm/rig` `:3955-3975`; `homestead/kit` `:3637-3652` | these change the character without a gameplay act; a capability that can be granted is not a capability |
| Positional staging / teleports | `drive/teleportToNpc` `:787-813`; `homestead/move` `:3822-3848`; `farm/place` `:3976-4017`; `mail/mailbox` `:3144-3171` | travel is the capability under test in every movement/conversion scenario; staging is setup and must be labelled |
| Progress-event injection | `drive/kill` `:727-729`, `killGroup` `:730-732`, `gather` `:733-735`, `useItem` `:736-738`, `talk` `:739-741`, `interact` `:742-744`, `enterSphere` `:745-747`, `express` `:748-750`, `levelUp` `:751-753`, `aggro` `:754-756`, `zoneKill` `:757-759`, `cinemaStarted/Ended` `:760-765` | these *are* gameplay; letting the harness fire them makes bot competence unprovable |
| Fixture memory override layer | `BotMemory.cs:84-136` + reads at `BotWorldStateProvider.cs:93…174`, `HomesteadActions.cs:49…553`, `GoalArbitrator.cs:108-113` | a capability that trusts a fabricated world state |
| Rig actor / DB-row-less session | `HeadlessSession.Create` `:492-516`; `GameplayActorTestRig.CreateActor` `:713-782` | synthetic actors must not be addressable as bots |
| Auth/identity bypass | (none found — `BotNetworkSession.cs:29-33` is clean) | keep it that way |
| Stand-in target ids in production actions | `Harvest(0)` `FarmingActions.cs:247`; `Interact(2001)` `HomesteadActions.cs:421`; `Interact(101)` `HomesteadActions.cs:535` | a fixed id is a fake target |
| Manual reward/packet authoring | `SCSkillStartedPacket`/`SCSkillFiredPacket`/`SCSkillEndedPacket` + manual `ChangeLabor` in `Harvest` `:3543-3596` | observer packets must be engine emissions |

---

## 7. Test / Control API

### 7.1 What it must own

| Group | Verbs | Backing |
|---|---|---|
| **Identity** | spawn (provision), remove (deactivate), list, status, relocate, restore | `IPlayerBotManager`, `BotAdminService.List/Add/Remove/Go/Restore` (`BotAdminService.cs:136/210/312/345/513`) |
| **Assignment** | set activity / set roam route / set cadence / enroll in a scenario | `BotAdminService.Lab` (`:373`), `BotRoamStepExecutor.SetRoamRoute` (`:381`), `SetBotCadence` |
| **Observation** | observe (structured snapshot) | `IGameplayActor.Observe` `:202-265` → `ActorObservation` |
| **Waiting** | wait-for(condition, timeout); wait-until-idle | **MISSING** — see §10 gap 4 |
| **Trace** | trace(bot, limit); trace(traceId); export | `BotActionCommandQueue.TraceFor` (`:341`), `GET /api/actors/actions/{id}` (`BotActionController.cs:1283`), `GET /api/actors/trace` (`:1309`) |
| **World fixtures (privileged, labelled)** | grant kit, stage position, seed roster, set level/money, spawn fixtures | the fixture ops enumerated in §6.4 |

### 7.2 Hard rules for the Test/Control API

1. **Every privileged op carries a mandatory `setup: true` marker** and is echoed into
   the run artifact's `setup` block. (Wave-1 conversion already requires a `setup` block
   per report — `scenario_conversion.md` §D1 — this makes it machine-checkable.)
2. **No Test/Control verb may inject a gameplay event.** If a scenario needs "the mob
   died", the bot must kill it through `Cast`/`AutoAttack`; if a scenario needs "the item
   was gathered", the bot must `Harvest`/`Loot` it. The 20 injection ops are removed or
   quarantined behind a `simulate:` prefix that stamps `is_synthetic: true` on the run.
3. **No Test/Control verb may produce a success the capability layer could not.**
   `drive/setLevel` is the model violation: it writes `character.Level` directly
   (`BotDriveBridge.cs:832`), so a level-gated quest accept proves nothing about
   progression. Either it stays (and the run is stamped synthetic) or the scenario grants
   level through ordinary play.
4. **Fixture ops must be unreachable in production.** They already are, gate-wise
   (`BotDriveBridge` requires an explicit config flag, `BotControlSettings` requires the
   token) — the design keeps that and adds a compile-time boundary (§7.3).
5. **Control ops never touch the world off the execution boundary.** Today's bridge
   violates this (§5 P3). Control ops must enqueue, as `/api/actors` does
   (`BotActionCommandQueue.Enqueue` `:288-302`, executed only in `DrainCommands`
   `:349-351` after `ExecutionBoundary.RegisterExecutionThread()`).

### 7.3 Where the boundary should be enforced

The cleanest enforcement the repo already supports is **assembly/namespace separation plus
the existing gate flag**:

- Capability layer: `AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs` +
  `GameplayActor.cs` (+ facet interfaces) — shipped gameplay.
- Control layer: `AAEmu.Game/Services/WebApi/Controllers/*` and the bridge — already
  gated off in prod; add the rule that it may only construct `BotActionSpec`s and call
  `BotActionCommandQueue`.
- Fixture layer: the `*Override` setters and staging ops become `internal` to the test
  assembly (`InternalsVisibleTo`), which is what `EnableFixtureOverrides` already
  approximates at runtime (`BotMemory.cs:81`).

`[INFERENCE]` A full project split (`AAEmu.BotControl`) is *possible* but not required —
the repo already compiles the sidecar separately (`AAEmu.BotControlMcp`), so the boundary
can be a namespace + gate today and a project split later without rework.

---

## 8. Observation API

### 8.1 What exists

**(a) `IGameplayActor.Observe()` → `ActorObservation`** — the *player-observable* snapshot.
All fields are direct engine reads at call time (`GameplayActor.cs:202-265`;
`ActorObservation.cs:24-85`): position, current target, HP/MP, nearby characters/NPCs/
doodads via `WorldManager.GetAround(…, 25f)`, active quest ids, money/bank/labor,
bag/bank item counts, carried pack template, party fields (owner, pending invitation,
leader objId/position/target). **Nothing here is fabricated**; the only synthesis is the
aggregation (`CountByTemplate`, `:270-284`). It emits an audit record as a query
(`:257-262`).

**(b) `BotWorldStateProvider.Project` → `BotWorldState`** — the *planner* projection, 34
flags (`BotWorldStateProvider.cs:60-233`). Classified:

| Class | Flags | Source |
|---|---|---|
| engine-query | LowHealth, LowMana, Recovered, InCombat, IsSitting, HasActiveTarget, TargetIsHostile, TargetInRange, TargetDead, Labor, Gold, BagFull | `BotWorldStateProvider.cs:77-90`, `:113-115`, `:204-229` |
| projected-memory | SecretGrovePlanted, NearSeedMerchant, AtWildFarm, NearHousingZone, NearHomeSite, NearWorkbench, HasCorpseToLoot, HasLooted | `:104`, `:124`, `:131`, `:183`, `:190`, `:197`, `:220`, `:225` |
| synthetic-override-first | HasEdibleFood, HasTreeSaplings, HasScarecrowDesign, HasTaxCertificates, HasLandPlot, HasTimber, HasBuildingMaterials, HomeConstructed, PlotConstructed | `:93-177`, each an `?? live-read` chain |
| **MISSING (defined, never written)** | NearContinentalRoad, HasQuestObjective, QuestObjectiveComplete, NearQuestGiver, QuestTurnedIn, NearGeneralMerchant, NearCampfire, AtTradePost, HasTradePack, TradePackSold | declared `BotWorldState.cs:32-43`; no write site in `Project` |

Two correctness notes that matter for a capability API built on this projection:
- `BagFull` is now the honest `ItemContainer.FreeSlotCount` with an unspecified-mask clear
  when indeterminable (`BotWorldStateProvider.cs:107-115`) — the earlier
  maturity-conflated version is fixed.
- `HasScarecrowDesign` checks **15596 only** (`:140-142`), with an in-file note that 15566
  is a different design; `BotHomeSubCommand`/`AcquireScarecrowAction` historically used a
  different second id (`ScarecrowDesignId = 267`). UNKNOWN whether the divergence is
  still live; flagged as a constant-consistency check.
- `TargetIsHostile` is real (`CombatDecisionTree.IsHostileTarget`, `:206-207`) — the
  earlier "every NPC counts hostile" state is repaired.

**(c) `PlayerTraceService`** — JSONL per-character trace with a real schema
(`PlayerTraceService.cs:24-42`: `ts`, `elapsed_ms`, `character_id`, `character_name`,
`category`, `event`, `data`), fed from `RecordPacketIn/Out`, `RecordMovementSample`,
`RecordSkill`, `RecordInteraction`, `RecordWorld`, `RecordRefusal`, `RecordResource`,
`RecordTarget` (`:267-442`), with hook sites in `GameConnection.cs:61-64`,
`GameProtocolHandler.cs:206-209`, `CSMoveUnitPacket.cs:53-59`,
`CSStartSkillPacket.cs:67-102`, `Skill.cs:337-364`, `Doodad.cs:430-434`, `:803-808`,
`Character.cs:1620-1624`, `:1676-1681`, `DoodadManager.cs:3114-3118`, and `GameplayActor`
(`:964-968`, `:1035-1038`, `:1998-2002`, `:2037-2040`, `:2089-2092`, `:2753-2757`,
`:2812-2813`, `:3146`, `:3589`, `:3600`). **It is inactive unless a GM runs `/trace start`**
(`PlayerTraceStartSubCommand.cs:79`); no test or script arms it (verified). This is a
ready-made *player-observable* event stream that the API currently does not use.

**(d) `BotActionCommandQueue` trace + `playerbot_audit`** — the *audit* stream
(§4 of the observation-trace audit): `ActorAuditRecord` fields at `ActorAuditRecord.cs:47-66`,
`ToJson` snake_case at `:86-111`, written by `GameplayActor.Finish` (`:4640-4660`),
published by `BotActionCommandQueue.PublishSnapshot` (`:833-868`), buffered by
`PlayerBotAuditSink.Enqueue` (`:53-58`, cap 10 000, drop-oldest) and flushed inside the
SaveManager transaction to `aaemu_game.playerbot_audit (id, character_id, audit_json,
created_at)` (`:71-127`).

**(e) `ActorObservation` vs `Observe` over the control plane.** `POST /api/actors/observe`
returns the observation as the action result (`BotActionCommandQueue.cs:519`); the bridge
has no generic observe op except domain-specific read ops.

### 8.2 The cheating boundary (SERVER vs PLAYER-OBSERVABLE)

| Class | Examples | Rule |
|---|---|---|
| **PLAYER-OBSERVABLE** (a human client could see it) | position, HP/MP, nearby units, target, chat-visible events, inventory contents the player holds, quest log, money, `SCOneUnitMovementPacket`/`SCSkill*Packet` broadcasts, `PlayerTraceService` packet records | legal for both capability decisions and assertions |
| **SERVER-ONLY** (a human client cannot see it, but it is engine truth) | `HousingManager.GetAllHouses()` ownership scan, `character_quests` rows in MySQL, `TeamManager.GetActiveTeamByUnit`, `Character.LaborPower` before the next packet, `playerbot_audit`, scheduler metrics, `playerbot_metadata` | legal for **assertions** and for **server-side decision logic**; must never be presented as "the player would have seen X" |
| **SYNTHETIC** (fabricated or override) | every `BotMemory.*Override`, `GameplayActorTestRig` state, `drive/setLevel`, injected `UnitEvents` progress, authored `SCSkill*` packets in `Harvest` | legal for fixtures **only**, must be stamped `is_synthetic`, and must never back a capability claim |

The concrete leaks across this boundary today: §5 P1 (harness performs gameplay),
§5 P5 (override layer), §5 P7 (stand-in targets). All three are **assertion-side**:
they let a scenario assert an engine state that the bot did not cause.

---

## 9. Scenario model

The model the repo already supports: **SETUP → BOT INTENT → REAL EXECUTION →
ENGINE PATH → OBSERVED STATE → ASSERTION → TRACE**, where the harness never drives
gameplay.

| Phase | Owner | Mechanism today | Required change |
|---|---|---|---|
| SETUP | Test/Control API | `provision`, `kit`, `move`, `place`, `rig`, `stock`, `setLevel` | mark every op `setup:true`; echo into the artifact |
| BOT INTENT | behaviour layer | activity module arbitration (`IBotActivityModule` / `BotGoalArbiter`) **or** a scripted scenario composer | intent must be *declared*, not *executed*: a scenario says "pursue quest 250", not "fire 8 kills" |
| REAL EXECUTION | capability API | `IGameplayActor` verbs | unchanged; remove fixture-dependent verbs |
| ENGINE PATH | engine | `CharacterQuests`/`Skill.Use`/`Doodad.Use`/managers | unchanged |
| OBSERVED STATE | observation API | `Observe()` + bridge reads + MySQL | add `wait-for` and a motion series (§10) |
| ASSERTION | scenario | per-scenario asserts + MySQL conservation | assert on engine state *caused by the bot*; refusals must be present (§9 negative-case rule) |
| TRACE | trace API | `ActorAuditRecord` → `playerbot_audit`; bridge `traceRecords`; `PlayerTraceService` (unused) | one trace contract; actor identity mandatory (`actor_id` is already in the record `:48`) |

### Example 1 — Quest spine, bot-driven (M1 conversion)

```
SETUP      provision(fresh, level 1, no kit)            [setup:true]
           stage at canonical starter spawn             [setup:true]
BOT INTENT scheduler wake → arbiter → quest.progress        (QuestBootstrapActivityModule)
           QuestDecisionScenario.Run: perceive → decide → act
EXECUTION  DiscoverQuests(npcObjId) → AcceptQuest(questId, Npc, acceptorId)
           NavigateTo(objectivePos) → SetTarget(npc) → Cast(skill, npc)
           Loot(corpseObjId) → NavigateTo(giverPos) → TurnInQuest(questId, npcObjId)
ENGINE     CharacterQuests.AddQuest (CharacterQuests.cs:82)
           Quest.RunCurrentStep (NewQuestCode.cs:62)
           Npc.DoDie → QuestManager.DoOnMonsterHuntEvents (QuestManagerEvents.cs:169)
           QuestManager.DoReportEvents (QuestManagerEvents.cs:22)
OBSERVED   Observe(): ActiveQuestIds, position, bag counts
           MySQL: character_quests row; characters.level
ASSERT     quest completed flag set AND level increased AND loot delta == expected
           AND ≥1 refusal observed (e.g. re-accept refused) with no state change
TRACE      ActorAuditRecord per verb → playerbot_audit (character_id, audit_json)
```
**Forbidden under the model:** `drive/kill`, `drive/gather`, `drive/setLevel`.
Evidence today: `AdventurerSpikeE2eTests` real damage via `Npc.DoDie` → hunt events;
`Q4LiveHuntLootE2eTests` real cast-kill + loot; `LevelingLoopScenario` has
**no live driver** (`leveling-loop-perception` appears in **0** integration tests —
verified by grep).

### Example 2 — Homestead claim → construct (step-8 loop)

```
SETUP      homestead kit (design 15596 ×1, certs ×10)   [setup:true, labelled grant]
           homestead move to an engine-legal area centroid [setup:true, NOT traversal]
BOT INTENT arbiter holds homestead.progression → StepHomesteadLeg → GoapPlanRunner.Tick
           GoalArbitrator → GoalClaimHomestead → SurveyAndPlacePlotAction
EXECUTION  BuildHouse(267, 15596, resolvedZonePos)   (GameplayActor.cs:3167)
           → resolved unfinished frame ObjId → Interact(frameObjId, 18553)
ENGINE     HousingManager.Build (HousingManager.cs:612) + HousingPlacementValidator
           Character.UseSkill → CraftEffect → House.AddBuildAction
OBSERVED   live housing scan (CurrentStep → -1), frame ObjId, LP delta, bag deltas
ASSERT     house row byte-equal across a real restart; LP delta == canonical charge;
           design 1→0 exactly once; frame resolver returns 0 for a frame-less bot
TRACE      per-action audit records + the loop report's leg table
```
**Forbidden:** the bare `Interact(skill)` stand-in (deleted for this variant per
`step8-bot-loop-dossier`), and any in-run fixture repair.

### Example 3 — Economy: craft → load → drive → sell → mail

```
SETUP      clean reset; labor pool + seed money recorded [setup:true]
           GrowthRate raised for the crop leg (FAST_SIM)  [setup:true, recorded verbatim]
BOT INTENT arbiter activity (hauler/crafter) → composer cycle
EXECUTION  Harvest(cropObjId) → Craft(recipeId, benchObjId) → PackPickup(packObjId)
           → BoardVehicle(slaveObjId, Driver) → LoadPackOntoVehicle(slaveObjId, packObjId)
           → DriveVehicle(slaveObjId, dest) → UnboardVehicle → SellSpecialty(traderObjId)
           → [mail take] DepositMoney(amount)
ENGINE     Doodad.Use / Character.Craft.Craft (CharacterCraft.cs:44)
           RecoverItem.Execute ← CSLootOpenBagPacket; SlaveManager.BindSlave (:167)
           PackVehicleService.TryLoadCarriedPack; SpecialtyManager.SellSpecialty (:247)
OBSERVED   pack instance consumed exactly once; labor Δ == −60; mail payout row;
           vehicle attachment row (cargo point, plant_time unrewritten)
ASSERT     conservation law: bankΔ == payout − deposit; no duplication across kill-9
TRACE      audit records + `mails` read + MySQL ledger snapshot
```
**Forbidden:** `drive/stock`-style supply grants on a tested leg; the mail-take leg is
**MISSING** from the capability API (§3.5) — that is the concrete gap.

---

## 10. Gap analysis in dependency order

Ordered so each step's prerequisite is already satisfied.

| # | Gap | Blocks | Depends on | Evidence it is open |
|---|---|---|---|---|
| 1 | **No typed action-result model.** `ActorRequest.Result` is `object?` with 7 payload shapes. | consistent assertions, MCP/JSON schema stability | — | `ActorRequest.cs:71-72`; MCP tools return untyped `result` |
| 2 | **No `wait`/`wait-for` primitive.** ~20 ad-hoc poll helpers in E2E; one blocking server-side poll in the bridge. | every async leg (craft, putdown, maturation, restart-resume) | 1 | `BotDriveBridge.cs:4151-4154`; poll helpers across `AAEmu.IntegrationTests/E2e/*` |
| 3 | **No single trace contract.** 4 sinks, 3 schemas; `PlayerTraceService` armed only by `/trace`. | "did the bot act through the real path" answerable uniformly | 1 | `ActorAuditRecord.ToJson` vs `PlayerTraceRecord` vs bridge inline vs queue snapshots |
| 4 | **Harness can inject gameplay** (20 ops). | any capability claim | — | `BotDriveBridge.cs:727-786`, `:825-833` |
| 5 | **Fixture layer reachable by production reads** (§5 P5). | capability honesty | 4 | `BotMemory.cs:74-136` + 9 provider reads + 8 action reads |
| 6 | **Stand-in targets in GOAP actions.** | homestead/farm capability claims | — | `FarmingActions.cs:247`, `HomesteadActions.cs:421`, `:535` |
| 7 | **16 actor capabilities unreachable from the audited plane.** | party/expedition/auction/house/cast-at/auto-attack scenarios via the audited path | 3 | §3.8 verified count |
| 8 | **Bridge uses throwaway actors off-boundary.** | single-writer + audit for bridge-driven runs | 4 | 19 `new GameplayActor(...)` sites; `ServeClientAsync` `Task.Run` at `:233` |
| 9 | **Mail take/list/send missing from the capability API.** | M4 "mail payout → deposit" leg | 1 | `IGameplayActor.cs` has no mail member; `MailS3RestartE2eTests` proves the wire path |
| 10 | **Movement fidelity unobservable on the bot path** (`BroadcastMovement=false`). | M5/M5.3 movement regrade | 2, 3 | `BotRoamStepExecutor.cs:504-506`; `GameplayActor.cs:4273-4281` |
| 11 | **GCD/combat executor not wired into the live cast path.** | combat-timing claims | — | `CombatExecutor.cs` has zero production callers; `GameplayActor.cs:571-574` defers |
| 12 | **No live driver for the perception/quest loop.** | M1/M2 bot conversion | 4, 7 | `leveling-loop-perception` in 0 integration tests; `LevelingLoopScenarioRigTests` is a rig |
| 13 | **Dead executors / unwired seams.** | clarity, not capability | — | `GameplayActorStepExecutor.cs:30`, `GoapBotStepExecutor.cs:15`, `UnwiredBotStepExecutor.cs:15`, `UnwiredPlayerBotLifecycleService.cs:17` |
| 14 | **`PlanTemplateCache` ignores resources.** | planner determinism | — | `PlanTemplateCache.cs:14`, `GoapPlanRunner.cs:309/344` |

---

## 11. Migration plan (phased, each phase shippable and reversible)

**Phase 0 — freeze the vocabulary (no behaviour change).**
- Land the facet interfaces as *additive* interfaces that `IGameplayActor` inherits; no
  implementation change, no caller change. `[INFERENCE]` this is mechanically safe: the
  members already group cleanly (movement `:81-106`; targeting/combat `:113-150`;
  interaction/loot/item `:162-199`; quest `:635-788`; inventory/economy `:591-620`,
  `:805-861`; production `:428-582`; social `:218-343`; transit `:354-532`).

**Phase 1 — typed results + the action-result contract.** Replace `object? Result` with a
discriminated result; introduce one `ActionOutcome { State, Reason?, ObservedChanges[] }`
where `ObservedChanges` is the same shape `TalkResult`/`InteractWithResult` already carry
(`ActorRequest.cs:60-80`). Keep the audit record's additive-field discipline
(`ActorAuditRecord.cs` doc: names never change, renames need a version bump).

**Phase 2 — the wait primitive.** Add `WaitFor(condition, timeout)` to the control layer
(not the capability layer): it polls `Observe()`/engine reads on the execution boundary
until the condition holds or the budget expires, and emits a trace row. Replaces the ~20
ad-hoc helpers and the bridge's blocking `farm/needs` poll.

**Phase 3 — route bridge gameplay ops through the audited plane.** For each of the ~42
gameplay-driving bridge ops, either (a) map it to a `BotActionSpec` executed by
`BotActionCommandQueue` (getting the real actor, the ledger, the audit record), or
(b) delete it. This kills §5 P3 and §5 P4 together.

**Phase 4 — quarantine fixture ops.** Mark all 14 privileged ops `setup:true`; move the
event-injection ops behind a `simulate:` namespace that stamps the run `is_synthetic:true`
and is **refused** when the run declares a non-synthetic capability claim. Make
`BotMemory.EnableFixtureOverrides` + the `*Override` setters `internal` to the test
assembly.

**Phase 5 — close the capability gaps that the model needs.** In order: mail take/list
(#9), unload pack, trade cancel, party leave/kick (engine path first), auction cancel.
Each is real gameplay breadth; none is new semantics.

**Phase 6 — observation completeness.** Publish the motion series (velocity/heading per
sample) from the roam executor while keeping the throttle, and give
`PlayerTraceService` a programmatic arming path so the player-observable stream is
available to scenarios without a GM command.

**Phase 7 — delete dead weight.** `GameplayActorStepExecutor`, `GoapBotStepExecutor`,
`UnwiredBotStepExecutor`, `UnwiredPlayerBotLifecycleService`, `NavigateRoutedForTest`
(moved behind the fixture boundary), and the bridge's throwaway-actor helpers.

**Phase 8 — converge the transports.** Make `/api/actors/*` + the MCP sidecar the single
control surface; keep `BotDriveBridge` only as an explicitly-deprecated debug alias (or
remove it once Phase 3 has migrated its consumers). Reconcile with the 44-tool MCP surface,
which is already 1:1 with the REST routes.

**KEEP / EXTEND / CONVERGE / REPLACE / REMOVE / UNKNOWN (Phase 9 roll-up):**

| Subsystem | Verdict | Basis |
|---|---|---|
| `IGameplayActor` | **KEEP + EXTEND** | 55 actions, real engine paths, lifecycle, idempotency, boundary discipline; add facets, typed results, missing gameplay verbs |
| `PlayerBotRuntime` / `PlayerBotManager` | **KEEP** | registry/lifecycle are clean and already delegate embodiment to the shared lifecycle service |
| `PlayerBotScheduler` | **KEEP** | due-time + lease + boundary marshal is the right shape; fix only the "not auto-started" operational note |
| GOAP (`GoapPlanner`/`GoapPlanRunner`/`GoalArbitrator`/`BotWorldStateProvider`) | **KEEP + EXTEND** | real planner with resource-aware search; fix stand-in actions (#6), override reachability (#5), cache key (#14) |
| `BotWorldStateProvider` | **CONVERGE** | projection is honest but overlaps `Observe()`; the 10 never-written flags should be either implemented or removed |
| `Activity modules` (9) | **KEEP** | the arbitration seam is the right intent layer |
| `BotActionCommandQueue` + `/api/actors` | **KEEP → become the canonical control API** | the only surface with the designed properties |
| MCP sidecar (44 tools) | **KEEP** | exact 1:1 proxy, no invented semantics |
| `BotDriveBridge` | **CONVERGE → REMOVE** | broadest surface, least discipline; migrate to the queue then retire |
| `BotControlController` + `/bot` subcommands (`BotAdminService`) | **KEEP** | management, not gameplay; already one core two frontends |
| `PlayerBotController` | **REPLACE (narrow to a fixture adapter)** | event-injection surface; keep only as the injection primitive *inside* the labelled `simulate:` namespace |
| `PlayerTraceService` | **EXTEND** | good schema and hook coverage; needs programmatic arming |
| `PlayerBotAuditSink` / `playerbot_audit` | **KEEP** | correct boundary discipline (never on the loop tick) |
| `ExecutionBoundary` | **KEEP** | the strongest structural guarantee in the codebase |
| `GameplayActorTestRig` / `HeadlessSession.Create` | **KEEP (fixture-only)** | already labelled; keep out of every capability path |
| Scenario composers (`*Scenario`, `*Cycle`) | **KEEP + re-label** | several are unexercised by any integration test (verified: `Hauler*Cycle` 0, `Crafter*Cycle` 0, `FarmerCycleScenario` 0, `ExpeditionFormationScenario` 0, `TradeHandshakeScenario` 0 integration references) |
| `GameplayActorStepExecutor`, `GoapBotStepExecutor`, `Unwired*` | **REMOVE** | dead code |
| `CombatExecutor` | **UNKNOWN → wire or remove** | real gate fields, zero production callers; either wire into the cast path or drop the claim |

---

## The Final Question

*Reconstructed from the brief's Phase 4 (no verbatim "Final Question" line was supplied in
this task; the following is the question Phases 4–7 jointly converge on, and is answered
rather than deferred.)*

**Question:** should the player-bot surface be one API or two; if two, where exactly is the
line, and what may never cross it?

**Answer.**

**Two layers, split by truth obligation rather than by caller convenience.**

- **Gameplay Capability API** = `IGameplayActor` and its facets: validated, single-writer,
  idempotency-keyed, execution-boundary-pinned requests that reach the engine only through
  paths a human client also uses. Its admission test is: *could a bot with nothing but
  observation and this API have caused this state?* If no, the member is not a capability.

- **Test/Control API** = identity (spawn/remove/list/status), assignment (activity, route,
  cadence), observation, **wait**, **trace**, and privileged fixtures — every one of which
  is either read-only, or marked `setup:true`, or marked `is_synthetic:true`.

**What must never cross the line (the load-bearing list):**

1. **Progress-event injection** — `drive/kill`, `killGroup`, `gather`, `useItem`, `talk`,
   `interact`, `enterSphere`, `express`, `levelUp`, `aggro`, `zoneKill`,
   `cinemaStarted/Ended` (`BotDriveBridge.cs:727-765`). These make the harness the actor.
2. **Fixture grants that substitute for a tested step** — `drive/setLevel` (`:831-833`),
   `drive/stock` (`:828-830`), `*/rig` ops, `homestead/kit` **when the test claims ordinary
   acquisition** (`:3637-3652`).
3. **Positional staging presented as travel** — `drive/teleportToNpc` (`:787-813`),
   `homestead/move` (`:3822-3848`), `farm/place` (`:3976-4017`).
4. **The fixture memory override layer** — `BotMemory.*Override` (`BotMemory.cs:84-136`)
   and its nine provider reads plus eight action reads; and `EnableFixtureOverrides`
   (`:81`) must stay unreachable from production code.
5. **Stand-in targets inside production actions** — `Harvest(0)`
   (`FarmingActions.cs:247`), `Interact(2001)` (`HomesteadActions.cs:421`),
   `Interact(101)` (`:535`).
6. **Manual reward/packet authoring** — the `SCSkillStartedPacket`/`SCSkillFiredPacket`/
   `SCSkillEndedPacket` + manual `ChangeLabor` block in `Harvest`
   (`GameplayActor.cs:3543-3596`); observer packets must be engine emissions.
7. **Rig actors addressable as bots** — `HeadlessSession.Create` (`:492-516`) and
   `GameplayActorTestRig.CreateActor` (`:713-782`).
8. **Test-only seams on the capability interface** — `NavigateRoutedForTest`
   (`GameplayActor.cs:429`).

Everything else in the current surface either belongs to one of the two layers already, or
is simply missing and should be added as real gameplay (mail take/list, trade cancel,
party leave/kick, pack unload, auction cancel).

**One-sentence closing position:** the capability layer is already ~80 % built and is the
part of this codebase that behaves correctly; the work is not to invent a new API but to
**stop the test layer from doing gameplay**, finish the missing gameplay verbs, and give
both layers one observation/wait/trace contract.

---

## Sources read for this document (all read directly at `d0bea9e6a` unless marked)

- `AAEmu.Game/Core/Managers/Bots/`: `IGameplayActor.cs`, `GameplayActor.cs`,
  `ActorRequest.cs`, `ActorAuditRecord.cs`, `ActorObservation.cs`, `ActorIdempotency.cs`,
  `IPlayerBotManager.cs`, `PlayerBotManager.cs`, `PlayerBotRuntime.cs`,
  `IPlayerBotScheduler.cs`, `PlayerBotScheduler.cs`, `PlayerBotSchedulerOptions.cs`,
  `IBotStepExecutor.cs`, `GameplayActorStepExecutor.cs`, `BotGoalArbiterStepExecutor.cs`,
  `BotGoalArbiter.cs`, `BotRoamStepExecutor.cs`, `IBotActivityModule.cs`,
  `HomesteadActivityModule.cs`, `QuestBootstrapActivityModule.cs`, `BotActionCommandQueue.cs`,
  `PlayerBotAuditSink.cs`, `PlayerTraceService.cs`, `ExecutionBoundary.cs`,
  `BotAdminService.cs`, `PlayerBotLifecycleAdapter.cs`, `IPlayerBotLifecycleService.cs`,
  `BotScenarioRunner.cs`, `PlayerBotControllerAdapter.cs`
- `AAEmu.Game/Core/Managers/Bots/Goap/`: `BotWorldStateProvider.cs`, `BotWorldState.cs`,
  `BotMemory.cs`, `GoapPlanRunner.cs`, `PlanTemplateCache.cs`, `GoalArbitrator.cs`,
  `Actions/HomesteadActions.cs`, `Actions/FarmingActions.cs`, `Actions/RecoveryActions.cs`,
  `Actions/CombatActions.cs`, `CombatExecutor.cs`
- `AAEmu.Game/Models/Game/Bots/`: `BotDriveBridge.cs`, `HeadlessSession.cs`,
  `PlayerBotController.cs`, `BotScenarioTemplate.cs`
- `AAEmu.Game/Services/WebApi/Controllers/`: `BotActionController.cs`,
  `BotControlController.cs`, `BotControlSettings.cs`
- `AAEmu.Game/Models/Game/Char/`: `CharacterQuests.cs`, `CharacterCraft.cs`, `Inventory.cs`,
  `Character.cs` (targeted)
- `AAEmu.Game/Core/Managers/`: `QuestManagerEvents.cs`, `HousingManager.cs`,
  `SpecialtyManager.cs`, `SlaveManager.cs`, `TeamManager.cs`, `TradeManager.cs`,
  `AuctionManager.cs`, `World/PackVehicleService.cs`
- `AAEmu.Game/Models/Game/`: `Quests/NewQuestCode.cs`, `DoodadObj/Doodad.cs`, `Units/Unit.cs`,
  `Skills/Skill.cs`
- `AAEmu.Game/Core/Packets/C2G/`: `CSStartSkillPacket.cs`, `CSMoveUnitPacket.cs`
- `AAEmu.Game/Program.cs`, `AAEmu.BotControlMcp/ActionMcpServer.cs`
- `AAEmu.IntegrationTests/E2e/`: `E2eStack.cs`, `BotNetworkSession.cs`, `BotDriveClient.cs`,
  `BotTcpLink.cs`, `E2eQuestDriver.cs`, `BotControlApiE2eTests.cs`, `QuestDiscoveryE2eTests.cs`
- Prior artifacts (verified, not re-derived): `/tmp/actor_audit.md`,
  `/tmp/scenario_conversion.md`, `/tmp/e1_inventory.md`, `/tmp/step1_plan.md`,
  `/tmp/step3_inventory.md`, `/tmp/step4_inventory.md`, `/tmp/step5_inventory.md`,
  `/tmp/step6_inventory.md`, `/tmp/step7_inventory.md`, `/tmp/step8_inventory.md`,
  `scorecard-explorations/mechanics/step8-bot-loop-dossier-2026-09-17.md`,
  `wave1-actor-audit-2026-09-18.md`, `wave1-scenario-conversion-2026-09-18.md`
