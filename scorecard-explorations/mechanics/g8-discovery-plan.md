# G8 Discovery Plan — Quest 251 Turn-In Closeout (2026-09-20, branch develop)

SYNTHESIS ONLY. Nothing modified, built, or run. Sources: `g8-turnin-paths.md` (§§A–E), `g8-return-ownership.md` (§§F–J). G3–G7d frozen, untouched.

## A. TurnInQuest Production Path (bot chain + per-layer legality)

1. Perceive: `GameplayActor.Observe` snapshot (`GameplayActor.cs:284,292`); nearby radii 25 m (turnin §A.1).
2. Ready detection: Ready quest gets no advance work (`QuestBehavior.cs:306-312`); every active quest tested for turn-in (`QuestBehavior.cs:316-317`) (turnin §A.2).
3. Proposal: `TurnInProposals` yields only if `Status == Ready` (`QuestBehavior.cs:601-603`); template + Ready-component scan (`QuestBehavior.cs:604,607-608`); ReportNpc → doodad → auto (`QuestBehavior.cs:609-631`) (turnin §A.3).
4. Resolution: `GetNpcByTemplateId(reportNpc.NpcId)` (`QuestBehavior.cs:613`), global first-match (`WorldInstance.cs:476-479`); null → yield break; non-null → `TurnInProposal(TurnInQuest, reporter.ObjId, QuestTurnInParams(objId,-1))` (`QuestBehavior.cs:616-617,634-655`) (turnin §A.4).
5. Proposal legality: goal `quest.turn-in`, priority 30 default (`QuestBehavior.cs:638-648`; `QuestDecisionScenario.cs:49-73`); only hard precondition is quest-active (`QuestBehavior.cs:651-654`) — no range/liveness/LOS/zone/service check (turnin §A.5).
6. Selection: `BotDecisionSelector.Select` preconditions-first, priority-desc (`BotDecisionProposal.cs:231-291`); 30 beats pursuit/combat/loot/advance by construction (turnin §A.6).
7. Dispatch: `TurnInQuest` → `gameplayActor.TurnInQuest(questId, TargetObjId, SelectedReward, key)` (`QuestBehavior.cs:1081-1082`) → `TurnIn(...)` (`GameplayActor.cs:955-956,970-1030`) (turnin §A.7).
8. Dispatch legality: `TryBegin` busy gate (`GameplayActor.cs:4750-4757`); quest-active else reject (`GameplayActor.cs:977-980`); `ResolveUnit`/`GetUnit` existence only (`GameplayActor.cs:982-990,4868-4875`) (turnin §A.8).
9. Engine call: `QuestController.ReportTurnIn` (`GameplayActor.cs:995-996`) → `DoReportEvents` (`PlayerBotController.cs:126-130`) — the packet path (turnin §A.9).
10. Drain + terminal: ≤8 `RunCurrentStep` passes while active (`GameplayActor.cs:1006-1019`); `HasQuestCompleted` → ledger marker + `Complete` (`GameplayActor.cs:1021-1029`); still-active → `Complete(completed:false)` (`GameplayActor.cs:1029`) (turnin §A.10).

## B. Canonical Human/Engine Turn-In Path (packet/handler/validation + comparison)

