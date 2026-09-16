# PlayerBot Target Architecture — Consolidated v1 Candidate

> RESEARCH / DESIGN ONLY. No AAEmu code modified, built, tested, or committed for this draft.
> Merges: `playerbot-target-architecture-proposal.md` (Muse proposal) + `playerbot-target-architecture-independent-review.md`
> (independent review, "Astra"). Basis only — no new prior-art survey, no new architecture invented here.
> AAEmu tree: `/root/aaemu-dev`, branch `develop`, HEAD `d0e2d72d6fe9b3de33a9659c1fe90e13ce85b012`
> (dirty worktree with concurrent-sibling edits; line numbers ±20 drift; symbols are the stable keys).
> Every claim: OBSERVED FACT / SERVER EXPLANATION / ARCHITECTURAL INFERENCE / DESIGN CHOICE.
> Every concept: LOCK NOW / PROVISIONAL / DEFER (§4). Conflicts resolved in §7 with PROPOSAL / REVIEW / FINAL rows —
> disagreements are recorded, never smoothed over.

## 1. Executive decision

**Adopt Option A — Minimal Extension, as revised by the independent review.** One lifecycle (`ActorRequest`), one new async verb
(`DeferUntil`-shaped, name not locked), leg-local explicit phases, narrow PAC sequencing contract, minimal `ReachSpec` parameter object,
per-family eligibility from engine/handler/trace — and nothing else. All deferrals from the proposal stand (Travel FSM, Blackboard, Task
chain, InteractionSession, Utility/GOAP, LOD, second anything), with revisit triggers preserved in §28.

The review changed the proposal in **eight load-bearing ways** (full table §7): (1) Harvest is NOT shared-path today — convergence through
`Skill.Use` is pre-work, not an assumption; (2) GCD bypass is a real parity gap — converge onto the human gate, still no second engine;
(3) interrupt = abandon + new request — all pause-shift/resume language deleted; (4) canonical ladder letters preserved — B2 is a threshold
inside B, E keeps its embodiment meaning; (5) "zero production callers" narrowed — zero *autonomous-selection* callers, with PlayCinema and
bridge reachability carved out; (6) RunningPhase lives leg-local first — request annotation only on proven inspector need;
(7) failure vocabulary stays at the existing 7 values — routing concepts without enum expansion; (8) U3/observer is NOT a correctness gate —
correctness ships on U1+U2, observer capture is a separate work item. Plus two newly-scoped pre-work gaps: queue-backstop ledger parity and
actor-Buy atomicity.

**Confidence: HIGH** on direction (cross-source: traces + server source + client DB + tests); MEDIUM on the four convergence shapes
(Harvest seam, GCD seam, ledger fix, Buy rollback), which are PROVISIONAL until Phase-0 decisions land.

### Project positioning — additive to ongoing AAEmu completion

This document governs the **PlayerBot execution/parity architecture** and the shared engine seams that PlayerBots touch. It is **not a replacement for the broader AAEmu completion roadmap**, and it does not freeze unrelated emulator work. Ongoing work to finish AAEmu — game systems, content, quests, world behavior, networking, vehicles, housing, economy, combat, tools, tests, fixes, and other milestone work already in flight — continues in parallel.

The rule is scope-based: when broader AAEmu work changes a semantic path that PlayerBots consume, this architecture must re-ground against that new engine truth; when work is unrelated to PlayerBot execution/parity, it should proceed without waiting on this document. Likewise, existing PlayerBot work and previously established milestone/scorecard decisions remain in force unless this document explicitly revises them. The architecture is an **overlay and review contract for new or migrated PlayerBot behavior**, not a project-wide rewrite or a stop-the-world migration.

## 2. Evidence basis

Precedence applied throughout: (1) live human traces — 10 files, 13,959 records, single actor Dingus id 2, 2026-09-14
(`traces/player-actions/`); (2) AAEmu server source at the above SHA; (3) client/game archaeology (`compact.sqlite3` 1.2 r208022:
seed 15659 → skill 25536 → crop 2259 → phases 4379→4456→4457→4458→4459 → harvest skill 13980 → loot pack 6452; game_pak x2ui Lua
presentation only); (4) current tests/runtime contracts (meanings from assertions, nothing executed); (5) prior art — execution patterns
only, never ArcheAge semantics; (6) inference, always labeled. Trace never overrules engine ownership where trace visibility is incomplete
(talk-no-gate, doodad range exemption, GCD parameters stand over trace silence). Prior art never locks behavior.

## 3. Canonical progression model

LOCK NOW. P = PLAYER ENGINE PATH, B = PLAYERBOT PARITY, L = GAMEPLAY LOOP, A = AUTONOMOUS PLAYER, N = POPULATION COEXISTENCE,
G = GUILD / GROUP SOCIETY, T = TERRITORY / AURORIA (`PLAYERBOT_PROGRESSION_AND_TESTING_FRAMEWORK.md:19–40`). **B2 is a completion
threshold inside canonical B for one action family — not a rung, not a rewrite.** E = Embodiment / Presentation Fidelity, orthogonal;
B2 ≠ E2 (a headless bot with correct semantics is B2; a networked bot with synthetic credit is not).

## 4. What is LOCK NOW

Policy-grade, cross-source supported. Each is an invariant in §27 and review-enforceable now:

