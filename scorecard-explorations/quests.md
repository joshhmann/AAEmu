# QUESTS Domain Report — AAEmu (ArcheAge 1.2 fork, .NET 10)

Date: 2026-08-03 · Explorer deep-dive · Repo: /root/aaemu-dev

## 1. Quest engine, end-to-end

### Load path (all in `QuestManager`, no separate `QuestGameData`)
`QuestManager.Load()` — `AAEmu.Game/Core/Managers/QuestManager.cs:234-265` — is a single monolithic sqlite loader:
1. `LoadQuestSupplies` (:420) — `quest_supplies` → `_supplies[level]`
2. `LoadQuestContexts` (:480) — `quest_contexts` → `_questTemplates`; **skips category 45 (tutorial) quests** (:508-510)
3. `LoadQuestComponents` (:440) — `quest_components` ordered by `(quest_context_id, component_kind_id, id)` → `QuestComponentTemplate` attached to quest + `_componentTemplates`
4. `LoadBaseQuestActs` (:380) — `quest_acts` → thin `QuestActTemplate{ActId, DetailId, DetailType}` cached in `_actsByComponent[componentId]` + `_actsBaseByActId`
5. `LoadDetailQuestActTemplates` (:531) — **64 separate `SELECT * FROM quest_act_*` blocks** instantiate the typed act: `GetComponentByActTemplate(detailType, id)` (:1917) does a linear scan of `_actsBaseByActId` to find the owning component, then `AddActTemplate` (:518) links `ParentQuestTemplate`, assigns `ActId`, appends to `ParentComponent.ActTemplates`, and registers in `_actTemplatesByDetailType[className][detailId]`.
6. `LoadQuestItemGroups` (:353) + `LoadQuestMonsterNpcs` (:327) — `_groupItems` / `_groupNpcs`
7. `UpdateQuestComponentActs` (:192) — assigns `ThisComponentObjectiveIndex` (objective counter index) and `ThisSelectiveIndex` per act.

`_actTemplatesByDetailType` is seeded by reflection over namespace `AAEmu.Game.Models.Game.Quests.Acts` (:239-241), keyed by class name — **this is the implicit `act_detail_type` → class map**. All 65 act classes (50 `Acts/` + 15 `UnusedActs/`) are in that namespace, so no load-time KeyNotFound.

### Runtime model
`Quest` (partial; `Quest.cs` + `NewQuestCode.cs`) → `QuestSteps[QuestComponentKind]` → `QuestStep` → `QuestComponent{}` → `QuestAct`. `QuestComponent.RunComponent` (`QuestComponent.cs:45-79`): OR-rollup for Start/Ready (multi-starter / multi-report), AND-rollup for Supply/Progress/Reward; `IsCurrentlyActive` gated by `UnitRequirementsGameData.CanComponentRun` (`GameData/UnitRequirementsGameData.cs:20`). `QuestStep.RunComponents` (`QuestStep.cs:52-132`) adds score-quest and LetItDone overrides, then `DistributeRewards(true)`.

### Accept
`CSStartQuestContextPacket` → `CharacterQuests.AddQuestFromNpc/Doodad/Sphere` → `AddQuest` (`CharacterQuests.cs:69-159`): dup/completed checks, `CanAcceptSupplyItems`, unit-req gate on start components → `new Quest` → `StartQuest()` sends `SCQuestContextStartedPacket` → `RunCurrentStep()` → `QuestInitialized()` (arms `RequestEvaluationFlag` → `QuestManager.EnqueueEvaluation`).

### Progress (event-driven, evaluation-queue)
World events funnel through `CharacterEvents`/`UnitEvents` → `QuestManagerEvents.DoXxxEvents` (`QuestManagerEvents.cs`) → subscribed act handlers (`InitializeAction`/`FinalizeAction` register/unregister, e.g. `QuestActObjMonsterHunt.cs:31`): `AddObjective/SetObjective` (`QuestActTemplate.cs:119-174`, capped by `MaxObjective()`) → `RequestEvaluation()` → `EnqueueEvaluation` → `QuestManagerRunQueueTask` → `DoQueuedEvaluations` (:157) → `RunCurrentStep` → `RunComponents` → `SCQuestContextUpdatedPacket`. Packet is pure status mirror (`SCQuestContextUpdatedPacket.cs`); objectives are `int[5]` (max 5 objective acts per quest).