- Packet: `CSCompleteQuestContextPacket` 0x0d6 reads `{questContextId, npcObjId, doodadObjId, selected}` → `DoReportEvents` (`CSCompleteQuestContextPacket.cs:14-27`; `GameNetwork.cs:218`) (turnin §B.1).
- Fan-out: `DoReportEvents` npc branch `GetNpc` null → silent return else `OnReportNpc{QuestId, NpcId=TemplateId, Selected, Transform}` (`QuestManagerEvents.cs:24-41`); doodad mirror (`QuestManagerEvents.cs:42-59`); neither-id sets `Step = Reward` (`QuestManagerEvents.cs:60-69`); no proximity/alive/visibility/zone/service check in any branch (turnin §B.1).
- Match + arm: `QuestActConReportNpc.OnReportNpc` act-id + `NpcId` equality, readiness floor (`let_it_done=f` so `QuestComplete`), sets `SelectedRewardIndex`/`OverrideObjectiveCompleted`, `Step→Ready`, `RequestEvaluation` (`QuestActConReportNpc.cs:40-62,28-38`); status enum (`QuestObjectiveStatus.cs:3-9`) (turnin §B.1).
- Step machine: Ready → Reward → reward acts → completed flag + `DropQuest` + `SCQuestContextCompletedPacket` (`NewQuestCode.cs:131-151`); `SCQuestContextUpdatedPacket` per pass (`NewQuestCode.cs:86-87`); evaluation queue (`Quest.cs:480-485`; `QuestManager.cs:145-156,163-174`) (turnin §B.1).
- Reward: `QuestActSupplyItem.RunAct` → `QuestRewardItemsPool` (`QuestActSupplyItem.cs:26-51`); `Quest.cs:307-341` distributes (bag or quest-reward mail fallback); gather cleanup consumes 4058×3 (`QuestActObjItemGather.cs:49-58`) (turnin §B.1).
- Human chain: approach → `CSStartInteractionPacket` (explicit `// TODO: Distance-check`, `CSStartInteractionPacket.cs:37-39` — server does not gate even dialog range) → `CSQuestTalkMadePacket` → `DoTalkMadeEvents` (`CSQuestTalkMadePacket.cs:20-28`) → `CSCompleteQuestContextPacket` (turnin §B.3); accept mirrors via `CSStartQuestContextPacket` → `AddQuestFromNpc` (`CharacterQuests.cs:200-207`; gates in `AddQuest`, `CharacterQuests.cs:82-192`, no proximity) (turnin §B.3).
- Comparison: reporter resolution differs (client dialog target vs global `GetNpcByTemplateId`); approach/talk differ (player walks vs none); report event identical (`ReportTurnIn` → same `DoReportEvents`, `PlayerBotController.cs:126-130`); drain shares `RunCurrentStep`; reward/removal identical (turnin §B.3 table).
- Bypass (separate, bot never touches): `CSTryQuestCompleteAsLetItDonePacket` 0x0dd → `Step = Reward` (`CSTryQuestCompleteAsLetItDonePacket.cs:20-30`; `CharacterQuests.cs:742-756`) (turnin §B.2).

## C. 85m Turn-In Explanation (classification + code paths)

- Facts: char 56 wake `quest-56-639255607033044476` landed `TurnInQuest` at `(15587.1,15120.3,130.8)` (`game.log:4344`), flat ≈86 m from giver spawner `(15655.91,15172.51)`; next wake sweep saw only 3475 in-bubble (`game.log:4346-4347`) — reporter outside 25 m bubble (turnin §D.1). Char 55 same shape at `(15582.1,15113.4,130.7)` (`game.log:3723`), ≈95 m; 3rd loot drove Progress→Ready (`game.log:3705-3709`), next wake proposed and won at priority 30 (turnin §D.1). Completion pass `ReportNpc.Finalize(104)` → `SupplyItem(4075)` → accept/gather `FinalizeQuest` → removed (`game.log:3715-3722`) (turnin §D.1).
- Classification: **PLAYERBOT SEMANTIC SHORTCUT resting on a MISSING LEGALITY CHECK** — not expected engine semantics (Transform carried for a distance check, `UnitEvents.cs:211-217`, plus TODOs at `CSStartInteractionPacket.cs:38` and `QuestActConReportDoodad.cs:51`); not a QuestBehavior policy gap alone (proposal mirrors engine acceptance) (turnin §D.2).
- Bot shortcut path: `QuestBehavior.cs:613-617` → priority-30 select (`QuestDecisionScenario.cs:49`; `BotDecisionProposal.cs:283-290`) → `QuestBehavior.cs:1081-1082` → `GameplayActor.cs:955-956,970-1030` (existence-only `:982-990`) (turnin §D.2).
- Missing engine check: `QuestManagerEvents.cs:22-70` (silent-return on null only) + `QuestActConReportNpc.cs:40-62` (template + status only, `Transform` unread) — any client packet enjoys the same distance-blind completion today (turnin §D.2).

