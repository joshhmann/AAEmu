# PlayerBot final architecture synthesis

Author: Astra. Layer 15. Date: 2026-10-07.

**Verdict: STRUCTURAL-ISSUE.** AAEmu has useful execution machinery, narrow live seams and a bounded planning foundation. It does not yet establish continuous autonomous player simulation. The decisive gap is agreement between observed opportunities, chosen intent, asynchronous execution and actual gameplay outcomes.

All 14 subsystem reports exist before this synthesis. Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. This is a source audit and recommendation, not a fresh live verdict, milestone closure or authorization to implement. Existing work and historical evidence remain intact.

## PlayerBot architecture

| Responsibility | Current architecture | Convergence and canonical owner |
|---|---|---|
| World understanding | Several bounded observation/projection surfaces with incompatible coverage and eligibility; production consumes only part of the machinery. | One gameplay observation adapter exposes caller-relative opportunities and unknown/absent distinctions. Belief interprets evidence, never invents rights. |
| Goal selection | Deterministic fixed-priority activity election followed by competing inner branch decisions. | One strategic owner holds a useful intent and its disposition. Modules propose opportunities; reactive safety may interrupt. |
| Planning | Bounded GOAP and a symbolic material/homestead graph; concrete targets and requirements are incompletely bound. | Keep bounded prerequisite planning where it solves a real dependency chain. Bind source, quantities and destination before execution. |
| Reactive behaviors | Combat/survival/travel/loot brains exist, alongside direct loops and historical rules. | Each capability has one bounded executor. Combat responds to actual aggression; survival suspends rather than silently replaces intent. |
| Execution | Shared GameplayActor is a strong seam, but timing, outcome meaning and normal skill parity differ by verb. | One request lifecycle over ordinary engine services, with exact request identity, asynchronous completion and authoritative results. |
| Recovery | Wake/stall thresholds, loot-once records and synthetic recovery can hide distinct causes. | Classify refusal, waiting, contention, unknown, unreachable and exhausted opportunities. Re-observe, recover or choose another goal without losing purpose. |
| Persistence | Ordinary gameplay is saved; metadata stores configuration/JSON. Intent consumption and embodiment replacement are incomplete. | Lifecycle owns current Character generation. A small durable intent is reconciled against loaded gameplay; requests and stale object IDs are rebuilt. |
| Shared-world behavior | Operational fidelity/density controls and real Team paths; independent intent and contention remain weak. | Engine owns rights and transactions; each player owns intent; population services own server cost. Retire actor memory without affecting other citizens. |

These are ownership corrections around ordinary Character gameplay, not a request to replace the whole stack. Evidence is in [observation](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-02-observation-2026-10-07.md), [arbitration](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-03-arbitration-2026-10-07.md), [execution](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-05-quest-execution-2026-10-07.md), [planning/acquisition](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-11-acquisition-economy-2026-10-07.md), [persistence](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-12-persistence-2026-10-07.md) and [shared world](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-13-shared-world-2026-10-07.md).

## 1. What architecture do we really have?

A server-hosted collection of deterministic gameplay controllers, fixture-derived quest interpretation, authored scenario execution and operational population infrastructure. Production runs Scheduler → GoalArbiter → BotRoamStepExecutor → GameplayActor → engine services. It has more than a quest script, but the selected activity does not consistently own the running task or persistent purpose. Separate scenario paths exercise engine seams without establishing this production chain's autonomy. See [lifecycle](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-01-lifecycle-2026-10-07.md), [decision ownership](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-03-arbitration-2026-10-07.md) and [harness evidence](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-14-harness-2026-10-07.md).

## 2. What architecture should we converge toward?

A modest persistent player agent: observe available opportunities → retain/select one useful goal → bind any necessary prerequisites → execute through normal gameplay → observe results → recover or reconsider → preserve high-level intent.

Keep tactical decisions deterministic and reactive. Use GOAP for bounded acquisition chains when the prerequisites and sources are actually known. Broader utility tuning is secondary to making the existing strategic owner's decision govern execution.

## 3. Which current systems already belong there?

KEEP or KEEP + HARDEN: ordinary Character/lifecycle composition, tick-marshalled actor requests, legality-first deterministic proposals, server-derived quest facts, actual objective counters, normal auto-attack/skill/loot/transaction services, owner-scoped journeys, real crop phases, bounded resource-aware search, observed plan completion, additive metadata and operational fidelity control. The explicit fixture-override gate is useful. Preserve narrow live traces and first-failure diagnostics. Each report's Do Not Touch section identifies the boundary.

