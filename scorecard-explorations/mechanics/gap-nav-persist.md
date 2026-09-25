# Navigation / Progress / Persistence Gap Audit (code audit, develop @ 2026-09-18)

READ-ONLY audit. No file was modified; no server, test, or build was run. Every claim
cites `file:line`. Anything not found in code is marked UNKNOWN — nothing is inferred.
Prior context (do-not-redesign): the canonical seam is
`POST /api/actors/* → BotActionCommandQueue → GameplayActor → engine`
(`scorecard-explorations/mechanics/capability-surface-matrix.md`);
G1 proved MoveTo converges (0.4 m measured vs 0.5 f box); G3 proved quest autonomy is
deployment-gated off. This pass audits gaps/risks only.

Movement code under audit: `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs`
(`MoveTo` :292, `NavigateTo` :312, `NavigateToUnit` :323, `MoveToUnit` :462,
`NavigateToInternal` :338, `StartMove` :398, `Tick` move branch :4116, drive branch
:4207, stuck detector :4343, `ClearMovementState` :4444), dispatch in
`AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs` (`ExecuteKind` :513+,
move kinds :521–547, backstop :389–411, busy gate :437–452), contract in
`IGameplayActor.cs` (:81–106) and lifecycle in `ActorRequest.cs` (:14–206).

---

## 1. NAVIGATION CONTRACT: MoveTo vs NavigateTo vs MoveToUnit vs NavigateToUnit

### 1.1 The four verbs (all collapse to `ActorActionType.Move`)

| Verb | Entry | Target resolution | Path planning | Queue kind / external route |
|---|---|---|---|---|
| `MoveTo(dest)` | `GameplayActor.cs:292` | absolute `Vector3`, pre-flight finite check :307–308 | none — `StartMove` direct :309 | `BotActionKind.Move` :24 → `ExecuteKind` :521–527 → `POST /api/actors/move` |
| `NavigateTo(dest)` | `:312` | absolute `Vector3`, finite check :342–343 | `NavigateToInternal` :338: navmesh A* :353–368 → obstacle detour :377–388 → `StartMove` fallback :391 | `BotActionKind.Navigate` :45 → :529–535; NO WebAPI/MCP route (queue-only; matrix §C.1) |
| `MoveToUnit(objId)` | `:462` | unit resolved **once at dispatch** :471–474, snapshot `unit.Transform.World.Position` → `StartMove` | none | `BotActionKind.MoveToUnit` :25 → :537–541 → `POST /api/actors/move_to_unit` |
| `NavigateToUnit(objId)` | `:323` | unit resolved **once at dispatch** :331, snapshot → `NavigateToInternal` :335 | same 3-tier as `NavigateTo` | `BotActionKind.NavigateToUnit` :85 → :543–547; NO WebAPI/MCP route (queue-only) |

Related fifth leg: `DriveVehicle(vehicleObjId, dest)` (`GameplayActor.cs:2098`; kind
`DriveVehicle`, `ExecuteKind` :722–734, `POST /api/actors/drive_vehicle`) — drives the
*vehicle* transform through `VehicleMovementModel` (:4432–4437), arrival radius same
0.5 f (:2126–2127, tick :4213–4218), requires driver seat (:2117–2124).

### 1.2 Arrival predicates (identical for all four)

Arrival ⇔ flat (XZ) distance ≤ `ArrivalRadius` (0.5 f, `:75`) AND `|ΔZ|` ≤ 0.5 f.
Four independent enforcement sites, all consistent: `NavigateToInternal` no-op
:346–347, `StartMove` no-op :400–401, `Tick` :4153, drive :2126/:4213. Already-there
legs walk the full lifecycle (`Start("walking")` → `Complete("already at
destination")`, :349–350/:407–408) so a `Completed` record never skips `Running`.
Pure-vertical legs step Z only (:4198–4203, drive :4229–4233).

### 1.3 Terminal-condition table (movement legs)

| Terminal | Reason / detail | Produced at |
|---|---|---|
| `Completed("arrived")` | flat+Z box met; `BroadcastStop()` + `ClearMovementState()` + `Finish` | `GameplayActor.cs:4176–4178` (drive :4215–4216) |
| `Completed("already at destination")` | no-op leg, full lifecycle | `:349–350`, `:407–408`, drive `:2131–2132` |
| `TimedOut(Navigation, "navigation budget exceeded")` | `Elapsed > Timeout` (default `DefaultMoveTimeout` 30 s, `:78`); `BroadcastStop()` on Move | `:4031–4042` (`ActorTimeoutPolicy.ReasonFor` maps Move/Drive → Navigation) |
| `TimedOut(Navigation, "stuck: no progress {t}s")` | stuck detector (see §1.4); `Expire` used because `Interrupt` cannot carry a §17 reason (:4330–4336) | `:4372–4376` |
| `TimedOut(Starvation\|Navigation, "action budget exceeded (queue backstop)")` | scheduler stopped/not ticking; measured from **enqueue** time on queue clock | `BotActionCommandQueue.cs:399–410` |
| `Rejected(RejectedAction, …)` | `speed must be positive` / `destination must be finite` / `target unit not found` | `:305–308`, `:340–343`, `:471–473`, `:331–333` |
| `Rejected(StateTransition, "actor busy with …")` | single-writer `TryBegin` gate | `:4565–4572` |
| `Rejected(StateTransition, duplicate idempotency key)` | key whose prior attempt Completed/Interrupted/TimedOut is never re-executed | `:4583–4592` |
| `Interrupted("stop requested")` | `Stop()` interrupts live Move + `BroadcastStop()` | `:477–497` (`:493–495`) |
| `Interrupted("interrupted by controller")` | `Interrupt(traceId)` exact-trace match; mismatch/terminal → `false` (silent no-op) | `:761–767` |
| `Interrupted(…)` (queue preempt) | world-internal leg (roam/scenario) preempted so an API command lands | `BotActionCommandQueue.cs:448–451` |
| `Rejected(StateTransition, "actor busy with {Move}")` | API command arrives while another API command runs — caller must poll to terminal first (no preempt of API-owned legs) | `BotActionCommandQueue.cs:437–446` |

