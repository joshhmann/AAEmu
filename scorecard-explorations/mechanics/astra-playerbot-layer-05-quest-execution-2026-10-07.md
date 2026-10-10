# PlayerBot quest execution behavior audit

Author: Astra. Layer 5. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Execute the chosen quest opportunity using reusable gameplay capabilities. Maintain leg-local intent and pending-request state across wakes, observe real progress, and recover from a named refusal. Quest behavior should not implement separate movement/combat/loot laws or declare world unavailability from wake counts.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| Generic plan/leg loop and live credit predicates | KEEP + HARDEN | Normal quest counters and shared proposals are sound. |
| BotBehaviorRuntime | REFACTOR | Tick lifecycle conflates running dispatched work with failure. |
| Static per-actor/per-target memory | MOVE OWNERSHIP | Belongs to the behavior intent/incarnation with targeted cleanup. |
| Combat/leash/give-up streaks | REPLACE WITH AUTHORITATIVE OBSERVATION | Counts cannot identify availability, legality, leash, or damage cause. |
| Loot-once terminal memory | REFACTOR | A rejection is not permanently exhausted loot. |
| Withhold/diagnostic seams | KEEP | Useful declared controls, never autonomy evidence. |

## C. What Is Good

[Leg loop](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:1208) shares one derived plan/funnel per quest and records enter/emit evidence. Dispatch uses the existing actor and selector. Objective-credit readers use the actual quest acts/counters. Keep these; the need is a truthful outcome contract, not replacing the whole quest executor.

## D. Main Problems

**HIGH — Running movement loses ownership at the runtime boundary.** [QuestBehavior](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:1506) explicitly returns WorkSelected=true for a Running pursuit/return/approach move so work suppresses ambient fallback. [BotBehaviorRuntime.Tick](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotBehaviorRuntime.cs:134) instead requires a Completed request for landed=true and otherwise marks the result Failed. BotRoam receives false for dispatched asynchronous work and can arm fallback travel or run other branches. This is a concrete contract disagreement, not simply an architectural preference.

**HIGH — A terminal refusal becomes permanent loot-once state.** [DispatchLoot](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:4853) records LootedCorpses for terminal outcomes including Rejected. Transient range, claim, capacity, or legality failures are not successful acquisition or permanent exhaustion. Remembering attempt completion independently from resource disposition is essential.

**HIGH — Counted wakes are interpreted as world failure.** [Give-up processing](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:1265) counts unresolved target wakes; [AdvanceEnter](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:2647) suppresses advance after the threshold. Combat adds per-target and actor-wide no-progress counters. These can bound wasted work, but do not distinguish dormant spawn, foreign damage, engine refusal, or route obstruction. GiveUp is logged/reported, not a complete top-level abandonment or alternate-goal decision.

## E. Hidden Assumptions

Priorities encode a quest pipeline, not a player-wide preference. Comments, q251tgt trace keys and G-gates retain quest-251 lane vocabulary even where facts are generalized. Fixed wake thresholds depend on scheduler cadence. Static bounded maps clear whole populations when full. [WithholdTurnIn](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:1483) deliberately prevents selected work from dispatching; lane results must declare it.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: current objectives, actor terminal reasons, combat/target and inventory state. MISSING OBSERVATION: an owned-running status, per-failure retryability, actual target availability/leash and caller loot eligibility. UNKNOWN: precise causal interpretation of historical stall lanes; no fresh live run occurred.

## G. Ownership Problems

Runtime owns behavior status; leg executor owns pending request and recovery; actor owns terminal lifecycle. Move static target/stop/corpse/retry memory under one behavior identity rather than resetting unrelated bots. CROSS-LAYER QUESTION: which terminal loot refusals are temporary? DEFER TO: layer 8. Travel integration and threat primitives are layers 6–7.

## H. Generalization Risk

Longer travel and asynchronous interaction expose the Completed-only runtime mismatch. Dense/shared combat makes HP movement a poor self-progress signal. Full inventory or changing loot rights can permanently poison a corpse attempt. Different objective combinations inherit layer-4 flattening. Restart/incarnation replacement loses or reuses static state without an intent-aware recovery path.

## I. ArcheAge Reverse-Engineering Questions

No blocking CLIENT RE NEEDED finding. These contract/state bugs are visible in server code. Investigate a retail-specific objective event only after its server act and normal action path prove insufficient.

## J. Live Proof Needed

