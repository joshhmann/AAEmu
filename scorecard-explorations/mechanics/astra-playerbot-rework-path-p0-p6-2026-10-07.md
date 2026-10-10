# PlayerBot rework path P0 to P6

Author: Astra. Date: 2026-10-07. Status: audit-derived recommended direction; implementation and acceptance pending.

The destination is a primitive ArcheAge player that can acquire useful means, act legally, recover, retain purpose and continue for 30–60 minutes. Start by protecting gameplay and making autonomous entry reliable. Establish truthful observation and one intent owner before expanding quest, economy or homestead breadth.

**P0–P6 are ordered delivery phases in this rework path.** They are not severity scores, evidence grades, new milestones, or the historical Phase 0–6 numbers in the target-architecture document. A layer can receive a small P0 correction and deeper work later. Every phase needs live evidence for its claimed gameplay scope; live testing does not wait until P6.

This extends the [15-layer audit](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-audit-index-2026-10-07.md) and [synthesis](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-15-synthesis-2026-10-07.md). Source baseline remains `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Recommendations reuse the cited audit findings; no new implementation or live verdict is asserted.

Use the [Muse handoff](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-muse-handoff-2026-10-07.md) as the operating brief for carrying out this path. It links the direction to specific evidence and separates the first review assignment from later implementation/live-proof tasks.

## Phase order

| Phase | Player-visible result | Main layers |
|---|---|---|
| P0 | A designated bot starts autonomously; requests and effects remain correct. | 1, 3, 5, 7, 9, 10, 12, 14 |
| P1 | Two production consumers agree about what is known, legal and available. | 2, 4, 6, 7, 8, 9, 10, 11 |
| P2 | The elected purpose owns work, waiting, interruption and reconsideration. | 3, 5, 11, 13; interfaces with 1/12 |
| P3 | Quest, party and nonquest work use the same travel, combat, interaction and recovery semantics. | 5, 6, 7, 8, 9, 10 |
| P4 | A selected useful goal can obtain its prerequisites and complete a supported chain. | 4, 5, 11; recovery means from 10 |
| P5 | Purpose survives restart; each citizen competes and cooperates without corrupting others. | 1, 6, 8, 12, 13 |
| P6 | A bounded primitive player lives in the ordinary world for 30–60 minutes. | All, through 14 |

Dependencies are outcome-specific, not a demand to finish whole subsystems. P0 request integrity and Running handling precede changes that introduce normal cast waits. P1 opportunities feed P2 selection. P3 capabilities enable P4 acquisition. P5 requires a single-bot activity worth resuming or sharing. P6 integrates and challenges the supported scope. A newly discovered P0 correctness defect interrupts later work.

## Ownership and existing project direction

Preserve the [target architecture's invariants](/root/aaemu-dev/PLAYERBOT_TARGET_ARCHITECTURE_CONSOLIDATED_V1.md): one scheduler, one ActorRequest lifecycle, terminal finality, ordinary gameplay services, bounded tick work and no duplicated rules. Interrupted requests are abandoned. Retaining a player goal does not pause/resume its old request; continuation uses a new request and rechecks effects that may already have landed.

[PROJECT-CONTROL's ownership checkpoint](/root/aaemu-dev/PROJECT-CONTROL.md#playerbot-ownership-checkpoint--2026-09-16) places strategy at the retained GOAP boundary and treats arbiter/modules/BotRoam as transitional adapters. P2 follows that existing ownership direction: integrate **one** goal/intent owner using the retained primary-intent concept, with module offers and bounded capability executors. The GOAP search solves useful prerequisite chains; not every reactive decision requires planning. Do not leave a strategic arbiter and a strategic homestead runner active in parallel.

This path organizes the requested audit rework. It does not silently replace the [current dispatch record](/root/aaemu-dev/STATUS.md:4), [canonical task state](/root/aaemu-dev/.omp/state/CURRENT.md), standing breadth restrictions or milestone exits. Actual implementation slices reconcile to those records and use the [existing implementation contract](/root/aaemu-dev/.kanban-templates/implementation.md). No code, deployment, external dispatch or signoff is authorized by this document.

## Reconcile existing work before replacement

The latest ledger is relevant to the first steps:

- [E-42](/root/aaemu-dev/.omp/state/EVIDENCE.md:1680) records that the reporter-spawner walk arm already exists. Review that arm and its caller/request ownership before proposing another one.
- [E-46/E-47](/root/aaemu-dev/.omp/state/EVIDENCE.md:1692) record healthy chunk turnover and travel progress; E-47 supersedes earlier interpretations of that lane. Those observations do not close generalized navigation, but they bar repeating the disproved chunk-death premise.
- [E-48](/root/aaemu-dev/.omp/state/EVIDENCE.md:1696) records a field interval with zero scheduler enrollment. The exact enrollment cause remains unproved; the adjacent dormant-source SQL error is not established as the cause.

These are existing ledger records, not newly rerun evidence. Preserve the current staged retention work and quarantine disposition. Before each code slice, reconcile its relevant dirty files, test reds and source/data identity. A baseline defect needs ownership and evidence; it is never classified green.

## P0 Correct gameplay and reliable entry

**Outcome:** A designated bot starts autonomously; requests and effects remain correct.

- P0.1 Autonomous enrollment: distinguish provisioned, embodied, registered and due; repair the proved handoff so first and subsequent wakes need no bridge driving.
- P0.2 Request integrity: preserve dedupe before busy rejection, terminal finality, and current embodiment generation.
- P0.3 Gameplay parity: separate bounded slices for normal GCD with executor wait/retry, async harvest/interactions, and ordinary consumable/regen recovery.
- P0.4 Running ownership: preserve pending work at the behavior/runtime boundary; consume the exact terminal later.
- P0.5 Evidence integrity: per-bot wake/request correlation, valid unknown-distance handling and intervention disclosure.

**Evidence required to advance:** One ordinary designated bot enrolls and continues after a failed step; a long request yields without ambient takeover; completed effects cannot repeat after a busy retry; changed skill/harvest/recovery families show normal engine timing/costs. Missing parity evidence stays explicitly unproved.

## P1 Authoritative observation

**Outcome:** Two production consumers agree about what is known, legal and available.

- P1.1 Capture identity/sequence/time/coverage and unknown semantics, including known-mask prerequisite evaluation.
- P1.2 Resource sites: distinguish configured hints, current materialization/population, unavailable and unknown; add actual aggression evidence.
- P1.3 Caller loot eligibility, exact recipient receipts and stack-aware capacity.
- P1.4 Current-phase interaction and skill/resource readiness from normal engine semantics.
- P1.5 Migrate one quest and one nonquest consumer to corrected shared facts; retain dispatch revalidation.

**Evidence required to advance:** Live quest and nonquest consumers select from corrected facts; an unseen resource is not marked exhausted, another party's receipt is not caller acquisition, and a friendly selected target is not an attacker. Queries remain bounded. Diagnose any unsupported engine query before guessing.

## P2 Single strategic intent owner

**Outcome:** The elected purpose owns work, waiting, interruption and reconsideration.

- P2.1 Make candidate evaluation pure and route schedule/quest/needs offers to the documented strategic boundary.
- P2.2 Keep one inspectable high-level intent with selected opportunity, goal disposition and current request reference.
- P2.3 Feed waiting/blocked/completed/abandoned outcomes back to that owner; suppress unrelated ambient dispatch while work owns the request.
- P2.4 Let reactive danger and explicit party policy interrupt, retaining the purpose and reconstructing execution with a new request.

**Evidence required to advance:** A real delayed activity remains owned; danger interrupts it and the purpose continues after safety; a blocked goal yields to useful alternate work without disappearing. Schedule/quest/farm do not independently replace each other's routes. Deterministic priorities suffice.

## P3 Reusable player capabilities

**Outcome:** Quest, party and nonquest work use the same travel, combat, interaction and recovery semantics.

- P3.1 Travel: validated route availability, exact request retirement and action-specific arrival.
- P3.2 Combat: one bounded executor with engine readiness/refusals and attributable outcomes.
- P3.3 Loot/death: per-entry disposition, real receipt, revisitable temporary refusal and recovery-owned resurrection policy.
- P3.4 Interaction: phase-relative binding, async postconditions and one NPC talk mapping.
- P3.5 Survival/recovery: actual threats, bounded safe escape and usable normal recovery resources.

**Evidence required to advance:** An ordinary bot traverses a realistic obstructed route, fights, obtains its actual reward, uses a legal interaction and recovers. Include a disappearing target, cast interruption and temporary capacity/resource failure. Reuse the same capability from at least two goal sources before extracting shared orchestration.

## P4 Acquisition and progression dependencies

**Outcome:** A selected useful goal can obtain its prerequisites and complete a supported chain.

- P4.1 Quest projection: all outstanding requirements and acquisition alternatives; unsupported conjunctions remain explicit.
- P4.2 Bound planning: exact item quantities/recipes/resources plus the same merchant/site/workbench binding for cost, travel, dispatch and completion.
- P4.3 Bootstrap and repeat: discover a normal source, acquire inputs, perform the activity, replenish and choose subsequent work; missing means lead to supported alternative acquisition or explicit abandonment.

**Evidence required to advance:** One ordinary character self-acquires a supported chain's inputs, completes it and repeats without supplied stock/locations. Exercise a different supported quest objective and another town. Canonical design/level/tax eligibility is followed where relevant; unsupported homestead prerequisites keep that larger loop incomplete.

## P5 Durable intent and shared-world correctness

**Outcome:** Purpose survives restart; each citizen competes and cooperates without corrupting others.

- P5.1 Versioned intent persistence and production consumption; rehydrate one Character generation and re-observe opportunities.
- P5.2 Distinguish missing metadata from failed reads; reconcile partial/late effects and safe dormancy/save transitions.
- P5.3 Per-actor/generation retirement replaces whole-population eviction.
- P5.4 Party policy and contention use normal Team/resource rights; operational activity metrics reflect the canonical goal and measured progress.

**Evidence required to advance:** The proved single-bot activity resumes after dormancy and process restart with no rearming or duplicate spend; an offline target change is handled. Then several bots share natural resources with correct recipients, stable personal intent and real Team decisions. Storage-byte equality or a wake storm alone cannot close this phase.

## P6 Unattended life and controlled expansion

**Outcome:** A bounded primitive player lives in the ordinary world for 30–60 minutes.

- P6.1 Observation-only production run after START with per-bot first-failure and intervention evidence.
- P6.2 Held-out supported zone/quest/inventory variants plus separate restart/competition extensions.
- P6.3 Reconcile coverage and measure per-bot progress, tick work, query cost and pressure before adding breadth or scaling machinery.

**Evidence required to advance:** No manual wakes, teleports, stocks, forced progression or fallback spawns during the accepted interval. Show discovered useful work, legal travel, acquisition, danger/recovery, continued intent and repeat. Report exact failed/unproved legs; client presentation and H remain separate.

## Direction for every layer

Each subsystem report now has a dedicated **N. Rework direction P0 to P6** section: first outcome, owner, ordered work, dependencies, stopping point, parts to preserve and deferred breadth. The table below is the dispatch map into those sections. It does not turn all phases into Ready cards.

| Layer | Start / main rework | First reviewable outcome | Report |
|---|---|---|---|
| 1. Scheduler / runtime / lifecycle | P0 | P0.1: prove the designated autonomous bot reaches the scheduler on ordinary activation and receives its first wake without a Character reload. | [Layer 1](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-01-lifecycle-2026-10-07.md) |
| 2. Observation / perception | P1 | P1.1: make one capture distinguish valid coverage, absent-in-coverage and unresolved facts; correct current-mask prerequisite evaluation with layer 11. | [Layer 2](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-02-observation-2026-10-07.md) |
| 3. Goal arbitration | P0 / P2 | P0.3 recovery slice: remove gameplay mutation from eligibility/activation; actual recovery dispatch uses the existing actor. | [Layer 3](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-03-arbitration-2026-10-07.md) |
| 4. Quest interpretation | P1 / P4 | P4.1: expose all unfinished requirements for one already supported compound objective; refuse partial support explicitly. | [Layer 4](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-04-quest-interpretation-2026-10-07.md) |
| 5. Quest execution | P0 | P0.4: a selected Running move remains owned pending work and cannot fall through as a failed/unselected action. | [Layer 5](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-05-quest-execution-2026-10-07.md) |
| 6. Travel / navigation | P0 / P3 | P0.4/P3.1: carry the exact in-flight movement request through the runtime and classify its actual terminal without log-string inference. | [Layer 6](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-06-travel-2026-10-07.md) |
| 7. Combat | P0 / P3 | P0.3a: normal learned-skill GCD enforcement and executor wait/retry must change together; a bare bypass flag flip is incomplete. | [Layer 7](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-07-combat-2026-10-07.md) |
| 8. Death / corpse / loot | P1 / P3 | P1.3: distinguish an entry disappearing from the caller actually receiving its item; return a receipt aligned with the engine transaction. | [Layer 8](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-08-death-loot-2026-10-07.md) |
| 9. Interactions | P0 / P3 | P0.3b: migrate one mature-crop harvest onto its real asynchronous skill path and verify labor, interruption and completion. | [Layer 9](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-09-interactions-2026-10-07.md) |
| 10. Survival / recovery | P0 / P3 | P0.3c: remove synthetic heal/manual regen application; normal Character regen and normal usable items remain authoritative. | [Layer 10](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-10-survival-2026-10-07.md) |
| 11. Acquisition / economy / GOAP | P1 / P4 | P1.1/P4.2: make one plan use known requirements and one concrete source binding consistently in prediction, travel and observed completion. | [Layer 11](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-11-acquisition-economy-2026-10-07.md) |
| 12. Persistence / dormancy | P0 / P5 | P0.2: ensure rematerialization activates the freshly hydrated Character, not a retained stale runtime.Character. | [Layer 12](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-12-persistence-2026-10-07.md) |
| 13. Shared world / population | P2 / P5 | P5.3: retire only stale/expired actor-generation entries; hitting a bound must not clear other live citizens' intent. | [Layer 13](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-13-shared-world-2026-10-07.md) |
| 14. Harness / evidence | P0 throughout | P0.5: correlate enrollment, wake and action witnesses to the named bot/request; population-global step growth is insufficient. | [Layer 14](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-14-harness-2026-10-07.md) |

Layer 15 remains synthesis: its job is to reconcile results and the next priority, not to become another gameplay subsystem.

## First bounded slice

**Begin at P0.1: ordinary activation to first autonomous wake.** This addresses the recorded first failed predicate rather than starting a new quest-solving patch.

| Card field | Proposed content |
|---|---|
| Parent | Existing PlayerBot lifecycle/runtime work; audit layers 1 and 14. Phase labels are sequencing, not a new milestone. |
| Outcome | One designated autonomous Character enters the production runtime and receives its first wake without a manual bridge Wake or a reload of an already live Character. |
| Baseline | HEAD above plus relevant dirty lifecycle/harness files; E-48 records zero enrollment, exact cause unproved. |
| First action | Capture per-bot lifecycle registration/activation, scheduler entry/due time and wake sequence at the ordinary enrollment boundary. Distinguish no entry, entry never due and due wake never executed. |
| Scope | Production enrollment handoff and attributable read-only witnesses; reuse the ordinary lifecycle and scheduler. |
| Non-goals | New quests, longer field windows, supplied stock/locations, global NPC searches, alternate scheduler or a speculative SQL/schema fix. |
| Initial state | One normal provisioned bot explicitly designated for autonomous operation; disclose provisioning, supported assets/config and saved gameplay state. Human characters must not be enrolled accidentally. |
| Acceptance | Live first and subsequent attributable wakes after normal activation. No manual Wake after START. Include failed-step disposition and a second activation without duplicate enrollment. Authoritative world state remains normal. |
| Dependencies | Name the actual handoff failure from observation before implementing its repair. P0.2 integrity and P0.4 Running changes are separately reviewed where required. |
| Owner / verifier | Unassigned; no external dispatch or independent signoff has occurred. |
| Stopping point | Enrollment is attributable and continuous for the named activation path; hand off the next concrete request-integrity/parity outcome. This does not close player autonomy. |

**Readiness:** diagnostic discrimination is bounded; a behavior repair is not Ready until the failed handoff is located. Do not rerun another long field lane simply hoping enrollment changes. Once enrolled, use the ordinary first activity to identify the next failure.

## Evidence and stopping rules

Use contract tests for invariants such as terminal finality, same-key retry, known-mask applicability and exact request ownership. Use real server execution for timing, rights, receipts, travel, progression, recovery and repeat. Use normal-client captures only where they settle parity; H is its separate actual human verdict.

Every phase exit records the named supported scope and its required legs: starting means/acquisition → discovery/binding → legal reach → ordinary action/timing → observed consequence/conservation → recovery → repeat → applicable persistence. If a required leg is missing or unproved, the loop remains incomplete even when a subloop passes.

Each dispatched child slice records exact SHA/dirty baseline, canonical archaeology where required, commands/environment/assets, build/compiler and pass/fail/skip identities, MCP smoke status where applicable, artifact paths, supplied inputs, independent review and deployment state. Required implementation gates follow AGENTS.md; this documentation pass runs only structure/link/preservation checks and claims no gate result.

Do not create a long queue of speculative class-level cards. The phase entries are work packages; split only the next available package into one observable outcome using the existing template. In particular P0.3's GCD, harvest and recovery items are separate bounded changes, each with its own acceptance and refusal/interruption proof.

If a claimed server rule is absent or ambiguous, stop that affected capability, inspect its normal service/data chain and open the smallest research question. Client RE is limited to the conditional questions in layers 6, 8 and 9. Do not change thresholds, vendor stock, quest prerequisites or the acceptance interval to hide the missing dependency.

## Deferred until the foundations prove useful

Keep the bounded planner and existing operational population infrastructure. Defer additional quest-specific watchdogs, GOAP action breadth, full homestead/trade/guild systems, a new blackboard/cache, new movement/interaction machines and population optimization without measured need. None is a substitute for observation, normal effects or durable purpose.

The target remains useful independent player behavior. Counts of quests, micro-gates or completed actor verbs are diagnostic metrics; they are not the phase-exit criterion.
