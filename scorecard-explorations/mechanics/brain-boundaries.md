# Brain Boundaries — READ-ONLY audit (develop @ 2026-09-19)

READ-ONLY. No repo file modified, nothing built or run. Proposals only, no implementation.
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
Locked: scheduler NOT replaced, no per-bot threads, GOAP not the brain, no second gameplay implementation.
Context: `capability-surface-matrix.md`, `gap-observe-scale.md` (§2 S1–S12, §4), `gap-nav-persist.md` (§1, §3), `prereq-changes.md` (PreemptCurrent). `UNKNOWN` = not found in code, nothing inferred.

---

## 1. GOAP OWNERSHIP PLAN — `GoapPlanRunner.Tick` audit

Source: `AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanRunner.cs`.

### 1.1 Actual responsibilities (all in `Tick`, `:102-392`)

| # | Responsibility | Code | Verdict |
|---|---|---|---|
| 1 | **Sensing** — projects world into compressed plan state | `_stateProvider.Project(bot, context)` → `observedState` (`:109`); provider reads live `Character` (Hp/Mp/IsInBattle/Stance `:77-90`, bag scans `:94-101`, housing scans `:152-177`, `CurrentTarget` `:201-222`, labor/gold `:228-229` in `BotWorldStateProvider.cs:60-232`) | Sensing **inside** runner (delegated to provider, but invoked per Tick) |
| 2 | **Goal arbitration** — picks/validates goals | `_goalArbitrator.ArbitrateGoal(bot, observedState, context, PrimaryGoal)` twice per Tick: interrupt probe (`:112`) + primary selection (`:313`); `IsGoalAchievable` checks (`:153`, `:216`, `:274`, `:299`) | Arbitration **inside** runner (delegated to `GoalArbitrator`, strict hierarchy `GoalArbitrator.cs:7-16`) |
| 3 | **Interrupts** — survival preemption + primary-intent preservation/resume | `isInterrupt` name-check DefendSelf/Recover (`:114-116`); preserve primary (`:122-128`); clear+resume or abandon (`:144-173`); satisfied-interrupt resume (`:270-285`) | Interrupt policy **inside** runner |
| 4 | **Intent** — which goal is active, across Ticks | `PrimaryGoal` / `InterruptGoal` / `ActiveGoal` fields (`:23-26`); `SetPrimaryGoal` (`:72-86`); all Tick branches mutate them | Intent memory **inside** runner (memory-only; lost on restart per gap-nav-persist §3.2.2) |
| 5 | **Plan generation** — A* over action registry + template cache | `_templateCache.TryGetTemplate/StoreTemplate` (`:325`, `:360`); `_planner.Plan(bot, observedState, ActiveGoal, actions)` (`:332-333`); unplannable → goal abandon (`:335-347`) | Plan gen **inside** runner (delegated to `GoapPlanner`/`GoapActionRegistry`) |
| 6 | **Retries** — bounded per-action retry then replan/abandon | `MaxActionRetries = 2` (`:33`); `ActionRetryCount` (`:32`); retry branch (`:205-212`); exhausted → achievable? replan (`:233-240`) : abandon (`:215-230`) | Retry policy **inside** runner |
| 7 | **Verification** — effect confirmation before advancing | In-flight `EvaluateStatus(bot, observedState, CurrentActorRequest, context)` (`:179`); `Succeeded` clears failures + advances index (`:183-197`); `Failed` records failure (`:203`); `Invalidated` replans (`:244-255`); pre-dispatch `CheckPreconditions(observedState)` (`:368`) | Verification **inside** runner (delegated to action impls) |
| 8 | **Replan** — drop plan, regenerate next Tick | Six replan sites: resume (`:157-162`), retry-exhausted (`:236-240`), invalidated (`:250-254`), interrupt-satisfied (`:278-282`), unachievable (`:299-308`), precondition (`:376-380`) | Replan trigger **inside** runner |
| 9 | **Abandonment** — drop goal, go idle | Post-interrupt unachievable (`:164-170`); retries+unachievable (`:216-230`); satisfied primary (`:286-295`); unachievable (`:301-308`); unplannable (`:340-346`) | Abandonment **inside** runner |
| 10 | **Dispatch bookkeeping** — create + preempt actor requests | `CurrentActorRequest = nextAction.CreateActorRequest(bot, actor)` (`:383`); `DropInFlightRequest(actor, reason)` → `PreemptCurrent` + null reference (`:49-54`, 12 Tick sites per prereq-changes.md Part 4) | Execution glue **inside** runner (correct layer — owns `CurrentActorRequest`) |
| 11 | **Telemetry emission** — per-transition events | `EmitTelemetry(...)` at every branch (`:394-412`; event vocabulary `GoapTelemetry.cs:7-22`) | Telemetry **inside** runner (sink-injected, memory-only `InMemoryGoapTelemetrySink` `:47-72`) |

