# PlayerBot resource acquisition, economy, homestead and GOAP audit

Author: Astra. Layer 11. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Own bounded prerequisite planning for useful player goals: obtain inputs, choose actual sources, travel, spend normal resources, observe results, and reconsider when opportunities disappear. Farming and homestead are activities, not independent strategic brains.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| GoapPlanner | KEEP + HARDEN | Bounded search with cost/resource dominance; honest limits. |
| BotWorldState / provider | REFACTOR | Symbolic facts lose knowledge coverage and concrete requirements. |
| GoapPlanRunner | KEEP + HARDEN | Checks observed effects; needs bound opportunities. |
| Farming / homestead actions | REFACTOR | Useful ordinary actor adapters, but default coordinates and recipe constants bind scenarios rather than discovered opportunities. |
| Production Tier 0 farming | KEEP + HARDEN | Acquires seed from a visible merchant and tracks real crop phases; remains a single commodity loop. |
| Fixture overrides | KEEP | Explicit opt-in in BotMemory separates seeded projection from production. |

## C. What Is Good

The planner caps search and does not claim universal optimality. It considers gold and labor in dominance entries; this is not a flags-only resource planner. The runner evaluates fresh projected state instead of declaring simulated effects successful. Production farming revalidates crop identity, ownership and phase, adopts scanned crops, and approaches observed merchants ([BotRoamStepExecutor.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2787)). Keep normal Buy/Plant/Harvest/Craft/Build actor calls and explicitly gated fixture overrides.

## D. Main Problems

**HIGH — Unknown facts can satisfy negative requirements.** [BotWorldState.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/BotWorldState.cs:100) compares required bits against Flags without checking the current Mask. The provider's stated unknown-field handling therefore cannot establish that a negative prerequisite is known true. This is an IMPLEMENTATION BUG, not a need for a larger planner.

**HIGH — Travel execution and observation can describe different places.** [FarmingActions.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/Actions/FarmingActions.cs:19) defaults to a fixed merchant coordinate, while NearSeedMerchant is measured against context.Memory.KnownSeedMerchantPos ([BotWorldStateProvider.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/BotWorldStateProvider.cs:121)). A travel request can finish while its predicted spatial effect remains absent. The same action interface does not receive the context that supplied the opportunity.

**HIGH — Boolean possession is too weak for material progression.** Saplings are projected by category while purchasing binds one template; material possession omits recipe quantities and identity. [HomesteadActions.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/Actions/HomesteadActions.cs:488) binds recipe 1001 rather than a resolved craft opportunity. Engine rejection remains authoritative, but repeated replanning cannot manufacture missing bindings.

**MEDIUM — Self-acquisition is narrow.** Tier 0 farming discovers a local merchant and one crop loop. Homestead contexts seed a housing destination with a fixed fallback ([BotRoamStepExecutor.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2746)). This does not establish design acquisition, scarce land selection, distant supply discovery or economic recovery.

## E. Hidden Assumptions

Fixed merchant/farm coordinates, potato and sapling identities, one material-pack recipe, known workbench/housing locations and starter design availability shape the current action graph. A stockpile or Known* position supplied by a harness must be recorded as an input. Fixture overrides are disabled by default; their existence does not prove production uses fabricated inventory.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: inventory identities/counts, recipe inputs, merchant packs/prices, labor/gold, housing ownership/construction and Doodad phase. MISSING OBSERVATION: reachable candidate merchants/workbenches, legal land placements and prerequisite source opportunities with observation coverage. GAME-DESIGN DECISION: which economy/progression activities this primitive player pursues and how far it searches.

## G. Ownership Problems

One strategic owner chooses the activity. GOAP may solve that activity's bounded prerequisite chain; it should not introduce another player priority stack. Context must carry the same bound target used by travel and interaction. Engine services own transactions and legal construction; symbolic effects describe predictions only.

## H. Generalization Risk

