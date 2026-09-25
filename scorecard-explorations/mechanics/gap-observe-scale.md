# Observe & Scale Gap Audit — PlayerBot autonomy (develop @ 2026-09-18)

READ-ONLY audit. No repo file was modified; no server, test, or build was run.
Canonical seam (unchanged): `POST /api/actors/* → BotActionCommandQueue → GameplayActor → engine`
(see `scorecard-explorations/mechanics/capability-surface-matrix.md`).
This note audits gaps/risks only — no redesign. Every claim carries file:line
evidence; anything not found in code is marked UNKNOWN, nothing is inferred.

Established facts reused (not re-verified here): Q0/G1/G2 green; G3 NOT IMPLEMENTED;
`QuestBootstrapActivityModule` default-OFF.

---

## 1. Observability — what a decision trace contains today

### 1.1 `ActorAuditRecord` (per-action terminal record)

Source: `AAEmu.Game/Core/Managers/Bots/ActorAuditRecord.cs:46-66` (fields), `:69-72` (`ToString`), `:86-111` (`ToJson`).

| Field | Notes |
|---|---|
| `TraceId`, `ActorId`, `Action`, `TargetId` | identity (`:47-50`) |
| `RequestedAtUtc`, `StartedAtUtc` (null when rejected pre-start), `CompletedAtUtc` | timing (`:51-53`); doc `:20-22` |
| `Result` (Completed/Rejected/Interrupted/TimedOut), `Failure` (§17 taxonomy, null for Completed/Interrupted), `Detail`, `StateChanges` (full transition log, oldest first) | outcome (`:54-57`); doc `:8-9` "exactly one record on terminal transition; immutable" |
| `TargetHpBefore/After`, `EffectObserved`, `EffectWait` | v2 additive cast-effect window; "observation outcome NEVER changes Result" (`:35-40`) |
| `DecisionGoal`, `DecisionPolicy`, `DecisionCandidates`, `DecisionRejections`, `DecisionSeed` | v3 additive decision context (`:62-66`, serialized `:105-110`) |

Gap: v3 decision fields exist on the record but are populated only where callers set
`request.DecisionGoal/...` — the only writer found is the generic capture
`GameplayActor.cs:4650-4654` (`DecisionGoal: request.DecisionGoal, ...` passthrough).
Whether any scheduler/GOAP path actually stamps goal/candidates/rejections per action is
UNKNOWN (no writer found in `BotRoamStepExecutor.cs`, `GoapPlanRunner.cs`, or
`QuestDecisionScenario.cs` by search). So "why did it choose X" is NOT routinely answered
by the audit trail today even though the columns exist.

### 1.2 Queue snapshots (control-plane trace surface)

Source: `AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs`.

- `BotActionSnapshot` = TraceId, ActorId, BotName, Action, State, Failure, Detail,
  Requested/Started/CompletedAtUtc, StateChanges, AuditJson, Result (`:168-181`).
- Poll: `TryGetSnapshot` (`:312-323`); per-bot trail `TraceFor` newest-first, default limit 20,
  max 100 (`:238-239`, `:330-338`); counts `GetStats` queued/in-flight/history (`:340-342`).
- Retention: `HistoryCap = 1024`, eviction drops oldest TERMINAL first (`:198-199`, `:939-943`);
  audit JSON flushed to `PlayerBotAuditSink` once per terminal snapshot (`:848-852`).
- Lifecycle: Requested→Accepted→Running→Completed|Rejected|Interrupted|TimedOut (`:216-219`);
  no-wedge deadline backstop expires stuck-Running entries (`:230-232`, `:405-409`).

### 1.3 Scheduler metrics

Source: `AAEmu.Game/Core/Managers/Bots/PlayerBotSchedulerMetrics.cs:25-42`.
WorkerCount (clamped 4–8), ActiveWorkers, DueQueueDepth, EventQueueDepth, InFlight,
TotalStepsRun/Skipped/Failed/TimedOut, TotalDuePopped, LastCycleDue, MaxCycleDue,
Total/MaxWakeLatencyMs (+ derived `AverageWakeLatencyMs`, `:45`), WorkerUtilization,
ElapsedMs, TotalResurrections. "Why stalled" inputs present at pool level
(queue depths, wake latency, timeouts, utilization) but NOT per-bot: which bot is stuck,
on what goal/leg, since when, is UNKNOWN in this surface.

