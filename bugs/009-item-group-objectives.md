# BUG-009 — QuestActObjItemGroupGather / QuestActObjItemGroupUse stall (9 quests)

- **Status**: FIXED (branch `fix/quest-item-group-objectives`, 2026-08-04)
- **Severity**: Medium (9 act rows stall; 4 live quests + 1 test quest, 4 orphaned contexts)
- **Component**: Quest engine — `QuestActObjItemGroupGather` / `QuestActObjItemGroupUse` acts
- **Discovered via**: M1-2 stub catalog (scorecard exploration) — `RunAct` passed through to `base.RunAct`

## Symptom

Quests whose Progress component contains a `QuestActObjItemGroupGather` or
`QuestActObjItemGroupUse` act can never advance past that objective. `RunAct` fell
through to `QuestActTemplate.RunAct` (`QuestActTemplate.cs:95-99`), which logs
`not implemented!` and returns false — despite the doc comment on that method saying
*"descendents should never call base()"*. The objective never completes and the step
machine stalls.

Prod data check (compact.sqlite3, `quest_acts` → `quest_components` → `quest_contexts`):

| act type | act rows | contexts | live quests |
|---|---|---|---|
| QuestActObjItemGroupGather | 7 (detail ids 2, 5, 6, 20, 23, 25, 26) | 1955, 1957, 2140, 5490, 6578, 6600, 6615 | **5490** 신기루 섬을 깨끗하게, **6578** 이이제이, **6600** 보다 더 강력한 힘, **6615** 신의 방패 정식 대원이 되다! |
| QuestActObjItemGroupUse | 2 (detail ids 3, 7) | 1958, 5489 | **5489** test_time (test quest) |

Contexts 1955, 1957, 2140, 1958 do not exist in `quest_contexts` — orphaned act rows
(no template, never loadable). Live impact: **4 gather quests + 1 use test quest**.
Gather detail 20 (quest 5490) has `cleanup = 1`; gather details 2/5/6/20 (quests
1955/1957/2140/5490) have `destroy_when_drop = 1` (items removed when the quest is
dropped), so cleanup/drop handling matters for real data.

## Root cause

Both classes were stubs: `RunAct` → `base.RunAct`. No event registration, no
`CountsAsAnObjective`, no objective counting. The sibling single-item acts
(`QuestActObjItemGather`, `QuestActObjItemUse`) implement the real pattern; the group
variants were never wired even though the event plumbing existed
(`UnitEvents.OnItemGroupGather`, `QuestManagerEvents.DoItemsAcquiredEvents` already
fires group-gather events with `ItemGroupId`, and `QuestManager._groupItems` /
`GetGroupItems` / `CheckGroupItem` index `quest_item_group_items`).

## Fix

1. **`QuestActObjItemGroupGather`** (UnusedActs, class stays in place):
   - `CountsAsAnObjective => true`; objective = **sum of inventory counts over every
     item id in the group** (`QuestManager.Instance.GetGroupItems(ItemGroupId)`), so
     any group member counts.
   - `RunAct` recounts from inventory and returns `GetObjective >= Count` — same
     shape as `QuestActObjItemGather.RunAct`.
   - Registers/unregisters `OnItemGroupGather` (event already carries `ItemGroupId`);
     handler re-syncs the objective from inventory (handles both gain and loss).
   - `QuestCleanup`/`QuestDropped` mirror the ItemGather sibling, consuming
     `min(Objective, MaxObjective)` spread across group members (covers quest 5490's
     `cleanup = 1` and the `drop_when_destroy` rows).
2. **`QuestActObjItemGroupUse`** (UnusedActs, class stays in place):
   - `CountsAsAnObjective => true`; `RunAct` returns `currentObjectiveCount >= Count`
     — same shape as `QuestActObjItemUse.RunAct`.
   - Registers/unregisters the existing `OnItemUse` event (the only item-use event
     actually fired, `Character.ItemUse*`); handler counts a use only when
     `QuestManager.Instance.CheckGroupItem(ItemGroupId, args.ItemId)` — group-expanded
     version of the ItemUse sibling's `args.ItemId == ItemId` check. (The
     `OnItemGroupUse` event delegate exists but nothing fires it; using `OnItemUse`
     + group membership keeps the change to the two act classes.)
3. **Untouched**: the loader (`QuestManager.Load`), the
   `quest_act_obj_item_group_gathers`/`quest_act_obj_item_group_uses` tables, and the
   UnusedActs folder placement.

## Files changed

- `AAEmu.Game/Models/Game/Quests/UnusedActs/QuestActObjItemGroupGather.cs`
- `AAEmu.Game/Models/Game/Quests/UnusedActs/QuestActObjItemGroupUse.cs`
- `AAEmu.UnitTests/Game/Models/Game/Quests/Acts/QuestActObjItemGroupGatherTests.cs` (new)
- `AAEmu.UnitTests/Game/Models/Game/Quests/Acts/QuestActObjItemGroupUseTests.cs` (new)

## Tests

New test classes (fail-before/pass-after, 14 tests total):

Gather (`QuestActObjItemGroupGatherTests`, group 1 = {100, 101}, Count 2):
- `RunAct_WithEnoughOfGroupItemA_ReturnsTrue` — failed before (stub false), passes after
- `RunAct_WithEnoughOfGroupItemB_ReturnsTrue` — group member B must count too
- `RunAct_GroupCountsSumAcrossDifferentGroupItems_ReturnsTrue` — 1×A + 1×B = 2
- `RunAct_WithOnlyNonGroupItems_ReturnsFalse`
- `RunAct_WithPartialGroupItems_ReturnsFalse`
- `RunAct_WithUnknownGroup_ReturnsFalse`
- `OnItemGroupGather_MatchingGroup_UpdatesObjectiveToInventoryTotal` — event handler
  re-syncs objective after a new acquisition
- `OnItemGroupGather_NonMatchingGroup_DoesNotUpdateObjective`

Use (`QuestActObjItemGroupUseTests`, group 1 = {100, 101}, Count 2):
- `OnItemUse_GroupItemA_CountsTowardObjective` — 2 uses → objective 2
- `OnItemUse_GroupItemB_CountsTowardObjective` — group member B must count too
- `OnItemUse_NonGroupItem_DoesNotCount`
- `RunAct_WithObjectiveCountMet_ReturnsTrue` / `RunAct_WithObjectiveCountNotMet_ReturnsFalse`
- `FinalizeAction_UnregistersEventHandler`

Test infrastructure: seeds the `QuestManager` singleton (`_groupItems` index) via
reflection, builds a real `Character` + a real `Inventory` (uninitialized-object +
backing-field injection to bypass the ItemManager singleton), and fires the real
`UnitEvents` delegates — no mocks on the code under test.

Gate: Release build 0 errors · compiler-check 0 errors · full unit suite green
(baseline 1082 + 14 new = 1096).

## Upstream note

Lane gate: NO upstream PR. Upstream develop still carries both classes as stubs
(verified 2026-08-04). Stays on joshhmann/AAEmu fork until Josh decides.

## Verification on prod (once merged to develop + deployed)

1. `ssh aaemu && cd /root/AAEmu && git pull fork develop && docker compose up -d --build game`
2. Quest 5490 (신기루 섬을 깨끗하게): gather items from group 9 (28557/28558/29288) →
   objective advances; on completion the gathered items are removed (cleanup = 1).
3. Quest 6578/6600/6615: gather any group item (groups 14/15/16) → objective advances.
4. `QuestActObjItemGroupUse` (quest 5489 test_time, group 10 = {8518, 29173}):
   using either item increments the objective.
