# G8 Return Ownership — §§F–J (READ-ONLY audit, develop @ 2026-09-20)

READ-ONLY. Nothing modified, built, or run. G3–G7d frozen — untouched (cited, never edited).
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
G8 mission: Ready → return → Talk/interact → TurnInQuest → reward; **planning only**.
Read-first context: `g5-pursuit-semantics.md` (route ownership, stuck, local boundary);
`brain-boundaries.md` (§3 nav contract); `g6-closeout-freeze.md` (contract + debt).
`UNKNOWN` = not found in code, nothing inferred. `[INFERENCE]` = derived, marked as such.

Incoming state (G7d end, assumed): quest 251 `QuestStatus.Ready` (= 3,
`AAEmu.Game/Models/Game/Quests/Static/QuestStatus.cs:3-12`), 4058×3 held/consumed,
bot away from giver NPC template 3512
(`BaragiVillageNpcPosition (15320.1, 15024.4, 128.5)`,
`AAEmu.Game/Core/Managers/Bots/SolzreedStarterCorridorProfile.cs:30-31`;
giver+reporter identity 3512: `M1M2ReplayScenario.cs:94-95`, `g4-objective-credit.md:32`).

---

## F. RETURN LOGIC — classification (§5)

### F.1 The one production path (PRODUCTION)

Ready → reporter-resolved → turn-in already exists in production autonomy:

1. `QuestBehavior.TurnInProposals` (`AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:598-632`):
   Ready-gated (`:602`), template Ready-component scan (`:607-608`),
   `QuestActConReportNpc` → `GetNpcByTemplateId(reportNpc.NpcId)` per wake, never stored
   (`:611-617`); doodad → `TurnInAtDoodad` (`:619-626`); neither → `AutoTurnInQuest`
   (`:627-631`). For 251 the Ready act is `QuestActConReportNpc` npc 3512
   (comp 638 / act 457 / detail 104 — `g4-objective-credit.md:32`).
2. Priority: `TurnInPriority = 30`, the top of the quest ladder
   (`QuestDecisionScenario.cs:49-53`; target 25 > pursuit 24 > combat 23 > loot 22 >
   advance 20 > accept 10, `:49-73`).
3. Ready-suppresses-advance rule (`QuestBehavior.cs:307-317`): a Ready quest contributes
   NO `AdvanceProposal` (a Ready `RunCurrentStep` is a Completed no-op that starved the
   route layer — observed live: 60 "advances", 0 turn-ins, bot never moved).
4. Dispatch arms (`QuestBehavior.cs:1081-1086`): `TurnInQuest` → 
   `gameplayActor.TurnInQuest(TargetId, turnIn.TargetObjId, …)`; Doodad / AutoTrip
   likewise. `QuestTurnInParams(uint TargetObjId, int SelectedReward)` (`ActorRequest.cs:280`).
5. `GameplayActor.TurnIn` (`GameplayActor.cs:970-1030`): quest-active preflight
   (`:977-980`), target-resolve preflight (`:982-990`), real packet path
   (`PlayerBotController.ReportTurnIn/ReportDoodadTurnIn/AutoTurnIn`,
   `PlayerBotController.cs:126-143` → `QuestManagerEvents.DoReportEvents`,
   `QuestManagerEvents.cs:22-70`), bounded `RunCurrentStep` drain ≤ 8 (`:1014-1019`),
   terminal = `HasQuestCompleted` + `questcredit` ledger marker (`:1021-1029`).
6. Selection/execution plumbing: `BotDecisionSelector.Select` over proposals (`:467`),
   `SetPendingDecision` + `SetPendingCycleId` join keys (`:505-510`),
   `BotDecisionCycle.Execute → Dispatch` (`:511-512`), post-wake completed-set diff
   (`:550-562`), RUN-catch → `Fail("RUN", FidelityError)` (`:564-568`).

### F.2 Learn-to-return movement (PRODUCTION, partial)

