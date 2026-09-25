# Nearby-Quest Contracts — QuestBehavior / Perception / Nav / Fixture / Violations

CODE-ONLY audit, repo `/root/aaemu-dev`, branch `develop`, 2026-09-19. Nothing modified, built, or run.
Read-first context: `questbehavior-extraction.md` (seam (b) shared selector), `brain-wake-ownership.md`
(§1 wake trace edges 17–26, §3 roam map), `playerbot-brain-roadmap.md` (§1 position truth),
`g3-diagnosis-arbitration.md` (funnel F0–F10). Locked: scheduler untouched, GOAP untouched, no second
actor, engine legality in `GameplayActor`. Canonical seam: `BotActionCommandQueue → GameplayActor → engine`
(autonomy legs bypass the queue by design and call the same actor verbs).

---

## 1. QuestBehavior state machine (live flow, exact)

### 1.1 Entry — caller gate + runtime Tick

1. Wake gate (executor): quest leg runs only when `!handledByParty && !pvpEngaged` and the arbiter-held
   activity starts with `"quest."` and the actor is idle — `BotRoamStepExecutor.cs:672-679`. Otherwise the
   pending runtime (if any) is marked superseded via `Cancel("quest activity not held")` (`:680-686`,
   observation-only).
2. `StepQuestLeg(bot, actor, state, questActivity)` (`BotRoamStepExecutor.cs:1203-1231`): returns `false`
   unless `actor is GameplayActor` (`:1205-1206` — non-concrete actors silently skip); provider is
   `NearbyNpcProvider ?? DefaultNearbyNpcs` (`:1208-1209`); per-bot runtime from `_questRuntimes`
   (`:1210-1211`); options carry ONLY `CycleId = quest-{characterId}-{ticks}` (`:1213-1216`, so band
   `[1,9]` / cap 3 / priorities are all defaults — `QuestDecisionScenario.cs:31-52`); `runtime.Tick(...)`
   (`:1212-1217`). Landed → debug log (`:1219-1223`); anything else → `ArmQuestTravel` fallback (`:1224-1229`).
3. `BotBehaviorRuntime.Tick` (`BotBehaviorRuntime.cs:102-154`): actor-busy at entry → `Waiting` /
   `"waiting-actor-busy"`, return false (`:113-119`, defense-in-depth — caller already gates on idle).
   Else records `CurrentActivity`/`BehaviorInstanceId=CycleId`/`StartedAt`, `Running`/`"quest-leg"`
   (`:121-126`), calls `QuestBehavior.Run` (`:128`), then maps: landed
   (`WorkSelected && Request.State == Completed`) → `Completed`, phase = action name (`:132-140`);
   `!WorkSelected && FailStage == "DECIDE"` → `Blocked` / `"no-legal-work"`, NOT an error (`:142-148`);
   everything else → `Failed`, phase = lowercased fail stage (`:150-153`). `NextWake` is always null
   (purely reactive leg). `Cancel(reason)` moves `Running`/`Waiting` → `Cancelled`, drops the request
   pointer, never touches the actor (`:162-170`).

### 1.2 PERCEIVE — `QuestBehavior.cs:56-58`

- `BotObservedContext.Capture(actor)` #1 (`QuestBehavior.cs:57`) → `actor.Observe()`
  (`GameplayActor.cs:222-288`); `before = ActiveQuestIds` snapshot (`:58`).
- Consumed from the context: **only** `Position` (assigned to a dead local, see §1.7) and `ActiveQuestIds`.
  `NearbyNpcObjIds` (populated by `Observe()` at `GameplayActor.cs:263`) is **never consumed** — the sweep
  calls the provider directly with `(actor.Character, radius)` (`QuestBehavior.cs:82`).

### 1.3 DECIDE — `QuestBehavior.cs:60-127`

Order per wake (NPC-vs-quest-first answer: **quest-state first, NPC-sweep second, priority decides**):

1. **Active-quest proposals first** (`:62-75`): `before.Order()`; a `Ready`/`Completed` quest gets NO advance
   proposal (`:70-72` — `RunCurrentStep` from `Ready` is a no-op that still reports `Completed`, comment
   `:64-68`); all other active quests get one `AdvanceProposal` each (`:72`) plus `TurnInProposals` (`:73`).
