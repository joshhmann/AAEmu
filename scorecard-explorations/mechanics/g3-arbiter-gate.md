# G3 Arbiter Gate — yielded activity around START: quest.progress → recovery.rest at the START wake (2026-09-20, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (ports 2237/2239/2250/2260/2234/2280/3311,
`COMPOSE_PROJECT_NAME=q0pilot`, token `e2e-q0-pilot-token`). Budget consumed exactly as ordered:
ONE authorized restart (new `arbiterActivity` diagnostics field needed a game rebuild, §A — stated
why below) + ONE extended run (charId 19). No rerun: the result is decisive (FAIL-ARBITRATION),
not UNKNOWN. Bootstrap flag ON in the game process (`AAEMU_QUEST_BOOTSTRAP_ENABLED=1`, new pid
2241404 after restart; old pid 189877 killed, login/MySQL untouched). Fixture: quest 251 (L2),
giver 3512, level 10, `waitMs=30000`, no gameplay commands after START.

Read-first context: `g3-stable-start-gate.md` (full A-K: route settled, START 0.014 m, wake UNPROVEN
by quest-tick signals, debt J predicting the quest.progress→recovery.rest flip);
`BotGoalArbiter.cs` (per-bot `_activeActivity` memory, written only inside `Arbitrate`);
`BotDriveBridge.cs` `HandleQuestOp` (quest-observe payload); `BotControlController.BrainInspect`
(brain endpoint); `QuestBootstrapActivityModule.cs:64-90`, `OutOfCombatRecoveryModule.cs:443-481`,
`PresenceRoamActivityModule.cs:41-52`, `IdleActivityModule.cs:27-35` (predicates); priority ladder
Schedules 100 > Recovery 85 > Conflict 75 > Fishing/Homestead 60 > QuestBootstrap 58 > NeedsFarm 55
> PresenceRoam 50 > Idle 0 (first Allow wins, lower modules never consulted).

## A. Activity surface (used, not added-twice)

| Candidate surface | Verdict |
|---|---|
| quest-observe payload (`quest wake`/`quest observe`, bridge TCP 2260, ungated) | MISSING before this pass: `wakeSeq`/`questLegActive`/`questDecideDetail` yes, yielded activity NO (code-verified `BotDriveBridge.cs` payload ctor) |
| `GET /api/bots/brain/<name>` (projects `IBotGoalArbiter.GetActiveActivity` via `BotBrainProjection.Capture(..., arbiterActivity, ...)`) | EXISTS but UNREACHABLE: live-probed `→ 404 {"Message":"Bot control API is disabled"}` — same `CheckGate` (`AAEMU_BOT_CTRL` absent) that blocks `/api/actors/stop` |
| Server log (`BotGoalArbiter: bot <id> activity <old> -> <new>`, Info) | EXISTS but transitions-only: steady-state wakes log nothing, so per-phase current values unobservable; used as corroboration (§B), not the gate surface |
| quest-observe `arbiterActivity` (this pass) | THE ONE allowed change, additive diagnostics-only, no behavior change: `BotDriveBridge.cs` reads `IBotGoalArbiter.GetActiveActivity(character.Id)` (same DI pattern as the adjacent `arbiterStep` read; pure per-bot memory read, never arbitrates/mutates) and adds one string field to the shared wake/observe payload. Rebuild + restart of ONLY the game process was REQUIRED (payload ctor is game-side; no HTTP/API/behavior touched) — this is the pass's single restart, stated why |

## B. Activity around START (charId 19, read-only observes, same budgets)

| Phase | `arbiterActivity` | Corroboration |
|---|---|---|
| post-enroll | `quest.progress` | server log 20:53:04 `(none) -> quest.progress (module QuestBootstrap)` — enroll arbitration |
| post-settle (route stilled, `settled=True rearmed=False`) | `quest.progress` | held; no transition logged |
| post-stage / pre-START (0.014 m, giver objId 44320 live, money 0, active 0) | `quest.progress` | held through teleport + staging |
| START wake response (`waitMs=30000`) | `recovery.rest` | server log 20:53:34 `quest.progress -> recovery.rest (module OutOfCombatRecovery)` |
| START window end | `recovery.rest` (`everProgress=false`) | only 2 char-19 quest log lines exist, both the enroll spawn tick (`cycle=quest-19-639254731844306795`, spawn pose) — NO staged tick; `questDecideDetail` byte-identical pre/post, audit 0→0 |

