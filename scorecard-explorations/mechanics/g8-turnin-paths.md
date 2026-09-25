# G8 Turn-in Paths — Quest 251 (Ready → report → reward)

CODE+DATA-ONLY audit, repo `/root/aaemu-dev`, branch `develop`, 2026-09-21. Nothing modified, built, or run.
G3–G7d frozen (untouched). G8 mission: **turn-in semantics discovery only** — no behavior verdict on the
funnel/credit legs, no policy proposal.

Context read first: `/tmp/postready-transition-report.json` (present; identical content to
`/root/aaemu-e2e-q0/logs/postready-transition-report.json`). Both JSON reports are the **same observe-only
run** (`postready-transition-observe`, verdict `FAIL-BEHAVIOR/QUEST-LOST`, quest lost at `QUEST/not-active`) —
no turn-in occurs inside either report. The turn-in evidence lives in `/root/aaemu-e2e-q0/logs/game.log`
(two landed `TurnInQuest` wakes, §D) plus the mission-stated facts (reward 18791, quest removed).
Also read: `scorecard-explorations/mechanics/g4-objective-credit.md` (§A act shapes — not re-derived here),
`AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs` (turn-in proposal branch).

---

## A. Bot path: Ready detection → reward (Q1 — Sections 1+4)

Chain, every hop with its legality gate:

1. **Perceive.** `GameplayActor.Observe` snapshot: `ActiveQuestIds = Character.Quests?.ActiveQuests.Keys`
   (`GameplayActor.cs:292`), `CurrentTargetObjId` (`GameplayActor.cs:284`). Nearby scan radii are 25 m
   (doodad side at `GameplayActor.cs:291`; NPC side per G4 `GameplayActor.cs:262-264`).
2. **Ready detection.** Per active quest per wake: an already-Ready quest gets **no** advance work —
   `RunCurrentStep` from Ready is a no-op that still reports Completed, so advancing would starve the route
   layer (`QuestBehavior.cs:306-312`, comment). Only non-Ready/non-Completed quests get an `AdvanceProposal`
   (`QuestBehavior.cs:313-315`); every active quest is tested for turn-in (`QuestBehavior.cs:316-317`).
3. **Proposal gate.** `TurnInProposals` yields nothing unless the live quest object
   `is { Status: QuestStatus.Ready }` (`QuestBehavior.cs:601-603`). Template via
   `QuestManager.Instance.GetTemplate(questId)` (`QuestBehavior.cs:604`); Ready components' acts
   (`QuestBehavior.cs:607-608`); first `QuestActConReportNpc`, else doodad, else auto
   (`QuestBehavior.cs:609-631`, LevelingLoop precedent cited at `QuestBehavior.cs:592-596`).
4. **Giver resolution.** `character.ParentWorld?.GetNpcByTemplateId(reportNpc.NpcId)`
   (`QuestBehavior.cs:613`) — a **global** world lookup returning the first dict match
   (`WorldInstance.cs:476-479`), no distance/zone/visibility input. Null → `yield break`: no proposal, and the
   travel layer later walks to the reporter spawner (`BotRoamStepExecutor.cs:1418-1422`,
   `GameplayActor.cs:1271-1360` region). Non-null → `TurnInProposal(... TurnInQuest, reporter.ObjId,
   QuestTurnInParams(reporter.ObjId, -1))` (`QuestBehavior.cs:616-617`), built by `TurnInProposal`
   (`QuestBehavior.cs:634-655`).
5. **Proposal legality (the whole of it).** Goal `quest.turn-in`, priority `opts.TurnInPriority`
   (`QuestBehavior.cs:638-648`; default **30**, `QuestDecisionScenario.cs:49` — top of the quest ladder:
   target 25 / pursuit 24 / combat 23 / loot 22 / advance 20 / accept 10, `QuestDecisionScenario.cs:49-73`).
   The **only** hard precondition is `quest-active` (observed `ActiveQuestIds` contains questId)
   (`QuestBehavior.cs:651-654`). No range, liveness, distance, LOS, visibility, zone, or NPC-service
   precondition exists at proposal level.