## D. Giver Resolution (3512 accept+report, spawner, reward 18791)

- 251 components (sqlite `compact.sqlite3`; kinds `QuestComponentKind.cs:3-14`): 383 Start act 333 `QuestActConAcceptNpc`/77 → `quest_act_con_accept_npcs` 77 → npc 3512; 633 Progress act 10473 `QuestActObjItemGather`/616 → 4058×3; 638 Ready act 457 `QuestActConReportNpc`/104 → `quest_act_con_report_npcs` 104 → npc 3512 (`use_alias=f`); 639 Reward act 36911 `QuestActSupplyItem`/4075 → detail 4075 → item 18791×1 (turnin §C.2).
- **3512 is both acceptor and reporter** (accept match `QuestActConAcceptNpc.cs:17-21`); template row 3512 `촌장 고트`, kind 1, level 5, faction 101, non-aggressive; single spawner `(15655.91,15172.51,121.24)` (`npc_spawns.json:184417-184424`; post-turn-in dest `(15655.3,15170.9)` corroborates, `game.log:3727`); accept-time objId 44311 both reports' prehold (turnin §C.2).

## E. Interaction Legality (radii per path; turn-in/report 0m; Transform-args unread)

- Metric: `MathUtil.CalculateDistance(..., includeZAxis=false)` flat 2D default (`MathUtil.cs:332-361`); bot verbs pass flat (turnin §C.3).
- Enforced radii: Talk 25 m flat reject (`GameplayActor.cs:1067-1068`); `InteractNpc` 25 m (`GameplayActor.cs:1166-1167`); `DiscoverQuests` 25 m (`GameplayActor.cs:1294-1296`; `MaxQuestDiscoverRange = MaxInteractRange = 25f`, `GameplayActor.cs:1270,1535`); merchant 3 m (`CSBuyItemsPacket.cs:51-58`; `MaxShopRange = 3f`, `GameplayActor.cs:2836,2857-2859`); loot 200 m (`GameplayActor.cs:1742-1743`); `CSInteractNPCPacket` sets `CurrentInteractionObject`/`CurrentTarget` with no check (`CSInteractNPCPacket.cs:16-24`) (turnin §C.3).
- Turn-in/report: **no radius/metric/bounding/visibility/LOS/zone/service check at any of three layers** (turnin §A table, §C.3); debt markers: `// TODO: Distance-check` (`CSStartInteractionPacket.cs:38`), `// TODO: Check doodad range?` (`QuestActConReportDoodad.cs:51`), `// TODO Verify: targeted?` (`QuestActConReportNpc.cs:24`); `OnReportNpcArgs.Transform` "чтобы проверять расстояние" carried, never read (`UnitEvents.cs:211-217`; `QuestActConReportNpc.cs:40-62`) (turnin §C.3).

## F. Existing Return Logic (production turn-in path exists; return travel does not — only unspawned-reporter walks; Talk trap: 251 has no talk acts so Talk void-rejects, InteractNpc is dialogue fallback; interaction owner = InteractNpc)

