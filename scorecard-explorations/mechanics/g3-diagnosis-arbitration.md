# G3 Diagnosis — Arbitration Inputs, Proposal Funnel, Travel-vs-Proposal, Determinism, Trace Quality

CODE-ONLY diagnosis, branch `develop` @ 2026-09-19. No file modified, nothing built or run.
`compact.sqlite3` is 0 bytes (empty — `stat` 2026-09-19), so every DATA-level fact
(quest 251's starter linkage, template level, unit_reqs, supply items, repeatability,
spawner positions) is marked UNKNOWN. Nothing is inferred; test-header claims
(quest 251 LEVEL 2, giver NPC 2425) are cited to the test file, not to game data.
Locked evidence (do NOT merge): Run A = `quest.progress` held + travel reason
"walking to quest target at (15562,15354) from (15578,15382)", accepts=0,
questActionCount=0. Run B = questLegActive=false, reason empty.

## 1. ARBITRATION INPUTS

Entry: `BotGoalArbiterStepExecutor.StepAsync` runs one `Arbitrate(bot, gameHour)` pass per
wake, then delegates to the inner step
(`AAEmu.Game/Core/Managers/Bots/BotGoalArbiterStepExecutor.cs:31-43`).
`Arbitrate` inputs: the module snapshot (registration order + `Priority`), per-bot
`_activeActivity` memory, and one `BotActivityContext{Bot, GameHour, ActiveActivity}` per wake
(`AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs:128-155`;
`AAEmu.Game/Core/Managers/Bots/IBotActivityModule.cs:50-60`).
First `Allow` in priority order wins; else `NoCandidate`, world untouched
(`BotGoalArbiter.cs:157-218,230`). Ties break by registration order via stable
`OrderByDescending(Priority)` (`BotGoalArbiter.cs:240-244`). Production order/priorities
(`AAEmu.Game/Program.cs:290-322`): Schedules 100, Recovery 85, ConflictJoin 75,
FishingContest 60, Homestead 60, QuestBootstrap 58, NeedsFarm 55, PresenceRoam 50, Idle 0.
GameHour defaults to `BotActivityGameClock.Hour` (`BotGoalArbiterStepExecutor.cs:22-28`).
Contract: `CanActivate` must be cheap and side-effect free; only `Activate` on activity
change applies visible behavior (`IBotActivityModule.cs:30-44`).

