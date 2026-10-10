# PlayerBot survival recovery and needs audit

Author: Astra. Layer 10. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Keep the player alive using actual engagement, vitals and resources. Interrupt deliberately when required; choose navigable escape and ordinary recovery. Needs are bot policy over observed resources, not invented Character hunger/economy. Report procurement or waiting when recovery resources are absent.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| SurvivalBrain pure veto/demand | KEEP + HARDEN | Good separation of reaction and recovery demand; fight evidence is too weak. |
| SurvivalBrainPlanner / veto state | REPLACE WITH AUTHORITATIVE OBSERVATION | Selected target/old commitment do not prove current threat. |
| StepSurvivalWake | KEEP + HARDEN | Immediate reaction through same actor; escape completion needs actual engagement clearance. |
| OutOfCombatRecovery execution | REFACTOR | Remove synthetic fallback and extra regen; execute ordinary consumables/posture with observed effects. |
| BotNeedsEvaluator | KEEP | Explicit pure policy and honest food-count proxy. |
| Name-based consumable selection | REPLACE WITH AUTHORITATIVE OBSERVATION | Derive usable effect from item/skill data. |

## C. What Is Good

Survival separates Hold, Recover demand and Flee, and reuses the actor/travel seam. [Needs evaluator](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotNeedsEvaluator.cs:60) explicitly states there is no hunger stat and labels thresholds as policy. Keep that honesty, pure computation and actual Character balances.

## D. Main Problems

**HIGH — An NPC selected for dialogue can count as a fight.** [HasFightEvidence](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Survival/SurvivalBrainTypes.cs:137) is live commitment OR selected target. [SnapshotInputs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Survival/SurvivalBrainPlanner.cs:70) supplies CurrentTargetObjId without target life, hostility or actual aggression. A critically wounded bot targeting a merchant, friendly reporter or stale corpse can flee instead of recover. Flee recomputes an opposite point from the current position, so stale evidence can continually extend retreat.

**CRITICAL — Recovery adds a parallel regeneration path.** Layer 3 established direct consumable fallback. In addition, [ApplySittingRegenTick](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:311) directly adds HP/MP without elapsed-time accounting and invents five-percent regen when values are nonpositive. [Character.RegenTick](/root/aaemu-dev/AAEmu.Game/Models/Game/Char/Character.cs:3128) already owns regeneration and its sitting modifier. An execution call is not another authoritative regen tick.

**MEDIUM — Recovery capability is guessed from names/categories.** [IsEdibleConsumable](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:137) recognizes Potato/Bread/Soup/Potion strings. A counted item is not a verified usable HP/MP effect. Needs defaults can treat unconfigured food/lumber as satisfied, so resource acquisition coverage must be declared.

## E. Hidden Assumptions

Unknown threat positions still generate a finite opposite anchor; finite is not safe. HP/MP targets and food/lumber quantities are policy. Selected target and remembered disengagement are bot-supplied fight evidence. Name heuristics and fallback percentage recovery compensate for absent item-effect observation.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: IsInBattle, current aggro/attackers, HP/MP, item UseSkillId, skill effects, normal regen, labor. MISSING OBSERVATION: actual threat clearance, legal recovery opportunity and safe destination. GAME-DESIGN DECISION: desired reserves and conservative escape policy.

## G. Ownership Problems

Reactive safety owns the override; combat reports real engagement; normal Character owns regen/effects; recovery executor owns waiting and consumable use; acquisition supplies missing resources. Death continuation depends on layers 8/12. No additional survival clock or synthetic hunger stat belongs here.

## H. Generalization Risk

Friendly targets and dead targets expose false flee. Higher density/multiple attackers expose a one-target opposite anchor. Low MP, different food, cooldown or empty bag exposes guessed recovery. After resurrection, low vitals need real safe recovery before resuming work.

## I. ArcheAge Reverse-Engineering Questions

No blocking CLIENT RE NEEDED finding. Existing server regen/skill rules must be used first. Sitting feel or client animation parity requires a human comparison, but does not authorize a second regen implementation.

## J. Live Proof Needed

Resurrect naturally, target a friendly service NPC while wounded, and recover through ordinary sitting/consumables. Then survive a real attack, retreat to validated terrain, clear engagement, acquire a missing recovery resource and continue prior intent. Verify elapsed-time HP/MP and inventory effects against normal server/client behavior.

## K. Recommended Changes

1. Base fight evidence on actual engagement and valid hostile attackers.
2. Remove direct fallback heals and manually applied regen ticks.
3. Derive usable recovery effects/readiness from canonical item/skill data.
4. End/replan escape from actual threat and navigation state, not selected-target persistence.
5. Turn unmet recovery resources into acquisition or explicit waiting goals.

## L. Do Not Touch

Keep pure needs policy, explicit UNKNOWN vitals, reactive veto ordering and the shared actor. Preserve ordinary Character sitting/regen implementation pending its separate engine verification.

## M. Verdict

**STRUCTURAL-ISSUE.** Reactive architecture is appropriate, but threat evidence and recovery effects are not consistently authoritative. Correcting those facts and effects is required before additional survival heuristics can establish a bot that stays alive without fixture assistance.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0 / P3. **Canonical owner:** Reactive safety policy and a normal recovery executor; strategy owns acquiring recovery means.

**First reviewable outcome:** P0.3c: remove synthetic heal/manual regen application; normal Character regen and normal usable items remain authoritative.

| Phase | Work owned by this layer |
|---|---|
| P0 | Eliminate selection-time effects and additional bot regen; preserve explicit waiting when no legal recovery action exists. |
| P1 | Separate selected targets from valid attackers and derive usable recovery resources from canonical effects. |
| P3 | Bound escape to actual danger and a validated route; clear/reconsider it when the threat ends. |
| P4 | Request food/potion/resource acquisition or another safe activity through the goal owner when recovery means are absent. |

**Dependencies:** P1 threat/resource facts, P2 interruption policy, P3 combat/travel and P4 acquisition. Survival cannot invent player vitals or silently discard purpose.

**Live stopping point:** An injured bot near a friendly NPC recovers rather than fleeing forever; actual aggression interrupts it; depleted resources lead to useful acquisition/waiting.

**Preserve:** Pure needs policy, nullable vitals, reactive veto and normal sitting/regen services.

**Defer:** Synthetic hunger/economy, recovery grants or another retreat streak.


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

## Evidence provenance

Archaeology list_sources {}; search_files {root:game-source, glob:OutOfCombatRecoveryModule.cs, pattern:ApplySittingRegenTick|Hp =|Mp =|SelectConsumableItem|Name.Contains, limit:40}; 36 matches, no truncation, 2026-10-07T17:23:02Z. game-source /root/aaemu-dev/AAEmu.Game; version null/catalog fork develop. Textual code only. DB MD5 78b3bdbf038db3b927056106efdf91af (1.2 r208022). [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

