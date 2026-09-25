# G8a Closeout Freeze — PASS-BEHAVIOR (2026-09-21, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (same lane family as G3-G7d).
This is a documentation/reconciliation/freeze record. Zero behavior change in
this pass. Locked: withhold seam stays ON as documented dependency; G3-G7d
frozen (behavior untouched).

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-21.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g8a-resolve-report.json`
  (`verdict=PASS-BEHAVIOR`, `failBoundary=none`), charId=62.
- **PASS-BEHAVIOR chain fields:** reporter template 3512 resolved same-ObjId to
  ObjId 44295 across all 27 observed wakes (`stabilityMode=same-ObjId`,
  `respawnEpochs=[]`, every wake `reporter=44295 xcheck=44295`); START staged
  at dist 18.0 m (inside 25 m sweep, hold radius 25 m, stage +18x); 10/10
  quest-leg ticks (wakes 18-27, `warming=False`) each
  `WITHHELD TurnInQuest quest 251 reporter=44295` (`SelectedAction=TurnInQuest`,
  `Request=null`, `detailReporter=44295`); `withheld=10/27`, `landedTurnIn=0`,
  `sweepWithheld=20 sweepLandedTurnIn=0 sweepReporters=[44295]`, `foreign=[]`;
  quest `Ready/Ready`, `active=True`, objectives `[3,0,0,0,0]` throughout;
  reward deltas zero (`turnIns 0→0 completed 0→0 money 0→0 meat 3→3`).
- **Call path (report):** bridge quest wake → scheduler Wake → quest leg
  (`QuestBehavior` turn-in path: Ready check → Ready-acts reportNpc 3512 →
  `GetNpcByTemplateId` per wake → TurnInProposal TurnInQuest at TurnInPriority
  30 → `BotDecisionSelector` → withhold seam `Fail(WITHHELD,
  SelectedAction=TurnInQuest, Request=null)`); the gate adds no verbs — wake +
  observe + read-only npcState/npcObjId/charPos polls only.
- **Withhold dependency (locked):** withhold ON is REQUIRED so the Ready
  fixture survives: without it the first Ready tick would dispatch production
  `GameplayActor.TurnInQuest`, complete 251, and destroy the fixture under
  observation. A landed TurnInQuest here would be WITHHOLD/leaked-dispatch,
  never an expectation.
- **Test file:** `AAEmu.IntegrationTests/E2e/G8aReporterResolveGateTests.cs`
  (exists; not modified in this pass).

## (b) Causal timeline (every stage HISTORICAL/SUPERSEDED except the final)

1. **G8 decomposition — HISTORICAL proposal, CONFIRMED shape.** Three-gate
   split (G8a reporter-resolved / G8b return+InteractNpc / G8c
   TurnIn+completed+reward; G8d folded into G8c — engine does report → Reward →
   `DistributeRewards` → completed-drop in one step-machine pass) proposed in
   discovery-plan §H / return-ownership §H. G8a ran in exactly the proposed
   observe-only shape. Record: `g8-discovery-plan.md` §H, `g8-return-ownership.md` §H.
2. **Withhold control — HISTORICAL gap, RESOLVED as fixture.** Plan recorded
   withhold-turn-in as REQUIRED but UNKNOWN-shaped ("no existing flag found").
   The withhold seam now exists and was armed (`withholdTurnIn=True`) for the
   whole gate; it is the documented dependency that keeps the Ready fixture
   alive across 10 ticks. Record: report `withholdDependency`,
   return-ownership §§H/I.2.
3. **Warm-up wakes — HISTORICAL method, retained doctrine.** 17 warming wakes
   (sweep-empty DECIDE, bot drifting on a live Move leg 17.7 m → 204 m, movement
   after START allowed, not judged) before the first quest-leg tick at wake 18;
   warm-up wakes are neutral contrast, never proof. Record: report evidence
   `gate-wakes`, G3-closeout doctrine §e.2.
4. **Sweep-radius staging at 15-20 m — HISTORICAL staging, retained lesson.**
   Staged +18x to 18.0 m (REQUIRE reporter live + 10 m < dist < 25 m ONCE);
   START inside sweep, then drift to 204 m across ticks — the TurnIn proposal
   has no range gate (priority 30 fires from anywhere), so identity held at
   range. Record: report `stage-hold`, `DRIFT-NOTE (not judged)`.
5. **PASS — FINAL, FROZEN.** PASS-BEHAVIOR chain (§a). No further gate stages.

## (c) Proves / does-NOT-prove boundary

Proves:
- Reporter resolution: template 3512 → live ObjId 44295, same-ObjId stable
  across 27 wakes / 10 quest ticks (no respawn, no epoch change).
- Ready-gated TurnIn proposal identity under withhold: every quest-leg tick
  proposes exactly the observable `TurnInQuest` for quest 251 naming reporter
  44295, withheld (never dispatched).
- Fixture survival: quest stays Ready/Ready + active with zero reward deltas
  while withhold is ON.
- Stable reselection: per-wake `GetNpcByTemplateId` re-resolution returns the
  same reporter (xcheck agreement every wake).

Does-NOT-prove (explicitly out of scope, no claim):
- Return movement (post-START drift to 204 m explicitly not judged).
- `InteractNpc` / dialogue (zero InteractNpc dispatch in this gate).
- Proximity gating (TurnIn proposal fired at 204 m — no range gate claimed).
- TurnIn landing (0 landed by design; landing would be a withhold failure).
- Rewards (money/meat deltas zero; 18791/money assertions belong to G8c).
- Persistence (no restart in this gate).
- Scale (single bot, single quest 251).

## (d) Freeze rule

G8a is FROZEN at PASS-BEHAVIOR. No new features, no new acceptance criteria, no
new journey stages on the G8a path. Regressions may REPAIR ONLY: a regression
restores the frozen chain (Ready + same-ObjId 3512→44295 + 10/10 withheld
TurnIn + 0 landed + Ready/active end state) without widening scope; any
widening is a new gate with its own fixture, not a G8a follow-on. Historical
proposal language elsewhere ("withhold UNKNOWN-shaped", "G8a asserts…") stays
as run/proposal record and MUST be read as superseded by this freeze where it
describes pre-pass state; no reconciling edit was needed (no doc labels G8a
unproven — grep confirms).

## (e) Reusable E2E doctrine (lessons)

1. **Moving-START doctrine:** START is a staged instant, not a held position —
   stage inside the sweep ONCE (here 18.0 m), then allow post-START movement
   without judging it; judge only the signals the gate names (here reporter
   identity + withheld proposal per tick).
2. **Withhold-as-fixture:** when the behavior under observation self-destructs
   its own fixture on first fire (priority-30 remote TurnIn), the withhold seam
   is part of the fixture, not a cheat — arm it, echo it per wake
   (`withholdEcho=True`), and treat any landing as a withhold failure, never a
   pass.
3. **Unanimity-vs-task-bar verdict discipline:** the gate passes on the
   unanimous task bar (10/10 ticks withheld, same reporter, quest untouched),
   not on any single wake; warming wakes are contrast-only and the 17-0
   warm-up/tick split is expected, not a weakness.
4. **Sweep-radius staging at 15-20 m:** stage the bot in the 10-25 m band (here
   +18x → 18.0 m) so START satisfies the sweep gate even if a live Move leg is
   still carrying the bot outward; re-verify reporter liveness + distance ONCE
   at stage-hold, then release.

## (f) Architectural debt (follow-up ONLY — not G8a work)

- TurnIn has no range gate (`GameplayActor.cs:970-1004`): priority-30 proposal
  fires at any distance (observed at 204 m) — recorded, untouched.
- Return-leg ownership: post-START drift rode an executor-owned Move leg with
  no behavior intent channel — G8b's question, not G8a's.
- Cleanup gap (recorded, untouched): bridge deactivate threw
  `InvalidOperationException` for networked charId=62; TCP disposed, registry
  entry left in place (`cleanupSummary=UNAVAILABLE`).
- Talk-vs-InteractNpc wording trap for 251 (Talk void-rejects; InteractNpc is
  the interaction owner) — G8b scope.

## (g) G8b handoff

Starting state: quest 251 Ready/Ready + active on charId-62's lineage with
reporter 3512 live-resolved to ObjId 44295 and the TurnIn proposal withheld
(not landed). Question: the return leg — drive the bot from Ready-away back to
the reporter, reaching flat ≤ 25 m, and complete one `InteractNpc` with
dialogue detail naming template 3512, with `turnIns == 0` and reward deltas 0
throughout. Stop before TurnIn landing — G8b never dispatches TurnInQuest;
withhold stays ON.