### Complete
`CSCompleteQuestContextPacket` → `DoReportEvents` (QuestManagerEvents.cs:22-70) → `OnReportNpc/OnReportDoodad` → `QuestActConReportNpc.OnReportNpc` (`QuestActConReportNpc.cs:53-84`) verifies objective status, sets `SelectedRewardIndex`, `OverrideObjectiveCompleted = true`, bumps `Step = Ready`, requests evaluation → `GoToNextStep` (`NewQuestCode.cs:95-161`): Start→None→Supply→Progress→Ready→Reward; on Reward: `SetCompletedQuestFlag` (bit-block `quest_id/64`), `DropQuest`, `SCQuestContextCompletedPacket`.

### Persistence
`CharacterQuests.Load/Save` (MySQL `quests` / `completed_quests`): `Quest.WriteData/ReadData` serializes `Objectives[5]`, `Step`, acceptor type/id, `Time` (timed quests).

## 2. `quest_act_*` table coverage (65 tables)

| Group | Loaded | Notes |
|---|---|---|
| check_* (5) | ✅ all | CheckDistance/Buff live in `UnusedActs/` but **are instantiated** by the loader |
| con_accept_* (13) | ✅ all | ✅ kill-accept bug FIXED 2026-08-03 (BUG-006, fix/quest-kill-acceptor) |
| con_* (4) | ✅ all | |
| etc_* (1) | ✅ | |
| obj_* (30) | ✅ 29 loaded + 1 verified dormant | `quest_act_obj_aliases` = id→name dict, ZERO live refs in 1.2 data (verdict 2026-08-04, see §2 note) |
| supply_* (11) | ✅ all | |

- **All 15 `UnusedActs` classes are actually instantiated by the loader** (verified `new QuestActXxx(` for each) — the "Unused" label means stub logic, not unloaded.
- **CORRECTED 2026-08-04 (Tai verdict, live evidence against prod compact.sqlite3):** `quest_act_obj_aliases` is a DORMANT id→name dictionary — **0 rows** across all 27 act-obj tables have `use_alias=1`, **0** `quest_acts` rows have `act_detail_type=QuestActObjAlias` (26,886 acts total), and nothing in AAEmu.Game SELECTs it. The earlier "2,746 dangling FKs" read was wrong: the rows are the dictionary itself and nothing in the 1.2 data references them. (The 17,735 act_detail_id "collisions" with alias ids are per-table id-space restart, not FKs.) `use_alias` is read at QuestManager.cs:949/987/1031/1062/1084/1107/… — column exists, every row is 0. **No loader needed.**
- Non-act quest tables: `quest_contexts`, `quest_components`, `quest_acts`, `quest_supplies`, `quest_item_group_items`, `quest_monster_npcs` loaded. **Zero refs**: `quest_cameras`, `quest_chat_bubbles`, `quest_component_texts`, `quest_context_texts`, `quest_item_groups`, `quest_names`, `quest_mail_*` (5), `quest_tasks`/`quest_task_quests` (DB has 0 rows anyway), `quest_monster_groups`.

## 3. Upstream-broken quests (issue # → real quest id)

- **#1208 → quest 1119** ("Arcum Iris", Haranya): Start `QuestActConAcceptNpc(npc 2237)` + Ready `QuestActConReportNpc(npc 5697)`. Clean single-starter shape; reported breakage is a housing/farm issue, not engine.
- **#1255 → quest 922**: Progress `QuestActObjInteraction(wi_id=19, doodad 1349, count 3)`. Bug: "Explosives Pit doodad summons Enraged Stone Elemental but quest doesn't progress." `QuestActObjInteraction.cs:60` contains an unresolved `TODO Verify: Is Phase here used to move the Doodad to that phase` — the doodad's phase-change interaction path is the suspect, plus `wi_id` matching on interact.
- **#1257 → quest 111**: gather 5× item 17471 from doodad 2961 (`highlight_doodad_phase=6676`). Bug: "only 3 Red Poppy doodads spawn, no respawn." Server-side doodad spawner/phase logic, not act logic.
- **#1329 → quest 3889**: Progress `QuestActObjItemUse(item 24165, count 1, highlight doodad 4614)`, skill 18017, SphereQuest 789 → **quest 3889** (confirmed `sphere_quests` 789 = quest 3889 trigger 1 — the "unrelated quest" claim in the issue is stale). Item-use objectives depend on item-use event wiring + sphere gating to advance.
- **#1450 → quest 3447**: Progress `QuestActObjInteraction(doodad 4252, wi_id 19, alias 1883)`. Complaint is doodad 4252 disappearing and mob-spawn lifetime (spawner 61649 via skill 16790) — world/doodad lifecycle, outside the act engine.

