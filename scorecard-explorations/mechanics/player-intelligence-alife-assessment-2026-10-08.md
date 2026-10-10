# AAEmu: player intelligence and A-Life architecture assessment

Date: 2026-10-08. Investigation only. No production edits, project-status changes, implementation dispatch, servers, or test campaigns.

Baseline: HEAD **e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d**, with substantial pre-existing staged, unstaged and untracked work. The working tree changed during inspection; the newest ledger entry reviewed is **E-62**. Source additions and reported tests are distinguished below. This is not a release approval.

The repository and archaeology catalog identify **ArcheAge 1.2 r208022**, not the prompt's 1.2.4 designation. Client research must use the actual supported build; compatibility with a different build is unverified.

**Evidence language:** “source” means the inspected code implements the behavior; “recorded live” means an existing named server run reports it; “reported tests” means the ledger records checks not rerun here; “recommendation” means proposed direction. A fixture or planner prediction is never treated as an observed game result. Older audit notes were used as leads and checked against current source where conclusions depend on it.

## A. Executive assessment

**AAEmu is building several correct foundations, but has not yet demonstrated a persistent population of characters that independently decide how to live.** It has ordinary server Characters, bot creation and lifecycle services, one production scheduler, a substantial gameplay action interface, reactive behaviors, a bounded planner, population controls and useful observation/testing tools. Real server quest progress and a narrow housing loop are legitimate achievements.

The main gap is the connection between **what the bot can actually do now, what it chooses to achieve, the task it is currently executing, and what really happened afterward**. Several systems make decisions in different scopes. They do not consistently share one retained purpose or one understanding of available opportunities. Saving a character and running an action planner do not, by themselves, solve that gap.

The strongest maturity statement is: **parts of Can Play are demonstrated; parts of Can Decide are implemented and narrowly exercised; Can Live and Living World are not demonstrated as complete stages.** This is more capable than a fixed quest replay, and less capable than a general autonomous player.

Authored goal types are not a defect. A useful deterministic bot can choose from a small set of programmed goals. The autonomy question is whether the bot selects, carries out and revises them from actual game conditions, rather than receiving the goal, target or next step from its test driver.

The next direction should be one short proof of that choice-and-feedback loop, using existing gameplay. It should not be another planner, a replacement runtime, an LLM brain, or a large off-screen economy.

## B. Current architecture map

The production entry point is explicit in [Program.cs](/root/aaemu-dev/AAEmu.Game/Program.cs:377): the scheduler's step executor is BotGoalArbiterStepExecutor around BotRoamStepExecutor. The alternate GoapBotStepExecutor class exists, but is **not that production binding**. GOAP still runs inside the production homestead branch.

~~~mermaid
flowchart TD
    P[Population and lifecycle services] --> S[PlayerBotScheduler]
    S --> A[Activity selection: BotGoalArbiter]
    A --> R[BotRoamStepExecutor]
    R --> Q[Quest behavior]
    R --> N[Needs and farm behavior]
    R --> G[Homestead GOAP runner]
    R --> C[Combat, travel, party and roaming branches]
    Q --> X[GameplayActor and ActorRequest]
    N --> X
    G --> X
    C --> X
    X --> E[Ordinary Character and game services]
    E --> O[Observed results]
    O --> Q
    O --> N
    O --> G
~~~

This diagram describes inspected wiring, not acceptance of every enabled branch. Features and bot creation paths have separate configuration gates. The population layer controls which bots run and at what cost; it does not supply a demonstrated autonomous world economy.

