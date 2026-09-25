# G8c Closeout Freeze — PASS-BEHAVIOR (2026-09-21, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (same lane family as G3–G8b).
This is a documentation/reconciliation/freeze record. Zero behavior change in
this pass. Locked: G3–G7d + G8a/G8b frozen (behavior untouched).

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-21.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g8c-turnin-report.json`
  (`verdict=PASS-BEHAVIOR`, `failBoundary=none`), charId=74
  (identity proof `name=G8cTurnIn181804`, `activeBefore=False`).
- **PASS-BEHAVIOR chain fields:** START staged Ready/Ready 251 + live reporter
  3512 objId 37270 at 12.0 m (2 m < dist < 25 m, in-range START, withhold ON);
  withhold released ONLY at START (`withholdTurnIn=false echoed=True`, then the
  scheduler wake owns every decision); wake 3
  `landed TurnInQuest (quest 251 completed by turn-in)` ×1
  (`landedTurnInQuest=1 completedByTurnIn=1 otherTurnIn=0`);
  `turnIns 0→1`, `completed 0→1`, quest end state step/status null, `active=False`,
  `hasCompleted=True`; reward 18791 bag 3→4, mail 0→0 (route=bag);
  meat (4058) 3→0 (cleanup); money 0→42; 3 confirming wakes 4–6
  (`no legal quest proposal`, quest stays inactive); `stabilityMode=same-ObjId`,
  `respawns=[]`; `questLegTicks=3/6`, 4 fresh CycleIds; window 1.4 s / 180 s.
- **Code change (suppression-release predicate, pre-existing — not this pass):**
  `AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:556`
  (`if (opts.WithholdTurnIn && selected.Goal == TurnInGoal)` → `Fail("WITHHELD",
  …)` — the withhold seam; proposal stays observable, dispatch withheld, quest
  stays Ready/active); option
  `AAEmu.Game/Core/Managers/Bots/QuestDecisionScenario.cs:53`
  (`WithholdTurnIn`, default false, production never sets);
  leg wiring `AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:1270`
  (`WithholdTurnIn = IsQuestTurnInWithheld(bot.CharacterId)`);
  E2E-only bridge control
  `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:4401-4426` (arm/clear per-bot
  flag; no gameplay action, no quest state touched) + read-only echo/probe
  (`:4578-4582`).
- **Unit tests referenced (as reported, NOT rerun in this pass):** withhold
  3/3 (`AAEmu.UnitTests/.../QuestTurnInWithholdTests.cs`: Defaults,
  Withheld-observable, Progress-still-advances) + return 5/5 neighbors
  (`QuestReturnInteractTests.cs`: far-Move, near-Stop→InteractNpc, held-drift,
  reporter-lost, priority-layout).
- **Gate test file:** `AAEmu.IntegrationTests/E2e/G8cTurnInRewardGateTests.cs`
  (exists; not modified in this pass).
- **Untouched check:** G8a (`g8a-closeout-freeze.md`) / G8b
  (`g8b-discovery-plan.md`, gate tests) files untouched by this pass; grep
  confirms no doc labels G8c unproven — no reconciling edit needed.

## (b) Causal timeline (every stage HISTORICAL/SUPERSEDED except the final)

1. **G8 decomposition — HISTORICAL proposal, CONFIRMED shape.** Three-gate
   split (G8a reporter-resolved / G8b return+InteractNpc / G8c
   TurnIn+completed+reward; G8d folded into G8c — engine does report → Reward →
   `DistributeRewards` → completed-drop in one step-machine pass) proposed in
   discovery-plan §§H–J / return-ownership §§H–I. G8c ran in exactly the
   proposed withhold-release shape. Record: `g8-discovery-plan.md` §§H–J.
2. **Withhold control — HISTORICAL gap, RESOLVED as fixture.** Plan recorded
   withhold-turn-in as REQUIRED but UNKNOWN-shaped ("no existing flag found",
   discovery-plan §J). The seam (`QuestBehavior.cs:556`,
   `QuestDecisionScenario.cs:53`, bridge `withhold` sub-command) now exists and
   was armed (`withholdTurnIn=True`) through staging, released ONLY at START.
   Record: report `withholdDependency`, `withhold-arm` / `withhold-release` legs.
3. **G8a pre-gate — HISTORICAL proof, retained dependency.** Reporter 3512
   live-resolved same-ObjId, 10/10 quest ticks WITHHELD observable, 0 landed,
   quest Ready/active, zero reward deltas. G8c's START fixture is G8a's shape
   plus release. Record: `g8a-closeout-freeze.md` §a.
4. **G8b neighbor — HISTORICAL parallel, retained boundary.** Return leg
   (Move `RETURN_MOVE_TO_UNIT` → Stop → InteractNpc dialogue, `turnIns == 0`,
   zero reward, withhold ON throughout) is the travel half; G8c starts after it
   with withhold released. G8c wake 2 landed one InteractNpc dialogue before
   the wake-3 TurnIn — consistent, not judged. Record: `g8b-discovery-plan.md`.
5. **Staging at +12x / 12.0 m — HISTORICAL staging, retained method.**
   Reporter live + template 3512 + 2 m < dist < 25 m verified ONCE at
   stage-place; moving-START (no stillness REQUIRE); preempt Stop
   ATTEMPT-FAILED (no WebApi backend, record-only; idle verified, START
   snapshot decides). Record: report `stage-place`, `preempt-stop/idle` legs.
6. **PASS — FINAL, FROZEN.** PASS-BEHAVIOR chain (§a). No further gate stages.

## (c) Proves / does-NOT-prove boundary

Proves (frozen contract — withhold release → TurnIn → completed + reward, stop there):
- Withhold release re-arms the production turn-in path: after release the
  scheduler wake alone proposes (priority 30), dispatches, and lands exactly
  one `TurnInQuest` Completed for quest 251 at reporter 3512 (same ObjId 37270).
- Terminal semantics: `HasQuestCompleted(251)` true + inactive
  (`ActiveQuests` drop) + `completedByTurnIn` ledger detail + `turnIns 0→1`.
- Reward conservation on this run: 18791 bag 3→4 via bag (mail 0→0) + meat
  4058 3→0 cleanup + money 0→42 recorded.
- Post-terminal silence: 3 confirming wakes with no legal quest proposal, quest
  stays inactive, no foreign turn-in, `talkRows=0`, `busy=0`.

Does-NOT-prove (explicitly out of scope, no claim):
- Journeys (no travel-to-giver from afar in this gate; staged in-range at 12 m).
- Multi-quest (quest 251 only; `foreign=[]` is absence-of-spill, not coverage).
- Scale (single bot charId=74, single run).
- Persistence (no restart/reload in this gate).
- Return-to-other-givers (reporter is 3512 only; no general return proof — that
  is G8b's question, frozen separately).

## (d) Freeze rule

G8c is FROZEN at PASS-BEHAVIOR. No new features, no new acceptance criteria, no
new journey stages on the G8c path. Regressions may REPAIR ONLY: a regression
restores the frozen chain (Ready + live 3512 + release → 1 landed TurnInQuest
Completed → inactive + `HasQuestCompleted` + 18791 +1 via bag + meat 3→0)
without widening scope; any widening is a new gate with its own fixture, not a
G8c follow-on. Historical proposal language elsewhere ("withhold
UNKNOWN-shaped", "G8c asserts…") stays as run/proposal record and MUST be read
as superseded by this freeze where it describes pre-pass state; no reconciling
edit was needed (no doc labels G8c unproven — grep confirms).

## (e) Reusable E2E doctrine (lessons)

1. **Suppression-must-release:** when the behavior self-destructs its fixture
   on first fire (priority-30 remote TurnIn), the withhold seam is part of the
   fixture — arm it through staging, release it ONLY at the named START moment
   via the existing control, then let the scheduler own every decision.
2. **Withhold discipline:** echo `withholdTurnIn` per wake (`withholdEcho`);
   any landing while ON is a withhold failure, never a pass; any absence of
   landing after release past the window is the gate's fail, not staging's.
3. **Reward-conservation checks:** assert the full ledger (bag delta + mail
   zero + cleanup consume + money recorded), not just "reward arrived" — the
   bag-vs-mail route (`Quest.cs:311-322`) and cleanup (`Cleanup = t`) are part
   of the terminal proof.
4. **Cleanup semantics:** bridge deactivate may throw for networked chars
   (`cleanupSummary=UNAVAILABLE`, TCP disposed, registry entry left) — record
   it as cleanup-only, never as behavior evidence for or against the gate.

## (f) Architectural debt (follow-up ONLY — not G8c work)

- TurnIn has no range gate (`GameplayActor.cs:970-1004`; engine
  `QuestManagerEvents.cs:22-70`; `Transform` carried never read) — recorded,
  untouched.
- Talk-vs-InteractNpc wording trap for 251 (Talk void-rejects; InteractNpc is
  the interaction owner) — G8b scope, untouched.
- Cleanup gap (recorded, untouched): bridge deactivate threw
  `InvalidOperationException` for networked charId=74; TCP disposed, registry
  entry left in place (`cleanupSummary=UNAVAILABLE`).
- Preempt Stop seam unavailable when no WebApi backend is enabled on the
  shared port (12 hashed attempts, ATTEMPT-FAILED record-only) — staging
  doctrine (idle-verify + START snapshot decides), untouched.

## (g) Arc closure

Full quest-251 loop proven across Q0–G8c gates (reference, not re-proof): G3
accept at 3512 → G4 objective/target → G5 pursuit → G6 credit → G7 loot/death
handling → G8a reporter-resolved (withheld) → G8b return + dialogue (withheld)
→ G8c TurnIn + completed + reward (released). The arc stops at the frozen
contracts; journeys, multi-quest, scale, and persistence remain explicitly
unproven by design.