What the runner does **NOT** do (verified absences): no `Character`/world mutation of its own (only via `IGameplayActor` + provider reads); no goal **policy** scoring beyond the arbitrator's fixed hierarchy (no utility/weights — `GoalArbitrator.cs:47-129` is priority-ordered, not scored); no scheduler interaction (no `Wake`/`WakeAt` calls; cadence is the step executor's job — `GoapBotStepExecutor.cs:58-64`); no per-action wall-clock bound of its own (relies on action `EvaluateStatus`; per-action bound UNKNOWN per gap-observe-scale §2.1).

### 1.2 Move outward vs stays planner-owned

**Moves OUTWARD to brain/behavior (proposal):**

- (a) **Sensing invocation + snapshot stewardship** — the brain calls `Project` once per wake, holds the resulting `BotWorldState` (+ the `ActorObservation`/`BotObservedContext` it was derived alongside), and hands the snapshot *in*. Runner never calls the provider itself. Rationale: one wake = one world-read; runner re-arbitrating mid-Tick on a second projection is today's double-`ArbitrateGoal` (`:112` + `:313`).
- (b) **Goal arbitration (which goal)** — brain-owned. The runner's `GoalArbitrator` hierarchy (Recover > DefendSelf > committed primary > routines > Roam, `GoalArbitrator.cs:7-16`) is a *domain policy*, not planner machinery. Brain selects `GoapGoal?`; runner only validates achievability-or-abandon. (GOAP stays un-wired in production — production arbitration is `BotGoalArbiter`, not this; gap-observe-scale §4.)
- (c) **Interrupt policy (what preempts what, name-checks)** — brain-owned. The `DefendSelf`/`Recover` name-check (`:114-116`) and preserve/resume/abandon matrix (`:118-173`, `:270-285`) encode survival doctrine. Planner keeps only the mechanism: `DropInFlightRequest` + index reset on told-to-switch.
- (d) **Retry budgets + abandonment thresholds** — brain-owned policy (`MaxActionRetries`, backoff choice, give-up conditions). Planner keeps the counter mechanics.
- (e) **Replan triggers from non-plan signals** (death, pressure, schedule phase — `ReplanTaxonomy.cs:42-53` maps triggers to `Wake`/`WakeAfter`) — brain/scheduler-owned; planner replans only when told or when its own verification fails.
- (f) **Telemetry sink + join keys** — brain-owned (§5 schema); planner emits events with caller-supplied ids, owns no buffering policy.

**Stays PLANNER-owned:**

- (g) Plan generation: registry → `Plan()` → ordered action list + cost (pure function of goal + observed state + actions).
- (h) Effect verification protocol: `EvaluateStatus` / `CheckPreconditions` / `IsSatisfied` dispatch order (Succeeded → advance; Failed → count; Invalidated → replan).
- (i) In-flight execution bookkeeping: `ActivePlan` / `CurrentActionIndex` / `CurrentActionStatus` / `CurrentActorRequest` + `DropInFlightRequest` hygiene (`:49-54`).
- (j) Cache mechanics: template-cache lookup/store keyed by (flags, labor, gold, goal) (`:325-330`, `:358-361`) — policy-free memoization.

### 1.3 Target shape (derived from existing types, proposal only)

