# PlayerBot Brain Roadmap — Phase 1 (diagnostics/observability half)

Owner: phase1-diag. Lane: warm q0 (`E2E_ROOT=/root/aaemu-e2e-q0`, adopted only).
Scope: diagnostics/observability ONLY. Behavior extraction is owned by phase1-behavior —
`BotRoamStepExecutor.cs`, `QuestDecisionScenario.cs` (shell) / `QuestBehavior.cs`,
`NeedsDecisionScenario.cs`, `GoapPlanRunner.cs`, `GameplayActor.cs` action bodies, and the
scheduler core (`PlayerBotScheduler.cs`) are NOT edited here. Collision protocol: message
before touching shared-compile surface (`IGameplayActor.cs`, fakes, adapter).

Context docs (read first, not re-derived): `brain-wake-ownership.md` (§1 wake trace,
§5 interrupt model), `brain-g3-funnel.md` (§1-2 runs, 4.4km anomaly §50-60),
`brain-boundaries.md` (§5 telemetry schema, §2 perception), `prereq-changes.md`
(DecisionCycleId, arbiter stamping, PreemptCurrent), `g3-diagnosis-arbitration.md`
(§3 funnel F0-F10, §4 travel-vs-proposal), `g3-diagnosis-repro.md` (leakage finding).

## 0. GROUND TRUTH (2026-09-19, branch develop)

- HEAD: `6c3efe25ecb68998544eb3f75699bc1674c7eef1`
- Log-5: `6c3efe25e` live quest perception loop driver + travel arm · `d0bea9e6a`
  full-suite run-order repair · `53a8255f7` Wave One exit dossiers · `6f4c17d84`
  combat/homestead/loop tests · `d58d68c5d` homestead provisioning + GOAP bounds
- `git status`: 25 modified + 19 untracked. Diff-stat headline: 25 files +1400/-889.
- Dirty PlayerBot game files (pre-existing prereq Parts 1-4 work + behavior landing,
  NOT mine — zero edits by phase1-diag at doc creation):
  `ActorAuditRecord.cs`, `ActorRequest.cs`, `BotActionCommandQueue.cs`,
  `BotGoalArbiter.cs`, `BotGoalArbiterStepExecutor.cs`, `BotRoamStepExecutor.cs`,
  `GameplayActor.cs`, `Goap/GoapPlanRunner.cs`, `IGameplayActor.cs`,
  `PlayerBotControllerAdapter.cs`, `QuestDecisionScenario.cs` (recomposed shell),
  new `QuestBehavior.cs` + `BotBehaviorRuntime.cs` (phase1-behavior landing),
  `E2eStack.cs` (Part 1 manifest), 5 unit-test fakes (compile stubs).
- Untracked E2E: `Q0ActorAcceptQuestPilotTests.cs`, `G1ActorMoveConvergenceTests.cs`,
  `G2ActorTalkCapabilityTests.cs`, `G3AutonomousQuestAcceptTests.cs`,
  `PreemptCurrentLifecycleTests.cs` (exist — re-run, do not recreate).
- NOTE (2026-09-19 mid-day): phase1-behavior landed the quest-leg extraction
  (new `QuestBehavior.cs` + `BotBehaviorRuntime.cs`, shell `QuestDecisionScenario.cs`).
  Pre-landing cites below say so; all cites re-grounded at implementation time.
  Builds green per behavior report (Game + IntegrationTests + UnitTests, 0 errors).

## PHASES

### Phase 1 — Behavior Runtime + Quest Vertical Slice + Brain Inspector — COMPLETE 2026-09-19 (director decision; all 10 exit criteria verified, see §5)
- Goal: prove one coherent autonomous production decision chain. Main work: canonical
  perception truth, bot-specific decision identity, minimal behavior runtime,
  QuestBehavior extraction, G3 proof, Brain Inspector.
- Entry: prereq Parts 1-4 merged-dirty (DecisionCycleId, arbiter stamping,
  PreemptCurrent); G3 funnel analyzed (F0-F10); warm q0 lane available.