| Input | Source (live read at wake) | UsedByArbitration (which CanActivate reads it) |
|---|---|---|
| `AAEMU_QUEST_BOOTSTRAP_ENABLED` env gate | `QuestBootstrapModuleOptions.FromEnvironment` (`QuestBootstrapActivityModule.cs:21-25`; wired `Program.cs:310`) | QuestBootstrap only: deny at `:68-69` when off |
| `BotScheduleOptions.Enabled` + authoritative `BotScheduleService.Options.Enabled` | Options/services (`SchedulePhaseActivityModule.cs:80-88`) | Schedules only |
| `Bot.Character.IsDead` (bool) | Character record | Recovery (`OutOfCombatRecoveryModule.cs:447-449`), ConflictJoin (`ConflictJoinActivityModule.cs:55-56`), Homestead (`HomesteadActivityModule.cs:43-44`), QuestBootstrap (`QuestBootstrapActivityModule.cs:72-73`), NeedsFarm (`NeedsFarmActivityModule.cs:119-120`). NOT read by PresenceRoam, Idle, FishingContest, Schedules |
| `Bot.Character.IsInBattle` (bool) | Character record | ALL modules except none: Schedules (`:90-91`), Recovery (`:451`), ConflictJoin denies only indirectly (no battle check; HP/combat checks `:58-62`), FishingContest (`:77-78`), Homestead (`:46-47`), QuestBootstrap (`:75-76`), NeedsFarm (`:122-123`), PresenceRoam (`:48-49`), Idle (`:31-32`) |
| `ServerPressure` vs High (world-budget observance) | `_pressureProbe`, default Healthy | Recovery? NO probe read in CanActivate body shown except sit handling — UNKNOWN for Recovery pressure; FishingContest (`:80-81`), Homestead (`:49-50`), QuestBootstrap (`:78-79`), NeedsFarm (`:125-126`). NOT read by Schedules, ConflictJoin, PresenceRoam, Idle |
| `Character.Quests.ActiveQuests.Count` | Live quest map | QuestBootstrap (`:84`, quest-work branch) — the ONLY module reading quest state |
| `Character.Money` vs `GoldTarget=1000` | Live `Character.Money`; default 1000 (`QuestBootstrapActivityModule.cs:17-18`) | QuestBootstrap (`:84-87`, quest-less branch only). NeedsFarm reads Money too but against its OWN `GoldTarget` default 1000 (`NeedsFarmActivityModule.cs:21-22`; snapshot `:134-148`) — separate option instance, separate urgency math. NOT read by PresenceRoam/Idle |
| `LaborPower`, bag counts, food/lumber targets, urgency threshold | Live inventory + options (`NeedsFarmActivityModule.cs:134-150`) | NeedsFarm only |
| HP/MP fractions | Live `Hp/MaxHp/Mp/MaxMp` | Recovery triggers `<0.75 HP \|\| <0.60 MP`, exits `>=0.95` (`OutOfCombatRecoveryModule.cs:97-116,122-132`; CanActivate `:459-480`); ConflictJoin denies HP fraction `<0.7` (`ConflictJoinActivityModule.cs:33,58-59`). QuestBootstrap/NeedsFarm/PresenceRoam/Idle read NO fractions (only dead/battle booleans) |
| `Transform.ZoneId` + conflict registry | Live transform; registry source UNKNOWN (not in audited files) | ConflictJoin only (`ConflictJoinActivityModule.cs:64-67`) |
| Equipment weapon slots / learned-skills count | Live inventory/skills (`ConflictJoinActivityModule.cs:91-103`) | ConflictJoin only (`IsCombatReady`, `:61-62`) |
| `Personality` metadata | `PlayerBotMetadata` (`ConflictJoinActivityModule.cs:69-72`) | ConflictJoin only (reluctant set denies; eager set is documentation-only — no allow-boost in code) |
| Contest window `_utcNow` vs `OpensAt/ClosesAt` | Clock + options (`FishingContestActivityModule.cs:83-85`) | FishingContest only |
| Housing completion scan | `HousingManager` live (`HomesteadActivityModule.cs:53-57`) | Homestead only |
| `GameHour` | `BotActivityGameClock.Hour` via executor ctor | Schedules Travel branch (`SchedulePhaseActivityModule.cs:122-126`); carried opaquely in context for the rest |
| `ActiveActivity` (arbiter memory) | `_activeActivity` per characterId (`BotGoalArbiter.cs:87-88,149-155`) | Recovery re-entry (`OutOfCombatRecoveryModule.cs:459-471`); equality short-circuit Unchanged (`BotGoalArbiter.cs:190-191`) |
| Skill cooldowns / GCD / mana costs / buffs / stance (except sit) | — | NOT an input to ANY module CanActivate (no `Cooldowns`/`SkillManager` reference in any module body read; combat-tree reads at decision time per senses audit §1 are NOT on the arbitration path) |
| Zone (except ConflictJoin), position, needs, quests | — | NOT read by QuestBootstrap / NeedsFarm / PresenceRoam / Idle (bodies `:64-90`, `:111-153`, `:41-52`, `:27-35` contain no such read) |
| Randomness | — | NOT an input: no `System.Random`/`Shuffle` in `BotGoalArbiter*.cs`, `*ActivityModule.cs`, `QuestDecisionScenario.cs`, `BotDecisionProposal.cs` (grep 2026-09-19; only unrelated hits: `AuctionHouseScenario` nonce, `RumorStore` ids) |

## 2. GOLDTARGET VERDICT — CONTRIBUTES (conditionally decisive, never random)

Exact role, `QuestBootstrapActivityModule.cs:71-87`:
- `:84` `activeCount = character.Quests?.ActiveQuests.Count ?? 0`.
- `:85-87`: `if (activeCount == 0 && character.Money >= _options.GoldTarget) Deny("no active quests and copper … at or above …")`.
- `:89`: otherwise `Allow("quest.progress")`.