### 1.4 `BotQuestLoopObservation` (quest-leg projection)

Source: `AAEmu.Game/Core/Managers/Bots/BotQuestLoopObservation.cs:26-57` (rows/snapshot),
`:64-107` (`Capture`). Per wake: CharacterId/Name, Level, Money, XYZ, ZoneId,
`QuestLegActive` flag (`:47`), `ActiveQuests` ordered by id with Status/Step/Objectives
(`:49`, `:72-84`), CompletedCount, positional ArcCompleted (`:86-89`).
Observation ONLY — "never accepts, advances, or turns in" (`:14-15`); pure static
projection, no world lookups (`:20-21`). Answers "what quest state resulted", not
"why this quest was chosen".

### 1.5 `GateSoakRunner` evidence (soak/gate harness)

Source: `AAEmu.IntegrationTests/E2e/Gate/GateSoakRunner.cs:8-28` (doc), `:30-46` (paths).
Per stage: one markdown evidence file; TickManager invoke p95/max + ActiveRegionTick worst
pass; scheduler wake latency (n/a when citizen path unwired); DB `SHOW GLOBAL STATUS Com_*`
deltas — explicitly "server-global scope, INVALID for verdicts … recorded as evidence only"
(`:21-23`); physics warning + tick-overrun rates from log scan (`:24-26`). Golden route
16 quests (`:33-34`); dual-log window (`GameLogPath`, `GameRestartLogPath`, `:38-46`).
Soak evidence answers capacity/regression questions, NOT per-decision "why".

### 1.6 MINIMAL trace contract (to answer "why did it do that / why stalled")

Required per decision point (wake/leg/action), in order:

1. `tick` — wake id + timestamp (which scheduler step; UNKNOWN today — no wake/step id
   joins scheduler metrics to audit records).
2. `observed state` — the snapshot the decision consumed (`BotObservedContext` /
   `ActorObservation`; exists but is NOT persisted beside the decision — held in memory
   only, `BotDecisionProposal.cs:99-102`).
3. `goal candidates` — ranked list with scores (exists transiently in GOAP arbitrator /
   `BotDecisionSelector.Select`; `BotDecisionProposal.cs:231-292`; persisted nowhere
   except v3 `DecisionCandidates/Rejections` ints — counts without names).
4. `selected goal/behavior` — v3 `DecisionGoal/DecisionPolicy` columns exist
   (`ActorAuditRecord.cs:62-63`) but writers are UNKNOWN (see §1.1 gap).
5. `capability` — Action + TargetId + idempotency key (PASS — always recorded).
6. `result` — terminal state + failure taxonomy + post-condition check
   (`BotDecisionExecution.ExpectedPostconditionSatisfied`, `BotDecisionProposal.cs:295-300`; PASS at dispatch sites that use `BotDecisionCycle`, `:307-334`).
7. `replan reason` — `ReplanTaxonomy.TriggerKind` names the vocabulary
   (`ReplanTaxonomy.cs:17-30`) with wake mapping + debounce (`:40-53`), but the doc
   states "replanning is wake-driven only … this taxonomy names the events a future
   planner subscribes to" (`:9-12`): trigger attribution per replan is NOT recorded today.

Net: the vocabulary for 2/3/6/7 exists in code; durable per-decision linkage for
1/3/4/7 does not. The cheapest close is stamping the v3 decision fields at every
dispatch site + a wake/step id join key — stated as contract gap, not designed here.

### 1.7 Full-world-per-tick logging risks

- `GameplayActor.Observe()` performs 3× `WorldManager.GetAround<T>(Character, 25f)`
  + `.Select(...).ToList()` plus `ActiveQuestIds … .ToList()` plus TWO `CountByTemplate`
  dictionary builds over full bag/warehouse snapshots (`GameplayActor.cs:242-250`,
  `:277-285`). Every `Observe()` is O(nearby + bag + warehouse) allocations.
