# PlayerBot Brain: Wiring Audit + Jev-vs-Our-Brain Replay A/B Design

**Analyst:** Lane B audit (read-only)
**Date:** 2026-10-01
**Question:** Where does brain-improvement effort actually pay off — and can Jev (`typesafe/jev-1.13.0`) beat our own arbitration if we replay recorded decisions?

---

| item | value |
|---|---|
| `git rev-parse HEAD` | `626548de92b8dca8af1f0f92e22d37c9fbe6074e` |
| branch | `develop` |
| dirty files (`git status --porcelain`) | **28 before this audit → 30 after**. The delta is the 2 artifacts this audit produced (this report, plus `.hermes/` created by the harness). All 28 pre-existing entries are inherited work, not mine. |

No source file was created, edited, or deleted by this audit. The only file written is this report.
No build, no test run, no `gate.sh`.

---

## 2. Data availability verdict — **CASE (b)**

> Only movement/event traces exist. **No per-decision records have ever been collected.** One harvest run with the flag armed is required before any A/B comparison is possible.

### 2.1 Evidence

| probe | result |
|---|---|
| `Logs/jev-shadow.jsonl` anywhere in checkout (incl. `bin/`, `.worktrees/`) | **absent** — `find . -name 'jev*'` returns nothing |
| `AAEMU_JEV_SHADOW` in `.env.example`, `docker-compose*.yaml`, `Scripts/`, `scripts/`, `.github/` | **zero hits** |
| `AAEMU_JEV_SHADOW` repo-wide | only `JevShadow.cs` itself (lines 87, 95, 102, 418, 420, 423) |
| env vars that compose/scripts *do* set | `AAEMU_PRESENCE_*`, `AAEMU_NEEDS_FARM_ENABLED`, `AAEMU_BOT_CTRL_*`, `AAEMU_LIVE_RIG`, `AAEMU_MCP_BOT` — never the shadow flag |
| `traces/bot-trajectories/*.jsonl` | 5 files, **34.4 M rows each**; keys `ts, bot_id, name, pos{x,y,z}, d_xy, d_z, speed_mps, slope_pct, stalled, total_dist_m` — **pure kinematics, zero candidates, zero scores** |
| `traces/player-actions/*.jsonl` | 14 files (human traces); keys `ts, elapsed_ms, character_id, category, event, data` — **event log, not arbitration** |
| `playerbot-brain-evidence.zip` → `lane-logs/g3-quest-autonomy-report.json` | `"verdict": "HARNESS-UNKNOWN"`; `claim`: *"no production verdict: OUR bot showed no per-bot step signal within the 30s budget"* |

**Conclusion: the pipeline has never been armed.** The disagreement dataset is empty. This is the single blocking prerequisite.

### 2.2 What `JevShadow` *would* collect (declared, not observed)

`JevShadow.cs:444-467` — `JevShadowRecord` already carries exactly what the A/B needs:

```
schema, scenario, cycle_id, wake_sequence, character_id, inputs_hash, decide_detail,
candidates[{goal, action, priority, personality_weight, reason}], candidate_count,
rejection_count, selected_goal, selected_action, verdict, reason, policy,
request{trace_id, action, state}, completed_quest_ids, audit_row_count,
travel / survival / needs / combat (optional frozen brain inputs)
```

That is **candidates + scores + the incumbent's pick + the outcome references**. The harvest side is already built and correct. What is missing is (1) arming it, and (2) a consumer.

### 2.3 Two defects in the harvest surface that must be fixed before a harvest

| defect | evidence | impact |
|---|---|---|
| The per-brain `JevShadowBrains` carrier is **never populated in production** | `BotRoamStepExecutor.cs:1552` calls the **4-arg** overload; the **5-arg** overload (`JevShadow.cs:132-137`) — the one that carries `JevShadowBrains` — has no caller outside `JevShadowTests.cs` | Every row's `travel`/`survival`/`needs`/`combat` blocks will be **null**. Only the quest-arbitration slice is harvestable. Re-rankable brain classes would be *empty*. |
| Only the quest leg emits | the single `MaybeEmitQuest` call site is inside `StepQuestLeg` | Combat / travel / needs / loot arbitration wakes are **not recorded at all** |

A third, lower-severity note: `JevShadow.Record` is built from `result.Rejections`, i.e. **only the non-selected candidates** carry an explicit score row. The selected proposal's score components are recoverable only from `policy`/`request`, not per-candidate. For a fair re-rank, the selected candidate must also be materialized with its `priority` and `personality_weight`. *(Evidence tier: declared — read from source, not observed in a produced row.)*

