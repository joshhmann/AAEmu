# Fresh-Bot Scheduler Path — Final Report (§10)

Diagnosis doc: `fresh-bot-scheduler-path.md` (§§1–5). Diagnostic implemented per its §4,
lane redeployed + rerun 2026-09-19. No behavior change: reschedule logic, lifecycle,
arbitration, and quest code untouched.

## A. Enrollment path (diagnosis §1 edge table, condensed)

| # | Edge | Site | Effect |
|---|---|---|---|
| 1 | TCP login + enter-world | `CharacterLifecycleService.ActivateHuman` (`CharacterLifecycleService.cs:77-97`) | `EnterWorld`, live session in `GameConnectionTable` |
| 2 | Bot resolution | `BotDriveBridge.TryResolveNetworkedBot` (`BotDriveBridge.cs:3035-3056`) | match by `ActiveChar.Name`; Err unless in-world |
| 3 | Bridge `quest wake` | `BotDriveBridge.cs:4375-4411` | `Spawn` → `Activate` → `scheduler.Wake` → poll **GLOBAL** `TotalStepsRun` (any bot's step satisfies it — masks per-bot starvation) |
| 4 | Manager store | `PlayerBotManager.Spawn` (`PlayerBotManager.cs:36-52`) | `TryAdd` runtime, state `Registered`; **no scheduler contact** |
| 5 | Active marking | `PlayerBotManager.Activate` (`PlayerBotManager.cs:54-101`) | `State = Active` on success |
| 6 | Headless embodiment | `ActivateHeadless` → `EnterWorld(character, null)` (`CharacterLifecycleService.cs:180-192`) | `character.Connection = null` (clobbers TCP back-pointer; non-blocking for steps) |
| 7 | Wake | `PlayerBotScheduler.Wake` (`PlayerBotScheduler.cs:189-195`) | `_events.Enqueue` only; no registration check, no reschedule |
| 8–9 | Scan: event→due→lease | `RunScanCycle` (`:250-282`), `Schedule` (`:496-517`) | first due at next 100 ms scan; per-bot lease |
| 10 | Handoff → marshal | channel cap 256 (`:284-297`) → `WorkerLoopAsync` (`:323-333`) | workers are pure wake producers into `_tickQueue` |
| 11 | Boundary dispatch | `DrainTickQueue` (`:544-560`, 10 ms tick) → `ExecuteStepOnExecutionBoundary` (`:343-458`) | game-loop thread; `ExecutionBoundary` pin |
| 12 | **Registry gate** | `:359-364` | `TryGet` + `State == Active`, else `TotalStepsSkipped++` + lease release + silent `return` (no reschedule, formerly no log) |
| 13 | Step dispatch | `:398-405` → `BotGoalArbiterStepExecutor.StepAsync` | `Arbitrate` → `wakeSeq AddOrUpdate` → inner roam step |
| 14 | Next wake | `:429-451` | reschedule only if `ok && nextDelay is { }`; skip/fail/timeout/dormant → dormant until external `Wake` |

State taxonomy: Enrolled (registry) ≠ Active (embodied) ≠ Due (scheduler queues) ≠ Awake
(queued/in-flight) ≠ stepped (`wakeSeq ≥ 1`). Single-shot wakes are use-once: any terminal
wall (skip/fail/timeout/dormant) is permanent for that wake.

## B. Working-vs-failing comparison + first divergence

Post-first-step bots (presence-demo roam, `BotPresenceCoordinator.cs:396-429`) self-perpetuate
via edge-14 reschedule and own the advancing global counter. A fresh bot must survive the
one-shot funnel (schedule→pop→marshal→drain→gate→arbitrate) exactly once. First divergence
candidate: the boundary gate (edge 12) — code-identical pre-boundary path for fresh and stale
ids (singletons per `Program.cs:245-249,269-288,328-332,345-346`; no per-bot pre-boundary drop;
saturated channel self-heals). Excluded by code: instance splits, decorator/actor key mismatch,
second-actor reads, quest-extraction interference, channel/drain block, Wake-time registration
states, inner-step throw before `wakeSeq` increment.

## C. Earliest boundary + post-diagnostic refinement

- **Primary: `DEQUEUED_NOT_DISPATCHED`** — wake consumed by the registry gate (`:359-364`),
  one skip permanently kills a one-shot wake; matched `wakeSeq 0→0` across two independent
  30 s wakes with an intervening level/position change (state-independent wall).
- **Runner-up (not excludable pre-diagnostic): `DISPATCHED_NOT_STEPPED`** — `Activate` rethrow
  inside arbitration (`BotGoalArbiter.cs:196-213` → `TotalStepsFailed`, no reschedule). Both
  shapes were byte-identical in observables (`wakeSeq` 0, no rows, advancing globals).
- **Refinement (diagnostic verdict, fresh charId=14, 2026-09-19 19:29:52Z): the primary did
  NOT reproduce on the current lane.** One production `quest wake` → per-bot record
  `LastOutcome=Ran, Ran=1, Skipped=0, Failed=0, TimedOut=0`; globals
  `TotalStepsSkipped=0, TotalStepsFailed=0, TotalStepsTimedOut=0` with `TotalStepsRun`
  1054→1055 on our wake. **Surviving hypothesis: neither — the wake
  DISPATCHED_AND_STEPPED.** Skip path (primary) excluded by `botStepsSkipped=0` + global
  `TotalStepsSkipped=0`; runner-up excluded by `botStepsFailed=0`. The §3 wall was either
  lane-state-specific or already resolved upstream; the scheduler funnel is now proven
  delivery-capable for fresh networked ids.

## D. Micro-gate PASS (2 runs, adopted q0 lane, test unmodified)

- Run 1 (pre-diagnostic binary, charId=10, wall 13.2 s): PROVEN per-bot via
  `traceWakeSeq=108` above enroll baseline (`stepped=False`, `reason=[]` — dispatched
  through the actor, quest leg inert).
- Run 2 (diagnostic binary, charId=11 `SchedGate192717`, wall 4.1 s, setup 3 s / exec 1 s):
  PROVEN per-bot via `questTravelReason=[walking to quest target at (15562,15354) from
  (15578,15382)]` in 1.0 s; enroll `steppedGlobal=False`.
- Both: bootstrap game-side `1`, per-bot signals only (`stepped` global contrast-only).

## E. Production fix — diagnostic ONLY

1. `AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs` (+62): one `Logger.Debug` on the
   skip branch (bot id + `TryGet-miss` vs `State` value); new
   `ConcurrentDictionary<uint, BotStepOutcome>` (last outcome + UTC + per-outcome counts)
   written at the four terminal sites only (skip `:361`, timeout `:411`, fail `:418`,
   run `:427`); public `GetBotStepState(uint)` query. O(bots) memory, no hot-path
   allocation. **Reschedule logic, lifecycle, arbitration untouched.**
2. `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs` (2 hunks): quest-observe payload gains
   global `totalStepsSkipped/Failed/TimedOut` beside `stepsBefore/stepsAfter`, plus the
   per-bot `botLastStepOutcome/botLastStepUtc/botSteps{Skipped,Failed,TimedOut,Ran}` for
   the observed charId. Read-only; one wake + one read discriminates skip/fail/timeout.
Build: `AAEmu.Game` Release, 0 errors. Lane republished (E2E_REBUILD-equivalent), stamp
recomputed byte-exact, log caps re-applied; game rebooted via `E2eStack.RestartGameServer`
(pid 4109252, `AAEMU_QUEST_BOOTSTRAP_ENABLED=1`, token `e2e-q0-pilot-token` preserved in
`/proc` environ, `Server started!`); MySQL + login + DB volume untouched.

## F. G3 rerun — FAIL-BEHAVIOR at a NEW downstream boundary (stop)

`G3AutonomousQuestAccept3512Tests`, unmodified, charId=12 (`G3Auto3512192740`, fresh account,
level 10, staged ≤25 m of true giver 3512/obj 37273, 30 s budget, wall 11.5 s):
verdict **FAIL-BEHAVIOR**, `failBoundary=DISCOVERY/no-legal-proposal`. Wake leg PROVEN
per-bot (`stepped=True`, snap==actor pos `[15645.0,15193.7]`, travel reason set) — scheduler
delivery is not the wall. Discovery swept 25 m with `MaxQuestDiscoverRange`, produced
**zero in-band offers** (`discover=0/0/0`, quest 251 L2 ∈ band [1,9] unproposed); no Dispatch
ran (`questLegActive=False`, `accepts=0`, `questActions=0`); travel fallback armed toward the
**3597 (Mor) spawner at (15562,15354)** (`SolzreedStarterCorridorProfile.cs:25`) — i.e. the
quest-330 chain head, not 251's giver. Refined boundary: **discovery-at-staged-giver**.
No further scheduler work is indicated; stop per budget.

## G. Evidence

| Item | Value |
|---|---|
| charIds | 10 (gate run 1), 11 (gate run 2), 12 (G3), 14 (counter probe) |
| Counter proof (charId=14) | `Ran=1, Skipped=0, Failed=0, TimedOut=0`; globals `Skipped=0, Failed=0, TimedOut=0`, steps 1054→1055 |
| Timestamps (UTC) | gate run 2: 19:27:17Z; G3: 19:27:40Z; probe outcome: 19:29:52.6465976Z |
| Lane reports | `/root/aaemu-e2e-q0/logs/scheduler-microgate-report.json`, `/root/aaemu-e2e-q0/logs/g3-quest-autonomy-3512-report.json` (copies in `/tmp/q0-*-before/after.json`) |
| Provenance | source `6c3efe25ecb68998544eb3f75699bc1674c7eef1`; runtime sqlite md5 `78b3bdbf038db3b927056106efdf91af` |
| Game boot | pid 4109252, `game-restart.log` shows `Server started!` |

## H. Files changed

- `AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs` — §4(1) diagnostic (mine, 6 hunks).
- `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs` — §4(2) payload (my 2 hunks; remaining
  hunks in that file's working-tree diff are pre-existing and untouched).
- Nothing else: two throwaway tests (lane restart, counter probe) created, run, and deleted;
  all other worktree dirt (scorecard notes, sibling test files, playertrace outputs) is
  pre-existing and untouched.

## I. Next smallest step (exactly one)

Sweep-IO diagnostics (§6.1 proposal): instrument the discovery sweep at the staged giver so
the next run records WHY zero in-band offers were proposed (F1 sub-cause) — the live
boundary is now discovery-at-staged-giver, and nothing else should be touched until that
signal exists.