| Responsibility | Actual implementation and dependencies | Integration/status | Evidence and ownership limits |
|---|---|---|---|
| 1. World perception | [GameplayActor.Observe](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:261), ActorObservation and BotObservedContext copy Character, inventory, quest and nearby world state. | Implemented, consumed by quest/needs decisions. | Direct authoritative reads are useful; different callers also perform their own world scans. Coverage and rights are not one complete opportunity model. |
| 2. State representation | [BotWorldState](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/BotWorldState.cs:11) and its provider compress planning facts; [BotPerceptionSnapshot](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotPerceptionSnapshot.cs:135), delta and belief types provide richer representations. | GOAP projection is connected to homestead. Rich perception/belief code exists; bounded production search found no direct Capture/Interpret consumption in the inspected decision owners. | Do not count a richer type as adopted production perception. Multiple projections are legitimate if they share facts, freshness and unknown-state meaning. |
| 3. Working memory | [BotRoamState and caches](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:649), quest memory, travel/loot ledgers and [GOAP BotMemory](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/BotMemory.cs:25). | Implemented, mainly in memory. | Holds current routes, targets, failures, groves and known locations. Scope differs by behavior; no general personal knowledge store was established. |
| 4. Persistent memory | Ordinary Character persistence plus [PlayerBotMetadataStore](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotMetadataStore.cs:90), schedule payload and progression cursor serialization. | Implemented storage/configuration; partial runtime consumers. | Home/schedule/profession/personality/planner-state fields exist. A durable field is not proof that production restores meaningful intent or learned relationships. |
| 5. Goal selection | [BotGoalArbiter](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs:129) selects an eligible activity; [GoalArbitrator](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/GoalArbitrator.cs:17) chooses goals inside GOAP. | Both implemented; activity election is production-wide, GOAP selection is locally hosted. | Selected activity does not fully determine all later branch behavior. Strategic ownership overlaps. |
| 6. Goal prioritization | Registered activity priorities and ordered eligibility checks; GOAP has an ordered survival/committed-intent/routine policy. | Implemented deterministic policies. | A small fixed priority policy is valid AI. General opportunity-aware utility and commitment are not demonstrated merely by these priorities. |
| 7. Planning | [GoapPlanner](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanner.cs:45), action registry, runner and template cache; quest director composes supported verb patterns. | Real bounded search; production homestead host at [StepHomesteadLeg](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2763). | Resource-aware search and actual-result evaluation are foundations. Quest composition and navigation planning have different scopes; they are not interchangeable “brains.” |
| 8. Action execution | [IGameplayActor](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs:88), GameplayActor, ActorRequest and ordinary services; external action queue shares the execution boundary. | Implemented broad action surface, with uneven parity. | Keeps bots as ordinary Characters. Some methods approximate normal timing/transactions; successful dispatch is not enough. |
| 9. Failure recovery | Runner retries/replanning, travel/loot/quest decisions, actor terminal results, scheduler failure handling. | Implemented but distributed. | E-61 reports one failed-turn retry, then an observable fault. That is scheduler recovery, not choosing another useful gameplay goal. |
| 10. Character progression | [QuestDirector](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestDirector.cs:245), QuestBehavior, fixture-derived template facts, PatternCatalog and VerifiedVerbRegistry, normal quest/XP/inventory state. | Connected for enabled supported quest work. | Recorded quest turn-ins prove real progress in named scenarios. Broad quest choice, equipment policy and self-acquisition remain partial. |
| 11. Population management | [PopulationDirector](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PopulationDirector.cs:109), provisioning/presence, density/pressure checks and proximity/dormancy. | Implemented production services; historical operational proofs. | Owns cost and embodiment. It must not assign gameplay success or masquerade as personal goal choice. |
| 12. World simulation | AAEmu world/entities, spawn and gameplay managers; [DormantBotRegistry](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:211) removes/restores bots. | Ordinary detailed world simulation exists. Abstract autonomous off-screen gameplay was not found in inspected population/dormancy paths. | Dematerializing a bot is not simulating its travel, production or trade while absent. |
| 13. Scheduling/lifecycle | PlayerBotManager, PlayerBotLifecycleAdapter/shared CharacterLifecycleService, [PlayerBotScheduler](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:384). | Implemented; E-56/E-60/E-61/E-62 report recent uncommitted hardening. | Connected-character protection, duplicate handling, failed-step disposition and activation-generation checks are real progress. Generation recreation does not restore purpose. |
| 14. Testing/observability | Actor audit, decision traces/inspector, GOAP telemetry, counters, bridge, unit/integration scenarios and existing dossiers. | Implemented varied evidence layers. | Tools expose decisions; drivers can also inject targets/state/events. Traces need per-bot joins and disclosed interventions to prove autonomy. |

### Documentation disagreements that matter