6. **Selection.** `BotDecisionSelector.Select` filters by hard preconditions first, then orders by priority
   desc (personality clamped, tie-break, index) (`BotDecisionProposal.cs:231-291`). A Ready quest's turn-in
   (30) beats every pursuit/combat/loot/advance proposal by construction.
7. **Dispatch.** `Dispatch` maps `ActorActionType.TurnInQuest` → `gameplayActor.TurnInQuest(TargetId=questId,
   turnIn.TargetObjId, turnIn.SelectedReward, key)` (`QuestBehavior.cs:1081-1082`), i.e.
   `TurnIn(questId, TurnInQuest, npcObjId, selectedReward, key)` (`GameplayActor.cs:955-956,970-1030`).
8. **Dispatch legality (the whole of it).** Single-writer `TryBegin` busy gate (`GameplayActor.cs:4750-4757`;
   busy → `StateTransition` reject); quest-active else `StateTransition` reject (`GameplayActor.cs:977-980);
   target existence via `ResolveUnit` → `ParentWorld.GetUnit(objId)` (`GameplayActor.cs:982-990,4868-4875`)
   else `RejectedAction`. **Existence only** — no distance, alive, visible, LOS, zone, or service-type check.
9. **Engine call.** `QuestController.ReportTurnIn(questId, targetObjId, selectedReward)`
   (`GameplayActor.cs:995-996`) → `QuestManager.Instance.DoReportEvents(Character, questId, npcObjId, 0,
   selectedReward)` (`PlayerBotController.cs:126-130`) — literally the packet path (§B).
10. **Drain + completion.** Report event drives the step machine; the actor drains up to 8
    `quest.RunCurrentStep()` passes while the quest stays active, stopping on a false advance so a
    not-ready quest is never force-advanced (`GameplayActor.cs:1006-1019`). `HasQuestCompleted` →
    idempotency-ledger reward marker + `Complete` (`GameplayActor.cs:1021-1029`); still-active →
    `Complete(completed:false, "still active")` (`GameplayActor.cs:1029`).

Per-layer legality matrix (turn-in only):

| Check | Proposal (`QuestBehavior.cs:598-655`) | Dispatch (`GameplayActor.cs:970-990`) | Engine (`QuestManagerEvents.cs:22-41`, `QuestActConReportNpc.cs:40-62`) |
|---|---|---|---|
| Ready? | YES (`:602`) | [INFERENCE] re-checked implicitly via still-active + drain stop | YES — `GetQuestObjectiveStatus() >= QuestComplete` (`:47-55`) |
| Giver exists? | YES (`:613-615`, world-global) | YES (`:982-990`, registry-global) | YES — `GetNpc` null → return (`QuestManagerEvents.cs:27-30`) |
| Giver matches? | by template (`:609-613`) | no (opaque objId) | YES — `NpcId != args.NpcId` → return (`QuestActConReportNpc.cs:42-43`) |
| Alive? | NO | NO | NO |
| Visible / LOS? | NO | NO | NO |
| Distance / range? | NO | NO | NO (`Transform` carried but unread — §C) |
| Zone? | NO | NO | NO |
| NPC service type? | NO | NO | NO |

---

## B. Engine path, human/client chain, bot-vs-human comparison (Q2 — Section 2)

### B.1 Normal client turn-in, packet → reward

- **Packet.** `CSCompleteQuestContextPacket` (0x0d6) reads `{questContextId, npcObjId, doodadObjId, selected}`
  and calls `QuestManager.Instance.DoReportEvents(Connection.ActiveChar, ...)`
  (`CSCompleteQuestContextPacket.cs:14-27`; registered `GameNetwork.cs:218`).
- **Fan-out.** `DoReportEvents` (`QuestManagerEvents.cs:22-70`): npc branch resolves `GetNpc(npcObjId)`,
  null → silent return, else fires `OnReportNpc{QuestId, NpcId=npc.TemplateId, Selected, Transform=npc.Transform}`
  (`QuestManagerEvents.cs:24-41`); doodad branch mirrors with `GetDoodad`
  (`QuestManagerEvents.cs:42-59`); neither-id branch sets `Step = Reward` directly
  (`QuestManagerEvents.cs:60-69`). **No proximity, aliveness, visibility, zone, or service check in any branch.**
- **Match + arm.** `QuestActConReportNpc.OnReportNpc` (`QuestActConReportNpc.cs:40-62`): act-id + `NpcId`
  equality; readiness floor (`LetItDone ? CanEarlyComplete : QuestComplete` — 251 has `let_it_done=f`,
  sqlite `quest_contexts`, so `QuestComplete`); sets `SelectedRewardIndex`, `OverrideObjectiveCompleted=true`,
  `Step<=Progress → Ready`, `RequestEvaluation()`. The event subscription is armed in `InitializeAction`
  and removed in `FinalizeQuest` (`QuestActConReportNpc.cs:28-38`); status enum
  (`QuestObjectiveStatus.cs:3-9`).
- **Step machine.** `Ready → Reward` (Status Completed) → Reward acts run → completed flag +
  `DropQuest` + `SCQuestContextCompletedPacket` (`NewQuestCode.cs:131-151`); each pass emits
  `SCQuestContextUpdatedPacket` (`NewQuestCode.cs:86-87`). Live log proof of the exact pass sequence:
  `QuestActConReportNpc.FinalizeAction(104)` → `QuestActSupplyItem.Initialize+Finalize(4075)` →
  accept/gather `FinalizeQuest` → quest removed (`game.log:3715-3722`).
- **Evaluation queue.** `RequestEvaluation` sets the flag + `MarkDirty` (`Quest.cs:480-485`);
  `EnqueueEvaluation` logs + schedules `QuestManagerRunQueueTask` (`QuestManager.cs:145-156`);
  `DoQueuedEvaluations` runs `RunCurrentStep` (`QuestManager.cs:163-174`) — seen firing around both turn-ins
  (`game.log:3704,3707,3710-3711,3714,3724,4345`).
- **Reward.** `QuestActSupplyItem.RunAct` puts `ItemCreationDefinition(ItemId, Count, Grade)` into
  `QuestRewardItemsPool` (`QuestActSupplyItem.cs:26-51`); `Quest.cs:307-341` distributes the pool (bag, or
  quest-reward mail fallback when slots are short) and clears it. Gather cleanup consumes the 3×4058
  (`QuestActObjItemGather.cs:49-58`) — the observed `meat 3->0` at both transitions.
- **ObjId trust.** The server trusts the client's `npcObjId` beyond existence: identity is re-derived as
  `TemplateId` and matched purely on template + status. A forged packet naming any spawned NPC of the
  report template completes a Ready quest from anywhere — no server-side check contradicts this. [INFERENCE
  from code absence, stated as absence: no range/alive/visibility/zone gate exists in
  `QuestManagerEvents.cs:22-70` or `QuestActConReportNpc.cs:40-62`.]

### B.2 Generic CompleteQuest bypass

`CSTryQuestCompleteAsLetItDonePacket` (0x0dd) checks only that the supplied objId matches `CurrentTarget`
(or is 0), then `TryCompleteQuestAsLetItDone` sets `Step = Reward` directly
(`CSTryQuestCompleteAsLetItDonePacket.cs:20-30`; `CharacterQuests.cs:742-756`). A genuine bypass path, but
**separate** — the bot never touches it (no references in bot code; `TurnIn` uses only the report branches).

### B.3 Human/client chain vs bot chain

Normal human chain: approach → `CSStartInteractionPacket` (dialog open; **explicit `// TODO: Distance-check`**,
  `CSStartInteractionPacket.cs:37-39` — the server does not gate even dialog range) → per-dialog
  `CSQuestTalkMadePacket` → `DoTalkMadeEvents` (`CSQuestTalkMadePacket.cs:20-28`) → `CSCompleteQuestContextPacket`
  → §B.1. Accept side mirrors this: `CSStartQuestContextPacket` → `AddQuestFromNpc` (existence + set as
  `CurrentTarget`, `CharacterQuests.cs:200-207`; deeper gates are data/level/supply checks in `AddQuest`,
  `CharacterQuests.cs:82-192` — no proximity either).