```csharp
// Inputs are all existing types; the runner creates none of them.
sealed record PlanQuery(
    GoapGoal Goal,                 // brain-selected; cf. GoalArbitrator.GoalRecover etc. (GoalArbitrator.cs:19-45)
    BotWorldState ObservedState,   // brain-projected snapshot (BotWorldState.cs:11-151)
    IReadOnlyList<IGoapAction> Actions);  // registry slice (GoapActionRegistry.cs:39-64)

sealed record PlanResult(
    bool Success,                  // cf. GoapPlanResult.Success (GoapPlanResult.cs:8-13)
    IReadOnlyList<IGoapAction> Actions,
    float TotalCost,
    int NodesExpanded,
    string? FailureReason);

// Runner surface after the move (proposal — no implementation):
//   PlanResult Plan(PlanQuery query);                       // pure; replaces §1.1 rows 5 (gen half)
//   ActionDisposition Advance(ActionStatus status);         // Succeeded/Failed/Invalidated/Running → advance|retry|replan|abandon
// Everything in §1.2(a)-(f) is a caller concern; (g)-(j) is the planner.
```

---

## 2. PERCEPTION CONVERGENCE — decision-time world reads

### 2.1 Read map (verified sites)

| Reader | What it reads | Live vs snapshot |
|---|---|---|
| `GameplayActor.Observe()` — `GameplayActor.cs:244-270` | `Transform.World.Position`, `CurrentTarget`, Hp/Mp, 3× 25 m `GetAround` (char/npc/doodad → objId lists `:253-255`), `ActiveQuests.Keys` (`:256`), Money/Money2/LaborPower, bag+warehouse `CountByTemplate` (`:288-299`), Backpack-slot pack, team/leader/leader-target | **Canonical snapshot** (`ActorObservation.cs:22-91`, immutable) |
| `BotObservedContext.Capture/From` — `BotDecisionProposal.cs:67-110` | Copies `ActorObservation` (lists re-arrayed, dicts re-hashed) | **Canonical snapshot** copy — decision code must consume this (`:5-7`) |
| Quest scenario — `QuestDecisionScenario.cs:100-133` | `Capture` (`:100`) + `ActiveQuestIds` hash (`:101`); per-quest `Character.Quests.ActiveQuests[questId].Status` (`:113`); `nearbyNpcs(Character, MaxQuestDiscoverRange)` (`:125`) then `ParentWorld.GetNpcByTemplateId` (`:248`), `GetAllDoodads().FirstOrDefault` (`:256`) | Snapshot for position/quests; **intentional live recheck** for reporter/doodad existence at proposal build |
| Needs/Economy scenarios — `NeedsDecisionScenario.cs:301-312`, options `PolicyVersion` (`:66`), proposal priorities | `actor.Character.ParentWorld` + `PublicFarmManager.InPublicFarm` + `ItemManager.GetDoodadIdFromItem` + `CommonFarmGameData.GetAllowedDoodads` | **Derived projection** (soil legality computed from snapshot position + static data) |
| GOAP provider — `BotWorldStateProvider.cs:60-232` | Live `Character` vitals/stance/battle (`:77-90`), bag `Items.Any` scans (`:94-101`, `:141-167`), `HousingManager.PeekInstance.GetAllHouses()` ownership scans (`:152-177`), `CurrentTarget` + hostility + distance + death (`:201-222`), labor/gold clamps (`:228-229`); POI distances from `BotMemory` (`:120-198`) | **Undesired live reads** — bypasses `ActorObservation`; bag/housing re-scanned per Tick outside the canonical snapshot |
| `BotMemory` — `BotMemory.cs:26-179` | Groves, POI positions, merchant ids, loot flags, failure counts; fixture-override gate (`:63-81`, production never writes) | **Cached memory** (cross-wake; the only durable-ish intent besides metadata) |
| Arbiter modules — `BotGoalArbiter.Arbitrate` (`BotGoalArbiter.cs:128-231`) + `IBotActivityModule.CanActivate` (`IBotActivityModule.cs:36`) over `BotActivityContext{Bot, GameHour, ActiveActivity}` | Module-local probes (e.g. `QuestBootstrapActivityModule.cs:71-87`: alive, battle, pressure, quests/money; `NeedsFarmModuleOptions`, schedule/chatter options) — full module inventory UNKNOWN (not enumerated this pass) | **Mixed**: eligibility should consume the canonical snapshot; several modules reach into live `Character`/managers instead — per-module classification UNKNOWN |
| Roam executor — `BotRoamStepExecutor.cs` | `ParentWorld ==` world check (`:541`), `Transform.World.Position` distances (`:544-554`), `ParentWorld.GetUnit` resolver (`:689-690`), `CurrentTarget` get/clear/set + `SCTargetChangedPacket` broadcast (`:724-746`), `MoveToUnit`/`MoveTo` re-issue on drift, Hunt/Butcher scans via `NearbyNpcProvider`/`NearbyDoodadProvider` (`:124-169`) → `WorldManager.GetAround` + `GetNpc/GetDoodad` per id (`BotSurveySenses.cs:244-264`) | Snapshot for cadence/position; **intentional live rechecks** for target validity + **undesired live reads** for per-id resolution loops |
| Movement — `MoveToUnit`/`NavigateToUnit` resolve-once (`GameplayActor.cs:471-474`, `:331-335` per gap-nav-persist §1.1); `Tick` never re-resolves (gap-nav-persist §1.5.1) | Target position snapshotted at dispatch | **Cached memory** (stale by design — BLOCKER for follow/escort per gap-nav-persist §1.5.1) |
| Survey senses — `BotSurveySenses.cs:161-300` | `Transform.ZoneId` → `ZoneManager` zone/group names (`:167-169`); stealth-filtered occupant resolution via `CanSeeTarget` (`:250-268` — notes plain `Observe` does NOT apply it, `:157-159`); heightmap lidar (`:286-300`) | **Derived projection** (zone identity + visibility live here, not in the snapshot) |

