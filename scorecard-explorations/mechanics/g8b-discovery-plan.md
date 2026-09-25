# G8b Discovery Plan — Return to Reporter + InteractNpc Dialogue (READ-ONLY, develop @ 2026-09-21)

READ-ONLY. Nothing modified, built, or run. G3–G7d frozen — cited, never edited.
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
G8b mission: from Ready (quest 251) + resolved reporter 3512 + withheld TurnIn, return movement
to reporter → flat ≤ 25 m → `InteractNpc` dialogue Completed; stop before TurnIn landing.
Read-first: `g8-return-ownership.md` (§F return classification, §G ownership, §I gate design, §J risks),
`g5-pursuit-semantics.md` (§§E–H snapshot/route/stop), `nearby-quest-contracts.md` (§3 nav contract).

START (assumed from G8a): 251 `QuestStatus.Ready` (`AAEmu.Game/Models/Game/Quests/Static/QuestStatus.cs:3-12`);
reporter 3512 live via `ParentWorld.GetNpcByTemplateId(3512)` (the `QuestBehavior.cs:627` predicate);
flat bot→reporter distance > 25 m; `QuestDecisionScenario.QuestOptions.WithholdTurnIn = true`
(`QuestDecisionScenario.cs:53`, enforcement `QuestBehavior.cs:512-518`); actor idle.

---

## A. MOVEMENT OPTIONS — what exists today, what G8b reuses

Three candidates, one pick:

### A1. Executor `ArmQuestTravel` reporter-spawner walk — REJECTED for G8b
- `ResolveQuestTravelTarget` case 1 walks to the Ready reporter's **spawner only when the reporter
  is NOT spawned** (`AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:1407-1424`); spawned
  reporter → `continue` ("turn-in will land next wake", `:1420-1421`), i.e. NO travel.
- G8b START is spawned-but-distant (> 25 m) — the exact hole `g8-return-ownership.md:60-62` names:
  "NO production return leg". Arming is single-leg `BotPath.PathTo` with owner `QUEST_TRAVEL`
  (`BotRoamStepExecutor.cs:1326-1350`), never re-armed over a live route (`:1328-1329`).
- Verdict: keep as unspawned-fallback only (untouched). It cannot move G8b's leg.

### A2. Roam route layer (`BotPath` + route pump `actor.MoveTo`) — REJECTED as owner
- Pump issues `actor.MoveTo(target, …)` per waypoint (`nearby-quest-contracts.md §3.1`,
  `BotRoamStepExecutor.cs:986` vicinity) and flat-arrival `Stop`s (`:1063-1066`); quest leg
  suppresses route issue while landing (`:981` area, `!state.QuestLegActive` gate).
- Route arrival gate is the tight box (`ArrivalRadius = 0.5f`,
  `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:75`), not a 25 m purpose radius; reaching a
  *spawner coordinate* to 0.5 m is the wrong terminal for "within talk range of a live reporter".
- Verdict: route layer stays fallback transport for spawner walks; G8b does not route through it.

### A3. Quest-owned `MoveToUnit(reporterObjId)` per-wake leg (G5 pursuit shape) — PICK
- `MoveToUnit` (`GameplayActor.cs:554-569`): `ResolveUnit` (`:565`) → null-reject (`:566-567`) →
  `StartMove(request, unit.Transform.World.Position, speed)` (`:568`). **Snapshot-once**: live unit
  discarded, only coordinates enter `_moveTarget` (G5 §E.1; Tick `:4084-4307` never re-resolves).
- G5 proves the reusable discipline for exactly this: `DispatchPursuitMove`
  (`QuestBehavior.cs:1133-1154`) — preempt live Move first (`:1135-1136`, roam `:787-789`
  precedent), stage owner tag (`SetPendingMoveOwner("PURSUIT_MOVE_TO_UNIT")`, `:1140-1141`;
  owner vocabulary `ActorRequest.cs:117-120`), `MoveToUnit(proposal.TargetId, PursuitSpeedMps,
  PursuitLegTimeout, …)` (`:1142`; `PursuitSpeedMps = 4.5f` `:75`, `PursuitLegTimeout = 10 s` `:77`),
  record live pos for drift gate (`:1143-1152`).