[PROJECT-CONTROL's ownership checkpoint](/root/aaemu-dev/PROJECT-CONTROL.md:257) assigns strategic goal selection/replanning to GOAP and calls the arbiter transitional. [Brain wake ownership](/root/aaemu-dev/scorecard-explorations/mechanics/brain-wake-ownership.md:69) assigns goal selection to BotGoalArbiter; [brain boundaries](/root/aaemu-dev/scorecard-explorations/mechanics/brain-boundaries.md:1) calls GOAP a planner rather than the brain. These investigations do not automatically supersede canonical project decisions.

The GOAP document also describes broad intent rather than a verified exit. A later brain note's claim that GOAP is unwired must be narrowed: the inspected source **does host GoapPlanRunner inside production homestead work**, while the standalone GOAP executor is not the production entry point. Preserve the code; reconcile the ownership decision before extending it.

The brain roadmap calls its initial quest/runtime extraction complete while marking later perception, utility, population and persistence phases not started. That is a narrow extraction claim, not completion of player intelligence. CURRENT's summary remains behind the newest ledger entries; read E-62 for recent hardening rather than treating the older summary as the full current implementation.

## C. Capability evidence matrix

All “demonstrated” results below are bounded to their named evidence. No new live or human proof was collected for this assessment.

| Capability | Classification | What is actually supported | What remains unproved |
|---|---|---|---|
| A. Execute predefined task sequences | Demonstrated | Scripted scenarios and recorded claim→construct/quest chains exercise real game effects. STATUS/SCORECARD's September housing exit and E-35…E-38 document narrow successes. | Choosing those sequences without the setup/driver. |
| B. Choose predefined behaviors from conditions | Partially demonstrated | Production activity modules, deterministic quest proposals, survival and branch gates exist; E-8/E-11 describe live activity and subsequent quest work. | Consistent choice and ownership across competing behaviors and supported zones. |
| C. Select goals independently from world state | Partially demonstrated | Activity eligibility and GOAP GoalArbitrator make state-dependent choices. This is real decision logic, not merely an ordered script. | Choice among diverse useful goals from naturally discovered opportunities, with no injected objective or supplied location. |
| D. Construct plans dynamically | Partially demonstrated | Bounded GOAP search combines authored actions, resources and prerequisites; homestead runtime and synthetic/planner tests exist. | General, naturally acquired inputs/targets across zones; a fully autonomous acquisition-to-completion chain. |
| E. Recover and replan | Partially demonstrated | GOAP reevaluates action outcomes and replans; movement/quest/loot recovery exists; E-61 reports scheduler failure handling tests. | One purposeful bot adapting to live disappearance, contention, danger and persistent failure without driver help. |
| F. Retain meaningful state across interruption/session | Partially demonstrated | Character state and some metadata persist; GOAP primary intent survives local interrupts in memory. E-62 reports current-run generation protection. | Durable purpose and relevant knowledge restored after restart; no stale request/object IDs or duplicate effects. |
| G. Choose subsequent long-term objectives | Unknown | Ordered routine fallback and a recorded next quest exist; personality/profession metadata exists. | Self-directed progression into a new kind of livelihood, or preferences and relationships demonstrably affecting long-term choices. |
| Abstract off-screen resource/economic life | Proposed only for the larger vision | Operational fidelity/dormancy scaffolding is implemented. | Authoritative distant gameplay outcomes, reconciliation and conservation. |

“Missing” is appropriate for a particular absent consumer or action; “unknown” is used where this bounded inspection cannot establish the whole project's behavior. No evidence supports treating all seven capabilities as absent, or all as complete.

Recent evidence worth retaining: [E-56/E-57](/root/aaemu-dev/.omp/state/EVIDENCE.md:1720) protect connected Characters, [E-60](/root/aaemu-dev/.omp/state/EVIDENCE.md:1732) fixes completed-action retries while busy, [E-61](/root/aaemu-dev/.omp/state/EVIDENCE.md:1735) gives failed turns a disposition, and [E-62](/root/aaemu-dev/.omp/state/EVIDENCE.md:1738) binds queued work/caches to a fresh activation. All report tests and no live lane. These supersede the corresponding older source findings; they do not close generalized autonomy.

## D. AzerothCore Playerbots comparison

Reference code was inspected at **mod-playerbots/mod-playerbots 037c01418b5d01506917a3db9b44fd56ac5f965c**. This is the requested maintained project, not every historical Playerbots fork.

Its engine collects actions from active strategies and triggers, ranks candidates, checks whether an action is useful/possible, and schedules prerequisites, alternatives or follow-on actions. This is a condition-driven action architecture, not a guarantee of global optimal planning. [Engine.cpp, lines 144–250](https://github.com/mod-playerbots/mod-playerbots/blob/037c01418b5d01506917a3db9b44fd56ac5f965c/src/Bot/Engine/Engine.cpp#L144).

| Playerbots pattern | Existing AAEmu equivalent | Useful direction |
|---|---|---|
| Strategies provide related policies | Activity modules, quest/needs/combat behaviors | Keep domain policies together, but feed one decision owner rather than multiple independent priority loops. |
| Triggers offer work from conditions | CanActivate, quest proposals, survival checks | Conditions should produce proposals/reasons; selecting a winner should own execution until an explicit change. |
| Actions check possibility/usefulness | Actor validation, decision legality, action preconditions | Separate “legal now” from “worth doing”; use normal server legality, then observe the actual result. |
| Values provide reusable context | ActorObservation, planning projection and memory | Share facts with explicit freshness and invalidation; do not let every behavior redefine availability or rights. |
| Prerequisites/alternatives/continuers | GOAP chains and quest/needs fallback branches | Keep GOAP for real prerequisite chains; keep immediate alternatives bounded and explainable. |
| Combat/noncombat/dead policy separation | Combat executors, recovery, scheduler death-watch | Preserve state-appropriate execution; do not make every cooldown or resurrection tick a graph-planning problem. |

Playerbots has separate combat, noncombat and dead engines, and calculated values with refresh intervals. AAEmu can adopt those logical distinctions using its current types; it does not need three new runtimes or a string-based value framework. [PlayerbotAI.cpp, lines 158–166](https://github.com/mod-playerbots/mod-playerbots/blob/037c01418b5d01506917a3db9b44fd56ac5f965c/src/Bot/PlayerbotAI.cpp#L158), [Value.h, lines 62–100](https://github.com/mod-playerbots/mod-playerbots/blob/037c01418b5d01506917a3db9b44fd56ac5f965c/src/Bot/Engine/Value/Value.h#L62).

The travel system represents quest pickup/work/turn-in destinations and a retained travel target; target selection also considers group destinations and existing versus new work. This is useful precedent for **a destination belonging to a purpose**, instead of another component silently replacing its movement. It does not establish that every Playerbots quest is autonomously supported. [TravelMgr.h, lines 44–76 and 594 onward](https://github.com/mod-playerbots/mod-playerbots/blob/037c01418b5d01506917a3db9b44fd56ac5f965c/src/Mgr/Travel/TravelMgr.h#L44), [ChooseTravelTargetAction.cpp, lines 12–105](https://github.com/mod-playerbots/mod-playerbots/blob/037c01418b5d01506917a3db9b44fd56ac5f965c/src/Ai/Base/Actions/ChooseTravelTargetAction.cpp#L12).

Do not copy its WoW assumptions, spell/class logic, quest ecology or numerical priority bands. ArcheAge needs labor, crop phases, property rights, packs, transfers and server-specific transactions. Its random-bot manager also schedules randomization and teleport events. Those are population facilities, not evidence of organic travel or self-acquisition to import into this project's autonomy proof. [RandomPlayerbotMgr.cpp, lines 1337–1353](https://github.com/mod-playerbots/mod-playerbots/blob/037c01418b5d01506917a3db9b44fd56ac5f965c/src/Bot/RandomPlayerbotMgr.cpp#L1337).

**Does AAEmu need the equivalent model?** It needs its useful responsibilities—conditions, reusable facts, comparable proposals and bounded actions. Most already have partial equivalents. Consolidate their meaning and ownership; do not clone the framework alongside GOAP. Borrow the decision discipline, not the codebase's size.

## E. A-Life comparison

Original developer Dmitriy Iassenev described online behavior as detailed and offline behavior as simplified, including movement on a global graph. He also distinguished early dealer/artifact and resource-seeking experiments from the shipped smart-terrain task model. He explicitly said some richer behaviors never reached the original game, and distinguished assigned sleeping tasks from characters choosing their own needs. This is primary testimony about the original implementation, not proof of later mod features. [2008 developer interview](https://www.gamedeveloper.com/game-platforms/interview-inside-the-ai-of-i-s-t-a-l-k-e-r-i-).

Current OpenXRay code corroborates explicit detailed/abstract switching, parent/child handling, scheduling and saved simulator state. It is a community-maintained engine reference, not an untouched retail Shadow of Chernobyl implementation. Inspection revision: **30beaf02608777a3f50752a3d17fb322bbf75643**. [Switch manager](https://github.com/OpenXRay/xray-16/blob/30beaf02608777a3f50752a3d17fb322bbf75643/src/xrGame/alife_switch_manager.cpp#L106), [schedule registry](https://github.com/OpenXRay/xray-16/blob/30beaf02608777a3f50752a3d17fb322bbf75643/src/xrGame/alife_schedule_registry.cpp#L14), [storage manager](https://github.com/OpenXRay/xray-16/blob/30beaf02608777a3f50752a3d17fb322bbf75643/src/xrGame/alife_storage_manager.cpp).

For AAEmu, three different things must remain distinct:

- A **headless bot** has no player client connection. It may still be fully active in the detailed world.
- A **reduced-detail bot** costs less to update. That does not prove it makes progress while distant.
- An **abstractly simulated bot** changes durable position/resources/world outcomes without running every detailed action. That requires an explicit reconciliation and conservation contract.

PopulationDirector already provides useful cost/density/proximity controls. DormantBotRegistry provides removal/restoration. Neither inspected path implements a demonstrated distant livelihood simulation.

Necessary foundations are durable personal intent, verified detailed gameplay, exclusive ownership of each character/world outcome, consistent transitions, authoritative resource accounting and visibility to every nearby player—not one single-player camera. Before abstract progress is added, define what can happen while no player observes it, how two bots contend for the same resource, and how a player arriving mid-task receives the actual result.

Defer abstract combat, farming income, cross-zone trade and faction economies. Moving a saved position or crediting items while dormant would create another gameplay system unless tied to authoritative world operations. No evidence supports claiming emergent market behavior, independent faction goals or needs-driven relationships from current scheduling alone. Later community mod features and popular stories are not used as proof here.

## F. GOAP and brain assessment

The proposed world→perception→knowledge→goal→plan→execution→server→result→reevaluation pipeline is a useful **responsibility model**. It is not today's single connected implementation. Server validation also happens throughout normal gameplay, not merely as a final approval after an action.

Today the steps are distributed:

- World and Character state are authoritative; observations and additional behavior scans read them.
- The activity arbiter chooses a broad activity. Quest, needs, wildlife and party branches still make narrower competing choices.
- The homestead runner senses, chooses/retains a goal, handles interrupts, generates a plan, dispatches requests, retries, verifies and abandons/replans.
- GameplayActor executes through ordinary services, but parity differs by method.
- Completion is observed through request results and domain-specific state changes; long-term goal success is not consistently joined to these.
- Memory is largely local to each behavior; durable bot configuration is separate from restored purpose.

**GOAP's actual role is several of the listed roles combined:** task planning plus hosted goal policy, plan execution bookkeeping and bounded recovery. It is not the production population scheduler. See [GoapPlanRunner.Tick](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanRunner.cs:102), its [goal policy](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/GoalArbitrator.cs:48), and the [production binding](/root/aaemu-dev/AAEmu.Game/Program.cs:377).

Much of that combination is reasonable for a small hosted plan. The problem is allowing it to choose personal strategy while an outer activity selector and executor branches can choose something else. Calling the entire system “GOAP-owned” does not make these decisions agree.

Keep the bounded search. It carries gold/labor constraints and resource-aware dominance rather than treating only a flag word as state; it honestly declines to promise optimality. Keep runner evaluation of observed action results. These are useful engineering foundations. [GoapPlanner](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanner.cs:37).

Fix the planning facts before expanding the graph. The provider clears unknown facts from its mask, but [Satisfies](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/BotWorldState.cs:99) compares required bits without checking whether the observed mask establishes them. An unknown fact can satisfy a required-false condition. Other gaps are concrete source/destination binding, exact item quantities and authoritative action success. Another search algorithm does not repair those.

The evolutionary arrangement should be **logical boundaries around existing modules**, not separate services:

1. One personal decision owner chooses/retains a useful goal from legitimate opportunities. Start with simple deterministic priorities; introduce richer utility only when comparisons need it.
2. The retained GOAP planner answers how to achieve selected goals with genuine prerequisite chains. Simple reactive work does not need a graph plan. No current evidence makes HTN mandatory.
3. Combat/navigation/interaction behavior owns a bounded task's internal timing and interruption handling. The scheduler owns execution opportunities and server cost.
4. GameplayActor remains the normal-gameplay adapter; ordinary engine services own legality, ownership and effects.
5. Observation/result handling updates the decision owner and relevant memory. Preserve purpose across interruption, rebuild requests from fresh game state, and persist only the personal facts needed for restart.

This requires an explicit ownership decision because project records conflict. The existing arbiter is a practical production entry point to consolidate proposals, and the existing GoalArbitrator provides reusable policy. That is a recommendation for reconciliation, not permission to override the canonical GOAP ownership checkpoint. Do not add a third strategic selector.

## G. Technical debt and architectural risks

| Priority/type | Evidence and consequence | Direction |
|---|---|---|
| HIGH: prevents broader autonomy | Activity selection, internal branch gates and GOAP goal selection all influence behavior. [Homestead gate/fallback](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:1087) shows narrower work and wildlife fallbacks after election. A named activity alone does not guarantee retained intent. | Consolidate personal goal ownership and explicit task status. Preserve the existing scheduler and executors. |
| HIGH: prevents trustworthy decisions | Multiple sensing paths, unknown-mask mismatch and spawn-location versus current availability. A bot can choose work whose inputs or rights were never established. | Expose available/blocked/unknown opportunities with authoritative facts; do not add a timer to compensate for missing knowledge. |
| HIGH: execution mismatch | [BotBehaviorRuntime](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotBehaviorRuntime.cs:132) treats only Completed as landed quest work, while actor requests can legitimately remain Running. Timing/purpose can be lost when other work assumes failure or no work. | Keep ownership of accepted asynchronous work until completion, refusal or explicit interruption. Confirm each caller's behavior before changing it. |
| HIGH: gameplay parity | [Learned-skill GCD direction](/root/aaemu-dev/ROADMAP.md:112) remains deferred; [Harvest](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:3737) directly drives Doodad.Use and authors skill packets. [Recovery](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:331) writes extra HP/MP. A sophisticated goal selector could appear successful through different player rules. | Resolve one necessary action through the normal player path and truthful outcome before expanding dependent goals. |
| HIGH: persistent life incomplete | Metadata stores PlannerState, but bounded production searches found no RecordPlannerState consumer restoring runner purpose. [Generation-based recreation](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:827) clears old execution caches rather than restoring intent. | Keep generation protection. Add/reconcile a small durable purpose only after one goal loop works; do not serialize stale requests or object IDs. |
| MEDIUM: maintainability/evidence drift | A separate GOAP step executor exists beside the bound production executor; richer belief code is not demonstrably consumed by the main decision path; proposal ownership disagrees with control docs. | Label experimental/unbound code accurately; retire duplicate production responsibility when its replacement is proved. Do not delete useful code merely to reduce class count. |
| MEDIUM: false autonomy claims | [Field-test setup](/root/aaemu-dev/AAEmu.IntegrationTests/E2e/BotFieldTestV1Tests.cs:189) logged in a player without bot enrollment; bridge and scenarios can supply state/targets. E-48's silence is not a failed intelligence decision. | Test a legitimately configured autonomous bot and disclose every supplied advantage. Preserve debugging tools without using them as autonomy evidence. |

Recent duplicate/retry/generation fixes are **not repeated here as unfixed defects**. E-60…E-62 improve execution reliability. The scheduler's fault response protects the server; it does not teach a bot how to find another source or abandon an impossible goal.

Necessary MMO complexity includes asynchronous skills, entity lifetimes, resource/loot/property rights, transactions and variable population cost. These cannot be collapsed into planner flags. Distributed AI infrastructure, generic event buses and universal memory frameworks are speculative solutions until the present process has a measured bottleneck.

## H. Gameplay system dependencies

This is a bounded coverage assessment, not an audit of every packet/zone. “Server exists” identifies ordinary machinery; it does not certify all cases. Actor methods identify access, not autonomous choice. Existing [capability inventory](/root/aaemu-dev/scorecard-explorations/mechanics/playerbot-api-architecture-2026-09-18.md:235) provides historical detail; current methods were checked where used below.

| Domain | Ordinary game-server functionality | Bot access | Decision support and evidence boundary |
|---|---|---|---|
| Quests | QuestManager, CharacterQuests, templates, counters, reports | Discover/accept/advance/talk/turn-in actor methods | Supported patterns and enabled bootstrap; recorded turn-ins, not all quests or self-selected progression. |
| Movement/navigation | World/transform and navigation/path facilities | MoveTo, NavigateTo, unit-target travel | Travel brains/routes exist; route reachability and recovery need zone-specific live proof. A known spawn coordinate is not a current opportunity. |
| Combat/abilities | Skill/Unit/buff/combat services | Cast, AutoAttack, target/stop | Combat brains/trees exist; no broad class/GCD parity claim. Recorded kills establish named combat, not tactical quality for all levels. |
| Inventory | Ordinary containers and item persistence | Use, deposits/withdrawals, bag observation | Holdings are observed; deliberate bag management is partial. Seeded items do not prove acquisition. |
| Equipment | Ordinary equipment slots/item movement | Equip and repair | Equipping is available; general comparative upgrade policy and level/item eligibility coverage remain unverified. |
| Loot | Ordinary containers/rights/transfer paths | Loot, pack pickup; LootBrain/ledger | Recorded loot and quest credit; exact recipient, rights and bag-capacity handling need stronger observation before general claims. |
| Vendors | NPC goods and player buy/sell paths | Buy/Sell/Repair | Narrow purchase/sale access exists. Merchant discovery, concrete stock/payment binding and full transaction parity are not a livelihood proof. |
| Gathering | Doodad phase/functions, skill effects | Harvest/Interact | Phase-driven crop selection exists; current harvest timing/effect path needs normal-player convergence. |
| Farming | Player Doodad creation, crop phases | Plant/Harvest, needs-farm branches | Scripted/seeded crop loops and memory exist. Ordinary seed acquisition, legality, repeat sustainment and stolen/contested crop handling remain open. |
| Crafting | CraftManager and CharacterCraft | Craft request/tick handling | Action exists; exact material/workbench acquisition and selection of useful recipes are distinct missing proofs. |
| Player trading | TradeManager and transaction handlers | Offer/put-up/lock-confirm | Action seams exist; autonomous partner discovery, valuation and agreement are not established. |
| Economy | Money, auction, mail, specialty services | Auction post/buy, bank, specialty sale subset | Server transactions are not an autonomous market. No demonstrated production/consumption policy or price-responsive population. |
| Progression | XP, level/Ability/ActAbility and quest reward state | Mostly effects of ordinary quests/combat | Narrow progression is recorded; skill allocation and broad equipment/resource progression remain incomplete. |
| Mounts/transport | Mate/Slave/Transfer services | Mount/dismount/board/unboard and some pack loading | Access exists; autonomous transport selection, summon coverage and route integration are not demonstrated. |
| Death/resurrection | CharacterResurrection and resurrection data | Scheduler death-watch invokes ordinary resurrection | Server recovery/test coverage exists; persistent goal resumption and corpse/route consequences need live proof. [Death-watch](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:599). |
| Zones/world transitions | Ordinary worlds/instances/region/spawn services | Actor travel and transport subsets | World presence is real; general cross-zone/instance planning and continuity are unknown at this scope. |
| Social/group | Team and Expedition services | Invite/accept/follow/assist; Expedition actions | Primitive cooperation exists. Leave/kick team packets inspected below are log-only stubs; relationships and autonomous group purpose are not established. |
| Homestead/property | HousingManager, item designs, Doodad/property state | BuildHouse and GOAP housing chain | September claim→construct loop is recorded live in named provisioned scope. Ordinary acquisition, first-owned-plot journey, tax/harvest/replant and human acceptance remain separate. |

Concrete access anchors: [actor interface](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs:193), [economy/plant/build methods](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:2939), [craft](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:3916), [Team leave stub](/root/aaemu-dev/AAEmu.Game/Core/Packets/C2G/CSLeaveTeamPacket.cs:7), [Team kick stub](/root/aaemu-dev/AAEmu.Game/Core/Packets/C2G/CSKickTeamMemberPacket.cs:7). A missing actor method can often use an existing ordinary service; do not infer the whole mechanic is absent or implement it twice.

### What genuinely needs client research

**No client RE is needed** to establish scheduler membership, actor identity, stored quest progress, actual bag counts, existing goods tables or current Doodad phase. These are server observation/integration questions. Missing party leave behavior is a known server implementation gap, not evidence that its already-decoded packet must be rediscovered.

Conditional research candidates, only after inspecting the normal server path and target-build data:

| Question | Why source/data might be insufficient | Target and what an answer changes |
|---|---|---|
| Which NPC dialogue/menu or scripted cinematic event is necessary for an unsupported quest pattern? | A server objective row can name the condition without implementing the initiating client interaction. This is not assumed for all talk quests. | 1.2 r208022 Lua/UI and packet trace around that exact quest. Determines an ordinary interaction/event implementation, not a bot-only quest shortcut. |
| What are the normal start/cancel/complete effects and timing of a specific harvest interaction? | Current actor packets approximate the action; source may not establish client-observable cancellation/animation sequencing. | Compare normal Skill.Use/Doodad path first, then capture a human harvesting/cancelling the named crop. Changes asynchronous execution/result observation, not template maturity guesses. |
| What client asset or protocol knowledge is missing for a demonstrated transport/navigation failure? | A particular route could lack mesh/attachment/transfer support; not every no-path result is a client mystery. | Relevant geodata/navigation assets or boarding/movement packets for the exact build. Changes a route/transport capability only after separating server routing/data defects. |

Loot-rights rules, labor costs and proximity legality may also have emulator gaps, but require a bounded comparison of authoritative code/data first. Only unresolved retail semantics justify a further capture. None of these research tasks is launched here.

## I. Testing strategy assessment

The repository's tiered testing contract, decision traces, actor audits and isolated lanes are useful. Their scope must match the claim. A long run that completes a supplied quest tests execution and some robustness; it cannot establish independent goal choice if the test supplied the quest, target, items or next wake. A short run can establish one decision boundary more cleanly.

Keep four evidence layers separate: deterministic contracts; simulated decision/execution scenarios; authenticated ordinary live server behavior; actual human experience. Fake actors and event injection are good diagnostic tools. They must not carry a live-autonomy or human-feel verdict.

| What to measure | Useful evidence |
|---|---|
| Goal choice | Candidate goals, observed facts/coverage, eligibility and rejection reasons, selected purpose; compare controlled changes in health, danger and opportunity without assigning a winner. |
| Plan validity | Exact prerequisites, inputs, quantities, costs and concrete targets. Predicted effects remain predictions until the server changes. |
| Action success | Request ID joined to actual quest/XP/inventory/entity/transaction change attributable to that bot. Mere packet emission is insufficient. |
| Recovery/replanning | Cause of refusal, fresh world observation, revised task/goal and preserved or explicitly abandoned purpose. Distinguish waiting on a cooldown from unreachable or exhausted resources. |
| Independent progress | Self-selected goal ID joined to start/effect/completion and subsequent goal choice; disclose all setup and post-start interventions. |
| Repeated failure/stuck states | Same failed target/reason, time without useful progress, and whether new evidence justified retrying. Movement distance alone is not success. |
| Persistence | Loaded gameplay truth plus restored purpose after restart, fresh entity bindings, no duplicated reward/effect. Saved JSON alone is insufficient. |
| Resource efficiency | Gold/labor/item inputs, outputs and failed attempts per completed useful outcome. No invented balances or rates. |
| Scalability | Decision/observation/planning/execution cost separately, queue delay, pressure, allocations and per-bot progress under a stated population. Cheap idling is not scalable productive life. |

Use replayable snapshots and deterministic fault injection for changing opportunities, stale actors, rejected transactions and interrupted movement. Inject observations/failures through the production decision interfaces; do not replace the selector with a scenario's own brain. Record time when testing waits, and capture unknown facts rather than converting them to false.

Then validate each short slice live with the production scheduler. Run an expensive soak only after the slice works, or for a named load/persistence hypothesis. Repeating a 30-minute lane before finding the first missing handoff wastes time; E-48 is the example. Preserve time for later 30–60-minute life tests once independent choices actually work.

The latest ledger reports test successes for E-60…E-62 and also 13 full-suite failures considered pre-existing by the worker. This assessment did not rerun or independently establish that attribution. No fresh green full gate, deployment or human signoff is asserted. [Testing contract](/root/aaemu-dev/Docs/TestingPlan_en.md:1), [E-62](/root/aaemu-dev/.omp/state/EVIDENCE.md:1738).

## J. Incremental target architecture and maturity stages

The target is a **small persistent player decision loop around ordinary server gameplay**. Retain one scheduler/execution boundary. Give one component responsibility for the current purpose and why it remains worthwhile. Reuse domain executors and the existing planner. Let population services control cost, not personal achievement. Logical boundaries are enough; this does not require new processes or a generic AI platform.

| Stage | Existing foundations | Missing work and gameplay dependencies | Observable completion and risks |
|---|---|---|---|
| 1 — Can Play | Ordinary Characters/services, actor requests, quest/combat/travel actions, narrow live quest/property loops, recent execution hardening. | Reliable enrollment; truthful Running/completion/refusal handling; necessary skill/interaction parity, rights, reachable routes and resource accounting. | A supported bot repeatedly performs ordinary gameplay and recovers without fixture repair. Named scope/variants and interventions disclosed. Risks: a test helper or bot-only recovery creates apparent success. |
| 2 — Can Decide | Activity eligibility, goal policy, bounded GOAP, reactive behaviors and traces. | One retained purpose, usable opportunities, consistent task ownership, meaningful alternatives and result-driven reevaluation. | Unassigned choices change appropriately with conditions; bot completes a chosen objective, adapts to failure and chooses subsequent useful work. Risks: driver supplies targets; multiple selectors fight; a planner predicts success without observing it. |
| 3 — Can Live | Gameplay persistence, metadata and schedule/home storage, current-run activation versioning. | Durable intent/preferences/knowledge with explicit invalidation; sustainable acquisition/consumption, restart recovery and limited demonstrated relationships. | Same identity resumes appropriate work after interruption/restart, balances real resources and retains demonstrated preferences. Risks: stale cache or metadata absence masquerades as a valid empty state; durable descriptions never affect decisions. |
| 4 — Living World | Operational density/pressure/proximity/fidelity and real shared-world transactions. | Multiple Stage-3 characters, contention/fairness, observable economic/social effects, and optionally narrowly reconciled abstract simulation. | Population creates persistent, conserved world consequences with no double execution when detail changes; ordinary players can observe/interact with them within measured cost. Risks: offline rewards invent economy, population manager scripts activity, or proximity to one player hides others. |

Each stage is a claim over stated supported gameplay, not a percentage or permission to bypass earlier proof. Stage 4 does not require every imagined A-Life feature. Start with detailed productive bots; add abstract simulation only for a measured scale problem and a mechanic with an authoritative reconciliation rule.

## K. Recommended next vertical slice

**One ordinary bot chooses useful progression, recovers when necessary, and finds different work when its chosen opportunity is unavailable.** Proposed research direction, not a created implementation task or replacement for the current dispatch decision.

Start with one legitimately configured headless bot using the existing production creation, scheduler and Character paths. Give it no assigned quest, destination or target. Use one zone where supported quest offers, travel and combat are already available; do not expand the action library for this proof.

The bot chooses among three concrete options: recover when its actual condition requires it; progress an eligible discovered quest when there is a usable opportunity; hunt a viable nearby target for ordinary progression when quest work is unavailable. Simple deterministic rules are sufficient. Quest choice and hunting are means of progression, not fixed lane commands. If the supported zone has no two usable alternatives, name the missing mechanic/observation instead of stocking or injecting a fake opportunity.

### Acceptance

1. Production bot enrollment occurs once. First and later turns belong to that bot; no manual Wake after START and no connected human Character is taken over.
2. Its record explains the choices from observed health/danger, quest eligibility, availability, ownership and reachability. No test-supplied quest/target IDs, teleport, reward event, state forcing or extra supplies drive the result. Normal documented starter inventory is disclosed.
3. It makes useful server-confirmed progress toward its own selected work: a normal quest objective/completion or attributable combat XP/loot. Progress is checked in ordinary state, not only trace strings.
4. When the selected opportunity disappears or becomes unavailable, it distinguishes that from a temporary wait, observes another option, and changes its task or explicitly abandons the goal. A deterministic version may inject the loss for diagnosis; the live version must disclose any such interference and cannot call that an unassisted-life proof.
5. An actual danger/health condition can interrupt progression. The bot subsequently resumes or rejects its earlier purpose from fresh state, without duplicate effects or a lost running request.
6. After completing or rejecting one opportunity it independently selects useful subsequent work. Decisions remain explainable and bounded; repeated failure is visible, not concealed by longer timeouts.

Evidence: per-bot wake/activation ID, observations and rejected alternatives, selected goal/task, actor request/terminal result, before/after Character and DB facts where durable, and a complete setup/intervention record. Begin with deterministic short tests of the same production decision path, then an assigned short isolated live run. A time bound may produce a honest incomplete result; it must not manufacture acceptance.

Restart/persistent identity and large-population behavior are later proofs. They are not smuggled into this slice. Existing request/generation fixes should be finished and reviewed before the live proof; this audit does not authorize additional code or a test campaign.

### Engineering direction by disposition

- **Must fix now:** observation/result contracts needed for the selected slice; one purpose/task owner; ordinary execution of the specific required mechanics. Preserve and verify the current request hardening.
- **Investigate next:** actual opportunity discovery/availability, competing decision ownership, and the smallest production choice path. Reconcile the GOAP/arbiter policy conflict explicitly.
- **Preserve as-is where sufficient:** bounded resource-aware planner, normal Character/service composition, existing scheduler/execution boundary, explicit unsupported quest patterns, diagnostic traces and configuration isolation.
- **Defer:** broad personality/relationships, market actors, off-screen production/combat, new zones/transport and planner algorithm comparisons until they unblock a demonstrated behavior.
- **Avoid:** another strategic brain or scheduler, per-bot threads, scripted teleport/resource help presented as autonomy, GOAP actions for every cooldown, speculative distributed infrastructure and default LLM decisions.

## L. Open questions

1. Which canonical decision owner is intended after reconciling the GOAP strategic checkpoint with later arbiter-first notes? Source shows both scopes; proposal titles do not settle authority.
2. Which naturally occurring eligible opportunities can the current supported zone supply without a driver-provided quest, target or surface? Static spawn/template knowledge and nearby live state must be joined, not conflated.
3. Which caller-relative rights and skill/interaction timings are already fully enforced by ordinary services, and which need a narrow engine correction? A bot adapter's approximation does not prove the engine lacks the rule.
4. Do actor/request/cached-state generation changes cover external queue access and remove/recreate races, not just the scheduler's first new turn? E-62 reports tests; a full concurrency review and live effect proof were not conducted here.
5. What durable purpose/knowledge does production currently consume on restart? Storage and a cursor serializer exist; end-to-end consumption remains unestablished in this scope.
6. Which current feature gates and population settings are enabled in the actual deployed server? Source wiring is not deployment configuration. No deployment was inspected.
7. What full-suite failures predate the exact final dirty tree, and what human-visible gameplay has been accepted? Worker attribution and historical exits are not a fresh baseline or human signoff.
8. For any future abstract simulation, which authoritative service owns a distant result and excludes detailed concurrent execution? No off-screen outcome contract was found in inspected population code.

## Answer to the final question

**Yes, several architectural foundations are correct. They need a clearer connected decision loop before they amount to autonomous player intelligence.**

1. **Successfully built:** ordinary playable Character composition, broad action adapters, real server quest/property effects, a bounded planner, scheduling/population controls and strong debugging seams. Recent retry and activation-version fixes improve reliability rather than add another brain.
2. **Possibly misunderstood:** logged-in test characters are not automatically autonomous bots; planner existence is not independent goal choice; saved character/configuration state is not durable purpose; distant dormancy is not A-Life; a successful supplied sequence is not self-acquisition.
3. **Boundaries to clarify:** who owns the player's current purpose, who owns its running task, what observations establish an available opportunity, and who confirms actual progress. Keep personal goals separate from population cost control.
4. **What must change:** connect those existing pieces, reconcile decision ownership, preserve accepted asynchronous work, and remove the needed gameplay approximations one proven mechanic at a time.
5. **Build or validate next:** one ordinary bot choosing and revising useful progression from real conditions, with observed effects and no driver-provided next steps.
6. **Avoid prematurely:** more quest-specific rescue patches, additional planners/runtimes, broad economic/social simulation, offline rewards and claims of a living population before a small number of detailed characters can decide and sustain useful work.

## Inspection and provenance

Documentation reviewed: repository instructions/current task-state contract and decisions; current/relevant sections of START-HERE, PROJECT-CONTROL, STATUS, SCORECARD, ROADMAP and TestingPlan; GOAP architecture; brain boundaries/wake ownership/roadmap; capability and conversion investigations; prior subsystem synthesis and recent E-48…E-62. Historical paragraphs retain their original evidence boundary. Source investigation was bounded to the production wiring and cited adjacent bot/gameplay services, not every subsystem implementation. External downloads were research copies outside the repository.

Read-only archaeology invoked through the existing Release MCP executable with list_sources {}, list_tables {}, and read_file for AAEmu.Game/Program.cs, AAEmu.Game/Core/Managers/Bots/Goap/BotWorldState.cs, and AAEmu.Game/Core/Managers/Bots/PlayerBotRuntime.cs. All returned ok=true, truncated=false; bounds not hit. Catalog: source_id compact.sqlite3, path /root/aaemu-dev/AAEmu.Game/Data/compact.sqlite3, version 1.2 r208022, 679 tables, generation 2026-10-08T08:28:17.6792658+00:00. Source reads returned exact paths, source_id/version null as returned. Confidence exact for read code/catalog; conceptual architecture recommendations are interpretations, not live facts. Canonical DB md5 rechecked **78b3bdbf038db3b927056106efdf91af**. [Archaeology acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

External repository trees were not truncated. Playerbots and OpenXRay revisions are pinned in sections D/E; a few initially assumed remote paths returned 404 and were corrected from those inventories. No missing remote file was treated as an absent feature. The original A-Life discussion uses the named developer's published interview; current community engine code is separately labeled.

Snapshot SHA-256 witnesses for mutable source at inspection:

| File under repository root | SHA-256 |
|---|---|
| AAEmu.Game/Program.cs | ef19721fb20b384a2bdeb17dafbc4a02de0265605665dcafb0b149b8a1ef0751 |
| AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs | d4f396363e29226943146a949a0298d5cb11ec181cfc0bbb134ab9d94f4b21e5 |
| AAEmu.Game/Core/Managers/Bots/PlayerBotRuntime.cs | 12bca46780a9ea5463845c97621808c8d57b3e3fa17f8ef59c006c87a684d71c |
| AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs | 07ad6c4540c8c4e4822d20f1a28d24f81d1f968aa2a90b704c5587f7cc866bf0 |
| AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanRunner.cs | 71f1ce5301ffbcd38dd84f69d6596d05df9593fce2cb789fb1477a18ff4f2da9 |
| AAEmu.Game/Core/Managers/Bots/Goap/BotWorldState.cs | de0f78497ca72970e03954dc852ae6087c647e3b9cc7957ccaabd572a0ef15d0 |

Checks for this deliverable: report structure, citation/link targets and source witness review. No compilation, unit/integration suite, full gate, live campaign, commit, push, deployment, authoritative status update or H acceptance. Only this exploration report was created.