---

## 3. The proposed A/B experiment

### 3.1 What Jev can and cannot do — and therefore what the experiment may claim

`typesafe/jev-1.13.0` is a **stateless text/JSON classifier**: no world access, no memory between calls, no action execution. It therefore cannot be a better *brain*; it is a **re-ranker of candidates the game brain already enumerated**.

The honest scope is the **arbitration/scoring layer only**:
`BotGoalArbiter` → `QuestDecisionScenario` proposals → `Goap/IGoalArbitrator` → `CombatTargetScorer` → `NeedsBrain.Decide` → `SurvivalBrain.Decide`.

It does **not** test perception, navigation, or action execution. Any claim beyond arbitration would be overclaim under the AGENTS.md evidence-honesty gate.

**Feasibility probe (live, this session, 1 wake, synthetic):** Jev was given a frozen quest wake with 4 candidates and the incumbent's pick, and returned a single legal candidate with `confidence 0.81` and a usable `probabilities` map. The mechanism works. *(Evidence tier: live-executed probe on synthetic data — not a production wake.)*

### 3.2 Experiment specification

| field | definition |
|---|---|
| **Unit** | one recorded arbitration wake (one JSONL row of `jev-shadow/v1`) |
| **Incumbent** | `selected_goal` / `selected_action` — what the game brain actually did |
| **Challenger** | Jev's `choice` over the **same** `candidates[]`, same order, same labels |
| **Primary metric** | **agreement rate** = P(challenger == incumbent) |
| **Secondary metric** | on oracle-scored classes only: **win / tie / loss** vs incumbent |

State passed to Jev (justification in §3.4): `scenario`, the `candidates[]` array **verbatim in recorded order**, and the wake's own decision context. **Do not** include `selected_action` in the prompt — that leaks the answer and inflates agreement. It is kept only for scoring, outside the prompt.

### 3.3 The counterfactual problem and the per-class oracle

Only the incumbent's action executes, so an outcome delta exists **only for the game's pick**. Wherever Jev diverges, there is no observed outcome. Each decision class is therefore assigned one of three treatments:

| class | oracle | verdict computable? |
|---|---|---|
| **quest selection** | **Objective.** Canonical chains recorded in `playerbot-blockers.md` PB-002 (Solzreed → Dewstone 921/931 → Marianople 321/173/188 → Sharpwind 6381), plus `Docs/wiki/Golden-Route-Solzreed.md`. A challenger that picks a prerequisite-blocked or out-of-band quest **loses**; a canonical next step **wins**. | **YES** |
| **survival / needs veto** | **Objective.** `SurvivalBrainPlanner.CouldVeto` defines the predicate (`BotRoamStepExecutor.cs:1393`). At the recorded HP%, not vetoing when the predicate is true = loss; vetoing when it is false = loss. | **YES** |
| **combat target** | **Conditional.** `CombatTargetScorer`'s own scoring function is a defensible *reference policy*, but that is circular — it re-tests the incumbent against itself. Use only as a **sanity check**, never as the headline metric. | weak — report as tie-by-construction |
| **travel / loot / GOAP leg choice** | none defensible from the repo | **NO — declared undecidable.** Report agreement rate only; do not invent a score. |

### 3.4 Prompt design

- Candidates must retain **recorded order and exact label strings**, so a `choice` maps back to a concrete proposal and choices are comparable across wakes.
- Include each candidate's `priority` and `personality_weight` — these are the incumbent's own score components and are what a fair re-ranker must see. **Omitting them measures Jev's priors, not its arbitration.**
- Exclude `selected_action`, `verdict`, `reason`, `decide_detail` — all leak the incumbent.
- Keep the instruction frame constant across all wakes (no per-wake hand-tuning); otherwise the comparison measures prompt engineering, not arbitration.

### 3.5 Falsification conditions — what would kill the hypothesis

The experiment must be able to lose. It is falsified if **any** of:

1. **Agreement ≥ 99 %** across all classes — Jev is merely echoing the priority ordering it was handed; it is not arbitrating, and cannot improve anything.
2. **Win rate < 50 %** on the oracle-scored quest or survival classes — Jev is worse than the incumbent.
3. **Jev picks an illegal candidate** (one the engine would reject; e.g. `reason` = `quest-not-active`) in > 1 % of divergent wakes — the disagreement is noise, not signal.
4. **Cost exceeds ~$0.50 per 5 000 wakes** without a usable verdict — the economics break before the signal does.