1. `ActorRequest` is the ONE action lifecycle (Requested→Accepted→Running→Completed/Rejected/Interrupted/TimedOut); terminals final.
2. Interruption = abandon; continuation = new request referencing its predecessor. No pause/resume language anywhere.
3. Bots and humans differ in intent source, not in human-path engine semantics. No silent bypass of human-path gates on parity paths.
4. No second scheduler, movement integrator, cooldown tracker, or game-rule engine. Engine refusal is data; duplication is a bug.
5. Discovery range ≠ action eligibility. No universal arrival radius. Every gate from engine, handler, trace, or explicit policy.
6. Movement has ONE authoritative execution seam; every reach names its arrival predicate; travel ends on arrival.
7. Presentation never alters correctness; authored fake skill packets are fabrication, deleted — never "moved to presentation."
8. Humanization defaults OFF, ledger-tagged, never on canonical mechanics.
9. Synthetic-only evidence is GAME-STATE-EVIDENCE: supports P at most, never B.
10. No abstraction promotes from one user. No global scan per wake. No Travel FSM / Blackboard / chain / session / planner / LOD
(deferrals locked with triggers, §28).

## 5. What is PROVISIONAL

Working design, needs trace/second-family proof. Each names its promotion evidence in §28:

- PAC as contract + leg-local pattern (not a class mandate). Promote to module only on third-family cross-leg need.
- `DeferUntil`-shaped verb (name/shape open): Tick-sited read-only predicate + existing Deadline; hold-Running and audit-only
post-terminal uses; abandon-not-resume. Promote shape to LOCK after Two-Potatoes exercises both shapes + maturity.
- Minimal `ReachSpec`: target + arrival predicate + deadline. Suppression/retry/re-resolve stay adjacent reach-layer metadata until a second family proves they belong in the shared parameter object. Promote fields only on duplication.
- Leg-local phase (not request field). Promote to request annotation only on proven live-inspector need.
- MoveToUnit opt-in default-off re-resolve (cadence UNKNOWN, trace-gated). Enable by default only on measured cadence.
- GCD convergence shape (human `false` path vs scoped justification) + refusal tracing. LOCK the no-bypass rule now; shape after trace.
- Harvest-via-`Skill.Use` seam; Buy atomicity + convergence shape; B2 thresholds (range/facing numbers pending re-trace).

## 6. What is DEFERRED

Do not build. Revisit triggers in §28; deferral loses no information:

Travel FSM / `TravelTarget` hierarchy · generic Blackboard / Values · generic Task chain / task graph · `InteractionSession`
(presentation-only at most, Buy-gated) · 13-value failure enum (keep 7) · Utility AI / GOAP / action scripting · simulation LOD /
population model · universal timeout engine · general arrival/facing framework · humanization system · core fork / content ports ·
observer-fidelity claims · scaling machinery before profiling.

## 7. Reconciliation table

| Topic | Muse Proposal | Independent Review | Final Consolidated Decision | Status | Evidence / Reason |
|---|---|---|---|---|---|
| ActorRequest | Single lifecycle + Running-substate metadata | CONFIRM lifecycle; phase leg-local-first | Single lifecycle LOCKED; phase leg-local, audit-copy only | LOCK / PROVISIONAL | `ActorRequest.cs:1–206`; nested begin impossible by busy-reject |
| RunningPhase | Annotation on request | Leg-local enum first | Leg-local `LegPhase`; promote to request field only on inspector need | PROVISIONAL | No enum exists; privates scattered; inspector need unproven |
| PAC | Sequencing role, contract §13 | Real role, not module; authored packets = fabrication | Contract + leg pattern; ZERO authoring — fake packets deleted via convergence | PROVISIONAL | Ordering gap real; module need unproven |
| DeferUntil | One new verb, incl. pause-shift | Need confirmed; pause-shift REJECTED | Verb need LOCKED; shape PROVISIONAL; abandon-not-resume | LOCK need / PROV shape | 3 loops/2 shapes/zero shared verbs; terminal semantics forbid resume |
| Interrupt semantics | Pause-aware deadlines | Terminal; contradiction proven | Abandon + new request; delete all pause language | LOCK | `Interrupt`→`Interrupted` terminal; `InterruptActive` clears, shifts nothing |
| ReachSpec | Param object + suppression data | Confirm minimal; warn against FSM smuggling | Target + predicate + deadline only; suppression/retry/re-resolve remain adjacent reach metadata until duplication proves promotion | PROVISIONAL | Move legs + BotPath exist; predicates trace-gated |
| Travel FSM | NO | NO (deferral locked) | DEFER locked | LOCK deferral | ACore PREPARE dead; WORK→TRAVEL re-entry commented out |
| Blackboard | Constraints only | Constraints + fill-overload | DEFER locked; constraints LOCKED | LOCK deferral | Zero in-wake repeats; 1.2 s gates bound cost |
| Task chain | NOT YET + promotion rule | DEFER | DEFER; rule kept | LOCK deferral | One user; extraction needs two-family duplication |
| Failure enum | Extend to ~13 | Keep 7; concepts w/o expansion | Keep 7 values; routing concepts in §19 | DEFER extension | CooldownActive=not-ready; OutOfRange=prerequisite; TargetMoved=tracking |
| Harvest | Assumed ~shared | DIVERGED (proven) | NOT B2; converge via `Skill.Use` pre-Phase-1 | LOCK finding / PROV seam | Trace 4000 ms cast vs actor sync + authored packets |
| Plant | Shared seam | CONFIRMED shared; no Skill.Use either side | LOCKED shared; never force through Skill.Use | LOCK | `CSCreateDoodadPacket` sync both sides; skill fields inert |
| GCD | R8 do-not-fix | Gap proven at param level | No-bypass LOCKED; convergence shape PROVISIONAL | LOCK rule / PROV shape | Human sites `false` ×6; actor path `true`; engine owns gate |
| Synthetics | Zero production callers | Autonomous-zero holds; PlayCinema + bridge carved out | Narrowed claim + PlayCinema decision required | LOCK narrowed | `PlayCinema :1257–1260` synthesizes in-production; bridge default-off gated |
| B2 | 4 gates, redefined ladder | 4 gates, canonical letters | 4 gates inside canonical B; E excluded (§21) | PROVISIONAL thresholds | Trace supports shape; numbers need re-trace |
| U3 | Potato gate (presentation half) | Apparatus cannot answer | NOT a correctness gate; separate capture item | LOCK de-gating | Single-connection capture; zero observer packets exist |
| NPC Buy | 2nd family, convergence assumed | Best family BUT convergence first + atomicity gap | Gated 2nd family; precondition Buy/Sell/TalkTo convergence | PROVISIONAL | Packet atomic rollback vs actor grant-then-charge (`:2622–2625`, gap self-documented `:2608–2610`) |
| Scaling | Constraints, no cache/LOD | Confirmed; +regionless tail + Observe-3× notes | Constraints LOCKED; two profiling questions added | LOCK constraints | Rates are arithmetic; no fire proven |

