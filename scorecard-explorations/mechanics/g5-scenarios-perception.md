# G5 Scenarios & Perception — pursuit to engagement range (READ-ONLY audit, develop @ 2026-09-20)

READ-ONLY. Nothing modified, built, or run. G3/G4 frozen — untouched.
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
G5 mission: from 251-ACTIVE with 3475 already selected (G4 output), approach to
engagement range and stop before attack. Ends at in-range + stopped. NO cast,
NO AutoAttack, NO loot, NO credit, NO turn-in.
Read-first context: `g4-perception-targets.md` (§D resolver sites incl.
SelectHuntTarget/AdventurerSpike, §G duplications),
`capability-surface-matrix.md` (Target/Cast verbs, bridge bypass list).

## 1. SCENARIO PURSUIT — shape (reusable primitive) vs production ownership

### 1.1 Pursuit SHAPE — demonstrated in four places, none of them G5-owned

| Site | Shape | Cite |
|---|---|---|
| Roam wildlife hunt, live leg | per-wake: validate pinned target → `dist > engageRange` → move; else stop + face + cast | `AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:766-832` |
| Roam PvP loop | same shape on `Character` victims: `MoveToUnit(target.ObjId, HuntChaseSpeed, 10s)` when `dist > engageRange`, `Stop` + face when inside | `BotRoamStepExecutor.cs:2394-2414` |
| Adventurer spike `MaintainRange` | band check → `MoveToUnit` (melee) or band-point `MoveTo` (ranged) → `runtime.Drive` to terminal → re-observe next round | `AAEmu.Game/Core/Managers/Bots/AdventurerSpikeScenario.cs:870-907` |
| GOAP `ApproachTargetAction` | `MoveToUnit(CurrentTarget.ObjId)`; `EvaluateStatus`: Invalidated when target lost/dead, Succeeded when `TargetInRange` | `AAEmu.Game/Core/Managers/Bots/Goap/Actions/CombatActions.cs:44-78` |
| Leveling loop hunt legs | Observe → `SelectHuntTarget` → `NavigateToUnit(target.ObjId)` → `DriveRequest` to terminal → `CombatDecisionTree.Evaluate` | `AAEmu.Game/Core/Managers/Bots/LevelingLoopScenario.cs:1480-1484,1681-1683,2728-2730` |

The reusable primitive is: **validate → range-check → single move leg →
drive to terminal → re-observe**. Every site re-derives distance from live
positions each wake (`MathUtil.CalculateDistance`, flat) and never stores a
distance.

### 1.2 Production OWNERSHIP verdict — the roam executor owns live legs today

- **Owner of live pursuit legs: `BotRoamStepExecutor`.** It holds the only
  production per-target tracking state (`BotRoamState.TargetNpcObjId`
  `BotRoamStepExecutor.cs:234`, `TargetEngagedUtc :235`, `PendingLeg :222`),
  the only production revalidation predicate (`isDeadOrInvalid`: null / Hp<=0 /
  `!IsAttackableWildlife` / 30 s cap, `:719-722`), the only move-suppression
  rule (no new leg while a Move is `ActiveRequest` unless destination drifted
  > 2.0 m, `:781-783`), and the only in-range stop (`PreemptCurrent("hunt in
  engage range")`, `:794-798`). Rig proof: scan→engage→cast→drop→resume-roam
  (`AAEmu.UnitTests/Game/Core/Managers/Bots/BotRoamStepExecutorTests.cs:220-281`).
- **Quest-blind caveat (G4 §G.8, still true):** the roam hunt acquires
  nearest-attackable-ANYTHING (`:835-875`, no template filter, no quest link).
  Reusing it verbatim for G5 kills foxes as happily as boars. G5 must pin the
  already-selected 3475 objId, never re-acquire by proximity.
