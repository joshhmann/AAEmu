# G8b Closeout Freeze — PASS-BEHAVIOR (2026-09-21, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (withhold ON throughout, no G8c).
This is a documentation/reconciliation/freeze record. Zero behavior change in this pass.

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-21.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g8b-return-report.json`
  (`verdict=PASS-BEHAVIOR`, `failBoundary=none`, `scenario=g8b-return`), charId=71
  (`G8bReturn163813`, identity proof `activeBefore=False`).
- **PASS-BEHAVIOR chain fields:** dist 35.0→0.2 m (min 0.2 m, strictly decreasing
  trace to 0.2 m) to reporter 3512 objId 44319 (same-ObjId, `epochs=[44319]`,
  `respawns=[]`); `landedMove=1` with owner `RETURN_MOVE_TO_UNIT x1` (server log
  `BotMoveStart` join on actor/name) + `landedStop=1` (wake 40, `stopped`) +
  `InteractNpc` dialogue Completed naming 3512
  (`npc 44319: dialogue with npc 3512`, wakes 41–80); quest 251 Ready/Ready,
  Ready/active throughout; `turnIns 0→0`, `completed 0→0`, money 0→0, meat 3→3,
  reward18791 3→3; `talkRows=0 outOfRange=0 busy=0 stuck=0`, `foreign=[]`;
  `freshCycleIds=42`, `questLegTicks=41/80`.
- **Code changes (this freeze changes none; implementation landed under test):**
  `AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs` — `ReturnGoal="quest.return"`
  (`:80-81`), `ReturnProposal` (`:1115`), `DispatchReturnMove` with
  `RETURN_MOVE_TO_UNIT` owner tag (`:1402-1411`), `DispatchReturnStop`
  (`:1432`), goal-guarded dispatch arms
  (`:1326-1328`), `ObjectiveReturnPriority=21` (`QuestDecisionScenario.cs:86`),
  withhold gate (`:556-559`); `GameplayActor.InteractNpc` 25 m gate + dialogue
  fallback (`GameplayActor.cs:1154-1256`); supersession guard for the return leg
  (`BotRoamStepExecutor.cs:1300-1304`).
- **Unit tests exist (not run in this pass):**
  `AAEmu.UnitTests/Game/Core/Managers/Bots/QuestReturnInteractTests.cs`
  (far-Move + owner, near Stop→InteractNpc, held-leg withdraw, lost-reporter
  withdraw) and `QuestTurnInWithholdTests.cs` (withhold observable/suppressed).
- **Withhold dependency (recorded, stays ON):** withhold ON is REQUIRED throughout
  so the Ready fixture survives — without it the first Ready tick would dispatch
  production `TurnInQuest` (no range gate) and destroy the fixture. Withhold stays
  ON after PASS — no G8c in this pass.

## (b) Causal timeline (every stage HISTORICAL/SUPERSEDED except the final)

1. **No production return leg — HISTORICAL gap, FIXED.** Executor
   `ArmQuestTravel` walks to the reporter spawner only when unspawned
   (`BotRoamStepExecutor.cs:1407-1424`); spawned-but-distant was the named hole
   (`g8-return-ownership.md:60-62`). Quest-owned `MoveToUnit` per-wake leg closes it.
2. **Withhold control — HISTORICAL UNKNOWN, GROUNDED.** Discovery called the shape
   UNKNOWN (`g8-return-ownership.md:196-199`); now `QuestOptions.WithholdTurnIn`
   (`QuestDecisionScenario.cs:53`) + enforcement (`QuestBehavior.cs:512-518` /
   `:556-559`), default-off, E2E-only, production never sets it.
3. **Return/Move owner + Dispatch arms — HISTORICAL deferred gaps, LANDED.**
   Discovery named them proposed-not-present (`g8b-discovery-plan.md §§A3/C/I/J`);
   now `RETURN_MOVE_TO_UNIT` + `ReturnGoal` arms (`QuestBehavior.cs:1326-1328,
   1402-1432`).
4. **Talk-vs-InteractNpc wording — HISTORICAL trap, RETIRED by policy.**
   `Talk` void-rejects on 251 (no talk-family objective); `InteractNpc` dialogue
   fallback Completes (`GameplayActor.cs:1140-1143` vs `:1245-1256`). G8b never
   dispatches `Talk`.
5. **Moving-START doctrine — HISTORICAL method, retained.** No stillness REQUIRE at
   START (moving pose 15690.9,15172.5,121.2); post-START movement is behavior
   evidence; single harness preempt Stop pre-START only (staging, record-only).
6. **PASS — FINAL, FROZEN.** PASS-BEHAVIOR chain (§a). No further gate stages.

## (c) Proves / does-NOT-prove boundary

Frozen contract (proves):
- Quest-owned return: Ready 251 + per-wake live-resolved reporter 3512
  (`GetNpcByTemplateId`, never stored; template + alive revalidated) → outside
  25 m `MoveToUnit` (fresh leg when none live, drift-gated retrack only after
  > 2.0 m motion) → inside 25 m audited `Stop` → hold-confirm withdraw →
  settled `InteractNpc` dialogue Completed naming 3512.
- Owner-tagged legs: every return leg owns as `RETURN_MOVE_TO_UNIT` (server-log
  join); pursuit telemetry (`LastPursuitIssue`, priority 24) never entangled.
- Withhold-as-fixture: TurnIn stays proposed-but-unlanded (`WITHHELD` rows only),
  quest Ready/active, zero reward deltas; stop-before-attack/interact boundary —
  Stop settles before InteractNpc so a live leg never busy-rejects it.
- Stop before TurnIn landing: G8b never dispatches `TurnInQuest`; withhold ON all wakes.

Does-NOT-prove (explicitly out of scope, no claim):
- TurnIn landing, completion flag, reward distribution (18791/mail/copper/XP).
- Persistence across restart, multi-bot scale, any other quest.
- Moving-target retracking at range (reporter is static; drift gate unfired by design).
- Route-layer/navmesh return (executor spawner-walk stays unspawned-fallback, untouched).

## (d) Freeze rule

G8b is FROZEN at PASS-BEHAVIOR. No new features, no new acceptance criteria, no
new journey stages on the G8b path. Regressions may REPAIR ONLY: a regression
restores the frozen chain (35 m→≤25 m owner-tagged return → Stop → InteractNpc
dialogue, `turnIns==0`, quest Ready/active) without widening scope; any widening
is a new gate with its own fixture, not a G8b follow-on. Historical
proposed/UNKNOWN labels elsewhere stay as run records and MUST be read as
superseded by this freeze. Locked G3–G7d behavior untouched.

## (e) Reusable E2E doctrine (lessons)

1. **Moving-START applied to return:** gate START on measured flat range
   (26 m < dist < 60 m, outside the 25 m gate) with withhold ON — never on
   stillness; post-START motion is the evidence.
2. **Owner-tagged legs:** every quest-owned Move carries its own owner
   (`RETURN_MOVE_TO_UNIT`, staged before dispatch, server-log joined) so intent
   is attributable per wake and never entangled with pursuit/travel owners.
3. **Withhold-as-fixture:** when the terminal verb has no range gate, hold the
   Ready fixture with a wake-scoped, goal-guarded suppression (return goal ≠
   `TurnInGoal`) that lifts exactly on convergence — the suppressed proposal
   stays observable in diagnostics.
4. **Stop-before-attack/interact boundary:** inside the purpose radius, Stop
   (audited) → hold-confirm withdraw → verb; without Stop-first the live leg
   busy-rejects, without withdraw Stop starves the verb.
5. **First-zero funnel + same-ObjId stability:** `epochs=[single]` /
   `respawns=[]` plus per-wake template bind; arrival proves coordinates only
   joined to the live reporter (G5 §E.3 discipline).

## (f) Architectural debt (follow-up ONLY — not G8b work, recorded untouched)

- Snapshot-once `MoveToUnit` (live unit discarded at issue; Tick never
  re-resolves) — adequate for a static reporter, fragile for a moving target.
- Bare-dict reporter resolve (no generation check) — recycle detected only via
  per-wake template re-check, not identity.
- Shared `LastStopHold` memory between pursuit and return (keyed by
  actor+target) — correct today, coupled tomorrow.
- TurnIn carries no range gate (`GameplayActor.cs:970-1004`) — the withhold
  fixture exists because the engine never requires proximity.
- G8a/G8c lane files untouched by this pass; no test, fixture, source, or lane
  state modified here.

## (g) G8c handoff

G8c (withhold release → `TurnInQuest` → completed + 18791 reward) is already
completed — reference it as done: starting state is this freeze's end state
(quest 251 Ready/active, reporter 3512 live-resolved, bot settled inside the
25 m gate after dialogue), and its question (landing the withheld TurnIn to
completion + reward deltas) is answered there, not here.