- Exit: builds 0 errors; G3 classified with per-bot proof; Q0/G1/G2 green;
  observe); funnel first-zero visible in observe payload; position truth recorded.
- Dependencies: warm q0 lane (adopt-only; ONE rebuild+restart allowed for new
  observe fields); behavior extraction lands first (lane runs ordered after it).
- Components: `BotGoalArbiterStepExecutor.cs` (wake sequence), `ActorRequest.cs` /
  `ActorAuditRecord.cs` (WakeSequence), `IGameplayActor.cs` (additive setter) +
  `GameplayActor.cs` staging plumbing ONLY (no verb bodies),
  `PlayerBotControllerAdapter.cs` (delegate), `BotActionCommandQueue.cs`
  (spec/snapshot carry), `BotDriveBridge.cs` quest-observe additive fields,
  G3 test (wakeSeq signal, additive), 5 unit-test fakes (stub), this doc.
- Evidence: §1 position truth, §2 wake identity, §3 funnel fields, §4 G3
  classification, §5 regressions (all below as completed).

### Phase 2 — Perception Convergence — NOT_STARTED
- Goal: one authoritative decision-cycle perception flow (AAEmu state →
  ActorObservation → BotObservedContext → specialized projections); add world
  identity, zone identity, entity generation/safety, skill readiness, cooldowns, GCD,
  equipment, vehicle/mount state, threats, navigation state, quest opportunities as
  needed; eliminate duplicate live reads; do not duplicate engine legality.
- Exit: TBD at phase start. Dependencies: Phase 1. Components: TBD. Evidence: none yet.

### Phase 3 — Navigation Runtime — NOT_STARTED
- Goal: separate MoveTo from NavigateTo with explicit semantics (moving targets,
  reacquisition, repath, stuck detection, unreachable result, world/zone identity,
  portal travel, zone transition, transport, arrival policies); quest behavior
  eventually requests navigation intent.
- Entry: Phase 1 exit. Exit: TBD. Dependencies: Phase 1 (travel-vs-proposal §4
  analysis is the input). Components: TBD. Evidence: none yet.

### Phase 4 — Goal Utility + Commitment — NOT_STARTED
- Goal: evolve BotGoalArbiter from priority+eligibility toward eligibility + utility
  + personality + commitment + hysteresis; prevent thrashing; retain inspectability;
  dashboard exposes candidate scores.
- Entry: Phase 1 exit. Exit: TBD. Dependencies: Phase 1 (arbitration counts/stamps).
  Components: TBD. Evidence: none yet.

### Phase 5 — Behavior Expansion + BotRoam Decomposition — NOT_STARTED
- Goal: move coherent activities out of BotRoamStepExecutor one at a time
  (Needs/Farm/Combat/Economy/Party/Exploration), each with its own live vertical
  proof; no mass-refactor.
- Entry: Phase 1 exit (QuestBehavior is the precedent). Exit: TBD.
  Dependencies: Phase 1. Components: TBD. Evidence: none yet.

### Phase 6 — Planner Containment / HTN / GOAP — NOT_STARTED
- Goal: planners subordinate to behavior/brain (brain WHAT, behavior lifecycle,
  planner HOW); GOAP no longer owns global arbitration/intent/lifecycle; evaluate
  HTN where natural.
- Entry: Phase 1 exit. Exit: TBD. Dependencies: Phases 1, 5. Components: TBD.
  Evidence: none yet.

### Phase 7 — Scale / AI LOD / World Population — NOT_STARTED
- Goal: large populations via existing scheduler semantics
  (reactive/active/goal/background/offscreen cadences); measure before optimizing.
- Entry: Phase 1 exit. Exit: TBD. Dependencies: Phase 1 (scheduler untouched).
  Components: TBD. Evidence: none yet.

### Phase 8 — Persistence + Long-Term Bot Life — NOT_STARTED
- Goal: persist only long-lived intent (profession/personality, home, places,
  preferences, relationships, long-term goals); reconstruct world truth from engine
  after restart.
