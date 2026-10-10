# PlayerBot observation perception and world-state audit

Author: Astra. Layer 2 of the requested subsystem audit. Date: 2026-10-07.

**Verdict: STRUCTURAL-ISSUE.** Production has useful authoritative reads, but no common, trustworthy interpretation of what this player can see, attack, interact with, acquire, or expect at a resource site. The richer perception stack is deliberately isolated and contains semantic defects; connecting it wholesale would spread those defects.

Audit baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` **plus the existing dirty working tree**. This is source evidence, not fresh live proof. No builds, tests, server runs, or gameplay edits were performed. Saving this report does not alter project grades.

## A. Intended Responsibility

Capture bounded, observer-relative facts from the ordinary Character and its world: self resources, visible entities, current combat involvement, interaction capabilities and eligibility, loot availability, and known resource sites. Preserve capture identity, time, coverage, and UNKNOWN when a read fails.

Separate physical facts, caller eligibility, remembered knowledge, and goal relevance. A nearby merchant is a capability; needing that merchant is a decision. An absent object is outside the observation, not necessarily despawned. Static spawn data describes a possible site, not a currently available resource.

Execution must revalidate changing facts through normal gameplay services. Observation must not become a second gameplay rules engine or grant players omniscient knowledge of undiscovered sites.

## B. Actual Responsibility

The production observation contains self state, inventory/economy summaries, active quest IDs, and nearby object IDs. Production consumers subsequently resolve their own gameplay facts. A separate radar → snapshot → delta → belief → attention stack provides richer interpretation, but a [surface test explicitly requires no shipping references from outside that stack](/root/aaemu-dev/AAEmu.UnitTests/Game/Core/Managers/Bots/PerceptionStackSurfaceTests.cs:76). Source searches found no production decision callers.

| Component | Classification | Why |
|---|---|---|
| ActorObservation / GameplayActor.Observe | KEEP + HARDEN | Ordinary Character reads and bounded census are useful; coverage, visibility, and capture metadata are missing. |
| BotObservedContext | KEEP + HARDEN | Copying decision inputs is appropriate; the sparse copied context is not a complete world observation. |
| BotRadarProjection | KEEP + HARDEN | Reuses supplied IDs and sorts deterministically; later live reads are stamped as a new frame without binding them to the original capture. |
| BotPerceptionSnapshot | REFACTOR | Useful typed rows, but ownership, legality, interaction, and lifecycle fields overstate their evidence. |
| PerceptionDelta | KEEP + HARDEN | Deterministic comparison is valuable; appearance/loss must not imply actual spawn/despawn, and self damage is missing. |
| BotBeliefState / BeliefInterpreter | KEEP + HARDEN | Pure derived state and explicit unknown values are useful; threat and corpse interpretation need corrected inputs. |
| AttentionScorer | MOVE OWNERSHIP | Goal-required and owned-target bands need goal/eligibility evidence. Defer arbitration policy to layer 3. |
| CurrentPlace | KEEP | A small descriptive place value does not need a new subsystem. |
| BotSurveySenses occupant filtering | KEEP | Uses CanSeeTarget; preserve that engine predicate in the common observation path. |
| QuestObjectiveTargetSelector live gates | KEEP + HARDEN | CanAttack and CanSeeTarget are correct engine gates. Share observation facts without eliminating execution-time checks. |
| LootBrainPlanner claim observation | REPLACE WITH AUTHORITATIVE OBSERVATION | Managed-account cohort and pinned-target inference substitute for real loot distribution evidence. |
| QuestBehavior.ProbeCorpse | REFACTOR | Container presence/count/range are useful facts; the name Lootable must not imply caller eligibility. |
| Spawner-position lookup and repeated-source inference | REPLACE WITH AUTHORITATIVE OBSERVATION | A location and retry count cannot establish availability or obstruction. |
| Snapshot harvest inspection via replay scenario | MOVE OWNERSHIP | Real Doodad function inspection belongs in a reusable engine-facing capability reader, not a replay class. |
| BotWorldStateProvider spatial projections | DEFER | Adjacent evidence of another projection using remembered positions; planning semantics belong to layer 11. |
| BotQuestLoopObservation | KEEP | Real quest-state telemetry is useful. Telemetry and supplied scenario intent do not establish autonomous perception. |

## C. What Is Good

[Observe](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:261) reads ordinary Character state and region-based nearby entities. Radar projects the supplied census rather than introducing a global scan. Deterministic ordering, immutable-style rows, nullable unresolved facts, and pure delta/belief computation are good foundations.

The [quest target gates](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/QuestObjectiveTargetSelector.cs:195) reuse gameplay predicates. [Survey occupant filtering](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotSurveySenses.cs:250) already demonstrates observer-relative visibility. Preserve both.

The isolation test is an honest boundary: typed infrastructure is not currently claimed by its own contract to drive production. Keep isolation while correcting the contract, then change that test deliberately when one consumer migrates.

## D. Main Problems

### HIGH — Competing observations disagree about loot rights

[BuildCorpse](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotPerceptionSnapshot.cs:307) treats Npc.OwnerId as corpse ownership. Radar's Lootable comes from container contents and range, without ownership. [AttentionScorer](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Belief/AttentionScorer.cs:164) consequently promotes any such corpse into OwnedCorpseBand; belief separately compares the owner value to SelfObjId.

Production uses a different approximation. [LootBrainPlanner](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootBrainPlanner.cs:305) treats unrelated managed bot accounts as party-equivalent. [LootBrain](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Loot/LootBrain.cs:114) turns a pinned corpse into Self when claim evidence is absent. A bot designation or selected target is not an engine loot entitlement.

AAEmu [generates loot during Unit death](/root/aaemu-dev/AAEmu.Game/Models/Game/Units/Unit.cs:533), retains eligible players and distribution rules in [LootingContainer](/root/aaemu-dev/AAEmu.Game/Models/Game/Items/Containers/LootingContainer.cs:124), and [clears live tagging in Npc.DoDie](/root/aaemu-dev/AAEmu.Game/Models/Game/NPChar/Npc.cs:1027). Post-death tagging is therefore an inadequate source. Contribution for quest credit must also remain distinct from loot rights.

**Classification:** SERVER ALREADY KNOWS much of the distribution state; MISSING OBSERVATION for a read-only caller-relative query; IMPLEMENTATION BUG in presenting substitute evidence as ownership. This does not assert that every current server loot method enforces ideal retail rules. The query must reflect actual normal gameplay, and enforcement discrepancies belong to layer 8.

### HIGH — Visibility and action legality have incompatible definitions

[Observe's three nearby lists](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:301) do not apply CanSeeTarget. Survey and the quest selector do. A shared primitive player cannot depend on which subsystem happens to consume the census to decide whether it knows a hidden target.

[IsTargetable](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotPerceptionSnapshot.cs:352) describes legal attack targets but accepts every living Character and living NPC deemed hostile. It does not establish CanAttack or visibility. The [hostility helper](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/CombatDecisionTree.cs:73) accepts null-faction fixture NPCs and returns true on exceptions.

[IsInteractable](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotPerceptionSnapshot.cs:265) generally means within the broad interaction band, rather than that a specific action is legal. Doodad interaction additionally rejects any scheduled despawn timestamp, including a future one. Harvestability is read through a replay helper; phase capability alone does not prove ownership permission, labor, range, or inventory readiness.

**Classification:** SERVER ALREADY KNOWS visibility and many legality gates; IMPLEMENTATION BUG / MISSING OBSERVATION in the projected meanings. Unknown eligibility should remain unknown.

### HIGH — Missing coverage becomes a confident empty or safe world

[GetAround](/root/aaemu-dev/AAEmu.Game/Core/Managers/World/WorldManager.cs:1250) returns empty when the observer lacks a region. ActorObservation cannot distinguish that from a successful empty census. Snapshot skips unresolved rows without positions, and belief can consequently produce zero enemies and Threat.None.

[LifecycleOf](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotPerceptionSnapshot.cs:327) calls first sight Spawned, and changes a dead NPC from Corpse to Dead when loot is out of reach. [Delta](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PerceptionDelta.cs:248) calls disappearance Removed. These are observed transitions, not authoritative spawn, depletion, or object lifecycle events.

[HealthChanged](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PerceptionDelta.cs:230) compares only NPC HP. Radar explicitly excludes the observer; therefore [SelfHurtThisFrame](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Belief/BeliefInterpreter.cs:439) cannot detect ordinary self damage through this pipeline. [ThreatOf](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Belief/BeliefInterpreter.cs:409) also returns None before considering damage whenever enemyCount is zero. A ranged attacker beyond the census or non-NPC damage exposes this gap.

**Classification:** MISSING OBSERVATION for valid coverage, self damage, and actual engagement; IMPLEMENTATION BUG for the unreachable self-damage signal. Physical lifecycle and observation lifecycle need distinct meanings.

### HIGH — Repeated failure substitutes for resource availability

[TrySpawnerPosition](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2498) chooses the first matching configured NPC spawner location. It does not establish active population, schedule, or current materialization.

The [objective-source arm counter](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2106) withholds Doodad travel after three same-source arms until the quest set changes. Its prey exemption is justified by a recorded quest-251 failure. The remaining assumption that three attempts establish Doodad unreachability is unsupported by observation.

[NpcSpawner](/root/aaemu-dev/AAEmu.Game/Models/Game/NPChar/NpcSpawner.cs:22) already tracks population and activation conditions, including proximity-driven materialization. [DoodadSpawner](/root/aaemu-dev/AAEmu.Game/Models/Game/DoodadObj/DoodadSpawner.cs:232) schedules respawns. Expose bounded state and UNKNOWN where prediction is unavailable. Do not turn private scheduler details into a promise of exact future availability.

**Classification:** SERVER ALREADY KNOWS substantial runtime state; MISSING OBSERVATION forces retry heuristics. What sites a player may know is a GAME-DESIGN DECISION.

### MEDIUM — Rich perception is disconnected, and capture boundaries are incomplete

The rich stack is intentionally unused. Its tests establish local/contract properties, not useful production observation. Production consumers have different adapters for the same semantics.

[Radar.Project](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRadarProjection.cs:170) combines a supplied observer position and IDs with later live lookups, then stamps projection time. Snapshot resolves live objects again. Neither establishes an original capture version or observer-instance boundary across all facts. Immutable output does not guarantee a coherent capture.

**Classification:** GAME-DESIGN DECISION for staged integration; MISSING OBSERVATION for provenance and validity. Correct these contracts before migrating consumers. A fresh dispatch check remains necessary afterward.

## E. Hidden Assumptions

- Null faction and failed hostility reads produce attackable fixture behavior.
- Managed bot accounts imply a cooperating cohort even without an engine party.
- A pinned quest corpse implies ownership after tagging disappears.
- Twenty-five metres is treated as enough knowledge for several decisions; it is only the present census extent.
- First sight means Spawned, absence means Removed, and nonempty nearby loot means owned opportunity.
- Three Doodad-source arms imply obstruction; the prey exception embeds knowledge from quest 251's measured lane.
- Attention labels imply a current goal or plan without receiving either.
- A harvest capability reader is housed in M3aM4ReplayScenario. Its data-based implementation is worth extracting; its location is not evidence that every answer is fake.
- Remembered vendor/farm/workbench locations in the adjacent planning projection may be legitimate memory or supplied setup. Their discovery is not proven by the projection.

No fresh inspected live scenario establishes where initial locations, inventory, or targets were acquired. Teleports, stocking, event injection, manual waking, and pre-seeded surfaces must be recorded in each later field proof rather than assumed absent.

## F. Missing Authoritative Truth

| Missing fact | Available server basis | Uncertainty class |
|---|---|---|
| Observer-visible entity | CanSeeTarget and region/world membership | SERVER ALREADY KNOWS; MISSING OBSERVATION |
| Legal attack versus hostile relationship | CanAttack, faction relation, current entity state | SERVER ALREADY KNOWS |
| Actual engagement / self damage | CurrentAggroTarget, combat state, self HP history | SERVER ALREADY KNOWS; MISSING OBSERVATION |
| Caller-relative corpse opportunity | Container generation/distribution state and normal loot paths | SERVER ALREADY KNOWS; missing query |
| Doodad capability and eligibility | Current function group, skill/function data, normal permission/resource gates | SERVER ALREADY KNOWS substantially; gate completeness DEFER |
| Configured site versus available resource | Spawner population, activation, scheduled respawn, live entities | SERVER ALREADY KNOWS substantially |
| Whether absence is meaningful | Capture validity, extent, failed lookup and instance identity | MISSING OBSERVATION |
| Permitted prior knowledge of distant sites | Player-simulation design | GAME-DESIGN DECISION |
| Exact retail client presentation/discovery rules | Not established by inspected server code | UNKNOWN; no blocking RE claim |

## G. Ownership Problems

A common observation adapter should own capture and engine-facing reads. Normal gameplay models/services own eligibility. Belief owns interpretation of explicitly supplied facts. Goal arbitration owns goal relevance. Memory owns durable discoveries and their provenance.

Move harvest capability reading out of the replay scenario. Replace ownership interpretation duplicated across snapshot, belief, attention, and loot planning with one caller-relative source. Keep dispatch revalidation: it protects against changed world state and is not redundant observation.

**CROSS-LAYER QUESTION:** Which layer-3 owner supplies current goal relevance to attention and decides useful versus merely available opportunities? **DEFER TO:** layer 3.

**CROSS-LAYER QUESTION:** What are the exact engine loot and Doodad permission gates, including public conversion and per-entry distribution? **DEFER TO:** layers 8 and 9.

## H. Generalization Risk

Different quests and zones expose unavailable sites and offers that are not usable by this player. Higher density makes nearby-hostile counts resemble danger even without aggression. Different ownership and multiple bots expose fictional party membership and pinned ownership. Greater distance turns census loss into apparent removal and suppresses threat evidence.

Different combat situations expose friendly Characters classified as targets and missed self damage. Different inventory, labor, or level exposes capability mistaken for executable opportunity. Restart or a world/instance transition can compare frames without a valid continuity boundary; durable memory recovery is deferred to layer 12.

## I. ArcheAge Reverse-Engineering Questions

**No blocking CLIENT RE NEEDED finding is established for this layer.** The principal gaps are server observation and semantic interpretation, not missing client behavior.

Do not RE tagging, spawn availability, hostility, or Doodad phase capability before reading/exposing the server truth already identified. Exact client discovery/UI rules remain UNKNOWN. If later evidence shows a material mismatch, isolate a question then with its packet/Lua target and the decision it would change.

## J. Live Proof Needed

Run one sustained ordinary-world session with an unpartied bot, another unpartied bot, and a human competitor; no forced party equivalence. Start with declared ordinary inventory and only permitted prior knowledge. Let the bot discover a merchant, fight, encounter contested corpses, approach an inactive/depleted resource site, and find another useful activity. Include an attacker outside the current census and a real visibility-hidden entity.

For each decision record capture validity/extent, observed facts, remembered source, engine gate result, chosen action, and resulting ordinary inventory/quest state. Require no target-ID injection, teleport rescue, stocked completion items, or manual wake driving. Controlled respawn/public-loot timing may diagnose a failure, but the accepted autonomy result must use normal ecology.

Observe a restart and return to the same world as a separate boundary check, without claiming persistence ownership is audited here. Success means coherent observation supports continuing useful activity, not a count of quest-251 gates.

## K. Recommended Changes

1. **Define one capture contract:** observer/world/instance identity, capture sequence/time, coverage validity and extent, and explicit unresolved facts. Distinguish seen/lost from actual spawned/despawned. Preserve the bounded census.
2. **Expose caller-relative loot eligibility:** a read-only engine query over real container/distribution state, with per-entry/rule uncertainty where needed. Remove bot-account party equivalence and pin-as-ownership inference when consumers migrate.
3. **Correct actionable semantics:** share CanSeeTarget and normal attack/interaction gates; separate capability, range, permission, and resource readiness. Extract the Doodad capability reader from replay code.
4. **Expose bounded resource-site state:** configured site, current population/materialization, known respawn/schedule state, and explicit UNKNOWN. Replace retry-derived availability verdicts; travel still owns execution failures.
5. **Migrate one production consumer after corrections:** share facts while retaining dispatch checks. Add self HP deltas and actual engagement inputs before trusting threat; leave goal-ranking policy to layer 3. Change the isolation test intentionally.

No new GOAP layer, global entity cache, or watchdog is justified by these findings.

## L. Do Not Touch

Keep ordinary Character inventory/economy/quest truth, region-based bounded collection, deterministic sorting/diff computation, nullable facts, survey visibility predicates, and quest target engine gates. Keep the typed stack isolated until corrected. Preserve local diagnostic tests as diagnostics without treating them as live autonomy proof.

## M. Verdict

**STRUCTURAL-ISSUE.** This layer contains good readers and useful pure projections, but their vocabulary does not consistently mean what a player can know or do. Production also maintains independent interpretations, including bot-only loot sharing and retry-derived resource conclusions. The highest leverage is a small authoritative observation contract and a few engine queries, followed by incremental consumer migration. Wiring the existing rich stack directly into decisions would increase infrastructure use without proving generalized autonomy.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P1. **Canonical owner:** A bounded gameplay observation adapter; engine services own legality and rights.

**First reviewable outcome:** P1.1: make one capture distinguish valid coverage, absent-in-coverage and unresolved facts; correct current-mask prerequisite evaluation with layer 11.

| Phase | Work owned by this layer |
|---|---|
| P1 | Add caller-relative opportunity facts and resource-site availability, actual aggression, phase readiness and exact loot receipts. |
| P2 | Feed readiness/blockers to the strategic owner without embedding goal preference. |
| P3 | Move one quest and one nonquest consumer onto corrected facts; keep final engine checks at dispatch. |
| P6 | Compare consumers across held-out supported terrain, density and ownership; profile bounded queries. |

**Dependencies:** Engine eligibility/receipt seams from layers 8–9 and threat semantics from 7/10. An unsupported query is an explicit gap, not false availability.

**Live stopping point:** Both consumers act on the same factual eligibility/coverage semantics, and stale observations fail normally without invented ownership.

**Preserve:** Region-bounded collection, deterministic projections, nullable facts and ordinary inventory/quest truth.

**Defer:** Wiring the entire quarantined perception stack, a global cache/blackboard or bot-account rights shortcuts.


## Cross-layer notebook

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|
| 1 — Scheduler/runtime/lifecycle | STRUCTURAL-ISSUE | Tick-marshalled execution and terminal request lifecycle | Enrollment and embodiment continuity | Lifecycle coordinator; scheduler owns timing; actor owns requests | Current embodiment + explicit next scheduling disposition | Layer 2 — observation |
| 2 — Observation/perception/world-state | STRUCTURAL-ISSUE | Bounded engine reads and pure deterministic projections | Incompatible eligibility and absence semantics | Common observer adapter over ordinary gameplay; belief interprets | Caller-relative opportunities + valid coverage/resource availability | Layer 3 — arbitration; rights/gates deferred to 8–9 |

## Evidence provenance

HEAD: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d`; existing dirty tree included. Archaeology invoked through the local Release stdio server. Inputs: `list_sources {}`; `list_tables {}`; `search_files {pattern:"EligiblePlayers|MakeLootPublic|CharacterTagging|CanSeeTarget",root:"game-source",glob:"LootingContainer.cs",limit:25}`; `search_files {pattern:"AllowedToInteract|IsTargetable|BuildCorpse",root:"game-source",glob:"BotPerceptionSnapshot.cs",limit:12}`. Source search results: 19 and 6 matches respectively, one file each, no truncation. Generated provenance: 2026-10-07 16:47:30–31 UTC. Source ID `game-source`, path `/root/aaemu-dev/AAEmu.Game`; search provenance version null, catalog identifies fork develop. Confidence **textual**, corroborated with bounded filesystem reads. Source evidence is code, not live/client/H.

Catalog: canonical DB, ArcheAge 1.2 r208022, 679 tables; measured MD5 `78b3bdbf038db3b927056106efdf91af`. No reference-row or client-behavior claim was inferred from catalog counts. See the [archaeology acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md). No gates or live proofs ran during this audit.

