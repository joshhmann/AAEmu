# Brain G3 funnel — 2 live runs, warm q0 lane (2026-09-19, branch develop)

READ-ONLY analysis + lane-read verification. Nothing modified, nothing built.
Lane: adopted only (`E2E_ROOT=/root/aaemu-e2e-q0`, ports 2237/2239/2250/2260/2234/2280/3311,
`COMPOSE_PROJECT_NAME=q0pilot`); never restarted/rebuilt. 2 G3 runs (the allowed max);
reports archived to `/tmp/g3-run1-report.json`, `/tmp/g3-run2-report.json` BEFORE overwrite.
Lane-log read attempted: no coverage — `/root/aaemu-e2e-q0/logs/game.log` is stale
(00:11–00:13, zero quest-leg lines); no runtime `*.log` newer than 09:00. Nothing below
is log-joined; everything is bridge-observe payload + archived report JSON.
Provenance both runs: src rev `6c3efe25ecb68998544eb3f75699bc1674c7eef1`,
runtime sqlite md5 `78b3bdbf038db3b927056106efdf91af` (report `provenance`).
Game-side bootstrap flag: ON both runs (`gameSide=1 [game-proc-environ pid=522433]`).

Context (not re-derived): `AAEmu.IntegrationTests/E2e/G3AutonomousQuestAcceptTests.cs`
(fixed harness: per-bot wake proof, game-side flag, derived verdicts),
`g3-diagnosis-arbitration.md` (funnel F0–F10), `g3-diagnosis-repro.md`
(HARNESS-UNKNOWN leakage), `prereq-changes.md` (DecisionCycleId, arbiter stamping).
No new instrumentation was added; DecisionCycleId/arbiter-stamp/observe payloads are
consumed as-is. Consequence, stated up front: several funnel transitions below are
INFERRED from endpoint payloads (counts + predicates), not joined by cycle id —
a wake that dispatches nothing mints no `decision_cycle_id` row to join on, and the
arbiter stamp lands on audit rows only when a request exists.

## 1. Run 1 — 09:45:32Z, charId=10 — FAIL_PERCEPTION (test verdict FAIL-BEHAVIOR/DISCOVERY)

Full funnel (predicate per transition, measured values from `/tmp/g3-run1-report.json`):