## 8. Current AAEmu strengths preserved

KEEP AS-IS: real `Character` + `HeadlessSession` E1/E2 bridge; `ExecutionBoundary`; `ActorRequest` lifecycle + idempotency + audit;
`ActorEffectLedger`; scheduler wake-producers + game-thread drain + lease + backstop shape; `VehicleMovementModel` shared seam;
skill engine as unit range/cooldown owner; per-family actor gates where engine has no owner; `BotPath` math; arbiter + ranks;
`PopulationDirector` dormancy + observer-gated broadcast; bounded queues/ledgers; scorecard/ladder/ledger discipline.
KEEP + EXTEND: `ActorRequest` (ledger-gap fix only); ownerless-path gates (harvest wiring, loot discipline); movement seam (Orient
convergence, teleport convergence). Nothing graded consolidate-or-replace. Presence-only note: E1 leaf vs E2 roam vs E3 arbiter layering
stands; the smell is *inside* E2 (god-executor) — seams cut as part of slices, never a merge project.

## 9. Final architecture

Gameplay Loop / Goals (scenarios + phase enums today; DesiredAction verb+target+predicate) → existing Arbiter (single-active ranks) →
leg-local orchestration beside E2 (explicit phases, `LegPhase`) executing the PAC contract → reach / execute / defer / observe via
`IGameplayActor` primitives → ONE `ActorRequest` lifecycle → shared human-path engine calls (converged: plant `CreatePlayerDoodad`,
harvest `Skill.Use`, Buy handler sequence) → authoritative systems → world result. Side channels: trace/audit spine (WHAT+WHY +
defer-reason/phase/suppression/clock-fired); presentation = genuinely observer-only signals, tagged. Interrupt arrows point to
abandon + new request. Utility/GOAP/Travel-promotion/Blackboard/LOD appear only as dashed FUTURE boxes with triggers.

## 10. Layer contracts

| Layer | PURPOSE | INPUT → OUTPUT | OWNS | MUST NOT OWN | FAILURE BOUNDARY | CURRENT | MISSING |
|---|---|---|---|---|---|---|---|
| Goals / Loop | Activity order | goal → DesiredAction | phase order, prerequisites | movement/timing per step | PrerequisiteMissing → satisfy-or-abandon | scenarios + phase enums | chain (NOT YET) |
| Arbiter | Single-active selection | candidates → one goal | ranking data | execution | always yields (idle default) | ranks 50–100 | nothing |
| Leg + PAC role | Embodiment order + wait/retry state | DesiredAction → terminal request | validate→reach→stop→orient→ready→begin→defer→observe→typed-fail | skill/cooldown/quest/economy rules; movement integration; authoring; decisions | each failure to exactly one owner (§19) | verbs + gates + Tick polls | Orient primitive, DeferUntil, `LegPhase` |
| Knowledge | Policy over queries | — | §26 constraints | cached facts | stale reads impossible (no cache) | statics + gated scans | nothing, deliberately |
| Reach | Eligibility transport | ReachSpec → arrived / typed-fail | dest/unit + predicate + deadline + suppression consult | WORK, decisions, static tracking | NoPath→replan-once; NoProgress→suppress; TargetMoved→reacquire | Move legs + BotPath | ReachSpec shape (small) |
| Shared semantic | Human-path engine calls | semantic args → result | exact handler-equivalent sequence | bot timing/retries | MechanicRejected surfaced verbatim | VehicleMovementModel, Buy/Craft paths | Harvest + Buy/Sell/TalkTo convergence |
| Authoritative systems | Canonical rules + truth | calls → mutations/refusals | range (unit), cooldown/GCD, quests, nav, economy | anything bot | refusals are terminal data | Skill.Use, managers | nothing |
| Presentation | Observer-only signals | events → broadcasts | stop/posture/broadcast requests via shared seams | correctness (never alters outcomes) | presentation failure never fails action | BroadcastStop, throttled broadcast | authored-packet deletion, Orient rule from trace |
| Trace spine | WHAT + WHY | transitions → audit | audit, reason labels, clock-fired labels | second log system | missing context = actor bug | audit + state_changes | defer-reason + phase + suppression fields |