Timeout semantics: `ActorRequest.Timeout` (`ActorRequest.cs:48–49`), `Elapsed`
accumulates **only in `Running`** via `Tick` (`:180–184`, advanced `:4026`).
Stuck declaration arrives well before budget whenever `NoProgressWindow < Timeout`
(`:4337–4338`); with defaults 2.5 s vs 30 s a blocked leg fails in ~2.5 s + one nudge.

### 1.4 Timeout / stuck semantics; navmesh vs detour fallback

- Stuck detector (`UpdateMoveStuckState`, :4343–4377): 3-D displacement
  (`Vector3.Distance`, :4350) from last progress mark > `ArrivalRadius` resets the
  no-progress timer; else it accumulates over `NoProgressWindow` (default 2.5 s,
  :116/:130). Expiry → one bounded lateral nudge (2 m, `UnstickNudgeDistance` :119,
  alternating sides :4391, `MaxUnstickNudges = 1`, :137) steered as `legTarget`
  (:4123, :4366), then fail-fast `TimedOut(Navigation)`. `NoProgressWindow <= Zero`
  disables detection (test seam, :4347–4348) — leg rides full budget.
- Navmesh tier (`NavigateToInternal`, :353–374): only when
  `parentWorld.Template.GeoData != null && GeoDataMode` (:354); `FindPath` exception
  → warn + fall through (:370–373). Path with ≤1 waypoint is **not** a route —
  falls through (`:360` requires `Count > 1`).
- Obstacle tier (:376–389): `ObstacleManager.IntersectsObstacle` → `FindDetour`,
  requires `Count > 1`.
- Silent-degrade tier (:391): anything else → `StartMove` straight line. There is
  **no distinct terminal/detail for "fell back to straight line"** — audit shows
  `navigating route (N waypoints)` / `navigating obstacle detour (N)` / `walking`;
  a caller cannot tell from the terminal record that navmesh was unavailable.
- Corner blending (`MoveCornerBlendRadius = 1 f`, :166; tick :4139–4151):
  intermediate waypoints inside the blend radius are cut (no stop-start, speed
  carried through); final destination, pure-vertical, and unstick legs keep exact
  arrival. Waypoint advance resets progress tracking (:4169–4170).
- Trapezoid profile (`ProfiledMoveSpeed`, :4297–4308): accel/decel-shaped cruise,
  no overshoot (step clamped to remaining distance, :4191).

### 1.5 Gap findings (navigation)

1. **Moving targets are snapshot-only (both `*ToUnit` verbs).** `MoveToUnit`
   (:471–474) and `NavigateToUnit` (:331–335) resolve the target's position once at
   dispatch; `Tick` never re-resolves. A walking player/mob outruns the snapshot
   and the leg `Completed("arrived")` at a stale point. The roam executor
   compensates *locally* (re-issue when `PendingLeg.Destination` drifts >2–3 m:
   `:583–584`, `:755–756`, `:890–891`; chase legs with 5–10 s budgets :559/:590,
   :762/:897), but a generic queue/MCP brain calling `move_to_unit` gets no
   re-track and no "target moved" signal. **BLOCKER-class for follow/escort/pursuit
   autonomy.**
2. **Unreachable destinations are never `Rejected`.** No reachability pre-flight
   exists: navmesh miss → straight-line walk (:391); `Tick` stepping
   (`ApplyCharacterMove`, :4253) has **no collision check** — the leg walks into
   geometry until the stuck detector fires (2.5 s + 1 nudge → `TimedOut`). Cost
   per bad leg ≈ 5 s + audit noise; a brain exploring unknown terrain pays this
   per probe. Major (latency sink), not a hang (always bounded).
3. **No path invalidation / no repath mid-leg.** `_moveWaypoints` is dequeued once
   at dispatch; nothing recomputes on obstacle-appear, navmesh-update, or target
   teleport. An external teleport mid-leg **resets the stuck timer** (displacement
   > radius, :4350) and the leg continues to the stale destination — teleport does
   not complete/cancel a leg. Major for long legs in dynamic scenes.
4. **Interzone behavior is undefined — BLOCKER-class.** Destinations are bare
   `Vector3` with no world/instance/zone id; every lookup is
   `Character.ParentWorld` (same-world registries throughout §1.5 grep). `Tick`
   never changes `ParentWorld`/`InstanceId`; no cross-world guard or
   `Rejected(cross-world)` exists. A destination in another world/instance is
   walked-toward meaninglessly until budget expiry (30 s). Continental-scale
   autonomy (portals, transfers, cross-zone quest chains) has no movement
   contract. Zoning during a leg: UNKNOWN (no zone-transfer code in `Tick`;
   whether `FinalizeTransform` re-zones was not verified — marked UNKNOWN).