2. **Turn-in resolution** (`:195-229`): only for `Status == Ready` (`:199`); template via
   `QuestManager.Instance.GetTemplate` (`:201`); `Ready`-component acts scanned (`:204-205`); NPC reporter →
   `world.GetNpcByTemplateId(reportNpc.NpcId)` (`:210`, **world-wide, unspecified order, no range check at
   proposal time**); doodad reporter → `GetAllDoodads().FirstOrDefault(template match)` (`:218`, same
   caveats); neither → `AutoTurnInQuest` with target 0 (`:226-228`). Reporter missing → `yield break`
   (silent, no tally).
3. **Discovery sweep** (`:78-90`): `nearbyNpcs(actor.Character, GameplayActor.MaxQuestDiscoverRange)` (`:82`)
   where `MaxQuestDiscoverRange = MaxInteractRange = 25f` (`GameplayActor.cs:1196,1432`); **first-3-take in
   enumeration order, no distance sort** (`QuestBehavior.cs:86-88`); null NPCs skipped. Note the comment
   (`:76-77`) claims "nearest in-range NPCs" — the code does not sort (§5 flag).
4. **Discover dispatch** (`:95-112`): `actor.DiscoverQuests(targetObjId, idempotencyKey)` per target
   (terminal-synchronous; non-`Completed` → silent `continue`, `:99-100`); offerings filtered by band
   (`:105-106`) and already-active (`:108-109`); `inBand` is tally-only. Engine legality (`IsDiscoverable`,
   `GameplayActor.cs:1407-1425`) runs **inside** `DiscoverQuests` (`:1253-1255`), so the behavior's
   eligibility = engine gate + band + not-active.
5. **Accept proposals** sorted `OrderBy(Level).ThenBy(QuestId)` (`:113-116`); priority
   `AcceptPriority + max(0, BandMax - Level)` (`:267`), competing in ONE `Select` against turn-in (30) and
   advance (20) (`QuestOptions`, `QuestDecisionScenario.cs:49-51`) — seam (b) preserved.
6. **Select** (`:118-127`): fresh `Capture` #2 as `decideContext` (`:118`); `BotDecisionSelector.Select`
   (legality → priority → clamped personality → ordinal tie-break → index,
   `BotDecisionProposal.cs:235-291`; >64 candidates → no-proposal, `:233,246-248`). No-proposal →
   `Fail("DECIDE", WrongDecision, ...)` with funnel tallies
   `[swept=… targets=… offerings=… inBand=… legal=… firstZero=sweep-empty|no-offerings|all-filtered]` (`:124-126`).
- **Giver identification: none.** There is no template→giver matching, no "find the giver of quest X" step;
  discovery is NPC-first (whoever enumerates first) and quest-identity only enters via offering filters.
  A staged correct giver is found only if it survives the first-3 enumeration cut within 25 m.

### 1.4 EXECUTE — `QuestBehavior.cs:129-145`

`SetPendingDecision(goal, PolicyVersion, rejections+1, rejections, CycleId)` (`:131-132`) +
`SetPendingCycleId(CycleId)` (`:136`) → `BotDecisionCycle.Execute` (`:137-138`), which re-`Select`s the
single proposal (legality recheck, `BotDecisionProposal.cs:322-324`), dispatches (`:326`), and on terminal
captures terminal observation + postcondition (`:330-332`). Non-terminal request → `Fail("EXECUTE",
Starvation, ...)` (`QuestBehavior.cs:140-145`). Dispatch map (`:276-291`): `AdvanceQuest`, `AcceptQuest`
(with `(offering, targetObjId)` payload → `AcceptQuest(id, AcceptorType, AcceptorId)`), `TurnInQuest`,
`TurnInDoodad`, `AutoTurnIn` — **and nothing else** (no `Talk`/`InteractNpc`/`MoveTo`, §1.5).

### 1.5 Talk path — ABSENT

