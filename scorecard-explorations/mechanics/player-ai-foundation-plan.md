# Player AI foundation plan — assignment, retirement and supported arming

Design dossier for the principal's *Player AI Engine — Architecture Foundation & Layered
Intelligence* programme. This is the **DESIGN** output of the agreed workflow
(AUDIT → GRILL → DESIGN → PROTOTYPE → TICKETS → IMPLEMENT → VERIFY). It is not an
implementation, not a capability grade and not a deployment claim.

| Field | Value |
|---|---|
| Parent | [Player AI layer gap audit](player-ai-layer-gap-audit.md) (seven read-only seam audits, five-layer framing) |
| Source baseline | `6a8f590ecda18497e6defcdb511cd3750f90ce45` + uncommitted working tree (170 dirty paths = 144 tracked + 26 untracked) |
| Audit baseline | `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8` (parent of the arbiter slice) |
| Evidence already banked | [E312](../../.omp/state/EVIDENCE.md#e312--the-bot-host-now-stops-converged-one-production-stop-owner-and-the-live-lane-that-found-the-ordering-defect) — Stage A live lane 9/9 legs, 154.9 s, unit suite 4809/0/1 |
| Decision record | [D126](../../.omp/state/DECISIONS.md#d126--the-next-wave-is-the-foundation-and-execution-runtime-stage-a-then-stage-b-not-more-capability) (stage A then B), [D127](../../.omp/state/DECISIONS.md#d127--the-player-ai-foundation-plan-is-approved-t0-is-exactly-the-audits-l1-k-row) (this plan's approval and grill answers) |
| Status | **APPROVED by the principal (2026-10-16, D127).** Implemented one card at a time under the [all-agent local-repair contract](../../PROJECT-CONTROL.md#playerbot-local-repair-contract), a live lane at card exit only; nothing is committed, pushed or deployed (Q1 answer (a): the tree stays uncommitted and every run records the dirty digest). |
| Verdict vocabulary | The six principal labels are aliases of the repository's evidence layers: `VERIFIED LIVE` → live lane, `VERIFIED INTEGRATION` → real-service integration test, `VERIFIED UNIT` → unit/rig, `IMPLEMENTED BUT UNVERIFIED` → wired, `BLOCKED`, `FAILED`. Lower layers are never presented as higher. |

## 0. Outcome and boundary

- **Outcome (one sentence).** An operator can hand a running bot a bounded activity with typed
  parameters and an expiry, the bot's existing goal arbiter honours it through the existing
  action runtime, the outgoing activity is asked to retire so the new one is not blocked, and
  the operator can read back whether it was honoured.
- **Parent programme.** Foundation and execution runtime first (D126): Stage A closed the four
  L1/L2 correctness rows; this plan is Stage B (the control baseline) plus the two standalone
  cards the principal ordered first (security) and last (dead-surface consolidation).
- **Scope kind.** Bot controller / execution runtime / service surface. **No new gameplay.**
- **Bot scope.** One ordinary `Character` per bot, one scheduler, one action lifecycle
  ([supported-capability contract](../../PROJECT-CONTROL.md#playerbot-supported-capability-contract)).
  This plan adds no second brain, no parallel gameplay path and no client.
- **Supported variants (coverage denominator).** One assigned activity (`assigned.travel`),
  one bot, one destination, one expiry, one refusal path, one read-back. Everything else —
  other activities, fleets, rosters, population roaming, cooperation, durability — is
  explicitly unsupported in this wave.
- **Non-goals.** Durable assignment across restart; roster/class/level selection; proximity
  admission; roaming populations; new gameplay capabilities; GOAP changes; personality;
  economy; a formal brain interface; any second planner.
- **Full journey or subloop.** Bounded subloop: operator assignment → election → travel →
  arrival → read-back. Not a self-acquisition loop, not adaptive autonomy.
- **Authority granted by the principal (2026-10-16 grill).** The audit recorded that
  "orchestration has no authorised scope … any fleet/operator-control plan needs new
  approval". The principal supplied exactly the bounded approval: a **fixed, config-declared
  count of bots armed at startup and disarmed at shutdown**, and an **operator assignment
  plane** (HTTP + E2E bridge) with activity name, typed params and expiry. No roster, no
  roaming population, no proximity admission. This dossier is the record of that approval.

## 1. Current architecture map (code references)

Trace the principal asked for, stage by stage, with the state verdict for each stage.
Verdicts: **KEEP** (works, leave it), **HARDEN** (works, thin edge), **CONSOLIDATE** (real but
duplicated or misleading), **DECOUPLE**, **REPLACE**, **UNVERIFIED**.

| # | Stage | Real implementation | Verdict | Evidence / anchor |
|---|---|---|---|---|
| 1 | Live playerbot | Ordinary `Character` + `aaemu_login.users` row (`account_type=HeadlessBot`, `banned=1`), no network client by design; `PlayerBotManager` owns activation/deactivation, `PlayerBotScheduler` owns the wake loop | **HARDEN** | `PlayerBotManager.cs:119-184`; `PlayerBotScheduler.cs:200-286`; Stage A closed teardown convergence (E312) |
| 2 | Perception | The census the decisions actually read is a bounded nearby-NPC/unit scan handed into the leg; the richer stack (snapshot / radar projection / delta / belief / attention / survey) was quarantined and referenced by nothing, and T3 deleted it | **DELETED (T3)** | A2 declaration in the audit L1.2; dead-code inventory items 1a-1h; `PerceptionStackSurfaceTests` removed with the stack it guarded ([E316](../../.omp/state/EVIDENCE.md#e316--the-provably-dead-bot-surface-is-deleted)) |
| 3 | Decision | `BotGoalArbiter` — nine `IBotActivityModule` bands, within-band 25 % commitment margin, name retention, transition counters; `Goap/` (18 `.cs` files after T3; this row's earlier "23" was already stale) reachable **only** inside the homestead leg | **KEEP + HARDEN** | `BotGoalArbiter.cs:76-140, 226-344`; `BotRoamStepExecutor.cs:3684` inside `StepHomesteadLeg` (gated `:1837-1839`) |
| 4 | Intent | **Missing at goal level.** `IBotGoalArbiter` has `Register`/`Arbitrate`/`GetActiveActivity`/`Forget`/counters and **no setter**; "run this objective" exists only in the test-only loopback bridge. Verbs exist (42 action routes) but there is no way to hand the bot a goal | **REPLACE (this plan's T1)** | `BotGoalArbiter.cs:79-100`; audit L2.2 |
| 5 | Dispatch | `BotActionCommandQueue` → `GameplayActor` request state machine; control-API requests are stamped `command_origin="control-api"` and no longer inherit the bot's staged decision vocabulary | **KEEP** | `BotActionCommandQueue.cs:248, 440-560, 899-914`; Stage A A3 (L2-a resolved) |
| 6 | Gameplay execution | `GameplayActor` + real gameplay services: `MoveTo`, `AdvanceQuest`, cast, harvest, interact, use-item … with real per-verb postconditions from engine artifacts | **KEEP** | `GameplayActor.cs` (verb region), 42 routes with postconditions |
| 7 | Authoritative state change | Ordinary world/inventory/quest services and MySQL rows; the lane reads them back independently | **KEEP** | E311 (25.1 m ordinary movement), E312 (position read-back) |
| 8 | Result | `ActorRequest` lifecycle `Requested → Accepted → Running → Completed \| Rejected \| Interrupted \| TimedOut`, bounded audit trace, idempotency ledger, two backstops; **honest payloads** since A3 (a declined advance is `Result=false`) | **HARDEN** | `ActorRequest.cs`; `BotBehaviorRuntime.cs:243-244, 289`; audit L1-i/L1-j resolved |
| 9 | Next decision | Arbiter re-evaluates each wake; **no completion/failure return path** from legs into election except quest sinks, so a failed module re-`CanActivate`s identically | **HARDEN (T4)** — done: a leg's terminal outcome reaches the module that issued it ([E317](../../.omp/state/EVIDENCE.md#e317--a-legs-terminal-outcome-returns-to-the-election-that-chose-it)) | audit L3-b; `QuestBootstrapActivityModule.cs:88-104` |

Cross-cutting: one game-loop execution boundary; per-bot generation stamping; a per-tick step
budget (`PlayerBotSchedulerOptions.MaxStepsPerTick`, A4); the operator plane is **ten routes in
`BotControlController.cs` plus 36 actor routes in `BotActionController.cs`**, each gated by a
per-route `CheckGate` copy (`BotControlController.cs:370-380`, `BotActionController.cs:1343-1353`
— 404 when disabled, 401 on a bad token, fail-closed `CryptographicOperations.FixedTimeEquals`),
while the legacy controllers (`CommandController.cs:12` `"/api/commands/([^/]+)"`,
`CharacterController.cs:11`, `StatusController.cs:15`, Expedition/Mail/World/Auction) have **no
gate at all**; the E2E bridge is a loopback-only TCP listener gated by `Bots.EnableE2EBridge`
with no token (`BotDriveBridge.cs:92-112`). The audit counted 53 gated bot routes; the current
tree enumerates 10 + 36 — the difference is unresolved and does not affect this plan.

## 2. Foundation gap analysis

Rows are the audit's; the disposition column is what this plan does about each. Rows marked
*done* are closed by Stage A and are listed only so the plan's baseline is explicit.

| Row | Gap (short) | Severity | Disposition |
|---|---|---|---|
| L1-a | No presence teardown at all | High | **Done** (A1: `Disarm`, one stop owner, E312) |
| L1-b | Teardown cannot converge | High | **Done** (A1 + review widening of `Converged`) |
| L1-c | Whole seam default-OFF; no supported arming | High | **T2** |
| L1-d | Four independent `Start()` owners, no disarm | Medium | **T2** (reroute all callers through the one owner) |
| L1-e | Re-enrollment duplicates provisioning | Medium | **T2** (stable index-keyed names, reconciled by name) |
| L1-f | Stale arbiter memory across re-enrollment; `Forget` uncalled | Medium | **T5** — done ([E318](../../.omp/state/EVIDENCE.md#e318--one-retire-owner-per-activity)); retention is T6 |
| L1-g | Schedule split-brain: two writers for `SetRoamRoute` | High (architectural) | **T8** — done ([E321](../../.omp/state/EVIDENCE.md#e321--one-writer-for-the-bots-route)) |
| L1-h | Per-bot anchors never produced | Medium | Deferred (schedule phase work; not foundation-blocking) |
| L1-i | `AdvanceQuest` reported a declined step as `Completed`/true | Medium | **Done** (A3) |
| L1-j | Roam cursor treated a non-arrival leg as an arrival | Low-Med | **Done** (A3, corrected mechanism) |
| L1-k | `POST /api/commands/{name}` has no token check; WebApi binds `*:1280` by default | High (security) | **T0** |
| L2-a | No external-command provenance | Medium | **Done** (A3) |
| L2-b | No per-tick step budget | High | **Done** (A4) |
| L2-c | Orphan window: an abandoned leg keeps executing to its own timeout | Medium | **T1** (closed on the assignment path) |
| L2-d | Generation reset discards live work with no terminal audit | Medium | Deferred; T6 records the elected goal across a restart but adds no generation-reset audit (recorded as a T6 limit) |
| L2-e | Dedupe ledger lost on actor recreation | Medium | Deferred (recorded limit) |
| L2-f | A thrown verb publishes no audit row | Low | Deferred (small; fold into T4 if cheap) |
| L2-g | No refund for a craft/harvest cancelled mid-flight | Low-Med | Deferred |
| L2-h | Interrupt scope is narrow (one trace id / one request) | Medium | Partly **T1** (the assignment path preempts by owner); "cancel my commands" stays open |
| L3-a | Seven of nine modules plan-only; no dispatcher-or-deny (on evidence the audit's count collapses to one ownerless name, `fishing.contest`) | High | **T9** — done ([E322](../../.omp/state/EVIDENCE.md#e322--arbitration-cannot-succeed-without-an-action-owner)) |
| L3-b | No completion/failure return path into choice | Medium | **T4** — done ([E317](../../.omp/state/EVIDENCE.md#e317--a-legs-terminal-outcome-returns-to-the-election-that-chose-it)) |
| L4-a | No `Deactivate` in `IBotActivityModule` | High | **T1** (interface + honest default); the retirement remainder is **T5** — done ([E318](../../.omp/state/EVIDENCE.md#e318--one-retire-owner-per-activity)) |
| L4-b | Retention is an unpersisted name string, never cleaned | High | **T6** — done ([E319](../../.omp/state/EVIDENCE.md#e319--interrupted-goal-continuity-and-a-durable-elected-goal)); the active activity is still an in-memory name string, but the goal put in force and the goal it ended (with the reason) are now durable, and `Forget` drops the memory |
| L4-c | Cross-family precedence ignores unfinished work | Medium | **T6** — done ([E319](../../.omp/state/EVIDENCE.md#e319--interrupted-goal-continuity-and-a-durable-elected-goal)); the record names the interrupted owner and hands it the fact on the next read, so the loss is no longer silent — precedence itself is unchanged, and the module still has to answer differently |
| L4-d | Two goal-selection layers with independent commitments | Low | Recorded; homestead-only (no card — see §4 "keep") |

**New gaps the audit did not name** (found while designing this plan, all evidence-backed):

| # | Gap | Evidence |
|---|---|---|
| N1 | **The working tree is shared with a paused author's WIP inside the very files the assignment card must edit.** `BotActionCommandQueue.cs` carries that author's `RecordBackstopTimeout` reorder next to this programme's provenance hunks; `BotRoamStepExecutor.cs`, `PlayerBotManager.cs`, `PlayerBotScheduler.cs`, `GameplayActor.cs`, `ActorRequest.cs`, `IGameplayActor.cs` all carry foreign hunks. A path-scoped commit of the Stage A slice therefore cannot isolate it | `git diff --stat` on those paths (1129 / 379 / 265 changed lines); `git diff BotActionCommandQueue.cs` hunks 34-43 |
| N2 | **No bot-side assignment storage of any kind.** Nothing in the tree can hold "this bot was told to do X until T"; the bridge's enrollment state is lane-only | audit L2.7; `BotDriveBridge.cs` command surface |
| N3 | **The operator cannot distinguish "accepted" from "honoured".** The only decision-side read-back is the arbiter's current activity name | `BotControlController.cs:267-283`; `BotDriveBridge.cs:4287, 4729` |
| N4 | **Module retirement is ad hoc**, one-shot checks scattered in the executor, and the runtime deliberately refuses to touch the actor during a retire | `BotGoalArbiter.cs:299-330`; `BotBehaviorRuntime.cs:532-536` |
| N5 | **An operator instruction could not reach a parked bot** (found by the T1 lane's first run, 2026-10-16): the arbiter only elects when the bot is stepped, and a bot with no work parks itself, so a new assignment sat `Accepted` while the scheduler logged `no due bots … due=1, dormant=1` and the bot's last wake stayed at `02:24:28`. **Closed inside T1** by the registry's `Wake` hook (the existing `IPlayerBotScheduler.Wake` seam) plus the assigned leg's active cadence until the verdict lands | `/root/aaemu-e2e-cmdgate/logs/game.log` (run 1); [E314](../../.omp/state/EVIDENCE.md#e314--an-operator-assignment-is-accepted-elected-walked-retired-and-read-back-on-a-real-server) |
| N6 | **The bridge `assign` op could not name a managed bot** (found by the T1 fold re-run, 2026-10-17): it resolved only the bridge's own persistent sessions and real networked sessions, so the op was unusable for every bot provisioned through the management surface — the very population the operator route addresses. The lane failed with `bridge error: bot 'Assign01' is not in the world (no active networked session)`. **Closed inside T1** by resolving through `IPlayerBotManager` first, with the same id/name rules as `BotControlController.TryResolveBot`, so the two frontends cannot diverge on which bot an instruction names | `BotDriveBridge.cs` (`TryResolveManagedBot` / `TryResolvePersistentBot` / `TryResolveNetworkedBot`); `BotControlController.cs:429-436`; `/root/aaemu-e2e-cmdgate/logs/bot-assignment-lane-report.json` (failed run) |
| N7 | **A fresh lane root fails before the harness can explain itself** (found while provisioning the T2 lane, 2026-10-17): `E2eStack.EnsureUp()` calls `RestoreCanonicalSqlite()` — which needs the `runtime/game-data` reference tree — *before* `EnsureRuntimeLayout()`, the step that throws the actionable "E2E data missing at … — rsync the game .server_files once" message, so a root with no `game-data` tree dies with a raw `DirectoryNotFoundException: Could not find a part of the path '/root/aaemu-e2e-arming/runtime/game-data/Data/compact.sqlite3'` instead of the guidance. A harness ergonomics wart, not a source defect, and not repaired inside T2 (outside the arming scope): the lane root was provisioned by symlinking `runtime/game-data` to a pristine tree, canonical md5 `78b3bdbf038db3b927056106efdf91af` verified. Recorded for a future harness card | `E2eStack.cs:249` (`RestoreCanonicalSqlite()` in `EnsureUp`) vs `:518-523` (`EnsureRuntimeLayout`'s message); `/root/aaemu-e2e-arming/runtime/game-data` |

## 3. Layered architecture proposal

The five layers the principal named, as they will exist after this wave. The rule for every
layer: **a higher layer consumes the lower one and never reimplements it.**

| Layer | What it is after this wave | What changes here |
|---|---|---|
| **L1 Game integration** | Ordinary `Character`, authoritative world state, persistence, the nearby-unit census handed into a leg, existing gameplay APIs. Supported arming: one config-declared, fixed-count startup service over the one scheduler owner; stable index-keyed bot names reconciled by name | T0 (bind/token), T2 (arming) |
| **L2 Execution runtime** | One action contract: intent + params, preconditions, ownership tag, pending/running/succeeded/failed/blocked/cancelled, timeouts, cancellation, failure reason, authoritative result, cleanup. Plus a **goal-level** entry: an assignment (activity + typed params + expiry) the arbiter honours | T1 (assignment, retirement, orphan window), T4 (result return path) |
| **L3 Gameplay capabilities** | Unchanged capability set; what is added is the *contract* that arbitration cannot succeed without an action owner (dispatcher-or-deny) and the assignment's own thin `assigned.travel` carrier | T9 (Stage C) |
| **L4 Decision intelligence** | The existing arbiter stays the single goal owner. One new band sits above every module band for operator assignments; retirement becomes part of the module contract (`Deactivate`), so a new goal cannot sit behind the previous leg's live request | T1, T5, T6 |
| **L5 Advanced intelligence** | Untouched. No personality, economy or social work in this wave | — |

**Seams that must stay single (non-negotiable).**

1. One scheduler per process, one wake loop, one per-tick budget.
2. One action lifecycle (`GameplayActor` request + `BotActionCommandQueue`), one audit trace.
3. One goal owner per bot (`BotGoalArbiter`); the operator band is a *band*, not a second arbiter.
4. One writer for a bot's roam route (T8 closes the current two-writer split).
5. Additive layer first: composition/adapters/extension points before core hooks.

**The assignment's shape (design decision).** An assignment is *not* a new planner and *not* a
second scheduler. It is:

- a small record — `activity name`, typed params, `expiry`, `state`, `last reason` — held in
  memory by the operator-assignment registry (one instance, registered in DI, not durable in
  this wave; the durable path already has a precedent if a later card wants it: additive JSON
  inside an existing `playerbot_metadata` `TEXT` column, the way `BotDailyAnchors.cs:35-38`
  rides `schedule`);
- elected through the **existing** arbiter: a new `IBotActivityModule` registered alongside the
  nine in `Program.cs:366-411`, whose `Priority` sits above the highest existing band (100 =
  `Schedules`), reading the registry in `CanActivate`;
- executed through the **existing** executor: a new `assigned.` dispatch prefix in
  `BotRoamStepExecutor.StepAsync` with its own route-move owner tag, reusing `BotPath` +
  `IssueRouteMove` (`BotRoamStepExecutor.cs:1368-1380`);
- retired through the **new** `IBotActivityModule.Deactivate` contract, with the outgoing
  module's live actor request preempted by owner with a recorded reason (this is the part that
  closes the orphan window on the assignment path);
- driven from the **existing** operator plane: `[WebApiPost("^/api/bots/assign$")]` in
  `BotControlController.cs` behind the same `CheckGate`, plus a `case "assign"` in
  `BotDriveBridge.HandleCommand` (`:247-299`, `{ok,data}`/`{ok,error}` envelope) for lanes;
- read back through the **existing** projection: the assignment's fields join
  `BotBrainSnapshot`/`BotBrainProjection.Capture` (`BotBrainProjection.cs:21, 179-195`) and the
  `GET ^/api/bots/brain/([^/]+)$` route, so the operator surface stays one thing instead of
  two.

**Precedence (principal's decision) and why it is mechanically achievable.** The assignment band
wins absolutely: the operator instruction outranks every module band, including out-of-combat
recovery (85). If the bot dies or the travel fails, the assignment ends with a recorded reason
instead of parking the bot in a recovery loop. Expiry (default 10 minutes) bounds every
assignment, so a forgotten instruction cannot hold a bot forever.

This is not a hope — it follows from how `Arbitrate` scans: `BotGoalArbiter.cs:188-238` walks
`_modules` in descending `Priority` and returns the first band that yields a candidate, so lower
bands are never consulted, and `Decide`'s commitment margin (`PreemptionMarginPercent = 25`,
`PreemptionMargin(weight) = max(1, weight*25/100)`, `:326-334`) only compares **band-mates**.
A new band above 100 therefore decides alone, with no margin arithmetic against `Schedules` (100).

**The one seam that matters, and the fact that shapes the card.** The arbiter's only inputs are
`(bot, gameHour)`, its `_modules` list and its per-bot `_activeActivity` (`BotGoalArbiter.cs:151-178`),
and the decorator consults nothing before it arbitrates — `BotGoalArbiterStepExecutor.StepAsync:46-80`
calls `_arbiter.Arbitrate(...)` at `:54` as its first statement. So an operator override that must
win at the next wake has to be readable by the module itself (the registry injected into the new
module), not staged through the scheduler, the decorator or the executor.

Equally load-bearing: **the arbiter never preempts.** On an activity change it writes
`_activeActivity` (`:297`) and runs the new module's `Activate`; the outgoing leg is released only
by the legs that carry a move-away release path (recovery `:1540-1542`, quest discovery
`:1543-1545`, the decorator's `ReleaseRecovery` at `BotGoalArbiterStepExecutor.cs:60`) or by
`SupersedeQuestTravelRoute` (`BotRoamStepExecutor.cs:3102-3109`). The needs-farm, homestead, hunt
and roam legs are simply **abandoned** — their dispatch stops firing and the live actor request
runs to its own terminal, which can be up to `RoamLegTimeout` (60 s) of the bot still walking the
*old* destination. Consequence for this wave: the assignment path must preempt a live foreign leg
by owner itself (recording the owner tag and the reason) before arming its own route, because
waiting for the abandoned leg would make "assigned travel" observably wrong for up to a minute.
That preemption is bounded to the assignment path, so ordinary gameplay is untouched.

## 4. Reuse plan

**Reuse (no new implementation).**

| Reused | For | Anchor |
|---|---|---|
| `BotGoalArbiter` bands + commitment | electing the operator band and returning to normal election | `BotGoalArbiter.cs:226-344` |
| `IBotActivityModule` + `BotActivityContext` | the assignment module's contract | `IBotActivityModule.cs:24-101` |
| `BotPath` (+ `ArrivalRadiusDefault = 0.5f`, `SkipCurrentWaypoint`) | the assigned travel route | `BotPath.cs:38-149` |
| `BotRoamStepExecutor.SetRoamRoute` + `IssueRouteMove` + owner tags | issuing and owning the assigned leg | `BotRoamStepExecutor.cs:768, 1368-1380` |
| `GameplayActor.MoveTo` + request lifecycle + audit | the actual movement and its honest result | `GameplayActor.cs` verb region |
| `[WebApiPost]`/`[WebApiGet]` + `RouteMapper.DiscoverRoutes<IController>()` + per-route `CheckGate` | the operator plane (assign + read-back) | `WebApiPostAttribute.cs:7-22`; `RouteMapper.cs:39-73`; `WebApiServer.cs:23-27`; `BotControlController.cs:370-380` |
| `BotControlSettings` token gate (fail-closed) | refusing an unauthenticated operator call | `BotControlSettings.cs:16-46, 101-115` |
| `BotDriveBridge` command switch + `{ok,data}`/`{ok,error}` envelope | the lane's control path | `BotDriveBridge.cs:247-299, 1083-1087` |
| `BotBrainProjection`/`BotBrainSnapshot` + `GET ^/api/bots/brain/([^/]+)$` | the operator read-back surface | `BotBrainProjection.cs:21, 179-195`; `BotControlController.cs:246-289` |
| `playerbot_metadata` additive-JSON precedent | a durable assignment later, without a schema change | `BotDailyAnchors.cs:35-38`; `PlayerBotMetadataStore.cs:291-320` |
| `PlayerBotScheduler` (`Start`/`StopAsync`/`Disarm`/`Wake`) | the one owner the arming service drives | `PlayerBotScheduler.cs:129, 200-286` |
| E2E lane stack + the Stage A runner pattern | the card's live proof | `AAEmu.IntegrationTests/E2e/E2eStack.cs`; `artifacts/bot-host-teardown-live/` |

**New (small, additive).**

| New | Why it cannot be reused |
|---|---|
| Operator-assignment registry + assignment record | nothing in the tree holds "told to do X until T" (N2) |
| `IBotActivityModule.Deactivate` (default implementation) | the interface has no teardown member (L4-a) |
| `assigned.*` dispatch branch + `assigned.travel` module | the only goal-level carrier today is a test-only bridge path |
| Assignment fields on the existing brain projection (no new inspection route) | "accepted" and "honoured" are indistinguishable today (N3); `BotBrainSnapshot` has no assignment fields |
| Supported-arming startup service + two config keys | the seam has four independent owners and no supported switch (L1-c/L1-d) |

**Keep unchanged (deliberate).** The nine activity modules' bands; the homestead-only GOAP tree
(L4-d is recorded, not fixed — collapsing two arbiters is a bigger change than this wave
justifies); the quest/farm legs; the chatter engine; formation (inert); the 42 action routes.

**Delete (T3, provably dead — from the read-only inventory).**

| Item | Evidence |
|---|---|
| Quarantined perception stack: `BotPerceptionSnapshot.cs` (436), `BotRadarProjection.cs` (341, its own doc says no decision path references it), `PerceptionDelta.cs` (282), `Belief/BeliefInterpreter.cs` (514), `Belief/AttentionScorer.cs` (233), `Belief/BotBeliefState.cs` (257), `Belief/CurrentPlace.cs` (33), `BotSurveySenses.cs` (325) + `SurveyLegRunner.cs` (131) | only non-test references are inside the stack; `PerceptionStackSurfaceTests` asserts the quarantine; three doc comments name it |
| `Goap/GoapBotStepExecutor.cs` (66) | zero production constructors; only its own inert factory default |
| `BotProgressionCursor.cs` (115) + `PlayerBotMetadataStore.RecordPlannerState` | write-only round-trip; callers are tests only |
| `RoleCombatDecisionTree.cs` (343) | no caller; party roles are Wave 3B and would rebuild it against a real contract |

**Executed (T3, uncommitted in the tree).** All four rows are gone in one change set — 21 files
(12 shipping, 9 test) — and the delete list matched the tree row for row. The only edits the
deletion needed were `PlayerBotMetadataStore.RecordPlannerState` (its callers were tests), the
unread `BotAssignment.MinExpirySeconds`, one test call plus its assertion, and five comments that
named a deleted type. Change-set digest
`60d7023a1ecddabb2d7f165fc30f17f781bb29e1f37b779236fc459108333a19`; whole tree
`74eb7b29b3686d9cc762dbbb071d94b1ae2ee1229d5f0bfdec4f6a111d07ce59` (+11/−5466 lines).
`PerceptionStackSurfaceTests` (3 tests) is **removed, not rewritten**: it asserted that no
shipping type referenced the quarantined stack, and with the stack deleted a reference cannot
compile, so the compiler enforces a strictly stronger invariant than the IL scan did (R5); the
A2 declaration stays the canonical statement. The `planner_state` column keeps its read and its
write path and loses its last writer — `PlannerState` has no producer in the tree now, recorded
rather than deleted. `IBotGoalArbiter.Forget` (`BotGoalArbiter.cs:94`, `:381`) and the four
half-wired items survive untouched, as promised to T5. Evidence:
[E316](../../.omp/state/EVIDENCE.md#e316--the-provably-dead-bot-surface-is-deleted).

**Excluded from deletion, with the reason.** `IBotGoalArbiter.Forget` is *not* dead surface to
delete — the audit prescribes wiring it (L1-f: a re-enrolled bot can be reported `Unchanged`, so
the winning module's `Activate` never re-runs), and **T5 wires it**. The half-wired items
(`BotFormation` getters, the `PopulationDirector` activity dimension, `fishing.contest`,
`PlayerTraceService`) stay in the tree and stay labelled `IMPLEMENTED BUT UNVERIFIED` /
partial; `fishing.contest`'s dispatch gap is closed by T9's dispatcher-or-deny contract rather
than by deletion.

## 5. Minimal vertical slice — assigned travel

One already-supported capability (ordinary walking) through the **real** runtime. No simulator,
no mock executor, no fabricated success.

**Scenario.** An operator assigns bot *A* the activity `assigned.travel` with a destination
~60 m away and a 10-minute expiry.

1. Operator: `POST /api/bots/assign` with
   `{"nameOrId":"BotA","activity":"assigned.travel","params":{"x":…,"y":…,"z":…},"expirySeconds":600}`
   (the bot travels in the body, matching `POST /api/bots/remove$` / `relocate$`). The route is
   token-gated, accepts, and answers with the assignment id/state.
2. The assignment lands in the registry. At the next wake the arbiter elects the operator band;
   `GetActiveActivity` reports `assigned.travel`.
3. The outgoing activity's module is asked to retire (`Deactivate`); a refusal is recorded and
   the assignment does not claim success. The outgoing leg's live actor request is then preempted
   by owner with a recorded reason — necessary because the arbiter never preempts and an
   abandoned leg would otherwise keep walking its old destination for up to its 60 s timeout.
4. The executor's `assigned.` branch arms the route (`BotPath.PathTo(destination)`) with its own
   move-owner tag and issues the route move through the existing verb.
5. The leg's terminal state — not the bot's position — decides arrival: `Completed` **and**
   inside the arrival radius. Arrival completes the assignment (`state=completed`,
   `reason=arrived`).
6. Bounded stuck recovery (the existing one-nudge-then-`Expire`) runs first; if it is exhausted,
   the assignment fails with a recorded reason. No silent retry loop.
7. Expiry (or completion) clears the assignment and the bot returns to normal election.
8. Operator read-back: the assignment's fields appear in the **existing**
   `GET /api/bots/brain/{bot}` snapshot (activity, state, expiry, last reason) — no second
   inspection route.

**What the slice must prove (and at which layer).**

| Claim | Layer |
|---|---|
| The operator route accepts and records an assignment; the token gate refuses without a token | real-service integration test |
| Read-back: the existing `GET /api/bots/brain/{bot}` snapshot reports the assignment's activity, state, expiry and last reason | real-service integration test |
| The arbiter elects the operator band and reports the assigned activity | unit/rig + live lane |
| The bot walks to the destination in the ordinary world and the leg reports `Completed` | live lane |
| MySQL `characters.x/y/z` agrees with the arrival (independent read-back, not the bot's own log) | live lane |
| Retirement ran: the outgoing module's `Deactivate` was called, the preempted leg carries a recorded reason, and the next activity is observable | unit/rig + live lane |
| Refusal path: a module refusing to retire is recorded and the assignment does not claim success | unit/rig |
| Expiry path: a short-expiry assignment clears and normal election resumes | unit/rig |
| A live run with no assignment produces no `assigned.*` activity (nothing is armed by accident) | unit/rig |

**Explicitly not proved by this slice.** Durability across restart; self-acquisition; adaptive
replanning; more than one bot; population behaviour; client presentation; any human verdict (H
stays UNKNOWN); any capability grade.

## 6. Risk assessment

| # | Risk | Mitigation / decision |
|---|---|---|
| R1 | **Shared dirty files (N1).** The assignment card must edit files that carry a paused author's WIP; a path-scoped commit cannot isolate this programme's hunks | **Decided by the principal (§9 Q1, answer (a), D127):** the tree stays uncommitted and every run records the dirty digest, so a result stays attributable to the tree it ran on. Nothing is committed, pushed or deployed. This is an ownership conflict, not a code problem |
| R2 | Immediate preemption can interrupt a quest/farm leg mid-flight | Only an operator can trigger it; the interrupted leg is preempted *by owner* with a recorded reason; the outgoing module is asked to retire first |
| R3 | A default interface member hides "no teardown" for the other eight modules | Verified: **no** activity module has any teardown today (`IBotActivityModule` declares only `Name`/`Priority`/`CanActivate`/`Activate`; a grep for `Deactivate|Stop|Dispose|Clear` in the Bots directory finds only unrelated `PlayerTraceService.Dispose`, `GoapTelemetry.Clear`, `PlanTemplateCache.Clear`). So the default member is the honest shape, not a shortcut: it returns "retired" and is documented as "this module has no teardown"; the eight are recorded as having none, and T5 gives the one-shot retire checks a single owner |
| R4 | New band above 100 could starve ordinary election if an assignment sticks | Expiry is required (default 10 min); completion/expiry/refusal all clear with a reason; the read-back shows the state |
| R5 | Deleting the quarantined perception stack removes a guard test that currently *prevents* re-wiring | The card deletes the stack and rewrites that test's intent into a narrower assertion (or removes it with the reason recorded); the A2 declaration stays the canonical statement. **T3 outcome:** the test was removed, not rewritten — with the stack deleted a re-wiring reference cannot compile, so the build enforces the invariant more strictly than the IL scan did; the reason is recorded in [E316](../../.omp/state/EVIDENCE.md#e316--the-provably-dead-bot-surface-is-deleted) and §4 |
| R6 | The security fix could break existing GM tooling that relies on the token-less route | The token becomes required and the shipped config binds loopback; same-host tooling keeps working, remote tooling must present the token. Recorded as a deliberate breaking change |
| R7 | Lane availability: the default E2E root's ports are held by another deployment | Use an isolated root + port set (the Stage A pattern, `/root/aaemu-e2e-teardown` ports 1437/1439/1450/1460/1434/1480/54357); `E2E_GAME_HOST=127.0.0.1` is required |
| R8 | Scope creep into the decision brain while touching the arbiter | Every ticket below is one gap row; GOAP, personality, economy and cooperation stay untouched |
| R9 | **Residual after T0:** the other legacy controllers stay ungated — `CharacterController.cs:11` (`/api/character/list`), `StatusController.cs:15` (`^/status$`), Expedition/Mail/World/Auction — and the E2E bridge stays token-less (loopback-only, `Bots.EnableE2EBridge`) | **Confirmed as residual by the principal (§9 Q2, answer (a), D127).** Not silently widened into T0: the audit's L1-k row is the GM command route. Recorded here so it is a decision, not an omission; each controller needs its own card before it is gated |
| R10 | **Deployment path unverified.** `Scripts/e2e/docker-compose.yaml` bind-mounts `.server_files/AAEmu.Game/Config.json` over the image config and publishes `1280:1280`; that mounted file is absent in this checkout, so the container's effective `WebApiNetwork.Host` is unknown — a mounted `*` keeps the wildcard, while a loopback bind makes the published port unreachable | Found by the T0 review (2026-10-16); the shipped repo default is loopback and the Docker path was not executed ([E313](../../.omp/state/EVIDENCE.md#e313--the-generic-gm-command-route-is-behind-the-operator-gate-and-the-shipped-webapi-binds-loopback)). No deploy is authorised, so nothing changes now: whoever deploys must reconcile the mount and the published port together (loopback bind and no published port, or a deliberate remote config plus token) and record the effective value |

**Testing budget (principal's decision, binding).** Per card: unit/rig tests + one targeted
real-service integration test; **one live lane at card exit**; on a lane failure exactly one
diagnostic re-run, then stop and report. Deterministic prepared world states over long
campaigns. A card that cannot state its first divergence does not get a third run.

## 7. Implementation tickets (dependency order, one gap row each)

Each ticket becomes one board card filling
[the implementation contract](../../.kanban-templates/implementation.md#bounded-local-repair-brief)
before work starts. Files listed are the expected edit surface, not a commitment.

| # | Card | Gap rows | Concrete outcome | Acceptance layer | Depends on | Stop / escalate |
|---|---|---|---|---|---|---|
| **T0** | Security: close the token-less generic GM route and the shipped bind default | L1-k | `POST /api/commands/{name}` refuses without the operator token; the shipped `Config.json` WebApi binds loopback; the bot plane keeps its token gate | integration (route refuses) + unit (config default) | — | Stop if the fix would require changing GM command semantics |
| **T1** | Assignment + retirement contract (the vertical slice) | L2.2, L4-a, L2-c (assignment path), N2, N3 | Operator assigns `assigned.travel` with params + expiry (`[WebApiPost("^/api/bots/assign$")]` behind the existing `CheckGate`, plus `case "assign"` in the bridge switch); a new `IBotActivityModule` above band 100 (registered per the `Program.cs:384` precedent) reads the registry in `CanActivate`; `Deactivate` is added to the interface with an honest default; the outgoing module is asked to retire and the assignment path preempts any live foreign leg by owner with a reason; arrival is decided by the leg's terminal state; read-back rides the existing `GET /api/bots/brain/{bot}` | unit/rig + integration + **live lane** | T0 | Stop and report if retirement cannot be made explicit without touching the runtime's actor-refusal rule; escalate before editing files carrying foreign WIP (R1) |
| **T2** | Supported arming | L1-c, L1-d, L1-e | Two new explicit config keys arm a fixed count at startup and disarm at shutdown through the one scheduler owner; bot names are index-keyed and reconciled by name (no duplicate provisioning); the four existing `Start()` callers become callers of the one owner | unit/rig + integration + live lane (startup/shutdown with N=2) | T1 | Stop if rerouting a caller would change its existing gate semantics |
| **T3** | Consolidate the provably-dead surface | — (dead-code inventory) | The listed dead types and their tests are deleted; the guard test's intent is preserved narrowly; `Forget` is explicitly kept for T5; `BotAssignmentRegistry.MinExpirySeconds` (unread, found by the T1 review) joins the list | unit suite green + build | T1 | Stop if a "dead" item turns out to have a shipping consumer |
| **T4** | Completion/failure return path from legs to election | L3-b | A leg's terminal outcome (completed/failed/blocked + reason) reaches the electing module, so a failed activity does not re-`CanActivate` identically forever | unit/rig + integration | T1 | Stop if it requires a second scheduler or a new plan store |
| **T5** | Retirement on supersede, wire `Forget`, one retire owner | L1-f, L4-a (remainder) | Every supersede path retires the outgoing module through one owner; arbiter memory is dropped on deactivation, so a re-enrolled bot is not reported `Unchanged` | unit/rig + integration | T1, T4 | Stop if the one-shot retire checks cannot be unified without changing behaviour |
| **T6** | Interrupted-goal continuity + durable elected goal | L1.3, L4-b, L4-c | The elected goal and its interruption reason are recorded (durable), so a lower-band in-progress goal is not silently dropped by a higher-band allow | unit/rig + integration | T5 | Stop if persistence needs a new table without the SQL workflow (schema + base file + persistence proof) |
| **T7** | Operator read-back of decision reason; commanded vs autonomous separation | L2.7 | The operator can see why a bot is doing what it does, and commanded work is distinguishable from autonomous work in the same surface | integration | T1, T4 | Stop if it would duplicate T1's read-back |
| **T8** | One writer for the roam route | L1-g | Schedule and arbiter stop writing the route independently: one writer with a lease/ownership check | unit/rig + integration + live lane | T2, T5 | Stop if the schedule service's phase semantics need re-deciding (that is product scope) |
| **T9** | Dispatcher-or-deny contract (Stage C) | L3-a | Arbitration cannot succeed without an action owner: startup validates each module against a real dispatch owner, or the module denies | unit/rig (startup validation) | T4 | Stop if validating requires implementing a missing capability (that is a capability card) |
| **T10** | One capability proved end-to-end beyond roam (Stage C) | — (capability) | One named capability with earned acquisition (no seeding) | live lane | T9 | Separate scope decision by the principal |

Order rationale: security first (principal), then the architectural element (assignment +
retirement), then the deployment concern (arming), then the dead-surface cleanup, then the
audit's B2-B6 in dependency order, then Stage C.

## 8. Verification matrix

| Ticket | Unit/rig (VERIFIED UNIT) | Integration (VERIFIED INTEGRATION) | Live lane (VERIFIED LIVE) | Human/client (H) |
|---|---|---|---|---|
| T0 | config default assertion | route refuses without token, accepts with it | — | UNKNOWN |
| T1 | election above every band, arrival decided by terminal state, refusal recorded, expiry clears, no accidental arming | assignment route round-trip + read-back | assigned travel arrives; MySQL position read-back; retirement + next activity observable | UNKNOWN |
| T2 | key parsing, name reconciliation, no duplicate provisioning | startup arms N, shutdown disarms N | N=2 armed at boot, disarmed at stop, restart shows no leftovers | UNKNOWN |
| T3 | **MET**: build 0 errors and the suite green after deletion — 4794 total, 0 failed, the one named MySQL skip, exactly the 88-test drop the inventory predicted from T2's 4882; the guard test is removed and the compiler now enforces its invariant | — | — (a deletion; nothing observable in a running world changes) | — |
| T4 | **MET**: a leg's terminal outcome reaches the module that issued it — published exactly once from the one place every wake passes (the slot setter for a leg issued and dropped inside one wake, plus the two wake-top slots with a per-slot reported flag), the issuing activity travelling on the fact and the arbiter refusing a leg whose activity the bot has left; 14/14 targeted, bot area 735/735, gate 4808 total / 0 failed / the one named MySQL skip | **MET at the rig layer only** (not a live-server run): a real executor, a real arbiter and a real actor whose route leg really times out, where the module that owned the leg is handed its own failure and answers differently | — | UNKNOWN |
| T5 | **MET**: every supersede path retires through one owner — the duplicate per-wake recovery release is gone and the executor's own read of the election is that state's single owner, with the rest of the retirement inventory classified by trigger; `Forget` now has a production caller (the single retirement owner notifies on success) and drops both the active activity and any pending leg outcome; 5 new tests, bot area 740/740, gate 4813 total / 0 failed / the one named MySQL skip | **MET at the rig layer only** (not a live-server run): a real executor, a real arbiter and a real module — a bot is elected, the next wake reports `Unchanged`, native retirement runs through the real seam, and the election after it `Activate`s again instead of reporting `Unchanged` | — | UNKNOWN |
| T6 | **MET**: every election that changes the outcome is recorded durably in the existing `playerbot_metadata.planner_state` column — the goal put in force, the goal it ended and the reason — written only on an outcome change (a supersede, a same-owner rename, a no-candidate drop) and never on a steady-state wake, with a fresh arbiter reading the row once per bot and handing the record to the interrupted owner at most once per read; 13 new tests plus 1 store test, bot area 754/754, gate 4827 total / 0 failed / the one named MySQL skip | **MET at the rig layer only** (not a live-server run): the write-through lands in a real MySQL row byte-equal to `ToJson()`, a cold store reloads it, and a fresh arbiter wired exactly like `Program.cs` hands the reloaded record to the module that lost the goal — the isolated container was deleted afterwards, and no permanent restart test exists | — | UNKNOWN |
| T7 | **MET**: every wake records one `BotWakeDecision` per bot (the arbitration's outcome, the elected activity, the refusal reason when something refused, the candidate/refusal counts) and the existing brain read-back reports it together with the commanded/autonomous split, derived from the arbitration's own elected activity and the live assignment; 13 unit/rig tests plus 4 real-route tests, bot area 771/771, gate 4844 total / 0 failed / the one named MySQL skip | **MET at the rig layer only** (not a live-server run): the real `GET /api/bots/brain/{bot}` route over a real DI graph (real manager, scheduler, executor, arbiter, decorator and assignment registry) returns the arbiter's refusal reason and the split for a bot the real decorator just stepped | — | UNKNOWN |
| T8 | **MET**: the bot's route has one writer per moment — both schedule paths ask one rule (`BotRouteOwnership.OwnsRoute`) whose authority is the arbiter's live elected activity, so the tick-driven `BotScheduleService` defers a phase change while elected work owns the route (no route write, no phase-memory advance, no persisted `lastPhase`) and re-resolves it every scan until the work ends, while the arbiter's own `SchedulePhaseActivityModule` declines for the same reason; the idle fallback and the schedule's own `schedule.*` phases are not owners; a deferral whose window closes first is dropped, not queued; 9 new tests, bot area 780/780, gate 4853 total / 0 failed / the one named MySQL skip | **MET at the rig layer only** (not a live-server run): a real tick-driven service, a real arbiter and a real `RoamRouteScheduleBehavior` over a real `BotRoamStepExecutor` — the elected route object is still the installed one after the deferred tick, and is replaced only once the work ends | — (no live lane: the card is a writer contract and the schedule service's phase semantics are unchanged) | UNKNOWN |
| T9 | **MET**: one declaration (`BotActivityDispatchSurface`) says what part of the bot performs an elected activity and both sides read it — the executor's six election gates and the quest runtime's wake-ownership check resolve through it instead of testing their own literals, and the arbiter asks `IsCarried` before it may report `Activated`, so an activity nothing performs is refused with a reason naming the missing owner, counted (`OwnerlessRefusalCount`) and left out of the band so the next band runs; every registered module declares what it can elect (`IBotActivityModule.DeclaredActivities`) and a `[ModuleInitializer]` bootstrap validates the real module set against the real surface at startup, logging one line per gap; the audit's "seven of nine modules plan-only" collapses to one ownerless name (`fishing.contest`), which the stop condition forbids building here; 15 new tests (4 surface, 6 validation, 5 arbiter), bot area 795/795, gate 4868 total / 0 failed / the one named MySQL skip | — (the card's declared layer is unit/rig startup validation, which runs over the real DI container and the real module set, not a live server) | — (a module electing work nothing performs is refused; every activity a dispatch branch already carries is unchanged) | UNKNOWN |
| T10 | — | — | named capability with earned acquisition | UNKNOWN |

Rules that bind every row: a passing unit test is not integration evidence; a rig is not a live
lane; a live bot run is not a human verdict; seeded prerequisites are not self-acquisition; and
no ticket promotes a capability grade.

## 9. Authority, limits and the open decisions

**Authorised by the principal (2026-10-16).** The bounded arming capability and the operator
assignment plane described in §0; local implementation of the cards in §7 under the
[all-agent local-repair contract](../../PROJECT-CONTROL.md#playerbot-local-repair-contract);
one card at a time; a live lane at card exit only.

**Not authorised / not claimed.** Push, deploy, live campaigns, grade or milestone promotion,
human (H) acceptance, roster/class selection, roaming populations, proximity admission,
cooperation, durable assignments, and any change to ordinary gameplay.

**Decision Q1 (N1 / R1) — ANSWERED (a) by the principal, 2026-10-16.** The working tree carries a
paused author's WIP inside the files this programme must edit, and the Stage A slice's authorised
path-scoped commit cannot isolate its own hunks from that WIP. The choices were (a) leave
everything uncommitted and record the dirty digest per run (current state), (b) have the paused
WIP committed or stashed first by its owner, or (c) authorise this programme to commit the shared
files as they stand (not recommended: it would publish another author's unfinished work as this
programme's). **Chosen: (a).** Every run records the dirty digest, so a result stays attributable
to the tree it ran on; nothing is committed, pushed or deployed.

**Decision Q2 (T0 scope) — ANSWERED (a) by the principal, 2026-10-16.** Verified: the bot plane's
routes are token-gated, but `CommandController.cs:12` (`/api/commands/([^/]+)` — the GM verb route
the audit's L1-k row names) plus `CharacterController.cs:11`, `StatusController.cs:15`,
Expedition/Mail/World/Auction carry no gate at all, and the E2E bridge is loopback-only with no
token. The choices were (a) T0 fixes exactly the audit row — the token-less GM command route and
the shipped loopback bind default — with the rest recorded as residual R9 (recommended: one card,
one gap row), or (b) T0 widens to gate every ungated legacy controller and the bridge, a bigger
security card with more blast radius on existing tooling. **Chosen: (a).** R9 therefore stands as a
recorded residual, not an omission.

**Next action.** T0 is accepted
([E313](../../.omp/state/EVIDENCE.md#e313--the-generic-gm-command-route-is-behind-the-operator-gate-and-the-shipped-webapi-binds-loopback);
its independent review's P1 defect — three existing E2E lanes posted to the command route with
no token — was repaired by arming those lanes and the re-run is green). **T1 is accepted**
([E314](../../.omp/state/EVIDENCE.md#e314--an-operator-assignment-is-accepted-elected-walked-retired-and-read-back-on-a-real-server)):
the operator assignment plane, the band above every ordinary band, the retirement contract and
the brain read-back are unit/rig-proved and the isolated live lane walked a real bot to the
assigned destination. The lane's first run found and closed gap N5 (a parked bot never woke for
an operator instruction); the independent review (`T1AssignmentReview`, read-only) returned 2 P1
+ 3 P3 defects plus one note, all repaired and pinned by tests — the destination is armed as a
chunked route, a leg ended by another owner suspends the assignment instead of failing it, arming
marks the record `Active`, the operator read-back shares the lazy expiry, the bridge `assign` op
requires numeric coordinates, and retirement clears the assignment ownership — and the fold's
first lane run found and closed gap N6 (the bridge op could not name a managed bot). The fold's
exit lane is green (1/1, 103 s, ten legs: arrival 0.18 m, MySQL row 0.03 m from the live
position, 12.58 m from the start, a 90 m assignment walked 97.7 m in two chunks, ordinary
`presence.roam` resumed). **T2 (supported arming) is accepted**
([E315](../../.omp/state/EVIDENCE.md#e315--supported-arming-one-owner-for-start-and-stop-two-keys-and-a-boot-roster-that-adopts-instead-of-duplicating)):
one arming owner for the four start callers, two explicit keys that ship off, and a boot
roster that adopts by name instead of duplicating rows. Proof: unit/rig, the Tier 1 gate green
(4882 total, 0 failed, the one named MySQL-backed skip) and an isolated live lane 1/1 (262.8 s,
six legs — boot armed, two real managed accounts walking, a restart that re-enrolled the same
two characters with no duplicate row, a converging token-gated stop, and a keys-absent control
that enrolled nothing). The independent review (`T2ArmingReview`, read-only) returned 2 P2 + 3
P3 defects and four test gaps: four of the five defects are repaired and pinned (the boot line
now states what the enrollment actually did, a shutdown racing the enrollment is reported
apart from a refusal, one compensating disarm pass closes the detached-boot-task window, and
the duplicate-row warning names the right rows) and one was dismissed as T0's accepted change.
**T3 (dead-surface consolidation) is accepted**
([E316](../../.omp/state/EVIDENCE.md#e316--the-provably-dead-bot-surface-is-deleted)): the
21-file deletion matched the inventory row for row, the shipping assembly and the whole suite
rebuilt green (4794 total, 0 failed, the one named MySQL skip — exactly the 88-test drop the
inventory predicted), the guard test is removed with the reason recorded (the compiler now
enforces its invariant), `Forget` and the four half-wired items survive for T5, and the
independent review (`T3DeadSurfaceReview`, read-only) returned no defects — its one
observation, an over-claiming comment about the 25 m combat band, is repaired.
**T4 (completion/failure return path from legs to election) is accepted**
([E317](../../.omp/state/EVIDENCE.md#e317--a-legs-terminal-outcome-returns-to-the-election-that-chose-it)):
one `BotLegOutcome` fact per leg, published exactly once from the one place every wake passes
(the leg slot's setter for a leg issued and dropped inside one wake, plus the two wake-top
slots with a per-slot reported flag), stamped with the activity that issued the leg, refused by
the arbiter when the bot has already left that activity, and handed once to the owning module's
`CanActivate` through `BotActivityContext.LastLegOutcome`. Proof: unit/rig (14/14 targeted, the
bot area 735/735) and the Tier 1 gate green (4808 total, 0 failed, the one named MySQL skip);
the plan's §8 integration criterion is met **at the rig layer only** — a real executor, a real
arbiter and a real actor whose route leg really times out, where the module that owned the leg
answers differently on the next election. No shipping module reads the outcome yet: the first
production consumers are T7 (operator read-back of the decision reason) and T9
(dispatcher-or-deny), which the plan already declares as depending on T4. Three read-only
reviews ran on the same tree (`T4LegOutcomeReview` → one P1 + one P3; `T4FixVerify` → one P2;
`T4ChokePointReview` → one P2, then a verification pass returning no defects); all four findings
are repaired and pinned, including a duplicate report through the previous-leg slot that the
first fix introduced and the review caught.
**T5 (retirement on supersede, wire `Forget`, one retire owner) is accepted**
([E318](../../.omp/state/EVIDENCE.md#e318--one-retire-owner-per-activity)): the executor's single
retirement owner now notifies on success through a new `RetiredBotSink` (production wires
`arbiter.Forget`), so no election memory outlives the bot and a re-enrolled bot is elected afresh
instead of being reported `Unchanged`; the decorator's per-wake recovery release was a second owner
for the same state in the same wake and is deleted, leaving the executor's own read of the election
(`BotRoamStepExecutor.cs:2127`, "recovery superseded or unsafe") as that state's one owner, with
every other released state classified by trigger in the card's inventory (AssignedTravel through its
own `Deactivate`; schedules by route/generation/retirement; Homestead sidecars by generation, by
design; quest discovery and travel route through the executor's own primitives). Proof: unit/rig
(5 new tests — 4 retirement-owner plus 1 supersede case — the bot area 740/740, and a red-capable
proof: restoring the deleted duplicate fails the new test with
`recovery activity released or superseded` where the single owner says
`recovery superseded or unsafe`) and the Tier 1 gate green (4813 total, 0 failed, the one named
MySQL skip). The plan's §8 integration criterion is met **at the rig layer only**: a real executor,
a real arbiter and a real module, where native retirement drops the memory and the next election
`Activate`s instead of reporting `Unchanged`. The independent review (`T5RetirementReview`,
read-only) returned no defects across the card's seven criteria, and its one wording note is folded.
Recorded, not repaired: `ReleaseRecovery` now has no production caller, and the supersede detail
string changed (diagnostics only, no consumer reads it).
Next: **T6 (interrupted-goal continuity + durable elected goal)**, which carries the plan's own stop
condition ("stop if persistence needs a new table without the SQL workflow (schema + base file +
persistence proof)").

T6 in the tree, uncommitted: the elected goal and the reason it ended survive the process. Every election
that changes the outcome writes a `BotGoalContinuity` record — the goal put in force, the goal it ended
and the reason — into the existing `playerbot_metadata.planner_state` column
(`SQL/aaemu_game.sql:686`; no table, no column and no migration, so the stop condition above is not
triggered), and a steady-state wake writes nothing. The arbiter reaches the store through two delegate
seams wired in `AAEmu.Game/Program.cs` in the T4/T5 sink style, so it stays DI-free and testable, and the
write happens after the module's `Activate` succeeded, so a rolled-back election is not recorded as a
goal. A fresh arbiter reads the row once per bot (never per wake) and hands the record to the module named
as the interrupted owner at most once per read — a fact, not an instruction: nothing auto-resumes, and the
module decides. `Forget` drops the in-memory copy and leaves the row, so a re-enrolled bot re-reads it and
is told once more. Evidence
[E319](../../.omp/state/EVIDENCE.md#e319--interrupted-goal-continuity-and-a-durable-elected-goal): unit/rig
13 new tests plus 1 store test, the bot area 754/754, the Tier 1 gate green (4827 total, 0 failed, the one
named MySQL skip) plus both MCP smokes, and a real-MySQL write/reload run on an isolated container (the
row byte-equal to `ToJson()`, a cold store reload, and a fresh arbiter handoff) that was deleted
afterwards — so the plan's §8 integration criterion is met at the rig layer only. The independent review
(`T6Review`, read-only) raised 4 findings: the P1 (a rolled-back election was recorded before `Activate`)
and two P2s are fixed and re-verified, and the fourth (best-effort delivery) is recorded as a limit rather
than repaired. Recorded, not repaired: audit L2-d (a generation reset still has no terminal audit), the
defensive unregistered-owner branch (unreachable through the public surface), and the absence of a
permanent restart test. Card `player-ai-interrupted-goal-continuity` is **Accepted** on `E319`, and its
work record `work-981abe5d94714972b770ea1c5011a10f` is closed.
Next: **T7 (operator read-back of the decision reason; commanded vs autonomous separation)**, which
carries audit L2.7 and depends on T1 and T4.

T7 in the tree, uncommitted: the operator can read why a bot chose what it chose and whether the work in
force was commanded or the bot's own. Every wake records one `BotWakeDecision` per bot — the arbitration's
outcome, the elected activity, the refusal reason when something refused, and the candidate/refusal counts —
in the decorator that is the arbiter's only caller, and the existing `GET /api/bots/brain/{bot}` read-back
reports those facts plus `WorkOrigin` (`commanded` when a live assignment's activity is the one the
arbitration put in force, `autonomous` otherwise, null when nothing is in force). The split classifies the
arbitration's own elected activity rather than the runtime-first `Activity` field, which is why the snapshot
also carries `DecisionActivity`: the review's P1 finding was that the quest runtime's last-ticked leg
(`CurrentActivity` is written only while a quest leg ticks and is never cleared) decided the side, so
commanded work read `autonomous`. No new route, no new token, no arbitration or precedence change, and T1's
assignment read-back is untouched — the stop condition ("stop if it would duplicate T1's read-back") was not
triggered. Evidence
[E320](../../.omp/state/EVIDENCE.md#e320--the-operator-can-read-the-decision-reason-and-the-side-it-came-from):
13 unit/rig tests plus 4 real-route tests, the bot area 771/771, the Tier 1 gate green (4844 total, 0 failed,
the one named MySQL skip) plus both MCP smokes, and the read-back payloads captured from the route tests. The
independent review (`T7Review`, read-only) raised a P1, a P2 and two P3s — all four fixed and re-verified, with
red proofs for the P1 fix (reverting the origin to the pre-fix line fails exactly the two origin tests) and for
the wiring (passing no decision fails exactly the two route tests that assert it) — plus one P3 note about the
per-bot memory having no removal path, which is recorded rather than repaired. Its coverage note is now pinned
by a stale-runtime regression test, checked by a third read-only pass (`T7FinalTestCheck`). Recorded, not
repaired: the read-back is per-process memory (a restart reports no decision until the next wake), the refusal
reason does not reach the action-plane actor audit for commanded work, whether an out-of-repo dashboard
consumes the new fields is unverified, and `Activity` keeps its runtime-first precedence so it can legitimately
differ from `DecisionActivity`. The plan's §2 table has no L2.7 row to close (the audit states this gap as
Stage-B bullet B5), so the disposition is recorded here. Card `player-ai-operator-decision-readback` is
**Accepted** on `E320`, and its work record `work-c4bd0a7268734f77a5154088aac1d698` is closed.
T8 in the tree, uncommitted: the bot's route has one writer per moment. The tick-driven
`BotScheduleService` no longer overwrites a route an elected activity armed — it asks
`IBotGoalArbiter.GetRouteOwner` (the arbiter's live single-active activity, filtered so the terminal
idle fallback and the schedule's own `schedule.*` phases are not owners) and, while that is non-null,
defers the phase transition: no route write, no phase-memory advance, no persisted `lastPhase`, so
every scan re-resolves the same transition and it lands as soon as the work ends. A deferral is not
queued: if the pending phase's window closes first, the retry resolves back to the phase the bot is
already in and the steady-state branch drops it with a log line. The arbiter's own
`SchedulePhaseActivityModule` declines on the same rule, so both schedule paths read one fact;
`Program.cs` registers the explicit `Lazy<IBotGoalArbiter>` wrapper that keeps the service → arbiter
edge lazy and cycle-free, and an unregistered wrapper degrades to the pre-T8 unconditional path
instead of failing boot. No schedule phase semantics, no arbitration band/weight/precedence change,
no second route store and no new HTTP route — the stop condition ("stop if the schedule service's
phase semantics need re-deciding") was not triggered. Evidence
[E321](../../.omp/state/EVIDENCE.md#e321--one-writer-for-the-bots-route): 9 new tests (7 service plus
2 arbiter), the bot area 780/780, the Tier 1 gate green (4853 total, 0 failed, the one named MySQL
skip) plus both MCP smokes, three red proofs (removing the ownership read fails exactly the two
deferral tests; disabling the module's deny fails exactly the one arbiter test; replacing the drop
with a read fails exactly the new drop test), and the independent review (`T8Review`, read-only) with
verdict correct, no P1/P2, and one folded P3 — the comments promised that a deferred transition lands
when the work ends while a closed-window deferral was dropped silently, now documented, logged and
pinned by a test. Recorded, not repaired: `_deferrals` has the same lifetime as the pre-existing
`_phaseByBot` (no deactivation cleanup), the deferral counter and `SnapshotDeferrals()` have no
production consumer yet (no HTTP route surfaces the schedule service, as with `SnapshotPhases()`), the
operator/GM plane and provisioning remain deliberate second writers, and a dropped deferral means the
bot does not go home that night (it is not wedged — the resolver re-asserts the base phase at the next
boundary). Card `player-ai-roam-route-single-writer` is **Accepted** on `E321`, and its work record
`work-8a580973f99d4f98bd3d26d74117670a` is closed.
T9 in the tree, uncommitted: arbitration cannot succeed without an action owner. One declaration
(`BotActivityDispatchSurface`) says what part of the bot performs an elected activity — the four
leg prefixes, the two recovery names, the operator assignment, the schedule phase route, the presence
patrol and the idle fallback — and both sides read it: the executor's six election gates now resolve
through it instead of testing their own literals (the quest-leg runtime's wake-ownership check does
too), and the arbiter asks `IsCarried` before it may report `Activated`, so a module electing work
nothing performs is refused with a reason that names the missing owner, counted
(`OwnerlessRefusalCount`) and left out of the band, which lets the next band run. Every registered
module declares what it can elect (`IBotActivityModule.DeclaredActivities`, a default interface member
like the existing `Deactivate`), and a `[ModuleInitializer]` bootstrap validates the real module set
against the real surface at startup and logs one line per gap. The audit's "seven of nine modules
plan-only" collapses on evidence to ONE ownerless name (`fishing.contest`: the module allows it, the
executor has no branch for it, and the rig that would drive casting was never wired) — the plan's stop
condition ("stop if validating requires implementing a missing capability") was not triggered, so the
capability was NOT built: the arbiter refuses the allowance and the startup report names the gap.
Evidence [E322](../../.omp/state/EVIDENCE.md#e322--arbitration-cannot-succeed-without-an-action-owner):
15 new tests (4 surface, 6 validation, 5 arbiter), the bot area 795/795 (was 780), the Tier 1 gate
green (4868 total / 0 failed / the one named MySQL skip) plus both MCP smokes, and four red proofs
(disabling the arbiter's refusal fails exactly the three ownerless tests; deleting the `needs.` rule
from the surface, and separately deleting the executor's own needs dispatch branch, each fail the same
35 of 90 needs-family tests under the identical filter — the first is the evidence that the executor's
gate really reads the shared table, the second that a removed branch is caught at the rig layer even
though the startup validation cannot see it; making the surface carry `fishing.contest` fails 2 of 6
validation tests). The independent review
(`T9Review`, read-only, 23m43s) found no P0/P1/P2 and three P3s: D1, a second literal `quest.`
convention in the quest runtime's wake-ownership check — fixed by resolving through the surface (a
behaviour-preserving consolidation, pinned by the existing quest and runtime tests); D2, the startup
validation cannot catch a *removed dispatch branch* (the table is hand-kept, so deleting the needs leg
while its rule stays behind is not reported) — accepted with the mitigation named and documented in
the code: that direction is covered by each family's rig tests over the real executor (red proof d),
so the half of acceptance (2) the startup validation cannot see is delivered at the rig layer, and a
startup-side dispatch-coverage proof remains a candidate follow-up; D3, a sibling-wave semantic
(`!recoveryElected` in the quest-discovery retirement, unpinned by any test) — recorded, not repaired,
because T9 only redefined the `recoveryElected` expression and `Resolve` maps exactly the two recovery
names to `Recovery`, so T9 does not change it. Recorded, not repaired: `recovery.consume` is carried
but no module elects it; `DeclaredActivities` is a hand-kept declaration (the validation checks
declarations against the surface, not the reverse); the counter and the bootstrap's report are
in-process only (the operator sees the refusal through the existing decision read-back's `WhyNot`); the
startup bootstrap also runs inside the test process when a test installs a container, where it only
logs; and the operator/GM plane remains a deliberate second writer. Card
`player-ai-dispatcher-or-deny` is **Accepted** on `E322`, and its work record
`work-8837ca238d1f477c94c697f5f576c626` is closed.
The leg-outcome channel T4 built now has two production consumers in the tree, uncommitted (the
user-requested card `player-ai-quest-farm-failure-reports`, audit row L3-b):
`QuestBootstrapActivityModule` counts *hard refusals* (`ActorLifecycleState.Rejected`) of its own leg
against the quest it has chosen and, at `LegFailureGiveUpThreshold` (3) identical (generation, quest,
action, detail) refusals, gives up on that quest — it reports the give-up through the existing deferral
owner (`NotifyQuestGiveUp`, reason `GIVE-UP/LEG-FAILED: <detail>`) *before* releasing the chosen
purpose (the owner's first precondition reads that purpose; the reversed order the first independent
review found had made the call dead code, so the give-up silently degraded to the generation-scoped
block alone — fixed, and red proof e measures it), keeps the id only for quests that owner cannot defer,
and denies with `gave up on quest <id> after <n> failed legs (<detail>)` so `PickQuest` blocks it and
the band moves on; `NeedsFarmActivityModule` counts its own refusals and, at `LegFailureThreshold` (2),
stands aside for `LegFailureStandAsideWakes` (20) elections with `farm leg failed 2 times the same way
(<detail>) — standing aside for 20 elections` as the wake's first refusal, so roam really takes over
instead of the same farm wake repeating (bounded by elections, not by a clock, and not a session
disable). Timeouts are deliberately NOT counted: that class already has owners (the executor's unstick
nudge and `ReleaseFailedKnownFoodApproach` for the farm, the quest runtime's own ten-wake budget for
quests), and counting them broke the pre-existing designed test
`RepeatedStuckQuestTravel_RecoversOnceWithoutCompletingQuest` (red proof d). Evidence: four new rig
tests over the real modules, the real arbiter and the real executor (68 handoff / 653 quest family /
74 farm / 795 bot-area, 0 failed); six red proofs; the Tier 1 gate green (`./scripts/gate.sh`, run
`bg_299` on the frozen tree, wall 230.28 s: Release build 0 errors, 12 NU1903 package advisories, script
compiler 0 errors, unit tests 4872 total / 0 failed / 1 skipped — the named live-rig test that needs
`AAEMU_LIVE_RIG=1` — and both smokes green, which is four more tests than the previous card, exactly the
four new rig tests); and two independent
read-only reviews, neither of which built, ran or edited anything. The
first (`LegFailureReview`) returned needs changes: P1 - the give-up's `NotifyQuestGiveUp` call was dead
code because the purpose was released first, so no deferral was installed and the generation-scoped block
silently became the only mechanism; P2 - the give-up test could not tell the deferral from that block.
Both were fixed, and red proofs (e) and (f) measure them. The second (`LegFixReview3`, after the reviewer
agent's model failed twice at the transport level with no result) returned sound, no P0/P1, and its one
test-gap P2 - nothing pinned that an accepted quest is not also generation-blocked - was closed by an
added assertion (red proof f). Open and recorded: `Forget` has no production caller, so the deferrals are
retained for the process lifetime; a leg fact can be consumed on a wake that denies before the policy reads
it (under-count only); no `try/finally` around the notify. Card `player-ai-quest-farm-failure-reports` is **Accepted** on `E323`, and its work
record `work-2a12eb4cea6f43fe88fb32c2d8d92fdb` is closed.
Next: **T10 (one capability proved end-to-end beyond roam, Stage C)**, a separate scope decision by the
principal.