### 2.2 Classification

- **Canonical snapshot**: `ActorObservation` + `BotObservedContext` (position, target id, vitals, nearby id lists, quests, money/bank/labor, bags, pack, party). Single per-wake capture; all scoring/eligibility consumes copies.
- **Derived projection**: soil legality, zone names, stealth-filtered occupants, distances/bands, quest bands — computed from the snapshot + static data, never a second world query.
- **Intentional live recheck**: reporter/doodad existence at dispatch, target alive/visible before cast/engage, arrival predicates — narrow, named, at the dispatch site.
- **Undesired live read**: GOAP provider bag/housing/CurrentTarget scans; roam per-id `GetNpc/GetDoodad` loops; arbiter modules reaching past `BotActivityContext` into managers. Converge these onto the snapshot.
- **Cached memory**: `BotMemory` POIs/failures, `MoveToUnit` dispatch snapshot, scheduler `PendingLeg.Destination` drift thresholds — explicit staleness with owner + TTL.

### 2.3 Prioritized additions to the canonical observation (proposals — NO additions now)

1. **Zone/world identity** — `ZoneId` (+ zone/group names), `ParentWorld` template/instance key. Rationale: interzone destinations are bare `Vector3` with no world id (gap-nav-persist §1.5.4 BLOCKER); survey layer already resolves it (`BotSurveySenses.cs:167-169`) but the decision snapshot lacks it, so no eligibility rule can branch on "wrong world" before dispatching a 30 s doomed leg.
2. **Entity generation / resolver outcome** — per-id `(objId, templateId, alive, visible-via-CanSeeTarget, position)` for the nearby lists, or at minimum a snapshot-sequence number so rechecks can detect "world moved under me". Rationale: `Observe` returns ids only (`ActorObservation.cs:40-46`); every consumer re-resolves live (`BotSurveySenses.cs:249-268`, roam `:689-690`), causing N+1 queries (S2/S4) and the stealth divergence noted at `BotSurveySenses.cs:157-159`.
3. **Threat context** — attacker count/ids, `IsInBattle`, current-target hostility/range/dead (today only in the GOAP 64-bit flags, `BotWorldStateProvider.cs:200-225`). Rationale: survival interrupts re-derive this live every Tick; the arbiter cannot hysteresis on threat without it (§6).
4. Remainder (ordered): quest opportunities (discover-target NPC ids + offering counts, bounded by `MaxDiscoverTargets`); skill/GCD cooldowns + labor/MP affordability; equipment durability/repair need; nav state (mission world key, waypoint hash, `GeoDataMode`, last stuck detail); move-target liveness (for `*ToUnit` re-track decisions); vehicle/mount state (`GetIsMounted` mate vs `BindSlave`/seat bond — UNKNOWN branch per gap-nav-persist §1.5.5).

---

## 3. NAV CONTRACT — MoveTo vs NavigateTo (proposal, no implementation)