## 11. ActorRequest lifecycle

LOCK NOW. Requested → Accepted → Running → Completed / Rejected / Interrupted / TimedOut; terminals final, never re-run;
`Expire` idempotent; `Elapsed` accrues in Running only; single-writer busy-reject; `Finish` = ledger + audit + `_active` clear.
**Interrupt = abandon:** `InterruptActive` clears local watch-state and terminates; the engine effect (craft queue, cast damage, plot task)
is independent truth and may still land — the request never "resumes." Continuation is a NEW request (fresh TraceId, causal link to the
abandoned one). **Backstop parity (Phase-0 item):** queue-backstop expiry currently builds a local audit via `CaptureAudit` WITHOUT actor
`Finish` (`BotActionCommandQueue.cs:389–407,817–827`, VERIFIED) → no ledger outcome recorded. Resolve: record ledger on backstop expiry,
or prove starvation-expiry must stay ledger-silent. Until resolved, same-key retry after backstop-expiry is not dedupe-safe.

## 12. PAC sequencing contract

PROVISIONAL as reusable shape; LOCKED as review policy. PAC is a sequencing contract executed by leg-local code calling only
`IGameplayActor` primitives — not a class mandate, not a scheduler, not an engine.

| Step | Requirement | Owner today | Notes |
|---|---|---|---|
| Validate target resolves | always | leg | else TargetInvalid → reacquire path |
| Reach | delegated | Move legs vs ReachSpec predicate | never integrate movement; static-geometry tracking excluded |
| Stop | family-gated (farm: default ON per trace) | leg via BroadcastStop pattern | delete only with trace evidence it is unneeded |
| Orient | family-gated, tolerance TBD | leg (Plant/Harvest inline precedent) | never universal; U1 re-trace sets tolerance |
| Ready-check | observe-only | engine | skill known? reagents? labor? bench in range? never track cooldown copies |
| Begin | exactly one semantic call | shared-semantic seam | packet handler's engine sequence |
| Defer / Observe | DeferUntil within Deadline | leg | hold-Running or audit-only post-terminal per family |
| Typed-fail | exactly one owner (§19) | leg | audit + suppression where warranted |

MUST NOT OWN: skill/cooldown/GCD/quest/economy rules, movement integration, presentation authoring, planner decisions.
Movement owns integration/arrival-progress/stuck-detection. Engine owns range (unit), cooldown/GCD. Facing is action-specific.
Authored packets are fabrication: Harvest `SCSkillStarted/Fired/Ended` + manual labor are DELETED by convergence, not relocated.

## 13. Execution-phase ownership

LOCK NOW. `ActorRequest` owns lifecycle state. Leg-local execution context owns choreography phase (`LegPhase`, generalized `PendingLeg`).
Trace/audit may copy the phase value at transitions and terminal. Promote phase onto `ActorRequest` ONLY if live runtime inspection
provably needs it. Phase vocabulary is partially shared (approach/settle/begin/defer/observe) + family-specific (orient where required);
do NOT lock a universal enum — one user per exotic phase keeps it local. No transitions of its own, no terminals, cleared on terminal.

## 14. Reach / arrival model

LOCK NOW on principles; PROVISIONAL on per-family numbers. No Travel FSM: no PREPARE (dead), no WORK (execution's job; re-travel = new
request), no COOLDOWN state (suppression entry `{target, until, reason}` consulted pre-issue), no EXPIRED state (`TimedOut` result).
Minimal `ReachSpec`: target (objId | fixed point) + arrival predicate + deadline (2×ETA + moveWait seed). Retry budget, suppression,
and opt-in re-resolve remain **adjacent reach-layer metadata**, not fields of the minimal spec, until a second independent family proves
that promotion reduces real duplication. They remain data rather than states either way. Silent-vs-typed split: transient
deviation → silent replan; no-progress-twice → typed NoProgress + suppress + escalate; impossible → fail fast with reason.
Two-Potatoes is implementable without general Travel — single-leg reach + predicate + deadline + reacquire-once. Promotion trigger: second
independent family duplicates reach/retry/repath logic. MoveToUnit re-resolve: PROVISIONAL, default-off, cadence UNKNOWN (do NOT ship 1.2 s
without measurement); potatoes do not require it — keep out of the slice unless directly needed.

## 15. Async / DeferUntil model

Need LOCKED; signature PROVISIONAL. Minimum semantics: evaluated on the Tick/execution boundary; waits on read-only authoritative
engine/world truth; bounded by existing Deadline/Timeout; may hold Running (craft queue-active, PutDown slot, plant/harvest completion)
or enrich post-terminal audit-only without failing the action (cast effects); interruption terminates (abandon + new request — never
deadline-shift); which-clock-fired labeled (actor-elapsed vs queue wall-clock backstop). Reason taxonomy (vocabulary, not classes):
authoritative (cast/channel/cooldown) / world-condition (maturity/respawn) / response (service/dialog) / retry-cadence / completion-give-up
/ transition-settle / humanization-OFF. World-condition, response, and completion waits MAY share the low-level poll while carrying distinct
reason labels — one primitive, three vocabularies. Maturity-defer is UNPROVEN (no growth phases traced): earns its use only in
`two_potatoes_full_cycle`. No generic timeout framework. No suspend primitive, no timer objects.