- Entry: Phase 1 exit. Exit: TBD. Dependencies: Phase 1. Components: TBD.
  Evidence: none yet.

### Phase 9 — Data-Driven Tuning — NOT_STARTED
- Goal: telemetry + human traces for policy tuning without replacing deterministic
  execution; learned boundary at goal scores only; no LLM in runtime brain.
- Entry: Phases 1-8 signal availability. Exit: TBD. Dependencies: Phase 1
  (telemetry join keys). Components: TBD. Evidence: none yet.

---

## 1. POSITION TRUTH (step 2 — COMPLETE, diagnosis; repair boundary reported)

### 1.1 Surface trace (all code-cited; pre-extraction lines noted)

| Surface | Code | Value (G3 run 1, charId=10) |
|---|---|---|
| Fixture teleport echo | `BotDriveBridge.cs:789-815` — returns `spawner.Position` XYZ (ECHO, not a char read) | spawner 2425 pos |
| Teleport write | `TeleportWithRegionSync`, `BotDriveBridge.cs:79-85` — `Transform.Local.Position = pos` + ZoneId + `AddVisibleObject` | — |
| Bridge `charPos` | `BotDriveBridge.cs:957-969` — `connection.ActiveChar.Transform.World.Position` + zone/instance/world (LIVE) | (14536.6, 11117.1, 110.8) |
| Quest snapshot XYZ | `BotQuestLoopObservation.cs:91-101` — same drive character `Transform.World.Position` | same as charPos (same object) |
| `Observe()` | `GameplayActor.cs:244-270` — `Character.Transform.World.Position` + 3×25 m `GetAround` + quests/bags/party | actor's Character |
| Decision copy | `BotObservedContext.Capture`, `BotDecisionProposal.cs:67-110` — copies `ActorObservation` | — |
| Sweep | `QuestBehavior.cs:78-90` (post-extraction; was `QuestDecisionScenario.cs:121-133`) — `nearbyNpcs(character, 25f)`, first-3-take, no distance sort | EMPTY (0 rows ⇒ never swept) |
| Travel "from" | `BotRoamStepExecutor.cs:1224-1234` (pre-extraction cite; re-ground: travel stayed navigation-side) — `bot.Character.Transform.World.Position` | (15578, 15382) |
| Travel target | `ResolveQuestTravelTarget` spawner scan (world-wide, position-independent) | (15562, 15354) |

Scale: all surfaces consume the same `Vector3` world meters; no ×10/scale op on
these paths (concur with `g3-diagnosis-repro.md` §1). X/Y planar, Z up; range =
flat 2D (`MathUtil.cs:332-342`); `Transform.World` == `Local` when unparented
(`Transform.cs:381-385`) — fresh bots have no parent, so Local-vs-World is NOT
the gap.

### 1.2 World-data ground truth (live lane data, read-only)