- `AdvanceQuest` = `quest.RunCurrentStep()` only (`GameplayActor.cs:863-879`), completing unconditionally
  with `"advanced (step/status)"` — no observable-delta check. Talk-objective quests can never credit through
  this leg: `Talk` (`GameplayActor.cs:977-1078`, real `DoTalkMadeEvents` path `:1029-1030`, void-refusal when
  nothing credits `:1066-1069`) and `InteractNpc` (`:1080-1183`) are never dispatched by `Dispatch`.
- Accept path needs no talk (correct — accept is the `CharacterQuests.AddQuest` gate via
  `GameplayActor.cs:819-861`, which also drains early Start→Supply→Progress steps `:843-852`).
- Consequence: any active quest whose progress step needs a talk event stalls forever under this behavior —
  `AdvanceQuest` will keep "landing" `Completed` requests with zero world progress (the Ready-no-op trap of
  `:64-68`, generalized).

### 1.6 Range assumptions / movement requests / arrival

- Range: 25 m flat sweep radius (`:82`) + 25 m flat dispatch re-check inside `DiscoverQuests`
  (`GameplayActor.cs:1220-1222`); `Observe()` neighborhood lists also 25 m (`:262-264`). All flat (2D) via
  `MathUtil.CalculateDistance(..., false)`.
- Movement requests from `QuestBehavior`: **zero** — no `MoveTo`/`NavigateTo`/`ArmQuestTravel` reference in
  the file. Travel is the caller's `ArmQuestTravel` (`BotRoamStepExecutor.cs:1240-1263`): never re-arms over
  a live route (`:1243`), target = unspawned Ready-reporter spawner first (`ResolveQuestTravelTarget`
  `:1271-1296`), else nearest in-band offerer spawner world-wide when nothing is active (`:1303-1324`),
  armed as `BotPath.PathTo(target)` (`:1258`) and walked by the route pump (§3).
- Arrival determination in the behavior: **none** (no movement, no distance-to-giver check anywhere).

### 1.7 Placeholders / missing pieces

1. Tautological postconditions: advance (`QuestBehavior.cs:173-175`, `_ => true`) and turn-in (`:238-240`,
   `_ => true`) — `BotDecisionCycle` postcondition evidence is vacuous for both; only accept asserts
   (`ActiveQuestIds.Contains`, `:261-263`).
2. Dead local: `var position = context.Position` (`:81`) — never used (sweep passes `actor.Character`).
3. Silent drops with no tally: turn-in reporter-missing `yield break` (`:212`, `:220`); per-target discover
   non-terminal (`:99-100`); non-`QuestDiscoveryResult` (`:101-102`). Only sweep/offering/band counts are tallied.
4. Missing: talk dispatch (§1.5), navigation intent (§3), giver targeting, arrival check, retry (replan-next-wake
   only — correct per policy, but combined with §1.5 means talk quests spin `Completed` advances forever).
5. `TurnInProposal` sets `targetId: questId` (`:237`) while the runtime objId rides the payload
   (`QuestTurnInParams`, `:244,247`); `Dispatch` uses the payload (`:284-289`) — consistent with `TurnIn`'s
   `(questId, action, targetObjId, ...)` signature (`GameplayActor.cs:896-901`), so not a placeholder, but the
   dual-carry is fragile (see §5).

---

## 2. Perception contract

### 2.1 Which named observations exist? NONE of the five

`QuestAvailable`, `GiverVisible`, `GiverReachable`, `WithinTalkRange`, `QuestEligible` exist **nowhere as
such** (grep over `AAEmu.Game/Core/Managers/Bots` — no hits). The quest slice decides off raw fields plus
inline filters:

| Concept | Where it actually lives |
|---|---|
| QuestAvailable (approx) | `offerings` list post-band/post-active filter, `QuestBehavior.cs:103-111` (ephemeral local) |
| QuestEligible (approx) | `IsDiscoverable` inside `DiscoverQuests`, `GameplayActor.cs:1407-1425` (engine gate, not an observation) |
| GiverVisible / GiverReachable / WithinTalkRange | **do not exist** — no per-giver distance/visibility value is ever computed in the quest path |