## 16. Harvest semantic convergence

LOCK NOW on finding + invariant; PROVISIONAL on seam. FINDING: current Harvest is NOT B2 — human = real 4000 ms `Skill.Use` (Doodad,
`bypassGcd=false`) → fire → `doodad_use` → phase 4458 → labor −1 → ended (trace +60 ms overshoot); bot = synchronous `doodad.Use` +
authored Started (TlId 0) / Fired / manual labor / Ended, ~4 s timing delta, untouched GCD. INVARIANT: bot harvest must reuse the human
semantic engine path, never reproduce its packets. TARGET: `Skill.Use` (Doodad target, `bypassGcd=false`) with async completion through the
real CastTask; labor via pipeline (current manual post-mutation `ChangeLabor` has a sufficiency hole: mutation lands, refusal impossible).
RECORDED DIVERGENCES folded in: labor handling, GCD timestamps, 25 m live gate vs template 4 m (U1 number pending; template data is not
enforcement — engine exempts doodads). Exact refactor seam left to implementer (handler-share vs actor-core-share); B2 exit requires the
observer packets to be engine emissions, not authored shapes.

## 17. Plant semantic path

LOCK NOW. Plant does NOT use `Skill.Use` on the observed human path: `CSCreateDoodadPacket` (item-instance + labor-from-`UseSkillId` lookup
+ farm/house/permission gates) → `DoodadManager.CreatePlayerDoodad`, synchronous + persisted. Zero skill events in trace. Client-DB skill
fields (25536: cast 4000, range 2, Self) are inert lookups — never execution proof. **Do not force Plant through `Skill.Use` merely because
client data references a skill.** Plant and Harvest are DIFFERENT semantic families (placement/create vs skill-mediated doodad action)
despite gameplay adjacency — the family table (§26-adjacent §10/§26) records this split. Current actor Plant already mirrors the packet
order — keep the seam, prove with trace diff.

## 18. GCD/cooldown policy

LOCK NOW on rules; PROVISIONAL on seam. RULES: no second cooldown engine, ever; bots must not silently bypass human-path engine gates on
parity paths; GCD/cooldown logic is never duplicated — the bot converges onto the engine gate. FINDING: all six human `CSStartSkillPacket`
dispatch sites pass `bypassGcd=false` (`:136,169,187,194,200,208`); both `Unit.UseSkill` overloads pass `true` (`Unit.cs:1068–1092`);
`Character` has no override (grep-verified); actor Cast/CastAt ride the bypass. Human casts enforce SkillLastUsed/CheckInterval/GlobalCooldown
(`Skill.cs:124–159,643–650`); actor casts never set them. CONVERGENCE: human path (`false`) for actor casts, or an explicitly scoped,
tagged NPC-paced justification — shape PROVISIONAL pending refusal-trace/source work. TRACE REQUIREMENT: cooldown/GCD refusal events must
become observable (`RecordSkill` carries none; `SendSkillFailIfNeeded` untraced) — pacing parity is unmeasurable until then.

## 19. Failure/recovery ownership

LOCK NOW on routing; DEFER on enum expansion. Keep the existing 7 `ActorFailureReason` values. Routing concepts (no new enum):
REACH — NoPath → silent replan once → reacquire → typed fail; NoProgress → shrink/retry once → suppress (~12 s) → escalate to activity
replace; target gone → reacquire (nearest-rescan). EXECUTION — MechanicRejected → surface engine reason verbatim, no blind retry;
not-ready (cooldown) → back off to cadence, never fail; ResponseTimeout/TimedOut → abandon + record reason + which clock fired.
LOOP/PLANNER — PrerequisiteMissing → satisfy chain or abandon; PermissionDenied (no farm, no money) → abandon activity, never movement
retries. ARBITER — activity replace on exhaustion. Nothing reaches a planner except goal-level abandonment (no planner exists).
Promotion rule: a new enum value ships only if it changes routing or materially improves diagnostics — proven by the Buy family at earliest.

## 20. Synthetic evidence policy

LOCK NOW (narrowed claim). SCOPE: zero *autonomous-selection* callers for the synthetic quest-credit helpers (scheduler/executors/arbiter
never call event-fires — VERIFIED); runtime bridge/scenario/adapter access exists behind a default-off gate; actor Accept/TurnIn/Talk paths
are REAL gated paths (AddQuest/DoReportEvents/DoTalkMadeEvents + range gates) and must not be tainted by association. CARVE-OUT REQUIRED:
actor `PlayCinema` (`GameplayActor.cs:1247–1262`) fires `OnCinemaStarted/Ended` directly and Completes — an in-production synthesizer.
Decide in Phase 0: tag as presentation-fixture with explicit labels, or remove synthesis. Categories: GAME-STATE-EVIDENCE (quest engine
handling, transitions, fixtures — supports P at most, never B) vs PERFORMED-ACTION-EVIDENCE (embodied engine path). Preserve all fixtures;
label every synthetic-citing scorecard entry; grep-gate allowed callers (runner FireEvent, bridge ops, adapter forwarding, rigs/manifests);
`StockInventory` → rig-only (all 15 call sites are seeding). No retroactive invalidation of P-level evidence.

## 21. B2 definition

