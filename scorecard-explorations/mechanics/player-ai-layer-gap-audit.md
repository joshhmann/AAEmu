# Player AI layer gap audit — foundation and execution first

Read-only audit. Evidence class: **source read at `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8`
(dirty working tree, 131 modified + 21 untracked paths), 2026-10-16. No live run, no build,
no test execution was performed for this document.** Every claim below is a code/textual
claim with an anchor; where behaviour was not exercised, the item is marked *wired but
unproven*. `UNKNOWN` is used where the audits could not determine an answer.

Scope: seven read-only seam audits (lifecycle, knowledge/continuity, choice/planning,
execution/recovery, orchestration, control plane, direction) consolidated against the
five-layer framing the principal supplied:

| Layer | Principal's label | Contents |
|---|---|---|
| L1 | FOUNDATION | Character lifecycle · World state · Perception · Persistence · Game APIs |
| L2 | SOLIDIFY FIRST | AI Execution Runtime — Intent dispatch · Action state · Cancellation · Recovery · Results |
| L3 | BUILD INCREMENTALLY | Gameplay Capabilities — Combat · Questing · Gathering · Trading · Equipment |
| L4 | FUTURE | Decision Brain — Utility AI · GOAP · Behaviour Trees · Goal Prioritization |
| L5 | FUTURE | Advanced Intelligence — Long-term goals · Personalities · Social relationships · Economic decisions |

**Headline.** L4 and L5 are *already implemented* in this repository (a 23-file `Goap/`
tree, a banded `BotGoalArbiter`, nine activity modules, a chatter engine). They are not
the gap. The gap is that L1 and L2 — the facts a decision needs and the runtime that
carries an intent to a verified result — are the parts that are thinnest, and the
decision brain is already consuming inputs L1 does not supply.

---

## L1 — Foundation

### L1.1 Character lifecycle: real pipeline, three unsafe edges

Working: enroll → embody → wake → tick → retire with a single game-loop execution
boundary, per-bot generation stamping, and event-driven wakes (`PlayerBotScheduler.cs`,
901 lines; `PlayerBotManager.cs`, 407; `PlayerBotLifecycleAdapter` performs the real
`character.IsOnline = true; character.Spawn()`). A bot needs no network client by design
(`HeadlessSession.cs:22-30`; production path `Provision` `:145-325` creates a real
`aaemu_login.users` row with `account_type=HeadlessBot, banned=1` plus a real
`characters` row).

