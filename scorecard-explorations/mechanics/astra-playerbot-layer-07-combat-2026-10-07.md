# PlayerBot combat audit

Author: Astra. Layer 7. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Execute a selected fight or immediate defense: legal target retention, skill readiness, range, resource use, and disengagement. Report engagement/kill/failure separately from starting a cast or attack loop. Ordinary engine skill timing and effects remain authoritative.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| CombatBrain / target scorer / engagement memory | KEEP + HARDEN | Deterministic bounded tactics and commitment are useful; inputs must be authoritative. |
| CombatDecisionTree readiness/role/rotation | KEEP + HARDEN | Uses learned skills/templates/cooldowns; fallback guesses and partial readiness are not legal guarantees. |
| GameplayActor.Cast / CastAt | REFACTOR | Normal skill machinery is reused, but GCD parameters diverge from player path. |
| AutoAttack / StopAutoAttack | KEEP + HARDEN | Normal continuous engine task; starting/refused strike must not be mistaken for damage or kill. |
| Party/wildlife/PvP inline combat | COLLAPSE | Route those intent sources to one bounded fight executor. |
| Kite/disengage travel | KEEP + HARDEN | Needs a validated escape route, not just an opposite point. |

## C. What Is Good

[Cast](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:666) checks template existence and learned/default/common status. [AutoAttack](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:805) uses normal Skill and the Character auto task. [CombatBrain](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Combat/CombatBrain.cs:214) separates survival veto, disengage, crowd control, acquisition and range. Target commitment can reduce pointless churn.

## D. Main Problems

**CRITICAL — Learned casts bypass the player's GCD gate.** [Actor.Cast](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:705) calls [Unit.UseSkill](/root/aaemu-dev/AAEmu.Game/Models/Game/Units/Unit.cs:1069), which passes true to Skill.Use. The normal [packet](/root/aaemu-dev/AAEmu.Game/Core/Packets/C2G/CSStartSkillPacket.cs:175) passes false. This is documented deferred convergence, but remains a real parity gap. Test runners that do not advance time cannot justify production bypass.

**HIGH — Several execution paths own combat semantics.** BotRoam party assist uses a fixed skill and interval; wildlife/PvP use their own loops; quest behavior uses CombatBrain with rotation default-off. Shared Skill effects do not eliminate duplicated targeting, timing, range, and recovery policy. One bounded combat executor should serve these sources.

**HIGH — Nearby hostile is still substituted for active danger/readiness.** [Planner candidates](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Combat/CombatBrainPlanner.cs:180) count hostile NPCs; readiness checks cover cooldown/mana/range but not every engine refusal. AutoAttack starts the task and returns Completed even when the first Skill.Use refuses. This can legitimately mean “loop started,” but not “attack landed.” Progress must use observed engine effects/engagement, not request completion or ambient HP changes.

## E. Hidden Assumptions

Ability1 selects a coarse role. Rotation has an explicit known-skill vocabulary; defaults and unloaded-manager range fallbacks serve rigs. Skill 18131 in party assist and fixed cast intervals are bot policy supplied in code. A five-second commitment window is tactical policy. Melee/ranged gates and quest rotation opt-in must be declared in live evidence.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: learned skills, SkillTemplate ranges/costs, cooldowns, GCD state, target permissions and actual aggro. MISSING OBSERVATION: full readiness/refusal disposition and damage/engagement attribution. GAME-DESIGN DECISION: conservative target choice and retreat thresholds.

## G. Ownership Problems

Combat owns tactics inside a fight; strategy chooses voluntary targets, survival may interrupt, and navigation validates retreat. Engine owns timing/effects. CROSS-LAYER QUESTION: normal consumable recovery and safe retreat acquisition? DEFER TO: layer 10. Do not duplicate cooldown clocks.

## H. Generalization Risk

Other classes/levels expose role/rotation limitations; dense zones confuse ambient hostility with attackers. Ranged gaps, crowded ownership and depleted MP expose refusal handling. Multiple bots require actual contributor attribution. Restart must discard obsolete combat tasks and re-observe engagement.

## I. ArcheAge Reverse-Engineering Questions

No blocking CLIENT RE NEEDED finding. The GCD discrepancy is already represented in code. Retail combo optimization may need empirical/client research later, but is not a prerequisite for a believable primitive fighter.

## J. Live Proof Needed

Ordinary learned-skill fights at two levels and with melee/ranged equipment, including interrupted cast, GCD refusal, low MP, second attacker, and safe disengagement. Compare allowed cast cadence to a normal client using the same character build. Verify actual damage/kill and post-fight resource state; no stocked infinite recovery or synthetic damage.

## K. Recommended Changes

1. Converge casts onto normal GCD semantics with executor-owned wait/retry.
2. Send quest/party/wildlife/PvP fight intents through one bounded executor.
3. Consume exact skill refusals and distinguish loop-started, strike-landed and target-killed.
4. Observe actual aggression and attributable progress.
5. Validate retreat routes through the travel primitive.

## L. Do Not Touch

Keep normal Skill effects, learned-skill checks, auto-attack task, StopAutoAttack, deterministic target commitment and tactics. Do not add another combat simulator or cooldown manager.

## M. Verdict

**NEEDS-HARDENING.** There is enough ordinary engine machinery and tactical policy to support primitive combat. GCD parity and common execution ownership must be corrected before class/rotation breadth can count as generalized fighting capability.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0 / P3. **Canonical owner:** One bounded combat executor over normal Skill/auto-attack services.

**First reviewable outcome:** P0.3a: normal learned-skill GCD enforcement and executor wait/retry must change together; a bare bypass flag flip is incomplete.

| Phase | Work owned by this layer |
|---|---|
| P0 | Converge normal skill timing/refusals with a bounded pending/request cadence. |
| P1 | Observe valid hostility, actual aggression, capability/readiness and attributable progress. |
| P3 | Route quest, party and wildlife fight intents through the same executor; separate loop-started, strike-landed and target-killed. |
| P6 | Test supported character abilities/levels, moving targets and multiple attackers using normal resources. |

**Dependencies:** P0 Running handling, P1 engine readiness and P3 travel for legal retreat. Defense interrupts strategic work but does not select all future goals.

**Live stopping point:** Two goal sources use the same combat semantics; real GCD refusal, target death and disengagement result in correct observed effects and continued intent.

**Preserve:** Normal skill effects, learned-skill checks, auto-attack task, stop and deterministic tactics.

**Defer:** A second cooldown engine, class/rotation breadth before timing parity or simulated combat.


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

## Evidence provenance

Archaeology list_sources {}; search_files {root:game-source, glob:GameplayActor.cs, pattern:bypassGcd|GCD convergence|auto-attack loop starts|public ActorRequest AutoAttack, limit:40}; 6 matches, no truncation, 2026-10-07T17:19:26Z. game-source /root/aaemu-dev/AAEmu.Game; version null/catalog fork develop. Textual code evidence. DB MD5 78b3bdbf038db3b927056106efdf91af, 1.2 r208022. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