### 3.6 Cost

Observed this session: **$0.043 for 312 judged units** (210 + 102) ≈ **$0.00014/unit** at `typesafe/jev-1.13.0`.

| volume | cost | wall clock (observed rate: ~479 units / 1.8 s) |
|---|---|---|
| 1 000 wakes | ~$0.14 | ~4 s |
| 5 000 wakes | ~$0.70 | ~20 s |
| 50 000 wakes | ~$7 | ~3 min |

Large-scale replay is affordable — cost is not the binding constraint. **Data is.**

### 3.7 The ladder

| tier | what | cost | blocker |
|---|---|---|---|
| **L1** | offline replay of records **already on disk** | $0 | **BLOCKED — no records exist** (§2) |
| **L2** | one harvest run with `AAEMU_JEV_SHADOW=1`, then L1 on it | $0 + one run | needs §2.3's two defects fixed, else only quest arbitration is captured |
| **L3** | live A/B bridge — Jev decides in the eval kernel, the game executes | slow round trip per wake; **mutates live bot behavior** | not attempted, and not recommended |

**Recommended: L2 only.** L1 is not available. L3 changes live bot behavior from a stateless classifier with no world model — an unsound design regardless of outcome.

---

## 4. Judge-based wiring classification

### 4.1 Method and honesty statement

Rubric was frozen as constants **before** any data was loaded (4 questions: Q1 wiring status, Q2 mutates-live-state, Q3 brain stage, Q4 improvement leverage). Units = every `.cs` under `Core/Managers/Bots/`, `Models/Game/Bots/`, `Scripts/Commands/BotCmd.cs`, `Scripts/SubCommands/Bots/`.

**Sampling: none.** All units were classified; nothing is extrapolated.

| step | count |
|---|---|
| raw candidate `.cs` files | 213 |
| removed: `bin/`+`obj/` | 0 |
| removed: `*.Designer.cs` | 0 |
| removed: `*Tests.cs` | 0 |
| **removed: > 200 KB by size** | **3** — `GameplayActor.cs` (256 086 B), `QuestBehavior.cs` (219 114 B), `BotDriveBridge.cs` (234 542 B) |
| units judged | **210** |
| **truncated at the 8 000-char cap** | **102** (49 % of units), each marked `/*[[TRUNCATED_BY_AUDIT_CAP]]*/` and **counted, not dropped** |
| supplementary tail pass | 102 units judged on their final 7 000 chars |
| judge model | `typesafe/jev-1.13.0` · 210 units × 4 Q in 26.7 s · $0.031 |
| tail pass | 102 units × 2 Q in 7.5 s · $0.012 |
| judge call errors | **0** |

### 4.2 ⚠️ The judge is the wrong model for Q1 — read this before using the tables

The harness routed the batch to **`typesafe/jev-1.13.0`, a fast entity-tagging model, not a code-reasoning model.** Symptoms, all visible in the raw output:

- It **never** emitted `dead_legacy` or `parallel_implementation` — 0 of 210, for both the main pass and the tail pass.
- 102/210 units had Q1 top-probability **< 0.70** (median 0.70, p10 0.49) — the escalation trigger fired for half the corpus.
- Head-vs-tail stage agreement was only **76/102 (75 %)**.
- The tail pass changed exactly **1** stage label — it was not recovering information, it was guessing consistently.

**Therefore the Q1 judge verdicts below are NOT the finding.** They are reported for completeness. The finding is the deterministic ground truth in §4.4, which is what every downstream recommendation rests on.

### 4.3 Raw judge output (n=210) — reference only

**Q1 — judge verdict × deterministic ground truth**

| judge \ ground truth | wired_live (165) | declared_unwired (45) |
|---|---|---|
| `wired_live` | 86 | **11** |
| `declared_unwired` | 10 | 21 |
| `other` | 55 | 11 |
| `wired_partial` | 14 | 2 |
| `dead_legacy` | 0 | 0 |
| `parallel_implementation` | 0 | 0 |

**Q2 — mutates live world state:** `true` 72 · `false` 138
**Q4 — improvement leverage:** `high_leverage_working` 110 · `low_leverage_cosmetic` 43 · `dead_no_value` 40 · `high_leverage_broken` 17 · `needs_data` 0