- `BotRoamStepExecutor.ResolveQuestTravelTarget` (`BotRoamStepExecutor.cs:1393-1454`):
  case 1 walks to the **spawner** of a Ready quest's report NPC **only when the reporter
  is NOT spawned** (`:1406-1423`); spawned reporter → "turn-in will land next wake"
  (`:1419-1420`), i.e. NO travel. Case 2 walks to offerer spawners when nothing active.
- `ArmQuestTravel` (`:1325-1350`): single-leg `BotPath.PathTo`, owner `QUEST_TRAVEL`
  (`:1346`), never re-arms a live route (`:1328-1329`); `SupersedeQuestTravelRoute` on
  pursuit dispatch (`:1298-1302`).
- Verdict: production owns **unspawned-reporter spawner walks only**. A spawned-but-distant
  reporter (the G8 START) has NO production return leg — the turn-in is expected to land
  next wake **remotely** (see §I.1).

### F.3 Talk vs InteractNpc (PRODUCTION, asymmetric)

| Verb | Range gate | Talk-credit | Void behavior | Cite |
|---|---|---|---|---|
| `Talk` | `> MaxInteractRange (25 f)` → Reject | `DoTalkMadeEvents` per active quest with a talk-family objective (`QuestActObjTalk`/`QuestActObjTalkNpcGroup`) matching the NPC template | **zero delta → `RejectedAction` "produced no quest change"** | `GameplayActor.cs:1051-1152` (gate `:1067-1068`, fan-out `:1091-1105`, void-reject `:1140-1143`) |
| `InteractNpc` | same 25 f gate | same fan-out | **never void: falls back to `dialogue with npc …` and `Complete`s** | `:1154-1257` (gate `:1166-1167`, fan-out `:1203-1216`, fallback `:1245-1246`, Complete `:1256`) |

251 carries NO talk-family objective (Progress = `QuestActObjItemGather` 4058×3, Ready =
`QuestActConReportNpc` 3512 — `g4-objective-credit.md:31-33`). [INFERENCE] Talk on 3512
with only 251 active matches nothing (`:1094-1104`) → void → Rejected. **G8's
interaction leg must be `InteractNpc`, not `Talk`** — Talk is unusable for 251's return.

Engine turn-in needs NO Talk at all: `QuestActConReportNpc.RunAct` only checks
`CurrentTarget is Npc && TemplateId == NpcId` (`QuestActConReportNpc.cs:21-26`), and
`OnReportNpc` credits on `NpcId == args.NpcId` + `isReady` (`:40-61`); `DoReportEvents`
carries no proximity check (`QuestManagerEvents.cs:22-70` — resolve-or-silent-return only).

### F.4 REUSABLE (scenario library — copy the shape, never the code)

- `LevelingLoopScenario.TurnIn` (`LevelingLoopScenario.cs:3008-3104`): data-driven reporter
  resolution (Ready-component `ConReportNpc`/`ConReportDoodad` acts, `:3011-3014`),
  out-of-bubble `TravelToReporter` (bounded `MaxTurnInTravelLegs = 8` re-sweep legs,
  `:2951-2982`, `:418`), 3 m close-in `NavigateToUnit` before `TurnInQuest` (`:3047-3059`),
  doodad branch (`:3061-3082`), `AutoTurnInQuest` fallback (`:3083-3086`), completion
  assertion via `HasQuestCompleted` (`:3094-3098`). Travel pace `6 f` m/s, 90 s budget
  (`:408-410`).
- `AdventurerSpikeScenario` RETURN leg (`AdventurerSpikeScenario.cs:640-703`): the full
  return shape in one place — `MoveToUnit(acceptor)` → `AcceptQuest` →
  `MoveToUnit(reportNpc)` → `TurnInQuest` → bounded `AdvanceQuest` drain (≤ 8, `:693-702`).
  Precedent for G8b→G8c chaining.