| # | Transition | Predicate (code) | Measured |
|---|---|---|---|
| 0 | wake proven? (per-bot) | `questLegActive` true OR `questTravelReason` non-empty OR quest-action rows > baseline OR own-actor trace growth (`G3AutonomousQuestAcceptTests.cs:347-361`) | PROVEN in 0.2s via travel reason (not via leg flag) |
| 1 | modules evaluated | arbiter probes all modules, first-Allow-wins (`BotGoalArbiter.cs:157-218`); per-module WhyNot stays in transition log (no record column, prereq Part 2) | counts unobserved; winner INFERRED `quest.*` (see #2) |
| 2 | winner → behavior | leg gate: `ActiveActivityProvider` starts with `"quest."` + actor idle (`BotRoamStepExecutor.cs:662-667`) | HELD — `StepQuestLeg` ran (proven: `ArmQuestTravel` wrote a reason, `:1206`; it writes on every path, `:1228,1232-1234`) |
| 3 | scenario | `QuestDecisionScenario.Run` on executor live actor, default band [1,9], cap 3 (`BotRoamStepExecutor.cs:1189-1195`; `QuestDecisionScenario.cs:52-59`) | RAN (only caller under this gate) |
| 4 | world candidates | `nearbyNpcs(character, 25f)` → `discoverTargets`, first-3-take (`QuestDecisionScenario.cs:121-133`) | **0 — FIRST ZERO.** Proof: `questActionCount` stayed at baseline 0, so `DiscoverQuests` was never invoked (any Reject/Complete emits a row via `Finish`, `GameplayActor.cs:4653-4685`; actor idle by gating rules out the silent `TryBegin`-busy drop at `:1196-1197`). `discoverTargets` EMPTY at `QuestDecisionScenario.cs:137` |
| 5 | quest candidates | per-target `DiscoverQuests` → offerings (`GameplayActor.cs:1189-1243`) | 0 (never swept) |
| 6 | giver candidates | objId resolution NPC-first-then-doodad (`GameplayActor.cs:1199-1205`) | 0 (never swept; giver 2425 objId 44311 resolved world-wide only — existence, not 25 m perceivability) |
| 7 | eligible offerings | band [1,9] + not-already-active (`QuestDecisionScenario.cs:147-150`) | 0 |
| 8 | legal proposals | selector legality-before-preference (`BotDecisionProposal.cs:252-281`) | 0 → `Fail("DECIDE", WrongDecision, "no legal quest proposal")` (`QuestDecisionScenario.cs:161-165`); observe `lastAction/lastResult` empty |
| 9 | selected / dispatched / terminal | stage (`QuestDecisionScenario.cs:168-174`) → `BotDecisionCycle.Execute` → dispatch (`:314-329`) | none; travel fallback armed instead: `"walking to quest target at (15562,15354) from (15578,15382)"`, 32.2 m flat |

Classification: **FAIL_PERCEPTION**. Chain engaged through arbitration (bootstrap ON,
`quest.*` held, leg ran) and died at the sweep: zero NPCs within the 25 m region scan,
so zero offers downstream. Earliest failing layer; no compensation (the 32.2 m travel
target is the no-legal-work fallback `ArmQuestTravel`, `BotRoamStepExecutor.cs:1218-1241`,
reading spawner templates — a different identity universe from objId discovery — not a
contradiction). F1 sub-cause (beyond-25 m spawn offset vs Region-null window vs
not-yet-spawned) remains UNKNOWN from trace alone — the known gap (§6.1 of the
arbitration doc); sweep I/O is still unrecorded.

Two measurement notes (do not change the verdict):
- Server-side "from" (15578,15382) sits 32.2 m from the travel target — beyond the 25 m
  discovery horizon — consistent with an empty sweep for that spawner's NPCs. Whether the
  target is 2425's spawner is UNKNOWN (spawner data unverified).
- The bridge `charPos` surface read (14536.6,11117.1,110.8, byte-identical both runs and
  in the repro doc) is ~4.4 km from the travel-reason "from" surface (15578,15382).
  Both are stable, so the offset is systematic, not noise: one of the two surfaces is
  stale-or-local, contradicting repro-doc §1's claim that both are live
  `Transform.World.Position`. The sweep uses the server-side position, so the funnel
  verdict stands — but the fixture's "staged ≤25 m" precondition, asserted via charPos,
  is UNVERIFIED and numerically inconsistent with the server-side "from".

## 2. Run 2 — 09:46:24Z, charId=11 — UNKNOWN (test verdict HARNESS-UNKNOWN)

| # | Transition | Measured |
|---|---|---|
| 0 | wake proven? (per-bot, same 4-signal predicate, 30 s budget) | UNPROVEN: `questLegActive=False`, reason empty, `questActionCount=0=baseline`, no own-actor trace growth. Global `stepped=False` too this window (contrast-only) |
| 1–9 | everything downstream | unobserved — leg never provably ran for OUR bot |

Classification: **UNKNOWN**. Stop at the earliest layer (wake); do not compensate by
reading the global flag (it too was false, and it is unattributable by construction).
Production-quiescence (scheduler never stepped charId=11: cadence/dormancy/lease —
scheduler internals outside audited files) vs harness-blindness (30 s budget, poll-only
observe) are indistinguishable from this trace. Fresh charId (11 vs 10) rules out
same-bot cross-talk, but cleanup was UNAVAILABLE both runs (bridge `deactivate` refused
for networked chars — `InvalidOperationException`; TCP disposed, manager/arbiter/executor
registry entries left in place), so the stale-Active-bot pool the repro doc implicates
grew by two more entries. No production verdict is drawn from this run.

## 3. BEHAVIOR RUNTIME proposal (sketch only — derived from AAEmu semantics)

Reuse, never duplicate: `BotObservedContext.Capture` perceive entry
(`BotDecisionProposal.cs:99-102`); `BotDecisionProposal` incl. `hardPreconditions`,
`MaxPersonalityWeight`, `MaxTimeout` (`BotDecisionProposal.cs:147-150`);
`BotDecisionSelector.Select` + `MaxCandidates=64` (`BotDecisionProposal.cs:231-238`);
`BotDecisionCycle.Execute` (`BotDecisionProposal.cs:307-312`); actor terminal semantics
(`TryBegin` busy-drop `GameplayActor.cs:4597-4600`, `Finish`→audit rows `:4653-4685`,
idempotency keys); `DecisionCycleId` join key + `SetPendingDecision`/`SetPendingCycleId`
staging (`QuestDecisionScenario.cs:168-174`); quest observe payloads
(`BotQuestLoopObservation.cs:64`; bridge `BotDriveBridge.cs:4450-4484`).

Minimal interface (one wake → at most one dispatch; invoked synchronously from the
existing leg slot, same as `StepQuestLeg` today — no second scheduler):

- Input: the LIVE `IGameplayActor` (never a second instance — arbiter-executor precedent,
  `BotGoalArbiterStepExecutor.cs:61-62`), ONE already-captured `BotObservedContext`
  (perceive once per wake and pass down; the scenario captures 3×/wake today,
  `QuestDecisionScenario.cs:100,159,185`), the wake `CycleId` string, policy/options.
- Steps: build proposals (preference + already-observable identity preconditions ONLY —
  `quest-active`/`quest-not-active` on the passed context) → `BotDecisionSelector.Select`
  → stage (`SetPendingDecision` + `SetPendingCycleId`) → dispatch through EXISTING actor
  verbs only → terminal-check (`Request.IsTerminal`) → observe-active (recapture context,
  before/after `ActiveQuestIds` diff, cf. `:101,185-186`).
- Hard bans: no quest/range legality duplication inside the behavior (range, `IsDiscoverable`
  `GameplayActor.cs:1398-1416`, and liveness stay engine gates at dispatch via
  `TryBegin`/`Reject`); no parallel actor; no HTTP internally (bridge stays outside the
  game assembly seam); no second scheduler; no new audit schema (DecisionCycleId +
  existing record fields only).

## 4. FIRST EXTRACTION recommendation: `AcquireQuestBehavior`

Preserve current in-range gate semantics exactly — no navigation-to-giver expansion:
out-of-range stays a dispatch `Reject` (`GameplayActor.cs:1211-1213`), and the travel
fallback stays where it is.

MOVE into the behavior (find-offering → resolve-giver → accept → observe-active):
- find-offering: sweep loop + first-3-take (`QuestDecisionScenario.cs:121-133`) and the
  discover-first/offerings collection with band + already-active filter (`:135-157`).
- resolve-giver: NOT a new lookup — the offering already carries giver identity
  (`offering.AcceptorType/AcceptorId` consumed at dispatch, `:320-321`); "resolve" means
  threading `(offering, targetObjId)` payload through, nothing more.
- accept: `AcceptProposal` constructor verbatim (`:292-312`: goal `quest.accept`,
  priority `AcceptPriority + (BandMax − level)`, tiebreak, `quest-not-active`
  precondition) + the `AcceptQuest` dispatch arm (`:320-321`) + `quest:{actor}:{cycle}:accept:{id}`
  idempotency keys (`:301`).
- observe-active: before/after `ActiveQuestIds` snapshot + diff (`:100-101,185-186`) as the
  landing proof.

KEEP outside (caller/scenario shell + engine):
- Leg gating (`BotRoamStepExecutor.cs:662-668`: no-party/no-PvP/`quest.*`/actor-idle) and
  `CycleId` minting (`:1194`); `ArmQuestTravel` + `ResolveQuestTravelTarget` (`:1218-1241,1249-…`)
  stay in the executor — movement decision, not acquisition.
- `AdvanceProposal`/`TurnInProposals` + their dispatch arms (`:206-290,318-319,322-327`):
  a second behavior later, not this one. `Fail()` constructor (`:331-349`), band/cap
  defaults (`QuestOptions`, `:44-59`).
- All engine gates: `IsDiscoverable` (`GameplayActor.cs:1398-1416`), range re-check
  (`:1211-1213`), `TryBegin`/`Finish` (`:4597-4600,4653-4685`).

## 5. Seam verdict: AMBIGUOUS — documented, not manufactured

- OBVIOUS at the leg boundary: gate → run → landed-else-travel (`BotRoamStepExecutor.cs:1184-1209`)
  is a clean cut; the whole quest leg extracts as one behavior with zero caller change.
- AMBIGUOUS inside the scenario, which is what an accept-ONLY extraction must cut through:
  today accept proposals COMPETE with advance/turn-in in a single `Select` call over one
  proposal list (`QuestDecisionScenario.cs:104-160`). Splitting accept out forces a choice
  the code does not make for us: (a) each behavior selects internally and the executor
  takes first-landed-wins in priority order (changes today's cross-type priority contest —
  accept priority 10+ vs advance 20 vs turn-in 30 — into an ordering the executor invents);
  or (b) behaviors return proposal SETS and one shared select/dispatch shell remains
  (smaller extraction — proposal builders are already separate methods — but the "behavior"
  then owns neither selection nor dispatch). (a) risks behavior change through reordering;
  (b) risks a husk abstraction that owns only construction. Recommend deciding (a)-vs-(b)
  with the behavior owner before cutting; the leg-boundary seam (§4 KEEP/MOVE) is safe
  to implement under either.