**Q3 — brain stage:** `infrastructure_types` 49 · `scenario_cycle` 23 · `scheduler_dormancy` 20 · `goal_arbiter` 19 · `travel` 18 · `plan_goap` 14 · `belief` 13 · `admin_control` 13 · `other` 10 · `combat` 8 · `quest` 7 · `perception` 6 · `needs_survival` 6 · `loot` 4

### 4.4 Deterministic ground truth — reachability closure

Instead of trusting the judge, wiring was decided by a **transitive reference closure** over the whole production corpus (2 918 `.cs` files, comments stripped, 3 555 declared types). Seeds: `Program.cs`, `GameService.cs`, `ScriptReflector`, `CommandManager`, `WebApiServer` + all `IController` and `SubCommandBase` implementers.

| classification | n | meaning |
|---|---|---|
| `wired_live` (DI-reachable) | **148** | reachable from a DI composition root or a startup path |
| `wired_live` (reflective) | **17** | live via `[ModuleInitializer]`, `ICommand`, `IController`, or `SubCommandBase` — invisible to a DI graph |
| **`declared_unwired`** | **45** | **no production path reaches them** |
| **total** | **210** | |

### 4.5 Confirmed vs overturned

| metric | count |
|---|---|
| Judge Q1 agreed with ground truth | **107 / 210 (51 %)** — a coin flip |
| **OVERTURNED** — judge said `wired_live`, ground truth says unreachable | **11** |
| **CONFIRMED dead** — judge said non-live and it is dead | **34 of 45** |
| **CONFIRMED live** — judge said `wired_live` and it is live | **86 of 165** |
| **False alarms** — judge said non-live, ground truth says live | **79** |
| Escalation set (judge non-live) | 113 → 34 dead, **79 actually live** |
| Low-confidence set (< 0.70) | 102 → 26 dead |

Escalated units confirmed **by reading/grep, not by the judge**. The judge's "unwired" signal was **false in 79 of 113 cases** — because reachability is a graph property that a per-file classifier cannot see.

---

## 5. Old monolith vs. new decomposition — **the new brains ARE wired**

**Verdict: the `Goap/` `Travel/` `Combat/` `Loot/` `Needs/` `Survival/` decomposition is NOT parallel to `GameplayActor` — it is composed into the live wake. Effort on the new brains is not wasted.**

### 5.1 The live call chain (file:line verified)

```
BotPresenceCoordinator.cs:357 / :452   _scheduler.Start()
  └─ PlayerBotScheduler.cs:118  Start()
       └─ PlayerBotScheduler.cs:149   TickManager.Instance.OnTick.Subscribe(TickDrain, useAsync:false)
            └─ PlayerBotScheduler.cs:604  TickDrain → :618 DrainTickQueue
                 └─ PlayerBotScheduler.cs:348  ExecuteStepOnExecutionBoundary   ← single execution boundary
                      └─ :406  _executor.StepAsync(runtime, token)
                           └─ BotGoalArbiterStepExecutor.cs:59-62   arbitrate → stamp wake seq → delegate
                                └─ BotRoamStepExecutor.cs:98   ActorFactory = c => new GameplayActor(c)
                                     ├─ :1393  SurvivalBrainPlanner.CouldVeto(...)        ← Survival/
                                     ├─ :1417  SurvivalBrainPlanner.Prepare(actor, frozen) ← Survival/
                                     ├─ :2324  NeedsFarmLegDispatch.DecideNeedsTravelLeg(  ← Needs/
                                     │          NeedsBrainPlanner.Prepare(...))
                                     ├─ :2390  NeedsFarmLegDispatch.DecideNeedsFarmAction(  ← Needs/
                                     └─ :1941  new Goap.GoapPlanRunner()                    ← Goap/
QuestBehavior.cs:2300/2992  TravelLegDispatch.Prepare      ← Travel/
QuestBehavior.cs:2512      CombatBrainPlanner.Prepare    ← Combat/
QuestBehavior.cs:2534      CombatTravelDispatch.Prepare   ← Combat/ + Travel/
QuestBehavior.cs:2679      LootBrainPlanner.Prepare       ← Loot/
```

`BotRoamStepExecutor.cs:62-66` states the arrangement explicitly: *"this replaces `GameplayActorStepExecutor` as the production `IBotStepExecutor` wiring in Program.cs."*

### 5.2 The decomposition is real but **unevenly adopted**