So copper is a veto ONLY on the quest-less branch; with ≥1 active quest the bot
arbitrates into `quest.progress` regardless of wealth (`activeCount==0` short-circuits —
`Money` is not even evaluated when quests are active). No randomness: deterministic `>=`
on two integers; `GoldTarget` is ALWAYS 1000 in production wiring because
`FromEnvironment` sets only `Enabled` (`:21-25`) and the default is 1000 (`:18`;
wired `Program.cs:310`). NeedsFarm's 1000 is a different field on a different options
record (`NeedsFarmActivityModule.cs:21-22`; evaluator default
`BotNeedsEvaluator.cs:36`) — same number, no shared state.

Fresh-account money: the creation default is UNKNOWN (not found in audited files), but
the G3 fixture's drive ops (`setLevel`, `teleportToNpc`, `npcObjId`, `charPos`) mutate
no money (`AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:789-835`), and the fresh-row
wipe deletes characters/quest/item rows (`:4507-4528` doc). Barring engine copper events
— of which none occur pre-START, and Run A shows zero quest-action rows — two fresh
accounts take the SAME money branch. Money therefore CONTRIBUTES to the gate but does
NOT differentiate Run A from Run B; their divergence must come from other inputs
(pressure/battle/schedule/recovery/conflict state) or from post-arbitration perception
(§4), never from copper randomness — there is none.

## 3. PROPOSAL FUNNEL — quest 251 / giver 2425

Leg gating first (`BotRoamStepExecutor.cs:661-668`): no party, no PvP,
`ActiveActivity` starts with `"quest."`, actor idle — else `StepQuestLeg` never runs.
Inside (`:1184-1209`): `nearbyNpcs = NearbyNpcProvider ?? DefaultNearbyNpcs` (`:1189-1190`),
`QuestDecisionScenario.Run(concreteActor, nearbyNpcs, …)` with ONLY `CycleId` overridden
(`:1191-1195`) — so band is default `[BandMin 1, BandMax 9]`
(`QuestDecisionScenario.cs:52-56`), `MaxDiscoverTargets = 3` (`:59`).