Derived from `IGameplayActor.cs:88-107`, `GameplayActor.cs:292-501` + Tick `:4116-4241`/stuck `:4343-4377` (via gap-nav-persist §1), dispatch `BotActionCommandQueue.cs:521-547`.

### 3.1 The two intents

- **`MoveTo` (local)** — "walk this body to a point in the *current* world." Absolute `Vector3`, finite/speed pre-flight, straight-line stepping per Tick through the ordinary Transform, arrival = flat XZ ≤ 0.5 f AND |ΔZ| ≤ 0.5 f (`GameplayActor.cs:75`). No planning, no re-resolution, no world change. Failure is geometric (blocked → stuck detector) or budgetary.
- **`NavigateTo` (travel intent)** — "get me to the objective, handling everything between here and there." Sub-cases the brain must name explicitly: (a) local path — navmesh A* → waypoints → corner blending, with silent-degrade to straight line disclosed; (b) **moving target** — re-resolve + repath on drift (today snapshot-only, gap-nav-persist §1.5.1); (c) **repath** — invalidate on obstacle-appear/teleport (today none, §1.5.3); (d) **portal/transport/zone** — world-key guard + transfer legs (today undefined, §1.5.4 BLOCKER); (e) **reacquisition** — target gone → re-scan → resume or `TargetGone`.

### 3.2 Result vocabulary mapped to current terminals

| Proposed | Current terminal(s) | Produced at |
|---|---|---|
| `Running` | `Accepted`/`Running` (non-terminal; `Elapsed` accrues only in Running, `ActorRequest.cs:180-184`) | dispatch → Tick |
| `Arrived` | `Completed("arrived")` / `Completed("already at destination")` (`GameplayActor.cs:4176-4178`, `:349-350`/`:407-408`) | Tick / no-op path |
| `TargetGone` | NEW split of `Rejected(RejectedAction, "target unit not found")` (`:331-333`, `:471-473`) — reject-at-dispatch vs lost-mid-leg need distinct codes | dispatch vs Tick recheck |
| `Unreachable` | NEW split of `TimedOut(Navigation, "stuck: no progress…")` (`:4372-4376`) — after nudge exhausted; today also covers walked-into-geometry (§1.5.2) | stuck detector |
| `WrongWorld` | NEW — today walks meaninglessly to cross-world coords until 30 s budget (`:4031-4042`); needs world-key guard at dispatch | dispatch pre-flight |
| `Stalled` | `TimedOut(Navigation, "navigation budget exceeded")` (`:4031-4042`) + queue backstop `TimedOut(Starvation\|Navigation, "action budget exceeded")` (`BotActionCommandQueue.cs:399-410`) | budget paths |
| `Cancelled` | `Interrupted("stop requested")` (`:477-497`), `Interrupted("interrupted by controller")` (`:761-767`), queue preempt (`BotActionCommandQueue.cs:448-451`), `PreemptCurrent(reason)` (prereq Part 4) | Stop/Interrupt/preempt |

`Rejected(StateTransition, busy)` (`:4565-4572`, queue `:437-446`) and idempotency-dedupe (`:4583-4592`) are orthogonal — they govern *admission*, not travel outcome, and stay unchanged.

### 3.3 Where it should live (proposal)

- A `NavigationIntent` record (destination + world key + mode local/route/follow + repath policy + arrival radii) beside `ActorRequest` (`ActorRequest.cs:14-206` neighborhood) or in the `Bots/` root next to `BotPath`; validation (finite, world-key match, target resolvability) inside `GameplayActor` pre-flights; travel orchestration (re-resolve, repath, portal legs, reacquisition) in the **brain/behavior layer** (roam executor successor), NOT in `GameplayActor.Tick` — the actor keeps one-leg stepping + stuck detection (the proven loop, gap-nav-persist §2.1). Queue gains no new kinds until a caller needs them (matrix §C precedent: `Navigate` kinds dispatch queue-only, no WebAPI).

---

## 4. SCHEDULER SCALING — can Wake/WakeAt/WakeAfter express the cadences?