- `QuestDecisionScenario.Run` calls `BotObservedContext.Capture(actor)` (→ `Observe()`)
  THREE times per wake (`QuestDecisionScenario.cs:100,159,181`) plus `ToHashSet()` before/after
  (`:101`, `:181-182`); `Talk`/`InteractNpc` snapshot full quest dict + clone objective arrays
  per call (`GameplayActor.cs:935-937`).
- `ActorObservation` carries unbounded lists + full bag/bank count dicts
  (`ActorObservation.cs:39-64`); persisting it per tick per bot is O(bots × (nearby + inventory)).
- Backstops exist but are lossy: audit sink `MaxBufferedRecords = 10_000` drop-oldest,
  `MaxFlushBatch = 1_000` per save tick (`PlayerBotAuditSink.cs:36-39`, `:71-98`);
  queue history 1024 terminal-evicted (`BotActionCommandQueue.cs:198-199`).
  Risk: per-tick full-observation logging would saturate these caps and evict the very
  terminal records needed for forensics, while adding per-wake allocation churn on the
  game-loop thread (drains run inline, `:957-958`). Log deltas + on-why (goal switch,
  rejection, terminal), never full world per tick.

---

## 2. Scale risks (code-backed only; no proposals beyond naming risk + location)

Assumes default cadences: scheduler scan 100 ms (`PlayerBotSchedulerOptions.cs:14`),
tick drain 10 ms (`:20`), queue drain 10 ms (`BotActionCommandQueue.cs:196`),
workers 4–8 (`PlayerBotSchedulerOptions.cs:11`), work channel 256 (`:31`),
step timeout 30 s (`:34`).

