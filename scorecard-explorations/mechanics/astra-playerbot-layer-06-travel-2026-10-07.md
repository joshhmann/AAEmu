# PlayerBot travel and navigation audit

Author: Astra. Layer 6. Date: 2026-10-07.

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty working tree. Source audit only; no builds, tests, live runs, gameplay changes, commits, or deployments. The only repository edits are audit documents.

## A. Intended Responsibility

Turn selected travel intent into legal bounded movement, preserve journey identity across retries, and report arrival/obstruction/target loss truthfully. Distinguish route topology, navigation availability, motion execution, and action-specific arrival. Travel does not choose quest goals or infer depleted resources.

## B. Actual Responsibility

| Component | Classification | Reason |
|---|---|---|
| TravelBrain pure policy | KEEP + HARDEN | Explicit modes/terminals and wrong-world checks are useful; failure causes need better inputs. |
| TravelIntentStore | KEEP + HARDEN | Owner-scoped counters prevent cross-leg resets; global clear and rearm semantics need correction. |
| TravelBrainPlanner | KEEP + HARDEN | Reuses normal world lookup and roads; a road graph is not proof of traversability. |
| TravelLegDispatch outcome adapter | REFACTOR | Parses audit text and maps several rejections to Running. |
| MoveTo/NavigateTo/MoveToUnit | REFACTOR | Share one mover, but callers have materially different navigation guarantees. |
| Actor motion lifecycle/progress tracking | KEEP + HARDEN | Terminal/time-budget mechanics are sound; geometry and elapsed-time policy need validation. |
| BotRoam route ownership | MOVE OWNERSHIP | Route replacement must follow explicit task ownership, as layer 3 established. |

## C. What Is Good

[TravelIntentStore](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Travel/TravelIntentStore.cs:88) keys by actor and leg owner. [EnsureJourney](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Travel/TravelLegDispatch.cs:109) preserves an existing same-target journey instead of refreshing its budget every wake. Wrong-world and unresolved-target outcomes are named. All verbs ultimately use one ActorRequest mover; no second motion thread was found.

## D. Main Problems

**HIGH — Missing/no-path navigation becomes straight-line movement.** [NavigateToInternal](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:454) tries geodata and a local obstacle detour, then calls StartMove even after pathfinding fails. [MoveTo](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:352) starts a direct leg. [ApplyCharacterMove](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4500) applies the resulting position; this inspected path does not validate each segment against walkable collision geometry. Progress/stuck timers cannot detect passing through a wall if Transform keeps advancing. A road waypoint or height clamp does not establish cave/deck/slope traversal.

**HIGH — Refusal and ownership are reconstructed from trace vocabulary.** [MapLegOutcome](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Travel/TravelLegDispatch.cs:460) treats most Rejected results as Running, and recognizes caller retirement by detail strings. It scans recent rows by owner/target rather than holding the exact request identity. A new request can inherit an older journey outcome; a changed rejection string can silently change repath accounting.

**MEDIUM — Journey memory can erase its own terminal/budget.** [Arm](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/Travel/TravelIntentStore.cs:108) preserves counters on same-destination refresh but always constructs Terminal.None. EnsureJourney avoids that in its path, yet other rearm callers need an explicit retry/new-journey contract. At 256 entries Arm clears all actors' intents. Named sticky abandonment is therefore not robust population-wide.

## E. Hidden Assumptions

Default radii/local-mode/repath budgets are policy, not universal gameplay gates. Two resolve misses do not identify why a target disappeared. Retreat uses a geometric point opposite the threat, not a verified safe navigable destination. Caller-retirement strings encode scenario-specific recovery history. The actor mover enforces a minimum dt of 0.05 seconds per move tick; cadence independence needs a measured check.

## F. Missing Authoritative Truth

SERVER ALREADY KNOWS: world/instance membership, current positions, loaded geodata/obstacles and normal movement effects. MISSING OBSERVATION: geodata unavailable versus no valid route, validated segment/arrival capability, and exact request outcome/owner. UNKNOWN: completeness of collision coverage for every supported world surface.

## G. Ownership Problems

Travel owns the journey; the actor owns one request and motion application; engine geometry supplies legal traversal facts. Replace trace-text inference with explicit terminal/request identity. CROSS-LAYER QUESTION: what reach spec is required by each NPC/Doodad/loot action? DEFER TO: layers 8–9. Persistent travel intent is layer 12.

## H. Generalization Risk

Different distances and moving targets stress budget preservation. Different zones and caves/decks expose fallback geometry. Full population clears reset unrelated journeys. Different interaction ranges produce arrival without eligibility. Restart or instance replacement must invalidate route knowledge and revalidate the intent before new movement.

## I. ArcheAge Reverse-Engineering Questions

No unconditional client RE requirement established. First measure server geodata/obstacle coverage. Status: UNKNOWN; RE FIRST only if a required collision surface is absent.

- QUESTION: Which r208022 collision/navigation assets encode that demonstrated missing surface?
- WHY SERVER IS INSUFFICIENT: Only after measuring absent server geometry for that surface; no such absence was proved in this audit.
- LIKELY CLIENT/LUA/PACKET TARGET: Client collision/navigation assets in game_pak and normal movement traces.
- WHAT ANSWER WOULD CHANGE: Asset ingestion or explicit unavailable traversal, rather than another unstick threshold.

## J. Live Proof Needed