- `M1M2ReplayScenario` 251 row (`M1M2ReplayScenario.cs:94-95`): accept 3512, preseed
  4058×3, report 3512, reward 18791×1; reward-conserved criterion (`:182-193`) and
  `m1-quest-251-completed` criterion (flag set + not active, `:326-330`).

### F.5 SCENARIO-ONLY (never production)

- `BotScenarioRunner` ReportNpc/ReportJournal events (`BotScenarioRunner.cs:586-611`):
  world-adapter resolution + actor turn-in, exception-on-refusal — rig-only driver.
- `BotScenarioTemplates` (`BotScenarioTemplates.cs:73-75, :126-128`), `DevMapperService`
  replay talk (`DevMapperService.cs:388-392`).

### F.6 DUPLICATE (consolidate on touch, not now)

1. Talk-credit fan-out is near-identical in `Talk` (`:1091-1105` + drain `:1111-1119`) and
   `InteractNpc` (`:1203-1216` + drain `:1218-1226`) — same match predicate, same events,
   different void behavior. G8 must not add a third copy.
2. Reporter resolution exists in three shapes: `GetNpcByTemplateId` (QuestBehavior `:613`),
   perception-map lookup + travel (LevelingLoop `:3032-3044`), world-adapter resolve
   (spike `:642`, `:666`; runner `:587`). One engine path underneath (`DoReportEvents`).

### F.7 UNKNOWN

- No production return-navigation owner for a spawned-and-distant reporter (§F.2).
- `Dispatch` (QuestBehavior `:1073-1111`) has arms for Advance/Accept/TurnIn(×3)/
  Target/pursuit-Move/pursuit-Stop/combat-AutoAttack/loot-Loot — **no `Talk`,
  `InteractNpc`, or return-Move arm**. [INFERENCE] A Talk/interact/return proposal
  through this switch hits C# switch-expression exhaustion → throws → RUN FidelityError
  (`:564-568`). Any G8 proposal needs a new goal-guarded arm (G5/G6 precedent `:1097`,
  `:1104`).
- 3512 leash/despawn rows: 3512 is a static recruiter, not wildlife — leash N/A by kind;
  spawn-table presence is assumed from the spawner-walk code (`:1421`, `:1480-1488`),
  spawner row for 3512 not re-verified this pass → UNKNOWN.
- Busy-vs-leg arbitration for a return leg under the quest runtime: `TryBegin`
  busy → `Rejected(StateTransition, "actor busy …")` (`GameplayActor.cs:4750-4756`);
  the pursuit preempt-first pattern (`QuestBehavior.cs:1121-1122`) is the template.

---

## G. OWNERSHIP (§8)

Derived from the codebase's own splits (G5 §G: actor owns the leg, caller owns the
intent; brain-boundaries §3: actor keeps one-leg stepping + stuck detection, travel
orchestration lives brain/behavior-side):

| Slice | Owner | Why (cite) |
|---|---|---|
| WHY: Ready detection, reporter resolution, proposal + priority 30 | `QuestBehavior` | `TurnInProposals` + Ready-suppresses-advance (`QuestBehavior.cs:598-632`, `:307-317`); priorities (`QuestDecisionScenario.cs:49-73`) |
| HOW (travel): return Move leg, per-wake revalidation, drift retrack, abandon | `QuestBehavior` (new return proposal, PursuitGoal-shaped) | G5 precedent: Move/Stop proposals + `DispatchPursuitMove` preempt-first + drift record (`QuestBehavior.cs:739-868`, `:1119-1140`); brain owns re-resolve/repath, actor keeps stepping (brain-boundaries §3.3) |
| LEGALITY: resolve, range, packet path, drain, terminal | `GameplayActor` | `TurnIn` preflights + `DoReportEvents` + drain + `HasQuestCompleted` (`GameplayActor.cs:970-1030`); `InteractNpc` 25 f gate + dialogue fallback (`:1154-1257`) |
| Route fallback (unspawned reporter → spawner walk) | `BotRoamStepExecutor` (untouched) | `ResolveQuestTravelTarget`/`ArmQuestTravel` (`BotRoamStepExecutor.cs:1325-1454`) |
| Reward math + cleanup + history flag | engine (`Quest.DistributeRewards`, quest state) | pools → bag/mail, level-based exp/copper, cleanup consume (`Quest.cs:304-413`); completed flag + ActiveQuests drop (M1M2 criterion `:326-330`) |