Primitives: `Wake` (event enqueue → due now, `PlayerBotScheduler.cs:189-195`), `WakeAt`/`WakeAfter` → `Schedule` with earliest-wins dedup (`:496-517`); leased bots fold into `_pendingWake` earliest-wins (`:503-507`, `:519-525`); completion honors pending always, else step-returned delay clamped `delay > Zero ? delay : ScanInterval` (`:442-451`); `StepTimeout` 30 s (`PlayerBotSchedulerOptions.cs:34`); channel 256 `Wait` mode (`PlayerBotSchedulerOptions.cs:31`, `:119-124`); workers 4–8 (`:103`); scan 100 ms (`:14`); tick drain 10 ms inline (`:20`, `:143-144`).

| Cadence | Expressible today? |
|---|---|
| **Event** (death, quest done, interrupt) | YES — `Wake` now + `ReplanTaxonomy` maps 11 triggers to Wake/WakeAfter with debounce (`ReplanTaxonomy.cs:42-53`); never lost (pending fold, `:267-272`) |
| **Active** (leg live, 15 Hz broadcast / 100 ms step) | YES — step returns `ActiveCadence` (`BotRoamStepExecutor.cs:1115-1118`; GOAP executor `:62-64`); `SetBotCadence` override (`BotRoamStepExecutor.cs:423-462`) |
| **Goal** (re-arbitrate each wake) | YES — arbiter-decorator runs one pass per wake, zero modules = pass-through (`BotGoalArbiterStepExecutor.cs:31-43`); steady state = one `CanActivate` probe per module (`:35-36`) |
| **Idle** (nothing to do) | YES — step returns `null` = dormant (`IBotStepExecutor.cs:27-29`; roam `:1116-1118` returns shared `DormantTask`); re-wake only via explicit `Wake*` |
| **LOD** (proximity/dormancy/stagger) | YES via options, PARTIAL in signal — `WakeAfter` stagger offsets (`PopulationDirector.cs:495-517`), proximity sweeps 2 s (`PopulationDirectorOptions.cs:98`), materialize cap 3/sweep (`:217`); BUT tick/region overload cannot demote wakes — probe returns nulls, "H2 not landed" (`SchedulerPressureProbe.cs:25-38`, S11) |

**Verdict: YES for hundreds of bots on cadence expressiveness** — event/active/goal/idle/LOD are all representable without scheduler changes. Scale risks are throughput/retention, not vocabulary: per-scan `List<(BotId,Due)>` alloc + O(N) lease pass (`:254-282`, S1); channel-256 `Wait` backpressure under aligned wakes (`:284-297`); inline double 10 ms drains on the game-loop thread (S7); audit caps 10 k/1 k/1024/100 (S8); per-wake `Observe` ×3 + quest O(activeQuests) + A* on the boundary thread (S2/S3/S5). Allocations cited only where code-backed: scan batch list (`:255`), arbiter per-wake module snapshot (`BotGoalArbiter.cs:144` — skipped when zero modules, `:135-142`), `Observe` ToLists + 2 dicts (`GameplayActor.cs:253-261`), quest HashSets (`QuestDecisionScenario.cs:101`, `:181-184`). No optimization proposed (out of scope).

---

## 5. TELEMETRY SCHEMA — minimum viable joinable (proposal, reuses existing)

Existing structures reused verbatim: `ActorAuditRecord` (+`DecisionCycleId`, `ActorAuditRecord.cs:46-67`, JSON `:86-111`); `ActorRequest.DecisionGoal/Policy/Candidates/Rejections/Seed/CycleId` (`ActorRequest.cs:88-92` + CycleId per prereq Part 3); `GoapTelemetryEvent{CharacterId, EventType, GoalName, ActionName, Reason, StateFlags, TimestampUtc}` (`GoapTelemetry.cs:31-39`); `BotDecisionProposal{Goal, Action, Rationale, PolicyVersion, Priority, …}` (`BotDecisionProposal.cs:162-196`); `QuestRunResult{SelectedAction, Request, Rejections, Explanation, CompletedQuestIds, TraceRecords}` (`QuestDecisionScenario.cs:68-86`); `PlayerBotSchedulerMetrics` pool-level (`PlayerBotSchedulerMetrics.cs:25-42`); `BotArbitration{Outcome, Activity, Candidates, Rejections}` (`BotGoalArbiter.cs:24-28`).