| Hop | Human client | Bot (`TurnInQuest`) | Same handler? |
|---|---|---|---|
| Reporter resolution | physically-present dialog target (client UI) | global `GetNpcByTemplateId` first match, any distance (`QuestBehavior.cs:613`; `WorldInstance.cs:476-479`) | n/a (resolution differs) |
| Approach / travel | player walks (client enforces reach to open dialog) | none — fires from ~85-95 m (§D) | — |
| Talk / dialog | `CSStartInteraction` + optional `CSQuestTalkMade` | none | — |
| Report event | `CSCompleteQuestContextPacket` → `DoReportEvents` | `ReportTurnIn` → `DoReportEvents` | **YES — identical** (`PlayerBotController.cs:126-130`) |
| Step drain | engine evaluation queue | manual 8-pass drain + queue | same `RunCurrentStep` |
| Reward / removal | pool distribution + `DropQuest` | same | **YES** |

---

## C. Semantics, giver, legality, Talk (Q3–Q6 — Sections 3, 6, 7, 9)

### C.1 `TurnInQuest` semantics: "complete Ready quest", not "player-valid interaction" (§3)

Signature `TurnInQuest(questId, npcObjId, selectedReward = -1, ...)` (`GameplayActor.cs:955-956`;
contract `IGameplayActor.cs:676-683`, documented as "the exact path CSCompleteQuestContextPacket takes").
`npcObjId` is used **only** to locate a world NPC whose `TemplateId` is forwarded (`QuestManagerEvents.cs:34-40`);
`selectedReward` is stored as `SelectedRewardIndex` (`QuestActConReportNpc.cs:57`). Matching keys are
(actId, NPC template id, quest readiness) — never objId continuity, distance, facing, dialog state, or
targeting. It is the same handler as the player path (§B.1/B.3 table).

