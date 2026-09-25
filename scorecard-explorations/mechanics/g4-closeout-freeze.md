# G4 Closeout Freeze — PASS-BEHAVIOR (2026-09-20, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (same lane as the G3 freeze).
This is a documentation/reconciliation/freeze record. Zero behavior change in this pass.

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-20.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g4-objective-target-gate-report.json`
  (`verdict=PASS`, `failBoundary=none`, charId=25).
- **PASS-BEHAVIOR chain fields (gate leg):** selected ObjId 44319, template 3475,
  distance 5.9 m, relevance true (have 0 / need 3), funnel raw=1 relevant=1
  legal=1, rejects all zero
  (`unresolved=0 dead=0 template=0 hostile=0 attack=0 stealth=0`),
  cycle `quest-25-639254850248501522`, `landedTarget=44319`,
  `landedTargetRetick=44319` (reselect stable), `meatPre=0 meatPost=0`
  (no combat, no credit), decide detail `[landed Target (targeting 44319)]`.
- **Code changes (read-only verification, this pass modifies nothing):**
  new production selector `AAEmu.Game/Core/Managers/Bots/QuestObjectiveTargetSelector.cs`
  (251-relevance wrapper + `IsHostileTarget` legality + `CanSeeTarget` +
  nearest-first flat + ObjId tiebreak, named constants 251/3475/4530/4058);
  `QuestBehavior.cs` per-active-quest 251 path (`Evaluate` → `ObjectiveTargetProposal`
  → Target proposal) with funnel logging; `QuestDecisionScenario.cs`
  `ObjectiveTargetPriority=25` (above advance 20 / accept 10, below turn-in 30);
  Target dispatch arm `ActorActionType.Target => gameplayActor.SetTarget(...)`
  (canonical actor verb only, never the roam direct-write bypass).
- **Fixture chain (legs, all passed):** enroll charId=25 with `activeBefore=False`;
  route-clear settled; vitals refilled (hp/mp fractions 1.000/1.000);
  quest-hold `accept251=True giverObjId=44322 active=True` (251 ACTIVE at START);
  boar-stage spawner 3372 flat 0.0 m with live 3475 = 44319; pre-start
  `snapActorGap=0.0m meat4058=0 active251=True`; wake per-bot step signal;
  gate PASS at boundary none.

## (b) Objective map + funnel

Quest **251** (`화난 멧돼지들`, LEVEL 2) → Progress component **633** →
ObjItemGather act **10473** (detail 616) → item **4058 ×3** → NPC template
**3475** (Solzreed Boar, sole drop source via loot pack **4530**) → gate claim:
from 251-ACTIVE the bot deterministically selects live 3475 ObjId 44319
(relevant AND legal) via `SetTarget`, and stops there.

Funnel (full evidence in report §`failingCondition` + `fixture.funnel`):
`objective=[ok:act10473:item4058x3]` (Progress acts resolved from template 251) →
`source=[ok:npc3475>pack4530>item4058]` (static loot link resolved in live game
data) → relevance true (251 active AND bag 4058 < 3 AND template 3475) →
raw=1 / relevant=1 / legal=1 → selected=44319 @ 5.9 m → `SetTarget` landed
(`landedTarget=44319`, retick `44319`, decide `[landed Target (targeting 44319)]`).

## (c) Proves / does-NOT-prove boundary

Proves:
- From 251-ACTIVE, the production path reads the Progress objective (act 10473 =
  ItemGather 4058×3) and resolves the item→drop-source link (4058 → pack 4530 →
  template 3475) from live game data, fail-closed with named details.
- Quest-relevance (snapshot quest state + static loot chain, no combat state) and
  combat-legality (`IsHostileTarget` + alive + `CanAttack` + `CanSeeTarget`,
  queried live per candidate, never stored) stay separate projections sharing
  only the objId.
- Deterministic selection (nearest-first flat + ObjId tiebreak) before any cut;
  reselect across ticks is stable (44319 → 44319).
- Assignment rides the canonical `GameplayActor.SetTarget` verb through the
  shared `BotDecisionSelector` pass at priority 25 (above advance/accept, below
  turn-in); per-bot wake attribution on charId=25.
- Selection is credit-neutral: meat 4058 unchanged 0→0, no combat executed.

Does-NOT-prove (explicitly out of scope, no claim):
- Pathfinding to the prey, approach locomotion, target tracking across movement,
  target reacquisition after loss, pursuit maintenance, attack initiation,
  autoattack, skill use, damage, death handling, corpse loot, 4058 acquisition,
  objective credit (`OnItemGather` path untouched), Ready transition, return
  travel, Talk/report, turn-in, rewards, persistence across restarts, multi-bot
  scale, any other quest.

## (d) Lessons A–F

- **A. Objective-vs-target split:** 251's first step is loot-driven gather, so the
  target (template 3475) and the credit key (item 4058) are different identities;
  the selector carries template→objId resolution explicitly because no engine
  method matches objId.
- **B. Non-actor objectives:** `ArmQuestTravel` returns null for in-Progress 251
  by design (Ready-reporter + nothing-active families only); prey-travel must
  never inherit offer-band logic — G4 stops at selection and adds no travel.
- **C. Legality reuse:** one public spelling — `CombatDecisionTree.IsHostileTarget`
  wrapping `BaseUnit.CanAttack`, plus alive + `CanSeeTarget` — reused, never
  copied; a third "hostile?" variant is prohibited.
- **D. Deterministic ordering:** nearest-first flat + ObjId tiebreak before any
  take-N cut (G3 doctrine §e.7); enumeration order is region insertion order,
  never proximity.
- **E. Funnel diagnostics:** every silent `continue` gets a tally
  (unresolved/dead/template/hostile/attack/stealth); per-candidate
  questRelevant/combatLegal flags ride the log with the CycleId join key;
  first-zero rule names the earliest failing predicate and stops.
- **F. Per-bot attribution:** wake→selection joins on fresh per-bot signals
  (CycleId + decide-detail overwrite + landed-target retick), never global step
  counters; `snap==actor` gap 0.0 m gates START.

## (e) Freeze rule

G4 is FROZEN at PASS-BEHAVIOR. No new features, no new acceptance criteria, no
new journey stages on the G4 path (251-ACTIVE → objective resolved → source
resolved → relevant+legal 3475 selected via `SetTarget`). Regressions may REPAIR
ONLY the frozen chain without widening scope; any widening (travel, pursuit,
combat, credit, turn-in) is a new gate with its own fixture, not a G4 follow-on.
Historical FAIL/UNKNOWN labels elsewhere stay as run records and MUST be read as
superseded by this freeze.

## (f) Architectural debt (recorded, untouched — follow-up ONLY, not G4 work)

- Nav intent: behavior cannot request movement; travel stays executor-owned
  straight-`MoveTo` with no intent channel or per-purpose arrival radius.
- Pursuit maintenance: no approach/track/retrack ownership past selection.
- Stale identity: dual-object divergence voids the run, not the code (identity
  proof stays a precondition, not a repair).
- CycleId: wake→terminal join exists for quest; not yet a universal harness join.
- World nav: `MoveTo`-vs-`NavigateTo` route ownership unresolved; navmesh dead on
  the quest path; no fail-fast UNREACHABLE terminal.
- Stop/preempt: runtime `Cancel` observation-only; preemption with the owning leg.
- Registry cleanup: bridge deactivate threw for the networked char; TCP disposed,
  registry entry left in place (report `fixture.cleanupSummary`).
- 3D-vs-flat: discovery gates on flat distance; travel scans use 3D.
- Combat ownership: two initiation shapes (roam inline vs HuntLeg) remain;
  `CombatExecutor` test-only; GOAP acquire stub always `Rejected` — never a wire
  target.
- Loot generalization: static 3475→4530→4058 link lives beside the selector as
  named constants; no generic objective-resolver architecture is claimed.
- Objective-resolver architecture: ItemGather→corpse-loot resolution exists only
  for 251; doodad-only `GatherLeg` fails closed for highlight-NULL gathers.

## (g) G5 handoff

Starting state: quest 251 ACTIVE on the bot with a selected live 3475 target
(this freeze's end state: selected 44319 via `SetTarget`, relevance true, meat
0/3). Question: approach and pursuit — from selection, close and maintain range
to the combat-range boundary and stop before attack (no attack, no credit, no
loot). Recommendation: run a G5 discovery/planning pass first, because
`MoveTo`-vs-`NavigateTo` ownership, live-snapshot use during motion, retracking
after target drift, stuck/repath signals, route ownership vs behavior intent,
combat-range authority, and hunt-legic reuse questions are all still open.