Explicit non-owners: GOAP (unwired, brain-boundaries §1), queue (no new kinds —
`BotActionKind.Talk`/`TurnInQuest`/`AutoTurnIn` already dispatch queue-side,
`BotActionCommandQueue.cs:607-633`, `:905-908`), `Talk` verb for 251's interaction
(§F.3 — void-rejects; `InteractNpc` is the interaction owner).

---

## H. DECOMPOSITION (§11) — verdict: three gates (G8a / G8b / G8c), not four, not one

**Recommend G8a (Ready → reporter resolved) / G8b (return to range + dialogue) /
G8c (TurnInQuest → completed + reward).** G8d (removed + reward) is folded into G8c:
the engine performs report → Ready → Reward → completed-drop → `DistributeRewards` in
ONE step-machine pass (`GameplayActor.cs:1006-1029`; `Quest.cs:304-413`), so a separate
removal/reward gate would assert mid-pass engine internals with no dispatch boundary
between them. The four-way split (Talk-dispatch / TurnIn-dispatch / removed / reward)
collapses on that atomicity; the Talk half collapses on §F.3 (Talk void-rejects on 251
— there is no Talk dispatch to gate).

- G8a asserts the WHY without moving: 251 Ready + `TurnInProposals` yields exactly one
  `TurnInQuest` proposal (target 251, `TargetObjId` = live 3512 objId, `SelectedReward`
  −1) while the bot stands > 25 m out. Observe-only friendly (no dispatch needed).
- G8b asserts the HOW: return leg → flat ≤ 25 m → `InteractNpc` Completed with the
  dialogue detail. Fails while travel fails; never turns in (withhold control, §I.1).
- G8c asserts legality + completion: `TurnInQuest` Completed + `HasQuestCompleted(251)`
  + not-active + reward deltas (§I.4).

Single-gate variant (rejected except as post-pass sweep): Ready-away → completed+reward
in one run. Same objection as G7 §H: one `Starvation`/zero-delta cannot distinguish
unresolved reporter vs stalled travel vs dialogue reject vs credit failure. Acceptable
only as a final integration sweep after G8a–c pass, no new claims attached.

---

## I. GATE DESIGN + E2E PROOF SKETCH (§§12–14)

### I.1 START state + the remote-turn-in staging problem

START (all asserted from a pre-gate snapshot, observe-only):
251 active with `Status == Ready` (`QuestStatus.cs:8`); reporter live
(`ParentWorld.GetNpcByTemplateId(3512)` non-null — the `QuestBehavior.cs:613` predicate);
flat bot→reporter distance > 25 m (outside `MaxInteractRange`, `GameplayActor.cs:1535`);
bag 18791 = 0, 4058 = 3 (pre-cleanup); copper/XP baseline; `HasQuestCompleted(251) ==
false`; actor idle (no live request — `TryBegin` `:4750-4756`).

**The staging problem:** `TurnIn` carries **no range gate** (verified absence across
`GameplayActor.cs:970-1004` — resolve preflight only, no `CalculateDistance`; engine
`DoReportEvents` likewise proximity-free, `QuestManagerEvents.cs:22-70`). With
`TurnInPriority = 30` the first quest wake after staging dispatches a remote turn-in
and the fixture (Ready + away + no turn-in yet) self-destructs — the exact failure the
Ready-suppresses-advance comment records for Advance (`QuestBehavior.cs:307-311`), now
in turn-in form. Staging G8a/G8b therefore REQUIRES a withhold-turn-in control
(harness proposal filter or priority-0 override — mechanism UNKNOWN, no existing flag
found; `QuestOptions` exposes priorities as init-values, `QuestDecisionScenario.cs:31-74`,
so a `TurnInPriority = 0` staging override is the cheapest honest shape [INFERENCE]).
Without it, G8a degrades to a single-wake race the gate cannot win. This is G8's
first-zero FAIL family ( §I.3).