| # | Risk | Location | Scaling shape |
|---|---|---|---|
| S1 | Wake-scan pops EVERYTHING due per cycle into a fresh `List<(BotId, Due)>` (`PlayerBotScheduler.cs:254-258`); all N due bots lease + enqueue each 100 ms scan. At 100s of bots with aligned wakes, bursty channel fill vs capacity 256 (Wait mode, `:121`) + per-cycle list alloc. | `PlayerBotScheduler.cs:246-258`, `:121` | O(N) work + alloc per scan cycle; contention on the single queue lock (`:16-17`) |
| S2 | `Observe()` fan-out: 3× 25 m `GetAround` region queries per call, 3 calls per quest-leg wake (§1.7). `GetAround(obj, radius, result)` fans out to ALL neighbor regions when the radius crosses a region boundary (`WorldManager.cs:1248-1256`); the allocating overloads (`:1196-1201`, `:1227-1232`) add a `List<T>` per call. 10/100s of bots × wakes × 3 calls × 3 queries. | `GameplayActor.cs:242-244`, `WorldManager.cs:1237-1256` | O(bots × wakes × nearby) queries; neighbor-region fan-out per query |
| S3 | Quest leg iterates ALL active quests every wake (advance proposal each) + discovery sweep over up to `MaxDiscoverTargets = 3` NPCs (`QuestDecisionScenario.cs:59`, `:104-123`); each `AdvanceProposal`/`TurnInProposals` + `BotDecisionSelector.Select` is per-quest work, and `Capture` ×3 per run. Cost grows with quests-per-bot × bots. | `QuestDecisionScenario.cs:27-33`, `:100-182` | O(bots × activeQuests) per wake + 3 observations |
| S4 | Roam/hunt/butcher legs scan on fixed intervals (`HuntScanInterval`/`ButcherScanInterval` 1.2 s, `BotRoamStepExecutor.cs:112-113`, `:162-163`) via `NearbyNpcProvider` default `WorldManager.GetAround<Npc>` (`:124-125`) and `NearbyDoodadProvider` (`:165-166`); per-scan `GetAround` + resolve (`world?.GetNpc/GetDoodad` per id, `BotSurveySenses.cs:244-264`). Linear per bot per scan; no cross-bot sharing found. | `BotRoamStepExecutor.cs:807-812` + providers, `BotSurveySenses.cs:244-264` | O(bots × scans × nearby) |
| S5 | Pathfinding per routed leg: `PathNode.FindPath` (navmesh A*) at dispatch when `GeoDataMode` (`GameplayActor.cs:353-369`), else straight-line + obstacle-detour check (`:376-386`). A* cost is per-request on the boundary thread; 100s of concurrent `Navigate` legs contend the inline drain. No path cache found (UNKNOWN — searched, not verified absent). | `GameplayActor.cs:316-386`; contract `IGameplayActor.cs:84-97` | Per-leg A* on game-loop thread; unbounded concurrent legs |
| S6 | Movement legs hold `ActorRequest` + waypoints queue until arrival/timeout (30 s default, `GameplayActor.cs:78`); stuck detection window 2.5 s (`:116`); single-writer means a stuck leg blocks ALL control of that bot until timeout/preempt (`BotActionCommandQueue.cs:437-452`). At scale, wedged legs × 30 s budgets pin actors + audit history. | `GameplayActor.cs:78-130`, `:486-499`; queue `:437-452` | O(stuckBots × 30 s) actor occupancy |
| S7 | Two inline (`useAsync:false`) TickManager subscribers on the game-loop thread: queue drain every 10 ms (`BotActionCommandQueue.cs:957-958`) + scheduler marshal drain every 10 ms (`PlayerBotScheduler.cs:143-144`); tick loop sleeps 20 ms and warns >100 ms (`TickManager.cs:25-47`), metrics p50/p95/max + subscriber count (`:42-44`). Every added per-tick bot work item directly extends the tick. | `TickManager.cs:25-47`; subscribers as cited | All inline bot work is tick-latency; 10 ms × 2 drains baseline |
| S8 | Audit volume: 1 record + JSON per terminal action; sink drops oldest past 10 k, flushes ≤1 k per SAVE tick (`PlayerBotAuditSink.cs:36-39`, `:71-98`); queue history 1024 (`BotActionCommandQueue.cs:198-199`); trace API caps 100 (`:238-239`). At 100s of acting bots, forensic retention per bot → ~10 records; sustained bursts silently drop oldest. | `PlayerBotAuditSink.cs:31-98` | O(actions) records vs fixed caps; lossy under load |
| S9 | Per-bot per-wake allocations on hot path: `Observe` ToLists + 2 dicts (§1.7); quest-leg HashSets + proposal lists (`QuestDecisionScenario.cs:101-104`, `:181-184`); roam `RunScanCycle` batch list (`PlayerBotScheduler.cs:255`); Talk snapshot dict + `(int[])Objectives.Clone()` per quest (`GameplayActor.cs:935-937`); `CountByTemplate` dict per Observe (`:281-285`). GC pressure scales with bots × wakes; no pooling found. | As cited | O(bots × wakes) short-lived heap |
| S10 | GOAP planner: `DefaultMaxExpansions = 500` per plan (`GoapPlanner.cs:29-35`), budget-exceeded failure (`:90-95`); dominance-pruned frontier (`:115-118`); dynamic per-step `CalculateCost` (`GoapActionBase.cs:59`); registry ~20 actions across 4+ domains (`GoapActionRegistry.cs:39-64`). Per-wake replans × bots × 500 expansions worst case. Whether GOAP replans per wake in production is UNKNOWN (production executor is `BotRoamStepExecutor`, not `GoapBotStepExecutor` — wiring UNKNOWN). | `GoapPlanner.cs:27-118`, `GoapPlanRunner.cs:86-119` | O(bots × replans × 500) worst-case expansions |
| S11 | Pressure backstop is partial: `ServerPressure` bands Healthy→Critical (`ServerPressure.cs:9-22`); `SchedulerPressureProbe` returns `TickDurationP95Ms: null, RegionTickDurationMs: null` — "H2 probe not landed yet" (`SchedulerPressureProbe.cs:25-38`, doc `:8-9`). Tick/region overload therefore cannot demote wakes today; scale protection observes scheduler-only signals. | `SchedulerPressureProbe.cs:5-45`, `ServerPressure.cs:3-22` | Overload signal missing at the layer that matters |
| S12 | DB writes are save-tick batched, NOT per-tick per-bot (audit sink flush, `PlayerBotAuditSink.cs:71-98`; metadata `SaveDirty` REPLACEs dirty rows, `PlayerBotMetadataStore.cs:176-201`; autosave interval from config, `SaveManager.cs:68`; skip-guard when a pass overruns, `:32-37`, `:97-98`). Risk is batch size under load (≤1 k audit rows + all dirty metadata rows per pass), not per-tick queries. Any other per-tick DB access: UNKNOWN (none found). | As cited | O(dirtyRows + bufferedAudit) per save pass |