Raw world data available per `Capture`: `ActorObservation` (`ActorObservation.cs:22-86`) — position,
current target, HP/MP, nearby character/NPC/doodad objId lists (25 m region graph,
`GameplayActor.cs:262-264`), active quest ids (`:265`), money/bank/labor/bags/pack (`:266-272`), party
(`:273-278`) — copied 1:1 into `BotObservedContext` (`BotDecisionProposal.cs:16-110`). The quest slice
consumes `Position` (dead var) + `ActiveQuestIds` only. `BotWorldStateProvider` (GOAP) is quest-blind: its
only NPC reference is the hostility check (`BotWorldStateProvider.cs:206-207`).

### 2.2 Audit findings

1. **Duplicate queries (worst gap).** One `Run` performs **three** `Capture → Observe()` calls
   (`QuestBehavior.cs:57,118,147`), each running **three** `GetAround` 25 m scans
   (`GameplayActor.cs:262-264`) and emitting an `Observe` audit row (`:283-287`), **plus** the provider sweep
   (region `GetAround<Npc>` again via `DefaultNearbyNpcs`, `BotRoamStepExecutor.cs:2064-2071`) **plus** one
   `GetNpc`/`GetDoodad` resolve per swept target inside `DiscoverQuests` (`GameplayActor.cs:1210-1211`).
   Same NPC neighborhood re-queried up to ~5× per wake; 3 audit rows per quest wake are pure spam.
   (`decideContext` re-capture is nearly free of new information — discovery is terminal-synchronous, so
   positions/quests barely move between `:57` and `:118`.)
2. **Populated-but-unconsumed observations.** `NearbyNpcObjIds`/`NearbyDoodadObjIds` are scanned, snapshotted,
   copied (`BotDecisionProposal.cs:79-81`), and then ignored — the sweep re-scans via the provider. Either the
   context list or the provider call is redundant work.
3. **Over-claiming comment as synthetic assumption.** "nearest in-range NPCs" (`QuestBehavior.cs:76`) over a
   first-3 enumeration cut (`:86-88`) with region insertion-order iteration (`Region` swap-remove/array order
   per g3-diagnosis §5) — proximity is *assumed* where only enumeration order exists. Not fabricated data, but
   a fabricated guarantee. Same family: turn-in reporter taken `FirstOrDefault` world-wide (`:210`, `:218`)
   with no distance information attached.
4. **No stale caches, no non-authoritative sources on the read path.** All reads are live (`Character`,
   `WorldManager`, `QuestManager`, `SpawnManager`); `BotObservedContext` is immutable-once-returned
   (`ActorObservation.cs:11-12`). Injectable seams exist (`NearbyNpcProvider`,
   `BotRoamStepExecutor.cs:125`, test-only) but production default is the region graph. `DefaultNearbyNpcs`
   region-null fallback to world-registry scan (`:2067-2071`) is claimed never-live (comment `:2060-2063`) —
   UNKNOWN verified.
5. **No decision logic inside `Observe()`.** `Observe()` is a pure read + record (`GameplayActor.cs:222-288`).
   Legality lives where it belongs: proposal preconditions (selector) + engine gates (dispatch). No violation.

### 2.3 Minimum corrections (PROPOSAL ONLY — not implemented)