Walk an ordinary route with an actual obstacle, elevation change, and a moving target; require normal interaction eligibility at arrival. Repeat with navigation intentionally unavailable as a diagnostic: it must report that condition, not claim successful walkability. Verify route/request identity through a survival interruption and continued intent. No teleports or injected paths in the accepted field proof.

## K. Recommended Changes

1. Make navigation availability/no-path explicit; allow direct fallback only where a segment is positively validated.
2. Carry exact request identity and structured retirement/refusal disposition into travel.
3. Define new-journey versus same-intent refresh; preserve sticky terminals unless explicitly retried.
4. Replace population-wide memory clear with targeted lifecycle cleanup/eviction that cannot reset active budgets.
5. Use action-specific reach specs and measure movement against elapsed time and ordinary traversal.

## L. Do Not Touch

Keep owner-scoped journeys, EnsureJourney, finite-vector checks, wrong-world checks, one actor mover, normal Transform/buff updates, bounded timeouts and named terminals. Do not add another travel scheduler.

## M. Verdict

**STRUCTURAL-ISSUE.** The travel decision vocabulary is stronger than the execution guarantees beneath it. It can preserve and explain bounded journeys, but unknown pathfinding becomes direct motion and some outcome ownership is inferred from strings. Geometry truth and exact request association must precede more recovery thresholds.

## N. Rework direction P0 to P6

Added 2026-10-07 at the user's request. Direction only; original findings/verdict and evidence boundary remain unchanged. See the [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) for phase definitions and shared work packages.

**Start:** P0 / P3. **Canonical owner:** Travel owns journey/request binding; one actor applies movement; engine geometry supplies traversal truth.

**First reviewable outcome:** P0.4/P3.1: carry the exact in-flight movement request through the runtime and classify its actual terminal without log-string inference.

| Phase | Work owned by this layer |
|---|---|
| P0 | Keep pending movement owned; stop unsafe fallback on explicit no-path unless the direct segment is positively validated. |
| P1 | Expose route availability versus unknown; distinguish configured spawn destination from a currently materialized target. |
| P3 | Unify action-specific arrival, refresh versus new-journey identity and request retirement. Reconcile existing reporter-walk arms before adding any. |
| P5 | Retire travel per actor/generation and reconstruct a journey from durable purpose. |

**Dependencies:** P0 request continuity, P1 target/geometry truth and layer 9 reach requirements. E-42 and E-46/E-47 record an existing arm and healthy chunk turnover; do not repeat superseded failure explanations.

**Live stopping point:** An obstructed ordinary route reaches an action-legal point or reports an honest route failure; refreshed intent does not erase terminal history/budgets.

**Preserve:** Owner-scoped journeys, EnsureJourney, finite/wrong-world checks and one mover.

**Defer:** Another travel FSM/scheduler, global geometry guesses or RE before measured missing assets.


## Cross-layer notebook

| Layer | Verdict | Strongest part | Weakest part | Canonical owner | Biggest missing truth | Next dependency |
|---|---|---|---|---|---|---|
| 1 — Scheduler/runtime/lifecycle | STRUCTURAL-ISSUE | Tick-marshalled execution and terminal request lifecycle | Enrollment and embodiment continuity | Lifecycle coordinator; scheduler owns timing; actor owns requests | Current embodiment + explicit next scheduling disposition | Layer 2 — observation |
| 2 — Observation/perception/world-state | STRUCTURAL-ISSUE | Bounded engine reads and pure deterministic projections | Incompatible eligibility and absence semantics | Common observer adapter over ordinary gameplay; belief interprets | Caller-relative opportunities + valid coverage/resource availability | Layer 3 — arbitration; rights/gates deferred to 8–9 |
| 3 — Goal arbitration/top-level ownership | STRUCTURAL-ISSUE | Deterministic legality-first selection on the shared execution path | Elected activity does not own execution or persistent intent | One strategic owner; modules supply goals; executors own bounded tasks | Opportunity readiness + intent/execution disposition | Layer 4 — quest interpretation; route/recovery/planning deferred to 6/10/11 |
| 4 — Quest interpretation | HEALTHY-BUT-NARROW | Server-derived objective facts and explicit unsupported vocabulary | Single-objective/single-source reduction | Quest projection over server quest components | All outstanding objectives and viable acquisition alternatives | Layer 5 — execution |
| 5 — Quest execution | STRUCTURAL-ISSUE | Shared actor dispatch and live objective counters | Async ownership and retry state conflict | Per-intent quest behavior; engine owns progress | Terminal outcome disposition and real blocked cause | Layer 6 — travel |
| 6 — Travel/navigation | STRUCTURAL-ISSUE | Owner-scoped journeys and shared movement requests | Unknown navigation falls back to unvalidated straight movement | Travel intent + one actor mover; engine geometry adapter | Route availability and action-specific reachability | Layer 7 — combat |

## Evidence provenance

Archaeology list_sources {}; search_files {root:game-source, glob:TravelIntentStore.cs, pattern:Terminal|Intents.Clear|public static bool Arm, limit:40}; 20 matches, no truncation, 2026-10-07T17:17:59Z. source_id game-source; /root/aaemu-dev/AAEmu.Game; search version null, catalog fork develop. Textual/code, not live/client/H. DB MD5 78b3bdbf038db3b927056106efdf91af, 1.2 r208022. [Acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