---

## 3. Control modes — coexistence today + minimal ownership proposal

### 3.1 Today (all verified)

- External control is enqueue-only: API threads never touch Character/world
  (`BotActionCommandQueue.cs:210-215`); auth gate `AAEMU_BOT_CTRL=1/true` or
  `Bots.EnableBotControl` + `X-Auth-Token` (`BotActionController.cs:36-38`).
  MCP sidecar is an HTTP client with no engine reference (matrix §C verified;
  tools POST, e.g. `ActionMcpServer.cs:149-153`).
- Single-writer, two cases (`BotActionCommandQueue.cs:220-226`, impl `:433-452`):
  busy with a still-running **API command** → `Rejected(StateTransition, busy)` —
  caller must poll to terminal first (`:439-446`, "anti-race rule for concurrent callers");
  busy with a **world-internal request** (roam leg, scenario step) → PREEMPT via
  `actor.Interrupt`, audited as Interrupted (`:448-451`).
- Actor-level: any new request while Running is `Rejected(busy)`; `Stop()` is the only
  request allowed while busy (`GameplayActor.cs:47-48`, impl `:486-499`).
  `Interrupt(traceId)` → bool, idempotent no-op when no such active request
  (`:761-767`); queue `Interrupt` is a control op with no actor request of its own
  (`BotActionCommandQueue.cs:457-460`, `:618-632`), routed at `POST /api/actors/interrupt`
  (`BotActionController.cs:1251-1278`).
- Taxonomy wart: `BotActionKind.Interrupt → ActorActionType.Stop` in `ToActionType`
  (`BotActionCommandQueue.cs:915`) — control ops appear under a gameplay label in mapped views.
- Manual/test driving coexists WITHOUT ownership: bridge `drive`/`farm`/`quest` ops bypass
  the actor (matrix §A3; bridge disabled by default, `BotDriveBridge.cs:46-49`), `DevMapperService`
  records manual-walk routes (`DevMapperService.cs:113-116`, `:159-160`), and scenario rigs
  tick live actors inline. Nothing distinguishes "autonomy is driving" from "a human/test is
  driving" — preemption is by request provenance (API-owned vs world-internal,
  `_apiOwned`, `BotActionCommandQueue.cs:251-256`), not by mode. Two concurrent external
  callers race on busy-reject; a test wake and autonomy share one actor with no arbitration
  between them beyond single-writer.

### 3.2 Proposal — explicit ownership modes (minimal shape, no redesign)

```csharp
enum BotControlMode : byte { Autonomous, Manual, Test }  // per-bot, default Autonomous
// Stored beside the scheduler lease / runtime (location TBD — near _leases,
// PlayerBotScheduler.cs:17-18, or PlayerBotRuntime; NOT designed here).
// Transition: Manual/Test acquired explicitly (API/bridge call with owner token +
// expiry), released explicitly or on expiry; autonomy resumes on release.
// Enforcement (uses existing primitives only):
//  - Autonomous: scheduler steps + external API commands as today (busy-reject vs preempt unchanged).
//  - Manual: scheduler wakes skip stepping (bot held, wakes re-queued dormant);
//    API/MCP commands still flow through the queue with lifecycle + audit.
//  - Test: scheduler wakes skip stepping AND queue EXTERNAL commands are rejected
//    with a named reason (tests drive the actor directly); bridge/scripted legs unchanged.
// Audit: mode transitions recorded as control entries (same surface as Interrupt,
// BotActionCommandQueue.cs:466-478 pattern); running request interrupted on mode change
// via existing actor.Interrupt (GameplayActor.cs:761-767).
```

What this fixes: test/manual takeovers stop racing autonomy on busy-reject; "why stalled"
gains a first-class answer ("held in Manual/Test by owner O since T"). What it does NOT
change: queue lifecycle, single-writer rule, engine tails, scheduler cadence.