### I.2 Observe-only rules (all three gates)

No dispatch except the gate's named verb (G8b: Move/InteractNpc; G8c: TurnInQuest).
Per wake record: `ActiveQuestIds`, 251 `Step`/`Status`, reporter objId + template 3512
+ flat/3-D distances (G5 §E.3: arrival proves coordinates, so pin template + alive +
nearest alongside), movement owner tag (`MoveOwner` — `PURSUIT_MOVE_TO_UNIT` precedent,
`QuestBehavior.cs:1126-1127`; return leg needs its own owner, e.g. `RETURN_MOVE_TO_UNIT`
[INFERENCE — proposed, not present]), proposal `goal/action/targetId`, request
`State/Detail/Failure`, audit `TraceId` + `CycleId` join (`QuestBehavior.cs:505-510`).
Cadence: G5/G6 settled wakes (hold-confirm shape, g6-closeout-freeze §b).

### I.3 Evidence + PASS / first-zero FAILs

| Gate | Evidence (all joined by CycleId→TraceId) | PASS |
|---|---|---|
| G8a resolved | reporter ObjId (template 3512 via `GetNpcByTemplateId`), flat distance > 25 m, single `quest.turn-in`/`TurnInQuest` proposal present, zero dispatches | proposal exists with `TargetObjId ==` live reporter objId, `SelectedReward == -1`, quest untouched (still Ready/active) |
| G8b return | per-wake flat distance series, `MoveToUnit(reporter)` terminals, `Interrupted` records if retracked (G5 §G.2), final `InteractNpc` Completed detail naming template 3512 | flat ≤ 25 m + `InteractNpc` Completed (dialogue detail), quest still Ready/active, `turnIns == 0`, reward deltas 0 |
| G8c turn-in | `TurnInQuest` Completed detail (`GameplayActor.cs:1027`), post-wake `HasQuestCompleted(251) == true` + not in `ActiveQuests` (M1M2 `:326-330`), reward deltas (§I.4), `questcredit` ledger marker (`:1026`) | Completed terminal + flag set + inactive + 18791 +1 (bag or mail — `Quest.cs:311-322` fallback) |

First-zero FAILs (fail-closed, named): `reporter-unresolved` (proposal absent while Ready —
spawner-walk fallback armed, `BotRoamStepExecutor.cs:1406-1423`); `remote-turn-in`
(turn-in dispatched while flat > 25 m — withhold control failed); `talk-void`
(`Talk` dispatched instead of `InteractNpc` → Rejected no-quest-change, §F.3);
`dispatch-throw` (proposal with no `Dispatch` arm → RUN FidelityError, §F.7);
`busy-reject` (`StateTransition actor busy`, `:4755`); `still-active`
(turn-in Completed but quest active — drain incomplete, `:1015-1019`);
`reward-missing` (completed but 18791 delta 0 in bag AND mail); `bag-full-mailed`
(reward went to mail — pass-with-note, not fail, `Quest.cs:311-322`).

### I.4 REWARDS (§14) — authoritative evidence for 251

- Fixed item: **18791 ×1** via `QuestActSupplyItem` act 36911 / detail 4075, Reward comp
  639 (`g4-objective-credit.md:33`; rig header + route spec `M1M2ReplayScenario.cs:20-22, :94-95`;
  `Golden-Route-Solzreed.md` step 2 per `g7-human-trace-251.md:24-25`). Mechanism:
  act `RunAct` → `QuestRewardItemsPool` (`QuestActSupplyItem.cs:26-48`) → bag
  `AcquireDefaultItem` or quest-reward mail when full (`Quest.cs:325-341`).