| # | Gap | Evidence | Severity |
|---|---|---|---|
| L1-a | **No presence teardown at all.** `BotPresenceCoordinator` has only `Start`; nothing in production calls `IPlayerBotScheduler.StopAsync` (only the E2E bridge does). Embodied presence bots stay `Active` through shutdown and their queued steps are dropped while registry entries remain. | `BotPresenceCoordinator.cs:309-360`; `PlayerBotScheduler.cs:168-206`; `BotDriveBridge.cs:4612` | High |
| L1-b | **Teardown cannot converge.** A refusal during `TryRetireNativeWork` lands the runtime in `DeactivationFailed`, which is un-removable (`Remove` refuses it) and un-reactivatable (`Activate` requires `Registered`/`Deactivated`). Reachable whenever the scheduler is not running — `TryRetireNativeWork` returns false on `!IsRunning`, which is the default wiring. | `PlayerBotManager.cs:119-184, 194-203, 350-360`; `PlayerBotScheduler.cs:211-214` | High |
| L1-c | **Whole seam is default-OFF.** The only arming paths are presence bootstrap, proximity bootstrap and admin/GM; `Program.cs:434-436` records "Not auto-started". An ordinary deployment has an inert scheduler: nothing provisions, nothing wakes. | `BotPresenceBootstrap.cs:23-30`; `PopulationDirector.cs:957-958`; `BotScheduleService.cs:94-101`; `BotChatterOptions.cs:22-27` | High |
| L1-d | **Four independent `Start()` owners, no disarm.** Presence, proximity bootstrap, admin `ArmRoam` and the control host can each arm the scheduler; nothing can stop it. | `PlayerBotScheduler.cs:129`; `BotChatterService.cs:112`; `BotScheduleService.cs:103`; `PopulationDirector.cs:613, 980` | Medium |
| L1-e | **Re-enrollment duplicates provisioning.** `BotPresenceCoordinator.Start` guards only on `EmbodiedCount >= config.BotCount`; account names are positional, so a second `Start` with a different count/roster provisions additional rows. | `BotPresenceCoordinator.cs:313-317, 363, 457`; `BotAdminService.cs:217-235, 515+` | Medium |
| L1-f | **Stale arbiter memory across re-enrollment.** `IBotGoalArbiter.Forget` is documented as deactivation cleanup and has **no caller**; a re-enrolled bot can be reported `Unchanged` so the winning module's `Activate` never re-runs. | `BotGoalArbiter.cs:93-94, 270-273, 344` | Medium |
| L1-g | **Schedule split-brain.** The tick-driven `BotScheduleService` writes the roam route directly with no lease, arbiter or generation check, while the arbiter's own `SchedulePhaseActivityModule` self-denies in exactly that configuration. No single writer for `SetRoamRoute`. | `BotScheduleService.cs:205-240`; `SchedulePhaseActivityModule.cs:87-88`; `BotScheduleBehavior.cs:53-71` | High (architectural) |
| L1-h | **Per-bot anchors never produced** — every bot shares one template day, and "Work" means "roam around home". | `BotDailyAnchors.cs:42-64`; `SchedulePhaseActivityModule.cs:170-173`; `BotScheduleService.cs:234-237` | Medium |

`PopulationDirector` is a per-bot admission and cost controller (fidelity tier per
`CharacterId`, pressure bands, staggered wake, proximity sweep) — it never reads party,
formation, goal or activity identity, and its activity dimension is structurally dead
(ctor default `_activityResolver = _ => null`; DI cannot inject the unregistered
`Func<Character,string?>`). [INFERENCE from DI shape; not runtime-confirmed]
`PopulationDirector.cs:46, 101-102, 109, 187-290`.

**A1 resolution (2026-10-16) — converging teardown with one production stop owner.**
`PlayerBotScheduler.Disarm(string reason)` (AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:219)
is now the single production stop owner and the only thing that returns
`BotHostTeardownReport` (new file `AAEmu.Game/Core/Managers/Bots/BotHostTeardownReport.cs`:
reason, whether the host was running, queued steps dropped, bots deactivated, bots failed,
per-bot failure list, `Converged => BotsFailed == 0`). Its production caller is the ordinary
host shutdown: `GameService.StopAsync` (AAEmu.Game/GameService.cs:162) calls
`DisarmBotHost("host-shutdown")` FIRST — before the save manager and the networks stop — and
logs `Bot host disarmed on shutdown: {Report}` when it converged or
`Bot host teardown did NOT converge: {Report}` when it did not (GameService.cs:193-205). The
operator route `POST /api/bots/host/stop` (BotControlController.cs:174) returns the same report
and is a separate entry point. The queued steps the stopped game loop can no longer run are
dropped with a record (`PlayerBotScheduler.DrainDroppedSteps`, plus `StopAsync` draining under
`scheduler-stopped`), and every enrolled bot leaves through the ordinary manager path
(`PlayerBotManager.Deactivate` → its own leave-save) instead of staying `Active` (L1-a).

