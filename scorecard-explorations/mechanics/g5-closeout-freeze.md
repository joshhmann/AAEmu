# G5 Closeout Freeze — PASS-BEHAVIOR (2026-09-20, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (same lane as the G3/G4 freezes).
This is a documentation/reconciliation/freeze record. Zero behavior change in this pass.

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-20.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g5-moving-start-report.json`
  (`verdict=PASS-BEHAVIOR`, `failBoundary=none`, charId=39).
- **PASS-BEHAVIOR chain fields (gate leg):** selected live 3475 ObjId 44302;
  `startDist=14` (staged flat 15.0 m, REQUIRE 10..20), `minDist=2.2`,
  `endDist=2.2` (stop radius 3.0 m, `firstInRange=wake#4`);
  decide history `[landed Target (targeting 44302)] → [landed Move ()] →
  [landed Stop (stopped)]`; wakes 4, freshProven 4; `selectionChanges=0
  stable=True`; `meatPre=0 meatPost=0`, `questStillActive=true`, `turnIns=0`,
  `preyAlive=true`; combat scan NONE.
- **Call path (report):** bridge quest wake → manager Spawn/Activate +
  scheduler Wake → BotGoalArbiterStepExecutor (arbitrate →
  BotRoamStepExecutor.StepQuestLeg → QuestBehavior.Run G4 251 path + G5
  pursuit proposal → QuestObjectiveTargetSelector.Evaluate (G4 selection,
  reused production path) → Target proposal (25, yields once assigned) →
  pursuit Move/Stop proposal (24) → BotDecisionCycle.Execute →
  GameplayActor.SetTarget/MoveToUnit/Stop).
- **Fix reference (read-only verification, this pass modifies nothing):**
  `AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs` — stale-travel
  supersession: `SupersedeQuestTravelRoute` (:1363-1374, drops
  `Path`/`PendingLeg`/`QuestTravelTarget` scoped to `QUEST_TRAVEL` owner,
  idempotent no-op otherwise) + `IsForeignRouteInterruption` (:1386-1390,
  matches terminal-Interrupted non-`"stop requested"` quest-travel legs);
  guard site :1017-1027 (drop before issue/advance) + dispatch site :1294-1302
  (drop synchronously on `PURSUIT_MOVE_TO_UNIT` dispatch). Diff scope, read
  honestly: the working tree is dirty vs HEAD across 15 tracked bot files
  (audit/request/queue/arbiter/actor/GOAP/scheduler/etc. — accumulated
  multi-turn uncommitted work), so executor-only scope is NOT certifiable from
  tree state; the G5 supersession fix itself as read is the two hunks above —
  no pursuit-math, scheduler, or GOAP change is part of the G5 claim.
  Pursuit math is reused snapshot semantics, unchanged.
- **Fixture chain (legs, all passed):** adopt-lane; enter-world (inWorld=True);
  enroll charId=39 `activeBefore=False`; fixture-refill hp/mp 1.000/1.000
  (REQUIRE ≥0.95); quest-prehold `active251=True` (Progress);
  pre-stage-stop (clear-only Stop → 200); pursuit-stage spawner 3372 flat
  15.0 m live3475=44302; start-snapshot (gap 0.0 m, activity quest.progress,
  cycle baseline ABSENT, meat 0); stop-settle (`reSettled=True`,
  finalDist=2.2 m); gate PASS at boundary none (13.8 s exec).
- **Unit tests (reported, NOT rerun — this pass runs nothing):**
  `GoapRuntimeTests` 18/18 cited as lane-reported unit-only
  (projection truthfulness, not behavior competence); no G5 behavior claim
  rests on it.

## (b) Frozen contract (8-point proven list)

1. **251-ACTIVE Progress start:** quest-prehold + start-snapshot prove
   charId=39 with 251 active, step/status Progress, objectives `[0,0,0,0,0]`.
2. **G4 selection reused, never re-staged:** the gate stages only
   position/vitals/quest-state; production G4 path selects live 3475 44302
   (`g4Compat.traversed=true`, `selectedStable=true`, retrack/selection
   changes 0). G4 freeze stays the selection owner.
3. **Pursuit Move dispatch on fresh validation:** per-wake samples
   `validate=ok:rangeM=14.0:dispatch=move:reason=fresh` with
   `seenMove=true seenMoveRunning=true` and first fresh decision wake#1
   `[landed Target (targeting 44302)]`.
4. **Convergence 14.0 → 2.2 m into the 3.0 m band:** dist trail reaches 2.2 m
   at wake#4 (`firstInRangeWake=4 firstInRangeDistM=2.2`); post-START trail
   `15.0 → 10.0 → 1.0 → 1.0` corroborates approach (funnel trail caveat per
   movement-ownership-report §L3 is acknowledged; verdict rests on the
   behavior trail, not the re-appended funnel sweep).
5. **In-range Stop dispatched and held settled:** `dispatch=stop:reason=
   in-range` at 2.2 m, `seenStop=true`, `settledStop=true`,
   `reSettled=True`, settle pos trail static
   (15587.5,15117.6,130.9 ×3), finalDist 2.2 m.