- Level-based exp/copper: `DistributeRewards` adds `quest_supplies` row for
  `Template.Level` when `Step == Reward` (`Quest.cs:343-383`), XP via `AddExp` (`:386-392`),
  copper via `ChangeMoney` (`:394-401`). **Exact copper/XP for level-2 251: UNKNOWN** —
  `quest_supplies` rows not read this pass and `compact.sqlite3` is absent from
  `AAEmu.Game/Data/` (dir lists only Bots/json/xml/Navigation/Worlds). `SelectedReward`
  −1 default is correct (251's reward is non-selective; spec `Selected = 0`,
  `M1M2ReplayScenario.cs:94-95`; selective-item acts key on `SelectedRewardIndex`,
  `QuestActSupplySelectiveItem.cs:20-24`).
- Removal: 4058×3 `Cleanup = t, DestroyWhenDrop = t` (`g4-objective-credit.md:46-47`)
  → `QuestCleanupItemsPool` → `ConsumeItem(QuestComplete)` (`Quest.cs:403-410`).
- History: completion drops 251 from `ActiveQuests` (engine terminal behavior,
  `GameplayActor.cs:1006-1029`) + `HasQuestCompleted(251)` flag (criterion `:326-330`).
- Minimum clean completion proof: `TurnInQuest` Completed + `HasQuestCompleted(251)` +
  inactive + bag/mail 18791 0→1 + bag 4058 3→0. Copper/XP deltas recorded but NOT gated
  until the `quest_supplies` row is read.

---

## J. RISKS + HUMAN TRACE (§§15–16)

### J.1 G8-specific unknowns/risks

1. **Remote turn-in (staging killer, §I.1).** No range gate on `TurnIn` or
   `DoReportEvents`; priority 30 fires from anywhere. Withhold control is REQUIRED and
   currently UNKNOWN-shaped. Do not stage G8a/G8b without it.
2. **`Talk` is a trap for 251 (§F.3).** Void-rejects; use `InteractNpc`. A gate that
   names "Talk" literally will fail closed on the void-reject — file under policy
   wording, not engine defect.
3. **Missing `Dispatch` arms (§F.7).** Return-Move / `InteractNpc` / `Talk` proposals need
   new goal-guarded arms (G5/G6 precedent); until then they throw → RUN FidelityError.
4. **Busy-reject vs the return leg.** `TryBegin` single-writer (`:4750-4756`); the quest
   leg only runs while the actor is idle (G5 running-leg contract,
   `QuestBehavior.cs:519-544`) — a live roam/quest-travel leg busy-rejects the return
   dispatch unless preempted first (pursuit pattern `:1121-1122`).