1. Collapse to two captures: keep perceive (`:57`); drop `decideContext` (`:118`) and reuse the perceive
   context for `Select`+`Execute` (discovery is terminal-sync; the post-dispatch terminal capture inside
   `BotDecisionCycle.Execute`, `BotDecisionProposal.cs:330`, already gives the fresh post-state — reuse
   `execution.TerminalObservation` instead of capture #3 at `QuestBehavior.cs:147`).
2. Single sweep: resolve sweep candidates from ONE scan — either consume `context.NearbyNpcObjIds` (resolving
   objIds → units once) or stop populating NPC/doodad lists in `Observe()` for the quest path; do not run both.
3. Distance-sort the sweep (nearest-first before the take-3 cut) and fix the `:76` comment to describe the
   actual policy; attach per-target drop tallies (not-found / out-of-range / zero-offerings) to the existing
   DECIDE detail string (no schema change — the g3-diagnosis §6.1 inlet).
4. Add `GiverReachable`/`WithinTalkRange` as **pure functions** over `(Character position, target position)`
   evaluated at proposal time — never stored flags, never caches (per wake-ownership §2 conflict 5: no
   cross-behavior blackboard without TTL).
5. Range-check the turn-in reporter at proposal build (skip + tally when beyond 25 m), or verify the dispatch
   gate first: `TurnIn`'s range discipline is UNKNOWN (not re-read this pass) — cite before changing.

---

## 3. Nav contract

### 3.1 What QuestBehavior requests today: NOTHING

No `MoveTo`/`MoveToUnit`/`Navigate`/`NavigateTo` call exists in `QuestBehavior.cs`. The only quest-side
movement is executor-owned: `ArmQuestTravel` arms `BotPath.PathTo(spawnerPos)`
(`BotRoamStepExecutor.cs:1258`); the route pump issues **`actor.MoveTo(target, RoamSpeed, RoamLegTimeout)`**
(`:986`, `RoamSpeed`/`RoamLegTimeout` values UNKNOWN — not re-grounded), advances past rejected points
(`:988-994`), pumps `actor.Tick(elapsed)` (`:1005`), and flat-arrival `Stop`s (`:1007-1016`). The quest leg
suppresses route issue while it lands work (`!state.QuestLegActive` gate, `:981`).

### 3.2 Available machinery (all `GameplayActor.cs`)

- `MoveTo(destination, speed, timeout)` (`:312-330`): validates (positive speed, finite destination), `StartMove`.
- `NavigateTo(destination, ...)` (`:332-341`) → `NavigateToInternal` (`:358-412`): already-there → `Completed`
  (`:366-371`); GeoData navmesh `FindPath` when available (`:374-394`); obstacle-detour fallback
  (`:397-409`); silent fallthrough to straight `StartMove` when neither applies (`:411`).
- `NavigateToUnit(objId, ...)` (`:343-356`), `MoveToUnit(objId, ...)` (`:482-495`, snapshot-position straight leg).
- `Tick` convergence (`:4152-4241`): flat + Z within `ArrivalRadius = 0.5f` (`:75,4189`) → waypoint advance
  (`:4201-4209`) or `BroadcastStop` + `Complete("arrived")` (`:4211-4215`); corner blending (`:4175-4187`);
  trapezoid profiling (`:4224-4239`); application through `VehicleMovementModel` (`:4319-4320`, same family as
  the client path).
- Stuck: `NoProgressWindow = 2.5s` (`:116`), `MaxUnstickNudges = 1` (`:137`), one lateral recovery leg, then
  `Expire(Navigation, "stuck: no progress …s")` (`:4409-4410`).
- Budget: `DefaultMoveTimeout = 30s` (`:78`); expiry → `Expire(Navigation, "navigation budget exceeded")`
  (`:4073-4074`). Validation rejects use `RejectedAction`; busy uses `StateTransition` (`TryBegin`).

### 3.3 Verdict: machinery YES, orchestration NO

- **Enough machinery? YES.** `NavigateTo(target)` → `Running` → terminal `Completed("arrived"` /
  `"already at destination")` vs `RejectedAction` (bad target/speed, missing unit) vs
  `Expired(Navigation)` (stuck / budget) is a complete `Running → Arrived/Failed(reason)` primitive today.
- **Missing thin orchestration (proposal only):**
  1. No intent channel: the behavior cannot ask for movement (it returns bool + writes nothing navigable;
     `QuestTravelTarget/Reason` are executor-internal, `:1253-1256`). Proposal: behavior returns a navigation
     intent `{destination | targetObjId, arrivalRadius, purpose}`; the route layer owns verb choice + `Path`.
  2. Route pump uses straight `MoveTo`, never `NavigateTo` (`:986`) — navmesh/detour machinery is dead code on
     the quest path. Proposal: route layer calls `NavigateTo`/`NavigateToUnit` for travel legs.
  3. Arrival semantics mismatch: travel to a spawner completes at 0.5 m 3D, but quest purposes need "within
     25 m talk range" (or reporter-spawned). Proposal: per-intent arrival radius (25 m for discover staging,
     tight for reporter approach).
  4. No distinct UNREACHABLE terminal: navmesh-miss silently degrades to a straight leg (`:411`) that later
     expires as stuck/budget — misleading for diagnostics. Proposal: `Reject(Navigation, "no path")` when
     GeoData mode is on and `FindPath` yields nothing (fail-fast), keep straight-leg fallback otherwise.
  5. Ownership boundary (proposal): behavior owns *where/why* (intent); route layer owns `Path`/`PendingLeg`,
     verb choice, re-arm discipline (already its discipline, `:1242-1243`); actor owns leg lifecycle +
     terminal states. Terminal vocabulary: `Arrived | AlreadyThere | Rejected-target | Stuck(Navigation) |
     Budget(Navigation) | Preempted` — all expressible with today's `ActorLifecycleState` + `ActorFailureReason`
     + `Detail` strings, no new enums.