6. **Post-arrival credit neutrality:** meat 4058 0→0, money/credit deltas 0,
   turnIns 0→0, quest still active/Progress, prey alive.
7. **Combat exclusion:** casts~0, damage/kill/loot hits 0, combat scan NONE —
   the run stops at range-hold and never attacks.
8. **Stale-travel supersession held:** no quest-travel resume after pursuit —
   pos trail stays at the prey (no southward walk to 3597/Mor, cf. the §L
   FAIL mode); the route layer never reissues the superseded destination.

## (c) Proves / does-NOT-prove boundary

Proves: §b only — from 251-ACTIVE + G4-selected live 3475, pursuit closes to
≤3.0 m, lands Stop, holds settled, with zero combat and zero credit.

Does-NOT-prove (explicitly out of scope, no claim): moving-target retracking
under large drift (retrackCount=0 this run — the still boar never tested it);
attack initiation; autoattack; skill use; damage; death handling; corpse loot;
4058 acquisition; objective credit (`OnItemGather` untouched); Ready
transition; return travel; Talk/report; turn-in; rewards; persistence across
restarts; multi-bot scale.

## (d) Lessons

- **Request-vs-intent ownership:** the quest leg owns the *intent* (pursue /
  stop); the actor + route layer own the *request lifecycle*. The §L FAIL was
  an authorization-scope defect (stale `Path` surviving interruption), not a
  pursuit-math defect — pursuit walked its snapshot vector correctly
  (15.7 m / 4.5 m/s + ramp ✓) and `Completed(arrived)`.
- **Supersession semantics:** an interrupted travel route is *dead
  authorization*, not a paused walk. Drop-before-reissue (guard site) plus
  drop-on-preemption (dispatch site) closes both the 36 busy-reject churn and
  the post-completion southward resume with one rule; the next quest wake
  re-decides and re-arms when travel is genuinely needed.
- **Ownership seam sufficiency:** scoping the drop to the `QUEST_TRAVEL`
  owner exactly (route-owned arrival halts `"stop requested"`, patrol ROAM,
  farm stash/restore all excluded by the predicate) preserves every
  legitimate pause/resume while killing only the foreign-interrupted route.
- **Busy-reject churn as diagnostic:** 36 futile `StateTransition: actor
  busy` rejections per scheduler step (~9/s) against a Running pursuit is the
  signature of a route layer fighting the single-writer — next time, read the
  churn as the contention signal directly instead of chasing downstream
  predicates past it.

## (e) Freeze rule

G5 is FROZEN at PASS-BEHAVIOR. No new features, no new acceptance criteria, no
new journey stages on the G5 path (251-ACTIVE → G4-selected 3475 → close to
≤3.0 m → Stop → settled hold, zero combat, zero credit). Regressions may
REPAIR ONLY the frozen chain without widening scope; any widening (retrack
under drift, attack, credit, turn-in) is a new gate with its own fixture, not
a G5 follow-on. Historical UNKNOWN/FAIL labels elsewhere (notably
movement-ownership-report §§C/E UNKNOWN and §L FAIL-BEHAVIOR /
PURSUIT-NO-PROGRESS) stay as run records and MUST be read as superseded by
this freeze.

## (f) Architectural debt (recorded, untouched — follow-up ONLY, not G5 work)

- Nav intent survival beyond this fix: behavior still cannot request movement;
  no intent channel or per-purpose arrival radius exists past supersession.
- Behavior ownership/cancellation: runtime `Cancel` observation-only;
  preemption stays with the owning leg.
- Arbiter utility: priority+eligibility first-allow-wins; no utility,
  commitment, or hysteresis.
- CycleId generality: wake→terminal join exists for quest; not yet a universal
  harness join.
- Stop/preempt semantics: executor settle Stops vs route arrival halts share
  the `"stop requested"` detail by convention, not by type.
- Registry cleanup: bridge deactivate threw for the networked char; TCP
  disposed, registry entry left in place (report `fixture.cleanupSummary`).
- 3D-vs-flat: discovery gates on flat distance; travel scans use 3D.
- World nav: `MoveTo`-vs-`NavigateTo` route ownership unresolved; navmesh dead
  on the quest path; no fail-fast UNREACHABLE terminal.
- Combat ownership: two initiation shapes (roam inline vs HuntLeg) remain;
  `CombatExecutor` test-only; GOAP acquire stub always `Rejected` — never a
  wire target.

## (g) G6 handoff

Starting state: quest 251 ACTIVE on the bot with the G4-selected live 3475
target closed to 2.2 m and holding settled under Stop (this freeze's end
state: selected 44302, in-range, settled, meat 0/3, zero combat). Question:
combat initiation — from in-range + settled, which single production shape
opens attack (roam inline vs HuntLeg) and what confirms first hostile contact.
Stop before damage-credit detail: no damage, kill, loot, credit, or turn-in
reasoning belongs in G6's opening pass.