Different inventory can satisfy broad material flags with unusable items. Different zones invalidate coordinate defaults. Missing design, workbench, gold or free land can leave no useful plan. Multiple bots compete for crops and land; observed preconditions expire. Restart discards current contexts unless persistence explicitly reconstructs them (deferred to layer 12).

## I. ArcheAge Reverse-Engineering Questions

No client RE is justified for the defects above. Recipe, inventory, merchant and construction semantics are represented in server services/data. CROSS-LAYER QUESTION: are all placement restrictions implemented accurately for this client version? DEFER TO: housing gameplay archaeology only after the normal server path yields a concrete discrepancy.

## J. Live Proof Needed

Start several ordinary characters with normal starter inventory and modest funds near different towns. Let them discover sellers, buy inputs, find legal soil, plant, wait, harvest and fund another cycle. Then attempt an explicitly supported homestead prerequisite chain with no stocked design/materials or seeded destination. Introduce depleted funds, occupied land and another harvester. Record the first unsupported source or transaction, inventory/labor/gold deltas and observed outcome; do not call a partial potato loop generalized economy.

## K. Recommended Changes

1. Make knowledge coverage part of prerequisite evaluation and search-state identity.
2. Bind concrete merchant/site/workbench opportunities once, then use the same binding for costs, travel, dispatch and observed completion.
3. Project exact item quantities and recipe requirements only for the currently selected chain; replace broad possession flags where they claim craft readiness.
4. Give absent prerequisite sources an explicit disposition: discover, acquire, choose another goal, or report unsupported. Preserve the bounded planner.
5. Use the production farming loop as a self-acquisition field lane across towns and contention; remove supplied stock/locations from autonomy evidence.

## L. Do Not Touch

Search bounds, resource-aware dominance with its documented limitations, observed completion checks, real crop phase checks, normal transaction calls, category helpers for discovery, and the explicit fixture-override gate. Do not add a second planner or expand the economy action catalogue before bindings and observations work.

## M. Verdict

**STRUCTURAL-ISSUE.** There is a credible bounded planning foundation and a locally useful farming loop. The gap is between symbolic prerequisites and executable, discovered opportunities: coverage, identities, quantities and destinations do not consistently describe the same world fact. More actions would multiply these mismatches. Fix the small projection/binding contract before claiming homestead or economic autonomy.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P1 / P4. **Canonical owner:** The retained bounded planner under one strategic intent, using normal economy/property services.

**First reviewable outcome:** P1.1/P4.2: make one plan use known requirements and one concrete source binding consistently in prediction, travel and observed completion.

| Phase | Work owned by this layer |
|---|---|
| P1 | Correct unknown-mask applicability and distinguish exact requirements from broad discovery categories. |
| P2 | Plan prerequisites for the assigned goal; do not retain an independent homestead strategic chooser. |
| P4 | Bind exact items/counts/recipes and real merchant/site/workbench opportunities; close one acquisition-to-repeat chain. |
| P6 | Expand to another supported town/chain only after the first survives ordinary shortages and refusal. |

**Dependencies:** P1 facts, P2 goal owner and P3 performed capabilities. Earned design/eligibility/tax and property gaps stay explicit; no stocked merchant or inventory shortcut.

**Live stopping point:** An unstocked supported character can obtain its inputs, perform the activity and replenish; missing sources produce a useful alternate goal or honest unsupported disposition.

**Preserve:** Search bounds, resource-aware dominance, observed completion, real crop phases and fixture opt-in.

**Defer:** New GOAP actions, another planner or full homestead/trade breadth before existing bindings work.


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

## Evidence provenance

Read-only archaeology: list_sources + search_files(root=game-source, glob=**/BotWorldStateProvider.cs, pattern=Override|mask|MaxHp|KnownSeedMerchantPos, limit=40): 22 textual matches, not truncated, 2026-10-07T17:24:33Z. Source path /root/aaemu-dev/AAEmu.Game, catalog version develop; search version null. Canonical DB md5 78b3bdbf038db3b927056106efdf91af, target 1.2 r208022. Source/data evidence only; no live/client/H proof. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

