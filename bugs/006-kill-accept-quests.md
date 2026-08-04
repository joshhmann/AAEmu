# BUG-006 — Kill-acceptor quests can never start

- **Status**: FIXED (branch `fix/quest-kill-acceptor`, 2026-08-03)
- **Severity**: High (380 quests completely unstartable)
- **Component**: Quest engine — `QuestActConAcceptNpcKill` act / kill event path
- **Discovered via**: quests explorer deep-dive (`scorecard-explorations/quests.md`), Track 1 of the quest fix plan

## Symptom

Quests whose Start component contains a `QuestActConAcceptNpcKill` act can never start.
The client offers the quest, but the server never progresses it past Start — the quest
sits stuck. Live data check on prod compact.sqlite3: **380 quests** have ALL their Start
acts as `QuestActConAcceptNpcKill` (e.g. 182, 205, 556, 913, 1057, 1079, 1082, 1089,
1165, 1208), across 1043 distinct NPCs (all resolving in `npcs`).

## Root cause

`QuestActConAcceptNpcKill.RunAct` (`AAEmu.Game/Models/Game/Quests/Acts/QuestActConAcceptNpcKill.cs:19-25`)
is a copy-paste of `QuestActConAcceptNpc.RunAct` — it checks
`quest.QuestAcceptorType == QuestAcceptorType.Npc && quest.AcceptorId == NpcId`.
But no code path ever adds a quest with a *kill* acceptor:

- `QuestAcceptorType` (Static/QuestAcceptorType.cs) had no `Kill` value.
- The only combat-triggered starter, `npc.Template.EngageCombatGiveQuestId`
  (`Unit.cs:1636-1640`), calls `AddQuestFromNpc` → acceptor `Npc`.
- `DoOnMonsterHuntEvents` (`QuestManagerEvents.cs:169-203`) only fired
  OnMonsterHunt / OnMonsterGroupHunt / OnZoneKill — never a quest offer.

So the Start component's OR-rollup (`QuestComponent.RunComponent`) always returned false
for these quests and the step machine never advanced.

## Fix

1. **`QuestAcceptorType.Kill = 7`** added (Static/QuestAcceptorType.cs).
2. **`QuestActConAcceptNpcKill.RunAct`** now matches
   `QuestAcceptorType.Kill && quest.AcceptorId == NpcId` (log level Warn→Trace to match
   the Npc sibling).
3. **Kill-path wiring**: `QuestManager.BuildKillAcceptQuestIndex()` builds a
   NpcId → questIds lookup (Start-component kill-accept acts only) during `Load()`;
   `DoOnMonsterHuntEvents` now starts eligible quests for the credited player via
   `AddQuest(questId, false, QuestAcceptorType.Kill, npc.TemplateId)` — the same
   server-driven pattern `EngageCombatGiveQuestId` uses on aggro. Runs for every kill
   credit path (killer fallback Npc.cs:877, eligible players :986, tag-share :1019).
4. **Defensive guard**: `AddQuestFromNpc` returns false if the NPC object no longer
   exists (a client accept for a just-killed/despawned mob previously NRE'd).

## Files changed

- `AAEmu.Game/Models/Game/Quests/Static/QuestAcceptorType.cs`
- `AAEmu.Game/Models/Game/Quests/Acts/QuestActConAcceptNpcKill.cs`
- `AAEmu.Game/Core/Managers/QuestManager.cs` (index + accessor)
- `AAEmu.Game/Core/Managers/QuestManagerEvents.cs` (kill path offer)
- `AAEmu.Game/Models/Game/Char/CharacterQuests.cs` (null guard)
- `AAEmu.UnitTests/Game/Models/Game/Quests/Acts/QuestActConAcceptNpcKillTests.cs` (new)

## Tests

New `QuestActConAcceptNpcKillTests` (MethodName_Scenario_ExpectedResult):
- `RunAct_WithKillAcceptorAndMatchingNpcId_ReturnsTrue` — failed before fix, passes after
- `RunAct_WithKillAcceptorAndDifferentNpcId_ReturnsFalse`
- `RunAct_WithNpcAcceptor_ReturnsFalse` — failed before fix (old copy-paste bug), passes after
- `RunAct_WithUnknownAcceptor_ReturnsFalse`

Gate: Release build 0 errors · compiler-check 0 errors · 1082/1082 unit tests pass.

## Upstream note

Lane gate: NO upstream PR. Stays on joshhmann/AAEmu fork until Josh decides. Upstream
issue family #1208/#1255-#1282/#1329/#1450 are mostly other quest classes (doodad
interactions, spawns); #1208's quest 1119 is a plain Npc-accept quest (Npc 2237), not
part of this family — this fix is the largest single unstick (~380 quests).

## Verification on prod (once merged to develop + deployed)

1. `ssh aaemu && cd /root/AAEmu && git pull fork develop && docker compose up -d --build game`
2. Kill one of the 380 mobs (e.g. quest 182's NPCs 527/528/529/530/4941) with a fresh
   character → quest appears in journal, Start step passes, Progress begins.
3. `/quest list` shows Step(Progress) instead of stalling at Step(Start).