**Root-cause class found in code (quest 1208 + the kill-acceptor family):** `QuestActConAcceptNpcKill.RunAct` is a copy-paste of `QuestActConAcceptNpc.RunAct` — it returns `quest.QuestAcceptorType == QuestAcceptorType.Npc && quest.AcceptorId == NpcId` (QuestActConAcceptNpcKill.cs:19-25). There is **no code path that ever adds a quest with a kill acceptor**: the only kill-triggered starter, `npc.Template.EngageCombatGiveQuestId` (`Unit.cs:1636-1640`), calls `AddQuestFromNpc` → `QuestAcceptorType.Npc` with the aggro'd NPC's template id. So any quest whose Start component holds `QuestActConAcceptNpcKill` can never have its start acts pass. `DoOnMonsterHuntEvents` (`QuestManagerEvents.cs:169-203`) also fires no accept-starter — kills are only wired to `OnMonsterHunt`/`OnMonsterGroupHunt`/`OnZoneKill` objectives, never to quest acceptance.

**✅ FIXED 2026-08-03 (branch `fix/quest-kill-acceptor`, BUG-006):** added `QuestAcceptorType.Kill`,
wired `DoOnMonsterHuntEvents` to start matching quests with the Kill acceptor (index built in
`QuestManager.Load`), fixed `RunAct` to match. Live data check: **380 quests** had ALL Start acts
as kill-accepts (182, 205, 556, 913, 1057, 1079, 1082, 1089, 1165, 1208...) — all now startable.
Quest 1119 (upstream #1208) is actually a plain Npc-accept quest (Npc 2237), not part of this family.

## 4. What fixing the most common failure classes takes

1. ~~**Kill-accept starters (highest impact, ~40 quests)**~~ — **✅ DONE 2026-08-03** (BUG-006, branch `fix/quest-kill-acceptor`): added `QuestAcceptorType.Kill`, wired `Npc.cs` death path (`DoOnMonsterHuntEvents`, `Npc.cs:877/986/1019`) to check `QuestActConAcceptNpcKill` via a load-time index and call `AddQuest(..., Kill, npc.TemplateId)`, and fixed `QuestActConAcceptNpcKill.RunAct` to match. Live data: 380 quests affected, not ~40.
2. ~~**Load `quest_act_obj_aliases`**~~ — **✅ NO-OP 2026-08-04**: investigated with live data (verdict above) — dormant table, zero references in 1.2 data. No loader, no reconciliation. Revisit only if a future data pack sets `use_alias=1`.
3. **Audit stub acts**: `QuestActCheckCompleteComponent`..`QuestActConAcceptComponent` etc. (UnusedActs) return `true`/`false` without real checks; each needs a gameplay decision (must-return-`false` vs functional), since quests relying on them either auto-complete or stall silently.
4. **Doodad-interaction/phase objectives (`QuestActObjInteraction` wi_id+phase TODOs, `QuestActObjItemUse` skill-use gating):** verify the doodad phase-change event source reaches `DoDoodadInteractionEvents` and that `Phase`/`highlight` semantics are honored — this is the observed failure in 922/3889/3447.
5. **Sanity tooling:** a startup verifier that cross-checks every `quest_acts.act_detail_type` against the class registry and every referred detail-id against its loaded detail table (catches orphaned act types immediately; dormant tables like `quest_act_obj_aliases` are informational, not errors).

## Findings summary

- Traced the full engine: `QuestManager.Load` (7-stage loader), runtime `Quest→QuestStep→QuestComponent→QuestAct`, accept/progress/complete packet handlers (`CSStartQuestContextPacket`, `CSCompleteQuestContextPacket`, `CSQuestTalkMadePacket`, `CSDropQuestContextPacket`), event queue, persistence.
- Table coverage: 64/65 `quest_act_*` SELECTed (the 1 unloaded, `quest_act_obj_aliases`, verified DORMANT 2026-08-04 — no live references in 1.2 data); produced exact missing-table, missing-act-class, and missing-non-act findings.
- Queried live sqlite on 192.168.0.165 for quests 1208/1257/1329/922/111/3889/3447/1119 and pulled real upstream issue bodies; identified the copy-paste `QuestActConAcceptNpcKill` acceptor bug as the cleanest root-cause across the 40-quest kill-starter family.