- Production turn-in dispatch exists: `TurnInProposals` Ready-gated (`QuestBehavior.cs:598-632`), `TurnInPriority = 30` (`QuestDecisionScenario.cs:49-53`), `Dispatch` TurnIn arms (`QuestBehavior.cs:1081-1086`), `GameplayActor.TurnIn` preflights + packet path + ≤8 drain + terminal (`GameplayActor.cs:970-1030`; `PlayerBotController.cs:126-143`; `QuestManagerEvents.cs:22-70`) (return §F.1).
- Ready-suppresses-advance (`QuestBehavior.cs:307-317`): Ready quest contributes no `AdvanceProposal`; live symptom 60 "advances", 0 turn-ins, bot never moved (return §F.1).
- Return travel does NOT exist for spawned-and-distant reporter: `ResolveQuestTravelTarget` case 1 walks to reporter spawner only when reporter NOT spawned (`BotRoamStepExecutor.cs:1393-1454`, esp. `:1406-1423`); spawned → "lands next wake" (`:1419-1420`), no travel; `ArmQuestTravel` single-leg `QUEST_TRAVEL` (`BotRoamStepExecutor.cs:1325-1350`) (return §F.2).
- Talk trap: `Talk` >25 m rejects (`GameplayActor.cs:1067-1068`), fires `DoTalkMadeEvents` per talk-family objective (`GameplayActor.cs:1091-1105`), zero-delta → `RejectedAction` "produced no quest change" (`GameplayActor.cs:1140-1143`); `InteractNpc` same 25 m gate (`:1166-1167`), same fan-out (`:1203-1216`), never void — dialogue fallback (`:1245-1246`) + `Complete` (`:1256`) (return §F.3).
- 251 has no talk-family objective (Progress `QuestActObjItemGather` 4058×3, Ready `QuestActConReportNpc` 3512 — `g4-objective-credit.md:31-33`), so Talk on 3512 void-rejects; engine turn-in needs no Talk (`QuestActConReportNpc.cs:21-26,40-61`; `QuestManagerEvents.cs:22-70` proximity-free) (return §F.3). **Interaction owner = `InteractNpc`, never `Talk`** (return §§F.3, G).
- Reusable shapes (copy shape, never code): `LevelingLoopScenario.TurnIn` data-driven resolve + ≤8-leg `TravelToReporter` + 3 m close-in + `HasQuestCompleted` assert (`LevelingLoopScenario.cs:2951-3104,408-410`); `AdventurerSpikeScenario` RETURN leg `MoveToUnit→Accept→MoveToUnit→TurnIn→drain≤8` (`AdventurerSpikeScenario.cs:640-703`); M1M2 251 row + reward-conserved + completed criteria (`M1M2ReplayScenario.cs:94-95,182-193,326-330`) (return §F.4).

## G. Recommended Ownership (QuestBehavior WHY + return Move; GameplayActor legality; roam spawner-walk untouched; engine reward math)

| Slice | Owner | Why |
|---|---|---|
| WHY: Ready detection, reporter resolution, proposal + priority 30 | `QuestBehavior` | `TurnInProposals` + Ready-suppresses-advance (`QuestBehavior.cs:598-632,307-317`); priorities (`QuestDecisionScenario.cs:49-73`) (return §G) |
| HOW (travel): return Move leg, per-wake revalidation, drift retrack, abandon | `QuestBehavior` (new return proposal, PursuitGoal-shaped) | G5 precedent Move/Stop + preempt-first + drift (`QuestBehavior.cs:739-868,1119-1140`); brain owns re-resolve/repath, actor steps (brain-boundaries §3.3) (return §G) |
| LEGALITY: resolve, range, packet path, drain, terminal | `GameplayActor` | `TurnIn` preflights + `DoReportEvents` + drain + `HasQuestCompleted` (`GameplayActor.cs:970-1030`); `InteractNpc` 25 m gate + dialogue fallback (`GameplayActor.cs:1154-1257`) (return §G) |
| Route fallback (unspawned reporter → spawner walk) | `BotRoamStepExecutor`, untouched | `ResolveQuestTravelTarget`/`ArmQuestTravel` (`BotRoamStepExecutor.cs:1325-1454`) (return §G) |
| Reward math + cleanup + history flag | engine (`Quest.DistributeRewards`, quest state) | pools → bag/mail, level exp/copper, cleanup consume (`Quest.cs:304-413`); completed flag + ActiveQuests drop (M1M2 `:326-330`) (return §G) |