5. **Mounts: handled (mate branch). Vehicles/transports: UNKNOWN.** `Tick`
   stepping routes through `VehicleMovementModel.ApplyUnitMove` when
   `GetIsMounted` returns a mate (:4265–4271). Slave-boarded (`BindSlave`) and
   transfer-seat (`Bonding`) states have **no branch** in the Move path — what a
   Move leg does while ferry-riding is unverified (UNKNOWN). `DriveVehicle` covers
   driving but has **no stuck detection** (drive branch :4207–4241 only budget
   expiry) — a beached ship burns the full 30 s. Minor.
6. **Indefinite-"moving" states: one real case.** API-owned legs always terminate
   (actor budget :4031 + queue backstop :399, measured from enqueue). But the
   backstop iterates **only `_apiOwned`** (`DrainCommands`, :367) — world-internal
   legs (roam `PendingLeg`, scenario steps) are ticked solely by scheduler wakes
   (`BotRoamStepExecutor` :987, woken per API command :496–497 or roam cadence).
   **If the scheduler is stopped, an internal Move leg is never `Tick`d, never
   accrues `Elapsed`, never expires — indefinite `Running`.** Single-writer then
   wedges the actor (`TryBegin` busy-rejects everything). Secondary: drive legs
   lack fast-stuck (bounded, 30 s). `Interrupt(traceId)` mismatch returns `false`
   with no record (:761–767); the queue maps a history-miss to a `Completed("no
   matching active request (idempotent)")` control record (:463–470) — a
   `Completed` that cancelled nothing (misleading-success shape, minor).

---

## 2. PROGRESS DETECTION

Question: does behavior code expose *measurable* progress (distance-decreasing,
HP-decreasing, objective deltas) or wall-clock only?

### 2.1 Findings

| Layer | Progress signal | Wall-clock bound | Verdict |
|---|---|---|---|
| `GameplayActor` Move (stuck detector, :4343–4377) | **distance-decreasing**: 3-D displacement > 0.5 m resets no-progress timer (:4350); arrival box flat+Z (:4153) | `NoProgressWindow` 2.5 s + 1 nudge, outer 30 s budget | genuine progress + wall-clock (note: progress uses 3-D `Vector3.Distance` while arrival uses flat+Z — vertical jitter counts as progress but not arrival; negligible) |
| `GameplayActor` Cast effect observation (:4004–4017, :4484–4519) | **HP-decreasing telemetry**: `HpBefore` sampled at accept (:565), polled each `Tick`, `hpAfter != HpBefore` closes window early (:4512–4514), attached to audit as `TargetHpBefore/After`, `EffectObserved` (:4533–4538) | `EffectObservationWindow` 2 s (:85) | HP delta is **audit enrichment only** — the Cast request already `Complete`s at cast acceptance (:584); nothing gates on HP. Combat has HP-decreasing *evidence*, no HP-gated *timeout* |
| Needs/Quest decision scenarios (`NeedsDecisionScenario.cs:152`, `QuestDecisionScenario.cs:89`) | **objective deltas per wake**: before/after snapshots — money/material/food/seed counts (:171–175), `ActiveQuestIds` before-set (:101) vs after (:181) → `CompletedQuestIds` (:80); `BotProposalPostcondition` predicate over terminal observation (`BotDecisionProposal.cs:128–139`), checked in `BotDecisionCycle.Execute` (:330–332) → `ExpectedPostconditionSatisfied` (`NeedsRunResult` :144) | per-leg 30 s timeouts (craft/plant/buy/harvest/sell proposals); non-terminal after dispatch → `Fail("EXECUTE", Starvation)` (:207–212, "no tick service in N1") | deltas exist but are **single-wake before/after**, not tracked across wakes; a leg that lands 90% of the way N wakes in a row looks identical to one making no progress |
| Roam executor travel/wait legs | **distance-to-target re-checks** (merchant ≤ `MaxShopRange` :1445/:1483, crop ≤ 2.5 m :1517, soil ≤ `ArrivalRadius+1` :1587–1589); maturity-wait defers without dispatching Harvest (:1373–1377); soil attempts/defer wakes bounded (`NeedsFarmSoilDiscoveryRadius` 150 f :1141, attempts/defer caps :322–332) | `RoamLegTimeout` 60 s (:98–99, longer than actor default so route legs never mid-walk timeout); `MaxStepElapsed` 1 s clamp (:67) vs scheduler stalls | re-issue-on-drift (2–3 m, §1.5.1) is the de-facto progress loop; no leg-level "distance is not shrinking" detector of its own |
| GOAP runner (`GoapPlanRunner.cs:86`) | **effect-verification per action**: in-flight `EvaluateStatus` (:160–164) → `Succeeded` (world confirms effects :168–169) / `Failed` (retry ≤ `MaxActionRetries = 2`, :33, then replan or goal-abandon :189–225) / `Invalidated` (replan :228–233); interrupt watchdog preserves/resumes primary intent (:96–157) | UNKNOWN per-action wall-clock bound — `EvaluateStatus` impls (`GoapActionBase` and actions) were **not** read this pass; whether one action can sit `Running` forever without `Succeeded/Failed` is UNKNOWN | structure is progress-shaped; needs per-action bound verification (provisional major, see §2.2) |
| `CombatExecutor` | attempt counting (`NextAttemptAtUtc`, `Attempts`, :280–282), stationary clock spends nothing (:33–37) | `WaitBudget` (:33–34), `TimeProvider`-owned (fakeable, :66) | bounded waits; HP-gating depth UNKNOWN (not read) |
| `QuestDecisionScenario` Ready-quest guard | negative-progress guard: Ready quests are **excluded** from advance proposals — advancing Ready is a no-op `Completed` that starved route legs (60 advances, 0 turn-ins, bot never moved; :107–117) | — | precedent: counting terminal `Completed` as "work" without a delta check caused a live standstill; the per-wake `CompletedQuestIds` delta (:181–182) is the fix shape |