PROVISIONAL thresholds inside LOCKED shape. **B2 = canonical-B completion for one action family.** Requires all four, only these four:
(1) Mechanic-result parity — same valid game result. (2) Human-path engine-call equivalence — the engine calls the human packet handler
executes for the same action (packet-`Read`-is-the-handler shape may require refactoring handlers to share the core, not merely calling
"the same function"). (3) Mechanic-required + empirically established preconditions — canonical gates plus trace-observed stop/range/facing
(U1 numbers pending; facing required only where trace or engine gates it). (4) Observed async completion/failure — begin returns promptly;
completion/failure via DeferUntil within Deadline. EXCLUDED: animation smoothness, jitter, observer polish — E-axis work. NUANCE (locked):
if a "presentation" event is required by the semantic pipeline (e.g. fire-before-phase), it is correctness, not E — the test is whether the
pipeline needs it, not which client renders it. B2 ≠ E2.

## 22. Correctness / semantic execution / presentation / humanization

LOCK NOW. CORRECTNESS: target validity, canonical rules, inventory, labor, skill eligibility, cooldown/GCD, authoritative mutation — from the
engine, never randomized; may change outcomes (it IS the outcome); required for all parity; PRs cite the mirrored gate. SEMANTIC EXECUTION:
human-path engine calls, correct sequencing, async lifecycle, required reach/stop/facing — required for B; reviewed against trace + handler.
PRESENTATION/E: observer animation, posture, cast bars, build preview, smoothness — requested via shared seams, never authored; authored fake
skill packets are fabrication, not presentation; presentation failure never fails the action. HUMANIZATION: hesitation, idle variation,
imperfect choice, look-around — one duration-scale jitter primitive at most, default OFF, ledger-tagged, per-activity opt-in; any jitter on a
canonical gate is auto-reject. Facing/stop required for B2 only where trace shows humans observably do it or the engine gates it.

## 23. Trace corpus + tooling

PROVISIONAL (LOW cost, HIGH value). Action-oriented corpus; level is metadata. Per scenario: `meta.json` (scenario, actor, client version,
server SHA, zone, level, target type, expected action, IDs) + `trace.jsonl` + `annotations.jsonl` (intended-action, settle-point, begin,
expected-complete) + `diff-report.md` (bot-vs-human WHAT+WHY). Segmentation: PLANT_SINGLE → PLANT_PAIR_SPACED → GROWTH_TO_MATURITY →
HARVEST_SINGLE → NPC_APPROACH_TALK → NPC_BUY_SINGLE_LINE → NPC_SELL_SINGLE → MOUNT_ITEMCAST; quest/combat/loot/trade/craft/repair only
after farm+buy B2-clean. TOOL UPGRADES (research tooling, Phase 0): pos+yaw+range at skill/doodad hooks; inbound details for
CreateDoodad/StartInteraction/Buy/Sell; GCD/refusal events; operator-chat filter; two-connection observer stream (U3 item, non-blocking).
KNOWN LIMITS preserved: 250 ms movement throttle + stale stop velocity (`PlayerTraceService.cs:283–328`); no yaw-at-action; inbound/outbound
detail null-lists (`:388–429`); `packet_in` post-effect (`GameProtocolHandler.cs:205–209`); single session/actor/zone-corner; zero failure
sequences captured. Observer/U3 is NOT a correctness blocker.

## 24. Two-Potatoes final scope

REQUIRED: legitimate prerequisites (soil, seed×2, labor) → approach → Stop/settle as trace-established → plant #1 via
`CreatePlayerDoodad` path → `DeferUntil` phase-advance observe → reposition (U1 spacing behavior) → plant #2 + observe →
mature-crop reach → harvest via REAL `Skill.Use` path (§16) → fire+phase+ended + labor-delta observe → per-step WHAT+WHY trace.
The **2 s stationary hold belongs to the human validation protocol for clean measurement, not the architecture contract**; the bot implementation must use the actual stop/settle requirement established by trace and engine behavior, not an arbitrary fixed delay.
Maturity wait: include ONLY if `two_potatoes_full_cycle` establishes growth-phase behavior; else ship plant-pair +
separately-prepared mature-harvest probe. PRE-WORK (Phase 0): ledger/backstop decision; Harvest + GCD convergence designs;
PlayCinema decision; hook annotations (pos/yaw/range, inbound details, refusal events, chat filter); U1 range/facing numbers.
OPTIONAL: dormant MoveToUnit re-resolve branch (default-off); fill-overload call-site switch. OUT-OF-SCOPE: Travel FSM,
Blackboard, Task chain, Utility/GOAP, LOD, executor refactor, quest cleanup beyond labeling, MoveToUnit tracking default-on,
second family work, observer-fidelity claims, jitter. EXIT GATE: B2 state+path match + trace diff + no extra primitives +
no authored skill packets on harvest. Kill criterion: anything else introduced is removed.

## 25. NPC Buy second-family plan

