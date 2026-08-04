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
| con_accept_* (13) | ✅ all | bug: `QuestActConAcceptNpcKill` logic is broken (see §3) |
| con_* (4) | ✅ all | |
| etc_* (1) | ✅ | |
| obj_* (30) | ❌ **`quest_act_obj_aliases` never SELECTed** (0 refs in `AAEmu.Game`; 2,746 rows in DB) | 29 loaded |
| supply_* (11) | ✅ all | |

- **All 15 `UnusedActs` classes are actually instantiated by the loader** (verified `new QuestActXxx(` for each) — the "Unused" label means stub logic, not unloaded.
- **2,746 DB rows reference aliases via `use_alias`/`quest_act_obj_alias_id`** (e.g. `QuestManager.cs:905-906, 943-944, 1018-1019, 1040-1041, 1041, 1260-1261…`), but `quest_act_obj_aliases` data never loads — dangling FKs everywhere.
- Non-act quest tables: `quest_contexts`, `quest_components`, `quest_acts`, `quest_supplies`, `quest_item_group_items`, `quest_monster_npcs` loaded. **Zero refs**: `quest_cameras`, `quest_chat_bubbles`, `quest_component_texts`, `quest_context_texts`, `quest_item_groups`, `quest_names`, `quest_mail_*` (5), `quest_tasks`/`quest_task_quests` (DB has 0 rows anyway), `quest_monster_groups`.

## 3. Upstream-broken quests (issue # → real quest id)

- **#1208 → quest 1119** ("Arcum Iris", Haranya): Start `QuestActConAcceptNpc(npc 2237)` + Ready `QuestActConReportNpc(npc 5697)`. Clean single-starter shape; reported breakage is a housing/farm issue, not engine.
- **#1255 → quest 922**: Progress `QuestActObjInteraction(wi_id=19, doodad 1349, count 3)`. Bug: "Explosives Pit doodad summons Enraged Stone Elemental but quest doesn't progress." `QuestActObjInteraction.cs:60` contains an unresolved `TODO Verify: Is Phase here used to move the Doodad to that phase` — the doodad's phase-change interaction path is the suspect, plus `wi_id` matching on interact.
- **#1257 → quest 111**: gather 5× item 17471 from doodad 2961 (`highlight_doodad_phase=6676`). Bug: "only 3 Red Poppy doodads spawn, no respawn." Server-side doodad spawner/phase logic, not act logic.
- **#1329 → quest 3889**: Progress `QuestActObjItemUse(item 24165, count 1, highlight doodad 4614)`, skill 18017, SphereQuest 789 → **quest 3889** (confirmed `sphere_quests` 789 = quest 3889 trigger 1 — the "unrelated quest" claim in the issue is stale). Item-use objectives depend on item-use event wiring + sphere gating to advance.
- **#1450 → quest 3447**: Progress `QuestActObjInteraction(doodad 4252, wi_id 19, alias 1883)`. Complaint is doodad 4252 disappearing and mob-spawn lifetime (spawner 61649 via skill 16790) — world/doodad lifecycle, outside the act engine.

**Root-cause class found in code (quest 1208 + the 40-quest kill-acceptor family):** `QuestActConAcceptNpcKill.RunAct` is a copy-paste of `QuestActConAcceptNpc.RunAct` — it returns `quest.QuestAcceptorType == QuestAcceptorType.Npc && quest.AcceptorId == NpcId` (QuestActConAcceptNpcKill.cs:19-25). There is **no code path that ever adds a quest with a kill acceptor**: the only kill-triggered starter, `npc.Template.EngageCombatGiveQuestId` (`Unit.cs:1636-1640`), calls `AddQuestFromNpc` → `QuestAcceptorType.Npc` with the aggro'd NPC's template id. So any quest whose Start component holds `QuestActConAcceptNpcKill` (40 quests in DB, including 1208 with npcs 5006/5007/5008) can never have its start acts pass. `DoOnMonsterHuntEvents` (`QuestManagerEvents.cs:169-203`) also fires no accept-starter — kills are only wired to `OnMonsterHunt`/`OnMonsterGroupHunt`/`OnZoneKill` objectives, never to quest acceptance.

## 4. What fixing the most common failure classes takes

1. **Kill-accept starters (highest impact, ~40 quests):** add a `QuestAcceptorType.Kill` (or reuse Npc + acceptor=kill flag), wire `Npc.cs` death path (`DoOnMonsterHuntEvents` call site, `Npc.cs:877/986/1019`) to also check `QuestActConAcceptNpcKill` templates (`_actTemplatesByDetailType["QuestActConAcceptNpcKill"]`) and call `AddQuest(..., Kill, npc.TemplateId)`; then fix `QuestActConAcceptNpcKill.RunAct` to match that acceptor.
2. **Load `quest_act_obj_aliases`:** add one loader block to `LoadDetailQuestActTemplates` (`QuestManager.cs`), populate `_actTemplatesByDetailType["QuestActObjAlias"]`, and resolve FK lookups where `use_alias=true` acts reference alias ids (2,746 rows) so alias/UI-only acts resolve and don't dangle.
3. **Audit stub acts**: `QuestActCheckCompleteComponent`..`QuestActConAcceptComponent` etc. (UnusedActs) return `true`/`false` without real checks; each needs a gameplay decision (must-return-`false` vs functional), since quests relying on them either auto-complete or stall silently.
4. **Doodad-interaction/phase objectives (`QuestActObjInteraction` wi_id+phase TODOs, `QuestActObjItemUse` skill-use gating):** verify the doodad phase-change event source reaches `DoDoodadInteractionEvents` and that `Phase`/`highlight` semantics are honored — this is the observed failure in 922/3889/3447.
5. **Sanity tooling:** a startup verifier that cross-checks every `quest_acts.act_detail_type` against the class registry and every referred detail-id against its loaded detail table (catches missing tables like `quest_act_obj_aliases` and orphaned acts immediately).

## Findings summary

- Traced the full engine: `QuestManager.Load` (7-stage loader), runtime `Quest→QuestStep→QuestComponent→QuestAct`, accept/progress/complete packet handlers (`CSStartQuestContextPacket`, `CSCompleteQuestContextPacket`, `CSQuestTalkMadePacket`, `CSDropQuestContextPacket`), event queue, persistence.
- Table coverage: 64/65 `quest_act_*` SELECTed; produced exact missing-table, missing-act-class, and missing-non-act findings.
- Queried live sqlite on 192.168.0.165 for quests 1208/1257/1329/922/111/3889/3447/1119 and pulled real upstream issue bodies; identified the copy-paste `QuestActConAcceptNpcKill` acceptor bug as the cleanest root-cause across the 40-quest kill-starter family.
