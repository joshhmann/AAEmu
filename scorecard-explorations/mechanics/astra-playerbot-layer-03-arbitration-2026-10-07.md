# PlayerBot goal arbitration and top-level decision ownership audit

Author: Astra. Layer 3 of the requested subsystem audit. Date: 2026-10-07.

**Verdict: STRUCTURAL-ISSUE.** The deterministic arbitration primitives are useful, but one elected activity name does not establish one owner of the bot's intent or actions. Recovery executes gameplay during election; schedules reroute independently; imperative branches and the homestead planner make additional choices.

Audit baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` **plus the existing dirty working tree**, including executor and QuestDirector edits. Read-only source audit; no builds, tests, live runs, gameplay edits, commits, or deployments. Only this report was added during this pass.

Scope: activity arbitration, production decorator/wiring, activity-module eligibility and activation, executor dispatch gates, and immediately adjacent director/GOAP ownership boundaries. Quest interpretation, combat implementation, recovery mechanics, and planner search are deferred to their own layers.

## A. Intended Responsibility

Own the choice of useful primary intent and its disposition: continue, complete, temporarily yield to a reaction, abandon with a reason, or select another available goal. Distinguish urgency, gameplay eligibility, opportunity readiness, personal preference, and server execution budget.

A player needs both deliberate intent and immediate reactions. Combat defense or fleeing may preempt work without becoming another strategic brain. Preserve the primary objective through that interruption; revalidate before continuing through a new request. Do not resume an interrupted ActorRequest.

Schedules, events, needs, quests, and social coordination should supply candidate goals or constraints to the same decision owner. Executors own bounded implementation inside a selected task. Population management owns whether and at what fidelity a bot is simulated, not its gameplay ambition.

No planner algorithm is required to establish these responsibilities.

## B. Actual Responsibility

Production is [BotGoalArbiterStepExecutor around BotRoamStepExecutor](/root/aaemu-dev/AAEmu.Game/Program.cs:381). The arbiter sorts modules by fixed priority, probes until the first Allow, records an activity string, and invokes Activate only when that string changes. The inner executor always runs afterward.

| Component | Classification | Reason |
|---|---|---|
| BotGoalArbiter fixed ordering and single-name memory | KEEP + HARDEN | Good deterministic transitional selector; its memory is an activity label, not a complete intent contract. |
| BotGoalArbiterStepExecutor | KEEP + HARDEN | Reuses the one scheduler/executor boundary; must carry the selected disposition into dispatch rather than merely stage a trace and queryable name. |
| IBotActivityModule / BotActivityContext | REFACTOR | Eligibility and activation mix selection with effects; live runtime access does not make inputs an immutable observation. |
| BotDecisionProposal / BotDecisionSelector | KEEP + HARDEN | Named legality gates precede preference; deterministic local action selection remains useful. |
| BotDecisionCycle | KEEP + HARDEN | Actor dispatch bridge is appropriate; same-snapshot precondition reevaluation is not a fresh authoritative legality query. |
| PersonalityUtilityMap | DEFER | Bounded preference vocabulary is harmless, but source search found no shipping callers. It is not current utility-driven activity selection. |
| QuestBootstrap / NeedsFarm modules | KEEP + HARDEN | Correctly inspect ordinary Character resources; willingness is insufficient evidence of useful runnable work. |
| ConflictJoin module | KEEP + HARDEN | Reads actual zone conflict and ordinary readiness; policy is still a fixed activity preference, separate from self-defense. |
| PresenceRoam / Idle modules | KEEP + HARDEN | Useful ambient fallbacks; clearing or retaining a route does not resolve ownership of an existing request. |
| OutOfCombatRecovery arbitration methods | MOVE OWNERSHIP | Mutate posture/recovery state during eligibility and execute recovery during activation. Effects belong in the selected executor through normal gameplay. |
| FishingContest module | DEFER | Default-off eligibility shell; no production executor consumer was found. Do not elect it as executable work yet. |
| Homestead module | KEEP + HARDEN | Default-off boundary is good; its eligibility and the nested planner do not describe the same achievable goal. |
| Schedule phase module + schedule service | COLLAPSE | Production suppresses the module, leaving a separate service directly applying schedule behavior. Retain one phase resolver/source, one gameplay decision path. |
| Imperative BotRoam activity/party/hunt/work dispatch | REFACTOR | Shared actor is correct, but branch order forms a second activity selector with implicit overrides. |
| GOAP GoalArbitrator at homestead boundary | MOVE OWNERSHIP | It selects survival, hunting, farming, and homestead goals inside an already elected homestead activity. One strategic owner must choose; bounded planning may execute that choice. |
| GoapPlanRunner execution foundation | KEEP + HARDEN | Preserving primary intent and using ActorRequest preemption are useful concepts; detailed search/execution correctness is layer 11. |
| GoapBotStepExecutor | DEFER | Alternate adapter was not found in production DI; do not wire it alongside the existing actor-owning executor. |
| QuestDirector | KEEP | At this boundary it builds quest-local plans and delegates execution. Its name does not make it a competing top-level brain. Interpretation is layer 4. |
| PopulationDirector | KEEP | Fidelity, density, pressure, materialization, and scheduler wakes are appropriate population responsibilities. |

## C. What Is Good

The arbiter is deterministic, stable under equal priority, and avoids repeated activation for an unchanged activity. It does not add another scheduler. Production [uses the existing actor through the executor](/root/aaemu-dev/AAEmu.Game/Program.cs:321).

[BotDecisionSelector](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotDecisionProposal.cs:255) checks hard preconditions before priority/personality/tie-break ordering and records named rejection reasons. Legal alternatives are distinct from illegal proposals. Keep this distinction.

Default-off quest/farm/contest/homestead gates are honest rollout boundaries. Existing [arbiter tests](/root/aaemu-dev/AAEmu.UnitTests/Game/Core/Managers/Bots/BotGoalArbiterTests.cs:74) establish deterministic contract behavior; they do not establish useful player decisions in a shared world.

[QuestDirector.Run](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestDirector.cs:212) delegates to QuestBehavior and owns quest-local plan construction, rather than provisioning, economy, or top-level goal arbitration. Keep that separation.

## D. Main Problems

### CRITICAL — Election executes gameplay and eligibility is not pure

The [module contract](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/IBotActivityModule.cs:31) requires side-effect-free CanActivate. [Recovery.CanActivate](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:441) changes recovery tracking and can exit sitting posture even when it declines.

[Recovery.Activate](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:483) calls ExecuteRecoveryStep(character, null, ...). With a recognized consumable, the [null-actor branch](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:402) removes an item/count and directly adds HP/MP. This branch is reachable from the production-registered module. It is gameplay during goal election through a synthetic fallback, not merely choosing recovery.

The module is also activation-driven rather than continuously executor-driven. There is no recovery-specific step branch in BotRoam; later unchanged wakes do not call Activate. Meanwhile ordinary route/hunt logic can still run. The exact sitting/regen mechanics are deferred, but ownership is already incorrect.

The arbiter's [activation exception handler](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs:201) restores only the previous activity label and asserts that side effects never landed. Arbitrary Activate code offers no such guarantee. A partially applied activation cannot be repaired by restoring a string.

**Uncertainty classification:** IMPLEMENTATION BUG / ownership violation. No client RE is needed to identify or remove these bot-owned effects. Exact normal consumable/sitting behavior is a cross-layer question for layer 10.

### HIGH — Elected activity and actual execution are different authorities

The [decorator](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotGoalArbiterStepExecutor.cs:44) arbitrates and then unconditionally calls the inner executor. The inner step independently evaluates survival, party, PvP, needs, quests, homestead, wildlife, livestock, and routes.

A [party member's follow/assist branch](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:903) sets handledByParty independently of the elected goal and prevents the work legs. This may be a reasonable player policy, but the strategic owner never selected it.

More seriously, [wildlife dispatch](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:1080) is suppressed by whether a work leg landed, rather than whether an intent owns the wake. [StepQuestLeg](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2053) may arm quest travel and return false because no proposal landed; wildlife can then acquire a target and interrupt that travel. This occurs when wildlife is enabled, not in every deployment.

Recovery is also not a hunt/route ownership gate. Electing recovery.rest does not itself prevent downstream hunting or walking. Actor serialization prevents simultaneous requests, but cannot establish which behavior was authorized to replace another.

The [survival gate](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:868) is a valid place for a fast reaction. Keep reactive preemption; make its override and retained primary intent explicit. Do not solve this by merging rotation, navigation, and every action into a monolithic arbiter.

**Classification:** IMPLEMENTATION BUG in conflating “work dispatched” with “intent owns execution”; GAME-DESIGN DECISION for party/ambient policies; MISSING OBSERVATION where readiness is unknown.

### HIGH — Schedule and nested planner retain independent strategic ownership

The [schedule module](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/SchedulePhaseActivityModule.cs:83) declines when the authoritative schedule service is enabled. This correctly avoids applying the same phase twice. However, the [service](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotScheduleService.cs:157) then applies phases directly for active out-of-battle bots, without consulting the elected activity.

Its [behavior adapter](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotScheduleBehavior.cs:47) calls SetRoamRoute, including during recovery, quest travel, farm work, or homestead work. [SetRoamRoute](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:690) replaces the route and marks it ROAM, but does not transfer/cancel a live request as part of that assignment. The bot can therefore retain one live movement while the route cursor describes another intent. Both paths run on the game loop; this is competing ownership, not necessarily a thread race.

When homestead is explicitly enabled, [StepHomesteadLeg](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2741) creates an unrestricted default GoapPlanRunner. Its [GoalArbitrator](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/GoalArbitrator.cs:47) may choose recovery, defense, wild farming, hunting, or roaming. Thus “homestead.progression” can contain a second strategic decision system with broader goals than its parent election. Recovery also exists in outer module and survival policy.

[PROJECT-CONTROL's ownership checkpoint](/root/aaemu-dev/PROJECT-CONTROL.md:257) already specifies one strategic authority and calls activity/imperative branches transitional. That intended architecture is not the current production wiring. Retain the committed planner foundation; do not claim that wiring a second executor or broadening GOAP resolves the split.

**Classification:** MOVE OWNERSHIP / GAME-DESIGN DECISION. Schedule resolution remains a goal source; final selection and task ownership need one path.

### HIGH — Willingness outranks opportunity, causing starvation and empty activities

[QuestBootstrap](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBootstrapActivityModule.cs:84) allows quest.progress whenever any quest is active, or the bot is below its copper target. It does not check whether an active quest has supported, available work or whether any earnable quest is known.

Consequently, an unavailable or unsupported active quest can keep priority 58 above NeedsFarm at 55 indefinitely. No module-level blocked/completed/awaiting-availability disposition permits another goal to win while preserving quest intent. The executor may fall through to unrelated wildlife work, but that is not intentional resource-aware goal selection.

[FishingContest eligibility](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/FishingContestActivityModule.cs:70) checks an option window and pressure; Activate returns only a label. A source search found no production consumer of FishingContestScenario or a fishing activity branch. FromEnvironment sets only Enabled, leaving the default window open for all dates. Enabling this shell can therefore outrank quest/farm work without scheduling a fishing task. This is a source-reachable configuration defect, not a newly observed live failure.

[Homestead eligibility](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/HomesteadActivityModule.cs:35) allows until a completed house is found, without proving the next progression opportunity. The inner [goal arbitrator](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/GoalArbitrator.cs:117) can return null for a bot without a design before considering its later hunt/roam fallbacks. The outer activity remains eligible. Detailed design acquisition is layer 11.

**Classification:** MISSING OBSERVATION for executable opportunities and blockers; IMPLEMENTATION BUG for electing an unwired activity; GAME-DESIGN DECISION for policy precedence. A timer would hide these missing dispositions rather than resolve them.

### MEDIUM — Activity identity and traces overstate decision continuity

[Active memory](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs:88) stores only an activity name. Equality ignores module identity, bound objective, task version, and execution status. It cannot distinguish two quest intents sharing quest.progress. NoCandidate [deletes the label](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs:221) without touching ongoing work; returning later invokes Activate again. Intent can survive in quest/farm/planner memory, but the top level neither owns nor explains that continuity.

CanActivate's returned WhyNot is discarded; Candidates is the registered module count, although lower-priority modules after the winner were never evaluated. Therefore the staged activity trace is not a complete comparison of available alternatives. The decorator cannot by itself explain a party/wildlife override or an independently applied schedule route.

The “utility” vocabulary also exceeds the implementation. Activity selection is a fixed ordered decision list. Proposal selection uses priority first and personality only as a tie-break among equal priorities. [PersonalityUtilityMap](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PersonalityUtilityMap.cs:12) has no shipping callers in the source search. None of these facts requires replacing deterministic policy with GOAP or a new utility engine; they require truthful descriptions and evidence.

**Classification:** MISSING OBSERVATION of decision disposition; GAME-DESIGN DECISION about preferences; IMPLEMENTATION BUG if traces are interpreted as evaluated alternatives.

## E. Hidden Assumptions

- One activity name is treated as one active behavior, even though routing and gameplay ownership live elsewhere.
- Any active quest implies useful quest work; low copper implies a discoverable earning opportunity.
- A work leg that did not dispatch is treated as permission for ambient acquisition.
- Party membership implicitly authorizes following/assisting over the elected primary goal.
- An enabled contest option implies a scheduled, executable event. Default option dates are supplied by code, not observed event state.
- Schedule anchors, roam descriptors, home metadata, and remembered housing locations are supplied knowledge. Their existence does not prove discovery.
- The homestead boundary uses a fixed fallback coordinate when resolving housing-zone memory. That is scenario/world knowledge supplied by the bot code; its detailed target binding is deferred.
- Recovery's activation fallback supplies gameplay outcomes that a primitive player must obtain through normal services.
- Tests with module fakes and explicit schedules prove ordering and activation counts, not autonomous goal discovery.

No hard-coded quest-251 test was found in the top-level arbiter or QuestBootstrap eligibility. Current quest-251 harness assumptions and travel patches remain relevant downstream, but are not evidence that this selector itself compares a fixed quest ID.

## F. Missing Authoritative Truth

| Required input | Available basis / boundary | Classification |
|---|---|---|
| Is there supported, available quest work? | Server quest state plus bounded quest capability/opportunity interpretation | SERVER ALREADY KNOWS state; MISSING OBSERVATION of readiness |
| Is recovery needed and what can legally satisfy it? | Character HP/MP, combat state, inventory, normal item/skill gates | SERVER ALREADY KNOWS substantially |
| Is a contest really active and runnable? | Actual event state plus an installed executor binding; configuration alone is insufficient | MISSING OBSERVATION / IMPLEMENTATION BUG |
| What task owns the current request and route? | ActorRequest plus an explicit owner/intent identity supplied at dispatch | MISSING OBSERVATION; bot control state, not client RE |
| Is an objective blocked, waiting, completed, or abandoned? | Observed outcomes and server state interpreted by the capability executor | MISSING OBSERVATION |
| Should schedule, war, party, quest, or gathering win? | Explicit player-simulation policy over truthful inputs | GAME-DESIGN DECISION |
| What was actually compared and why did execution override it? | Arbiter evaluation and executor disposition records | MISSING OBSERVATION |

Layer 2 already records visibility, eligibility, loot, and availability gaps; this pass adds the decision statuses needed to use those facts. It does not re-audit their engine semantics.

## G. Ownership Problems

Canonical ownership should be one strategic decision authority consuming goal sources, plus explicitly bounded reactive overrides. The current arbiter can remain the transitional selection surface while that contract is made concrete. The repo names GOAP as the eventual strategic authority; migration should be one proven capability at a time, not parallel ownership.

Schedule service owns phase resolution/persistence. PopulationDirector owns fidelity/pressure/density. QuestDirector owns quest-local interpretation. Combat/navigation/interaction/recovery executors own normal actions inside their task. Planner search owns how to achieve an assigned goal where planning has proven value.

**CROSS-LAYER QUESTION:** What reliable quest readiness/blocked status can QuestDirector and QuestBehavior expose without inventing another quest interpreter? **DEFER TO:** layers 4–5.

**CROSS-LAYER QUESTION:** How should route replacement and action preemption transfer task ownership? **DEFER TO:** layer 6.

**CROSS-LAYER QUESTION:** Which recovery effects are ordinary authoritative mechanics and which are synthetic? **DEFER TO:** layer 10. Production activation bypass is already established here.

**CROSS-LAYER QUESTION:** Can the retained plan runner accept the selected strategic goal without independently choosing unrelated activities? **DEFER TO:** layer 11.

## H. Generalization Risk

Different quests expose unsupported/unavailable work retaining top priority. Different zones or distances expose schedule routes replacing deliberate travel and empty event/homestead choices. Higher mob density gives ambient combat more chances to steal a wake after work reports no dispatch.

Different ownership and multiple bots amplify implicit party/cohort assumptions; group follow is currently an executor override rather than an explicit social goal. Different combat situations require real reactive interrupts, but top-level NoCandidate currently loses the activity label while requests continue.

Different inventory exposes synthetic recovery and missing procurement opportunities. Different levels expose quest policy limits downstream; this layer cannot compensate through a broad activity label. Restart restores some domain memory independently of the nonpersistent activity string; authoritative continuity/persistence is layer 12.

## I. ArcheAge Reverse-Engineering Questions

**No blocking CLIENT RE NEEDED finding is established.** The ownership defects, unwired activity, direct recovery effects, and fixed-priority semantics are visible in server code.

How strongly a primitive player values schedules, war, hunger, or party loyalty is a GAME-DESIGN DECISION, not something a retail client supplies. Normal consumable, quest, and interaction mechanics may require focused RE in their own layers if server code/data prove insufficient; this pass has not established that insufficiency.

## J. Live Proof Needed

Use one declared production configuration and an ordinary bot with two genuine competing opportunities: an active quest whose target/resource is temporarily unavailable, and a known legal way to acquire a needed resource or copper. Do not give it the completion item, target ID, or a manually driven wake stream.

Observe whether it preserves the quest objective, chooses useful alternative work, and returns when authoritative availability changes. During the session, cross a normal schedule phase, take actual damage, and receive a real party invitation. Verify recovery through normal item/skill effects and ordinary inventory deltas.

For every change record primary intent, reactive override, evaluated candidates and blockers, selected task, owning request/route, and authoritative outcome. Require that a schedule transition cannot silently overwrite the selected task, and that recovery cannot consume/add resources during candidate evaluation. Repeat with a second bot competing for opportunities.

A separate normal-event session must demonstrate a real contest executor before fishing becomes an accepted goal source. Enabling an option and logging fishing.contest does not prove participation.

## K. Recommended Changes

1. **Make selection pure.** Remove recovery effects/posture changes from CanActivate and Activate; dispatch recovery through the existing actor and ordinary gameplay. Do not replace failed gameplay with synthetic grants. Review all activation side effects before trusting rollback claims.
2. **Give selected intent an explicit execution disposition.** Distinguish owned work, waiting on a live request, waiting for availability, no work, completion, and abandonment. Suppress unrelated ambient acquisition when a selected task owns the wake; declare survival and party overrides explicitly.
3. **Route goal sources through one owner.** Schedule service proposes its resolved phase instead of assigning routes independently. Keep the homestead planner bounded to the assigned goal, or deliberately move strategic selection to that retained runtime for a proven slice. Do not wire both strategic paths.
4. **Require truthful opportunity readiness.** Quest/farm/homestead candidates expose concrete next work or a named blocker; blocked intent can yield to useful alternatives without being forgotten. Keep fishing ineligible until an actual production executor and event-state source exist.
5. **Preserve and explain intent at the selection boundary.** Track a small intent identity including owner/objective and status, independently of the activity label. Report evaluated versus unevaluated candidates and the actual execution override. Retain fixed deterministic priorities until live comparison justifies another preference policy.

These are contract/ownership changes, not authorization to implement them in this audit. No new thresholds, second scheduler, universal planner rollout, or broad blackboard is recommended.

## L. Do Not Touch

Keep the scheduler decorator seam, shared production actor, deterministic stable ordering, legality-before-preference, named precondition failures, and legal-alternative distinction. Keep default-off experimental activity gates.

Keep QuestDirector's quest-local responsibility and PopulationDirector's population controls. Keep schedule resolution/hysteresis/persistence semantics while moving only its gameplay dispatch ownership. Preserve the committed planner foundation and its useful primary-intent concept; search/action details have not been audited here.

## M. Verdict

**STRUCTURAL-ISSUE.** The project currently has a fixed-priority activity selector decorating an imperative behavior executor, with independent schedule dispatch and a nested strategic planner when homestead is enabled. This can produce narrow successful behavior, but cannot consistently explain or enforce who owns a persistent player's intent. The direct recovery path also breaks ordinary gameplay authority. Convergence starts with pure election, explicit task disposition, and one owner for strategic changes; more planning machinery would not resolve the present conflict.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0 / P2. **Canonical owner:** One strategic intent owner at the documented GOAP boundary; arbiter/modules are transitional offers/adapters.

**First reviewable outcome:** P0.3 recovery slice: remove gameplay mutation from eligibility/activation; actual recovery dispatch uses the existing actor.

| Phase | Work owned by this layer |
|---|---|
| P0 | Make recovery selection side-effect free and eliminate synthetic success. |
| P2 | Use the retained primary-intent concept for one selected goal. Merge schedule, quest and needs offers there; publish owned-running, waiting, blocked and terminal dispositions. |
| P3 | Permit only named safety/party interruptions; reuse capability execution and preserve the interrupted purpose. |
| P4 | Allow the owner to request bounded prerequisite planning, rather than selecting another independent homestead goal. |

**Dependencies:** P0 Running ownership; P1 actionable facts. PROJECT-CONTROL's strategy checkpoint is the ownership reference; do not install arbiter and GOAP as competing brains.

**Live stopping point:** One inspectable selection explains the actual dispatched work, its wait, its interruption and the next useful activity.

**Preserve:** Legality-first deterministic ordering, shared production actor and default-off unsupported activities.

**Defer:** New utility machinery, broad planner rollout and enabling labels with no executor.


## Cross-layer notebook

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|
| 1 — Scheduler/runtime/lifecycle | STRUCTURAL-ISSUE | Tick-marshalled execution and terminal request lifecycle | Enrollment and embodiment continuity | Lifecycle coordinator; scheduler owns timing; actor owns requests | Current embodiment + explicit next scheduling disposition | Layer 2 — observation |
| 2 — Observation/perception/world-state | STRUCTURAL-ISSUE | Bounded engine reads and pure deterministic projections | Incompatible eligibility and absence semantics | Common observer adapter over ordinary gameplay; belief interprets | Caller-relative opportunities + valid coverage/resource availability | Layer 3 — arbitration; rights/gates deferred to 8–9 |
| 3 — Goal arbitration/top-level ownership | STRUCTURAL-ISSUE | Deterministic legality-first selection on the shared execution path | Elected activity does not own execution or persistent intent | One strategic owner; modules supply goals; executors own bounded tasks | Opportunity readiness + intent/execution disposition | Layer 4 — quest interpretation; route/recovery/planning deferred to 6/10/11 |

## Evidence provenance

HEAD: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d`; dirty working tree included. Archaeology invoked through the local Release stdio server with `list_sources {}` and three `search_files` calls under `root:"game-source"`:

- `{pattern:"CanActivate|Activate|_activeActivity",glob:"BotGoalArbiter.cs",limit:20}`: 20 matches, one file, **truncated at limit**; full file was separately read from the filesystem.
- `{pattern:"ExecuteRecoveryStep|ActiveBotRecoveries|ExitSittingStance",glob:"OutOfCombatRecoveryModule.cs",limit:30}`: 21 matches, one file, not truncated.
- `{pattern:"ApplyPhase|ResumeRoam|MoveToAnchor",glob:"BotScheduleService.cs",limit:12}`: 5 matches, one file, not truncated.

Provenance generated 2026-10-07 17:07:34 UTC; `source_id:game-source`, path `/root/aaemu-dev/AAEmu.Game`, search version null; catalog version `fork develop`. Confidence **textual**, supported by adjacent source reads and bounded call-site searches. Canonical DB version 1.2 r208022; measured MD5 `78b3bdbf038db3b927056106efdf91af`. No new reference-row, live, client, or H claim. See [archaeology acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md). No tests/gates ran; cited tests are inspected contracts, not fresh results.