### 2.2 Proposed generic progress-timeout shape

Mirror `UpdateMoveStuckState` (:4343) — the one proven loop — generalized over an
observation type (already the pattern of `BotProposalPostcondition`,
`BotDecisionProposal.cs:128`):

```
ProgressTimeout<TObs> {
  sample(): TObs                        // BotObservedContext.Capture today
  improved(before, after): bool         // e.g. distance shrank > ε, bag count Δ,
                                        // HP Δ, ActiveQuests Δ — the postcondition
                                        // predicates already written per proposal
  window: Duration                      // NoProgressWindow analogue (2.5 s move)
  maxRecoveries: int                    // MaxUnstickNudges analogue (1 move)
  recover(): void                       // nudge / re-issue / replan analogue
  // on exhaustion: TimedOut(Navigation|Starvation) with "stuck:"-prefixed detail
  // (the :4330–4336 convention), always with window << outer budget (:4337–4338)
}
```

Concretely: (a) wire per-action wall-clock bounds into GOAP `EvaluateStatus`
(close the §2.1 UNKNOWN); (b) lift the roam re-issue thresholds (2–3 m) into an
explicit shrinking-distance check with a bounded retry count instead of
unbounded re-issue; (c) persist a cross-wake delta (e.g. distance-to-objective at
last wake) in `BotRoamState` so 90%-then-stall loops are detectable — today every
wake re-derives from scratch.

---

## 3. PERSISTENCE: what survives restart / reconnect / zoning

Restart model: E2E `RestartGameServer` kills **only** the game process
(`E2eStack.cs:674–686`; MySQL + login stay). Anything not in MySQL (or the
runtime sqlite) is lost. `SaveManager.DoSave` persists per cycle (:95–217).

### 3.1 Classification table

| State | Class | Evidence | Survives kill -9? |
|---|---|---|---|
| `characters` row (level, money, position, world/zone) | **Authoritative** | `SaveManager` dirty-or-all save :131–139; same-row re-embody asserted (`AuctionHouseRestartE2eTests`: same character id); position sampled from DB post-save (fishing `ForceSaveAndSample` reads x,y,z) | Y (dirty-tracked; `saveAllCharacters` on shutdown/manual `save`, :84–85) |
| `items` / inventory / bank / containers | **Authoritative** | `itemManager.Save` :121; B4 item-set equality pre/post (`DumpItemRows`) | Y |
| `quests` / `completed_quests` | **Authoritative** | engine `AddQuest`/`DoReportEvents` rows; quest tables cleaned by tests (B1/B2 teardown) | Y |
| houses, doodads (crops), mails, `auction_house`, crimes, slaves | **Authoritative** | `housingManager.Save` :117, `mailManager.Save` :119, `auctionManager.Save` :123, `crimeManager.Save` :125, slave loop :143–150; B1 housing / B2 farm / auction / dominion restart proofs | Y |
| `playerbot_metadata` (home, schedule, profession, personality, behaviorConfig, plannerState) | **Long-lived intent** | write-through `REPLACE INTO` on mutation + `SaveDirty` hook :126–127; store :26–34, :176–202; fields `PlayerBotMetadata.cs:19–52` | Y (hard-kill safe by design) |
| `playerbot_audit` trace | **Authoritative (telemetry)** | `PlayerBotAuditSink.Flush` :129 | Y (buffered terminal records) |
| Dormant specs (managed-bot characters rows) | **Long-lived intent** | re-discovered from SQL join each boot (`MySqlDormantBotSource` :111–115; lazy `ListSpecs` :178–195) + home restored from metadata (`Materialize` :229) | Y (reconstructable from DB, not a separate row) |
| `BotProgressionCursor` Tier0 (quest 4438 / copper 100 / seed 15659 / yield 7992) | **Long-lived intent (capability)** | `BotProgressionCursor.cs:53–59`; serialize/deserialize :64–81; `Advance` pure over (copper, level, bag, completedQuests) :87–99 | **UNWIRED — see §3.2.4** |
| `BotRoamState` (path, PendingLeg, farm phase, quest-travel target, wake flags) | **Reconstructable (memory)** | `ConcurrentDictionary` in executor (`BotRoamStepExecutor.cs:356`); quest-travel explicitly "Memory-only … a restart re-resolves" (:259–261) | N by design ✓ (re-armed from durable quests/positions each wake) |
| Actor in-flight (`_moveTarget`, `_moveWaypoints`, `_driveTarget`, `_unstick*`, `_pendingPutDownPackId`, `PendingCastEffect`s) | **Reconstructable (memory)** | fields :3974–4002; cleared on terminal (`ClearMovementState` :4444); new actor per boot via `ActorFactory`/`GetOrCreateActor` | N by design ✓ |
| GOAP `ActivePlan`/`CurrentActionIndex`/retries/`BotContext.Memory` | **Reconstructable (memory)** | per-bot runner/context dicts (`BotRoamStepExecutor.cs:1342–1343`); `GoapPlanRunner` fields :23–33 | N — intent loss, see §3.2.2 |
| Queue `_history`/`_apiOwned`/snapshots, scheduler due/lease maps, idempotency `ActorEffectLedger` | **Reconstructable (memory)** | `BotActionCommandQueue.cs:247–258`; `PlayerBotScheduler.cs:47–55`; ledger outcomes in actor (`Finish` :4647) | N — dedupe loss, see §3.2.1 |
| Audit in-memory `_trace` (bounded `MaxTraceRecords`, oldest evicted :4655–4656) | **Reconstructable ring** | `Finish` :4648–4656 | N (durable copy via audit sink flush) |