- Reporter 3512 is a static NPC: snapshot staleness (G5's moving-target worry) is near-zero, but the
  same per-wake revalidation still applies because **despawn/recycle** (not motion) is the risk —
  re-resolve `GetNpc(reporterObjId)` + template check every wake; drift retrack (> 2.0 m,
  `PursuitRetrackDriftM`, `QuestBehavior.cs:73`) only fires on genuine motion.
- Withhold interplay: `opts.WithholdTurnIn && selected.Goal == TurnInGoal` returns
  `Fail("WITHHELD", WrongDecision, …)` with dispatch skipped (`QuestBehavior.cs:512-518`).
  A return-Move proposal MUST carry a **different goal** (e.g. `quest.return`, proposed — not present)
  so withhold never swallows it; TurnIn stays proposed-but-unlanded (observable in diagnostics,
  `Request == null`, quest stays Ready/active — `:507-511`).
- **Recommend: new return proposal (`MoveToUnit` on reporter objId) + new goal-guarded `Dispatch`
  arm, mirroring `DispatchPursuitMove`, NOT reuse of `PursuitGoal` and NOT executor travel.**
  Reusing `PursuitGoal` would entangle 3475-pursuit telemetry (`LastPursuitIssue`, hold-confirm) and
  priority 24 (`ObjectivePursuitPriority`, `QuestDecisionScenario.cs:64`) with return intent.

## B. INTERACTNPC CONTRACT (verified from code)

`GameplayActor.InteractNpc` (`GameplayActor.cs:1154-1257`); action kind `InteractNpc = 52`
(`IGameplayActor.cs:1085-1086`):

| Step | Behavior | Cite |
|---|---|---|
| Busy | `TryBegin` fail → busy terminal (single-writer; `StateTransition "actor busy"`, cf. `:4750-4756` via g8 §F.7) | `:1159-1160` |
| Resolve | `ParentWorld?.GetNpc(npcObjId)` null → `Reject(RejectedAction, "npc … not found in world")` | `:1162-1164` |
| **25 m gate** | flat `CalculateDistance(…, false) > MaxInteractRange (25f)` → `Reject(RejectedAction, "… out of interaction range")` | `:1166-1167`, `MaxInteractRange` `:1535` |
| Start | set+broadcast `CurrentTarget`, `SCNpcInteractionSkillListPacket` (option by template role flags) | `:1169-1199` |
| Talk-credit fan-out | per active quest with `QuestActObjTalk`/`QuestActObjTalkNpcGroup` matching template → `DoTalkMadeEvents` + bounded drain ≤ 8 | `:1201-1226` (mirror of `Talk` `:1091-1119`) |
| **Dialogue fallback (terminal semantics)** | zero quest delta → `changes.Add("dialogue with npc {template} …")`, then `Complete` — **never void-rejects** | `:1245-1246`, `Complete` `:1255-1256` |
| Trace | `talk_npc` + `talk_end` interaction records | `:1248-1253` |

Contrast `Talk` (`:1051-1152`): same gate (`:1067-1068`), same fan-out (`:1091-1105`), but zero
delta → `Reject(RejectedAction, "produced no quest change")` (`:1140-1143`). For 251 (no talk-family
objective — Progress is item-gather, Ready is `ConReportNpc`; `g8-return-ownership.md:71-74`),
`Talk` void-rejects; **`InteractNpc` is the only correct interaction verb**. G8b PASS expects the
dialogue detail (`"dialogue with npc 3512 …"`), not quest-credit detail.

## C. OWNERSHIP (who owns intent, leg, stop)

Per G5 §G (actor owns leg, caller owns intent) and g8 §G:

| Slice | Owner | Mechanism |
|---|---|---|
| WHY: Ready detection, reporter resolution (`GetNpcByTemplateId(3512)` per wake, never stored), return-vs-withheld-turnin arbitration | `QuestBehavior` (new return proposal) | `TurnInProposals` pattern (`:612-646`); withhold gate (`:512-518`) |
| HOW: return `MoveToUnit` leg, per-wake revalidation (resolve + template 3512 + alive), drift retrack, abandon | `QuestBehavior` (return proposal + goal-guarded `Dispatch` arm, `DispatchPursuitMove` shape `:1133-1154`) | public verbs only; new owner tag e.g. `RETURN_MOVE_TO_UNIT` (proposed; `PURSUIT_MOVE_TO_UNIT` precedent `:1141`, vocabulary `ActorRequest.cs:117-120`) |
| LEGALITY: resolve/range/dialogue/terminal | `GameplayActor.InteractNpc` | §B above |
| STOP at ≤ 25 m | `QuestBehavior` decides, `GameplayActor.Stop` executes (audited) | §D |
| Route fallback (reporter unspawned → spawner walk) | `BotRoamStepExecutor` (untouched) | `:1399-1424`, armed `:1326-1350` |
| Queue | no new kinds (`Move`/`Stop`/`InteractNpc` already in vocabulary; `Talk`/`TurnInQuest`/`AutoTurnIn` dispatch queue-side per g8 §G) | `IGameplayActor.cs:921-1093` |

`Dispatch` today has arms for Advance/Accept/TurnIn(×3)/Target/pursuit-Move/pursuit-Stop/combat/
loot (`QuestBehavior.cs:1089-1124`) — **no `Talk`, `InteractNpc`, or return-Move arm** (g8 §F.7).
A return/InteractNpc proposal through this switch hits exhaustion → throws → RUN FidelityError
(`:564-568` area, g8 §F.7 [INFERENCE]). Both need new goal-guarded arms (G5/G6 precedent
`:1111-1112`, `:1118`).

## D. STOP/SETTLE AT REPORTER (halt within 25 m without oscillating)

- **Mechanism: audited `Stop()` proposal at ≤ 25 m**, not arrival at 0.5 m. `Stop()`
  (`GameplayActor.cs:571-594`): interrupts live leg (`InterruptActive`), `BroadcastStop` if walking
  (`:587-588`), own request `Complete("stopped")` (`:592`). Silent alternative `PreemptCurrent` +
  clear reference exists (`:597-610`; contract `IGameplayActor.cs:123-137` per G5 §G.2) but Stop is
  the audited, dossier-§1.6-standstill choice — G5 precedent `DispatchPursuitStop` (`:1161-1177`)
  + in-range Stop proposal (`:809-845`).
- **Why not ride the leg to arrival:** leg arrival needs flat AND Z within `ArrivalRadius = 0.5f`
  (`GameplayActor.cs:75`; dispatch no-op `:436-440`; Tick `:4295` area per G5 §H). A `MoveToUnit`
  leg aimed at the reporter would walk to melee contact — overshoot past the 25 m gate's purpose,
  Z-fragile (arrival needs `|ΔZ| ≤ 0.5`, G5 §H), and unbounded-looking. The 25 m goal is a
  **brain-side radius check per wake** (`CalculateDistance(pos, reporterPos, false) <= 25f`),
  exactly like `PursuitStopRadiusM = 3.0f` (`QuestBehavior.cs:65`) gates Stop at 3 m (`:809`).
- **No-oscillation discipline (copy G5):** outside 25 m → Move (fresh leg when none live,
  drift-gated retrack only after target motion > 2 m — `:847-864`); inside → Stop once, then
  **hold-confirm withdraw** so the interact proposal can win the settled wake (G6 hold-confirm
  pattern `:811-827`: Stop landed + poses within 0.5 m → withdraw, else re-Stop). Without the
  withdraw, Move/Stop flap every wake; without Stop-first, a live leg busy-rejects InteractNpc.
- **Ordering inside 25 m:** Stop (settle) → next wake `InteractNpc`. InteractNpc needs no idle-leg
  preamble beyond the actor being free (`TryBegin`); Stop guarantees it.

## E. FAILURE TAXONOMY (predicate-named, current-code terms)

Spec §17 vocabulary is the ONLY rejection language (`IGameplayActor.cs:1107-1135`: WrongDecision 1 /
Navigation 2 / RejectedAction 3 / StateTransition 4 / Persistence 5 / Starvation 6 / FidelityError 7).
G8b classes (stage: `REPORTER-*` resolve, `RETURN-*` travel, `INTERACT-*` dialogue, `HARNESS-*` rig):

| Class | Predicate (fail-closed) | Code terminal |
|---|---|---|
| `REPORTER-UNRESOLVED` | Ready but `GetNpcByTemplateId(3512)` null → no turn-in proposal (`yield break` `:628-629`) | `Fail("DECIDE", WrongDecision, "no legal quest proposal…")` (`:501-502`) |
| `REPORTER-DESPawnED` | reporter live at staging, null mid-return (spawner-walk fallback does NOT arm — legal work exists, `BotRoamStepExecutor.cs:1307-1311` per g8 §J.1.6) | `MoveToUnit`/`InteractNpc`/`TurnIn` `Reject(RejectedAction, "…not found in world")` (`GameplayActor.cs:566-567`, `:1163-1164`, `:987-989`) |
| `REPORTER-RECYCLED` | objId resolves but `TemplateId != 3512` (bare-dict hit, no generation check — G5 §F.2) | gate must check template; else success-wrong [INFERENCE — check proposed, not present] |
| `RETURN-STUCK` | `NoProgressWindow = 2.5 s`, one 2 m nudge, then expire (G5 §H) | `TimedOut(Navigation, "stuck: no progress…")` |
| `RETURN-BUDGET` | leg exceeds `PursuitLegTimeout`-shaped budget (10 s precedent `:77`) | `TimedOut(Navigation, "navigation budget exceeded")` |
| `RETURN-BUSY` | return Move dispatched while non-Move leg live (quest leg runs only while idle; live roam/travel leg) | `Rejected(StateTransition, "actor busy…")` (cf. `:4750-4756`; queue `:446-448`) |
| `RETURN-DRIFT` | reporter moved > 2 m since last issue while leg live (static NPC: unexpected) | `PreemptCurrent("…retrack")` + re-`MoveToUnit` (shape `:1135-1142`), `Interrupted` record |
| `INTERACT-OUT-OF-RANGE` | `InteractNpc` dispatched while flat > 25 m (ordering bug) | `Reject(RejectedAction, "…out of interaction range")` (`:1166-1167`) |
| `INTERACT-TALK-VOID` | `Talk` dispatched instead of `InteractNpc` on 251 (policy wording bug) | `Reject(RejectedAction, "produced no quest change")` (`:1140-1143`) |
| `INTERACT-DISPATCH-THROW` | proposal with no `Dispatch` arm | RUN `FidelityError` (g8 §F.7) |
| `HARNESS-REMOTE-TURNIN` | withhold OFF/failed: priority-30 turn-in lands while flat > 25 m (`TurnIn` has NO range gate — verified absence `:970-1004`) | quest leaves Ready/active; fixture self-destructs (g8 §I.1) |
| `HARNESS-WITHHELD-SWALLOW` | return proposal carries `TurnInGoal` → withhold eats the return leg | `Fail("WITHHELD", WrongDecision, …)` (`:515-517`) on a Move (diagnostic smell) |
| `HARNESS-RECOVERY-OWNED` | `OutOfCombatRecoveryModule` priority 85 owns post-damage wakes (g8 §J.1.5) | setup noise, never a return failure; refill vitals at staging |

## F. FIXTURE (starting state, observe-only, funnel evidence)

- **Starting state (all asserted pre-gate, observe-only):** 251 active `Status == Ready`;
  reporter live (`GetNpcByTemplateId(3512)` non-null, record objId + template); flat distance > 25 m
  (`CalculateDistance(…, false)`); bag 18791 = 0; `HasQuestCompleted(251) == false`; actor idle
  (no live request); `WithholdTurnIn = true`; vitals refilled (recovery-noise guard, g8 §J.1.5);
  settled wakes (hold-confirm cadence, g6-closeout-freeze §b per g8 §I.2).
- **START observe-only rules (g8 §I.2):** no dispatch except the gate's named verbs (G8b:
  Move/InteractNpc + Stop); per wake record `ActiveQuestIds`, 251 `Step`/`Status`, reporter objId +
  template 3512 + flat/3-D distances (G5 §E.3: arrival proves coordinates — pin template + alive +
  nearest alongside), movement owner tag (`MoveOwner`, `RETURN_MOVE_TO_UNIT` proposed;
  `PURSUIT_MOVE_TO_UNIT` precedent `:1140-1141`), proposal `goal/action/targetId`, request
  `State/Detail/Failure`, audit `TraceId` + `CycleId` join (`:519-524`).