---

## 4. Correct fixture

### 4.1 Quest 251's giver is NPC 3512 (verified three ways, code/data)

- Unit-rig header (data-verified): "Quest 251 … accept from NPC 3512, gather item 4058 x3, report to NPC
  3512, reward item 18791 x1" (`AAEmu.UnitTests/.../GameplayActorQuestActionsTests.cs:20-22`); constant
  `AcceptorNpcTemplateId = 3512` (`:37`); rig sets `Character.Level = 2` with the comment "quest 251 is level
  2; the real gate evaluates" (`:65`) — level-2 acceptance at 3512 passes the REAL engine gate in the rig.
- Harness doc: "quest 251's report NPC (act id 104, npc 3512)" (`AAEmu.IntegrationTests/E2e/E2eStack.cs:761-764`).
- Linkage rule: offers = Start components carrying `QuestActConAcceptNpc.NpcId == template`
  (`QuestManager.cs:2101-2110`; act shape `QuestActConAcceptNpc.cs:6-8`). Roadmap's "Start component 383
  carries ConAcceptNpc 3512" is consistent but component-id UNKNOWN re-verified (DB-gated).
- Q0/G3's "giver 2425" is therefore a **fixture bug**, not an engine bug: explicit-accept at 2425 can PASS
  only if the engine gate is as permissive as alleged (Q0 PASS run 2 accepted 251 at 2425 — roadmap §5),
  while discovery can NEVER offer 251 at 2425.

### 4.2 Spawner / materializability (game data, read directly)

- NPC 2425 "Refugee Rhiania" (`Creatures.xml:2111`): ONE main_world spawner at
  `(14536.6, 11117.107, Z 110.49)` (`npc_spawns.json:131891-131897`). Materializable (Q0 PASS/fast,
  G1/G2 staging; two 30 s flat-zero polls on other boots = transient availability, roadmap §5).
- NPC 3512 "Mayor Gott" (`Creatures.xml:3051`): ONE main_world spawner at
  `(15655.91, 15172.51, Z 121.24)` (`npc_spawns.json:184418-184424`). Spawner EXISTS (single entry —
  no multi-spawner ambiguity). **Live lane materialization at 3512: UNKNOWN** (no recorded lane run has
  staged/teleported to 3512; `teleportToNpc` resolves by template so it should behave like 2425, but that is
  inference — do not cite as proven).
- NPC 3511 (giver of 6198) at `(15524.4, 15278.43)` (`:184410-184415`); NPC 3597 (giver of 330) at
  `(15562.45, 15354.32)` (`:185037-185043`).

### 4.3 What offers(2425)={532,533,534} need

- Quest 532 (code-cited, G2 header `G2ActorTalkCapabilityTests.cs:23-30`): `ConAcceptNpc(333, npc 2425)` →
  `ObjTalk(242, npc 2426)` → `ConReportNpc(398, npc 2425)`; Start component 2096 reqs **Level ≥ 26 + faction
  (MotherFaction 148/NuiaAlliance)**; fixture `setLevel 30` on a Nuian bot. Giver 2425 spawner + talk NPC 2426
  spawner `(14448.84, 11045.226)` (`npc_spawns.json:131900-131905`) are adjacent (~130 m).
- Quests 533/534 individually: UNKNOWN (no per-quest rep read this pass; roadmap claims all three L30/zone-14
  — treat as prior finding, not re-verified).
- Consequence: at the G3 fixture's level 10, offers(2425) are band- AND level-gated out twice over — and at
  level 30 they are still band-gated ([1,9] default). **No level makes 2425 yield an in-band offer.**

