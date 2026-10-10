# PlayerBot quest interpretation audit

Author: Astra. Layer 4. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Translate authoritative quest components and current objective state into bounded goal opportunities. Explain supported and unsupported requirements, preserve conjunctions/alternatives, and describe source capabilities without deciding global priorities or executing gameplay.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| QuestFixtureRow | REFACTOR | Data-derived adapter is useful; one row flattens multiple objectives and sources. |
| Source memoization/invalidation | KEEP + HARDEN | Static derivation can be cached; partial/unloaded results need explicit validity. |
| PatternCatalog | KEEP + HARDEN | Bounded capability vocabulary; declared verb sets must match actual dispatch. |
| VerifiedVerbRegistry | KEEP + HARDEN | Useful rollout evidence ledger; generic green status is not universal gameplay proof. |
| QuestDirector | KEEP + HARDEN | Stateless local plan construction; unknown/partial interpretation needs explicit readiness. |

## C. What Is Good

No quest-ID comparison drives the production classification. [Derivation](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/QuestFixtureRow.cs:366) reads actual QuestAct templates, Supply, reporter, and group identity. [PlanWake](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestDirector.cs:245) derives each active quest once per wake. Unsupported source/skill facts produce named failures. Keep group matching delegated to QuestManager.

## D. Main Problems

**HIGH — A quest is reduced to its first objective.** [Objective classification](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/QuestFixtureRow.cs:434) chooses the first CountsAsAnObjective; independent FirstAct calls also pick one gather/use/interaction/supply. The normal [Quest](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Quest.cs:43) retains objective counters and component state. This adapter cannot represent several unfinished objectives or their dependency/order. A supported first act does not establish full quest support.

**HIGH — Source possibility becomes a chosen acquisition method.** [ResolveDropSource](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/QuestFixtureRow.cs:657) prefers a default loot pack and lowest NPC/pack IDs, returning one prey. That is deterministic but not evidence of available, reachable, level-appropriate prey. Doodad alternatives are retained, but NPC alternatives are lost. [ResolveDoodadUseSkill](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/QuestFixtureRow.cs:785) picks the first binding across template phases; actual current-phase interaction belongs to the live capability adapter.

**MEDIUM — Evidence gates are broader than their proofs.** [Registry](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/VerifiedVerbRegistry.cs:1) assigns generic green verbs from named narrow artifacts; [catalog](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/PatternCatalog.cs:31) deliberately omits AdvanceQuest, TurnInDoodad and AutoTurnIn despite possible dispatch. Unknown patterns retain bootstrap legs. This is a bounded rollout system, not a comprehensive assertion that every executed branch has parity.

## E. Hidden Assumptions

Examples and proof artifacts center on 251/3512 and a few later quest shapes, but derivation itself is generalized. A default loot-pack preference and lowest IDs secretly supply acquisition strategy. Reporter fallback to the acceptor assumes that is a useful target when no Ready reporter was resolved. Catch-and-keep-partial derivation can blur unloaded data and genuinely absent acts.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: component/act lists, objective counters, current component, group membership, Supply and reporter definitions. MISSING OBSERVATION: a complete outstanding-objective projection and a set of source alternatives with runtime availability. GAME-DESIGN DECISION: which legal acquisition method to prefer. Client RE is not needed for these represented facts.

## G. Ownership Problems

Keep interpretation here; move phase-relative skill binding to interaction observation and source choice to goal/acquisition selection. The evidence registry should own capability rollout scope, not replace engine legality. CROSS-LAYER QUESTION: does execution track all objective counters or merely the flattened row? DEFER TO: layer 5.

## H. Generalization Risk

Different multi-objective quests can look supported while only one requirement is served. Different zone/level/availability can invalidate the lowest-ID prey choice. A different phase can invalidate the template-wide skill choice. Restart correctness depends on re-reading server quest state, not this cache. Multiple bots must choose available alternatives rather than share a single data-selected prey template blindly.

## I. ArcheAge Reverse-Engineering Questions

No blocking CLIENT RE NEEDED finding. Unsupported acts must first be compared with their loaded server templates/handlers. Do not label an unsupported bot pattern as an unknown ArcheAge mechanic.

## J. Live Proof Needed