PROVISIONAL, gated. Buy complements farming (service-dialog sequencing + money/stock/vendor prerequisites + buyback semantics) and
shares reach/defer/deadline/typed-fail while adding exactly one candidate (response window). PRECONDITIONS (not assumptions):
converge `GameplayActor.Buy/Sell/TalkTo` onto packet-handler engine sequences (parallel implementations today — same atoms
NpcManager/Inventory/ChangeMoney, different composition, different failure vocabulary, bot-only `npc_buy` events); add atomic
grant-rollback to actor Buy (packet `:160–210` rolls back; actor `:2622–2625` does not — failure mode: partial grant + full charge);
re-trace `npc_buy_single` with position discipline. InteractionSession capped at presentation-only (dialog open/interact/end telemetry):
server purchase is stateless sync (no dialog check, no session token), so a server-visible session promotes ONLY on trace evidence of
dialog-gated server state (none observed). FALLBACK if convergence proves too invasive: non-crop doodad interaction — explicitly the
weaker cross-check (overlaps harvest shape). Gate: starts only after Two-Potatoes exit gate met and reviewed. Repair/Talk-alone correctly
rejected (thin / unfalsifiable).

## 26. Knowledge/scaling constraints

LOCK NOW (constraints); DEFER (machinery). No Blackboard: no measured bottleneck, no repeated in-wake query, stale-risk > gain.
Constraints (review-enforceable): no unbounded scan per wake (region-bounded `GetAround`, 64 m regions); production radii ≤45 m schedule-scale
with gate intervals; keep 1.2 s gates + one-activity-per-wake + scan/busy exclusion; bounded game-loop work per bot per wake (one in-flight
step, one Running request, drain caps; new per-wake work needs a budget note); think cadence independent of 15 Hz execution with stagger
(GUID-hash precedent — no thundering wake); observer-gated broadcasts + standstill on stop; dormancy drop/re-discover contract on new per-bot
state (DB-row liveness UNKNOWN until verified); scale by thinking less often, never by acting more robotically. Scaling classification: NO
proven fire (rates are arithmetic, not measurements). Latent: regionless fallbacks (harden comment-premise with assert/log-gate), `Observe` 3×
on API calls, E2 inline growth, pressure-probe wiring unproven, observer fan-out. Profiling questions before machinery: per-query region cost
+ break-N via scheduler metrics; probe→cadence feedback check. DEFER: cache (trigger: scan-dominated tick), LOD/population model (trigger:
soak + proximity data), ACore population port (never without measurements). Fill-overload switch is allocation cleanup, NOT architecture.

## 27. Architectural invariants

LOCK NOW (review-usable; a violating PR amends the invariant first, through a proposal pass):

1. Bots and humans differ in intent source, not human-path engine semantics.
2. `ActorRequest` is the one action lifecycle; terminals are final; interruption abandons (continuation = new request).
3. Long actions yield (`Running` + predicate) and observe authoritative truth within a Deadline.
4. No fake skill packet sequence substitutes for the real semantic path.
5. Discovery range is not action eligibility; every gate from engine/handler/trace/explicit policy.
6. One authoritative movement seam; every reach names its arrival condition; travel ends on arrival.
7. Engine-owned rules are never duplicated; engine-owned human gates are never silently bypassed.
8. No shared abstraction promotes from one user.
9. Synthetic evidence never proves performed-action parity (P at most).
10. Presentation cannot alter correctness; humanization never modifies mechanics and defaults OFF.
11. No second scheduler, movement engine, cooldown engine, or game-rule engine.
12. No global scan per wake; no cache without invalidation + provenance.
13. Every retry/failure has one owner and bounded behavior; only goal-abandonment escalates.
14. This architecture is additive: it governs PlayerBot parity/execution seams and does not supersede unrelated AAEmu completion work or previously established project decisions unless explicitly stated.

## 28. Concept matrix

| Concept | Final Status | Needed Now? | Exact Scope | Must Not Become | Promotion/Revisit Trigger |
|---|---|---|---|---|---|
| ActorRequest lifecycle | LOCK | yes | keep + ledger-gap fix | second machine | — |
| Leg-local phase | PROVISIONAL | minimal | `LegPhase` + audit copy | request field / transitions | live-inspector need |
| PAC | PROVISIONAL | role only | contract + leg pattern, zero authoring | class / scheduler / god-object | 3rd-family cross-leg need |
| DeferUntil | LOCK need / PROV shape | yes | Tick predicate + Deadline; abandon-not-resume | pause-shift / timeout framework | maturity trace earns 3rd use |
| ReachSpec | PROVISIONAL | minimal | target + predicate + deadline; suppression/retry/re-resolve adjacent for now | TravelTarget in disguise | 2nd-family reach duplication |
| MoveToUnit re-resolve | PROVISIONAL | dormant | default-off branch; cadence TBD | default-on tracking | measured cadence lands |
| Travel FSM | DEFER (locked) | no | — | states via ReachSpec creep | reach duplication |
| Blackboard | DEFER (locked) | no | constraints only | TTL store | scan-dominated tick |
| Task chain | DEFER (locked) | no | local phases + rule | framework, one user | two-family duplication |
| InteractionSession | DEFER | no | presentation-only at most | server session | dialog-gated state observed |
| Failure enum expansion | DEFER | no | 7 values + routing concepts | 13-value enum | Buy proves routing need |
| GOAP Planner | ACTIVE (Phase 1) | yes | `BotWorldState` bitmask + `GoapPlanner` A* search emitting DesiredActions only (see `Docs/wiki/PlayerBot-GOAP-Planning-Architecture.md`) | state-mutation bypass / parallel engine | Revisit triggered 2026-09-15: L1 clean (>800km soak) + multi-activity contention |
| Utility AI / LOD | DEFER (locked) | no | data-ranks / constraints | selector engine / fake LOD | soak numbers + human-proximity data |

## 29. Migration roadmap