### 3.2 Flags

1. **Idempotency ledger is memory-only — same-key retry after restart re-executes.**
   `Finish` records outcomes in the per-actor in-memory ledger (`GameplayActor.cs:4647`);
   the queue backstop parity path (`:404–407`, `RecordBackstopTimeout` :4666) is also
   memory. After a restart the ledger is empty: a retried key executes again.
   Partial engine-true backstops survive (craft materials consumed :4067–4071, plant
   seed gone, currency balance gates :3303–3314), but the *guarantee* ("retries and
   timeouts cannot duplicate", :4576–4580) holds only within one process lifetime.
   Major for crash-recovery correctness.
2. **GOAP mid-plan intent is silently lost.** Restart drops `ActivePlan`, action
   index, retry counts, and `BotMemory` failure records; the homestead runner is
   rebuilt fresh and replans from scratch. For the multi-step homestead chain
   (plot → timber → pack → construct) there is no durable step cursor (see 4).
   The interrupt-watchdog resume (:134–147) only covers in-process interrupts.
   Major for long-horizon autonomy.
3. **Maturity-wait target (`NeedsFarm` planted objId/template) is memory-only.**
   Recovery works via world re-scan (`NearestNeedsFarmCrop`) because the crop
   doodad row itself is authoritative — but an objId-obsessed waiter could orphan
   if doodad identity shifts across respawn. Minor (self-heals to Idle → rescan).
4. **Progression cursor appears unwired in production.** `BotProgressionCursor`
   documents serialization "through `RecordPlannerState`" (:9–11), but a repo-wide
   grep finds **zero production callers** of `RecordPlannerState` (only the store
   definition :161 and the cursor doc). `StepHomesteadLeg` (:1350–1362) ticks the
   runner without persisting anything. So the one durable autonomy-progress
   mechanism (PlannerState JSON) has a producer/consumer pair with no writer on
   the live path — homestead progress has no durable cursor. Major (dead-seam or
   missing wiring; either way a brain cannot resume homestead across restarts).
5. **Nothing ephemeral is persisted unnecessarily — CLEAN.** Metadata carries only
   the six intent fields (home/schedule/profession/personality/behavior/planner);
   no per-tick state (`PendingLeg`, waypoints, stuck timers, `BotPath`, scheduler
   leases) reaches the DB. No planner-internal bloat found.
6. **Crash-between-consume-and-grant (craft) atomicity UNKNOWN.** `EndCraft`
   consumes before granting (comment :4067–4071); an in-flight craft step at
   kill -9 loses the engine queue (memory) — whether materials are lost or
   refunded was not verified in this pass. UNKNOWN, needs engine-path check.
7. **Trade handshake / party / expedition in-flight state durability UNKNOWN.**
   No save-table evidence found for in-flight trade sessions; `TradeLockOk` legs
   interrupted by restart presumably fail on re-embody (participants gone).
   UNKNOWN.
8. Zoning (same-process world/instance switch): character rows persist position +
   world/zone (DB read), in-flight legs reference `ParentWorld` objects — a zone
   switch mid-leg leaves `_moveTarget` pointing at old-world coordinates (§1.5.4).
   Reconnect (same process): actor idle, wakes re-arm from durable state. No
   silent corruption found beyond §1.5.3/4.

---

## 4. FLAGS + CONTENT + DETERMINISM

### 4.1 Full `AAEMU_*` env-flag manifest (bot-affecting)

All bot flags are **boot-time**: `E2eStack.StartServerProcess` builds the game
process env from the runner (`E2eStack.cs:533–568`) and every `*Options`
singleton is constructed once (`Program.cs:301–363`); several executors read env
in property initializers at construction (`BotRoamStepExecutor.cs:70–79`,
`:105–163`). Runtime env changes have no effect; E2E tests set flags before
`EnsureUp`/`RestartGameServer` (which re-reads runner env each boot, :674–686 —
`ProcessStartInfo` inherits the runner env, so flags persist across restarts
unless the test clears them).

| Flag | Default | Prod intent | Test intent | Silent-absence risk |
|---|---|---|---|---|
| `AAEMU_BOT_CTRL` | OFF (`BotControlSettings.cs:18`, needs `1/true` or `Bots.EnableBotControl`) | unset → API 404s | `1` (G1/Q0/MCP lanes) | LOW — loud 404 |
| `AAEMU_BOT_CTRL_TOKEN` | unset (fail-closed `TokenMatches`, :30–34; config fallback `Bots.BotControlToken`) | unset | per-run secret | LOW — loud 401 |
| `AAEMU_BOT_CTRL_URL` | `http://127.0.0.1:1280` (sidecar `Program.cs:13–16`, both sidecars) | off-box tool only | lane `WebApiPort` | LOW |
| `AAEMU_PRESENCE_DEMO` | OFF (`BotPresenceCoordinator.cs:110–112`) | ON (presence overlay) | `1` (presence/B4/soak lanes) | **HIGH — no citizens, no error**; B4 asserts on boot log line |
| `AAEMU_PRESENCE_BOT_COUNT` | 3, clamp 1..max (:174–177) | 250 (presence overlay) | 3–10 per lane | MEDIUM — wrong scale, no error |
| `AAEMU_PRESENCE_MAX_BOTS` | 10 (:213–218) | 250 | lane-sized | MEDIUM — roster silently clamped |
| `AAEMU_PRESENCE_MANIFEST` | unset → legacy 3-citizen loop (:254–257) | roster path or unset | soak manifests | MEDIUM — wrong roster silently |
| `AAEMU_PRESENCE_HOME_X/Y/Z` | unset/partial → `Vector3.Zero` → template-spawn fallback (:667–673; precedence :599–602) | patrol home | B4 pins `(19950,20050,100)` | **HIGH — partial set silently ignored** (typo → bots at spawn, test walks 6.7 km for nothing) |
| `AAEMU_PRESENCE_HUNT` / `_HUNT_RADIUS` | OFF / 45 m (`BotRoamStepExecutor.cs:105–110`; forwarded `:554`) | `1` / 45 (overlay) | opt-in per lane | MEDIUM — hunt silently off |
| `AAEMU_PRESENCE_BUTCHER` / `_BUTCHER_RADIUS` | OFF / 45 m (:155–160) | UNKNOWN (not in overlay snippet) | opt-in | MEDIUM |
| `AAEMU_PRESENCE_BROADCAST_HZ` | 15 Hz (:70–79) | 15 | default | LOW |
| `AAEMU_NEEDS_FARM_ENABLED` | OFF tri-state (`NeedsFarmModuleOptions.cs:48–51`) | `1` (presence overlay :23) | `1` (farm-loop lanes) | **HIGH — farm autonomy silently off** (G3-class gate) |
| `AAEMU_QUEST_BOOTSTRAP_ENABLED` | OFF (`QuestBootstrapActivityModule.cs:23–24`) | UNKNOWN (absent from overlay snippet; G3 lane verified absent) | `1` (perception-loop lane) | **HIGH — THE G3 finding**: quest leg never arbitrates, bot idles, no error |
| `AAEMU_BOT_SCHEDULES_ENABLED` / `AAEMU_BOT_SCHEDULE_SCAN_SECONDS` | OFF (`BotScheduleOptions.cs:54–58` / :42–44) | UNKNOWN (default OFF) | opt-in | MEDIUM |
| `AAEMU_BOT_CHATTER_ENABLED` / `_RADIUS` / `_ZONE_BUDGET` | OFF (`BotChatterOptions.cs:75–79` / :60–66) | UNKNOWN (default OFF) | opt-in | LOW (cosmetic) |
| `AAEMU_FISHING_CONTEST_ENABLED` | OFF (`FishingContestActivityModule.cs:25–26`) | UNKNOWN | opt-in | LOW |
| `AAEMU_BOT_PROXIMITY_FIDELITY` / `_FULL_M` / `_REDUCED_M` | OFF / 75 m / 200 m (`PopulationDirectorOptions.cs:136–137` / :191–198) | UNKNOWN | A3/A5 probes `1` | **HIGH for scale claims** — without it, always-embodied; dormancy tests assert on it |
| `AAEMU_BOT_TRUE_DORMANCY` | OFF (:144–145) | UNKNOWN | A3/A5 probes `1` | **HIGH** — same as above |
| `AAEMU_BOT_STAGGERED_WAKES` / `AAEMU_BOT_STAGGER_WINDOW_MS` | OFF / 5000 ms (:214 / :206–208) | UNKNOWN | A3 staggered phase | LOW (perf shaping) |
| `AAEMU_BOT_DORMANCY_MATERIALIZE_PER_SWEEP` | 3 (:201–203, :217) | UNKNOWN | default (A5 notes raising only front-loads) | LOW |
| `AAEMU_BOT_AUTO_RESTORE` | OFF (`BotAdminService.cs:640–643`) | UNKNOWN | opt-in | MEDIUM |
| `AAEMU_BOT_PROVISION_TEST` / `_PORT` | OFF / 1261, loopback-only (`BotProvisioningControlHost.cs:63–69`) | never set | live-rig `1` | LOW (test-only surface) |
| `AAEMU_E2E_LOG_LEVEL` | `Info` (`NLog.config`) | `Info` | `Debug` (rowboat/ship lanes) | LOW |
| `AAEMU_LIVE_RIG` / `AAEMU_E2E_DB_{PASSWORD,HOST,PORT}` / `AAEMU_E2E_ROOT` | OFF / unset (`HeadlessSessionProvisioningLiveTests.cs:23–26`, `:65–67`, `:381`) | N/A | live-rig gate only | LOW — loud skip |
| `AAEMU_COMPACT_SQLITE3` | repo `Data/compact.sqlite3` (`QuestNoStartClusterTests.cs:91–93`) | N/A | reference-DB override | LOW |
| `AAEMU_MCP_BOT` / `AAEMU_GAME_API_BASE` | tool defaults (benchmark/dashboard scripts) | tester token/host | lane values | LOW |
| `AAEMU_ROOT` / `ARCHEAGE_*` | archaeology MCP roots (`SourceCatalog.cs:87–90`) | N/A | N/A | none (out of scope) |
| `AUCTION_FLEET_SIZE` (no `AAEMU_` prefix) | 25 (`AuctionHouseScenario.cs:79–80`) | UNKNOWN | default | LOW — note non-conforming name |

DI wiring (`Program.cs`): schedule/fishing/quest-bootstrap/needs-farm options
singletons :301–311, arbiter priority order Schedules(100) > ConflictJoin(75) >
FishingContest(60) > QuestBootstrap(58) > NeedsFarm(55) > PresenceRoam(50) >
Idle(0) (:290–300), queue singleton (lazy tick subscription, :334–340), scheduler
+ bounded pool (:342–346), proximity/dormancy options :363, chatter :380,
schedules service :388. `E2eStack` explicitly forwards only 4 flags
(`AAEMU_NEEDS_FARM_ENABLED`, `AAEMU_QUEST_BOOTSTRAP_ENABLED`,
`AAEMU_PRESENCE_HUNT[_RADIUS]`, :540–559) — the rest ride `ProcessStartInfo` env
inheritance; the asymmetry is a **documentation trap** (a reader concludes only
4 flags survive restart), not a runtime gap. E2E-only `E2E_*` (root, ports
:55–63, bridge 1260, `E2E_WIRE_DUMP`, `E2E_REBUILD`, growth rate) are harness,
not game behavior.

### 4.2 Content / data dependencies + hard-coded-ID compensations

| Dependency | Consumer | Compensation / pin | Risk |
|---|---|---|---|
| `compact.sqlite3` quest rows (contexts/acts) | decision scenarios (band [1,9] `QuestDecisionScenario.cs:53–56`; quest 251 L2 in G3 lane) | canonical md5 asserts (`DominionSoak` runtime-vs-canonical); seeded-defect harness patches `quest_act_con_report_npcs` (`E2eStack.cs:766–777`); census-only drops kept live by design | sqlite overlay drift silently changes offer sets; band filter then yields "no legal proposal" (fail-closed, but indistinguishable from flag-off idleness) |
| Tier0/economy ids: quest 4438, seed 15659 → crop 2259 → yield 7992, merchants 8522 (pack 171)/8524, craft 2846 → 16187, water 15694, recover 11361, fishing 21571/plot 809/bait 27142 | cursor (`BotProgressionCursor.cs:53–59`), farm legs (`BotRoamStepExecutor.cs:1122–1127`), economy cycle (`EconomyDayCycleScenario.cs:32–42`, documented "verified against compact.sqlite3") | engine gates revalidate at dispatch (merchant `SellsItem`, `GameplayActor.cs:2719–2724`; plant farm-membership, `NeedsDecisionScenario.cs:298–314`) — wrong ids become `Rejected`, not corruption | merchant-pack edits break farm/earn loops loudly-ish; water 15694 NOT merchant-sold → `StockInventory` fixture prerequisite (documented :39–40) — a brain without the fixture cannot craft |
| `npc_spawns.json` / `doodad_spawns.json` | quest travel (`TrySpawnerPosition`, `:1331–1340`), hunt/merchant/crop discovery, spike fixtures (fox spawners `AdventurerSpikeScenario.cs:34–36`) | walk-to-spawner so world materializes reporter (:1257–1274); `GetNpcByTemplateId` null → skip/turn-in-proposal absent (`QuestDecisionScenario.cs:244–246`) | spawner edits strand travel targets; unspawned reporter → bounded walk-and-wait per wake (no infinite trek — single-leg `PathTo`, re-evaluated) |
| Navmesh `.bai` + `GeoDataMode` (default **true**, `World.json:23**) | `NavigateTo*` tier 1 (:353–368); heightmap clamp in roam (`GetReferenceHeight`, `:1019–1020`); sensor-flag honesty (`BotSurveySenses.cs:151–154`) | straight-line + obstacle-detour fallback when off/unavailable | **e2e-lane `GeoDataMode`/bai presence UNKNOWN** (runtime configs generated; not verified) — if lanes run mesh-off, G1 convergence proof is flat-terrain-only and routed legs are unproven where it matters |
| Merchant packs (`NpcManager.GetGoods`) | Buy leg, seed/earn loops (`ResolveNearestSeedMerchant` :1756–1779 checks `SellsItem(15659)`) | perception-time merchant gate mirrors dispatch gates (`EconomyDecisionScenario.cs:316–332`) | pack-content drift → buy refused; sell leg needs ANY merchant (weaker gate — noted :2123–2127) |
| Heightmap / `MaxStepHeight` | ground clamp, slope-limited Z (:1034–1036) | actor itself interpolates Z linearly (:4195) and trusts destination Z | actor without roam clamp (queue-direct `move` with bad Z) arrives in mid-air/under-terrain; arrival box still satisfiable — **no ground-truth validation on queue legs** |

### 4.3 Nondeterminism sources + where deterministic ordering is test-appropriate

1. **World-query enumeration order (confirmed).**
   `GetNpcByTemplateId` is `_npcs.Values.FirstOrDefault(...)`
   (`WorldInstance.cs:476–478`) — dictionary order, effectively nondeterministic
   across runs/capacities. `GetAllSpawners().SelectMany(...).FirstOrDefault(s =>
   s.UnitId == ...)` (`BotRoamStepExecutor.cs:1334–1336`) same class. `GetAround`
   region-list order (`WorldManager.cs:1196–1240`) undocumented (UNKNOWN, likely
   insertion order).
2. **Perception caps over unordered inputs (confirmed).** Discovery takes the
   first 3 enumerated NPCs (`QuestDecisionScenario.cs:122–133`,
   `MaxDiscoverTargets` :59); nearest-X scans use strict `<` so **ties resolve by
   enumeration order** (merchant :1771–1776, crop :1805–1808, hunt :822–825,
   :941–944, quest-travel bestDist :1285–1297). With >3 NPCs in range or
   equidistant candidates, the *candidate set* varies run to run.
3. **Decision layer itself is deterministic (confirmed).**
   `BotDecisionSelector`: priority desc → clamped personality → `TieBreakKey`
   (ordinal) → index (`BotDecisionProposal.cs:283–287`); quest offerings sorted by
   (level, questId) (:154–156); active quests `Order()` (:105); quest-travel
   quest scan `OrderBy(key)` (:1259); arbiter ties keep registration order
   (`BotGoalArbiter.cs:236–237`). Personality weight is 0 in quest/needs paths.
   **Test-appropriate fix: sort perception inputs by (distance, objId) before
   caps** — pushes determinism to the boundary without touching the engine.
4. **Scheduler timing (confirmed).** Due-time `PriorityQueue` + 4–8 workers
   (`PlayerBotScheduler.cs:47–55`, options :5–11); per-bot lease + pending-wake
   (:32–36); wake latency tracked (:77–82). Single-bot wake sequences are
   reproducible under fake `TimeProvider` (executor :82, queue :266, chatter :64);
   multi-bot interleavings are wall-clock nondeterministic. Stagger offsets are
   deterministic SplitMix32 (`ROADMAP` stagger notes; window 5000 ms default).
5. **RNG (confirmed, mostly benign).** Appearance: seeded `Random` per name
   (FNV-1a stable look, `BotAppearanceFactory.cs:77`, `:112`) ✓; fallback
   `Random.Shared.Next()` when seed missing (:109) — appearance-only. Leisure
   wander angle derives from wall-clock ticks (`BotRoamStepExecutor.cs:1666–1667`)
   — cosmetic. Chatter line selection order UNKNOWN (not read).
6. **Concurrent actors (confirmed shape).** One single-writer lease per actor;
   cross-bot races on shared engine state (last-item buys, auction bids, spawn
   claims) resolve as `Rejected` to the loser — fail-closed, but two-bot E2E
   outcomes (who got the item) are inherently unordered. Tests asserting a
   *specific* winner are order-fragile; assert set-membership or retry.
7. **Timestamps (confirmed).** `Guid.NewGuid` trace ids + `DateTime.UtcNow`
   request/transition stamps (`ActorRequest.cs:106`, `:114`, `:127`, `:136`,
   `:203`) — audit `state_changes` carry wall-clock; evidence comparisons must
   ignore timestamps (the `BotScenarioRunner.Evidence()` deterministic-string
   precedent, :100–101).

---

## Verdict roll-up (gap severity, no redesign)

- **BLOCKER:** interzone movement undefined (§1.5.4); `*ToUnit` snapshot-only,
  no re-track (§1.5.1); internal legs lack queue backstop — scheduler-halted
  wedges actor (§1.5.6); autonomy flags silent-absence (`QUEST_BOOTSTRAP`,
  `NEEDS_FARM`, `PRESENCE_HOME_*` partial, dormancy pair — §4.1 HIGH rows).
- **Major:** unreachable never `Rejected` (budget burn per probe); no mid-leg
  repath/teleport semantics; idempotency ledger memory-only (restart
  re-execution); GOAP mid-plan + homestead cursor unwired (§3.2.2/4);
  `RecordPlannerState` zero production callers; e2e `GeoDataMode`/bai presence
  UNKNOWN (G1 transferability open).
- **Minor:** drive legs lack fast-stuck; `Interrupt` trace-miss silent `false` +
  misleading `Completed("no matching…")`; progress uses 3-D distance vs flat+Z
  arrival; E2E 4-flag forwarding asymmetry is doc-trap only; craft
  consume-vs-grant crash atomicity UNKNOWN; trade/party in-flight durability
  UNKNOWN; chatter RNG order UNKNOWN.
