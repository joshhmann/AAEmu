# PlayerBot shared-world and multi-bot semantics audit

Author: Astra. Layer 13. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Make independent citizens share actual engine resources and ownership rules, react to competition, and cooperate through normal parties. Population operations control server cost and embodiment; player decisions control goals. Multi-bot correctness includes isolation of intent and memory, not only queue throughput.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| PopulationDirector | KEEP + HARDEN | Central operational fidelity/pressure control; not gameplay coordination. |
| TransitionSafetyProbe | KEEP + HARDEN | Reads real danger/equipment, but group and save signals are incomplete. |
| Production party follow/assist | MOVE OWNERSHIP | Shared Team semantics are good; unconditional takeover bypasses player goal policy. |
| Shared static behavior stores | REFACTOR | Actor keys exist, but whole-store eviction couples unrelated citizens. |
| PopulationGoalCoverage | DEFER | Useful proposed metric contract; no production collector was found. |
| Bot-account ownership shortcuts | REPLACE WITH AUTHORITATIVE OBSERVATION | As identified in layer 2; account type is not team or loot ownership. |

## C. What Is Good

Population fidelity is centralized, density scans are bounded and combat safety reads Character.IsInBattle. Party invitations and membership use TeamManager rather than inventing a bot party. Actor-scoped keys already exist in travel and loot stores. These are good foundations; no separate reservation economy or bot-exclusive rights are needed.

## D. Main Problems

**HIGH — A busy population can erase another citizen's intent.** [Travel/TravelIntentStore.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Travel/TravelIntentStore.cs:110) clears all intents at its memory bound. [Loot/LootLedger.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootLedger.cs:70) clears all dispositions. Quest issue/streak maps also use global clears. A new bot/request can discard unrelated actors' history and induce repeated travel or corpse attempts. Keep bounded storage, but evict expired/retired actor entries rather than everybody's live intent.

**HIGH — Party membership installs another decision path.** [BotRoamStepExecutor.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:884) accepts any invitation, then treats any same-world live NPC leader target as an assist opportunity and casts fixed skill 18131 ([BotRoamStepExecutor.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:904)). Target hostility, rights, capability and the bot's existing goal are not established by this branch. Engine refusal limits illegal effects; it does not make the player decision plausible. Shared tactics should reuse the combat capability and suspend/resume the chosen intent.

**MEDIUM — Operational population control is not gameplay fairness.** [PopulationDirector.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PopulationDirector.cs:102) defaults activityResolver to null; density gating skips activity caps without a binding ([PopulationDirector.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PopulationDirector.cs:445)). A bounded source search found no explicit production resolver binding or PopulationGoalCoverage consumer. Its zero-throughput fairness result is 1 by definition ([PopulationGoalCoverage.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PopulationGoalCoverage.cs:24)), so even future wiring must report progress alongside fairness.

**MEDIUM — Safety probes approximate the signals their names promise.** [BotTransitionSafetyProbe.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotTransitionSafetyProbe.cs:48) treats every party as a human group, including bot-only parties, and reports saving=false. Conservative grouping may unnecessarily block dormancy; missing save state may permit an unsafe transition. Classify these as MISSING OBSERVATION, not client RE.

## E. Hidden Assumptions

Party leader intent is assumed worth following; fixed assist skill assumes a compatible character. Bot account type has been used elsewhere as friendly ownership evidence (layer 2). Population caps assume a truthful activity binding. Sparse lanes do not expose global-store eviction or simultaneous resource attempts.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: Team membership/leader, account classification, faction relation, current NPC combat state, loot distribution and resource ownership. MISSING OBSERVATION: canonical chosen activity and its disposition, save-in-progress status, and why a contested resource attempt failed. GAME-DESIGN DECISION: whether a citizen accepts a stranger's invitation and how it balances social versus personal goals.

## G. Ownership Problems