---

## 4. Dead-code / wiring classification

"Production-active" = reachable on the production step/drive path or holding production
state. "Test-only" = harness/scenario/rig-invoked. "Compat" = superseded but referenced
by DI/docs. "Dead" is NOT claimed anywhere below without a no-reference proof (not run);
those rows are UNKNOWN.

| Symbol | Classification | Evidence | Risk if autonomy depends on it |
|---|---|---|---|
| `UnwiredBotStepExecutor` | Deprecated (fail-closed DI default, inert) | Doc: "placeholder is inert in production … failure mode is explicit" (`UnwiredBotStepExecutor.cs:5-13`); superseded per `IBotStepExecutor.cs:7-11`, `GameplayActorStepExecutor.cs:27-28` | None — must never be wired; if DI regresses to it, all wakes no-op (`:25-28`) |
| `UnwiredPlayerBotLifecycleService` | Deprecated (fail-closed default, refuses activation) | (`UnwiredPlayerBotLifecycleService.cs:7-17`); refuses with warn (`:23-27`) | None if unwired; activation silently fails if rewired |
| `GameplayActorStepExecutor` | Compat (designed M5 landing, superseded by roam executor) | "replaces UnwiredBotStepExecutor in Program.cs; designed M5 landing" (`GameplayActorStepExecutor.cs:26-29`); roam executor "replaces GameplayActorStepExecutor as the production wiring" (`BotRoamStepExecutor.cs:57-60`) | Low — tick-only semantics retained inside roam executor for routeless bots |
| `BotRoamStepExecutor` | Production-active (production `IBotStepExecutor` wiring) | DI note as above (`:57-60`); owns quest leg branch 0a (`:652-656`), needs-farm 0b, route/hunt/butcher legs | N/A (current host of autonomy legs) |
| `BotGoalArbiterStepExecutor` | Compat/Unknown (G3-B3 decorator seam; production wiring UNKNOWN) | "least invasive arbitration point … decorator" (`BotGoalArbiterStepExecutor.cs:3-15`) | Medium — if G3 wires through it, activity-switch behavior lives here, not in the roam executor |
| `GoapBotStepExecutor` + `GoapPlanRunner` + `GoapPlanner` + registry | Unknown wiring (GOAP stack fully implemented; production-step usage UNKNOWN) | Planner budget 500 (`GoapPlanner.cs:29`); runner Tick sense→arbitrate→plan→execute (`GoapPlanRunner.cs:92-119`); ~20 registered actions (`GoapActionRegistry.cs:39-64`) | High — autonomy claims must not cite GOAP behavior unless the executor is proven wired; `BotRoamStepExecutor` (not GOAP) drives production legs |
| `PlayerBotControllerAdapter` | Unknown wiring (alternate `IGameplayActor` impl over pilot controller) | "exposes that controller through the IGameplayActor contract" (`PlayerBotControllerAdapter.cs:9-24`); single-writer rule restated (`:21-23`) | Medium — which `IGameplayActor` impl the scheduler ticks determines lifecycle/audit semantics |
| `PlayerBotController` (`Models/Game/Bots`) | Production-active fixture path (quest-drive helper, no lifecycle/audit) | "additive quest-drive-only helper; holds no quest state" (matrix §A4); methods call engine directly, Audit=N | High if cited as autonomy — ENGINE/SCRIPTED only, never actor evidence |
| `BotDriveBridge` (+ `BotE2EBridgeBootstrap`) | Test-only (fixture/observation; disabled by default) | "DISABLED BY DEFAULT … prod config never sets it" (`BotDriveBridge.cs:46-49`); bootstrap no-op when disabled (`BotE2EBridgeBootstrap.cs:11-17`) | High if promoted — ~30 ops bypass actor (matrix §A3); `housing construct` mutates directly |
| Duplicate world models: `BotWorldState` (GOAP 64-bit flags) vs `ActorObservation` vs `BotObservedContext` | Production-active parallels (all live) | Provider invariant: "compressed planning projection of reality, not reality itself … Detailed spatial targets, entity IDs, timestamps remain in BotContext/BotMemory" (`BotWorldStateProvider.cs:15-16`); observation snapshot `ActorObservation.cs:22-91`; decision copy `BotObservedContext`, `BotDecisionProposal.cs:5-16` | Medium — three projections can diverge (e.g. stealth filtering in `BotSurveySenses.cs:157-160` notes plain Observe does NOT apply `CanSeeTarget`); decisions must name which projection they consumed |
| `QuestDecisionScenario` / `NeedsDecisionScenario` / `EconomyDecisionScenario` / `PartyDecisionScenario` | Production-active (decision legs on the live step path) | Quest leg branch 0a calls `QuestDecisionScenario.Run` on the SAME actor (`BotRoamStepExecutor.cs:652-656`, `:1190-1195`); precedent cited (`QuestDecisionScenario.cs:18-25`) | N/A — behavior owners (see §5) |
| `LevelingLoopScenario`, `M1M2ReplayScenario`, `M3aM4ReplayScenario`, `M53CoreSurfaceExitScenario`, `PartySpike/FollowAssist`, `AdventurerSpike`, `AuctionHouse`, `FarmerCycle`, `FishingContest`, trade/expedition scenarios | Test-only (scenario library; harness-driven) | Inline `actor.Tick` pumps in scenario code (e.g. `LevelingLoopScenario.cs:1441-1444`, `M3aM4ReplayScenario.cs:1283-1286`); bridge scenario drivers (matrix §A2/A3) | Low — must not be cited as autonomy; `homestead claim/construct` traverses actor via GOAP action objects harness-invoked (matrix §A3 row) |
| `Village*Cycle`, `Hauler*Cycle`, `Crafter*Cycle`, `EconomyDayCycle` | Test-only (cycle library; pump-injected) | `Run(actor, options, pump)` signatures (e.g. `HaulerDriveRouteCycle.cs:112`, `CrafterWorkstationCycle.cs:134`); live pumps tick inline (`BotDriveBridge.cs:2405-2424`) | Low — engine-true but harness-chosen |
| `CombatExecutor` / `CombatDecisionTree` / `RoleCombatDecisionTree` | Executor production-active (cast path), trees UNKNOWN | Executor: GCD/cooldown-only retry, bounded attempts (`CombatExecutor.cs:28-32`, `:59-66`, `:181-191`); tree wiring not traced | Low for executor; trees must not be cited until wired |
| `DevMapperService` | Test-only/dev tool (manual route recorder) | "tracks manual walk routes" (`DevMapperService.cs:113-116`) | None — manual driving surface, see §3 |
| `BotScenarioRunner` / `BotScenarioTemplates` | Test-only (E2E rig framework) | Template provision+deactivate inside one call (matrix §A3 `provision` row); quest/economy drive specs | None if kept in rig |

