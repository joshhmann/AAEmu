# Muse handoff for PlayerBot rework

For persistent execution, use the [OMP loop handoff](astra-playerbot-omp-loop-handoff-2026-10-07.md). Its freshness note updates this guide's E-48 starting point to E-49; always check the newest ledger before acting.

Author: Astra. Date: 2026-10-07. Scope: evidence-led execution guidance for the existing audit and P0–P6 path.

Use the audit to improve reusable player capabilities while preserving normal AAEmu gameplay. The target is an autonomous primitive ArcheAge player that acquires useful means, survives, progresses, recovers and retains purpose. Quest counts and narrow fixture passes are diagnostic evidence.

This handoff defines explicit technical direction, evidence anchors and stopping rules. It is not a fresh source/live verification or a deployment instruction. The first assigned task below is review and preparation of one bounded P0 slice. Later implementation follows the scope actually assigned by the user; existing authorization does not require repeated confirmation merely because a new phase starts.

## Message to give Muse

### Required correction before enrollment repair

The [review through E-51](astra-playerbot-omp-work-review-2026-10-07.md) identifies a missing decision in the repair sketch. Apply this constraint on the next iteration, checking newer evidence first:

- Establish the intended **production designation and enrollment contract**. Identify the actual designation signal, canonical owner, and supported activation path. A harness-created name or network login does not by itself designate an autonomous PlayerBot. Missing registry membership proves absence, not that production needs a new path.
- Distinguish **fresh headless activation** from **adoption of an already embodied network Character**. Trace the proposed calls through adjacent lifecycle code before selecting a repair. `PlayerBotManager.Activate` currently reaches headless activation, whose `EnterWorld` reloads the Character and sets its connection to null. Do not use that path for live-character adoption without resolving those effects.
- Compare the unregistered negative case with a legitimately designated bot using the supported production enrollment owner. Disclose pre-START provisioning/designation separately from post-START gameplay assistance. Keep no manual wake driving after START as the live acceptance boundary.
- Revise E-51's exclusive new-path ruling as a hypothesis until this comparison selects the owner and repair location. Re-observing no-entry alone does not resolve intended design versus regression. Preserve Character identity/generation, connection, instance/position, current gameplay state, and owned entities; prove no reload or duplicate embodiment when adopting a live Character.

**Required card output:** designation source → current embodiment state → canonical enrollment owner → lifecycle side effects → positive/negative discriminator → smallest justified repair. If any link is unknown, name the missing evidence and keep repair selection pending. This constraint adds no code/lane authority and does not require repeating permissions already granted for an assigned task.

```text
Repo: /root/aaemu-dev.
Workstream: PlayerBot rework based on Astra's audit and P0–P6 direction.
Initial task mode: REVIEW; prepare one bounded implementation brief.

Read scorecard-explorations/mechanics/astra-playerbot-muse-handoff-2026-10-07.md
and follow its First assigned task. Use the linked P0–P6 path, relevant layer
reports, canonical current state, and current code/ledger as evidence.

Keep the audit's ownership and normal-gameplay direction. Revalidate only the
next selected slice and its immediate dependencies; do not redo the whole audit,
design a replacement framework, or create a speculative backlog.

Separate CODE, LIVE-RECORDED, INFERENCE, DIRECTION and UNKNOWN. Cite exact source
symbols/lines or ledger IDs and quote the evidence for the proposed failure
mechanism. If newer evidence contradicts a finding, name it and update that
bounded conclusion; do not force the old recommendation through.

Start with P0.1: ordinary activation to attributable first autonomous wake.
E-48 records zero enrollment, not a proved SQL or quest-behavior cause.
Apply Required correction before enrollment repair: establish designation,
distinguish fresh activation from live-character adoption, trace lifecycle
side effects, and compare legitimate enrollment with the unregistered case.

Return the baseline/reconciliation, decisive evidence, one card using the
existing implementation template, missing facts/dependencies, and stopping rule.
No code edits or lane launch in this review assignment. Do not dispatch others,
commit, push, deploy, or change project objectives.
```

## Reading and authority

Start with AGENTS.md and START-HERE.md, then the [canonical task state](/root/aaemu-dev/.omp/state/CURRENT.md), [decisions](/root/aaemu-dev/.omp/state/DECISIONS.md) and relevant newest [evidence entries](/root/aaemu-dev/.omp/state/EVIDENCE.md). Apply PROJECT-CONTROL's delivery contract, STATUS's dispatch direction, ROADMAP's scope/freeze/exit requirements and SCORECARD's evidence boundaries. Read the full relevant current sections and their linked dependency records.