| folder | files | reachable | orphaned |
|---|---|---|---|
| `Travel/` | 5 | **5** | 0 |
| `Combat/` | 6 | **6** | 0 |
| `Loot/` | 6 | **6** | 0 |
| `Needs/` | 6 | **6** | 0 |
| `Survival/` | 4 | **4** | 0 |
| `Goap/` | 23 | 22 | `GoapBotStepExecutor` |
| `Belief/` | 4 | **0** | **4** — parked as a guarded unit (see §5.2 note), not orphaned dead code |

`GameplayActor.cs` (256 KB) remains the **action execution substrate** — every brain calls `IGameplayActor` methods and returns; the actor performs the real engine action. That is the intended "additive layer" shape (AGENTS.md rule #10), **not** a parallel gameplay path.

**`Belief/` is unwired, but it is a deliberately guarded parked surface — NOT orphaned dead code.** All four files (`AttentionScorer`, `BeliefInterpreter`, `BotBeliefState`, `CurrentPlace`) do have zero *production execution* callers, and that half stands. **Two corrections to this audit's own framing (2026-10-01, read-only source re-verification):**

1. **The reference direction was backwards.** This line said "`BeliefInterpreter` is referenced only by `AttentionScorer`, which is referenced by nothing." The true edge is the reverse: `AttentionScorer.cs:185` calls `BeliefInterpreter.EnemyBandM`. `AttentionScorer` is the *consumer*; `BeliefInterpreter` is the supplier of the constant. Neither is called from a decision path, so the "unreachable" conclusion is unchanged — but the call graph was stated inside-out.
2. **"Wire it or delete it" is wrong on both halves.** Wiring the belief layer into a decision path would *fail the build of the architecture*, and deleting it would delete its guard. See below.

**Why it is parked rather than orphaned (three independent lines of evidence, each verified):**

- **A guard test forbids wiring it.** `PerceptionStackSurfaceTests.cs:76` `PerceptionTypes_AreReferencedByNoShippingTypeOutsideTheStack` scans the shipping assembly's IL and **fails if any type outside the perception stack references** the listed types — and `BotBeliefState`, `BeliefFacts`, `BeliefNumbers`, `BeliefRefs`, `BeliefInputs`, `BeliefInterpreter`, `AttentionScorer`, `CurrentPlace`, `ThreatLevel` are registered in its `PerceptionTypeNames` list at `:62-70`. Production code *declines* them on purpose, citing that test: `Combat/CombatBrainTypes.cs:13-18` and `Combat/CombatBrain.cs:140-141` both derive their own `CombatThreat` verdict explicitly "never the belief stack's `ThreatLevel`". So the layer is **locked in place**, not merely unused.
- **It carries 27 passing unit tests** — `BeliefStateTests.cs` (21) and `AttentionScorerTests.cs` (6), added in `56b90ba90` (2026-09-25), which went back and *extended* the pre-existing guard rather than bypassing it.
- **Nothing can execute it anyway.** `BotPerceptionSnapshot.Capture`, `PerceptionDelta` and `BotRadarProjection` each have zero production callers, so `BeliefInterpreter.Interpret(Snapshot, PerceptionFrameDiff, …)` has no input path. The whole ~2060-line perception+belief stack is parked **as a unit**.

**Timeline:** guard test `e05ddbb9f` (2026-09-24) → perception stack `60ce2c063` (2026-09-24) → belief facts/attention `56b90ba90` (2026-09-**25**). The guard predates the belief layer; the belief layer was added *inside* the guarded boundary on purpose.

**Status:** unit-test-only and deliberately guarded. NOT wired, NOT live, no H. This correction establishes no new evidence tier.

---

## 6. Declared but never wired — 45 files

All 45 verified by **zero-caller search over the production corpus with comments stripped**. Clustered by subsystem:

| cluster | n | files | why unreachable |
|---|---|---|---|
| **Belief stack** | 4 | `AttentionScorer`, `BeliefInterpreter`, `BotBeliefState`, `CurrentPlace` | **Corrected 2026-10-01:** zero *production execution* callers is right, but this row's reasoning was inverted (`AttentionScorer` calls `BeliefInterpreter.EnemyBandM` at `AttentionScorer.cs:185`; it is not "referenced by nothing"). The layer is a **deliberately guarded** planning/diagnostic surface parked as a unit — `PerceptionStackSurfaceTests.cs:76` FAILS if any shipping type outside the perception stack references these types, and 27 unit tests cover them. **Not a "wire it or delete it" candidate** — see §5.2 |
| **Hauler pack-craft chain** | 4 | `HaulerDriveRouteCycle`, `HaulerPackCraftLoadCycle`, `HaulerSaleDepositCycle`, `HaulerVendorMultipackCycle` | zero references. A complete Slave/pack crafting and selling loop that never runs |
| **Village day-cycle chain** | 4 | `VillageDayCycle`, `VillageEveningCycle`, `VillageFullDayCycle`, `VillageFullDaySoak` | referenced only by each other + `BotDriveBridge` (E2E). The M8 "living village" loop is **E2E-only** |
| **Justice / rumor chain** | 5 | `BotWitnessPolicy`, `RumorComposer`, `RumorStore`, `TheftReportComposer`, `SentenceCalculator` | zero references. Crime-report composition never runs |
| **Combat second trees** | 4 | `CombatExecutor`, `RoleCombatDecisionTree`, `PersonalityUtilityMap`, `ReplanTaxonomy` | zero references. `RoleCombatDecisionTree` sits beside the *wired* `CombatDecisionTree` — a superseded generation |
| **Superseded step executors** | 4 | `GameplayActorStepExecutor`, `GoapBotStepExecutor`, `UnwiredBotStepExecutor`, `UnwiredPlayerBotLifecycleService` | `GameplayActorStepExecutor` is named in `BotRoamStepExecutor.cs:38` and `IBotStepExecutor.cs:8-11` as the thing it **replaced**; `UnwiredBotStepExecutor`'s own doc says *"no longer DI-wired"*. `GoapBotStepExecutor` has **zero references and zero tests** |
| **Party / Expedition** | 4 | `ExpeditionFormationScenario`, `PartyDecisionScenario`, `PartyFollowAssistScenario`, `PartySpikeScenario` | `PartyDecisionScenario` zero refs; the rest E2E-bridge-only. Expedition/Party formation never runs autonomously |
| **Crafter / economy** | 6 | `EconomyDayCycleScenario`, `EconomyDecisionScenario`, `FishingContestScenario`, `CrafterMerchantCycle`, `CrafterWorkstationCycle`, `FarmerCycleScenario` | E2E-bridge-only or zero refs |
| **Survey / senses** | 3 | `BotSurveySenses`, `SurveyLegRunner`, `SolzreedStarterCorridorProfile` | `SurveyLegRunner` zero refs → `BotSurveySenses` referenced only by it |
| **E2E-only scenarios** | 2 | `M53CoreSurfaceExitScenario`, `TradeHandshakeScenario` | bridge-only |
| **Orphan adapter** | 1 | `PlayerBotControllerAdapter` | **zero production references**; its 33 contract actions (`PartyInvite`, `Loot`, `Mount`, …) are exercised only by `PlayerBotControllerAdapterTests` |
| **Telemetry / coverage** | 2 | `BotProgressionCursor`, `PopulationGoalCoverage` | zero refs |

**Consequence for the experiment:** every decision class the Jev A/B wants to re-rank beyond the *quest* leg is either (a) never emitted, or (b) belongs to a brain that is live but whose candidate sets are not captured. See §7.

---

## 7. Parallel implementation — **none found**

| check | method | result |
|---|---|---|
| parallel character/inventory/quest/property/economy state | grep for `SqlConnection\|MySqlConnection\|INSERT INTO\|UPDATE .* SET\|ExecuteNonQuery\|ExecuteScalar` across `Core/Managers/Bots/` | only `PlayerBotMetadataStore.cs` and `PlayerBotAuditSink.cs` — both write to **additive bot metadata tables** (`playerbot_metadata`, `playerbot_audit`), which AGENTS.md rule #3 explicitly sanctions |
| own tables / own persistence for Characters | grep `Prepare(` / `DbManager` | none in the Bots subsystem |
| own movement or combat simulation | read `GameplayActor.cs` action bodies | every action dispatches into real engine services (`TeamManager`, `CharacterResurrection`, `QuestManager`, `SlaveManager`, …), not a simulation |

**Verdict: no AGENTS.md rule #9/#10 violation found.** The subsystem composes around ordinary `Character` records. `PlayerBotControllerAdapter` and `GameplayActor` are two adapters over the *same* engine, not two engines.

---

## 8. Ranked next actions — cheapest first

| # | action | why | evidence tier |
|---|---|---|---|
| **1** | **Fix the `JevShadowBrains` call site** — pass the brains carrier at `BotRoamStepExecutor.cs:1552` | 1-line change unblocks combat/travel/needs/loot re-ranking. Without it every harvested row has `null` brain blocks | **declared** (read from source) |
| **2** | **Fix `JevShadowRecord` candidate coverage** — materialize the *selected* proposal with its `priority`/`personality_weight`, not only `Rejections` | Jev cannot fairly re-rank a candidate it cannot see | **declared** |
| **3** | **Run one harvest with `AAEMU_JEV_SHADOW=1`**, `AAEMU_JEV_SHADOW_SAMPLE=1`, against the presence stack | produces the case-(b) → case-(a) transition. Cheapest possible step; costs one run | **declared** (flag default is OFF; `JevShadow.cs:91`) |
| **4** | **Run the L1 A/B on the quest + survival classes only** | the two classes with a defensible oracle (§3.3). ~$0.14 per 1 000 wakes | **live-probed** (mechanism verified on a synthetic wake) |
| **5** | ~~**Decide the fate of `Belief/` (4 files)** — wire it as the perception→belief stage, or delete it~~ **WITHDRAWN 2026-10-01** | it is the only brain folder with **zero** live files; §5.2. **Both options in this row are wrong.** Wiring `Belief/` into a decision path is forbidden by `PerceptionStackSurfaceTests.cs:76`, and deleting it would remove a guarded, test-covered planning/diagnostic surface. Correct framing: `Belief/` is **parked by design, as a unit, with the guard test as the load-bearing reason** — decide it when the perception stack gains a production entry point, not before. See §5.2 | **static analysis** |
| **6** | **Decide the fate of the 41 other unwired files** — particularly `PlayerBotControllerAdapter` (33 unused actions) and the 4 `Hauler*` files (a complete unused pack-craft economy loop) | 21 % of the subsystem is unreachable; effort spent improving it changes nothing | **static analysis** |
| **7** | **Add candidate emission outside the quest leg** if combat/travel arbitration is to be re-ranked | currently only `StepQuestLeg` emits | **declared** |
| — | **L3 live A/B** | **not recommended** — a stateless classifier driving live bot actions is unsound | — |

### Would the shadow JSONL have diagnosed the `needs_data` / broken stages?

**No — and that is the finding.** For `PB-FARM-01` (3 m merchant trap, 150 m soil horizon) the shadow rows *would* have carried the `Needs` brain inputs, but `JevShadow` never ran and never covered the needs-farm leg. For `Belief/` there are no candidates to capture because there is no brain. **The evidence needed to prioritize brain work is exactly the evidence that was never collected** — which is why §8 items 1-4 precede every tuning task.

---

## 9. What could not be determined

| gap | why | what would unblock it |
|---|---|---|
| Whether the 45 unwired files were **intentionally** parked or are regressions | git history not walked (read-only scope) | `git log --diff-filter=A -- <file>` per file, or a doc stating the park decision |
| Whether `playerbot_audit` / `playerbot_metadata` contain usable rows | no MySQL access in this environment | `SHOW TABLES LIKE 'playerbot%'` + row counts on the test DB |
| Whether any historical run armed `AAEMU_JEV_SHADOW` **outside this checkout** | only the repo and this filesystem were searched | the test server `.165` `Logs/` directory, and prior CI/lane environments |
| Real wake rates, and therefore whether a 5 000-wake harvest is ~hours or ~weeks | no live server, no metrics endpoint | one instrumented presence run reporting `TotalStepsRun` |
| Whether Jev beats the incumbent | **no data exists** | §8 items 1-4 |
| `QuestDecisionScenario` proposal scoring internals beyond `priority`/`personality_weight` | not read at full depth | read `BotDecisionProposal.cs` + the per-proposal scorers |
| **Any live behavior of the bot brains** | — | this audit is **static analysis only**. No claim in this report asserts live behavior. A passing unit test is not proof a path runs in production. |

---

## 10. Evidence tiers used throughout

- **declared** — the code says so (doc comment, flag default, signature); not observed running.
- **unit-test-only** — a test exercises it; no production caller.
- **live-probed** — I executed it this session (the single-wake Jev feasibility probe).
- **live-unverified** — static analysis only; would require a running server to confirm.

**Nothing in this report claims live PlayerBot behavior.** The wiring findings are reachability results from static analysis over the production corpus.