### C.2 Giver 251: both start and completion (§6)

sqlite `compact.sqlite3`, read-only (kind enum `QuestComponentKind.cs:3-14` — Start=2, Progress=4, Ready=6,
Reward=8; components query returns ids 383/633/638/639 with kinds 2/4/6/8, all `or_unit_reqs=f`):

| Component | Act id / type | Detail → meaning |
|---|---|---|
| 383 Start | 333 `QuestActConAcceptNpc` / 77 | `quest_act_con_accept_npcs` 77 → **npc_id 3512** |
| 633 Progress | 10473 `QuestActObjItemGather` / 616 | gather **4058 ×3** (see G4) |
| 638 Ready | 457 `QuestActConReportNpc` / 104 | `quest_act_con_report_npcs` 104 → **npc_id 3512**, `use_alias=f` |
| 639 Reward | 36911 `QuestActSupplyItem` / 4075 | `quest_act_supply_items` 4075 → **item 18791 ×1** |

**3512 is both start-only acceptor and completion reporter** (accept match is acceptor-type+template,
`QuestActConAcceptNpc.cs:17-21`). Template row `npcs` 3512: `촌장 고트`, kind 1, level 5, faction 101,
non-aggressive. Exactly one spawner in world data: `(15655.91, 15172.51, 121.24)` (`npc_spawns.json:184417-184424`;
post-turn-in travel dest `(15655.3,15170.9)` corroborates, `game.log:3727`). Live objId at accept-time prehold:
44311 (both reports' `quest-prehold ... giverObjId=44311`). [INFERENCE] Turn-in-time reporter objId is unlogged
(actor drain logs no objId), but resolution is template-keyed so continuity is irrelevant to the outcome.

### C.3 Interaction legality across paths (§7)

- Metric: `MathUtil.CalculateDistance(loc, loc2, includeZAxis=false)` — flat 2D by default
  (`MathUtil.cs:332-361`). Bot verbs pass `false` (flat).
- Enforced ranges (bot contract + packet, for contrast): Talk 25 m flat reject (`GameplayActor.cs:1067-1068`);
  `InteractNpc` 25 m (`GameplayActor.cs:1166-1167`); `DiscoverQuests` 25 m (`GameplayActor.cs:1294-1296`,
  `MaxQuestDiscoverRange = MaxInteractRange = 25f`, `GameplayActor.cs:1270,1535`); merchant shop 3 m in packet
  (`CSBuyItemsPacket.cs:51-58`) mirrored as `MaxShopRange = 3f` (`GameplayActor.cs:2836,2857-2859`);
  loot 200 m `LootingContainer.MaxLootingRange` (`GameplayActor.cs:1742-1743`); NPC interact packet itself sets
  `CurrentInteractionObject`/`CurrentTarget` with **no** check (`CSInteractNPCPacket.cs:16-24`).
- Turn-in/report path: **no radius, no metric, no bounding-radius, no visibility/LOS, no zone, no NPC-service
  check at any of the three layers** (§A table). Positive evidence that the absences are known debt rather
  than verified-unneeded: `// TODO: Distance-check` (`CSStartInteractionPacket.cs:38`),
  `// TODO: Check doodad range?` (`QuestActConReportDoodad.cs:51`),
  `// TODO Verify: Does it actually have to be targeted?` (`QuestActConReportNpc.cs:24`), and
  `OnReportNpcArgs.Transform` annotated "чтобы проверять расстояние до него" ("to check the distance to it",
  `UnitEvents.cs:211-217`) — the distance payload is carried and never read by
  `QuestActConReportNpc.OnReportNpc`. The only liveness-adjacent quest check found is escort-specific
  (`QuestActCheckGuard.cs:10-14`), not on the report path. UNKNOWN (not searched exhaustively): whether any
  generic visibility subsystem would hide the NPC object itself at 85 m — resolution operates on the world
  registry, not perception.

### C.4 Talk-vs-turn-in: Talk need not precede completion (§9)

`GameplayActor.Talk` resolves the NPC (`GameplayActor.cs:1061-1063`), enforces 25 m (`GameplayActor.cs:1067-1068`),
sets `CurrentTarget` + broadcast (`GameplayActor.cs:1072-1073`), then fires `DoTalkMadeEvents` **once per active
quest carrying a talk-family objective** (`QuestActObjTalk`/`QuestActObjTalkNpcGroup`,
`GameplayActor.cs:1091-1105`) and drains those quests (`GameplayActor.cs:1111-1119`). Talk credit matching is
template/group membership in the engine fan-out (`QuestManagerEvents.cs:130-162`). A talk crediting nothing is
**rejected as void** (`GameplayActor.cs:1140-1143`); `InteractNpc` shares the talk-credit block
(`GameplayActor.cs:1201-1226`). Quest 251 has no talk acts (act inventory §C.2 / G4 §A.2), so **Talk at 3512 is
always void for 251** — and the report path is fully independent (`DoReportEvents` reads no Talk state;
`OnReportNpc` checks status + template only). The one targeting-adjacent gate, `ReportNpc.RunAct`'s
`CurrentTarget is Npc && TemplateId == NpcId` (`QuestActConReportNpc.cs:21-26`), is **bypassed by design** when
the report event fires first: the event sets `OverrideObjectiveCompleted=true`, which `RunAct` honors
(`QuestActConReportNpc.cs:25,57-60`). Server-side, Talk is client-UI convention, never a prerequisite.

---

## D. The 85 m turn-in: classification (§10)

### D.1 Facts (log-grounded)

- **char 56 (postready run):** wake `quest-56-639255607033044476` — `landed TurnInQuest (quest 251 completed
  by turn-in)` at actor `(15587.1,15120.3,130.8)` (`game.log:4344`). Giver spawner `(15655.91,15172.51)`
  → flat `√(68.8² + 52.2²) ≈ 86 m` ("85 m"). The next wake's sweep sees only a 3475 in-bubble
  (`raw=1 eval=[40579:3475:2.4:2.4]`, `game.log:4346-4347`) — the reporter was **not** within the 25 m
  perceive bubble when the turn-in landed.
- **char 55 (G7d run), same shape:** wake `quest-55-639255579865836418` — `landed TurnInQuest` at actor
  `(15582.1,15113.4,130.7)` (`game.log:3723`) → flat `≈ 95 m` from the same spawner. Preceding wake landed the
  3rd loot (`grant=1`, `game.log:3705-3706`), which drove Progress→Ready
  (`ItemGather.Finalize(616)` → `ReportNpc.Initialize(104)`, `game.log:3708-3709`); the turn-in wake then
  proposed (Ready now true) and won at priority 30.
- **Completion path (both):** `ReportNpc.Finalize(104)` → `SupplyItem.Initialize+Finalize(4075)` →
  accept/gather `FinalizeQuest` → `quest 251 removed` (`game.log:3715-3722`); reward item 18791×1 per detail
  row 4075 (mission-stated as landed; log proves the Reward step ran, sqlite proves its content).
  Meat `3->0` consumed by gather cleanup at completion (both reports' transition lines).
- **Resolution at distance:** reporter resolved by global template lookup (`QuestBehavior.cs:613`), dispatched
  through registry existence (`GameplayActor.cs:982-990`), matched by template + Ready status
  (`QuestActConReportNpc.cs:40-62`) — no hop measures distance.

### D.2 Verdict: **PLAYERBOT SEMANTIC SHORTCUT resting on a MISSING LEGALITY CHECK**

- **Not EXPECTED ENGINE SEMANTICS.** The engine's own artifacts show proximity was intended: the report args
  carry the NPC `Transform` expressly for a distance check (`UnitEvents.cs:211-217`), and two TODOs mark the
  missing gates (`CSStartInteractionPacket.cs:38`; `QuestActConReportDoodad.cs:51`). A human client physically
  satisfies reach (dialog open) before the packet can name the NPC; nothing in §B suggests distance-blind
  completion is the designed semantic.
- **Not a QUESTBEHAVIOR POLICY GAP alone.** The proposal faithfully mirrors the engine's acceptance condition
  (Ready + reporter spawned); inventing a bot-side range gate would impose policy the engine does not hold for
  any client — the gap is not in proposal policy but in the absence of an engine gate to mirror.
- **The shortcut (bot):** `GetNpcByTemplateId` reporter resolution + `TurnInQuest` dispatch with zero approach,
  dialog, Talk, or range discipline — while the bot's own sibling verbs (Talk/Interact/Discover) all enforce
  25 m `MaxInteractRange`. Exact path: `QuestBehavior.cs:613-617` → `BotDecisionSelector` priority 30
  (`QuestDecisionScenario.cs:49`; `BotDecisionProposal.cs:283-290`) → `QuestBehavior.cs:1081-1082` →
  `GameplayActor.cs:955-956,970-1030` (existence-only resolve `:982-990`).
- **The missing check (engine):** `QuestManagerEvents.cs:22-70` (no proximity/alive/visibility/zone/service
  validation; silent return on null only) compounded by `QuestActConReportNpc.cs:40-62` (template + status
  only; `args.Transform` unread). Any client packet — human or bot — enjoys the same distance-blind
  completion today.

---

## E. Data-vs-inference ledger

- **Code facts (read):** all file:line cites above; G4 §A–B for act shapes, credit authority, drop source
  (not re-quoted here beyond §C.2).
- **Data facts (sqlite/world JSON, read-only):** 251 components/acts/detail rows (§C.2 table); 3512 template
  row; single 3512 spawner position; detail 4075 → 18791×1; `let_it_done=f, score=0`.
- **Log facts:** turn-in wakes, positions, pass sequence, removal (`game.log` lines cited); both JSON reports'
  trajectories (meat/objective/status) and their observe-only verdicts.
- **[INFERENCE]:** flat-distance figures (2D math on spawner vs actor positions; live NPC may wander metres —
  order of magnitude unaffected); live reporter objId at turn-in (unlogged; irrelevant — matching is
  template-keyed); "forged packet from anywhere" (absence-of-check reading — validated by code absence at the
  cited lines, not by live packet forgery); retail design intent (TODO comments + unused Transform — strong but
  still intent-reading); reward-18791-landed (mission-stated + reward-step log proof + sqlite content;
  item-level grant line not present in harvested log text).
- **UNKNOWN:** whether perception/visibility subsystems would even expose the 3512 instance at 85 m to a real
  client (bot resolution bypasses perception entirely, so this does not affect the classification); start
  component unit_reqs content (G4 §A.4, untouched — irrelevant to turn-in).
