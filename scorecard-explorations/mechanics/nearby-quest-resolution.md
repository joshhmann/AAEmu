# Nearby-Quest Resolution — G3 In-Range Autonomy Gate (quest 251 @ giver 3512)

Lane: q0 (`E2E_ROOT=/root/aaemu-e2e-q0`, adopted warm lane, no restart). Date: 2026-09-19. Branch `develop`, HEAD `6c3efe25e`.
Harness: `AAEmu.IntegrationTests/E2e/G3AutonomousQuestAccept3512Tests.cs` (fixed harness, used as-is, unmodified).

## A. Scope

In-range gate only: a fresh eligible character staged at the true giver's spawner, START = one bounded
scheduler wake, observe-only afterwards (no gameplay commands after START), per-bot wake proof.
Claim under test (only): the production scheduler autonomously accepts in-range in-band quest 251
through the live actor (`QuestBehavior` → `Talk` if required → `GameplayActor.AcceptQuest` → active).
Out-of-range navigation gate gated on in-range green (not reached → NOT RUN). Scheduler core untouched;
no production fix; no new test file.

## B. Starting state

- Warm adopted q0 lane. Live game process at run time: pid 1278066 (`dotnet AAEmu.Game.dll`, booted 11:52).
  Stale game/login processes from earlier boots present (pids 2085391/2079399, 1735771/1728344, 3406520/3399908)
  — game-side reads must resolve the live `AAEmu.Game.dll` pid, never assume one.
- Bootstrap flag game-side: `AAEMU_QUEST_BOOTSTRAP_ENABLED=1` present in live game environ
  (test-reported `gameSide=1 [game-proc-environ pid=1278066]` both gate runs). An early manual check against
  stale pid 2085391 showed the flag absent — that pid was not the live server; disregarded.
- Repo dirt pre-existing, untouched by this lane: 25 modified + 19 untracked (prereq Parts 1–4, behavior
  extraction `QuestBehavior.cs`/`BotBehaviorRuntime.cs`, E2E files incl. this G3 test, evidence zips).
  This lane modified/added/deleted zero code files (report file only, per lane allowance).
- Authoritative q0 port map (corrected during this lane from `Config.Local.json` + live logs; the brief's
  label order was wrong): login-client-TCP 2237 / game 2239 / stream 2250 / **bridge 2260**
  (`Bots.E2EBridgePort`, game.log: "bridge listening on 127.0.0.1:2260") / **WebApi 2280** / login-internal
  2234 (login↔game link, not client protocol) / mysql 3311. Two pre-gate runs failed on the swapped map
  (bridge 2234 = login internal link: TCP connects, JSON `ping` never answered) — HARNESS-FAIL, not verdicts.

## C. Fixture verification (code/data, no lane run needed)

| Item | Verdict | Evidence |
|---|---|---|
| Quest 251 starter = NPC 3512 | VERIFIED (authoritative linkage) | `quest_components`: 251 has Start comp 383 (`component_kind_id=2`); `quest_acts` row 333: comp 383 → detail 77 `QuestActConAcceptNpc`; `quest_act_con_accept_npcs` 77 → `npc_id 3512` |
| Required level (2-vs-10) | RESOLVED from `IsDiscoverable` semantics: **min char level 1** → both eligible | `IsDiscoverable` (`GameplayActor.cs:1407-1425`) gates on every Start component's `unit_reqs` via `CanComponentRun`. Comp 383 carries exactly one req: row 45452 `kind_id=1 (Level), value1=1` → level ≥ 1. Template `LEVEL=2` (`quest_contexts`) is display/band input, not the char gate. Kept test's **level 10** (eligible + Q0-proven direct-accept precedent); level 2 equally eligible (rig-proven). No correction needed |
| Band membership | VERIFIED | Default band [1,9] filters *quest* level; 251 `LEVEL=2` ∈ band |
| Giver 2425 retired | VERIFIED (fixture bug, not engine) | `offers(2425)={532,533,534}`, all `LEVEL=30` — out-of-band at every char level; discovery can never offer 251 there |
| Spawner 3512 exists, single | VERIFIED (data) | `main_world/npc_spawns.json`: one 3512 entry `(15655.91, 15172.51, Z 121.24)` "Mayor Gott"; 2425 at `(14536.6, 11117.107)` (~4.6 km away, different starter area) |
| Spawner live + eligibility live | VERIFIED (lane, both gate runs) | `teleportToNpc(3512)` + 30 s objId poll: giver objId **24702** both runs; char staged to **0.1 m** (`[15655.9,15172.5,121.2]`); snapshot == actor position (no stale-position gap on fresh enroll+teleport); fresh accounts (`activeBefore=False`); money 0 < 1000-copper bootstrap threshold |

Fixture verdict: CORRECT as coded. Zero test/fixture edits; never production-for-fixture.

## D. Exact production call path (expected, code-cited)

Bridge `quest wake` → manager Spawn/Activate + scheduler wake → `BotGoalArbiterStepExecutor.StepAsync`
(arbitrate → `StepQuestLeg`) → `QuestBootstrapActivityModule.CanActivate` (priority 58; flag ON;
alive; money 0 < `GoldTarget` 1000 → `Allow(quest.progress)` expected) → `BotRoamStepExecutor.StepQuestLeg`
→ `QuestBehavior.Run` on executor live actor (perceive; discover sweep `MaxQuestDiscoverRange` 25 f,
first-3-take; band [1,9]; `IsDiscoverable` engine gate; accept lowest-level-first) →
`BotDecisionCycle.Execute` → `GameplayActor.AcceptQuest(251, Npc, 3512)` → quest active.
Canonical seam preserved: autonomy legs call the live actor directly (NOT via `BotActionCommandQueue`).
Path never executed: no per-bot wake was delivered (see F).

