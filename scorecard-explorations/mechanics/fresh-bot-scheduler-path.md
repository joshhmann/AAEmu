# Fresh-Bot Scheduler Path — why a fresh networked bot never gets its StepAsync (develop @ 2026-09-19)

CODE-ONLY diagnosis. Nothing modified, built, or run. Every claim carries a `file:line` cite against
`/root/aaemu-dev` branch `develop`. Anything not decidable from code is marked UNKNOWN.
Fixture (quest 251 @ giver 3512) is CORRECT and NOT in question (see
`scorecard-explorations/mechanics/nearby-quest-resolution.md` §C). Quest behavior is NOT in question.
Locked evidence: fresh TCP-connected networked bot, enrolled+activated, wake requested, 30 s observed:
`wakeSeq 0→0`, `questLegActive=false`, zero audit rows for our charId, while global `TotalStepsRun`
advances (`/root/aaemu-e2e-q0/logs/g3-quest-autonomy-3512-report.json`: charId 8, `steppedGlobal=True`).

## 1. ENROLLMENT PATH (TCP login → first due time → Wake semantics)

| # | Edge | Exact method / class | What happens | Thread / side effect |
|---|---|---|---|---|
| 1 | TCP login + enter-world | test `BotNetworkSession.ConnectAsync` → server `CSSelectCharacterPacket` → `CharacterLifecycleService.ActivateHuman` (`AAEmu.Game/Core/Managers/CharacterLifecycleService.cs:77-97`) | `EnterWorld` + `connection.ActiveChar = character` (`:82`) + `AssignObjId` (`:83`) + `WorldManager.TryAddCharacter` (`:85`) | Game network thread; real session in `GameConnectionTable` |
| 2 | Bot resolution | `BotDriveBridge.TryResolveNetworkedBot` (`AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:3035-3056`) | first live `GameConnection` whose `ActiveChar.Name` matches → `character = connection.ActiveChar`; Err unless in-world | Bridge thread; read-only |
| 3 | Bridge `quest wake` | `BotDriveBridge.cs:4375-4411` | resolve → `manager.Spawn` (`:4387`) → `manager.Activate` (`:4394-4396`) → `scheduler.Wake` (`:4398`) → poll **GLOBAL** `TotalStepsRun` (`:4401-4411`, breaks on ANY bot's step) | Bridge thread |
| 4 | Manager store | `PlayerBotManager.Spawn` (`AAEmu.Game/Core/Managers/Bots/PlayerBotManager.cs:36-52`) | `ConcurrentDictionary.TryAdd(character.Id, new PlayerBotRuntime(character, owner))`; state `Registered`. **No scheduler contact** (ctor takes lifecycle only, `:31-34`) | Bridge thread |
| 5 | Active marking | `PlayerBotManager.Activate` (`PlayerBotManager.cs:54-101`) | `Registered`/`Deactivated` → `_lifecycle.ActivateHeadless` → on success `State = Active` (`:91-95`) | Bridge thread |
| 6 | Headless embodiment of a LIVE networked char | `PlayerBotLifecycleAdapter.ActivateHeadless` (`PlayerBotLifecycleAdapter.cs:44-69`) → `CharacterLifecycleService.ActivateHeadless` (`CharacterLifecycleService.cs:100-114`) → `EnterWorld(character, null)` (`:180-192`) | `character.Load()` re-run (`:185`), **`character.Connection = null` (`:186`) — clobbers the live TCP back-pointer**, `AssignObjId` (reuses id, `:199-210`), `TryAddCharacter`, `character.Spawn()` (`PlayerBotLifecycleAdapter.cs:56-57`) | Bridge thread (engine mutation, but pre-step) |
| 7 | Wake | `PlayerBotScheduler.Wake` (`PlayerBotScheduler.cs:189-195`) | `_events.Enqueue(characterId)` ONLY; `false` solely when stopped (`:191-192`). **No registration check, no state mutation, no reschedule, no signal** | Caller (bridge) thread |
| 8 | First due time | `RunScanCycle` event drain (`PlayerBotScheduler.cs:250-252`) → private `Schedule(botId, now)` (`:496-517`) | first due = the scan cycle that drains the event (scan cadence 100 ms default, `PlayerBotSchedulerOptions.cs:14`); dedup keeps earliest (`:510-511`); leased → fold to `_pendingWake` (`:503-508`) | Scan-loop thread |
| 9 | Due-pop + lease | `RunScanCycle` (`:254-282`) | pop all `due <= now`; stale-entry skip (`:262-264`); leased → fold (`:266-272`); else `_leases.TryAdd` + batch (`:274-276`) | Scan thread |
| 10 | Handoff → marshal | batch → bounded `Channel` (cap 256, `PlayerBotSchedulerOptions.cs:31`); saturated → lease released + reschedule-now (`:284-297`, self-healing) → `WorkerLoopAsync` (`:323-333`) pure-producer into `_tickQueue` | Worker pool threads (4 default, `:11`); never touch Character/world | — |
| 11 | Boundary dispatch | `TickDrain` → `DrainTickQueue` (`:544-560`, every 10 ms default, `PlayerBotSchedulerOptions.cs:20`, subscribed inline `useAsync:false` when `SubscribeToTickManager` default true, `:28` + `:143-144`) → `ExecuteStepOnExecutionBoundary` (`:343-458`) | game-loop thread; `ExecutionBoundary` pin (`:351`) | — |
| 12 | Registry gate | (`:359-364`) `TryGet` + `State == Active`, else `TotalStepsSkipped++` + `ReleaseLease` + **`return` — NO reschedule, NO log line** | Boundary thread | — |
| 13 | Step dispatch | `_executor.StepAsync(...).WaitAsync(...).GetAwaiter().GetResult()` (`:398-405`) → `BotGoalArbiterStepExecutor.StepAsync` (`BotGoalArbiterStepExecutor.cs:46-63`): `Arbitrate` (`:54`) → `wakeSeq AddOrUpdate` (`:58`) → stage (`:59-60`) → inner `BotRoamStepExecutor.StepAsync` (`BotRoamStepExecutor.cs:508-517`, actor keyed by uint id, `:371-382`) | Boundary thread | — |
| 14 | Next wake | (`:429-451`): lease removed; `_pendingWake` honored (`:442-446`); else reschedule **only if `ok && nextDelay is { }`** (`:447-451`); `ok=false` (fail `:415-419`, timeout `:408-414`) or `null` (dormant) → **never again without an external `Wake`** | Boundary thread | — |

### State taxonomy (distinct by construction)

- **Enrolled** = present in `PlayerBotManager._registry` (`Registered`; `PlayerBotRuntime.cs:8-18,27-62`).
  Enrolled says nothing about embodiment or scheduling.
- **Active** = manager `State == Active` (embodied via `IPlayerBotLifecycleService`). The scheduler's
  ONLY consumption of the registry is the boundary gate at edge 12.
- **Due** = present in scheduler `_scheduled`/`_due` (private, `:51`). Created solely by
  `Wake`→event→`Schedule` or `WakeAt`/`WakeAfter` (`:198-203`).
- **Awake** = event queued / step scheduled-or-in-flight. **Awake ≠ stepped**: awake is a queue
  membership; stepped is observable only via `BotGoalArbiterStepExecutor.GetWakeSequence`
  (`BotGoalArbiterStepExecutor.cs:33-35`, `0 = never stepped through this decorator`).
- **Can a bot be manager-active but scheduler-absent? YES — it is the default.** `Spawn`/`Activate`
  never touch the scheduler; after any step returning `null`, after any fail/timeout, and after any
  boundary skip, an Active bot sits in the registry with zero scheduler entries until an external
  `Wake` re-arms it (edges 12/14). Single-shot wakes are therefore use-once: any terminal wall
  encountered (skip/fail/timeout/dormant) is permanent for that wake.

## 2. WORKING-vs-FAILING COMPARISON

KNOWN-WORKING (code-verified, instance-UNKNOWN): any bot past its first step whose last step returned a
non-null cadence — e.g. presence-demo bots (`BotPresenceCoordinator.cs:396-429`:
`Spawn`→`Activate`→fidelity `Reduced→Full`→`SetRoamRoute`→`Wake`) whose roam steps return
`ActiveCadence` and are thus rescheduled forever (edge 14). They are the most plausible owners of the
advancing global counter; which charIds step is UNKNOWN from code (see §4). Per-wake singletons are
shared (`Program.cs:245-249` manager, `:269-288` roam executor factory, `:328-332` decorator,
`:345-346` scheduler — all `AddSingleton`; production never `new`s a manager/scheduler), so there is
NO instance-split attribution bug: bridge, scheduler, and metrics all share one registry, one
scheduler, one decorator.

| Checkpoint | KNOWN-WORKING (post-first-step bot) | FRESH-G3 (charId 8) | Same? |
|---|---|---|---|
| TCP enter-world | n/a (headless provisioned) | `ActiveChar` set, in-world ✓ (report `inWorld=True`) | n/a — divergent entry, converges below |
| Manager `Spawn` | ok (`BotPresenceCoordinator.cs:396-400`) | ok (enroll returned `Ok`; any refusal is `Err`, `BotDriveBridge.cs:4387-4390`) | SAME |
| `Activate` → `State==Active` | ok (`:402-406`) on a headless char | ok (enroll `Ok`, `PlayerBotManager.cs:91-95`); side effect: live `character.Connection = null` (edge 6) — impairs TCP send path, NOT scheduler gating | SAME registry outcome; extra networked-only side effect, non-blocking for steps |
| `scheduler.Wake` | `true`, event enqueued (`:428`) | `true`, event enqueued ×2 (enroll `waitMs=1000` + START `waitMs=30000`, `G3AutonomousQuestAccept3512Tests.cs:159-175,202-226`); `false` only if stopped | SAME |
| Scan: event→`Schedule(now)` | due created | due created (no per-bot branch; `Wake` has no registration check) | SAME (code-identical) |
| Due-pop + lease | leased, batched | leased, batched (fresh id ⇒ never leased; stale-skip `:262-264` needs a superseding `Schedule` inside the same cycle — impossible on the single scan thread when all callers use `Wake`-only; channel-full `:286-296` self-heals via reschedule) | SAME (no per-bot drop exists pre-boundary) |
| Worker marshal → `_tickQueue` | marshalled (workers/channel proven alive: global `TotalStepsRun` advances within 0.2 s at enroll) | marshalled (same channel, same workers, same drain) | SAME |
| `DrainTickQueue` on boundary | drained, `TryGet`+`Active` ✓ | **FIRST DIVERGENCE (candidate A)** — see §3 | UNKNOWN per-bot (no telemetry) |
| Decorator `Arbitrate` | steady-state `Unchanged`, `Activate` never called (`BotGoalArbiter.cs:190-191`) | first wake is always the TRANSITION path (`_activeActivity` empty); winner on verified config is `QuestBootstrap` (58): Schedules off (`SchedulePhaseActivityModule.cs:80-81`), Recovery healthy-deny, Conflict no-conflict-deny, FishingContest off, Homestead `Enabled=false` default (`HomesteadActivityModule.cs:27` → deny `:39-40`), QuestBootstrap Allows (flag ON game-side, alive, money 0 < 1000, `QuestBootstrapActivityModule.cs:64-90`); its `Activate` is an infallible identity return (`:93-98`) | SAME outcome (no throw available on the elected path) |
| `wakeSeq` increment (`:58`) | ≥1, cycles forever via edge-14 reschedule | **0 after ~300 scan chances** (100 ms scan × 30 s) and 2 separate wakes with an intervening fixture change (level 1→10, spawn→giver) — wall is state-independent | DIVERGED (observed) |
| Inner step / audit / quest | runs, audits, roams | never reached (`questLegActive=false`, `discover=0/0/0`, 0 rows, `active=False`) | DIVERGED (consequence) |

Why stale bots keep stepping while the fresh bot never starts: once ANY bot completes one step with a
non-null delay it is self-perpetuating (edge 14); a fresh bot must survive the one-shot funnel
(schedule→pop→marshal→drain→gate→arbitrate) exactly once with no retry. The bridge's `quest wake`
poll loop (`BotDriveBridge.cs:4401-4411`) additionally masks the starvation: it reports
`stepped=true` on the FIRST global step (stale bots satisfy it in ~0.2 s at enroll), so a dead
fresh wake looks alive at the bridge layer. (The G3 test correctly ignores this flag and polls
per-bot signals — `G3AutonomousQuestAccept3512Tests.cs:365-385`.)

Excluded by code (with cites): duplicate scheduler/manager instances (singletons, `Program.cs:245-249,345-346`;
no manual construction in `AAEmu.Game`); decorator/actor key mismatch (both keyed by uint id:
`BotGoalArbiterStepExecutor.cs:58`, `BotRoamStepExecutor.cs:375-379`); second-actor reads (bridge reads
the same singleton executor's state, `BotDriveBridge.cs:4377,4419`); quest-extraction interference
(`BotBehaviorRuntime`/`QuestBehavior` sit strictly below decorator entry — `BotRoamStepExecutor.cs:504-505`
— and cannot keep `wakeSeq` at 0); head-of-line channel/drain block (saturated channel reschedules,
`:290-295`; drain executes every queued step, `:558-559`; a hung step would freeze the GLOBAL counter
via the synchronous `GetResult`, `:398-405`); `NOT_REGISTERED`/`REGISTERED_NOT_ACTIVE` at Wake time
(enroll `Ok` proves `Spawn`+`Activate` succeeded); `DUE_NOT_DEQUEUED` (no per-bot drop pre-boundary, §2
table); inner-step throw (would leave `wakeSeq ≥ 1` since `:58` precedes `:62`).

Residual non-excluded alternative: `DISPATCHED_NOT_STEPPED` via an `Activate` rethrow
(`BotGoalArbiter.cs:196-213` → scheduler `TotalStepsFailed`, no reschedule) — requires arbitration to
elect a non-identity `Activate` (`PresenceRoam.ResumeRoam`, `BotPresenceCoordinator.cs`-style route arm,
`PresenceRoamActivityModule.cs:55-73`), which requires `QuestBootstrap`+`NeedsFarm`+`Homestead` to ALL
deny contrary to verified preconditions. Possible only on unwired pressure/probe inputs — UNKNOWN,
strictly less likely than the primary. A director true-dormancy `Dematerialize`
(`PopulationDirector.cs:726-735,764-783` → `DormantBotRegistry.Dematerialize`, `DormantBotRegistry.cs:260-274`
→ `manager.Deactivate` → `character.Delete()` on the LIVE session) is a second UNKNOWN: it needs
`EnableTrueDormancy` + N no-human sweeps with 2-sweep hysteresis (`:716-719`) while the bot's own live
session is feet away — timing-plausible over 30 s but provider-dependent (`_humanSnapshotProvider`),
and it would visibly desync the TCP session. Neither alternative is verifiable code-only.

## 3. CLASSIFICATION

**Primary: `DEQUEUED_NOT_DISPATCHED`.** The scheduled wake reaches the execution boundary and is
silently consumed by the registry gate (`PlayerBotScheduler.cs:359-364`): `TryGet` miss or
`State != Active` → `TotalStepsSkipped++`, lease released, return — no reschedule, no log line, no
per-bot record. One such skip permanently kills a one-shot wake, which matches `wakeSeq 0→0` across
TWO independent wakes 30 s apart with an intervening state change (level/position), i.e. a
state-independent wall on the scheduler side of the funnel. The exact pre-drain trigger that flips
(or misses) `State` for a fresh networked id is UNKNOWN from code (no fitting `Deactivate` caller is
verifiable in-window; `BotAdminService` gm-remove and harness `deactivate` — persistent-sessions-only,
`BotDriveBridge.cs:2632-2633` — provably do NOT touch networked bots).
**Cannot-exclude runner-up: `DISPATCHED_NOT_STEPPED`** (Activate-rethrow shape above).
Both produce byte-identical observables today (`wakeSeq 0`, no rows, advancing globals), which is
itself the finding: the scheduler cannot currently distinguish them per-bot (see §4).

## 4. SMALLEST DIAGNOSTIC ADDITION (proposal only — DO NOT implement; sibling may own it)

Gap: `GetMetrics` (`PlayerBotScheduler.cs:209-239`, contract `IPlayerBotScheduler.cs:52-56`) exposes
only GLOBAL counters (`TotalStepsRun/Skipped/Failed/TimedOut/DuePopped/...`) — there is NO
per-character registered/due/dequeued/dispatched/stepped signal anywhere, and the skip path (`:359-364`)
is the only terminal site without even a log line (fail `:415-419` logs `Error`, timeout `:408-414`
logs `Warn`). Minimal proposal, two parts, both read-only to stepping behavior:

1. `AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs`, method `ExecuteStepOnExecutionBoundary`
   (`:343-364`): on the skip branch (`:360-363`) add one `Logger.Debug` (bot id + whether `TryGet`
   missed vs `State` value) and record the outcome into a new
   `ConcurrentDictionary<uint, BotStepOutcome>` entry (last outcome + UTC + counts), updated at the
   four terminal sites (`:361` skip, `:411` timeout, `:418` fail, `:427` run). Expose via a new
   `GetBotStepState(uint)` query (or extend `PlayerBotSchedulerMetrics` with an optional per-bot
   section) — O(bots) memory, no hot-path allocation (update only on step completion/skip).
2. `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs`, `quest observe` payload (`~:4418-4470`): include
   the GLOBAL `TotalStepsSkipped/Failed/TimedOut` trio beside `stepsBefore/stepsAfter` so the next
   gate run discriminates skip-vs-fail-vs-timeout without any new plumbing (one wake + one read).

With (1), re-running the gate attributes charId N to exactly one of
registered/due/dequeued/dispatched/stepped within one wake; with (2) alone, the existing harness
already narrows it to skip/fail/timeout buckets.

## 5. FIX POINTER

**NO-FIX (reason: no single verified defect).** The evident defect SHAPE is the reschedule-less skip
at `PlayerBotScheduler.cs:359-364`, but the correct repair depends on the §4 diagnostic: re-queuing
skipped bots unconditionally would fight legitimate `Deactivate` (whose lifecycle path `Delete()`s the
character, `CharacterLifecycleService.cs:117-172`) and could resurrect intentionally-retired records;
doing nothing preserves silent one-shot starvation. Candidate fix, contingent on diagnostic proof that
fresh wakes die at the gate while `State==Active` at `Wake` time (i.e. a drain-side race or
identity mismatch rather than a genuine deactivation): bound the skip (requeue at most K times with
`ScanInterval` backoff, then park as dormant with a per-bot record from §4). Exact site if owned:
`PlayerBotScheduler.cs:359-364` (`ExecuteStepOnExecutionBoundary`). Until the §4 signal exists,
any production edit risks fixing the wrong gate — implement the diagnostic first.

---
*Method: read-only source reads on branch develop; nothing built or run. Per-bot runtime behavior
(who steps, pressures, sweep timing, live log lines) is live-state and marked UNKNOWN throughout —
no claim above depends on it.*
