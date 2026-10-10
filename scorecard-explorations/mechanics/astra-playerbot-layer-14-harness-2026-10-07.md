# PlayerBot harness, bridge and evidence architecture audit

Author: Astra. Layer 14. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Observe and reproduce production player behavior, diagnose the first failure and disclose every intervention. Rig/scenario tools may isolate engine semantics; autonomous field proof must leave goal choice, discovery, travel, acquisition, recovery and continued scheduling to the production bot.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| Live scheduler/executor observation | KEEP + HARDEN | Uses the actual actor trace; completion witnesses need per-bot correlation. |
| Teleport / stock / withhold / event controls | KEEP + HARDEN | Useful diagnosis when explicitly labeled, excluded from autonomy claims. |
| Scenario-specific gameplay interpreters | COLLAPSE | Reuse production capabilities; retain authored scenarios as test inputs. |
| Scenario world materialization fallback | REMOVE from autonomy proof | Teleport and direct spawning supply an opportunity the bot did not find. |
| Fixed quest/commodity lanes | KEEP + HARDEN | Regression evidence for named seams, not general player capability. |
| Restart byte checks | KEEP | Storage proof with a separate resumption acceptance boundary. |

## C. What Is Good

The quest lane records boundary snapshots and stops when travel fails rather than always teleporting over it. Production farm observation explicitly reads the executor's existing actor trace ([BotDriveBridge.cs](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:4233)). Rejects and completed effects can be distinguished. Withhold is default-off and named E2E-only. Many source comments disclose seeded inputs; preserve that honesty.

## D. Main Problems

**HIGH — “Live world” can still mean harness-supplied ecology.** [BotDriveBridge.cs](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:1099) resolves known template IDs, teleports to their spawner, waits, then directly creates an NPC if normal availability does not resolve ([BotDriveBridge.cs](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:1153)). Normal NpcManager construction proves factory/interaction compatibility, not discovery, travel or actual spawn availability. This is hidden scenario knowledge if the verdict omits it.

**HIGH — Completed chains can include externally performed prerequisites.** [Quest256LiveLaneTests.cs](/root/aaemu-dev/AAEmu.IntegrationTests/E2e/Quest256LiveLaneTests.cs:137) grants AddExp 1500, stages near known giver 3515 and directly accepts 254. Its 255 prerequisite is driven through accept/gather/advance/dialogue calls ([Quest256LiveLaneTests.cs](/root/aaemu-dev/AAEmu.IntegrationTests/E2e/Quest256LiveLaneTests.cs:228)). The later de-staged travel/accept/report seam is useful, but does not prove the bot self-earned this entire chain. Likewise [NeedsFarmEarnLoopE2eTests.cs](/root/aaemu-dev/AAEmu.IntegrationTests/E2e/NeedsFarmEarnLoopE2eTests.cs:85) supplies labor and three sellable potatoes, then selects a merchant via teleports/soil scans. It can prove liquidation-to-seed under those inputs, not self-acquisition of the surplus or discovery of the town.

**HIGH — Parallel scenario brains dilute production evidence.** LevelingLoopScenario constructs its own GameplayActor and interpretation loop ([LevelingLoopScenario.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/LevelingLoopScenario.cs:531)). BotScenarioRunner drives authored stages and gameplay events. Shared engine calls remain valuable, but passing those paths cannot validate the scheduler/arbiter/executor's intent lifecycle. Do not retain a second interpretation stack merely to protect green fixtures.

**MEDIUM — A wake witness is population-global.** Quest and farm bridge waits compare scheduler.TotalStepsRun before/after ([BotDriveBridge.cs](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:4476); farm at 4199). Another bot can satisfy the wait before the requested actor steps. Per-bot traces help diagnosis but do not repair the stepped flag's correlation. Manual Wake also does not prove ordinary enrollment and cadence continue without driving.

## E. Hidden Assumptions

Known quest/template IDs, initial teleports, AddExp, supplied labor/items, direct quest operations, event/kill seams, known merchants, forced world materialization, manual wake loops and withhold controls are test inputs. Distinguish pre-START setup from in-scope actions, and show both. Removing one START teleport does not erase prerequisite staging.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: per-bot actor requests/traces, quest/inventory changes and actual spawn state. MISSING OBSERVATION: an attributable scheduler wake/completion ID and a machine-readable intervention ledger. UNKNOWN: generalized 30–60 minute autonomy and human feel; no new live run or human acceptance occurred in this audit.

