# QuestBehavior Extraction — Change Log (Phase 1, behavior half, 2026-09-19, branch develop)

Scope: behavior-runtime + QuestBehavior extraction ONLY. Diagnostics half
(ActorRequest/audit/queue additive changes, position truth, G3 runs) is owned
by a sibling session — untouched here. Scheduler untouched, no per-bot
threads, GOAP untouched, no second actor/scheduler/perception, engine legality
stays in GameplayActor.

## Ground truth

- Branch `develop`, HEAD `6c3efe25e` at start; working-tree diffs on the owned
  files were the documented prereq-changes Parts 2–4 baseline (arbiter stamp,
  SetPendingCycleId, 3× PreemptCurrent), not a sibling collision — confirmed
  with the diagnostics session (read-only at check time, zero edits).

## Runtime shape (new: BotBehaviorRuntime.cs)

Minimal owner for behavior lifecycle, quest slice only — one wake runs at most
one synchronous tick against the bot's EXISTING live actor (never a second
instance); execution stays GameplayActor → engine; cadence stays
executor-owned (NextWake is a recorded hint, never consumed for scheduling).

- Identity: CurrentBehavior = "quest" (const; only hosted behavior),
  CurrentActivity (caller-gated, e.g. quest.progress), BehaviorInstanceId
  (per-wake CycleId — the wake→terminal join key), CurrentPhase (quest-leg
  while running, then landed action name / no-legal-work / fail stage).
- Pointers/evidence: CurrentActorRequest, LastResult (full QuestRunResult),
  StartedAt / LastProgressAt (landed only) / LastFailure, NextWake.
- Lifecycle: NotStarted / Running / Waiting / Completed / Blocked / Failed /
  Cancelled. Mapping: landed Completed request → Completed; DECIDE-stage
  no-legal-proposal → Blocked (NOT an error — LastFailure stays null, travel
  fallback may arm); EXECUTE non-terminal / RUN exception → Failed
  (LastFailure = detail); actor-busy at entry → Waiting (defense-in-depth —
  the caller gates on idle); Cancel(reason) moves Running/Waiting →
  Cancelled, observation-only (preemption stays with the owning leg via
  PreemptCurrent; the method never touches the actor).
- NextWake is always null: the quest leg is purely reactive (runs when the
  arbiter elects quest.*), so it never self-schedules. Recorded for the
  future navigation/behavior host + telemetry joins.

## Extraction cut (MOVE verbatim, zero semantic drift)

New QuestBehavior.cs owns the WHOLE quest leg through the ONE shared
BotDecisionSelector: per-wake perceive (BotObservedContext.Capture +
ActiveQuestIds snapshot) → advance proposals (Ready/Completed excluded) +
turn-in proposals (NPC/doodad/auto reporter resolution) + discover sweep
(first-3-take at MaxQuestDiscoverRange) + band/already-active filter +
accept proposals → single Select → SetPendingDecision + SetPendingCycleId
staging → BotDecisionCycle.Execute dispatch (existing actor verbs only,
idempotency keys unchanged) → terminal check → before/after ActiveQuestIds
diff → QuestRunResult (incl. Fail DECIDE/EXECUTE/RUN paths).

KEEP outside (unchanged): leg gating (quest.* + actor-idle), CycleId minting
(quest-{id}-{ticks}), QuestOptions band/cap/priority defaults (constructed by
the caller), ArmQuestTravel + ResolveQuestTravelTarget + OffersWalkableQuest +
TrySpawnerPosition (navigation side), all engine gates (range, IsDiscoverable,
liveness via TryBegin/Reject), no Character.Quests mutation, no HTTP.

Recomposed QuestDecisionScenario.cs keeps ScenarioName / QuestOptions /
QuestRunResult / Run signature byte-identical (existing callers — roam leg,
QuestBootstrapModuleTests — compile and behave unchanged); Run delegates to
QuestBehavior. Contract types stay nested in the scenario to avoid a second
copy; logic ownership moved.

Executor (quest-leg regions only): per-bot _questRuntimes registry (homestead
cache pattern); StepQuestLeg(bot, actor, state, questActivity) ticks the
runtime and keeps the landed contract + landed-log + else-ArmQuestTravel tail
unchanged; gate else-branch marks a pending tick Cancelled when the activity
moves away from quest.* (observation only).

## Seam decision: (b) shared shell — why

Finding D controls: today accept proposals COMPETE with advance/turn-in in a
single Select over one proposal list with deterministic cross-type priorities
(turn-in 30 > advance 20 > accept 10 + band bonus). Option (a) (per-behavior
select + executor first-landed-wins) would replace that contest with an
executor-invented ordering → behavior change. Option (b) keeps the one shared
BotDecisionSelector + BotDecisionCycle shell (no copy, no fork) with
QuestBehavior owning proposal BUILDING + the full leg run + lifecycle under
the runtime. The QuestDecisionScenario shell remains as the stable caller
contract.

## Build / test evidence

- `dotnet build AAEmu.Game` Release → 0 errors.
- `dotnet build AAEmu.IntegrationTests` Release → 0 errors.
- `dotnet build AAEmu.UnitTests` Release → 0 errors.
- QuestBootstrapModuleTests (17 tests): 14 pass / 3 fail — IDENTICAL at clean HEAD (stash-baseline, same 3 names), so all 3 are pre-existing and unrelated (one NRE in untouched ArmQuestTravel→TrySpawnerPosition travel code; two advance-landing rig issues). Parity: my tree matches HEAD 14/3 → 14/3, zero drift.
- Addendum (diagnostics request, no semantic change): DECIDE fail detail now appends funnel tallies `[swept targets offerings inBand legal firstZero]` (counts only, feeds bridge lastDetail; stage + message prefix unchanged). No QuestRunResult schema change. Verified on green tree: Game + UnitTests 0 errors, QuestBootstrapModuleTests parity 14/3 (same 3 pre-existing names).
- Lane regression (warm q0 lane, adopted only, full port env, current binary incl. extraction + tally + wakeSeq): G1 ExplicitMove_Plus4X_ConvergesViaActor PASS (5.3s), G2 ExplicitTalk_TalkObjectiveNpc_CreditsViaActor PASS (3.5s).
- Q0 NOT re-run by design: the test calls EnsureUp + RestartGameServer (reboots the warm lane against the lane owner's request), it covers the explicit queue accept path untouched by this extraction, and the lane owner already ran it 2× (red at SETUP/npc-unresolved) with the fixture boundary documented (giver 2425 never offers 251). G3 explicitly not run (diagnostics-owned).

## Remaining roam responsibilities (not this slice)

Party follow/assist, PvP engagement, needs-orchestration + farm travel/crop
tracking, homestead GOAP host, hunt acquire/engage/loot, butcher
acquire/engage, route issue/advance + actor tick pump + ground clamp +
broadcast + cadence/dormancy, actor factory/GetOrCreateActor, perception
providers, combat skill-choice calls, needs constants/phase enum, legacy
test-only seams — all untouched inline leg discipline until their own
extraction (ownership map: brain-wake-ownership.md §3).

## Stop conditions encountered

None triggered. No navigation architecture, GOAP restructuring, utility AI,
persistence redesign, or proven-capability modification was needed.