- **Rig/fixture, NOT owners:** `AdventurerSpikeScenario.SelectHostile`
  (`AdventurerSpikeScenario.cs:767-796`) and `LevelingLoopScenario.SelectHuntTarget`
  (`LevelingLoopScenario.cs:2842-2893`) are scenario-local selectors (rig-driven,
  G4 §F.2 verdict: REUSABLE-WITH-CLEANUP shape only). `MaintainRange`
  (`:870-907`) is the same — proven live behavior inside a scenario harness,
  not a production leg. `ApproachTargetAction.CreateActorRequest`
  (`CombatActions.cs:53-58`) composes `MoveToUnit` correctly, but its sibling
  `AcquireHostileTargetAction` dispatches `SetTarget(0)` which always rejects
  (`GameplayActor.cs:4753-4760`, G4 §G.7 stub) — the GOAP acquire path must
  never be cited as selection ownership. `GoapActionLibraryTests` hunt-plan
  shape (`AAEmu.UnitTests/.../Goap/GoapActionLibraryTests.cs:78-93`) asserts
  plan ORDER only, not movement.
- **Move tests prove the primitive, not pursuit:**
  `GameplayActorTests.cs:442-450` (unknown unit → Rejected),
  `:619-633` (MoveToUnit arrives at unit position),
  `GameplayActorNavigateTests.cs:111-156` (NavigateToUnit reject/already-there/
  direct). They exercise snapshot-position legs, never retracking.

**Verdict:** pursuit SHAPE is proven and reusable (roam live leg + spike band
+ GOAP approach + loop close-in all converge on validate→move→stop-in-range);
production OWNERSHIP of quest-pinned pursuit does not exist yet — the roam
executor owns quest-blind pursuit, G4 owns quest-valid selection, and G5 must
bridge them without creating a second hunter.

## 2. PERCEPTION FOR PURSUIT — what the snapshot gives vs what G5 needs

### 2.1 Snapshot contents (unchanged since G4 §D.1)

`ActorObservation` (`AAEmu.Game/Core/Managers/Bots/ActorObservation.cs:22-91`):
`ActorId :24`, `Position :26` (actor only), `CurrentTargetObjId :29`
(0 = none), Hp/Mp :31-37, bare-uint nearby lists :40-46 (25 m
`WorldManager.GetAround`, `GameplayActor.cs:262-264`), `ActiveQuestIds :49`,
economy :52-67, party :70-85. `BotObservedContext`
(`BotDecisionProposal.cs:16-110`) is a 1:1 copy (`From :67-96`).

### 2.2 Per-field verdict for G5 (selected 3475 objId known from G4 funnel)

| Needed | In snapshot? | Where it actually lives |
|---|---|---|
| selected-target ObjId | YES (`CurrentTargetObjId :29`, G4 funnel `SelectedObjId`, `QuestObjectiveTargetSelector.cs:227`) | snapshot + funnel |
| target position | NO | live `npc.Transform.World.Position` at leg time (every site does this) |
| target template (3475 confirm) | NO | live `ParentWorld.GetNpc(objId).TemplateId` (`BaseUnit.cs:20`) |
| target alive | NO | live `npc.Hp <= 0` (`Unit.cs:77-80`; `IsDead :946-949`) |
| target visibility (stealth) | NO | `character.CanSeeTarget(npc)` (only survey applies it today, `BotSurveySenses.cs:247-269`) |
| hostility of pinned target | NO | `CombatDecisionTree.IsHostileTarget` (`CombatDecisionTree.cs:73-102`) / `BaseUnit.CanAttack` (`BaseUnit.cs:54-138`) |
| distance | NO | derived flat `MathUtil.CalculateDistance` per wake (all sites) |
| **velocity** | **NO — explicitly absent** | nowhere on `Unit`/`Npc`: velocity exists only on ships/gimmicks/transfers/network move types (`Slave.cs:60`, `Gimmick.cs:247`, `Transfer.cs:40`, `MoveType.cs:31`), never on NPC locomotion. No `Vel`/`IsMoving` on `Unit.cs`/`Npc.cs` found |
| movement-state (moving/stationary) | NO | UNKNOWN as a readable flag — NPC speed is derived (`Npc.BaseMoveSpeed`, `Npc.cs:87-109`, stance/AI-driven), not reported; observers infer motion from successive positions |
| world/zone of target | NO | live `Transform.ZoneId` (G4 §D.1 gap, `brain-boundaries.md` §2.3 item 1) |
| actor position | YES (`Position :26`) | snapshot |