**This roadmap is a PlayerBot/parity workstream layered on top of ongoing AAEmu completion, not the master AAEmu roadmap.** Unrelated emulator development continues in parallel. The phases below constrain only PlayerBot behavior, shared semantic seams being migrated for parity, and evidence claims that depend on those seams. If ongoing AAEmu work changes an authoritative mechanic or handler, re-ground the affected PlayerBot family before continuing; do not halt unrelated AAEmu completion work.

Phase 0 — Evidence/integrity (no PlayerBot behavior changes required by this architecture): `two_potatoes_full_cycle` + `npc_buy_single` traces; hook upgrades (annotation,
inbound details, refusal events, chat filter); ledger/backstop + Harvest-seam + GCD-shape + PlayCinema decisions; wedge-rate + loop-tax
baselines; freeze new `PlayerBotController` verbs (adapter rule); adopt GAME-STATE labeling. Phase 1 — Two-Potatoes (§24) to exit gate,
then STOP. Phase 2 — Extract ONLY duplicated reuse (kill criterion: one user stays inline). Phase 3 — NPC Buy validation (§25) after
convergence preconditions. Phase 4 — Parity migration: synthetic paths → performed loops, shortcuts converged, authored packets deleted,
ownerless gates wired, teleports → MoveTo. Phase 5 — Autonomy over proven primitives (needs-farm pilot). Phase 6 — Population on soak
evidence. Ordering dependency: measure → prove → extract → migrate → autonomy → population.

## 30. Remaining open questions

Carry-forward only (blocker level / owner / evidence / blocks): Q-ledger — Phase-0 blocker / queue / ledger-write-or-justification /
Phase 1 auth. Q-harvest-seam — Phase-0 blocker / actor / convergence shape / Phase 1 auth. Q-gcd-shape — Phase-0 blocker for B2-cast claims /
actor+trace / refusal-trace + seam / Phase 1. Q-cinema — Phase-0 decision / actor / tag-or-remove / invariant 3. Q-range-number — blocks
ReachSpec values / trace hooks + full-cycle re-trace / Phase 1. Q-maturity — blocks world-condition defer / full-cycle trace / Phase 1
(alternative: plant-pair + mature probe). Q-buy-convergence — blocks Phase 3 / actor+handlers / converged shape + re-trace / second family.
Q-observer — non-blocking work item / trace service + harness / two-connection capture / E claims only. Q-scale — profiling, non-gating /
scheduler metrics + soak / Phase 6. ANSWERED (not carried): Expire idempotence (proven), plant path (shared), stop-before-farm-act
(observed), unit-range ownership (engine), ladder letters (canonical), pause-resume (rejected), task/blackboard/travel need (deferred).

## 31. Exact next authorization

**NEXT AUTHORIZATION: Complete Phase-0 evidence/integrity decisions required to freeze the Two-Potatoes contract. No PlayerBot behavior
implementation under this architecture is authorized by this draft; unrelated AAEmu completion work remains authorized by its existing plans and gates.** Concretely: run `two_potatoes_full_cycle` (+ `npc_buy_single`) with upgraded hooks;
decide Q-ledger, Q-harvest-seam, Q-gcd-shape, Q-cinema; record U1 numbers and maturity behavior. The Two-Potatoes implementation slice
(§24) may be requested ONLY after those land and the exit gate is reviewable. Anything beyond Phase 0 requires a new authorization.

## Appendix H — hostile final review record

- *Preserved disproved Muse assumptions?* No — Harvest-shared, R8, pause-shift, ladder rewrite, zero-callers-broad all corrected with
file:line (§7 rows). *Over-corrected on one trace?* Guarded: n=1 range stays PROVISIONAL number; engine ownership overrules trace silence
(talk gate, doodad exemption, GCD params); maturity-defer gated on capture, not assumed.
- *Travel FSM in disguise?* No — ReachSpec capped at target+predicate+deadline; suppression/retry/re-resolve remain adjacent reach metadata; states (WORK/COOLDOWN/EXPIRED) explicitly
rejected with reasons; promotion trigger requires observed duplication.
- *ReachSpec too large?* Trimmed: the shared spec is only target+predicate+deadline. Retry/suppression/re-resolve remain local reach metadata until cross-family duplication proves promotion.
- *PAC a class?* No — contract + leg pattern; module promotion gated on third-family need.
- *ActorRequest contaminated?* No — phase demoted to leg-local; request untouched except ledger-gap fix (same lifecycle, no new state).
- *Authored packets as presentation?* No — classified fabrication, deleted via convergence.
- *Duplicated Skill validation?* No new checks; Harvest fix removes authorship for engine path; talk-gate invention forbidden.
- *Silent GCD bypass?* No — no-bypass locked; shape provisional with refusal-trace requirement.
- *Redefined P/B/L/A/N/G/T?* No — canonical letters preserved; B2 inside B; E orthogonal.
- *U3 blocking correctness?* No — de-gated; separate capture item.
- *Failure enum expanded?* No — 7 values kept; routing without expansion.
- *Two-Potatoes absorbing cleanup?* No — REQUIRED/PRE-WORK/OPTIONAL/OUT-OF-SCOPE table enforced (§24).
- *Scaling machinery?* No — constraints + two profiling questions; triggers own all machinery.
- Net simplification vs sources: one verb-need, one role, one parameter object, zero machines, zero stores — smaller than either parent
document's maximal reading, with every cut carrying a trigger so nothing is lost.
