# PlayerBot scheduler runtime and actor lifecycle audit

Author: Astra. Layer 1 of the requested subsystem audit. Date: 2026-10-07.

**Verdict: STRUCTURAL-ISSUE.** The execution foundation is worth keeping. The weak point is continuity: entering the world, becoming scheduler-owned, surviving a failed step, and replacing an embodiment do not form one reliable lifecycle.

Audit baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` **plus the existing dirty working tree**, including lifecycle and diagnostic changes. This was a read-only source audit; no builds, tests, live runs, or edits. Historical lane results below are ledger evidence, not fresh verification. Saving this report does not change project grades or authorize implementation.

## A Intended Responsibility

This layer should ensure that each designated autonomous player:

- Has one current ordinary `Character` embodiment and one actor.
- Receives an initial wake and an explicit next scheduling disposition.
- Executes short, serialized steps through ordinary gameplay services.
- Terminates requests once, preserves uncertain-effect retry protection, and reports failures.
- Cancels obsolete execution when disconnected, dematerialized, removed, or replaced.

It should provide execution continuity. Quest selection, combat tactics, and resurrection choice belong to their respective gameplay behaviors.

## B Actual Responsibility

Production wiring is:

`PlayerBotScheduler → BotGoalArbiterStepExecutor → BotRoamStepExecutor → GameplayActor → ordinary engine services`

The [production registration](/root/aaemu-dev/AAEmu.Game/Program.cs:375) matters: `GameplayActorStepExecutor` is a separate adapter, not the current production executor.

| Component | Classification | Reason |
|---|---|---|
| `PlayerBotRuntime` / manager registry | KEEP + HARDEN | Ordinary Character composition is correct; lifecycle identity and failure disposition are incomplete. |
| Scheduler due queue, lease, pending wake | KEEP + HARDEN | Useful serialization and wake folding; no incarnation identity or comprehensive teardown. |
| Worker-to-tick marshalling | KEEP | Workers do not execute gameplay. |
| Tick drain / Task-shaped execution contract | REFACTOR | Unbounded drain and synchronous waiting expose the shared tick to bot work. |
| Provisioning → activation → enrollment | REFACTOR | Multiple callers assemble different subsets of the lifecycle. |
| Production actor cache | KEEP + HARDEN | Shared actor access is good; cached state outlives registry membership. |
| `ActorRequest` | KEEP + HARDEN | Terminal finality is sound; dedupe ordering has a reachable defect. |
| `ActorEffectLedger` | KEEP + HARDEN | Useful bounded protection; currently permits a locked outcome to be overwritten. |
| Scheduler death watch | MOVE OWNERSHIP | Uses canonical resurrection, but chooses recovery policy inside infrastructure. |
| Quest-only `BotBehaviorRuntime` | DEFER | Useful local reporting; insufficient evidence to promote it into a universal behavior host. |
| `GameplayActorStepExecutor` | DEFER | Do not mistake this alternate adapter for production ownership or expand it into another runtime. |

## C What Is Good

The scheduler's workers only marshal work; production execution happens inline through TickManager. The [per-bot lease and pending-wake mechanism](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:266) is an appropriate foundation.

The control queue resolves the [same production actor](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs:433), avoiding a second actor for API commands.

[Request terminals are final](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/ActorRequest.cs:310), and `Finish` centralizes ledger recording, audit emission, and active-request release. Those are useful semantics for interrupted MMO actions.

The [shared Character lifecycle](/root/aaemu-dev/AAEmu.Game/Core/Managers/CharacterLifecycleService.cs:105) and explicit region placement in the bot adapter preserve ordinary world participation. This is the right direction for authoritative player simulation.

## D Main Problems

### CRITICAL A busy retry can erase idempotency protection

The reachable sequence is:

1. Request with key `K` completes; its ledger entry is locked against retry.
2. An unrelated request becomes active.
3. A retry of `K` reaches [the busy gate before the dedupe gate](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4865).
4. That rejection is not marked `IsDedupeRejection`.
5. [Finish records it](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4947), and [the ledger overwrites the completed outcome](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/ActorIdempotency.cs:96) with `Rejected`.
6. After the actor becomes idle, another retry of `K` passes the dedupe check.

**Classification: IMPLEMENTATION BUG.** This defeats request-level protection within its own retention window. Whether a particular engine operation duplicates an effect depends on its independent safeguards; no duplicated live effect was demonstrated in this audit.

### HIGH Embodiment and autonomous enrollment are separate caller dependent operations

[HeadlessSession provisioning](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/HeadlessSession.cs:303) activates the Character. The presence coordinator subsequently calls manager Spawn/Activate and Wake. The [quest bridge](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:4470) also performs registration, activation, and wake when explicitly driven.

Consequently, "in world" does not imply "autonomously scheduled." Some paths can activate an already embodied Character again, re-entering the load lifecycle.

Ledger **E-48** records the realistic field test stopping with zero wakes. The [test source](/root/aaemu-dev/AAEmu.IntegrationTests/E2e/BotFieldTestV1Tests.cs:190) uses ordinary network login and deliberately performs no scheduler enrollment. Idle self-rearming cannot produce the first wake.

**Classification: missing enrollment contract / harness setup mismatch; exact live cause remains UNKNOWN.** Ordinary human login should not automatically enroll every player as a bot. A designated network-controlled bot needs an explicit supported enrollment path.

### HIGH A failed step can leave an Active bot permanently unscheduled

After an exception or timeout, the scheduler releases the lease but [only self-schedules after success](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:464), unless a pending external wake exists. The runtime remains Active.

This is intentional enough to have a [NoReschedule test](/root/aaemu-dev/AAEmu.UnitTests/Game/Core/Managers/Bots/PlayerBotSchedulerTests.cs:616).

Avoiding an infinite retry loop is sensible. Leaving an embodied player with no future execution and no explicit fault disposition is not sufficient for persistent autonomy.

**Classification: GAME-DESIGN DECISION needed, with a lifecycle handling gap.** Recovery must reconcile potentially applied effects before retrying.

### HIGH Cached actor identity is not tied to the current embodiment

[BotRoamStepExecutor caches by CharacterId](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:667). Manager removal does not evict that actor or the executor's other per-bot state.

A subsequent registration with the same CharacterId can therefore resolve an actor bound to an earlier Character object. Queued scheduler entries also contain only BotId and due time: they cannot distinguish an old activation from a new one.

An adjacent inconsistency appears in [dormancy materialization](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:211): it loads and restores a Character, but when a retained registry entry exists, manager activation uses the registry's existing Character instead.

**Classification: IMPLEMENTATION BUG / lifecycle identity gap.** The stale-object path is source-reachable; its live consequences need proof.

### HIGH The shared tick has no effective bot execution budget

[DrainTickQueue runs until empty](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:621). The bounded channel feeds an unbounded marshal queue; it does not bound tick execution time.

The scheduler also [synchronously waits on StepAsync](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:407):

- An incomplete Task can block the tick until timeout.
- A synchronous executor that hangs before returning its Task cannot be preempted by `WaitAsync`.
- Timing out the wait does not guarantee that an asynchronous executor has stopped.

**Classification: implementation-contract weakness.** Current synchronous-bodied production execution limits the exposure; this is not evidence of a measured current tick stall.

### MEDIUM ExecutionBoundary checks less than its comments imply

The [boundary assertion](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/ExecutionBoundary.cs:103) accepts an unregistered context and logs violations rather than refusing mutation. The drain repins its own calling thread.

This is useful diagnostics, but zero violations does not prove all activation, teardown, controller, and engine-task mutations were serialized through one boundary.

### MEDIUM Recovery policy lives in the scheduler

[HandleDeathWatch](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:541) automatically chooses delayed portal resurrection and relocation. Canonical resurrection reuse is good. Deciding how death changes combat, travel, and persistent intent belongs to recovery behavior.

## E Hidden Assumptions

- Presence setup supplies registration, fidelity, a patrol route, and the first wake.
- Quest bridge driving supplies enrollment and can repeat activation.
- The field test assumes idle-wake configuration is sufficient after network login; its source does not establish enrollment.
- Manual scan/drain pumps prove scheduler mechanics, not production bootstrap.
- The executor contains [a >500 m teleport detector scoped to quest travel](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:830). Its comment explicitly ties it to harness teleports. This is scenario-derived compensation for an unreported relocation event.
- Step failure assumes a controller/event will wake the bot again.
- Cached execution assumes CharacterId identifies the same live Character indefinitely.

The scheduler and request state machine themselves contain no quest-251 requirement. Quest-specific knowledge enters through the executor and harness.

## F Missing Authoritative Truth

| Missing primitive | Current substitute | Classification |
|---|---|---|
| Designated bot's enrollment and scheduling disposition | In-world state, configuration flags, aggregate step counts | SERVER ALREADY KNOWS / MISSING OBSERVATION |
| Current embodiment identity | CharacterId alone | SERVER ALREADY KNOWS |
| Explicit relocation/instance transition | Quest-only displacement threshold | MISSING OBSERVATION |
| Failed step's recovery owner and next disposition | Log plus no reschedule | GAME-DESIGN DECISION |
| Engine work still running after request interruption | Assumed from request terminal state | SERVER ALREADY KNOWS, exposure must be checked per action |

None requires client reverse engineering.

## G Ownership Problems

Canonical ownership should be:

- **Bot lifecycle coordinator:** enrollment, current embodiment, activation/deactivation reconciliation, initial wake.
- **Scheduler:** when a short step may execute, lease, queueing, work budget.
- **Actor:** request lifecycle and ordinary engine dispatch.
- **Behavior/recovery owner:** response to failure or death; preservation of intent.
- **AAEmu engine:** actual gameplay effects and persistent Character state.

The quest-only behavior runtime does not duplicate `ActorRequest` merely by reporting behavior status. Its `Completed` means a hosted tick landed work, not that a player goal completed. Keep that distinction.

## H Generalization Risk

| Change | Likely failure |
|---|---|
| Different quest or zone | Alternate setup path never enrolls, or fixture relocation bypasses stale-intent cleanup. |
| Different distance / density | More expensive steps consume the shared tick; delayed execution consumes action deadlines. |
| Different combat situation | Death bypasses behavior execution without explicit reconciliation of its live request. |
| Different ownership / inventory | A completed key retried while busy loses dedupe protection; engine safeguards become the remaining defense. |
| Different player level | Scheduler remains general, but progression validity is outside this layer's proof. |
| Dematerialization / reactivation | Old actor, request, route, or queued wake survives into a new embodiment. |
| Server restart | Local lifecycle state disappears; reconstruction correctness is deferred to layer 12. |
| Multiple bots | Aggregate progress can conceal an unscheduled individual; synchronized due work increases tick pressure. |

A useful correction: **E-47 supersedes the earlier travel-starvation interpretation** for the recorded first two chunks. They arrived and advanced; the run ended during later travel. An actor-busy guard during legitimate movement is not itself starvation.

## I ArcheAge Reverse Engineering Questions

**None established for this layer.** Enrollment, scheduling, incarnation identity, timeout handling, and retry protection are server implementation responsibilities. Resurrection gameplay and client handoff questions belong to layer 8.

## J Live Proof Needed

Run one ordinary starter through a **supported designated-bot entry path**, at natural spawn, for 30–60 minutes. After initial enrollment, the harness only observes.

Prove per bot:

- Initial wake occurs without quest/farm/manual wake driving.
- Active, waiting, dormant, and faulted states each have an explicit scheduling disposition.
- Real travel and interaction continue across idle periods.
- A naturally occurring refusal does not silently end execution.
- Death and dematerialization do not leave obsolete requests active.
- Reactivation uses the correct Character object and receives no old-incarnation commands.
- A second bot and a human remain responsive during concurrent activity.

For diagnosis, use focused deterministic tests for the busy-key sequence, stale-incarnation wake, and failure disposition. Those defects do not need a synthetic live micro-gate to establish their mechanics. Live verification must demonstrate continuity and engine consequences.

## K Recommended Changes

1. **Make locked idempotency outcomes non-overwritable.** Check dedupe before busy refusal and protect the ledger itself. Add the completed-K → unrelated-busy → retry-K → idle-retry-K regression.
2. **Establish one enrollment contract.** Separate provisioning, embodiment, and autonomous enrollment; ensure designated bots receive their first wake without reloading an already live Character.
3. **Bind execution to an embodiment generation.** Invalidate old queued work, terminate obsolete requests, and evict or deliberately rebind cached actor state during lifecycle transitions.
4. **Give failed steps an explicit disposition.** Surface failure to the owning behavior/recovery path; reconcile uncertain effects, then deliberately wake, back off, or fault the bot.
5. **Bound tick work and require prompt executor return.** Long gameplay actions remain Running requests polled on later wakes. An incomplete Task must not become permission to wait on the shared tick.

## L Do Not Touch

Preserve ordinary Character composition, shared lifecycle services, region placement, due-time scheduling, leases, earliest pending-wake folding, shared production actor access, terminal finality, and centralized audit emission.

Do not add GOAP, a second scheduler, another universal behavior machine, or a new stall watchdog to repair this layer.

Cross-layer questions:

- **Death:** What engine work survives death, and how is intent reconciled? **Defer to layer 8.**
- **Relocation:** Who invalidates travel after authoritative teleport or instance change? **Defer to layer 6.**
- **Dormancy/restart:** Which state is retained, reconstructed, or discarded? **Defer to layer 12.**

## M Verdict

**STRUCTURAL-ISSUE.** This is a credible execution substrate, but an autonomous player needs more than correct request execution when explicitly driven. The current seams permit an embodied bot to receive no first wake, an Active bot to stop scheduling after one failure, and cached execution to survive its embodiment. The idempotency ordering defect additionally weakens safe recovery. Repair these lifecycle contracts while preserving the existing scheduler and ordinary gameplay paths; this pass provides no reason to introduce another planning system.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0. **Canonical owner:** Lifecycle coordinator for enrollment/generation; scheduler for due work; actor for requests.

**First reviewable outcome:** P0.1: prove the designated autonomous bot reaches the scheduler on ordinary activation and receives its first wake without a Character reload.

| Phase | Work owned by this layer |
|---|---|
| P0 | Repair the proved enrollment handoff; protect idempotency and terminal outcomes; retire old-generation work. Preserve Running ownership with layer 5. |
| P2 | Schedule the intent owner's explicit continue/wait/backoff/fault disposition; scheduling does not choose gameplay goals. |
| P5 | Make dormancy/rematerialization replace execution references consistently. |
| P6 | Measure tick drain and prompt return under actual workloads before tuning budgets or adding machinery. |

**Dependencies:** Per-bot witness in layer 14 starts immediately. Layer 12 shares the generation contract. E-48 is a recorded enrollment stall, not a proved root cause.

**Live stopping point:** Activation, a deliberate failed step and reactivation all lead to attributable autonomous work; no same-effect retry duplication or stale actor use.

**Preserve:** Ordinary Character/lifecycle composition, leases, due-time scheduling, wake folding and terminal audit.

**Defer:** Another scheduler, global behavior machine or threshold-based enrollment rescue.


## Cross layer notebook

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|
| 1 — Scheduler/runtime/lifecycle | STRUCTURAL-ISSUE | Tick-marshalled execution and terminal request lifecycle | Enrollment and embodiment continuity | Lifecycle coordinator; scheduler owns timing; actor owns requests | Current embodiment + explicit next scheduling disposition | Layer 2 — observation |

## Evidence provenance

Archaeology inputs: `list_sources`, `list_tables`, and `search_files(pattern="ActivateHeadless|PlayerBotState.Active|WakeAfter", root="game-source", glob="*PlayerBot*.cs", limit=35)` succeeded; 15 textual matches, no truncation. Source: `/root/aaemu-dev/AAEmu.Game`, catalog version `fork develop`; canonical DB: `1.2 r208022`, 679 tables, MD5 `78b3bdbf038db3b927056106efdf91af`. Source audit commands included `git rev-parse HEAD`, `git status --short`, bounded `rg` searches and `sed`/`cat` reads of the cited paths. Evidence is **code/data**, not live/client/H. Historical E-47/E-48 references resolve to [.omp/state/EVIDENCE.md](/root/aaemu-dev/.omp/state/EVIDENCE.md). No gate or full smoke result is claimed. Behavior acceptance boundary: [archaeology MCP acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