### 2.3 Minimum missing fields for G5 (strengthen, no new projection)

G5 needs per wake: **(selected objId [have], live target position, alive,
visible, attackable, flat distance)** + actor position [have]. Recommendation
(prefer existing surfaces, G4 §D.3 pattern):

1. **No snapshot schema change.** G5 consumes the G4 funnel's `SelectedObjId`
   + `BotObservedContext.CurrentTargetObjId` as the pin, and re-resolves the
   five live fields via `ParentWorld.GetNpc` at leg time — exactly what
   `QuestObjectiveTargetSelector.Evaluate` already does per candidate
   (`QuestObjectiveTargetSelector.cs:142-204`).
2. **Velocity: do NOT add.** Flagged explicitly absent — and correctly so:
   every production site pursues with **snapshot-position legs + per-wake
   re-issue** (roam `:781-789` drift > 2 m check; spike re-observe next round
   `:867-868`), never with interception math. A moving boar is handled by
   re-issuing `MoveToUnit` to its refreshed position, not by predicting it.
3. **Refreshed position comes from re-resolve, not from the snapshot.**
   `MoveToUnit`/`NavigateToUnit` capture `unit.Transform.World.Position` once
   at request time (`GameplayActor.cs:491-494`, `:351-355`); the refresh loop
   lives in the caller (G5 owner), not the actor.

Top-3 perception gaps for G5: (1) no target position/velocity/movement-state
in snapshot — re-resolve live per wake; (2) no per-target alive/visible/
hostile in snapshot — revalidate with the G4 predicate chain; (3) staged
position problem persists from G4 §4.2: giver-doorstep sees nothing within
25 m (nearest boar ~60 m), so G5 starts only after staging inside Observe
range (see §8 fixture).

## 3. OWNERSHIP OPTIONS — evaluated against actual code

Principle: quest owns WHY (which objId), movement owns HOW (legs, stop,
re-issue). No new brain, no new scheduler.

| Option | Verdict |
|---|---|
| A. `QuestBehavior` → new `PursueTarget` proposal | **PICK (smallest cut).** `QuestBehavior` already owns the 251 per-wake path: `Evaluate` once per wake (`QuestBehavior.cs:95-106`), `ObjectiveTargetProposal` → Target proposal prio 25 (`:405-431`, `ObjectiveTargetPriority = 25`, `QuestDecisionScenario.cs:53`), dispatch via `SetTarget` only (`:480-482`). A pursuit proposal for the SAME selected objId (Move action, priority just below the Target proposal so selection wins first) keeps WHY in one file, rides `BotDecisionSelector`/`BotDecisionCycle`, and dispatches existing actor verbs only. |
| B. `QuestObjectiveBehavior` → `PursuitService` | **REJECT — neither exists.** Grep over `AAEmu.Game`, `AAEmu.UnitTests`, `AAEmu.IntegrationTests` finds `QuestObjectiveTargetSelector` (the G4 owner) but no `QuestObjectiveBehavior` class and no `PursuitService` class. B would invent the brain it claims to reuse. |
| C. Combat-behavior approach (`ApproachTargetAction` / `CombatDecisionTree`) | **REJECT as owner; REUSE as shape.** `ApproachTargetAction` (`CombatActions.cs:44-78`) already expresses approach→`TargetInRange` with Invalidated/Succeeded semantics, and `TargetEngagementRange = DefaultMeleeMax` (`BotWorldStateProvider.cs:37`) names the range. But GOAP acquire is a stub (§1.2), GOAP is not on the quest-leg path (quest leg runs `QuestBehavior`, `BotRoamStepExecutor.cs:672-687`), and combat owns attack — G5 must stop BEFORE attack. Quote the range constant, do not route through combat. |
| D. Existing hunt owner (roam executor) grows a quest-pin | **RUNNER-UP, not pick.** The roam leg owns every live-leg mechanism (§1.2) and its `needsMove`/`PreemptCurrent` discipline is exactly G5's. But its acquisition is quest-blind by design and it runs only when `!QuestLegActive` (`BotRoamStepExecutor.cs:711`) — quest work preempts it. Threading a 251-pin through the hunt loop inverts the arbitration (quest leg would have to reach into the hunt branch it preempts). Keep the hunt loop untouched; let the quest leg issue its own Move legs while it is active. |