Engine Team/loot/Doodad systems own actual rights. The player goal owner decides party participation and suspended intent. Combat owns assist execution. PopulationDirector owns fidelity and pressure. Actor lifecycle owns memory retirement. No population manager should award quest credit or reserve resources outside normal gameplay.

## H. Generalization Risk

At higher population, shared bounds erase live memories. Different leaders/levels expose fixed-skill assistance; an invitation can override gathering/progression. Bot-only parties affect fidelity safety. Competing bots select the same observed NPC/crop, so legal-at-observation becomes unavailable at dispatch. Restart introduces fresh identities and stale memory generations (layer 12).

## I. ArcheAge Reverse-Engineering Questions

No new client RE is required for bot isolation, grouping or operational caps. Retail party loot policy remains the conditional engine question recorded in layer 8; repeat neither an invented rule nor a bot-side approximation here.

## J. Live Proof Needed

Run several ordinary bots and one human around the same naturally spawned mobs, public crops and merchant. Include same-party and unrelated contenders. Invite a bot during its own acquisition goal, change leader targets, leave/disband and kill/despawn a selected resource first. Verify canonical reward recipients, re-observation/reselection, resumed personal intent and no cross-bot memory reset. A pressure storm proves scheduling capacity only; this field lane must show actual independent progress and shared-world legality.

## K. Recommended Changes

1. Replace whole-store eviction with bounded actor/generation/expiry retirement that preserves other live intents.
2. Make party participation an explicit goal policy and reuse normal combat/travel for follow and assist.
3. Bind the canonical activity into operational density metrics; report actual progress separately from fairness.
4. Derive human-group membership from Team members and add a real save-transition signal where the lifecycle needs it.
5. Prove competition through normal engine attempts and observed failure causes; do not invent bot-only resource ownership.

## L. Do Not Touch

TeamManager paths, centralized fidelity ownership, bounded density scans, pressure instrumentation, authoritative combat/trade-pack checks and per-actor keys. Keep the normal engine as contention arbiter.

## M. Verdict

**STRUCTURAL-ISSUE.** The operational population system is substantially stronger than shared gameplay coordination. Citizens can already occupy ordinary teams and resources, but party takeover and global memory eviction violate independent intent. Operational storms and density limits cannot prove fair progression or realistic competition; those require truthful rights and stable per-player state.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P2 / P5. **Canonical owner:** Engine rights/Team semantics; each player intent owner; population director for server cost.

**First reviewable outcome:** P5.3: retire only stale/expired actor-generation entries; hitting a bound must not clear other live citizens' intent.

| Phase | Work owned by this layer |
|---|---|
| P2 | Make party acceptance/following explicit policy and suspend/resume purpose through the same owner. |
| P3 | Reuse combat/travel for follow/assist rather than fixed-skill independent loops. |
| P5 | Replace global memory eviction; derive real human group/save safety and canonical activity/progress metrics; prove ordinary contention. |
| P6 | Scale only from measured independent progress and query/tick costs; retain honest fairness denominators. |

**Dependencies:** P0 integrity, P1 rights/threat, P2 intent and a proved P4 single-bot loop. Contention is resolved by normal engine attempts, not bot-only reservations.

**Live stopping point:** Competing citizens acquire legal rewards, react to losing opportunities and retain independent purpose; no actor's cleanup resets another actor.

**Preserve:** TeamManager, centralized fidelity, bounded density scans, pressure instruments and actor keys.

**Defer:** Guild/society planning, new LOD/scaling machinery or operational storms as gameplay-autonomy proof.


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

## Evidence provenance

Read-only archaeology list_sources + search_files(root=game-source, glob=**/PopulationDirector.cs, pattern=Activity|activity|Materialize|Dematerialize|Conflict, limit=40): 0 matches, not truncated, 2026-10-07T17:29:27Z; local source reads provide positive findings. Source game-source /root/aaemu-dev/AAEmu.Game, search version null, catalog fork develop. Canonical DB md5 78b3bdbf038db3b927056106efdf91af, 1.2 r208022. Code evidence only, not live/client/H. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

