# PlayerBot persistence, dormancy and restart recovery audit

Author: Astra. Layer 12. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Preserve ordinary character gameplay through normal persistence, and preserve a small durable player intent that can be reconciled against freshly loaded authoritative state. Dormancy should remove embodiment, then rehydrate one current Character and safely resume or replace the intent. Never persist a live actor request as if it survives restart.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| Character load/save lifecycle | KEEP | Correct canonical gameplay storage boundary. |
| PlayerBotMetadata / store | KEEP + HARDEN | Additive bot data, write-through and retry hooks; absence and failure need distinct handling. |
| DormantBotRegistry | REFACTOR | Fresh load is not consistently the Character activated. |
| BotProgressionCursor | DEFER | Pure bounded cursor exists, but no production persistence integration was found. |
| Travel/GOAP/quest memory | MOVE OWNERSHIP | Ephemeral execution should reconcile to one durable intent, not become parallel gameplay storage. |

## C. What Is Good

Metadata stays outside Character gameplay, leaving inventory/quests to normal persistence. [DormantBotRegistry.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:248) dematerializes through the lifecycle service. Materialization latency is passive instrumentation. Saved nonzero positions are preserved rather than unconditionally teleporting bots home ([DormantBotRegistry.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:336)). The cursor advances against supplied actual holdings/completed quests, not fabricated rewards.

## D. Main Problems

**HIGH — The freshly loaded Character can be discarded.** [DormantBotRegistry.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:220) loads and restores a Character, but if a deactivated runtime already exists it skips Spawn and calls Activate by ID. [PlayerBotManager.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotManager.cs:75) activates runtime.Character, retained by a read-only property. Thus same-process rematerialization and fresh-process hydration do not have the same state source. Stale state and stale actor references can survive a successful-looking wake.

**HIGH — Durable planner storage is not durable intent execution.** [BotProgressionCursor.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotProgressionCursor.cs:9) says the cursor has a metadata consumer, but a bounded repository search for BotProgressionCursor and RecordPlannerState found only their declarations. The reviewed production executor keeps GOAP contexts/runners in per-process dictionaries ([BotRoamStepExecutor.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:2733)); travel and quest execution memory is also ephemeral. No resumable production cursor is established by these source paths.

**MEDIUM — Transient read failure becomes durable in-memory absence.** [PlayerBotMetadataStore.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotMetadataStore.cs:90) caches Empty when load fails or no row exists. [DormantBotRegistry.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:106) returns an empty discovery set on DB failure, and EnsureDiscovered caches that first set ([DormantBotRegistry.cs](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs:307)). A temporarily unavailable DB can hide citizens/configuration for the process lifetime. This is missing failure-state handling, not a need for another behavioral watchdog.

## E. Hidden Assumptions

Home metadata and recognizable managed-account names drive discovery. Empty metadata falls back to defaults. The Tier0 cursor embeds quests/items in its ordered path and is not a general intent schema. Restart proofs that merely echo Schedule/PlannerState bytes prove storage, not production resumption.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: saved Character, inventory, quest completion, position, house/crop ownership. MISSING OBSERVATION: whether a stored intent remains executable after reload and whether a metadata read failed versus the row being absent. IMPLEMENTATION BUG: loaded embodiment can differ from activated embodiment. GAME-DESIGN DECISION: whether dormant citizens simulate any off-screen activity; this audit does not demand such simulation.

## G. Ownership Problems

The lifecycle coordinator owns Character generation/replacement and retirement of old execution state. A durable intent adapter owns goal/binding version and resumption policy. Character persistence remains canonical for gameplay. Metadata JSON is storage, not evidence that a scheduler/brain consumes it.

## H. Generalization Risk

Restart loses travel destination, selected opportunity and failure context while retaining real quest/inventory progress. Same-process dormancy may retain old contexts and activate the old Character. Missing metadata or initial DB failure changes behavior to defaults. Home fallback currently ignores the returned home world ID; cross-world fallback needs explicit placement reconciliation, not only XYZ restoration.

## I. ArcheAge Reverse-Engineering Questions

No client RE is needed for metadata, generation, restart or durable intent. These are server implementation concerns. CROSS-LAYER QUESTION: which ordinary logout-save completion guarantees does the lifecycle service provide during abrupt termination? DEFER TO: a targeted lifecycle persistence proof; do not infer crash durability from graceful-save source.

## J. Live Proof Needed

Let a bot self-select a goal and acquire some inputs, then interrupt it during travel, after planting, and before quest reporting. Test both graceful dematerialization/rematerialization and process restart. Verify DB gameplay state, preserved high-level goal, re-observed targets and absence of duplicate spending/rewards. Change the target while it is offline. Also start with metadata temporarily unavailable and recover the DB. No seeded planner JSON or manual rearming may supply the lost intent.

## K. Recommended Changes

1. Make materialization activate the freshly loaded Character under one lifecycle generation, retiring old actors and contexts.
2. Persist only a versioned high-level intent and useful discovered bindings; re-observe legality and reconstruct execution after reload.
3. Distinguish metadata/discovery failure from absence and permit a bounded explicit reload after recovery.
4. Wire a production intent consumer before promoting PlannerState/cursor storage as restart autonomy; keep unsupported schemas visible.
5. Run the mid-activity dormancy/restart field proof with gameplay and intent evidence, not only byte-equal metadata.

## L. Do Not Touch

Normal Character load/save, additive metadata schema, retry-on-write-failure intent, pure cursor advancement, preserved saved positions and passive latency measurements. Do not build offline inventory/quest progression or serialize entire live planner/request objects.

## M. Verdict

**STRUCTURAL-ISSUE.** Ordinary gameplay persistence and additive bot metadata are appropriate foundations. The missing piece is production reconciliation of durable intent with one newly hydrated embodiment. Until that exists, a citizen can return with its possessions but lose its purpose—or resume against stale process objects. This prevents continuous player simulation across dormancy and restart.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0 / P5. **Canonical owner:** Lifecycle owns current Character; a versioned durable intent adapter owns resumption reconciliation.

**First reviewable outcome:** P0.2: ensure rematerialization activates the freshly hydrated Character, not a retained stale runtime.Character.

| Phase | Work owned by this layer |
|---|---|
| P0 | Unify hydration/activation generation and retirement of obsolete actors/requests. |
| P2 | Define minimal high-level intent identity/disposition alongside the selected task; keep execution pointers ephemeral. |
| P5 | Persist and consume that schema; separate read failure from absent rows and re-observe targets/resources on recovery. |
| P6 | Interrupt the already proved ordinary chain at travel, acquisition and report boundaries without rearming. |

**Dependencies:** P0 generation integrity, P2 one intent and P4 an actual activity to resume. A serialized cursor declaration is not its production consumer.

**Live stopping point:** Dormancy and process restart preserve purpose and canonical possessions while rebuilding legal execution with no duplicate spend/reward.

**Preserve:** Character load/save, additive metadata, write retry and saved positions.

**Defer:** Offline quest/inventory simulation, complete live-request serialization or storage-only autonomy claims.


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

## Evidence provenance

Read-only archaeology list_sources + search_files(root=game-source, glob=**/DormantBotRegistry.cs, pattern=Materialize|SetPosition|SetWorld|HasHome|reading as empty, limit=40): returned 0 matches, not truncated, 2026-10-07T17:28:11Z; no positive code claim is based on that empty result. Direct local source reads and bounded rg searches supply textual findings. Source game-source /root/aaemu-dev/AAEmu.Game, version null; catalog fork develop. Canonical DB md5 78b3bdbf038db3b927056106efdf91af, 1.2 r208022. No live/client/H evidence. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