Then read the [rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) and [synthesis](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-15-synthesis-2026-10-07.md). The [index](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-audit-index-2026-10-07.md) locates every layer. For the selected slice, read its report, especially D/K/L/N, and directly corroborate the cited source and immediately adjacent normal gameplay paths. Use symbols as stable keys; line numbers may drift.

Current code/data decides implementation facts. Actual artifacts decide observed behavior. User instructions and canonical project decisions decide scope/ownership. Astra's reports supply source findings and recommended direction; they do not override contradictory newer evidence or create live acceptance.

At this handoff, HEAD is `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` with pre-existing staged, unstaged and untracked work. The ledger reaches E-48 while CURRENT still names E-39 as its maximum. **Record this freshness mismatch; do not infer that older summary text supersedes newer observations or silently replace the canonical objective.** Preserve staged retention work/quarantine and unrelated dirty paths.

## Evidence rules

| Label | What it can support | Required anchor |
|---|---|---|
| CODE | A source contract, path or contradiction at the inspected revision | File, symbol/line, exact bounded excerpt, SHA and relevant dirty state |
| LIVE-RECORDED | The named historical server/client observation | E-ID and original command/environment/artifact; disclose if not independently reopened or rerun |
| INFERENCE | A reasoned explanation still needing discrimination | Supporting observations and the alternative explanation/test |
| DIRECTION | A recommended ownership/change | Audit layer + P-phase + observable outcome and dependency |
| UNKNOWN | An unresolved fact or missing proof | Exact missing observation or next bounded investigation |

Do not label a code path as live-proven. A green fake actor, manually completed request or injected event proves its contract only. An audit's suspected enforcement gap does not prove all normal entry points lack enforcement.