**Pick: A.** QuestBehavior proposes pursuit of its own selected target;
GameplayActor executes snapshot legs; the existing lifecycle + audit prove it.

## 4. ACTION BOUNDARY — MoveToUnit vs NavigateToUnit vs PursueTarget

- `MoveToUnit(objId, speed, timeout, key)` (`GameplayActor.cs:482-495`):
  resolve-once (`ResolveUnit :4753-4760`, null → `RejectedAction`), then
  straight-line `StartMove` to the captured position. Arrival ⇔ flat ≤ 0.5 m
  AND |ΔZ| ≤ 0.5 m (`:420-421`, `ArrivalRadius :75`); already-there → full-
  lifecycle no-op `Completed("already at destination")` (`:427-428`); budget
  default 30 s (`DefaultMoveTimeout :78`), expiry → `TimedOut(Navigation)`
  (`ActorTimeoutPolicy.cs:4779-4783`); stuck (no displacement beyond
  `ArrivalRadius` in window) fails fast (`:123-126`).
- `NavigateToUnit(objId, speed, timeout, key)` (`:343-356`): same resolve-once,
  then navmesh A* + waypoint stepping with straight-line fallback
  (`NavigateToInternal :358-412`); queue-only, **no WebAPI/MCP route**
  (matrix §C gap 1). Same arrival/timeout semantics (same `ActorActionType.Move`).
- `PursueTarget`: **does not exist** (no class/method by that name in repo).
  No new public API is needed: G5 is expressible as **maintain-pursuit =
  repeated `MoveToUnit(selectedObjId)` legs with per-wake revalidation**,
  driven to terminal via the scenario `Drive` pump (spike precedent
  `:898-899`, loop `DriveRequest` precedent `:1480-1481`).

Move-to-unit-once vs maintain-pursuit (document distinctly):

- **Move-to-unit-once** (static target): one `MoveToUnit` leg → drive to
  `Completed("arrived")` → assert post-state within the 0.5 m box (matrix §E
  handoff). Sufficient when the boar never moves.
- **Maintain-pursuit** (moving target): per-wake loop — re-resolve position,
  revalidate (alive/visible/attackable), `dist > engageRange` → (re-)issue
  `MoveToUnit` only when no Move is running OR destination drifted > 2.0 m
  (roam `:781-783` rule, prevents leg spam); `dist <= engageRange` →
  `PreemptCurrent`/`Stop` + clear pending (roam `:794-798`). The actor leg
  stays snapshot-only; **retracking lives in the caller loop**, which is why
  §8 splits G5A (static) from G5B (moving) only if code proves retracking
  absent — it does NOT: roam `:766-791` + spike re-observe `:867-868` prove
  retracking exists as caller discipline. **No G5A/B code split needed on
  retracking grounds** (split only as test staging convenience, see §8).

G5 default: `MoveToUnit` (externally reachable, matrix A1). `NavigateToUnit`
only if the lane needs navmesh routing (then note its queue-only gap).

## 5. STATE MACHINE — smallest state recommendation

Existing actor lifecycle covers the leg: `Requested → Accepted → Running →
Completed | Rejected | Interrupted | TimedOut` (`ActorRequest.cs:9-13`;
`ActorActionType.Move`, failure taxonomy §17: `Navigation` for move timeout,
else `Starvation`, `IGameplayActor.cs:1107-1135`). No per-target tracking
exists in `QuestBehavior` today — the G4 funnel is a pure per-wake function
(never stores, `QuestObjectiveTargetSelector.cs:110-116`), and `BotObservedContext`
carries no pursuit memory.

Smallest state: **no new stored machine; derive G5 phase per wake from
(snapshot + live re-resolve + actor leg state):**

