# Prereq Changes Log — Parts 1–4 + Part 8 classification (2026-09-18, branch develop)

Scope: ONLY the four minimal items. One fix attempt per problem; no scope expansion.
No boot-behavior change, no new telemetry framework, no distributed tracing, no behavior rewrites.
No E2E test was run (lane owned by another session).

Acceptance: `dotnet build AAEmu.Game/AAEmu.Game.csproj -c Release` → 0 errors;
`dotnet build AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj -c Release` → 0 errors;
`dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Release` → 0 errors (fakes kept compiling).

## Files changed (17)

Game (11):
- `AAEmu.Game/Core/Managers/Bots/ActorRequest.cs` — `DecisionCycleId` + null-tolerant `SetDecisionCycleId` (Part 3).
- `AAEmu.Game/Core/Managers/Bots/ActorAuditRecord.cs` — trailing optional `DecisionCycleId`, serialized as `decision_cycle_id` (Part 3).
- `AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs` — `SetPendingCycleId`, `PreemptCurrent(reason)` contract (Parts 3+4).
- `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs` — `_pendingCycleId` staging consumed in `NewRequest`, `Finish` stamps cycle id, `PreemptCurrent` impl (Parts 3+4).
- `AAEmu.Game/Core/Managers/Bots/PlayerBotControllerAdapter.cs` — delegate both new members.
- `AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs` — `BotArbitration` gains `Candidates`/`Rejections`; `Arbitrate` counts declines/skips/errors before the winner (all probed on NoCandidate) (Part 2).
- `AAEmu.Game/Core/Managers/Bots/BotGoalArbiterStepExecutor.cs` — stages arbitration outcome as pending decision for the inner step's next request (Part 2).
- `AAEmu.Game/Core/Managers/Bots/QuestDecisionScenario.cs` — `SetPendingCycleId(opts.CycleId)` at dispatch (Part 3).
- `AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs` — `BotActionSpec.DecisionCycleId`, stage-before-dispatch + catch-clear, backstop fallback record + snapshot carry it (seeded at Requested) (Part 3).
- `AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanRunner.cs` — `DropInFlightRequest` + 12 Tick-site migrations (Part 4).
- `AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs` — 3 Stop-site migrations (Part 4).

Tests (6):
- `AAEmu.IntegrationTests/E2e/E2eStack.cs` — `LaneCapabilityFlag`, `CapabilityManifest()`, `RequireFlags()` (Part 1).
- 5 `IGameplayActor` fakes gain the 2 stub members (compile-only): `BotDecisionProposalTests`, `DevMapperServiceTests`, `Goap/GoapScenarioIntegrationTests`, `Goap/HomesteadRuntimeTests`, `Goap/HomesteadTenBotScenarioRigTests` (all under `AAEmu.UnitTests`).

## Part 1 — Lane manifest

Shape: `E2eStack.LaneCapabilityFlag(Name, Value, Source)` × 30 flags from gap-nav-persist §4.1
(bot-control ×3, quest-bootstrap, needs-farm, presence ×12 incl. HOME_X/Y/Z + hunt/butcher radii,
schedules ×2, chatter ×3, fishing contest, proximity/dormancy/scheduler ×7).
`CapabilityManifest()` reads the RUNNER env (what the game process inherits at boot);
`Source` = "runner-env (forwarded explicitly…)" for the 4 E2eStack forwards
(`AAEMU_NEEDS_FARM_ENABLED`, `AAEMU_QUEST_BOOTSTRAP_ENABLED`, `AAEMU_PRESENCE_HUNT[_RADIUS]`),
else "runner-env (inherited…)". Token is presence-only (`"(set)"`), never echoed.
`RequireFlags(params)` throws `InvalidOperationException` (configuration error, never a gameplay
timeout) listing absent flags + full manifest. Absence is literal (unset/empty); `"0"` counts as
present-but-off — tests asserting enablement read manifest values directly. No boot change.

## Part 2 — Decision trace (existing fields only)

- `BotGoalArbiterStepExecutor`: stamps selected activity + counts per wake —
  `DecisionGoal` = activity name (null on NoCandidate), `DecisionPolicy` = `module:{ModuleName}`,
  `DecisionCandidates`/`DecisionRejections` from the arbiter, `DecisionSeed` left null (no seed
  exists at this layer — absent, never synthesized). Skipped on `None` (zero modules: pass-through
  stays byte-identical) and when inner is not `BotRoamStepExecutor` (never creates a second actor;
  resolves the live actor via `GetOrCreateActor`). A staged stamp is overwritten every wake, so an
  idle wake never leaks context forward. Per-module `WhyNot` reasons have no record column — they
  stay in the transition log (deferred: would need a record-schema addition, out of scope).
- `QuestDecisionScenario` dispatch: verified ALREADY stamped (`SetPendingDecision`: goal =
  quest.advance/turn-in/accept, policy = PolicyVersion, candidates = rejections+1, seed = CycleId).
  No change needed there for Part 2; ordering is safe (stage → dispatch consumes → terminal
  Capture after). Side effect noted: the arbiter stamp is consumed by the leg's first request,
  which is often the perception `Observe()` — honest ("wake ran under activity X"); the scenario
  re-stages its own context before its dispatch, so the action record keeps scenario-level why.

## Part 3 — Join key