Non-owners: GOAP (unwired, brain-boundaries §1); queue (no new kinds — `Talk`/`TurnInQuest`/`AutoTurnIn` already dispatch, `BotActionCommandQueue.cs:607-633,905-908`); `Talk` for 251's interaction (return §G). [JUDGMENT] No new claim beyond source-doc ownership table.

## H. Recommended G8 Gates (three-gate split G8a reporter-resolved / G8b return+InteractNpc / G8c TurnIn+completed+reward; withhold-turn-in control as staging prerequisite; PASS + first-zero FAILs)

- Verdict: three gates, not four, not one. G8d folds into G8c: engine does report → Reward → `DistributeRewards` → completed-drop in one step-machine pass (`GameplayActor.cs:1006-1029`; `Quest.cs:304-413`), so no dispatch boundary separates removal from reward; the Talk half collapses because Talk void-rejects on 251 (return §H). Single-gate sweep rejected except as post-pass integration (same G7 §H objection: one zero-delta cannot localize) (return §H).
- G8a (resolved): 251 Ready + exactly one `TurnInQuest` proposal (target 251, `TargetObjId` = live 3512 objId, `SelectedReward` −1) while bot stands >25 m out; observe-only, zero dispatches (return §H).
- G8b (return + dialogue): return leg → flat ≤25 m → `InteractNpc` Completed with dialogue detail; never turns in — **withhold-turn-in control REQUIRED as staging prerequisite** (priority-0 override or harness proposal filter; `QuestOptions` priorities are init-values, `QuestDecisionScenario.cs:31-74`; no existing flag found) because `TurnIn` has no range gate (`GameplayActor.cs:970-1004`; `QuestManagerEvents.cs:22-70`) and priority 30 self-destructs the Ready-away fixture first wake (return §§H, I.1).
- G8c (turn-in + completion + reward): `TurnInQuest` Completed + `HasQuestCompleted(251)` + inactive + reward deltas (return §§H, I.3–I.4).
- PASS: G8a proposal present with `TargetObjId ==` live reporter objId, quest untouched; G8b flat ≤25 m + `InteractNpc` Completed, `turnIns == 0`, reward deltas 0; G8c Completed terminal + flag + inactive + 18791 +1 (bag or mail, `Quest.cs:311-322`) (return §I.3).
- First-zero FAILs (fail-closed): `reporter-unresolved`; `remote-turn-in` (turn-in while flat >25 m — withhold failed); `talk-void`; `dispatch-throw` (no `Dispatch` arm → RUN FidelityError, `QuestBehavior.cs:1073-1111,564-568`); `busy-reject` (`GameplayActor.cs:4750-4756`); `still-active`; `reward-missing`; `bag-full-mailed` is pass-with-note, not fail (return §I.3).

## I. Proposed E2E Proof (START 251-Ready away from giver; evidence fields; reward minimum = TurnIn Completed + HasQuestCompleted + inactive + 18791 0→1 + 4058 3→0)