## 4. Which abstractions are premature?

DEFER promotion of disconnected perception stacks, planner-state storage without a production consumer, population fairness contracts without actual progress collection, and unsupported activity labels as player capabilities. Broad economy/homestead action expansion is premature while destinations, material identities and quantities are unbound.

COLLAPSE duplicate scenario interpretation, combat loops and interaction semantics incrementally into production owners. The bounded planner itself is appropriate; another general planner is unnecessary. See [perception adoption](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-02-observation-2026-10-07.md), [combat](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-07-combat-2026-10-07.md), [GOAP](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-11-acquisition-economy-2026-10-07.md) and [population contracts](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-13-shared-world-2026-10-07.md).

## 5. Which missing server observations are forcing hacks?

| Missing primitive | Existing compensation | Classification |
|---|---|---|
| Observation coverage and actual spawn availability | Coordinate hints and unresolved-target wake streaks | SERVER ALREADY KNOWS much of runtime spawning; MISSING OBSERVATION of availability/coverage |
| Per-entry caller loot eligibility and recipient receipt | Account/pin ownership guesses, entry-count deltas and loot-once state | SERVER ALREADY KNOWS distribution state; eligibility query/enforcement needs review |
| Actual attackers and skill readiness | Selected-target/nearby-hostile proxies, separate cast clocks | SERVER ALREADY KNOWS combat state; MISSING OBSERVATION at the bot seam |
| Route availability and action-specific reach | Direct movement after no path, detail-string repath inference | MISSING OBSERVATION; geometry sufficiency remains UNKNOWN |
| Current-phase interaction legality and delayed completion | Direct Doodad phase driving, forced cast completion | SERVER ALREADY KNOWS much of legality/timing; IMPLEMENTATION BUG in parity |
| Exact acquisition requirements and bound source | Boolean materials, fixed coordinates/recipe and supplied stock | SERVER ALREADY KNOWS templates/counts/transactions; MISSING OBSERVATION/binding |
| Current embodiment and resumable intent | Cached actors, fresh default contexts and storage-only JSON | IMPLEMENTATION BUG / MISSING OBSERVATION; intent policy is GAME-DESIGN DECISION |

These gaps explain the repeated pattern of timers and watchdogs around unresolved truth. Keep budgets as operational bounds; do not treat them as proof of why an opportunity failed. See [quest sources](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-04-quest-interpretation-2026-10-07.md), [failure memory](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-05-quest-execution-2026-10-07.md), [navigation](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-06-travel-2026-10-07.md), [loot](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-08-death-loot-2026-10-07.md), [interaction](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-09-interactions-2026-10-07.md) and [danger/recovery](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-10-survival-2026-10-07.md).

## 6. Which client behaviors genuinely require reverse engineering?

No unconditional client-RE blocker was established. Three bounded questions remain conditional:

- Navigation: if a required collision surface is demonstrably absent from server geometry, identify the r208022 collision/navigation asset and ingestion requirement.
- NPC interaction: if packet/server review cannot determine the client's default action and quest-talk selection, inspect interaction Lua and normal interaction/talk packet traces.
- Retail loot parity: if full normal engine-path review leaves party/tag/public eligibility ambiguous, compare controlled normal-client party/unpartied traces and fix canonical enforcement and observation together.

Server inventory, quest counters, spawn state, skill/GCD timing, crop phase, labor and metadata do not need client RE merely because the bot does not observe them. The individual questions, targets and consequences are recorded in [travel](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-06-travel-2026-10-07.md), [loot](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-08-death-loot-2026-10-07.md) and [interactions](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-09-interactions-2026-10-07.md). Human feel is UNKNOWN until actual human evaluation.

## 7. What prevents a bot from living autonomously for 30–60 minutes?

Several concrete continuity failures can stop or invalidate such a run:

- Enrollment and failed-step rescheduling do not form a reliable lifecycle; embodiment replacement can retain stale objects.
- Running movement is interpreted as failed/non-owned work at the behavior boundary, allowing competing fallback work.
- A missing opportunity can become counted give-up rather than discovery, waiting, alternate acquisition or a new useful goal.
- GCD bypass, manually driven harvest and additional regeneration can produce success unlike ordinary player gameplay.
- Corpse entry removal is confused with caller acquisition; transient refusals/capacity can become permanent skip memory.
- Acquisition plans can lack real sources/requirements; restart loses intent; other bots can trigger whole-store memory eviction.