One id, `DecisionCycleId` (string, null = absent): scenario path = per-wake `CycleId`
(`StepQuestLeg` mints `quest-{characterId}-{ticks}`) → `SetPendingCycleId` → `NewRequest` →
`ActorRequest.DecisionCycleId` → `Finish` → `ActorAuditRecord.DecisionCycleId`
(JSON `decision_cycle_id`). Queue path = `BotActionSpec.DecisionCycleId` → staged on the actor
BEFORE `ExecuteKind` (separate channel from decision context — no clobber either way; cleared in
the dispatch-exception catch so it cannot leak onto an unrelated request) → same record path;
backstop fallback record and `BotActionSnapshot.DecisionCycleId` (seeded at Requested, refreshed
at publish) carry it. A test joins wake → terminal row by filtering trace/snapshots on
`decision_cycle_id`. The controller does NOT accept it over HTTP (no new API surface — queue-seam only).

## Part 4 — Preemption helper

`GameplayActor.PreemptCurrent(reason)` (+ `IGameplayActor` contract, adapter delegates):
interrupts the live request under `reason`, emits the Move standstill broadcast like `Stop`,
clears actor-owned movement/craft state via `InterruptActive`, emits NO request of its own
(the interrupted request keeps its own `Interrupted` record); returns bool. Ownership contract
(caller clears `PendingLeg`/`CurrentActorRequest`, treats plan as needing replan — notified by
return value, helper never reaches into behavior state). `Stop()`/`Interrupt(traceId)` remain as mechanism.

Migrated (15 sites):
- GOAP `GoapPlanRunner.Tick` (12): survival-interrupt, primary-resume, succeeded, retry,
  goal-abandoned, replan, invalidated, interrupt-satisfied, goal-satisfied, unachievable,
  plan-generated, precondition-invalidated — all via `DropInFlightRequest(actor, reason)`,
  which preempts then nulls the reference; runner state machine otherwise untouched
  (terminal-path calls are no-ops returning false).
- Roam `BotRoamStepExecutor` (3, proof): party-follow-arrived (~597), hunt-target-down (~735),
  hunt-in-engage-range (~769) — `Stop()` → `PreemptCurrent(reason)` + `PendingLeg = null`
  (same halt, minus the Stop receipt record).

Remaining (deferred, same semantic noted but untouched):
- `GoapPlanRunner.InvalidatePlan` (~97): no actor in scope — caller must preempt.
- Queue drain preempt (`BotActionCommandQueue.cs:452`): established `Interrupted("interrupted by
  controller")` detail + API-preempt audit expectations; left.
- Roam `Stop()` sites: 566 (party assist), 589/761/896/2349 (stop-then-dispatch chase/follow/
  butcher/PvP), 644 (needs-leg pause), 842/879/906/954 (hunt/butcher acquire/engage),
  998 (route arrival), 1456/1487/1545/1578 (needs-farm arrival), 2357/2424 (PvP).
- Needs/Economy scenario dispatches: no `SetPendingDecision` authorship (they inherit the
  arbiter-staged context until first Observe consumes it).

## Part 8 — QuestBootstrapActivityModule classification: TRANSITIONAL

- Who invokes: `BotGoalArbiter.Arbitrate` per scheduler wake via `BotGoalArbiterStepExecutor`,
  the production `IBotStepExecutor` binding (`Program.cs:328-332`); registered as an
  `IBotActivityModule` singleton (`Program.cs:318`), rank 58.
- Conditions: `Enabled` (env `AAEMU_QUEST_BOOTSTRAP_ENABLED=1|true|True`, default OFF —
  `QuestBootstrapActivityModule.cs:23-24,68-69`) + alive + not in battle + pressure below High +
  (active quests OR money below GoldTarget 1000) (`:71-87`).
- Observations: the module NEVER accepts/advances/turns in — arbitration only; behavior runs in
  `StepQuestLeg` (`BotRoamStepExecutor.cs:661-668`) gated on the arbiter-held `quest.*` activity.
  G3 lane evidence: without the flag, `CanActivate` denies at `:68-69`, arbiter never yields
  `quest.progress`, leg never runs (no error — silent idle).
- Decision: TRANSITIONAL (not PRODUCTION_BEHAVIOR: default-off deployment gate, G3 open; not
  TEST_SCAFFOLD/LEGACY: production-wired, pressure-observant, fail-closed deny, no harness
  dependency; not BOOTSTRAP_ONLY: it is the ongoing arbitration owner, not one-shot setup).
- Capabilities: none of its own — no actor verbs, no queue kinds (arbitration eligibility only).
- Seam: `IBotActivityModule.CanActivate`/`Activate` (pure probe + identity handle) — NOT the
  `POST /api/actors/* → queue → actor` seam.
- State: stateless (options + pressure probe); per-bot activity memory lives in the arbiter.
- Retry: N/A — eligibility re-probed each wake; no counters, no backoff.

## Deferred / not done

- No per-module rejection-reason column (record schema unchanged for Part 2 by instruction).
- No HTTP intake for `DecisionCycleId`; no scheduler-owned wake id (scenario CycleId + queue
  spec cover both dispatch paths).
- No new tests: behavior change is bounded to previously-unsafe paths (orphan legs now stop
  instead of running on); builds are the proof per task acceptance. No E2E run (lane owned
  elsewhere). Scoped self-check only: the three `dotnet build -c Release` invocations above.