- `Selected` — funnel `SelectedObjId != 0` AND `CurrentTargetObjId ==
  SelectedObjId` (G4 postcondition, `QuestBehavior.cs:418-420`).
- `Approaching` — selected AND `dist > engageRange` AND Move leg Running.
- `InRange` — selected AND `dist <= engageRange` (terminal for G5; movement
  stopped, no attack issued).
- `TargetLost` — `GetNpc(selected) == null` (resolve fail, cf. roam `:719`).
- `Invalid` — `Hp <= 0` OR `!IsHostileTarget` OR `!CanAttack` OR
  `!CanSeeTarget` (the G4 reject chain, `QuestObjectiveTargetSelector.cs:169-198`).
- `Unreachable` — leg `Rejected`/`TimedOut(Navigation)` (roam route-advance
  precedent `:997-1003`: advance past the point, fail the leg honestly).
- `TimedOut` — revalidation 30 s engagement cap precedent (roam `:722`)
  or leg budget expiry (30 s default).

Per-target stored tracking (`TargetNpcObjId`-style pin inside QuestBehavior)
is NOT recommended: the pin already exists as `CurrentTargetObjId` +
funnel `SelectedObjId`, and stored flags rot (nearby-quest §2.3 item 4 —
never stored flags). If a later moving-target lane wants hysteresis, add a
single `lastPursuedObjId + lastLegDestination` pair beside the selector —
not a machine.

## 6. DETERMINISM — commitment semantics

Current code commitment rules (what the code does about thrash):

- **Roam hunt commits until invalid:** the pinned `TargetNpcObjId` persists
  across wakes; re-selection (scan) runs ONLY when pin == 0 AND scan interval
  elapsed (`:835-840`); invalidation is the four-clause `isDeadOrInvalid`
  (`:719-722`: null/dead/unattackable/30 s). No nearest-better preemption
  while pinned — a closer boar does NOT steal the engagement.
- **Leg spam is suppressed:** new `MoveTo` only when no Move is Running OR
  destination drifted > 2.0 m (`:781-783`); same rule in follow (`:602-604`)
  and butcher (`:916-918`) legs.
- **Scenarios bound re-observation:** spike re-observes next round bounded by
  attempts (`:867-868`); loop-level `excluded` sets prevent immediate
  re-engagement (`SelectHuntTarget :2842-2844`, `SelectHostile :767-768`
  `excludedObjIds`); spike failures exhaust via `Starvation`
  (`AdventurerSpikeScenario.cs:609-612`).
- **Selection itself is deterministic:** nearest-first flat + ObjId tiebreak
  (selector `:213-219`; QuestBehavior discovery `:165-175`; G3 doctrine).

G5 commitment recommendation (mirror roam): **select once (G4), commit to the
pinned objId; re-run selection ONLY on** `TargetLost` (unresolvable),
`Invalid` (dead/hostile/attack/stealth flip), `Unreachable`
(leg Rejected/TimedOut-Navigation), or `TimedOut` (30 s cap). Never re-select
because a nearer candidate appeared. On re-select, exclude the failed objId
for the wake (scenario `excluded` precedent).

## 7. FAILURE TAXONOMY — predicate-named classes mapped to actual code

Actor reasons are §17 only (`IGameplayActor.cs:1111-1135`): `WrongDecision`,
`Navigation`, `RejectedAction`, `StateTransition`, `Persistence`,
`Starvation`, `FidelityError`. G5 predicate classes map:

| G5 predicate | Meaning | Actual code term |
|---|---|---|
| `TARGET.unresolved` | selected objId not in world | `RejectedAction("target unit not found")` (`GameplayActor.cs:491-493`); funnel `rejectUnresolved` (`QuestObjectiveTargetSelector.cs:147-149`) |
| `TARGET.dead` | boar Hp<=0 | roam `isDeadOrInvalid` `:719-720`; funnel `rejectDead` / `"dead"` (`:169-173`); GOAP `TargetDead` (`BotWorldStateProvider.cs:216-221`) |
| `TARGET.not-hostile` | fails hostility | `IsHostileTarget == false` (`CombatDecisionTree.cs:73-102`); funnel `"hostile"` (`:181-186`) |
| `TARGET.not-attackable` | fails engine legality | `!CanAttack` (`BaseUnit.cs:54-138`); funnel `"attack"` (`:187-192`) |
| `TARGET.not-visible` | stealth / cannot see | `!CanSeeTarget`; funnel `"stealth"` (`:193-198`) |
| `TARGET.wrong-template` | not 3475 (relevance) | funnel `"template"` (`:175-179`); `IsRelevant` (`:101-108`) false |
| `PURSUIT.superseded` | closer candidate exists — NOT a failure | nearest-first ordering is load-bearing (G4 §G.10); commitment says ignore while pinned (§6) |
| `PURSUIT.stale-leg` | leg destination drifted | drift > 2.0 m → re-issue (roam `:781-783`); else keep Running leg |
| `RANGE.in-range` | success terminal | `dist <= engageRange` → stop (`:794-798`); GOAP `TargetInRange` (`BotWorldStateProvider.cs:209-214`, `TargetEngagementRange :37` = `DefaultMeleeMax :59` = 3.5 m; roam melee `HuntMeleeRange = 3.0 m :119`; ranged 15.0 m `:776`) |
| `RANGE.out-of-range` | keep approaching | `dist > engageRange` → move (`:778-791`) |
| `HARNESS.busy` | actor already Running | `Rejected(StateTransition, "busy")` (`GameplayActor.cs:46-48`); queue parity (`BotActionCommandQueue.cs:440-449`) |
| `HARNESS.timeout` | leg budget expiry | `TimedOut(Navigation)` for Move (`ActorTimeoutPolicy :4779-4783`); pump `Expire(Navigation)` idiom (scenario precedent) |
| `HARNESS.stuck` | no displacement | stuck detection (`GameplayActor.cs:123-126`) |
| `HARNESS.no-progress` | static target never reached | scenario `Starvation` on exhausted attempts (`AdventurerSpikeScenario.cs:609-612`) |

Engagement-range note: three constants coexist — `DefaultMeleeMax 3.5 m`
(`CombatDecisionTree.cs:59`, GOAP band), roam `HuntMeleeRange 3.0 m`
(`BotRoamStepExecutor.cs:119`), ranged 15 m (`:776`). G5 (melee stop before
attack) should cite ONE: `TargetEngagementRange` (3.5 m) as the success band,
with the 3.0 m roam value noted as the live-leg precedent.

## 8. FIXTURE + PASS SKETCH for G5

### Starting state

- 251 ACTIVE (Progress, `have(4058) < 3`), G4 funnel `SelectedObjId = 3475-objId`
  (e.g. pinned boar), `CurrentTargetObjId == SelectedObjId` via `SetTarget`.
- Bot staged **inside Observe range (≤ 25 m)** of the pinned boar but
  **outside engagement** (`dist > 3.5 m`; suggest 10–20 m to keep both legs
  observable). G4 §4.2 warning applies: giver-doorstep staging sees nothing —
  G5 must teleport/place first (fixture-staging ops, matrix §D
  Fixture-SAFE: `teleportToNpc`/`quest place` — setup only, never traversal
  proof).
- Boar alive, hostile (faction 115), `CanAttack` true, visible.

### START observe-only rules (no move/attack/pick)

1. `Observe` → capture `BotObservedContext`; run
   `QuestObjectiveTargetSelector.Evaluate` → record funnel
   (`CycleId`, `RawCount/RelevantCount/LegalCount`, `SelectedObjId`,
   `SelectedDistanceM`, reject counts, candidate rows) — the G4 lane pattern
   (`QuestBehavior.cs:100-101`, `LogObjectiveFunnel :441-464`).
2. Assert `SelectedObjId == pinned` and `CurrentTargetObjId == pinned`.
   No `Move/Cast/AutoAttack/Loot` during START.

### Funnel evidence fields (per wake)

`cycle, selected, template==3475, distM (live flat), alive(Hp>0),
hostile(IsHostileTarget), attackable(CanAttack), visible(CanSeeTarget),
actorPos, legState/legAction/legDestination, rangeBand(engageRange value)`.