This is a source-supported risk assessment, not a measured 30-minute failure rate. The existing lanes may bypass these conditions through setup and manual driving.

## 8. What are the highest-leverage five changes?

1. **Make one intent own continuous execution.** Connect election, asynchronous Running ownership, explicit next scheduling disposition, interruption/resumption and Character generation. Persist only the high-level intent needed to reconcile after restart.
2. **Expose authoritative opportunities.** Start with spawn/coverage, caller loot eligibility/receipt, actual aggression and phase-relative interaction readiness. Carry unknown and explicit refusal causes rather than inferring them from wake counts.
3. **Restore normal gameplay parity and one capability path.** Converge casts on normal GCD, harvest/interactions on asynchronous skill timing/costs, and recovery on normal consumable/regen services. Reuse those capabilities for quest, party, farm and scenarios.
4. **Bind selected work to exact targets, requirements and outcomes.** Keep all outstanding quest objectives and viable sources; use exact item quantities/recipes, action reach and request identity. Reconsider temporary contention/capacity; retire memory per actor/generation instead of clearing the population.
5. **Establish an unassisted field proof.** Correlate per-bot wakes/requests and disclose interventions. Observe production bots from normal starter state for 30–60 minutes with actual acquisition, failure recovery, competition and a separate restart extension.

Each is a direction to split into reviewable outcomes, not one large implementation card. Initial structural fixes should be verified before the full field acceptance run.

## 9. What should we stop building?

Stop adding quest-specific watchdogs where the cause is unknown, more fixed commodity/recipe actions without discovered bindings, new perception/planning stacks without production adoption, and scenario gameplay interpreters that compete with production. Stop counting stocked surplus as self-acquisition, direct-spawn targets as discovered ecology, or manual wake-driven stages as proof of ordinary continuous cadence.

Retain diagnostic micro-gates and historical evidence for the exact seams they exercise. They remain useful regressions.

## 10. What should the next realistic field tests be?

Use observation-only after START; disclose all initial conditions.

1. **Primitive independent citizen:** normal starter Character, ordinary funds/inventory, an unfamiliar town. Discover an activity, travel, acquire inputs/rewards, recover from danger and continue for 30–60 minutes. Do not supply target IDs or known locations to the brain.
2. **Contested opportunities:** several bots and one human share normal mobs/crops. Include unrelated and party ownership, full-bag stack capacity and a target disappearing before arrival. Verify exact recipients and continued useful work.
3. **Continuity extension:** interrupt self-selected activity through dormancy and process restart, alter the opportunity while offline, then observe intent reconciliation without rearming or duplicated spend.
4. **Player parity checks:** normal client and bot use the same supported cast, crop interaction and recovery item. Compare timing, interruption, GCD, labor, inventory and actual effects.

Keep the first-failure format: last authoritative good state, first missing/incorrect primitive, bot decision/request identity, intervention ledger and gameplay DB outcome. Separate contract/rig, authenticated live and human evidence. The proposed Activity-Spine campaign can supply candidate legs, but this audit does not ratify it or replace the current dispatch objective.

## Rework direction P0 to P6

The [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) adds delivery direction to every subsystem report. Layer 15 owns reconciliation of the supported scope and evidence, not another gameplay decision path.

| Phase | Direction |
|---|---|
| P0 | Correct gameplay and reliable autonomous entry. |
| P1 | Authoritative observation and actionable opportunities. |
| P2 | One strategic intent owner and explicit execution dispositions. |
| P3 | Shared travel, combat, loot, interaction and recovery capabilities. |
| P4 | Real acquisition and progression prerequisite chains. |
| P5 | Durable intent, dormancy/restart and ordinary shared-world contention. |
| P6 | Unattended 30–60 minute field proof and measured expansion. |

After each named outcome, reconcile only the affected layer evidence and the cumulative notebook. Do not regenerate the whole audit or promote phases/milestones from subloop success. Recheck current source and existing ledger before turning any work package into a bounded implementation card. Implementation, verification, deployment and H acceptance remain distinct.