## E. Test results per gate

- FIXTURE VERIFY: **PASS** (table C; live half confirmed in-gate both runs).
- IN-RANGE (G3-IN-RANGE): **HARNESS-UNKNOWN (wake-unproven)** × 2 consecutive runs, identical signature
  (charId 7, then charId 8; fresh accounts each):
  adopt ✓ / enter-world ✓ (`inWorld=True`, setup 2.0–2.3 s) / enroll ✓ (fresh, `activeBefore=False`) /
  fixture-stage ✓ (giver 24702, char on spawner to 0.1 m, snap==actor) /
  wake ✗ UNPROVEN: `wakeSeq 0→0`, `traceWakeSeq 0`, `questLegActive=False`, empty travel reason,
  quest-action audit rows at baseline (`questActionCount=0`, `accepts=0`, `discover=0/0/0`),
  while GLOBAL `stepped=True` (scheduler stepping stale leftover bots — contrast-only, never proof) /
  autonomous-accept ✗ (`active=False`). Timings: setup ~2 s / exec 30.2 s / wall ~32–33 s.
  Report: `/root/aaemu-e2e-q0/logs/g3-quest-autonomy-3512-report.json` (last write = run 2, charId 8).
- OUT-OF-RANGE (navigate → Arrived → Talk → Accept): **NOT RUN** (entry condition — in-range green — unmet).
- Pre-gate HARNESS-FAILs (2, wrong port map, no gate verdict): adopt-timeout on bridge 2234; login-frame
  timeout on login 2234. Resolved by authoritative port map (see B); not counted against the gate.

## F. Earliest failed boundary

**SCHEDULER — per-bot wake never delivered** (harness label: `HARNESS/wake-unproven`).
Reasoning: the per-bot `wakeSeq` decorator counter is lane-proven (roadmap §4 run 1: 1→92), so `0→0`
with an advancing global `TotalStepsRun` means OUR bot's `StepAsync` never ran within ~32 s of enroll —
not arbitration-denied (indistinguishable without a wake, and correctly not claimed), not
perception/selection/Talk/dispatch (never reached: `discover=0/0/0`, zero audit rows). Fresh-bot
starvation vs wake-drop vs dormancy is unverified — that diagnosis lives in scheduler core (banned
territory) and was not pursued. No defect was verified to a small minimal repair → NO fix attempted
(one-fix budget unspent by design, not by omission); no second test file; no rerun beyond the one
confirmatory repeat (stable signature → stop).

## G. Code changes (every file + why + slice membership)

NONE. Zero files added/modified/deleted by this lane (aside from this report, the one allowed docs write).
No fixture edit (fixture verified correct), no harness edit (fixed harness behaved as designed: derived
`HARNESS-UNKNOWN` instead of a false verdict), no production edit (no verified in-slice defect; scheduler
core explicitly out of scope).

## H. Evidence (logs/audit/JSON/timings)

- Gate report (run 2, charId 8): `/root/aaemu-e2e-q0/logs/g3-quest-autonomy-3512-report.json`
  (`verdict:HARNESS-UNKNOWN`, `failBoundary:HARNESS/wake-unproven`, setup 2.0 s / exec 30.2 s / wall 32.2 s;
  run 1 charId 7 near-identical: 2.3/30.2/32.6 s).
- Wake evidence string (both runs): `charId=[7|8] [stepped=False questLegActive=False accepts=0
  activeCount=0 money=0 wakeSeq=0 traceWakeSeq=0 snap=[15655.9,15172.5] actor=[15655.9,15172.5]
  discover=0/0/0 reason=[]] steppedGlobal=True`.
- Bootstrap game-side: `gameSide=1 [game-proc-environ pid=1278066]`.
- Fixture data queries (all against `AAEmu.Game/Data/compact.sqlite3`, md5
  `78b3bdbf038db3b927056106efdf91af` per report provenance): components/acts/detail/req rows and
  spawner coord cited in C; `offers(2425)` and L30 levels re-queried live this lane.
- Runner logs: `/tmp/g3-3512-run3.log` (baseline gate), `/tmp/g3-3512-run4.log` (confirmatory rerun);
  `/tmp/g3-3512-run2.log` (login-frame timeout on wrong port 2234).

## I. Architecture notes (slice-only)

1. q0 port map is now authoritative (B): future lane runs must use bridge 2260 / webapi 2280 /
   login-client 2237; 2234 is the login↔game internal link (accepts TCP, never answers bridge JSON or
   client login frames — silent 60 s/20 s timeouts, respectively).
2. `snap==actor` on both runs closes the roadmap §1.3b stale-position gap for the fresh-enroll +
   `teleportToNpc` path — no dual-`Character` divergence observed here.
3. The fixed harness's derived verdicts worked as designed: wrong-env runs fail at SETUP with no
   production claim; unwaked runs yield `HARNESS-UNKNOWN`, never a false PASS/FAIL.
4. Bootstrap arbitration preconditions for this bot are all met on paper (flag ON, alive, money 0 < 1000,
   no active quests) — the only missing input is a scheduler step for the new charId.

## J. Next smallest step (exactly one)

Attribute one scheduler step to a freshly enrolled networked bot: enroll, then trace whether the
scheduler's per-bot registry/cadence/dormancy path ever schedules that charId within 60 s while global
`TotalStepsRun` advances — owner: scheduler/runtime layer (quest slice stays read-only observer).