**Owner inventory (checked, not asserted).** Every arming path calls `Start()` on the same DI
singleton (`Program.cs:437`): admin `BotAdminService.cs:463`, `:477`, `:490`, `:689`; presence
`BotPresenceCoordinator.cs:357` (manifest) and `:452` (legacy citizen loop); proximity
`PopulationDirector.cs:980`; control host / E2E bridge `BotDriveBridge.cs:130`. The `Disarm`
callers are host shutdown (`GameService.cs:201`) and the token-gated route
(`BotControlController.cs:174`). The one other `StopAsync` caller,
`BotDriveBridge.cs:4612` (`quest prepare-end`), is deliberately not the owner: it is gated on
`E2E_QUEST_END_BOT` plus exactly one persistent enrollment plus a matching generation and
intentionally leaves that single bot `Active` for a frozen-end observation. No separate
`BotHostControl` type was added (the card's A1 note named one) because a façade over the same
singleton would only rename the owner; if a distinct type is wanted that is a refactor.

**The live lane found the ordering defect this resolution had to fix.** The first live run
(`artifacts/bot-host-teardown-live/`, lane `/root/aaemu-e2e-teardown`) proved the owner was
wired — SIGTERM reached `GameService.StopAsync` and the host reported — but reported
`disarm(host-shutdown): running=True deactivated=0 failed=2` with
`PlayerBot deactivation failed: character 1 (Native retirement was refused or abandoned)` for
both bots: the first version of `Disarm` stopped the scheduler before asking the bots to leave,
and the retirement seam refuses outright once the host is not running
(`PlayerBotScheduler.TryRetireNativeWork` :321) and runs on the execution boundary that stop
tears down. `Disarm` now asks every `Active` bot to leave FIRST, while the host still owns that
path, and only then stops and drains. The ordering is pinned by a red-capable unit test
(`PlayerBotSchedulerDisarmTests.Disarm_AsksEachBotToLeave_WhileTheHostIsStillRunning`, which
witnesses the host's running flag at leave time and fails under the old order). Recorded limits:
shutdown latency scales with the population (one blocking retirement round-trip per bot, bounded
by the scheduler's `ShutdownTimeout`), and a `Disarm` called when the host is already stopped
still reports the refusals honestly rather than inventing a fallback leave path.

**Code review extended the convergence claim (F2).** `Converged` originally looked only at the
bots the disarm asked to leave, so a bot already left un-safely-deactivated
(`DeactivationFailed` is terminal and nothing retries it) or already `Deactivating` was skipped
while the report said `failed=0`. `Disarm` now skips only `Deactivated`/`Registered`, counts any
other non-Active state as a failure, and rescans after the stop for bots that landed in
`Deactivating`/`DeactivationFailed` while the host stopped (a leave in flight when the stop
cancels the scan registration fails its retirement acknowledgement). Pinned by
`PlayerBotSchedulerDisarmTests.Disarm_CountsABotThatWasAlreadyFailed_AndDoesNotClaimConvergence`
(red-capable: reverting both halves fails `report.Converged`). The mirror case is
reconciled as well: a bot counted as failed because it was already leaving but which
finishes its leave while the host stops is promoted to `deactivated`, so the report cannot
claim a failure for a bot that provably left (that branch has no unit test — the rig cannot
stage a leave that completes inside `StopAsync`).

### L1.2 Perception: one 25 m census, and a dead richer stack

Production perception is a single census — `GameplayActor.Observe()` →
`ActorObservation` → `BotObservedContext` → decision scenarios:

- Candidate lists carry **object ids only** (no level, hp, hostility per candidate):
  `GameplayActor.cs:331-338`, `WorldManager.GetAround<T>(Character, 25f)`.
  `NearbyLivingNpcObjIds` uses a 100 m radius and is populated **only when
  `ActiveQuests.Count > 0`** (`:333-337`, `QuestObjectiveTargetSelector.LivingPreyScanRadiusM`).
- Scalars: position, current target, hp/mp, active quest ids, money/bank/labor, bag and
  warehouse template counts, carried backpack, party (incl. `TeamManager.GetActiveInvitation`),
  mounted (`:326-330, 339-353`).
- **Costs, cooldowns, GCD and skill availability are absent from the seam** —
  `ActorObservation.cs` (97 lines) has no such field.
- `Observe()` does not apply `CanSeeTarget` (bulk `GetAround`); each caller must.

Every richer perception artifact is **dead** (zero non-test consumers; `BotRadarProjection.cs:144-150`
states it outright): `BotPerceptionSnapshot.cs` (436), `BotRadarProjection.cs` (341),
`PerceptionDelta.cs` (282), `Belief/BeliefInterpreter.cs` (514), `Belief/AttentionScorer.cs` (233),
`Belief/BotBeliefState.cs` (257), `Belief/CurrentPlace.cs` (33). `BotSurveySenses.cs` (325) is
rig-only (`SurveyLegRunner.cs:75`).

**A2 resolution (2026-10-16) — declaration branch taken.** No per-candidate state and no
cost/cooldown fact was added to the seam (Stage A is "no new capability"). Instead the absence is
now declared where a policy author looks: the class docs of `ActorObservation`
(`AAEmu.Game/Core/Managers/Bots/ActorObservation.cs:22-32`), `BotObservedContext`
(`AAEmu.Game/Core/Managers/Bots/BotDecisionProposal.cs:16-22`) and `IGameplayActor.Observe`
(`AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs:97-100`) name exactly what is not carried
(object ids only for candidates; no skill cost/cooldown/GCD/availability; no visibility verdict)
and state the rule: read the authoritative server service at action time or treat the fact as
unknown. The sanctioned pattern already exists in the tree — `BotRoamStepExecutor.cs:4557-4558`
reads `character.Cooldowns` and `character.IsGlobalCooldownDone` directly instead of expecting
them from the snapshot. Limit: the declaration is documentation, not machine-enforced; adding a
fact later is an additive change that must update these three docs and this row.

### L1.3 Persistence: two tables, no intent

Continuity is `playerbot_metadata` + `playerbot_audit` only. **No intent, request or leg is
persisted**, so restart cannot and does not resurrect in-flight work — it re-materialises the
character plus home/schedule. `BotProgressionCursor` is the designed durable planner cursor
(serialize `:73-93`, target `PlayerBotMetadataStore.RecordPlannerState`) and has **zero
callers**. `PlayerTraceService.cs` (1036 lines) is an implemented but write-only consequence
tracer. `BotBrainTuningStore`/`BotBrainTuning` hold process-global tuning in memory only.

### L1.4 Game APIs: strong, with two dishonest completions

`IGameplayActor.cs` (1150), `GameplayActor.cs` (5260), `ActorRequest.cs` (430),
`ActorAuditRecord.cs`, `ActorIdempotency.cs`. Per-verb postconditions are computed from real
engine artifacts (Sell refund → item move → money change `:3086-3113`; Craft only after real
material consumption + product delta `:4101-4145`; Harvest only when `phaseChanged && yieldDelta > 0`
`:3883-3888`; Loot grants from container delta `:1860-1910`).

| # | Gap | Evidence | Severity |
|---|---|---|---|
| L1-i | **`AdvanceQuest` reports a declined step as `Completed` with `Result=true`**, and the distinguishing token has no consumer. **Resolved 2026-10-16 (A3):** a declined step now completes with `Result=false` and its payload carries the applied verdict (`accepted not-applied`), and the consumer is real — `BotBehaviorRuntime.Tick` (`BotBehaviorRuntime.cs:243-244`) reads `appliedNothing` so a declined advance is no longer counted as a landed quest leg. | `GameplayActor.cs:1090,1094`; `QuestAdvanceOutcome.cs:32-48` | Medium |
| L1-j | **The roam route cursor treats a non-arrival leg as an arrival.** The turnover guard fires on any terminal Move leg (`Interrupted`/`TimedOut` included) and calls `Path.Move(position, flatArrival: true)`, so arrival is inferred from standing inside the 0.5 m radius rather than from the leg's own terminal state — the cursor steps past a waypoint whose leg never reported arrival. Practical effect is small (`BotPath.Move` only advances inside the radius; the next step re-walks the same target), but the route's progress no longer follows the leg's outcome. Only `QUEST_TRAVEL` had a foreign-interruption guard. Corrected 2026-10-16 from an earlier "silently skipping a waypoint" reading: the skip needs the bot inside the arrival radius at turnover. **Resolved 2026-10-16 (A3):** the turnover guard now advances only when the leg's own terminal state is an arrival (`IsArrivedMoveLeg`), pinned by a red-capable test that stands the bot ON the waypoint, preempts the leg with a foreign owner and proves the cursor does not move. **Extended 2026-10-16 (code-review F1):** that guard alone could PIN the cursor on a route leg that exhausted its walk budget short of the waypoint — nothing else cleared it and the next wake re-issued the same unreachable target (a livelock, the opposite failure of the skip). The escape is now explicit: the cursor moves only for the route's OWN leg for the CURRENT waypoint (`IsRouteLegForCurrentWaypoint` — party follow, hunt chase, butcher approach and survival flee all stage legs on the same field) and a terminal `TimedOut` one advances through `BotPath.SkipCurrentWaypoint`, which is deliberately not gated on the bot's position. Second red-capable test: `Step_TimedOutRouteLeg_AdvancesPastTheUnreachableWaypoint`. | `BotRoamStepExecutor.cs:2263-2270`, `:3115-3121`; `IsRouteLegForCurrentWaypoint`/`IsUnreachableRouteLeg` (`BotRoamStepExecutor.cs:3129-3148`); `AAEmu.Game/Models/Game/Bots/BotPath.cs:83-96` (`Move` advances only inside `ArrivalRadius`), `:139-149` (`SkipCurrentWaypoint`) | Low-Medium |
| L1-k | **`POST /api/commands/{name}` has no token check** and runs any GM verb as any online character with access ≥100, while the WebApi binds `*:1280` by default. | `CommandController.cs`; `AAEmu.Game/Config.json:14-17` | High (security) |

---

## L2 — Execution runtime (the layer to solidify first)

### L2.1 Action state: real machine, with a misleading acceptance

`Requested → Accepted → Running → Completed | Rejected(reason) | Interrupted | TimedOut`,
with an idempotency ledger, a bounded audit trace (512/actor), a queue lifecycle ledger and
two backstops (actor `RecordBackstopTimeout`, queue wall-clock expiry).

**Accepted ≠ started.** `TryBegin` calls `Accept` *before* the idempotency gate and the busy
gate, so a dedupe/busy refusal is a terminal `Rejected` whose audit still shows `Accepted`
(`GameplayActor.cs:5019-5052`; `ActorRequest.cs:237-248`). Instant verbs never call `Start`.
Completion is poll-only (`GET /api/actors/actions/{traceId}`); there is no blocking wait and
no push.

### L2.2 Intent dispatch: verb-level only — there is no goal verb

`IBotGoalArbiter` exposes `Register/Arbitrate/GetActiveActivity/Forget` and counters —
**no setter** (`BotGoalArbiter.cs:76-103`). An operator can fire single actions, relocate a
patrol home, or change five thresholds; "run this objective" exists **only** in the test-only
loopback bridge (`BotDriveBridge.cs:262-298`). This is the blocker for the stated direction.

### L2.3 Cancellation: real cleanup, narrow scope

`GameplayActor.PreemptCurrent(reason)` → `BroadcastStop()` for Move → `InterruptActive(reason)`,
which cancels pending item-use / craft / harvest, finishes the request as `Interrupted`, then
clears movement, craft and drive state (`:658-671, 5054-5090, 4822-4831, 4094-4099, 4871`).
The queue preempts a live **world-internal** leg but **rejects** a command against an
API-owned busy request (`BotActionCommandQueue.cs:442-457`). Absent: pause/suspend autonomy,
"cancel everything I sent", or any way to reach the active activity.

### L2.4 Recovery: bounded and honest

Stuck movement: 2.5 s no-progress window → exactly one 2 m nudge → `Expire(Navigation)` +
stop (`GameplayActor.cs:119, 140, 122, 4699-4740`). Budgets: move 30 s, item-use/harvest/craft
30 s, combat 3 attempts / 150 ms backoff / 2 s wait with only `CooldownTime` retryable.
Quest-travel stuck recovery requires ≥2 distinct stuck terminals for the same quest+waypoint,
a 60 s cooldown, same-zone relocation ≤75 m, re-checks owner/request/quest immediately before
the teleport, and **never credits arrival or an objective**
(`BotRoamStepExecutor.cs:2933-3065`).

### L2.5 Results and the runtime budget

Audit rows carry lifecycle, per-verb postcondition detail, `state_changes`, effect
observation (a pending ranged swing is reported `pending`, never zero damage) and staged
decision vocabulary (`decision_goal`, `decision_policy`, `decision_candidates`,
`decision_rejections`, `wake_sequence`).

| # | Gap | Evidence | Severity |
|---|---|---|---|
| L2-a | **No external-command provenance.** API-injected commands supply no `DecisionCycleId`/`WakeSequence` (null stays null) and rows copy whatever decision vocabulary the actor had staged — an operator-injected row can carry the bot's own last decision next to it. **Resolved 2026-10-16 (A3):** every control-API request is stamped `command_origin="control-api"` (`BotActionCommandQueue.CommandOriginControlApi`, applied through `GameplayActor.SetPendingCommandOrigin`/`NewRequest`) and the staged bot decision vocabulary is cleared for it, so an operator row can no longer inherit the bot's own decision; the queue-expired backstop row carries the same origin. | `BotActionCommandQueue.cs:248` (constant), `:480` (applied), `:496/:540` (cleared), `:913` (backstop row); grep of `BotActionController.cs` for `DecisionCycleId`/`WakeSequence` = 0 hits | High (evidence hygiene) |
| L2-b | **No per-tick step budget.** `DrainTickQueue` dequeues every queued step per game tick, so N bots translate directly into tick spikes; the 4–8 workers only marshal. This is what makes "N bots" fail before any policy does. **Resolved 2026-10-16 (A4):** `PlayerBotSchedulerOptions.MaxStepsPerTick` bounds the steps one drain may run; the remainder stays queued (leases held, nothing reordered or dropped) and is deferred to the next tick, while native retirements always run. Deferred steps are counted (`TotalStepsDeferred`) and logged. | `PlayerBotScheduler.cs:862-880`, `:431-441`, `:117` | High |
| L2-c | **Orphan window.** When the owning leg stops running (activity switch, generation reset) the request keeps executing until its own timeout, because the scheduler keeps waking while any request is live; the new activity's window overlaps physical execution of the old leg. | `BotRoamStepExecutor.cs:2298-2330`, `:1229-1238` | Medium |
| L2-d | **Generation reset discards live work.** `_states` is removed and a fresh `GameplayActor` created while only `NeedsFoodRequest` is preempted; a live Move/quest-Move/pending craft/harvest/item-use is dropped with the actor, so it gets no terminal audit and no cleanup. | `BotRoamStepExecutor.cs:1513-1539` | Medium |
| L2-e | **Dedupe ledger is per-actor and lost on actor recreation** → a replayed explicit idempotency key across an activation boundary is not suppressed. | actor recreated `BotRoamStepExecutor.cs:1538`; `ActorIdempotency.cs:71` | Medium |
| L2-f | **A thrown verb publishes no audit row.** | `BotActionCommandQueue.cs:423-530` catch block | Low |
| L2-g | **No bot-side refund for a craft/harvest cancelled mid-flight**; no refund logic exists in this seam. | `GameplayActor.cs:4147`; grep `refund` → only the Sell formula `:3086-3090` | Low–Medium |
| L2-h | **Interrupt scope is narrow** — cancel one API trace id, or one actor request; no pause/suspend, no "cancel my commands". | `BotActionController.cs:1251-1278, 158` | Medium |

### L2.6 Group execution: leader-follow only, and a write-only formation

A party does move and fight together today, but each member re-derives it independently every
wake from `TeamManager` state: auto-accept the invitation (`BotRoamStepExecutor.cs:1602-1610`),
resolve the active team and require member-not-leader (`:1612-1613`), set `handledByParty` and
assist `leader.CurrentTarget` (`:1618-1622`), follow beyond 3.5 m and hold at ≤3.0 m
(`:1658-1680`), suppressing the other legs (`:1709, 1713, 1739`). There is no shared task
object, no assigned target, no role, no leadership election and no regroup.

`/bot party follow|stay|role` writes `BotFormation` companion state that **nothing reads**
(no consumer of `GetCompanionMode`/`GetCompanionRole`/`CalculateSlotPosition` outside
`BotFormation.cs`, `BotPartySubCommand.cs` and `BotFormationTests.cs`); `RoleCombatDecisionTree`
has no caller. `/bot home run|claim|build` likewise prints success and changes nothing.

### L2.7 Control plane: two front doors, one side door, no goal

53 token-gated bot routes (5 management + 4 inspection/tuning + 44 actor), one **token-less**
generic GM route, 11 GM verbs, two MCP servers (49 tools). Management routes are direct;
action routes are enqueue-only and poll-based. The only decision-side write is
`POST /api/bots/tuning` (five thresholds). Nothing consumes the brain JSON in-repo.

---

## L3 — Gameplay capabilities

42 action routes with real postconditions and nine registered activity modules
(`Program.cs:366-411`). Live-proved: **`presence.roam` only** (E311 — the bot's own 25.1 m of
ordinary movement). Farm/quest runs are seeded, and E157/E163 are recorded FAILED.

| # | Gap | Evidence | Severity |
|---|---|---|---|
| L3-a | **Seven of nine modules are plan-only.** No `fishing` dispatch branch exists and the wildlife gate never reads the elected name, so the arbiter can report `Activated` while the bot just roams/hunts. Dispatch prefixes cover only `needs.`, `quest.`, `homestead.`, `conflict.`. | `FishingContestActivityModule.cs:73-95` vs `BotRoamStepExecutor.cs:1739-1859` | High |
| L3-b | **No completion/failure return path into choice** except quest sinks, so a failed module re-`CanActivate`s identically next wake — no cooldown, no progress delta. | `QuestBootstrapActivityModule.cs:88-104`; `Program.cs:341-344` | Medium |

---

## L4 — Decision brain (present, and reading inputs it does not have)

`BotGoalArbiter.cs` (351): priority-band ordering, within-band commitment with a 25 %
preemption margin, name retention, transition counters. `Goap/` (23 files) is reachable **only**
inside the homestead leg (`BotRoamStepExecutor.cs:3645`), which requires
`HomesteadActivityModule` (band 60, `Enabled` default false) to win; `Goap/GoalArbitrator.cs:47`
is a **second** arbiter with its own ladder and commitment; `GoapBotStepExecutor` has zero
production constructors (dead adapter).

| # | Gap | Evidence | Severity |
|---|---|---|---|
| L4-a | **No `Deactivate` in `IBotActivityModule`.** A new goal can sit behind the previous leg's live actor request; retirement is scattered one-shot checks and the runtime explicitly refuses to touch the actor. | `IBotActivityModule.cs`; `BotGoalArbiter.cs:299-330`; `BotBehaviorRuntime.cs:532-536` | High |
| L4-b | **Retention is an unpersisted name string, never cleaned.** Restart resets it; `Forget` is uncalled; dictionaries grow. | `BotGoalArbiter.cs:111, 344`; `BotProgressionCursor.cs:73-113` | High |
| L4-c | **Cross-family precedence ignores unfinished work.** Continuity is only incumbent weight + 25 % margin inside a band; a lower-band in-progress goal is dropped the moment a higher band allows, and the interrupted plan is deliberately discarded ("never resume a stale route/plan"). | `BotGoalArbiter.cs:252-257, 303-312`; `BotRoamStepExecutor.cs:1125-1127` | Medium |
| L4-d | **Two goal-selection layers with independent commitments**, reachable together only when homestead is enabled. | `Goap/GoalArbitrator.cs:47`; `BotGoalArbiter.cs:226` | Low |

---

## L5 — Advanced intelligence (later, correctly)

Chatter (`BotChatterService.cs`, 709 lines) is a real deterministic proximity engine — per-bot
cooldown ≥90 s, pair cooldown 10 min, per-zone budget 10/min, real local-chat sink — but it
never consults the arbiter, the active activity or the scheduler lease, so a bot harvesting or
travelling still greets; it is off by default. Formation is inert (above). No personality-driven
motivation, no economy input into choice, no invitation response. These map onto the parent
spec's later scenarios (`Docs/wiki/PlayerBot-Individual-Activity-Choice-Spec.md`).

---

## What is authorised, and what is not

- **Orchestration has no authorised scope.** The developed-capability baseline table has no
  fleet/orchestration row; operator-defined population policy is deferred (Wave 5);
  cooperation is Wave 3B; and no document in the repository defines bot-fleet orchestration
  semantics — treat as **absent, not merely undocumented**. Any fleet/operator-control plan
  needs new approval.
- Binding hard rules for any plan: one ordinary Character/scheduler/action path; no parallel
  brain; reuse-first; additive layer (composition/adapters/extension points first); one retained
  goal owner with explicit precedence; evidence honesty (a scenario never becomes the objective;
  bridge/admin/tuning paths are never capability evidence).
- Documented conflicts to resolve before using as plan inputs: the historical freeze text in
  `START-HERE.md:104-107` reads as binding against the approved direction; M8 is double-stated;
  M7 party completeness self-contradicts; `PROJECT-CONTROL.md:1-94` is an uncontrolled raw dump
  above the title.

---

## Ordered work (dependency order)

**Stage A — L1 correctness (no new capability):**
A1. Lifecycle teardown that always converges + one production stop owner (L1-a, L1-b, L1-d).
**Done 2026-10-16 — the live lane found and fixed the ordering defect (see the A1 resolution note under L1.1); the code review then widened `Converged` to bots that never left and added the unreachable-waypoint escape for the roam cursor (F1/F2).**
A2. Perception facts the decisions already assume — per-candidate state and costs/cooldowns —
or an explicit declaration that they are unavailable, so policy cannot silently assume them (L1.2).
**Done 2026-10-16 — declaration branch (see the A2 resolution note under L1.2).**
A3. Fix the two dishonest completions and add external-command provenance to audit rows
(L1-i, L1-j, L2-a).
**Done 2026-10-16 — both rows resolved (L1-i, L1-j) and the provenance added (L2-a).**
A4. Per-tick execution budget before any N>5 claim (L2-b).
**Done 2026-10-16 — `MaxStepsPerTick` bounds one drain; the rest is deferred, not dropped (L2-b).**

**Stage B — L2 solidify (the control baseline):**
B1. Goal-level intent: an external assignment the arbiter honours, with a declared boundary —
requires an arbiter setter plus the `Deactivate`/preemption contract (L2.2, L4-a).
B2. Completion/failure return path from legs to the electing module (L3-b).
B3. Retirement on supersede, one owner for the one-shot retire checks, wire `Forget` (L1-f, L4-a).
B4. Interrupted-goal continuity record + durable persistence of the elected goal (L1.3, L4-b, L4-c).
B5. Operator read-back of decision reason, and separation of commanded from autonomous work (L2.7).
B6. One writer for the roam route between schedule and arbiter (L1-g).

**Stage C — L3 capabilities, one at a time, ordinary services:**
C1. Dispatcher-or-deny contract validated at startup, so arbitration cannot succeed without an
action owner (L3-a).
C2. One capability proved end-to-end beyond roam, with earned acquisition (no seeding).

**Stage D — L4/L5:** untouched until A and B hold; then motivation profiles, economic influence
and invitation responses per the parent specification.

Cooperation (party roles with real effect, shared tasks) follows individual control;
fleet/operator orchestration remains unauthorised until explicitly approved.