| Column | Source (exists) | Notes |
|---|---|---|
| `BotId` (character id) | `ActorAuditRecord.ActorId`; `GoapTelemetryEvent.CharacterId` | THE pool-level join today is absent (metrics have no per-bot cut — gap-observe-scale §1.3) |
| `WakeId` / `DecisionCycleId` | `DecisionCycleId` (Part 3: scenario `quest-{id}-{ticks}` + queue `BotActionSpec.DecisionCycleId`) | Wake→terminal join exists; scheduler-owned wake id still UNKNOWN/absent |
| `BehaviorInstanceId` | UNKNOWN — no such id in code | Needed to separate overlapping legs on one bot (roam `PendingLeg` vs scenario step vs GOAP `CurrentActorRequest`); propose minting at leg/plan start |
| `ActorTraceId` | `ActorAuditRecord.TraceId` / `ActorRequest` trace | Per-request join (queue poll + trace APIs) |
| `Timestamp` (requested/started/completed) | Record `:51-53` | `StartedAtUtc` null when rejected pre-start |
| `PolicyVersion` | `DecisionPolicy` (`module:{name}` per arbiter stamp; `quest-v1`/`needs-v1`/`economy-v1` per scenario opts) | Per-module `WhyNot` stays in transition log (no column — prereq Part 2 deferred) |
| Decision fields (§19-style) | goal, candidates, rejections, seed (`:62-66`); selection rationale + priority + rejections (`BotDecisionProposal`, `QuestRunResult`) | Writers exist only at arbiter stamp + quest dispatch (gap-observe-scale §1.1 gap); needs/economy inherit stale stamp (prereq Part 4 notes) |
| Outcome | `Result` + §17 `Failure` + `Detail` + `StateChanges`; GOAP event + reason; `ExpectedPostconditionSatisfied` (`BotDecisionProposal.cs:295-300`) | Replan-trigger attribution NOT recorded (taxonomy names future events only, `ReplanTaxonomy.cs:9-12`) |

**Bounded hot-path notes (code-backed):** audit sink drops oldest past 10 k, flush ≤1 k per SAVE tick (`PlayerBotAuditSink.cs:36-39`, `:71-98`); queue history 1024 terminal-evicted (`BotActionCommandQueue.cs:198-199`); per-bot trace API max 100 (`:238-239`); in-actor ring `MaxTraceRecords` oldest-evicted (`GameplayActor.cs:4655-4656`); GOAP sink is unbounded in-memory (`GoapTelemetry.cs:47-72`) — must gain a cap before production wiring. Rule carried forward: log deltas + on-why (goal switch, rejection, terminal), never full world per tick (gap-observe-scale §1.7).

---

## 6. HYSTERESIS — eligibility + score + commitment (proposal, no implementation)

**Problem:** `BotGoalArbiter.Arbitrate` is first-allowing-wins in priority order (`BotGoalArbiter.cs:157-218`) — no scores, no stickiness. A bot flickering between two allowing modules re-`Activate`s every wake (route re-arm churn, audit noise). Per-bot memory is one string (`_activeActivity`, `:88`).

**Proposed interface (evolves the arbiter, no scheduler change):**

```csharp
// Eligibility stays exactly today's CanActivate (fail-closed deny preserved).
// Score + commitment are ADDITIVE — a module that omits them behaves as today.
sealed record BotGoalBid(
    bool Eligible,               // == CanActivate today
    string ActivityName,         // today's unit of change (BotGoalArbiter.cs:40-42)
    string? WhyNot,              // today's deny reason (BotActivityDecision, IBotActivityModule.cs:63)
    float Score = 0f,            // urgency/utility; higher wins among eligible
    float CurrentGoalBonus = 0f, // stickiness credit added ONLY to the incumbent activity
    TimeSpan? MinimumCommitmentWindow = null); // ignore challengers until incumbent tenure exceeds this

interface IHysteresisPolicy {
    // Returns the winner among eligible bids given incumbent + tenure.
    // Default: highest (Score + (incumbent ? CurrentGoalBonus : 0)) wins;
    // challenger wins only if margin > SwitchThreshold AND tenure > MinimumCommitmentWindow.
    string? SelectWinner(IReadOnlyList<BotGoalBid> eligible, string? incumbent, TimeSpan tenure);
    float SwitchThreshold { get; }  // required margin to dethrone (e.g. 5.0)
}
```