- NPC 2425's ONLY main_world spawner: **(14536.6, 11117.107, 110.49)**
  (`npc_spawns.json`) — matches charPos to 0.1 m (Z +0.3 = ground clamp).
  Teleport WORKED; charPos is live post-teleport truth.
 - Travel target (15562, 15354) = **NPC 3597's spawner** (0.55 m away). Not 2425.
 - Quest 251 starter NPC = **3512** (Start component 383 carries ConAcceptNpc 3512;
   kind 2 == Start per `QuestComponentKind.cs:7`; offers resolved exactly as
   `QuestManager.GetQuestsOfferedByNpc`, `QuestManager.cs:2108-2110`). 3512's
   spawner: (15655.91, 15172.51). Quest 251 LEVEL 2 ∈ [1,9] ✓.
 - `offers(2425)` = {532, 533, 534} — ALL **level 30, zone 14** (out of band [1,9]).
   `offers(3597)` = {330} (level 1, in band).
 - Around travel-"from" (15578, 15382): exactly ONE spawner within 60 m (3597 at
   31.7 m — OUTSIDE the 25 m discovery horizon).
 
 ### 1.3 Verdict (two stacked defects; mechanism live-to-decide)
 
 - **(a) Fixture-giver mismatch (DATA, certain).** Quest 251 ∉ offers(2425); 2425's
   real offers are all L30 (band-filtered). Even perfect perception at the staged
   spot yields zero legal 251 proposals. The funnel-doc "staged ≤25 m" precondition
   is satisfied geometrically but meaningless: the RIGHT giver (3512, ~4.6 km away)
   is nowhere near. G3-with-giver-2425 can never PASS — fixture change belongs to
   the behavior/harness layer (NOT made here; rerun keeps quest 251 / giver 2425).
 - **(b) 4.4 km stale-position gap (certain gap, mechanism UNKNOWN statically).**
   The decision path (sweep center + travel-"from") reads (15578, 15382) while the
   live character stands at (14536.6, 11117.1). Sweep-empty is EXPECTED at the
   stale center (nothing perceivable within 25 m), so FAIL_PERCEPTION there is an
   artifact of the wrong center, not of the sweep code. All resolvers return the
   same `connection.ActiveChar` object (`TryResolveNetworkedBot :3035-3056`,
   drive :700-714, quest op :4340-4342; `Spawn` stores the reference,
   `PlayerBotManager.cs:36-52`; scheduler steps the same runtime,
   `PlayerBotScheduler.cs:359-405`), so no static mechanism explains two live
   positions — candidates: replaced `ActiveChar` instance (re-login between
   enroll-Spawn and fixture), executor-cached actor on a superseded `Character`,
   or a post-teleport relocation. **Decisive live probes** (added §2-3, one lane
   rerun): snapshot XYZ + actor XYZ + `CharacterId/ObjId/ZoneId/WorldId/InstanceId`
   on BOTH the drive character and the executor actor in one observe payload.
 - **STOP boundary:** if the probes show two `Character` instances (or two ObjIds)
   for one charId, the repair (runtime re-resolution / spawn-teleport ordering /
   stale-actor eviction) belongs to the runtime/bridge layer — reported, not fixed
   here. Dashboard/tests never become truth: the fix, wherever it lands, must make
   the DECISION SNAPSHOT read authoritative server state.
 
 ## 2. WAKE IDENTITY (step 3 — IMPLEMENTED, lane proof pending)
 
 - Design (minimal, no gameplay change, bounded): `BotGoalArbiterStepExecutor` owns
   a per-bot wake counter (`ConcurrentDictionary<uint,long>`, one entry per bot —
   same key space as the arbiter's `_activeActivity`; O(bots) memory, no history).
   Incremented once per `StepAsync` (one wake = one increment, including
   pass-through wakes). Staged onto the actor via additive
   `IGameplayActor.SetPendingWakeSequence` → `ActorRequest.WakeSequence`
   (nullable, never synthesized) → `ActorAuditRecord.WakeSequence`
   (`wake_sequence` JSON) via staging-only `GameplayActor.cs` plumbing (mirrors
   prereq Part 3; no verb bodies touched). Queue path: `BotActionSpec.WakeSequence`
   → staged like `DecisionCycleId` → `BotActionSnapshot` carry (null for external
   commands: no wake). Observe: quest-observe payload gains `wakeSeq` (decorator
   lookup by charId) — dashboard/tests join wake→terminal without new framework.
 - Status: IMPLEMENTED in-tree (builds 0 errors Game/Integration/Unit):
   decorator counter + `GetWakeSequence` + staging, interface + staging-only
   plumbing + adapter delegate, request/record/JSON fields, queue
   spec/snapshot/stage/catch-clear/backstop carry, 5 fake stubs, bridge
   `wakeSeq`+`traceWakeSeq`+`lastWakeSeq`, G3 `wakeSeq`/`traceWakeSeq` growth
   signals (backward compatible). Lane proof = G3 rerun §4.
 
 ## 3. FUNNEL DIAGNOSTICS (step 4 — IMPLEMENTED, lane proof pending)
 
 - Decision-time counts live in `QuestBehavior` (banned file): swept targets,
   per-target discover outcome, per-offering drop reason exist only as silent
   `continue`s (`QuestBehavior.cs:78-114`). The Fail DECIDE explanation extension
   (`swept=N offerings=M inBand=K legal=J firstZero=<predicate>`) was implemented
   by phase1-behavior (Fail detail carries the tallies; no `QuestRunResult`
   schema change — bridge consumes the `lastDetail` bracket block).
 - Here (allowed files): quest-observe payload gains trace-derived counts —
   `discoverCalls / discoverCompleted / discoverRejected` (enum-joined
   `DiscoverQuests` audit rows, no string parsing) + `wakeSeq` (§2) + actor-side
   `actorX/Y/Z, actorObjId, actorCharId, worldId, instanceId` (position-gap
   probes, §1.3b). Bounded: scalars only, existing trace, no new storage.
 - Status: IMPLEMENTED in-tree (builds 0 errors). Lane proof = G3 rerun §4.

 ## 4. G3 RERUN (step 5 — COMPLETE; 3/3 runs used, fixture unchanged)
- FREEZE 2026-09-20: G3 is PASS-BEHAVIOR (frozen) — see `g3-closeout-freeze.md`; evidence `/root/aaemu-e2e-q0/logs/g3-truncfix-gate-report.json` (+ `g3-refill-gate-report.json`, charId=22). All FAIL/UNKNOWN labels in this §4 are HISTORICAL run records, superseded by the freeze.
 
 - RUN 1 (boot#1, charId=2, `/tmp/g3-run1-newbin-report.json`): FAIL_BEHAVIOR,
   DISCOVERY/no-legal-proposal. Wake PROVEN per-bot (`wakeSeq` 1→92 — decorator
   counter works on-lane). `snap==actor` (no dual-object divergence on a clean
   lane; the warm-lane 4.4 km gap does NOT reproduce). `discover=0/0/0`
   (Discover never called ⇒ sweep empty; busy-reject ruled out by zero rejected
   rows). Travel reason FOSSIL from the enroll window (`from (15578,15382)` =
   spawn-area center; never re-armed over the live enroll route).
 - RUN 2 (boot#2, charId=2): FAIL_BEHAVIOR, DISCOVERY/no-legal-proposal. Same
   signature (`wakeSeq` 0 at enroll-observe — enroll poll exited on a LEFTOVER's
   global step, the exact pollution `wakeSeq` was built to replace; bot stepped
   right after, armed at spawn, teleported under the live route).
 - RUN 3 (boot#2, charId=5): HARNESS-UNKNOWN (wake UNPROVEN, 30 s global quiet).
   Teleport byte-exact (`14536.6,11117.1,110.8`); `snap==actor==spawner`.
   Prime suspect: scheduler wake-drop (skip path releases the lease without
   re-scheduling — wake-ownership §1 edge 7) + leftover dormancy (global quiet).
   Scheduler core is banned territory → STOP boundary, mechanism UNKNOWN.
- CLASSIFICATION (HISTORICAL, SUPERSEDED by the 2026-09-20 PASS-BEHAVIOR freeze — see `g3-closeout-freeze.md`): at the time, **FAIL_BEHAVIOR (DISCOVERY/no-legal-proposal)** with per-bot
  proof (wakeSeq growth, quest-action baselines, travel-reason + discover
  tallies). Proximate first-zero = sweep-empty at a live center ~24 m from the
  NPC (range edge + spawn timing); ultimate cause = wrong-giver fixture
  (251 ∉ offers(2425); 2425 offers only L30) + enroll-route fossil drift
  (teleport clears no executor Path — navigation/behavior layer, not fixed here).
 - maxTraceWakeSeq is DEPLOYED (binary-verified) but unproven-live (no run has
   yet observed a stamped trace row externally; run-1 proved the counter half).
 
## 5. REGRESSIONS (step 6 — COMPLETE 2026-09-19 per director; all 10 exit criteria verified)

- G1ActorMoveConvergenceTests: GREEN (adopted warm lane, 5 s).
- G2ActorTalkCapabilityTests: GREEN (adopted warm lane, 3 s).
- Q0ActorAcceptQuestPilotTests: RED twice at SETUP/npc-unresolved (giver 2425
  objId 0 after full 30 s poll on fresh boots; `/tmp/q0-fail1-report.json`).
  Fresh-boot NPC-availability (spawner/spawn-radius timing) is a NEW LAYER
  (spawner engine + Q0 fixture, neither owned here; Q0 file not editable here)
  → STOP boundary. Same-lane G3 resolved 2425 minutes later, so data/registry
  are healthy; the failure is boot-timing, not content. Open Q0-side oddity
  (not verdict-affecting): Q0 charPos prints dotless ×10 magnitudes
  (`155631,153551,1281`) vs G3 floats on the same op — unresolved.
- Q0 RE-PROOF 2026-09-19 (q0pilot lane, `E2E_ROOT=/root/aaemu-e2e-q0`,
  ports 2237/2239/2250/2260/2234/2280/3311, max 2 runs, no manual lane restart):
  run 1 HARNESS-FAIL (wrong env — default ports, `EnsureDb` 3306 conflict; zero
  legs, `failBoundary:none`, wall 3.9 s; archived `/tmp/q0-run1-harnessfail-report.json`;
  no Q0 boundary verdict); run 2 PASS (`verdict:PASS`, `failBoundary:none`;
  enter-world 142.1 s inWorld=True objId=22011, enroll 1.3 s stepped=False,
  fixture-stage 9.0 s npcObjId=44183 non-zero, explicit-accept 0.3 s
  Completed active=True; setup 152.4 s / exec 0.3 s / wall 152.7 s; live report
  `/root/aaemu-e2e-q0/logs/q0-actor-accept-pilot-report.json`).
  Spawner-timing vs registry: TRANSIENT AVAILABILITY, not registry/data — 2425
  materialized promptly this boot (fixture window 9.0 s incl. setLevel+teleport,
  poll never neared the 30 s deadline) after two full-30 s flat-zero polls plus
  a minutes-later G3 resolution of the same template. Report records only the
  final npcObjId (no per-poll samples), so intermittent-non-zero vs flat-zero
  within a poll cannot be distinguished read-only; the cross-run contrast
  (0-after-30 s ×2, then 44183-fast) is the evidence. Q0 boundary CLOSED;
  dotless-×10 charPos oddity persists (`145421,111391,1111`) — still open, still
  not verdict-affecting.

- DIRECTOR COMPLETE NOTE 2026-09-19: Phase 1 marked COMPLETE on verified exit
  criteria — (1) wakeSeq 1→92 lane-proven (2) position truth closed + fixture
  mismatch identified (3) BotBehaviorRuntime sole lifecycle owner
  (4) QuestBehavior executes through it, builds green (5) shared-selector
  ordering preserved, unit parity 14/3 (6) GameplayActor tails untouched
  (7) /api/bots/brain + inspector unit-proven 7/7 (8) Q0 PASS run 2 + G1/G2
  green same binary (9) G3 FAIL/DISCOVERY per-bot + HARNESS-UNKNOWN discipline
  (10) scope verified, no second systems. Q0 re-proof: run 2 PASS
  (enter-world 142.1 s objId=22011, enroll 1.3 s, fixture 9.0 s
  npcObjId=44183, accept 0.3 s active=True); run 1 HARNESS-FAIL on wrong env
  (default ports hit 127.0.0.1:3306 conflict; .env self-restored via run 2
  EnsureUp with E2E_DB_PORT=3311 — caution: always prefix the full lane env:
  E2E_ROOT plus all seven E2E_*_PORT vars plus COMPOSE_PROJECT_NAME).
  Spawner verdict: TRANSIENT AVAILABILITY, not registry/data — with honest
  caveat that the report records final objId only (no per-poll samples), so
  intermittent-non-zero vs flat-zero within a poll is read-only
  indistinguishable; evidence is the cross-run contrast.