The flip itself is per-bot arbitration proof for the START window: `_activeActivity` is written
ONLY inside `Arbitrate` (set on change `:193`, cleared on NoCandidate `:224`), called only from the
per-bot decorator step — so at least one arbitration pass ran for OUR bot between pre-START and the
wake response (wake-driven or ambient scheduler pass, unattributed). The stable-start gate's
"wake UNPROVEN" is thus reframed: arbitration RAN, but the quest leg never engaged (no decide
overwrite, no audit rows) because the yielded activity was `recovery.rest`.

## C. Classification: FAIL-ARBITRATION at ARBITRATION/no-quest-activity

Predicate (code + observed state): `OutOfCombatRecovery` (85) ALLOWED over `QuestBootstrap` (58) —
`quest.progress` never consulted (first-allow-wins). Preconditions for the WITHHELD prong all hold:
flag ON (game-side `game-proc-environ`), broke (money 0 < 1000), staged (0.014 m ≤ 25 m, giver live),
alive — PROVEN by the predicate itself: recovery DENIES when dead (`:448-449`) or in battle
(`:451-457`), so its Allow certifies alive + out of battle. Trigger disjunct narrowed by observation:
the already-recovering activity-name disjunct (`ActiveActivity is rest/eat`, `:460`) is EXCLUDED
(pre-START activity was `quest.progress`); remaining: `Stance==Sit`, the `ActiveBotRecoveries` latch,
or `HP<0.75/MP<0.60` (`:459-478`) — stance/HP/MP unobserved on quest-observe (no vitals in payload).
[INFERENCE] leading hypothesis: `setLevel(10)` raised MaxHp without refill → fraction < 0.75; needs
a vitals surface or a pre/post-setLevel HP-fraction probe to confirm — that is the next gate, not this one.
Debt J (stable-start §J) CONFIRMED almost verbatim, with one correction: the flip lands exactly at the
START-window arbitration, and the persisting enroll travel reason is residue, not a live route.

## D. Verdict reinterpretation

The run's legacy verdict (`FAIL-BEHAVIOR at DISCOVERY/no-legal-proposal`, `progressHeld=true`) rests on
the STALE enroll travel reason and is SUPERSEDED: no quest tick ran at START (only the enroll spawn-tick
detail exists), so no discovery predicate was ever evaluated — `FAIL-ARBITRATION` replaces it, and the
stable-start gate's `UNKNOWN/wake-unproven` is reframed (arbitration proven by transition, quest tick absent
by design of the yielded activity). No accept chase ran beyond the one window, per order.

## E. Files changed + evidence + next (handoff only, no follow-on)

- `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs` — ONLY game change: `arbiterActivity` read + payload
  field (above; remainder of the worktree-vs-HEAD diff is pre-existing stacked lanes, untouched).
- `AAEmu.IntegrationTests/E2e/G3AutonomousQuestAccept3512Tests.cs` (untracked) — ONLY test change:
  `GetActivity`/`DescribeWithholding` helpers, 4 phase samples + START-window tracking, derived 3-way
  `arbiter-around-start` leg, `g3-arbiter-gate` scenario to the NEW report path. Same budgets.
- Evidence JSON (new path; `g3-quest-autonomy-3512-report.json` NOT overwritten):
  `$E2E_ROOT/logs/g3-arbiter-gate-report.json` (top verdict FAIL-BEHAVIOR/DISCOVERY legacy;
  `fixture.arbiterGateVerdict=FAIL-ARBITRATION`, `arbiterCondition` carries the predicate).
  Server-log corroboration: `$E2E_ROOT/logs/game-restart.log` (post-restart boot + both transitions).
- This doc. Provenance: source rev `6c3efe25ecb68998544eb3f75699bc1674c7eef1`, runtime sqlite md5
  `78b3bdbf038db3b927056106efdf91af` (unchanged across restart — DB preserved). Timings: setup
  32.1 s / exec 0.2 s / wall 32.3 s. New game pid 2241404 (`hub` process `q0game`, persistent),
  env preserved (flag, token, all `E2E_*` ports); NLog caps re-applied after publish (no-op).
- Exactly-one next action (HANDOFF, not executed): prove the recovery trigger — expose
  HP/MP/stance on quest-observe (same additive pattern) or probe HP fraction across `setLevel`;
  until then no discovery/nav/kill-quest follow-on: the quest leg is arbitration-gated, not broken.