| # | Predicate (exact) | Code | Count / fate for 251/2425 |
|---|---|---|---|
| F0 | ActiveQuests empty → no advance/turn-in proposals | `QuestDecisionScenario.cs:105-118` (fresh account: `before` empty) | 0 proposals from this stage (both runs) |
| F1 | Sweep input: `nearbyNpcs(character, MaxQuestDiscoverRange=25f)` | `:122-126`; `MaxQuestDiscoverRange = MaxInteractRange = 25f` (`GameplayActor.cs:1187,1423`); default provider = region `GetAround<Npc>` (`BotRoamStepExecutor.cs:2040-2048`); Region null → EMPTY (`WorldManager.cs:1242-1243`) | UNKNOWN count (world state). Fixture's `npcObjId` poll is WORLD-WIDE `GetNpcByTemplateId` (`BotDriveBridge.cs:816-820`) — proves existence, NOT 25 m perceivability |
| F2 | Cap: first `MaxDiscoverTargets=3` NPCs in ENUMERATION order, no distance sort | `QuestDecisionScenario.cs:125-132` | Giver dropped here if ≥3 other NPCs enumerate first (region array is insertion-ordered, §5) |
| F3 | Per target `DiscoverQuests(objId)`: `GetNpc(objId)` else `GetDoodad` else Reject-not-found | `GameplayActor.cs:1201-1205` | objId-identity gate (template lookup succeeds but objId stale → drop) |
| F4 | 25 m flat range re-check at dispatch | `GameplayActor.cs:1211-1213` | Drop if giver materialized beyond 25 m (teleport lands on SPWNER coords, `BotDriveBridge.cs:789-805`; spawn offset is data-UNKNOWN) |
| F5 | Linkage: `GetQuestsOfferedByNpc(TemplateId)` = Start components carrying `QuestActConAcceptNpc.NpcId == 2425` (+ kill channel) | `GameplayActor.cs:1222-1235`; `QuestManager.cs:2108-2110,2153-2172` | UNKNOWN whether 251 ∈ offers(2425) — DB data; `compact.sqlite3` empty |
| F6 | `IsDiscoverable(251)`: template known; not active; `CanAcceptSupplyItems`; ALL Start `unit_reqs` pass (level/race/chain); not (completed && !Repeatable) | `GameplayActor.cs:1398-1416` | UNKNOWN — all data (level-10 `setLevel` is real, `BotDriveBridge.cs:833-835`, but 251's reqs are data) |
| F7 | Scenario band `offering.Level ∈ [1,9]`; exclude already-active | `QuestDecisionScenario.cs:147-150` | 251 LEVEL 2 passes IF template level data matches (test header `G3AutonomousQuestAcceptTests.cs:33-37`; data itself UNKNOWN) |
| F8 | `AcceptProposal` hard precondition `quest-not-active` + selector legality-before-preference | `QuestDecisionScenario.cs:308-312`; `BotDecisionProposal.cs:252-281` | Passes on fresh account |
| F9 | Deterministic select: Priority (accept = 10 + (9−level)) → PersonalityWeight (0 default, clamped) → TieBreakKey Ordinal → Index | `QuestDecisionScenario.cs:304-306`; `BotDecisionProposal.cs:149,165,178,196,283-288` | Deterministic GIVEN candidates |
| F10 | Dispatch `AcceptQuest` → `QuestController.AcceptQuest` → `CharacterQuests.AddQuest(251, false, acceptorType, acceptorId)` engine gate | `GameplayActor.cs:810-851`; `PlayerBotController.cs:40-41` | Engine truth; `AddQuest` internals UNKNOWN (outside audited files) |

Code-determinable funnel counts: 3 (NPC sweep cap), [1,9] (band), 25 m (range),
64 (`BotDecisionSelector.MaxCandidates`, `:233` — unreachable here). Everything else is
world/data state.

EXACT DROP PREDICATE (Run A, grounded): the sweep input F1/F2, NOT F5–F10. Proof:
Run A has `questActionCount == 0` (locked evidence + test branch
`G3AutonomousQuestAcceptTests.cs:214-235`). ANY `DiscoverQuests` call that reaches
`Reject`/`Complete` emits an audit row via `Finish`
(`GameplayActor.cs:4653-4656,4672-4685`) and the bridge counts ALL quest-action rows
(`BotDriveBridge.cs:4427-4470`) — so zero rows means `DiscoverQuests` was NEVER invoked
(actor was idle by gating `:665`, ruling out the silent `TryBegin`-busy drop at
`GameplayActor.cs:1196-1197`), i.e. `discoverTargets` was EMPTY at
`QuestDecisionScenario.cs:137`. The giver's runtime objId never entered the funnel even
though its template resolved world-wide at fixture time — discovery-linkage
(template→offers, F5) vs direct-accept fixture (template→objId world-wide,
`WorldInstance.cs:476-479`) operate on different identity universes (§4). Which F1
sub-cause (beyond-25 m spawn offset vs Region-null window vs not-yet-spawned at wake
instant) is UNKNOWN from trace alone — that is the §6 gap. (Run B is a different
branch: reason EMPTY means `ArmQuestTravel` never ran — it always writes reason
(`BotRoamStepExecutor.cs:1228,1232-1234`) — so `StepQuestLeg` never ran, i.e. the
arbiter never yielded `quest.*` that wake. NOT merged with Run A.)

## 4. TRAVEL-VS-PROPOSAL — different identity, different surface, different gates

Arm path (`BotRoamStepExecutor.cs:1218-1241`): only when the scenario landed nothing
(`:1202-1207`), never re-arms over a live route (`:1221-1222`), writes
`QuestTravelTarget`/`QuestTravelReason` (`:254-265` fields; `:1231-1234` format parsed by
`G3AutonomousQuestAcceptTests.cs:380-398`).

| Aspect | Proposals (discovery→accept) | Travel (`ArmQuestTravel`) |
|---|---|---|
| Target identity | RUNTIME objId (`discoverTargets`, `QuestDecisionScenario.cs:129`), resolved per wake via `GetNpc(objId)` | TEMPLATE id (`spawner.UnitId`) + spawner POSITION (`BotRoamStepExecutor.cs:1286-1300,1331-1340`) |
| Observation surface | `BotObservedContext.Capture` (up to 3×/wake, `:100,159,181`) for position/quests + provider region scan for NPCs (provider called directly, `:125` — the context's `NearbyNpcObjIds` list itself is NOT consumed) | `SpawnManager` registry + `Character.Quests` + `Transform` position — NEVER touches `ActorObservation`/`BotObservedContext` (`:1249-1305`) |
| Range gate | 25 m TWICE (sweep radius F1 + dispatch re-check F4) | NONE — world-wide spawner scan, nearest-wins by `Vector3.Distance` (`:1283-1301`) |
| Legality gate | `IsDiscoverable` per quest per swept NPC (F6) | `OffersWalkableQuest`: SAME `IsDiscoverable` + SAME default band [1,9] (`:1317-1328`) but applied to spawner TEMPLATES, excluding nothing by distance |
| Failure visibility | Silent `continue` (`:141-144,147-150`) → `Fail("DECIDE", WrongDecision, "no legal quest proposal")` (`:161-165`); no per-target/per-offering reason persisted | Always writes a reason (`:1228` vs `:1232-1234`) |

Coexistence mechanism: travel reads the registry the proposals cannot see. Run A's
reason decodes to ≈32.2 m bot→target (dx −16, dy −28 → √1040), i.e. BEYOND the 25 m
discovery horizon — the bot walks toward a spawner whose offers are template-legal
(`OffersWalkableQuest` passed for some spawner) but whose runtime NPCs are outside the
perceivable radius, so the sweep is empty and zero proposals is the CORRECT deterministic
output of F1. Whether the target IS 2425's spawner is UNKNOWN (spawner data not
verified), but the geometry is consistent with it. Travel intent + zero proposals is
therefore not a contradiction: it is the designed no-legal-work fallback
(`:1204-1206`) firing on disjoint inputs.

## 5. ORDERING / RANDOMNESS — deterministic selector over accidentally-ordered sweep

Deterministic (byte-stable given identical inputs): module sort
(`BotGoalArbiter.cs:240-244`); first-Allow-wins (`:157-218`); `before.Order()` (`:105`);
`questIds.Order()` (`GameplayActor.cs:1243`); offerings `OrderBy(Level).ThenBy(QuestId)`
(`QuestDecisionScenario.cs:154-156`); selector Priority→Weight→TieBreakKey→Index
(`BotDecisionProposal.cs:283-288`); travel branch-1 `OrderBy(questId)`
(`BotRoamStepExecutor.cs:1259`); snapshot `OrderBy(questId)`
(`BotQuestLoopObservation.cs:72-74`).

NOT deterministic / accidental:
- Sweep membership: `Region._objects` is an insertion-ordered array with swap-remove on
  despawn (`AAEmu.Game/Models/Game/World/Region.cs:94-109`); `GetList` iterates it
  unsorted under lock (`:432-455,457-479`); cross-boundary radii fan out over
  `GetNeighbors()` order (`WorldManager.cs:1248-1256`). The first-3-take
  (`QuestDecisionScenario.cs:125-132`) has NO distance sort, so WHICH NPCs are swept is
  spawn-history luck. This is the accident inlet for the whole funnel.
- `WorldInstance._npcs` is a `ConcurrentDictionary` (`WorldInstance.cs:155`):
  `GetNpcByTemplateId.FirstOrDefault` (`:476-479`) and `GetAllNpcs` (`:637-640`)
  enumerate in unspecified order (fixture/turn-in side only, not the sweep).
- `GetQuestsOfferedByAct` iterates the act-index dictionary `Values` with
  `Contains`-dedupe (`QuestManager.cs:2157-2170`) — load-order-dependent, but harmless:
  both consumers re-sort (`GameplayActor.cs:1243`; `QuestDecisionScenario.cs:154-156`).
- Travel branch-2 scans `SpawnManager` dictionary `Values`
  (`SpawnManager.cs:1190-1215`) via `SelectMany` — order-unspecified, but outcome is
  nearest-wins so order matters ONLY on exact distance ties (first-seen wins, accidental
  tie-break, `BotRoamStepExecutor.cs:1294-1299`).
- Timestamps-as-inputs: `CycleId = quest-{id}-{UtcTicks}` (`BotRoamStepExecutor.cs:1194`)
  → idempotency keys + `DecisionSeed`/join key (`QuestDecisionScenario.cs:169-174`);
  `TraceId = Guid.NewGuid()` + `RequestedAtUtc/StartedAtUtc/CompletedAtUtc`
  (`ActorRequest.cs:127,135,148,157,224`). Identity/correlation ONLY — the selector
  never reads them; they cannot change WHO wins.

VERDICT: deterministic story with an accidental inlet. Post-sweep (F3–F10) is fully
determined; Run A's zero is determined given the empty sweep; but sweep membership rides
unspecified enumeration order + live spawn timing/positions, so the SAME wake one second
later (NPC spawned/moved 3 m closer) can flip the outcome. The A/B divergence needs no
randomness to explain — and none exists on this path.

## 6. TRACE QUALITY — what DecisionCycleId + arbiter stamping now answer, and the gaps

Reconstructible TODAY (post prereq Parts 2–3): arbiter outcome is staged as
goal=activity / policy=`module:{Name}` / candidates=module count / rejections=decline
count / seed=null onto the next request (`BotGoalArbiterStepExecutor.cs:57-74`),
consumed once by `NewRequest` (`GameplayActor.cs:4578-4587`) and sealed into the audit
record with `decision_cycle_id` (`:4682-4685`; `ActorAuditRecord.cs:62-67,107-114`).
The scenario re-stages goal/policy/candidates/rejections/cycle-id before dispatch
(`QuestDecisionScenario.cs:168-174`); queue specs carry it too
(`BotActionCommandQueue.cs:150-151,459-460,512,841-842,879-880,1010-1011`).
`QuestRunResult` carries SelectedAction/Request/Rejections/Explanation/TraceRecords
(`QuestDecisionScenario.cs:68-86`); the bridge exposes leg flag, travel reason/target,
quest-action rows and last action/result/detail (`BotDriveBridge.cs:4440-4485`); the
projection exposes Money/XYZ/ZoneId/active quests (`BotQuestLoopObservation.cs:35-107`,
flag noted executor-owned `:46-47`). So: wake → (arbiter counts on first request) →
selection (goal/policy/cycle-id on the dispatched row) → terminal row joined by
`decision_cycle_id` — YES for the dispatch path, COUNTS-ONLY for arbitration.

Still missing (minimal instrumentation PROPOSAL ONLY — not implemented):
1. **Sweep I/O is unrecorded.** `discoverTargets` objIds, per-target `DiscoverQuests`
   outcome (never-swept vs not-found vs out-of-range vs zero-offerings), and per-offering
   drop reason (band vs already-active vs which `IsDiscoverable` sub-gate) exist only as
   silent `continue`s. This is EXACTLY the Run-A ambiguity (F1 sub-cause UNKNOWN).
   Proposal: extend the existing `Fail("DECIDE", …)` explanation string (already plumbed
   to `QuestRunResult.FailReason` + bridge `lastDetail`) with swept-target count +
   first reject detail + drop tallies by reason, and add the swept count to the bridge
   observe payload. No schema change, no new framework.
2. **Per-module `WhyNot` has no record column** (stays in transition log; deferred by
   prereq Part 2). Run-B-class ambiguity (WHY did the arbiter yield roam/idle/None)
   needs either this column or log-joined reasons.
3. **`DecisionSeed` is null on the arbiter stamp** (no seed exists at that layer,
   `BotGoalArbiterStepExecutor.cs:47,71-73`); the scenario seed is a timestamp-derived
   `CycleId`, unique but not a seed. Cross-referencing counts-vs-identity is manual.
4. **Stamp lands on the wrong row first.** The staged arbiter context is consumed by the
   leg's FIRST request, usually perception `Observe()` (prereq Parts doc notes this) —
   so goal attribution on Observe rows means "wake ran under X", not "action chosen
   because of X". Joinable via cycle id, but misleading at a glance.
5. **No wake id joins scheduler metrics to audit rows** (`TotalStepsRun` before/after in
   bridge `:4418,4444-4445` cannot join to `decision_cycle_id`, which only the quest leg
   mints; queue/API path mints none per wake). `BotObservedContext` itself is
   in-memory-only, never persisted beside the decision (insert into record = schema
   change, deferred).