Preserve the requested uncertainty vocabulary alongside those evidence labels: SERVER ALREADY KNOWS, CLIENT RE NEEDED, GAME-DESIGN DECISION, IMPLEMENTATION BUG, MISSING OBSERVATION, UNKNOWN. Invoke read-only archaeology when investigating source/gameplay/reference-data facts as AGENTS.md requires; record source identity, inputs, bounds/truncation, confidence and canonical DB identity. The existing acceptance dossier is [here](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

When a claim changes, cite the superseding evidence and retain its history. A newer interpretation is not automatically better: identify the actual trace or source that resolves the ambiguity.

## First assigned task

**Reusable capability:** normal autonomous enrollment and continuous execution after ordinary activation.

**First failed predicate:** a designated autonomous bot receives an attributable scheduler wake/activity. [E-48](/root/aaemu-dev/.omp/state/EVIDENCE.md:1696) records:

```text
11/11 polls: activity null, leg false, travel '', decide '', counters all 0, TotalStepsRun delta 0, no wake seq.
FIRST FAILED PREDICATE: exists scheduler wake/activity for the bot within 300s of START.
```

These are exact excerpts from the existing ledger, not newly collected raw runtime logs. Root cause remains unknown. The adjacent dormant-source SQL error is not a demonstrated enrollment cause.

Read [Layer 1](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-01-lifecycle-2026-10-07.md) and [Layer 14](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-14-harness-2026-10-07.md), their source anchors and the relevant lifecycle/manager/scheduler/production enrollment callers. Build the smallest evidence chain that distinguishes:

| Boundary | Evidence to locate |
|---|---|
| Character provisioned/designated | Actual managed bot identity and the enrollment policy for that activation path |
| Character embodied | Current Character/world identity and completed ordinary activation |
| Bot runtime registered/active | The corresponding production registry entry and generation |
| Scheduler entry/due | The named bot's scheduler membership, due time and scheduling disposition |
| Wake executed | That bot's wake sequence and selected activity/request, not population-global step growth |

Produce **one proposed card** under the [existing implementation contract](/root/aaemu-dev/.kanban-templates/implementation.md). Name parent, current baseline, observed outcome, in-scope paths, non-goals, natural versus supplied initial state, hypothesis/discriminator, acceptance layer, dependencies, owner/verifier availability and stopping point.

If code and existing artifacts do not locate the handoff failure, the next card is bounded observation with specific witnesses. If they do locate it, propose the smallest repair and exact acceptance. Do not prescribe a SQL migration, quest fix, additional wake timer or longer field interval from adjacency alone.

**Acceptance for the later repair:** one designated ordinary bot enters through the production path and receives first/subsequent attributable wakes with no manual Wake after START; a deliberate failed step has an explicit next disposition, and repeat activation does not duplicate enrollment or reload live gameplay. The proof uses normal world state and an isolated authorized environment.

**Stop this review at:** one evidence-backed next card, or one precisely named missing discriminator. P0.1 does not close gameplay autonomy. The initial review does not launch a lane or alter code/state.

## Key direction and evidence for every layer

The following anchors are inherited **CODE findings at the audit baseline**, requiring targeted revalidation before a patch. The corresponding layer reports contain full reasoning, classification, preserved code and live proof boundaries.

| Layer / phase | Evidence anchors | Explicit direction |
|---|---|---|
| 1 / P0 | [busy/dedupe](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4865) and [Finish](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4947); [Layer 1](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-01-lifecycle-2026-10-07.md). | Locate ordinary enrollment first. Protect final/effect outcomes, current embodiment and deliberate rescheduling after failure. Do not add a second scheduler or rescue timer. |
| 2 / P1 | [corpse projection](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotPerceptionSnapshot.cs:307); capture/coverage findings in [Layer 2](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-02-observation-2026-10-07.md). | Unify bounded caller-relative facts, coverage and unknown semantics before adopting more perception machinery. Account type, target pins and absence from a scan cannot award rights or prove exhaustion. |
| 3 / P0/P2 | [recovery eligibility](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:441) and [module contract](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/IBotActivityModule.cs:31); [Layer 3](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-03-arbitration-2026-10-07.md). | Make selection pure. Integrate one strategic intent owner at the documented retained GOAP boundary; modules/arbiter supply offers/adaptation and executors perform bounded work. Do not leave independent schedule/quest/homestead dispatch authorities. |
| 4 / P1/P4 | [objective reduction](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/QuestFixtureRow.cs:434) and [source selection](/root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Director/QuestFixtureRow.cs:657); [Layer 4](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-04-quest-interpretation-2026-10-07.md). | Project all unfinished supported requirements and source alternatives from normal quest state. Preserve explicit unsupported conjunctions. Do not invent another quest-progress engine or a special quest-251 interpretation. |
| 5 / P0/P2/P4 | [Running ownership](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:1506) and [runtime completion test](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotBehaviorRuntime.cs:134); [Layer 5](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-05-quest-execution-2026-10-07.md). | Keep a selected Running request owned; consume its exact terminal later. Distinguish attempted, acquired, temporarily blocked and exhausted. Return disposition to strategy; a streak is a budget/diagnostic, not a world cause. |
| 6 / P0/P3 | [navigation fallback](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:454); request/arrival findings in [Layer 6](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-06-travel-2026-10-07.md). | Use exact request identity, positive traversal evidence and action-specific arrival. Reconcile the existing reporter arm and latest ledger before replacement. A direct position advance does not prove a legal route; do not add another travel machine. |
| 7 / P0/P3 | [actor cast](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:705), [Unit.UseSkill](/root/aaemu-dev/AAEmu.Game/Models/Game/Units/Unit.cs:1069), [normal player gate](/root/aaemu-dev/AAEmu.Game/Core/Packets/C2G/CSStartSkillPacket.cs:175); [Layer 7](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-07-combat-2026-10-07.md). | Converge the bot player-intent adapter to normal GCD with bounded executor wait/retry. Preserve legitimate NPC skill semantics; do not blindly change all Unit callers. Quest, party and wildlife use one combat capability. |
| 8 / P1/P3/P5 | [rotating recipient](/root/aaemu-dev/AAEmu.Game/Models/Game/Items/Containers/LootingContainer.cs:464) and [capacity skip](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootBrain.cs:258); [Layer 8](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-08-death-loot-2026-10-07.md). | Observe actual recipient/quantity and per-entry caller eligibility. Temporary range, capacity and rights failures remain revisitable. Review the full normal enforcement path before alleging or changing a retail rule. |
| 9 / P0/P3 | [Harvest](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:3792) and [forced cast completion](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:1754); [Layer 9](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-09-interactions-2026-10-07.md). | Use the normal asynchronous skill path for skill-mediated Doodad actions; remove manual labor/authored effects after convergence. Plant placement stays on its distinct normal creation path. No forced casts or no-op success. |
| 10 / P0/P3/P4 | [fight evidence](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Survival/SurvivalBrainTypes.cs:137) and [extra regen](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/OutOfCombatRecoveryModule.cs:311); [Layer 10](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-10-survival-2026-10-07.md). | Use actual hostile engagement and ordinary regen/usable recovery effects. End escape when real danger ends. Missing resources become acquisition/waiting goals; do not grant vitals or infer danger from a selected friendly NPC. |
| 11 / P1/P4 | [known-mask applicability](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/BotWorldState.cs:100) and [default destination](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Goap/Actions/FarmingActions.cs:19); [Layer 11](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-11-acquisition-economy-2026-10-07.md). | Keep bounded search. Bind known requirements, exact quantities and one concrete source consistently across costs, travel, action and verification. No action catalogue expansion or stocked prerequisite shortcut to make a plan pass. |
| 12 / P0/P5 | [hydration](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:220) and [activated Character](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotManager.cs:75); [Layer 12](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-12-persistence-2026-10-07.md). | Activate one current hydrated Character generation; retire stale execution. Persist and consume minimal high-level intent, then re-observe. Storage bytes or a cursor declaration alone do not prove resumption. |
| 13 / P2/P5/P6 | [global travel eviction](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Travel/TravelIntentStore.cs:110) and [global loot eviction](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootLedger.cs:70); [Layer 13](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-13-shared-world-2026-10-07.md). | Isolate citizen memory, make party participation deliberate and reuse shared capabilities. Engine rights arbitrate contention. Population fidelity/caps control cost; activity labels/fairness cannot replace observed independent progress. |
| 14 / All phases | [scenario world adapter](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:1099) and [fallback materialization](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:1153); [Layer 14](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-14-harness-2026-10-07.md). | Correlate evidence to the named bot/wake/request and disclose all inputs. Migrate authored goals to production owners incrementally. Teleports, stock, direct-spawn fallback, events and manual wakes are diagnostic interventions, excluded from unassisted claims. |

Layer 15 synthesizes affected evidence after delivery. It introduces no new strategic brain and does not trigger a full re-audit after every small fix.

## Phase discipline

| Phase | Required direction |
|---|---|
| P0 | Protect gameplay and make designated autonomous entry reliable. Handle request integrity/Running before introducing normal cast waits. Split GCD, harvest and recovery into bounded changes. |
| P1 | Supply truthful, bounded opportunities with coverage, readiness, rights and unknown states. Migrate real consumers after corrections. |
| P2 | Make one intent owner govern chosen work, waiting, interruption and reconsideration. Retain deterministic selection; use the documented strategic boundary. |
| P3 | Reuse performed travel/combat/loot/interaction/recovery capabilities from different goal sources. Rejection and async results remain attributable. |
| P4 | Acquire actual prerequisites, bind exact requirements and complete/repeat a supported progression or livelihood chain. |
| P5 | Reconcile durable purpose with freshly loaded gameplay and prove normal contention/cooperation without cross-bot corruption. |
| P6 | Observe 30–60 minutes of useful unassisted life, then expand supported coverage and scale based on measured costs/progress. |

These are the rework path's phase numbers, not historical architecture phase labels or progression-ladder grades. Dependency-ready observation/research can proceed earlier. Do not promote a phase because one fixture passes, or delay all live checks until P6.

Before changing the next layer, answer: **What authoritative fact is missing? Which existing owner should expose it? Which shared capability consumes it? What player outcome will prove the change?** If the evidence instead shows an ordinary engine defect, name that canonical defect and its bounded service-level repair rather than adding a bot approximation.

## Decisions to preserve

One ordinary Character, one scheduler/action lifecycle, one authoritative movement seam and normal inventory/quest/property/economy effects. Tick work yields; deadlines bound waiting; terminal requests stay terminal. Interruption abandons a request. Retaining high-level purpose means rebuilding legal execution with a new request, while checking whether a delayed engine effect already landed.

Keep legality-first deterministic proposals, bounded observation, actual quest counters, normal skill/auto-attack tasks, real crop phase, bounded resource-aware planning and observed postconditions. Preserve normal Plant creation semantics and ordinary sitting/regen. Keep the existing operational population layer.

Keep per-layer Do Not Touch decisions until a concrete incompatible result is shown. Shared code is extracted only from demonstrated duplication across real consumers; do not create another planner, cooldown tracker, travel FSM, blackboard or universal failure machine merely to organize the rework.

## Known history that prevents repeating work

- [E-36](/root/aaemu-dev/.omp/state/EVIDENCE.md:1647) records staged retention work and quarantine. Review/reuse it; this handoff does not authorize landing it.
- [E-42](/root/aaemu-dev/.omp/state/EVIDENCE.md:1680) records an existing reporter-walk arm. Do not implement a second arm on a missing-code premise.
- [E-46/E-47](/root/aaemu-dev/.omp/state/EVIDENCE.md:1692) record successful chunk arrival/turnover and later shutdown during continued travel. Earlier chunk-death explanations are superseded for that evidence. Verify timestamps/request identities before suggesting a changed lane budget.
- E-48 records zero enrollment. It supplies the next failed predicate, not the complete cause.
- The audit and rework path contain no newly executed gates or live acceptance. Historical claim→construct, storage, travel and quest seams retain their original supported scope.

## Scope and intervention rules

A scenario is evidence for a capability; it does not become the product goal. Every action must be attributable to the production owner. Disclose initial teleports, stocked items/labor, forced levels/quests, known target IDs, event/kill injection, seeded surfaces, manual wake driving and scripted ecology. A diagnostic intervention remains valid only within its named evidence boundary.

During an accepted unassisted interval, use observation only: no manual wakes, teleport/stock/progression repair or fallback target spawning. Normal starter templates and disclosed ordinary starting funds are inputs, not earned acquisitions. Prove the acquisition legs the claim requires.

Do not expand homestead/economy/quest breadth to mask a broken prerequisite. No new vendor stock, waived eligibility, fabricated gameplay effects or relaxed success thresholds. Timers bound work; they cannot infer spawn availability, ownership, legal reach or actual receipt.

Use server services/data before client RE. Existing conditional RE questions are navigation asset gaps, NPC default-action mapping and unresolved retail loot eligibility after full server review. A new RE question requires a demonstrated server insufficiency and an explicit answer that would change implementation.

Follow the user's actual task mode and persistent authorization. Do not request routine permission again for an already assigned reversible implementation task. This review assignment does not itself authorize code/lane execution, commits, pushes, deployment, external messaging or broad replacement of project plans.

## Required handoff for every selected slice

Report these fields with concrete evidence, not an entire regenerated architecture essay:

1. **Outcome and owner:** capability, parent, P-phase, failed predicate/mechanism and canonical responsibility.
2. **Baseline and anchors:** exact HEAD + dirty/WIP state, direct source excerpts, E-IDs/artifacts, archaeology provenance where applicable, contradictions and unknowns.
3. **Change:** existing path reused, smallest diff or proposed in-scope change, preserved parts and explicit non-goals.
4. **Verification:** exact command/environment/assets and real results; contract/rig, live server/client and H separately; supplied/bypassed inputs and actual postconditions.
5. **Stop and next:** implementation/verification/deployment/review status, incomplete required legs, one next dependency or card.

In REVIEW mode, item 3 is a proposal and item 4 states not run plus the planned discriminator. In IMPLEMENT mode, provide the actual relevant diff and necessary checks. In LIVE PROOF mode, provide actual per-bot traces and authoritative before/after results from the named isolated environment. Apply the repository's gate/evidence contract; no invented counts, skipped-smoke claims or independent signoff.

A completed actor request is not automatically a completed goal. Simulated planner effects are not observed consequences. A container change is not a caller receipt. A metadata round trip is not continued purpose. A pressure storm is not independent player progress.

If a required loop leg remains unproved, say **LOOP INCOMPLETE** and name it. Keep useful progress visible without replacing the yardstick.

## Director review before advancing

The reviewer should check that the cited code/artifact supports the claimed cause, the diff respects the named owner, gameplay effects come from normal services, accepted proof contains no hidden intervention, and the next slice follows the first actual remaining failure. Changed claims must reconcile to the authoritative records/dashboard when applicable; unchanged historical evidence is preserved.

Re-read the actual artifact or diff when a worker claim is unsupported. No Muse response has yet been evaluated under this brief, so its compliance/effectiveness is unmeasured. The scoped task/evidence/stop format adapts the repository's [brief-evolution skill](/root/aaemu-dev/.agents/skills/brief-evolution/SKILL.md); empirical scoring applies only after real worker turns.
