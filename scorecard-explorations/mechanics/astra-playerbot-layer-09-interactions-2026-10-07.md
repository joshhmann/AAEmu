# PlayerBot interaction primitives audit

Author: Astra. Layer 9. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Expose ordinary NPC/Doodad/item actions with accurate eligibility and asynchronous completion. Keep conversation, quest credit, shop use, skill-bound interaction, plain phase functions, and harvesting distinct where their engine semantics differ. Share the same skill/permission/resource paths used by players.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| Talk / InteractNpc | REFACTOR | Quest event matching is useful; duplicated fan-out and dialogue meaning need a common engine-facing adapter. |
| Interact / InteractWith / Harvest | COLLAPSE | Preserve distinct intents, converge execution onto canonical phase/skill paths. |
| Current-phase ResolveInteractionSkill | KEEP + HARDEN | Correct basis for live binding; static template guesses cannot replace it. |
| UseItem | KEEP + HARDEN | Normal SkillItem path; scheduled-cast timing needs the same completion correction. |
| Manual skill packets/labor and forced synchronous cast | REMOVE | Once normal async execution owns completion, these parallel effects must disappear. |

## C. What Is Good

[InteractWith](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:1734) uses the normal Doodad-target Skill.Use with GCD enabled and checks observable state. UseItem uses normal inventory and skill caster semantics. Quest talk uses actual QuestManager events and counters. Keep those engine seams and postconditions.

## D. Main Problems

**CRITICAL — Harvest bypasses the outer skill/cost/timing path.** [Harvest](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:3792) directly calls Doodad.Use, authors Started/Fired/Ended packets, and manually deducts labor only when enough remains. The code itself records the required convergence. A mature phase result does not prove player cast time, labor sufficiency, ActAbility effects, or interruption semantics.

**HIGH — Two alternative Doodad verbs report incompatible success.** [Interact](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:1638) directly calls Use and completes true even if conditions silently refuse. InteractWith enters Skill.Use and checks deltas, but [forces the scheduled cast synchronously](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:1754), clears SkillTask and cancels later execution. Production has a game-loop ticker; a headless/rig rationale is not a general timing exemption. The same family therefore has three meanings of completion.

**MEDIUM — NPC dialogue and quest talk share duplicated semantics.** Talk and InteractNpc both scan active quest act definitions, emit talk events, and drain steps. InteractNpc also imitates option-list selection. Keep quest event interpretation in one adapter and ensure a successfully opened ordinary interaction is not required to advance a quest to count as useful.

## E. Hidden Assumptions

The broad MaxInteractRange is a bot policy envelope, not a universal reach gate. Forced immediate cast completion and authored harvest animations are hidden scenario/time knowledge. Clarification to layer 2: scheduled-despawn rejection mirrors [Doodad.Use's actual server guard](/root/aaemu-dev/AAEmu.Game/Models/Game/DoodadObj/Doodad.cs:440); do not bypass that guard solely because a timestamp is in the future.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: phase functions, permissions, skill templates, casting tasks, labor costs, inventory and quest counters. MISSING OBSERVATION: pending/completed interaction identity and exact refusal. IMPLEMENTATION BUG: synthetic timing/cost effects. UNKNOWN: any retail UI-driven selection details not corroborated by existing client traces.

## G. Ownership Problems

Engine owns skill execution, labor and emitted packets; actor owns pending request/completion observation. Quest behavior supplies objective intent. Move shared interaction eligibility out of scenario/quest-specific helpers. Avoid extracting a broad new service before the separately approved packet/service seam strategy.

## H. Generalization Risk

Different casting times, interrupted harvests, low labor, ActAbility and bag capacity expose synthetic paths. Different Doodad phases invalidate template-wide bindings. Ordinary nonquest dialogue can be useful without credit changes. Multiple users can change/delete the target while the cast is pending.

## I. ArcheAge Reverse-Engineering Questions

No blocking RE is needed for Skill.Use, phase or cost convergence. RE FIRST only if NPC default-action parity remains unresolved after server/packet review.

- QUESTION: When does r208022 choose a default interaction and emit quest-talk versus dialogue packets?
- WHY SERVER IS INSUFFICIENT: Packet comments describe client choice but cannot establish its algorithm.
- LIKELY CLIENT/LUA/PACKET TARGET: NPC interaction Lua and normal CSStartInteraction/CSInteractNPC/CSQuestTalkMade traces.
- WHAT ANSWER WOULD CHANGE: NPC intent mapping; server quest-credit rules remain authoritative.

## J. Live Proof Needed

Use the same ordinary NPC and mature crop from bot and normal client, with known inventory/labor. Compare cast duration, interrupted cast, insufficient labor, permissions, actual item receipt and ActAbility changes. Require normal engine packets and delayed completion; no direct phase/event injection.

## K. Recommended Changes

1. Converge skill-bound Doodad actions and harvest onto normal async Skill.Use.
2. Remove manual labor/packet effects and forced cast execution after convergence.
3. Make silent no-op and pending cast observable through one request contract.
4. Share current-phase eligibility/binding and action-specific reach gates.
5. Deduplicate NPC quest-talk interpretation while retaining ordinary nonquest interaction outcomes.

## L. Do Not Touch

Keep authoritative phase/permission guards, normal SkillItem inventory path, current-phase binding and observed postconditions. Do not invent retail opcodes or a parallel Doodad phase machine.

## M. Verdict

**STRUCTURAL-ISSUE.** The engine already contains most required interaction truth. Parallel actor paths bypass or accelerate that truth, so narrow phase/quest successes cannot establish believable player interaction. A single asynchronous engine path has more leverage than more interaction fixtures.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0 / P3. **Canonical owner:** A normal semantic interaction adapter; the engine owns skill/phase/cost effects.

**First reviewable outcome:** P0.3b: migrate one mature-crop harvest onto its real asynchronous skill path and verify labor, interruption and completion.

| Phase | Work owned by this layer |
|---|---|
| P0 | Remove manual labor/authored skill events and forced synchronous cast firing after the normal path is wired; report actual no-op/pending results. |
| P1 | Expose current-phase binding, permission/resource readiness and the action's real reach requirement. |
| P3 | Share NPC quest-talk interpretation and legal nonquest outcomes across goal sources. |
| P6 | Compare ordinary client and bot timing/refusal/effects for the supported interaction families. |

**Dependencies:** P0 request handling, P1 legality, P3 travel and observed postconditions. Plant placement follows its normal creation path; do not force it through harvest's skill path.

**Live stopping point:** Insufficient labor and interrupted casting cannot mutate the crop/reward as successful completion; valid actions finish from observed engine effects.

**Preserve:** Normal phase/permission guards, SkillItem/inventory paths and distinct placement semantics.

**Defer:** A parallel Doodad machine, guessed opcodes or broad default NPC mapping before its bounded RE question is resolved.


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

## Evidence provenance

Archaeology list_sources {}; search_files {root:game-source, glob:GameplayActor.cs, pattern:public ActorRequest Interact|public ActorRequest Harvest|manual ChangeLabor|doodad.Use\\(Character, limit:40}; 8 matches, no truncation, 2026-10-07T17:21:42Z. game-source /root/aaemu-dev/AAEmu.Game; version null/catalog fork develop. Textual code evidence only. DB MD5 78b3bdbf038db3b927056106efdf91af, 1.2 r208022. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
