# PlayerBot death corpse loot and ownership audit

Author: Astra. Layer 8. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Recognize death, choose ordinary resurrection, invalidate obsolete actions, then recover and continue intent. For corpses, distinguish existence, container generation, entry eligibility, distribution, actual acquisition, and exhaustion. Quest contribution, tagging and loot entitlement remain distinct.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| Shared CharacterResurrection path | KEEP | Ordinary player/bot resurrection effects are shared. |
| Scheduler death-choice policy | MOVE OWNERSHIP | Scheduler supplies wakes; gameplay recovery chooses resurrection and continuation. |
| GameplayActor.Loot / OpenBag | KEEP + HARDEN | Same normal engine operation; result attribution is inaccurate. |
| LootBrain / LootLedger | REFACTOR | Explicit dispositions are useful; full bag and failed attempts are not permanent corpse facts. |
| Tag/contributor/cohort ownership adapter | REPLACE WITH AUTHORITATIVE OBSERVATION | Layer 2 established conflicting rights; use container distribution state. |
| LootingContainer enforcement | KEEP + HARDEN | Canonical owner; inspect eligibility enforcement without adding bot-only rules. |

## C. What Is Good

[Scheduler death watch](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:541) uses the same CharacterResurrection helper as the normal packet and region-aware relocation. [Actor.Loot](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:1831) calls normal OpenBag. [TryTakeLoot](/root/aaemu-dev/AAEmu.Game/Models/Game/Items/Containers/LootingContainer.cs:397) locks item access and handles per-entry winners/quest eligibility.

## D. Main Problems

**HIGH — Container removal is reported as caller acquisition.** Actor.Loot computes before minus after entry count and calls that granted. [RotateWinner](/root/aaemu-dev/AAEmu.Game/Models/Game/Items/Containers/LootingContainer.cs:464) may distribute an entry to another party member. Count shrinkage therefore cannot prove the caller received an item; it also counts entries, not quantities. Observe an engine receipt or the exact inventory result.

**HIGH — Full bag is banked as a terminal skip.** [LootBrain](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootBrain.cs:258) terminally skips a full bag; [ReadSpace](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootBrainPlanner.cs:465) only checks free slots, ignoring stack-specific capacity. [Ledger](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootLedger.cs:60) preserves Take and clears the entire population at its bound. Clearing space or receiving later rights should permit reconsideration. Layer 5 separately identified terminal rejection recorded as loot-once.

**HIGH — Canonical distribution needs an eligibility query and enforcement review.** [FreeForAll](/root/aaemu-dev/AAEmu.Game/Models/Game/Items/Containers/LootingContainer.cs:455) sets allowLootingNow without checking EligiblePlayers in that branch. The inspected actor entry path checks range/content, not an upfront claim. This is a specific enforcement seam to verify across the full normal path, not proof of all retail loot rules. Do not compensate with cohort/pin bot ownership guesses.

## E. Hidden Assumptions

Default death delay and portal choice are recovery policy. Pinned corpse/cohort assumptions were recorded in layer 2 and are not repeated as new findings. Whole-container junk assessment can hide useful individual entries. A few same-party tests cannot prove unpartied/public/rolled distribution.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: retained distribution rule, eligible players, entry winner/roll state, public-conversion state and actual inventory mutation. MISSING OBSERVATION: read-only per-entry eligibility and caller receipt. UNKNOWN: completeness of normal-path enforcement and intended retail sharing semantics.

## G. Ownership Problems

Engine owns distribution and receipt; behavior owns corpse selection/retry; strategic recovery owns death choice and retained intent. Scheduler should not own gameplay policy. CROSS-LAYER QUESTION: how are old combat/travel actions cancelled before post-resurrection continuation? DEFER TO: layers 10 and 12.

## H. Generalization Risk

Different loot methods can remove entries without granting the caller. Different inventory stacks can fit items in a nominally full bag. Public conversion changes eligibility after an earlier refusal. Multiple bots expose races/ownership fiction; restart requires re-observing corpse existence, not replaying object IDs.

## I. ArcheAge Reverse-Engineering Questions

No unconditional CLIENT RE NEEDED finding. First resolve normal server enforcement. Status: UNKNOWN pending that review; the following question is conditional.

- QUESTION: Who can open/take each loot rule after tag, death and public conversion?
- WHY SERVER IS INSUFFICIENT: Only a demonstrated ambiguous or missing rule after reviewing the full normal engine path would justify client investigation.
- LIKELY CLIENT/LUA/PACKET TARGET: Normal client loot UI/packets and controlled human party/unpartied traces.
- WHAT ANSWER WOULD CHANGE: Canonical enforcement and its eligibility query together.

## J. Live Proof Needed

Ordinary solo, unpartied contested, party rotate/roll, and public-conversion loot from real kills. Verify recipient inventory rows/quantities and remaining entries, including full-bag stack capacity and later space release. Include actual death/resurrection followed by recovery and a useful next activity. No forced tagging or injected containers in accepted proof.

## K. Recommended Changes

1. Return/observe actual caller loot receipts instead of entry-count shrinkage.
2. Expose per-entry read-only eligibility aligned with normal enforcement.
3. Treat capacity/rights refusal as revisitable state; use stack-aware capacity.
4. Verify and fix the named normal-path eligibility seam if missing.
5. Move resurrection policy into recovery and invalidate stale actions explicitly.

## L. Do Not Touch

Keep normal loot locks, distribution state, shared resurrection effects and ordinary inventory services. Do not create a parallel loot manager.

## M. Verdict

**STRUCTURAL-ISSUE.** Normal execution paths exist, but acquisition evidence and persistent corpse dispositions are inaccurate, while rights are split between normal distribution and bot approximations. Real caller receipts and eligibility are the smallest primitives that remove several downstream patches.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P1 / P3. **Canonical owner:** Normal loot distribution and inventory; recovery owns death/resurrection choice.

**First reviewable outcome:** P1.3: distinguish an entry disappearing from the caller actually receiving its item; return a receipt aligned with the engine transaction.

| Phase | Work owned by this layer |
|---|---|
| P1 | Expose per-entry caller eligibility and stack-aware capacity; verify the named normal-path enforcement seam before prescribing a retail rule. |
| P3 | Revisit temporary capacity/range/rights failures; reserve acquired/exhausted memory for proved outcomes. Move death choice to recovery. |
| P5 | Handle contenders, public conversion and new Character/object generations without stale corpse assumptions. |
| P6 | Verify solo, contested and party recipient inventories from real kills. |

**Dependencies:** P0 effect integrity, P1 caller-relative observations, P2 intent disposition and P3 recovery. Full normal-path review precedes any core enforcement fix.

**Live stopping point:** The exact recipient/quantity is correct; clearing space or changed rights permits legal reconsideration; resurrection leads to recovery and useful work.

**Preserve:** Loot locks, normal distribution/roll state, inventory services and shared resurrection effects.

**Defer:** Parallel loot rules, account/pin-based claims or unconditional client RE.


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

## Evidence provenance

Archaeology list_sources {}; search_files {root:game-source, glob:LootingContainer.cs, pattern:EligiblePlayers|TryTakeLootLocked|FreeForAll|MakeLootPublic, limit:40}; 21 matches, no truncation, 2026-10-07T17:20:42Z. game-source /root/aaemu-dev/AAEmu.Game; version null/catalog fork develop. Textual code, not live/client/H. DB MD5 78b3bdbf038db3b927056106efdf91af, 1.2 r208022. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
