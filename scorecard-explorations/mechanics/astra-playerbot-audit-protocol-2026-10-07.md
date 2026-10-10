# PlayerBot audit working protocol

Source: user instructions for Astra, saved as a working reference on 2026-10-07.

## Role and objective

Act as a senior ArcheAge/AAEmu MMO gameplay engineer evaluating whether autonomous players can observe, understand opportunities, choose useful goals, travel, interact, fight/gather/acquire, manage resources and danger, progress, recover, preserve intent and continue in the shared authoritative world.

Build a player simulator, not a quest solver. Quests are one goal source and one exercise of general capabilities. Infrastructure sophistication and narrow passing gates do not establish generalized autonomy.

## Four lenses

1. MMO SERVER CORRECTNESS: prefer canonical game data, authoritative ownership/tagging, server quest state, normal skill/interaction paths, inventory/economy truth and actual spawn/world state.
2. GAME-AI QUALITY: distinguish reactive behavior, deterministic execution, goal selection, bounded planning, interpretation, memory, recovery and persistence. Do not recommend GOAP merely because it exists.
3. PLAYER-SIMULATION QUALITY: inspect progression, travel, combat/post-combat, survival, acquisition, interactions, economy/homestead, persistence and contention dependencies.
4. PRIVATE-SERVER / RE REALITY: use server truth first. Identify genuine client-data/protocol gaps rather than reverse engineering facts already represented by AAEmu.

Classify uncertainty as SERVER ALREADY KNOWS, CLIENT RE NEEDED, GAME-DESIGN DECISION, IMPLEMENTATION BUG, MISSING OBSERVATION or UNKNOWN.

## Scoped pass method

Read only the requested implementation and immediately adjacent dependencies. Establish intended versus actual responsibility and canonical ownership. Find duplication, quest-251/fixture assumptions, heuristic substitutes for missing authoritative truth and good code worth preserving. Recommend the smallest high-leverage structural changes, realistic live proof and only genuinely necessary RE.

Do not redesign the whole system from each layer. Record unresolved dependencies as:

CROSS-LAYER QUESTION: specific unresolved fact.
DEFER TO: the relevant later layer.

Classify every significant component as KEEP, KEEP + HARDEN, REFACTOR, COLLAPSE, MOVE OWNERSHIP, REPLACE WITH AUTHORITATIVE OBSERVATION, DEFER, REMOVE or RESEARCH / RE FIRST, with a reason.

## Patch accumulation and hidden knowledge

Look for missing world truth → guesses → live failure → timer/streak/watchdog → another edge case → another watchdog. Name the missing primitive rather than another threshold: spawn availability, rights/contribution, navigation, legality, target/resource validity or durable intent.

For each success, distinguish information supplied by the bot from information supplied by the harness. Disclose teleports, stocked items, forced state, fixed quest/template and target IDs, event injection, manual wake driving, withhold controls, pre-seeded surfaces and scripted ecology. These are valid diagnostics but cannot alone support generalized-autonomy claims.

Find duplicated production/scenario movement, interaction, combat, quest interpretation and observation/belief/live-resolution semantics. Name the canonical owner. Tests do not justify preserving duplicated brains.

## Required report format

A. Intended Responsibility.
B. Actual Responsibility, with component classifications.
C. What Is Good.
D. Main Problems ranked CRITICAL, HIGH, MEDIUM or LOW by autonomy impact.
E. Hidden Assumptions.
F. Missing Authoritative Truth.
G. Ownership Problems.
H. Generalization Risk across quests, zones, density, ownership, distance, combat, inventory, level, restart and multiple bots.
I. ArcheAge Reverse-Engineering Questions, only where server code/data is insufficient.
J. Live Proof Needed, favor realistic field scenarios over synthetic micro-gates.
K. Recommended Changes, maximum five, ordered by leverage.
L. Do Not Touch.
M. Verdict: HEALTHY, HEALTHY-BUT-NARROW, NEEDS-HARDENING, STRUCTURAL-ISSUE or BLOCKS-AUTONOMY, followed by one explanatory paragraph.

For each RE question include QUESTION, WHY SERVER IS INSUFFICIENT, LIKELY CLIENT/LUA/PACKET TARGET and WHAT ANSWER WOULD CHANGE.

Carry only this tiny cumulative notebook between passes:

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|

## Audit order

1. Scheduler + runtime + actor lifecycle.
2. Observation/perception/world-state: ActorObservation, BotObservedContext, radar/snapshot/belief, tag/spawn/resource observation.
3. Goal arbitration/top-level ownership: arbiter, utility/priorities, director.
4. Quest interpretation: QuestFixtureRow, PatternCatalog, VerifiedVerbRegistry, QuestDirector.
5. Quest execution: QuestBehavior, memory, priorities/proposals.
6. Travel/navigation: TravelBrain, intent store, dispatch, NavigateTo/MoveTo/MoveToUnit, fallback.
7. Combat: targeting, AutoAttack, skills/rotation, kite/disengage, GCD.
8. Death/corpse/loot/ownership.
9. Interactions: NPC/Doodad, Talk/InteractNpc, Cast/Harvest/Use.
10. Survival/recovery/needs.
11. Acquisition/economy/homestead/GOAP.
12. Persistence/dormancy/restart.
13. Shared-world/multi-bot.
14. Harness/bridge/testing.
15. Final synthesis, only after all preceding reports exist.

## Final synthesis requirements

Describe world understanding, goal selection, planning, reactive behaviors, execution, recovery, persistence and shared-world behavior. Answer:

1. What architecture really exists?
2. What architecture should it converge toward?
3. Which current systems belong there?
4. Which abstractions are premature?
5. Which missing server observations force hacks?
6. Which client behaviors genuinely require RE?
7. What prevents autonomous life for 30–60 minutes?
8. What are the five highest-leverage changes?
9. What should we stop building?
10. What realistic field tests come next?

## Cost and evidence discipline

Keep each pass bounded. Avoid rereading unrelated files, generic AI essays and repeated findings. Prefer a few deep findings with concrete code citations. Keep contract/rig, live server/client and human evidence distinct. Saving recommendations does not authorize implementation, dispatch, commits, pushes or deployment.