Run an ordinary quest requiring movement longer than several wakes, a kill, and loot. Introduce a normal temporary failure—full bag or contested ownership—then resolve it through normal play. The bot must retain the move, retry acquisition when eligible, and confirm server credit/turn-in. No teleport, forced credit, fixed target injection, or withheld completion in the accepted run.

## K. Recommended Changes

1. Treat a selected Running request as owned pending work, not failure; consume its terminal outcome on later wakes.
2. Separate attempted, acquired, exhausted and temporarily blocked loot dispositions.
3. Replace causal uses of streaks with named authoritative blockers; retain only bounded diagnostic/backoff use.
4. Scope leg memory to behavior/intent identity with targeted cleanup.
5. Publish blocked/waiting/completed outcomes to the single strategic owner.

## L. Do Not Touch

Keep the generic leg loop, shared selector/actor path, actual objective counters, named failure evidence, and explicit test controls. Do not migrate every leg to a planner.

## M. Verdict

**STRUCTURAL-ISSUE.** The executor uses normal gameplay and useful generic legs, but its host contradicts its asynchronous ownership contract and several caches convert temporary failure into persistent assumptions. Fixing these seams is higher leverage than adding another quest shape or watchdog.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0. **Canonical owner:** Per-intent quest task execution; engine owns progress and reward.

**First reviewable outcome:** P0.4: a selected Running move remains owned pending work and cannot fall through as a failed/unselected action.

| Phase | Work owned by this layer |
|---|---|
| P0 | Correct Running ownership and consume the exact eventual terminal; stop treating rejected loot attempts as acquisition. |
| P2 | Return waiting/blocker/completion/abandonment to the strategic owner rather than merely logging give-up. |
| P3 | Use authoritative causes and scoped leg memory; retain budgets only for bounded work/backoff. |
| P4 | Execute the complete supported objective projection with shared acquisition capabilities. |

**Dependencies:** Request integrity in layer 1; P1 facts; P2 owner; layer 4 objective requirements. Actor interruption ends the request; continuing the quest creates a new one.

**Live stopping point:** A real pursuit survives successive wakes; a temporary refusal is reconsidered and a completed quest leads to a useful next goal.

**Preserve:** Generic leg loop, ordinary actor dispatch, actual objective counters and named diagnostics.

**Defer:** Another stall counter, quest-specific permanent retry memory or planner-per-leg conversion.


## Cross-layer notebook

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|
| 1 — Scheduler/runtime/lifecycle | STRUCTURAL-ISSUE | Tick-marshalled execution and terminal request lifecycle | Enrollment and embodiment continuity | Lifecycle coordinator; scheduler owns timing; actor owns requests | Current embodiment + explicit next scheduling disposition | Layer 2 — observation |
| 2 — Observation/perception/world-state | STRUCTURAL-ISSUE | Bounded engine reads and pure deterministic projections | Incompatible eligibility and absence semantics | Common observer adapter over ordinary gameplay; belief interprets | Caller-relative opportunities + valid coverage/resource availability | Layer 3 — arbitration; rights/gates deferred to 8–9 |
| 3 — Goal arbitration/top-level ownership | STRUCTURAL-ISSUE | Deterministic legality-first selection on the shared execution path | Elected activity does not own execution or persistent intent | One strategic owner; modules supply goals; executors own bounded tasks | Opportunity readiness + intent/execution disposition | Layer 4 — quest interpretation; route/recovery/planning deferred to 6/10/11 |
| 4 — Quest interpretation | HEALTHY-BUT-NARROW | Server-derived objective facts and explicit unsupported vocabulary | Single-objective/single-source reduction | Quest projection over server quest components | All outstanding objectives and viable acquisition alternatives | Layer 5 — execution |
| 5 — Quest execution | STRUCTURAL-ISSUE | Shared actor dispatch and live objective counters | Async ownership and retry state conflict | Per-intent quest behavior; engine owns progress | Terminal outcome disposition and real blocked cause | Layer 6 — travel |

## Evidence provenance

Archaeology list_sources {}; search_files {root:game-source, glob:QuestBehavior.cs, pattern:CountGiveUpWake|LootedCorpses.Add|CountCombatStallWake|LastPursuitIssue, limit:40}; 27 matches, no truncation, 2026-10-07T17:16:52Z. source_id game-source; path /root/aaemu-dev/AAEmu.Game; search version null; catalog fork develop. Textual/code evidence only. DB MD5 78b3bdbf038db3b927056106efdf91af (1.2 r208022). [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