- **Funnel evidence fields per wake:** `reporterObjId, reporterTemplate, flatM, dist3D, proposalGoal,
  proposalAction, moveOwner, requestState, requestDetail, failure, traceId, cycleId, turnIns,
  reward18791delta, questStatus`.
- **PASS shape:** reporter stable (same objId + template 3512 across wakes) + flat-distance series
  strictly decreasing → flat ≤ 25 m reached + `InteractNpc` Completed with dialogue detail naming
  template 3512 (`:1245-1246`, `:1256`) + TurnIn still withheld (`turnIns == 0`, `WITHHELD` rows only)
  + quest still Ready/active + zero reward deltas (18791 = 0, copper/XP = baseline).

## G. GATE (G8b shape)

Single gate, three conjuncts (all joined by CycleId→TraceId, g8 §I.2): (1) return leg terminates
with flat ≤ 25 m (Move `Completed("arrived")` at snapshot is INSUFFICIENT alone — must join live
reporter distance per G5 §E.3); (2) `InteractNpc(reporterObjId)` → `Completed` with dialogue detail;
(3) withhold holds (`turnIns == 0`, quest Ready/active, rewards 0). First-zero FAILs: §E classes in
order `REPORTER-UNRESOLVED → HARNESS-REMOTE-TURNIN → RETURN-BUSY/STUCK/BUDGET → INTERACT-*`.
G8a (proposal observable, zero dispatches) is the pre-gate; G8c (TurnIn → completed + reward) starts
only after G8b PASS with withhold released.