- START (pre-gate snapshot, observe-only): 251 active `Status == Ready` (`QuestStatus.cs:8`); reporter live (`GetNpcByTemplateId(3512)` non-null, `QuestBehavior.cs:613` predicate); flat distance >25 m (outside `MaxInteractRange`, `GameplayActor.cs:1535`); bag 18791 = 0, 4058 = 3; copper/XP baseline; `HasQuestCompleted(251) == false`; actor idle (`TryBegin`, `GameplayActor.cs:4750-4756`) (return §I.1).
- Per-wake evidence: `ActiveQuestIds`, 251 `Step`/`Status`, reporter objId + template 3512 + flat/3-D distances, movement owner tag (`RETURN_MOVE_TO_UNIT` proposed, `PURSUIT_MOVE_TO_UNIT` precedent `QuestBehavior.cs:1126-1127`), proposal goal/action/targetId, request `State`/`Detail`/`Failure`, audit `TraceId`+`CycleId` join (`QuestBehavior.cs:505-510`); settled-wake cadence (G5/G6 hold-confirm, g6-closeout-freeze §b) (return §I.2).
- Reward minimum (clean completion proof): `TurnInQuest` Completed (`GameplayActor.cs:1027`) + `HasQuestCompleted(251)` + not in `ActiveQuests` (M1M2 `:326-330`) + `questcredit` ledger marker (`GameplayActor.cs:1026`) + bag/mail 18791 0→1 + bag 4058 3→0 (Cleanup = t, `Quest.cs:403-410`); copper/XP recorded, NOT gated — exact level-2 251 `quest_supplies` rows unread, `compact.sqlite3` absent from `AAEmu.Game/Data/` (return §I.4). Fixed item 18791×1 via act 36911/detail 4075, comp 639 (`g4-objective-credit.md:33`; `M1M2ReplayScenario.cs:20-22,94-95`); `SelectedReward` −1 correct for non-selective reward (selective keys on `SelectedRewardIndex`, `QuestActSupplySelectiveItem.cs:20-24`) (return §I.4).

## J. Next Action (exactly one bounded implementation step: withhold-turn-in mechanism + RETURN_MOVE_TO_UNIT owner tag + Dispatch arms are named gaps — pick the single first one)

Single first step: **build the withhold-turn-in staging control** (harness proposal filter or `TurnInPriority = 0` staging override — cheapest honest shape per `QuestDecisionScenario.cs:31-74`; no existing flag found), proven by staging START (Ready + reporter live + flat >25 m) and holding it across settled wakes with zero `TurnInQuest` dispatches while the `TurnInQuest` proposal remains present. Named-but-deferred gaps (not this step): `RETURN_MOVE_TO_UNIT` owner tag (`PURSUIT_MOVE_TO_UNIT` precedent, `QuestBehavior.cs:1126-1127`) and new goal-guarded `Dispatch` arms for return-Move/`InteractNpc` (G5/G6 precedent `:1097,:1104`; without them proposals throw → RUN FidelityError, `:564-568`) (return §§F.7, I.1–I.2).

---

G8 DISCOVERY COMPLETE
Observed remote turn-in mechanism: PLAYERBOT SEMANTIC SHORTCUT resting on a MISSING LEGALITY CHECK — `GetNpcByTemplateId` resolution + priority-30 `TurnInQuest` dispatch with zero approach/dialog/Talk/range discipline, completing from ~85–95 m through the engine's proximity-free report path.
Canonical player turn-in path: move → `CSCompleteQuestContextPacket` 0x0D6 → `DoReportEvents` → Updated + ItemTaskSuccess + ExpChanged → Completed (accept side: target-select → `CSInteractNPCPacket` dialog → dwell → Started + Updated + `CSStartQuestContextPacket` + ItemTaskSuccess → `CSInteractNPCEndPacket`).
Proximity legality: Talk / InteractNpc / Discover all enforce 25 m flat; turn-in/report enforces 0 m at all three layers (proposal, dispatch, engine), with `OnReportNpcArgs.Transform` carried expressly for a distance check and never read.
Correct completion giver: NPC template 3512 (accept act 333/77 + report act 457/104), single spawner (15655.91, 15172.51, 121.24), reward item 18791×1 (act 36911/detail 4075).
Recommended G8 decomposition: G8a reporter-resolved / G8b return + InteractNpc / G8c TurnIn + completed + reward (Talk dispatch unusable — Talk void-rejects on 251 since it carries no talk acts, so InteractNpc is the interaction owner with dialogue fallback).
First implementation step: withhold-turn-in staging control (priority-0 override or harness proposal filter) as the prerequisite that stops the priority-30 remote turn-in from self-destructing the Ready-away fixture; return-Move owner tag and Dispatch arms are named deferred gaps.