---

## 5. Readiness checklist — NearbyQuestAcquisition

Scope note: NO symbol named `NearbyQuestAcquisition` exists in code (searched
`AAEmu.Game` + `AAEmu.IntegrationTests` — no hits; UNKNOWN). The acquisition path as
built is: `QuestDecisionScenario.Run` discovery branch (nearest in-range NPCs →
`DiscoverQuests` → accept lowest-level in-band offer, `QuestDecisionScenario.cs:27-33`,
`:119-133`) executed as the copper-bootstrap quest leg (`BotRoamStepExecutor.cs:1160-1195`)
under the `QuestBootstrapActivityModule` gate. Checklist assessed against that path.

| # | Item | Verdict | Evidence |
|---|---|---|---|
| R1 | Authoritative perception | PASS | `DiscoverQuests` asserts A1 marshal seam (`GameplayActor.cs:1164-1166`) and filters offers by real `AddQuest` pre-conditions (matrix §A1); leg perception via `BotObservedContext.Capture` + live NPC resolution (`QuestDecisionScenario.cs:99-101`, `:119-123`); default provider `WorldManager.GetAround<Npc>` (`BotRoamStepExecutor.cs:124-125`) |
| R2 | Goal owner | BLOCKED | Owner is `QuestBootstrapActivityModule` ("quest.progress", rank 58, `QuestBootstrapActivityModule.cs:34-41`) gated by `QuestBootstrapModuleOptions.Enabled`, **default OFF** (`:7-15`, `:20-25`); arbiter seam `BotGoalArbiter.cs:31-34` exists but G3 NOT IMPLEMENTED (established fact) — no production goal owns acquisition when the flag is off |
| R3 | Behavior owner | PASS | `QuestDecisionScenario` (per-wake advance-then-discover, deterministic priority, `QuestDecisionScenario.cs:13-34`, `:44-65`); invoked live-actor per wake on branch 0a (`BotRoamStepExecutor.cs:652-656`, `:1160-1195`) |
| R4 | Actor capability | PASS | `DiscoverQuests`/`AcceptQuest` are queue-dispatched capabilities with WebAPI+MCP, lifecycle Q, audit Y (matrix §A1 rows); `QuestDiscoveryResult` payload (`ActorRequest.cs:226-229`) |
| R5 | Action result | PASS | Terminal states + §17 failure taxonomy (`ActorRequest.cs:9-12`, `:53-57`); `QuestRunResult` carries `SelectedAction`, `Request`, `Failure/Detail` (`QuestDecisionScenario.cs:68-86`) |
| R6 | Cancellation | PASS | `actor.Interrupt(traceId)` (`GameplayActor.cs:761-767`); `Stop()` while busy (`:486-499`); queue `Interrupt` control op (`BotActionCommandQueue.cs:457-460`, `:618-632`); `POST /api/actors/interrupt` (`BotActionController.cs:1251-1278`) |
| R7 | Progress detection | PASS (leg) / UNKNOWN (autonomous) | Leg: before/after `ActiveQuestIds` diff → `CompletedQuestIds` (`QuestDecisionScenario.cs:101`, `:181-184`); post-condition check `BotDecisionCycle` (`BotDecisionProposal.cs:329-332`); loop projection `BotQuestLoopObservation.cs:35-57`. Whether a stalled acquisition (no offers, out-of-range reporter — the documented 60-advance/0-turn-in wedge, `QuestDecisionScenario.cs:107-112`) is detected WITHOUT the harness: UNKNOWN |
| R8 | Retry bound | UNKNOWN (leg) / PASS (primitives) | Primitives: idempotency ledger dedupe (`ActorIdempotency.cs:52-60`); GOAP `MaxActionRetries = 2` (`GoapPlanRunner.cs:32-33`, `BotContext.cs:24`). But `QuestOptions` carries NO retry/max-attempt field — only `MaxDiscoverTargets = 3` (`QuestDecisionScenario.cs:44-65`); no per-quest failure counter or cooldown on the acquisition branch found |
| R9 | Audit trace | PASS | Per-action `ActorAuditRecord` (§1.1); leg `TraceRecords` (`QuestDecisionScenario.cs:84-85`); queue history + sink (§1.2); v3 decision columns exist but writers UNKNOWN (§1.1 gap) — "which offer, why" needs the stamp |
| R10 | Fixture / testability | PASS | Provider seams (`NearbyNpcProvider`, `NearbyDoodadProvider`, `UnitResolver`, `BotRoamStepExecutor.cs:124-148`); deterministic selector (`BotDecisionSelector`, `BotDecisionProposal.cs:231-292`); Q0 pilot precedent (matrix ref `Q0ActorAcceptQuestPilotTests.cs`) |
| R11 | Feature-flag state | BLOCKED | `QuestBootstrapModuleOptions.Enabled` default OFF, env `AAEMU_QUEST_BOOTSTRAP_ENABLED=1` (`QuestBootstrapActivityModule.cs:7-25`); external driving separately gated by `AAEMU_BOT_CTRL` (`BotActionController.cs:36-38`). Acquisition cannot run in production without opting in the bootstrap module |

**Bottom line:** capability, behavior, result, cancellation, audit, and testability are
PASS; the path is BLOCKED on goal ownership + flag state (R2, R11), with retry bound (R8)
and autonomous progress detection (R7) UNKNOWN — the two items most likely to wedge a
live rollout (cf. the 60-no-op-advance wedge documented at
`QuestDecisionScenario.cs:107-112` and the arrival-gate wedge `BotPresenceCoordinator.cs:148-158`).