## G. Ownership Problems

Production runtime owns decisions and execution. Harness owns setup, observation and explicitly scoped fault injection. A scenario may request a goal through the production owner; it should not independently implement quest/combat/interaction semantics. Engine and DB state, not pass counters, own success.

## H. Generalization Risk

Multiple bots expose global wake misattribution. Different quests/zones break supplied identifiers and ecology. Unseeded inventory exposes bootstrap gaps. Natural spawn cooldown/ownership is bypassed by fallback materialization. Removing manual wakes exposes enrollment/cadence problems. Restart proves continuity only if the production owner resumes without rearming.

## I. ArcheAge Reverse-Engineering Questions

Client RE is not required to improve proof attribution or remove seeded knowledge. Real packet/client comparison is needed only for the conditional interaction/navigation/retail loot discrepancies already identified in layers 6, 8 and 9. Human behavior plausibility remains UNKNOWN until a human observes or completes the named scenario.

## J. Live Proof Needed

Run a production-enrolled ordinary bot for 30–60 minutes from disclosed normal starter state. Stop issuing Wake, teleport, stock, accept, event and spawn commands after START. Permit observation only. Require self-selected useful activity, real acquisition, travel, danger/recovery and continued intent; include a competing bot and a mid-run restart in separate extensions. Record first failure with per-bot request/wake IDs and authoritative DB outcomes. Maintain rig lanes for diagnosis, never aggregate them into an autonomy verdict.

## K. Recommended Changes

1. Add per-bot wake/request correlation and an intervention ledger to every live result.
2. Separate engine-seam, scheduler-seam, autonomous-field and human claims; name the exact unassisted interval.
3. Run authored scenarios through production intent/capability owners; collapse duplicate decision/interpretation paths incrementally.
4. Disable teleport/direct-spawn fallback and manual gameplay driving inside autonomous proof intervals; fail on missing opportunities.
5. Establish one observational 30–60 minute field lane before adding more quest micro-gates.

## L. Do Not Touch

Read-only status/trace/DB observations, first-failure snapshots, explicitly labeled setup/fault controls, engine regression fixtures and narrow de-staged seam proofs. Keep their evidence boundary; do not erase historical results or relabel source review as a live pass.

## M. Verdict

**STRUCTURAL-ISSUE.** The harness is strong enough to diagnose narrow gameplay seams, but its scenario paths can supply the world knowledge, resources and execution that generalized autonomy must provide itself. Evidence needs attribution and explicit intervention boundaries. Preserve narrow live proofs while building an unassisted production field lane; a sum of staged successes is not a living player.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0 throughout. **Canonical owner:** Harness owns disclosed setup/observation/fault injection; production owns decisions and execution.

**First reviewable outcome:** P0.5: correlate enrollment, wake and action witnesses to the named bot/request; population-global step growth is insufficient.

| Phase | Work owned by this layer |
|---|---|
| P0 | Add attributable witnesses and intervention records; treat invalid/unknown observations as unknown. |
| P1–P3 | Keep rigs for exact contracts, then perform real production capability checks after each change. |
| P4–P5 | Author supported goals/preconditions, call the production owner and remove duplicated scenario gameplay interpretation incrementally. |
| P6 | Observe after START only; disable teleport/stock/direct-spawn/manual-wake helpers in the accepted interval; retain first-failure and DB proof. |

**Dependencies:** All phases use the same evidence spine. E-48's zero enrollment must be resolved before another long autonomous run; root cause remains unproved.

**Live stopping point:** A result identifies what the bot discovered, which exact request caused a world consequence and every supplied input; unassisted life has no hidden control actions.

**Preserve:** Rig diagnostics, narrow live seams, actual executor traces, DB state and historical evidence boundaries.

**Defer:** More synthetic quest gates as a replacement for field proof or a second scenario brain.


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

## Evidence provenance

Read-only archaeology list_sources + search_files(root=game-source, glob=**/BotDriveBridge.cs, pattern=TeleportWithRegionSync|LiveScenarioWorldAdapter|scheduler.Wake, limit=40): 0 matches, not truncated, 2026-10-07T17:30:42Z; direct local source/test reads provide textual findings. Source game-source /root/aaemu-dev/AAEmu.Game, search version null, catalog fork develop. Canonical DB md5 78b3bdbf038db3b927056106efdf91af, 1.2 r208022. The inspected live-lane code is a test design, not a newly executed live result. No H proof. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