### PASS shape (all must hold)

1. selected stays: `CurrentTargetObjId == SelectedObjId == pinned` every wake.
2. pursuit starts: first `dist > engageRange` wake issues
   `MoveToUnit(pinned)` (Running, `ActorActionType.Move`, destination ==
   boar position at issue ± drift).
3. distance decreases across wakes (monotone-nonincreasing within arrival
   slack; moving-target lane: leg re-issued only on > 2 m drift).
4. range reached: `dist <= engageRange` observed live.
5. movement stops: `Stop`/`PreemptCurrent("hunt in engage range")` then no
   Running Move; actor idle.
6. no attack: zero `Cast`/`AutoAttack`/`Loot` audit records for the run.

### Predicate FAILs

- `TARGET.*` on START or any wake → fail at PERCEIVE with the funnel reject
  token (`dead/hostile/attack/stealth/template/unresolved`).
- Leg `Rejected` at issue → `TARGET.unresolved` (fail-closed, never retry
  blindly).
- Leg `TimedOut(Navigation)` / stuck → `HARNESS.timeout`/`stuck`; static lane
  fails, moving lane may re-issue once then fail `HARNESS.no-progress`
  (`Starvation`).
- `CurrentTargetObjId != pinned` at any wake → `TARGET.superseded/lost` fail
  (commitment violated).
- Any `Cast`/`AutoAttack` audit record → attack-boundary fail (G5 ends at
  InRange).

### G5A-static vs G5B-moving split

**N (single lane suffices) on retracking grounds:** code proves retracking as
caller discipline (roam `:766-791` drift-reissue; spike re-observe `:867-868`;
§4). A static boar and a slow boar run the SAME loop; the static case is just
the drift-never-fires path. Split ONLY as staging convenience (static = pinned
boar held still; moving = boar patrolling) sharing one PASS shape — do not
split the implementation.

## 9. TRACES — human/engine tests showing select→target→kill→credit

Selection semantics only — no new trace campaign. (Bridge `drive kill` etc.
are synthetic event-fire, matrix §A3/B — never cite as kill evidence.)

- Q4 live hunt+loot (engine-true kill → loot): `AAEmu.IntegrationTests/E2e/Q4LiveHuntLootE2eTests.cs:35-42` (canonical triple 251/3475/4530/4058 constants), `:84-97` (hold 251 gate), `:99-108` (teleport-to-boar + pin live boar), `:110-115` + `:203-210` (real cast rotation until Hp 0, no synthetic credit).
- Adventurer spike E2E (select→target→kill→credit chain live): `AAEmu.IntegrationTests/E2e/AdventurerSpikeE2eTests.cs:28-30` (cast damage → `Npc.DoDie` → `DoOnMonsterHuntEvents` → step machine).
- Adventurer spike rig (Observe→target→cast→credit through contract, rig-faked kill documented): `AAEmu.UnitTests/.../AdventurerSpikeScenarioRigTests.cs:31-35` (REAL `DoOnMonsterHuntEvents` entry), `:149-184` (synthetic kill helper), `:221-224` (hunt×3 → loot → auto-complete), `:555-638` (standoff band legs: back-off / band-edge close-in shapes).
- Roam hunt live leg (engage→cast→drop→resume): `AAEmu.UnitTests/Game/Core/Managers/Bots/BotRoamStepExecutorTests.cs:220-281`.
- G4 selection lane (Observe→selector→Target proposal→SetTarget, ends at selected): `AAEmu.IntegrationTests/E2e/G4QuestObjectiveTargetTests.cs:17-21` (path doc), `:795-796` (call path: wake → arbiter → `StepQuestLeg` → `QuestBehavior.Run` → `Evaluate` → Target proposal → `BotDecisionCycle.Execute` → `SetTarget`).
- Movement primitive proofs: `GameplayActorTests.cs:442-450,619-633`, `GameplayActorNavigateTests.cs:111-156` (§1.2).
- GOAP plan-order (shape only): `GoapActionLibraryTests.cs:78-93` (Acquire → Approach → Combo → Loot).