Let an ordinary bot discover and accept a quest with at least two different outstanding objective families, then complete both through normal actions. Include an unavailable preferred prey and a legal alternate source. Verify server objective counters and completed quest state, without supplied targets/items or injected credit. A single-verb proof does not close this scenario.

## K. Recommended Changes

1. Project all outstanding objective requirements from current server quest state; explicitly reject partially supported conjunctions.
2. Return acquisition alternatives instead of one lowest-ID NPC source.
3. Separate static source capability from runtime availability and current-phase interaction binding.
4. Attach evidence scope/branch to verb rollout records; keep ungated branches explicitly unavailable to autonomy claims.
5. Preserve UNKNOWN/partial-load validity in cached derivations.

## L. Do Not Touch

Keep data-derived classification, named unsupported outcomes, memoization with invalidation, authoritative group membership, and stateless plans. Do not add a second quest state machine or rewrite ordinary Quest progression.

## M. Verdict

**HEALTHY-BUT-NARROW.** This is a useful server-data adapter for a limited vocabulary, not yet a general quest interpreter. The highest leverage is retaining all objective requirements and source alternatives while leaving gameplay effects and current progress in the ordinary Quest system.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P1 / P4. **Canonical owner:** A projection of canonical active Quest components/objective state.

**First reviewable outcome:** P4.1: expose all unfinished requirements for one already supported compound objective; refuse partial support explicitly.

| Phase | Work owned by this layer |
|---|---|
| P1 | Attach load/coverage validity and keep static source possibilities separate from live availability. |
| P3 | Delegate phase-specific skill/reach binding to the interaction capability; keep quest interpretation stateless. |
| P4 | Retain acquisition alternatives and objective dependencies instead of first-act/lowest-ID selection. |
| P6 | Verify different supported objective families and prerequisite states, not another hard-coded quest total. |

**Dependencies:** P1 facts, P3 executable capabilities and authoritative quest progress. Interpretation does not choose a live source itself.

**Live stopping point:** A compound supported quest advances from actual counters through useful source choices; an unsupported branch is visible before dispatch.

**Preserve:** Server-derived classifications, named unsupported vocabulary, group membership and cache reuse with valid invalidation.

**Defer:** A second quest state machine, universal quest support or client RE for existing quest data.


## Cross-layer notebook

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|
| 1 — Scheduler/runtime/lifecycle | STRUCTURAL-ISSUE | Tick-marshalled execution and terminal request lifecycle | Enrollment and embodiment continuity | Lifecycle coordinator; scheduler owns timing; actor owns requests | Current embodiment + explicit next scheduling disposition | Layer 2 — observation |
| 2 — Observation/perception/world-state | STRUCTURAL-ISSUE | Bounded engine reads and pure deterministic projections | Incompatible eligibility and absence semantics | Common observer adapter over ordinary gameplay; belief interprets | Caller-relative opportunities + valid coverage/resource availability | Layer 3 — arbitration; rights/gates deferred to 8–9 |
| 3 — Goal arbitration/top-level ownership | STRUCTURAL-ISSUE | Deterministic legality-first selection on the shared execution path | Elected activity does not own execution or persistent intent | One strategic owner; modules supply goals; executors own bounded tasks | Opportunity readiness + intent/execution disposition | Layer 4 — quest interpretation; route/recovery/planning deferred to 6/10/11 |
| 4 — Quest interpretation | HEALTHY-BUT-NARROW | Server-derived objective facts and explicit unsupported vocabulary | Single-objective/single-source reduction | Quest projection over server quest components | All outstanding objectives and viable acquisition alternatives | Layer 5 — execution |

## Evidence provenance

HEAD/dirty baseline above. Archaeology: list_sources {}; search_files {root:game-source, glob:QuestFixtureRow.cs, pattern:FirstOrDefault|ResolveDropSource|catch|ResolveDoodadUseSkill, limit:40}; 12 matches, no truncation, generated 2026-10-07T17:15:46Z. source_id game-source; path /root/aaemu-dev/AAEmu.Game; search version null, catalog fork develop. Confidence textual/code; not live/client/H. Canonical DB MD5 78b3bdbf038db3b927056106efdf91af, version 1.2 r208022. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