## Cross-layer notebook

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|
| 1 — Scheduler/runtime/lifecycle | STRUCTURAL-ISSUE | Tick-marshalled execution and terminal request lifecycle | Enrollment and embodiment continuity | Lifecycle coordinator; scheduler owns timing; actor owns requests | Current embodiment + explicit next scheduling disposition | Layer 2 — observation |
| 2 — Observation/perception/world-state | STRUCTURAL-ISSUE | Bounded engine reads and pure deterministic projections | Incompatible eligibility and absence semantics | Common observer adapter over ordinary gameplay; belief interprets | Caller-relative opportunities + valid coverage/resource availability | Layer 3 — arbitration; rights/gates deferred to 8–9 |
| 3 — Goal arbitration/top-level ownership | STRUCTURAL-ISSUE | Deterministic legality-first selection on the shared execution path | Elected activity does not own execution or persistent intent | One strategic owner; modules supply goals; executors own bounded tasks | Opportunity readiness + intent/execution disposition | Layer 4 — quest interpretation; route/recovery/planning deferred to 6/10/11 |
| 4 — Quest interpretation | HEALTHY-BUT-NARROW | Server-derived objective facts and explicit unsupported vocabulary | Single-objective/single-source reduction | Quest projection over server quest components | All outstanding objectives and viable acquisition alternatives | Layer 5 — execution |
| 5 — Quest execution | STRUCTURAL-ISSUE | Shared actor dispatch and live objective counters | Async ownership and retry state conflict | Per-intent quest behavior; engine owns progress | Terminal outcome disposition and real blocked cause | Layer 6 — travel |
| 6 — Travel/navigation | STRUCTURAL-ISSUE | Owner-scoped journeys and shared movement requests | Unknown navigation falls back to unvalidated straight movement | Travel intent + one actor mover; engine geometry adapter | Route availability and action-specific reachability | Layer 7 — combat |
| 7 — Combat | NEEDS-HARDENING | Normal skills/auto-attack engine and deterministic tactics | GCD bypass plus competing combat paths | One bounded combat executor over normal Skill/Character | Actual aggression and authoritative skill readiness | Layer 8 — death/loot |
| 8 — Death/corpse/loot | STRUCTURAL-ISSUE | Shared resurrection and loot-container execution | Container changes confused with caller acquisition | Engine loot distribution; recovery behavior owns death intent | Per-entry caller eligibility and actual receipt | Layer 9 — interactions |
| 9 — Interaction primitives | STRUCTURAL-ISSUE | Ordinary skill/item/Doodad paths and post-state checks | Three Doodad paths with incompatible timing/cost semantics | Engine skill/interaction capability adapter | Async completion and exact action eligibility | Layer 10 — survival |
| 10 — Survival/recovery/needs | STRUCTURAL-ISSUE | Explicit reactive veto and ordinary vitals | Selected target mistaken for danger; parallel regeneration | Reactive safety + normal recovery executor | Actual attackers, safe escape and recovery resources | Layer 11 — acquisition/economy |
| 11. Acquisition / economy | STRUCTURAL-ISSUE | Bounded search and observed plan completion | Coarse prerequisites and unbound destinations | Goal owner; bound plans over normal economy | Discoverable sources, quantities and legal opportunities | 12. Persistence |
| 12. Persistence | STRUCTURAL-ISSUE | Ordinary Character persistence and metadata seam | Intent is not restored; reload can be discarded | Lifecycle coordinator plus durable intent adapter | Current embodiment generation and resumable intent | 13. Shared world |
| 13. Shared world | STRUCTURAL-ISSUE | Normal Team paths and operational fidelity limits | Cross-bot state eviction and unconditional party takeover | Engine rights; player goal owner; population operations | Actual activity, human group membership and contention | 14. Harness |
| 14. Harness | STRUCTURAL-ISSUE | Live executor traces and explicit rig disclosure | Scenario success can supply missing autonomy | Production runtime for behavior; harness for setup/observation | What the bot discovered versus what the lane supplied | 15. Synthesis |

## Evidence boundary

The layer reports contain concrete source citations and archaeology provenance. Canonical read-only DB md5: `78b3bdbf038db3b927056106efdf91af`, target 1.2 r208022. No new builds, gates, tests, live/client runs or human acceptance were performed. Positive source findings were corroborated by local reads; empty archaeology searches were recorded as empty rather than promoted to evidence. [Archaeology acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