## H. RISKS

1. **Reporter despawn mid-return** reads as `"…not found in world"` (`:1163-1164`, `:987-989`), not
   navigation failure — name `REPORTER-DESPawnED`, distinct from `RETURN-STUCK/BUDGET` (g8 §J.1.6);
   spawner row for 3512 assumed from spawner-walk code, not re-verified → UNKNOWN.
2. **Repath needs:** none expected (static reporter; snapshot-once adequate); bounded retry only
   (`CombatExecutor` MaxAttempts/WaitBudget precedent per G5 §H), never unbounded re-issue; drift
   retrack only on genuine > 2 m motion.
3. **Withhold × return:** withhold matches `Goal == TurnInGoal` only (`:512`) — return goal MUST
   differ or the leg is swallowed (`HARNESS-WITHHELD-SWALLOW`); conversely withhold must stay ON or
   priority-30 remote turn-in (no range gate `:970-1004`) kills the fixture (g8 §I.1).
4. **Busy-reject vs live legs:** quest leg runs while actor idle; a live roam/quest-travel leg
   busy-rejects the return dispatch unless preempted first (pursuit pattern `:1135-1136`); return
   dispatch must preempt-first on retrack and Stop-before-interact on arrival.
5. **Recovery preemption** (priority 85) may own early wakes — treat as setup noise (g8 §J.1.5).
6. **Missing `Dispatch` arms** for return-Move/`InteractNpc` throw → RUN FidelityError until added
   (g8 §F.7); 3512 leash/despawn/recovery rows UNKNOWN (g8 §J.1.8).