### 4.4 Low-level in-range pairs near spawn (all in the 3511/3512/3597 Solzreed cluster)

- Quest 330 via NPC 3597, level 1, in band (roadmap §1.2; `QuestPerceptionLoopE2eTests.cs:24` adds: report at
  3511, reward-gated by requirement `(36, 6198, 0)` — chain caveat for turn-in, accept unaffected).
- Quest 6198 via NPC 3511, report at 3512, reward item 18792, objective-free (`QuestPerceptionLoopE2eTests.cs:22-23`).
- Geometry: 3511→3512 ≈ 169 m; 3597→3512 ≈ 204 m; 2425→3512 ≈ 4.6 km (different starter areas — no
  single staging point serves both).

### 4.5 Recommendation (DO NOT RUN — code-only audit)

**Autonomy gate fixture: quest 251 + giver NPC 3512 (Mayor Gott) + character level 2**, fresh account,
`teleportToNpc(3512)` staging at spawner `(15655.91, 15172.51)`, default band `[1,9]`, default cap 3.
Rationale: 251's level-2 gate is rig-proven at 3512 with real engine gates; accept AND report both resolve
to 3512, so the same fixture scales from accept-gate to the full accept→advance→turn-in spine (M1M2-replay
precedent); single spawner removes placement ambiguity. Pre-run Devonshire: poll `npcObjId(3512)` to nonzero
(30 s, same SETUP/npc-unresolved boundary as Q0/G3) — live materialization at 3512 is the one UNKNOWN this
fixture retires. Fallback gate if 251's objective chain proves heavy: quest 330 via 3597 at level 1.

---

## 5. Violations

| # | Item | Verdict + cite |
|---|---|---|
| 1 | Direct quest mutation | **ALL-CLEAR.** `QuestBehavior.cs` never writes `Character.Quests`: reads only (`:58` snapshot, `:70` status check, `:199` status check, `GetTemplate` `:201`). All mutation via actor verbs (`Dispatch`, `:276-291`); step-drain loops live inside the actor (`GameplayActor.cs:843-852` accept drain, `:1037-1045`/`:1144-1152` talk drains). |
| 2 | Duplicated movement policy | **ALL-CLEAR** in `QuestBehavior` (zero movement calls). Pre-existing note (executor-side, untouched): route pump issues straight `MoveTo`, not `NavigateTo` (`BotRoamStepExecutor.cs:986`) — navmesh/detour bypassed on the quest-travel path. |
| 3 | Synthetic perception | **ALL-CLEAR on fabrication** (no invented positions/flags; absent stays absent). **FLAG (over-claim):** "nearest in-range NPCs" comment (`QuestBehavior.cs:76`) over an unsorted first-3 cut (`:86-88`); dead `position` local (`:81`) suggests a distance computation that was never written. |
| 4 | Wrong interaction primitive | **FLAG.** No `Talk`/`InteractNpc` in `Dispatch` (`QuestBehavior.cs:276-291`); `AdvanceQuest` (`RunCurrentStep` only, `GameplayActor.cs:863-879`) completes unconditionally with no delta check, so talk-objective quests spin landed-but-progressless wakes forever. Correct primitives exist (`Talk` `:977-1078` with void-refusal `:1066-1069`; `InteractNpc` `:1080-1183`) but are unwired. Turn-in reporter resolved world-wide with no proposal-time range check (`QuestBehavior.cs:210,218`; `TurnIn` dispatch gate UNKNOWN — not re-read). |
| 5 | Placeholder target IDs | **ALL-CLEAR** (AutoTurnIn `0` is the legitimate auto sentinel per `GameplayActor.cs:888,896-901`; turn-in `targetId=questId` + objId-in-payload matches `TurnIn`'s signature). **FLAG (adjacent):** tautological postconditions `_ => true` for advance (`:173-175`) and turn-in (`:238-240`) — terminal evidence vacuous. |
| 6 | Unbounded retry loops | **ALL-CLEAR.** No retry in the behavior (replan-next-wake only); engine-side drains bounded (`guard++ < 4` accept, `GameplayActor.cs:847`; `< 8` talk drains, `:1040,1147`); selector candidate bound 64 (`BotDecisionProposal.cs:233`). |