5. **Recovery preemption.** `OutOfCombatRecoveryModule` priority 85
   (`OutOfCombatRecoveryModule.cs:77-78`) may own post-G7-damage wakes while HP is low;
   G6 debt already flags it (`g6-closeout-freeze.md:136`). Return gates should refill
   vitals at staging (G7 decomposition fixture precedent, `g7-decomposition.md:40-43)
   and treat a recovery-owned wake as setup noise, never a return failure.
6. **Reporter spawn stability.** Spawned-at-staging ≠ spawned-at-turn-in: the spawner-walk
   fallback only arms when NO legal work exists (`BotRoamStepExecutor.cs:1307-1311`) —
   but a Ready quest WITH a produced turn-in proposal always has legal work, so a
   mid-gate reporter despawn reads as `turn-in target … not found in world`
   (`GameplayActor.cs:987-989`), not as travel. Name it `reporter-despawned`, distinct
   from navigation failure. Spawner row for 3512 unverified → UNKNOWN.
7. **Reward-mail fallback.** Full bag routes 18791 to mail (`Quest.cs:311-322`); the gate
   must check mail before failing `reward-missing`. Related: `SelectedReward` semantics
   (1-based per `ActorRequest.cs:279`) vs spec `Selected = 0` — harmless for 251's
   non-selective reward, but the gate must pass −1/0 exactly as the proposal does.
8. **No leash/despawn/recovery data for the return path.** 3475 leash/DPS caps
   (`g6-closeout-freeze.md:130-131`) do not apply to a 3512 walk; 3512-specific rows not
   found → all three UNKNOWN for the giver, recorded not assumed.

### J.2 HUMAN TRACE (§16) — verdict: no 251 Ready→return sequence exists in-repo,
but a genuine quest-agnostic human turn-in DOES (corrects g7-human-trace-251 §1)

No file contains a human accept → … → Ready → travel → interact → Talk → complete →
  reward sequence for 251 (all `251` hits outside code/docs are `elapsed_ms` false
  positives; trace schema carries no quest/item/NPC-template IDs, so no record ties to
  251/3512/4058/3475 — `g7-human-trace-251.md:22-28, :32-36`). That half of the g7 verdict
  stands.
CORRECTION to `g7-human-trace-251.md:41-45` ("full-file grep finds zero records"):
  re-grepped this pass, `quest_accept_and_turnin__Dingus__20260914_194206.jsonl` (22,690
  records, ≈157 s) DOES contain a human turn-in at the tail — `packet_in`
  `CSCompleteQuestContextPacket` 0x0D6 at elapsed_ms 154131 (~3 s before `trace_stopped`
  at 157316), preceded by `SCOneUnitMovementPacket` movement and followed same-tick by
  `SCQuestContextUpdatedPacket` 0x149 + `SCItemTaskSuccessPacket` 0x090 +
  `SCExpChangedPacket` 0x0FE (+ `SCErrorMsgPacket` 0x10A, detail not decoded this pass),
  then `SCQuestContextCompletedPacket` 0x14B at +92 ms. So the human turn-in order is:
  **move → `CSCompleteQuestContextPacket` → Updated + ItemTaskSuccess + ExpChanged →
  Completed**. The `packet-coverage.csv` 1× claim was correct; the g7 zero-hit grep was
  the stale artifact (likely a pattern-form mismatch), not the CSV.
Accept-side order (file head, `g7-human-trace-251.md:52-70` re-confirmed):
  `SCTargetChanged` → `CSChangeTargetPacket` (target picked first) → `CSInteractNPCPacket`
  0x065 (dialogue opened) → ~2.4 s dwell → `SCQuestContextStartedPacket` 0x147 +
  `SCQuestContextUpdatedPacket` 0x149 + `CSStartQuestContextPacket` 0x0D5 +
  `SCItemTaskSuccessPacket` 0x090 (accept bundle) → `CSInteractNPCEndPacket` 0x066
  (dialogue closed ~2 s later). No loot packet in file.
- Classification:
  - ENGINE REQUIREMENT: interact-before-context packet order; `NpcId` match on report
    (`QuestActConReportNpc.cs:42-43`); `isReady` gate (`:47-55`); resolve-or-ignore in
    `DoReportEvents` (`QuestManagerEvents.cs:24-48`); turn-in completion emits Updated +
    ItemTaskSuccess + ExpChanged before Completed (tail sequence above — the bot-side
    `DistributeRewards` bag/mail + `AddExp` + `ChangeMoney` in `Quest.cs:304-401` is the
    same bundle server-side).
  - HUMAN BEHAVIOR: target-select before talking; ~2–2.4 s dialogue dwell each side;
    post-dialogue mount summon (skill 10602, caster-type Item); movement immediately
    before the Complete packet (return-travel tail, quest-agnostic).
  - BOT POLICY CHOICE: `InteractNpc` over `Talk` (§F.3); close-in distance (3 m scenario
    precedent vs 25 m actor gate — G8b should close to ≤ 25 m, and the 3 m LevelingLoop
    close-in `:3048` is the conservative pick); withholding the priority-30 turn-in
    until in range (§I.1 — the engine never requires this, the gate does).