- `CurrentGoalBonus` rewards the status quo; `SwitchThreshold` demands a *decisive* challenger (kills 51/49 flicker); `MinimumCommitmentWindow` guarantees a newly activated activity N wakes/seconds to show progress (pairs with cross-wake deltas, gap-nav-persist §2.2c).
- Scheduler untouched: still consumes `IBotStepExecutor.StepAsync → TimeSpan?`; the decorator still runs one pass per wake (`BotGoalArbiterStepExecutor.cs:39-43`). Pending-wake folding (`PlayerBotScheduler.cs:503-507`) already preserves urgency signals.
- **Migration path (proposal):** (1) add optional `Bid()` default-implemented from `CanActivate` (deny → ineligible/0; allow → eligible/fixed-priority-as-score) — zero behavior change; (2) record incumbent tenure beside `_activeActivity` (one `DateTime` per bot); (3) flip selection to scored-with-hysteresis behind a flag defaulting to today-first-wins; (4) migrate modules one by one to real scores (threat distance, quest value, maturity ETA); (5) persist tenure in `playerbot_metadata.plannerState` only if cross-restart stickiness proves needed (else memory-only like `BotRoamState`, gap-nav-persist §3.1).

---

## 7. ANTI-PATTERN CHECK — §20 list

| Item | Verdict | Evidence |
|---|---|---|
| Per-bot threads | **ALL-CLEAR** | Hard gate `IPlayerBotScheduler.cs:17-20` ("no per-bot TickManager subscriptions and no per-bot threads/tasks"); `IBotStepExecutor.cs:15-18` ("no per-bot threads, no TickManager subscriptions"); scheduler owns exactly one scan loop + fixed pool (`PlayerBotScheduler.cs:127-137`). `Task.Run` hits are bootstrap fire-and-forget (`BotChatterBootstrap.cs:31`, `BotPresenceBootstrap.cs:31`, `BotScheduleBootstrap.cs:30`) and the boundary-scope helper (`ExecutionBoundary.cs:151-152`) — none is a per-bot loop. |
| Universal AI tick (one cadence for all bots) | **ALL-CLEAR** | No global behavior lock (`PlayerBotScheduler.cs:19-22`); per-bot next-wake from step return, `null` = dormant (`IBotStepExecutor.cs:27-29`; roam `BotRoamStepExecutor.cs:1115-1118`; GOAP `GoapBotStepExecutor.cs:62-64`); per-bot overrides (`SetBotCadence`, `:423-462`); stagger/dormancy options (`PopulationDirectorOptions.cs:123-126`). TickManager carries only drains, never behavior. |
| Policy in `GameplayActor` (goal choice/scoring inside the execution layer) | **ALL-CLEAR** | `GameplayActor.cs` contains no `CanActivate`/`Arbitrat`/priority/score selection (grep: no hits). Decision context is staged data only: `SetPendingDecision` copies into the next request (`IGameplayActor.cs:60-63`); `PreemptCurrent` is mechanism with explicit no-policy contract (`:115-129`). All choice lives in arbiter/scenarios/GOAP caller layers. |
| Second actor API (parallel gameplay implementation bypassing the seam) | **ALL-CLEAR with adjacent risk** | No second `IGameplayActor` gameplay path: `PlayerBotControllerAdapter` delegates the same contract (`prereq-changes.md` Part 4); `PlayerBotController` is engine-direct helpers with no lifecycle/audit (matrix §A4 — ENGINE/SCRIPTED, never presented as actor). Bridge `drive`/`farm`/`quest` ops bypass the actor but are disclosed fixture-only, default-disabled (`BotDriveBridge.cs:46-49`; matrix §A3/§B). No new endpoints proposed here. |
| Scheduler replacement / second scheduler | **ALL-CLEAR** | No proposal touches `PlayerBotScheduler`; §4 verdict and §6 migration are caller-side only. `PopulationDirector.Wake` re-arms through the scheduler (`PopulationDirector.cs:319`), it does not duplicate it. |
| GOAP as the brain | **ALL-CLEAR** | GOAP stack wiring in production UNKNOWN (gap-observe-scale §4: production executor is `BotRoamStepExecutor`); §1 moves arbitration/intent/policy OUT of the runner — planner keeps pure plan-gen + verification mechanics. |

---

*End of brain-boundaries audit. All section claims cite file:line above; UNKNOWN marks the rest. Nothing was implemented.*