## I. CONTRACT (seam + invariants)

- Seam: `BotActionCommandQueue → GameplayActor → engine` (locked). Brain proposes, actor verbs
  execute, engine gates (`TryBegin`/resolve/range) stay fail-closed at dispatch.
- New return proposal: goal `quest.return` (proposed), action `Move` → `MoveToUnit(reporterObjId)`;
  in-range: goal `quest.return`, action `Stop`; settled: action `InteractNpc`.
  New goal-guarded `Dispatch` arms (G5/G6 precedent); no writes to `_move*`/`_active`, no new queue
  kinds, no GOAP/scheduler touch.
- Invariants: per-wake live revalidation (resolve + template + alive); withhold ON all G8b wakes;
  TurnIn never lands (`turnIns == 0`); quest untouched (Ready/active); owner tag on every return leg.

## J. NEXT ACTION (no implementation now)

1. Confirm G8a PASS (single `TurnInQuest` proposal, `TargetObjId ==` live 3512 objId,
   `SelectedReward == -1`, flat > 25 m, zero dispatches) with `WithholdTurnIn = true`
   (`QuestDecisionScenario.cs:53`, `QuestBehavior.cs:507-518`) — the withhold shape g8 §I.1 called
   UNKNOWN is now grounded: it exists, default-off.
2. Then implement the G8b slice: return proposal + `RETURN_MOVE_TO_UNIT` owner + two `Dispatch`
   arms (return-Move, return-Stop) + `InteractNpc` arm, all goal-guarded; gate per §G.
3. G8c (withhold release → `TurnInQuest` → completed + 18791) only after G8b PASS.
