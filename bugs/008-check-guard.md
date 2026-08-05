# BUG-008 — QuestActCheckGuard silently auto-completes (6 escort/protect quests)

- **Status**: FIXED (branch `fix/quest-check-guard`, 2026-08-04)
- **Severity**: Medium-High (6 quests' escort/protect objective always passes — silent false positive)
- **Component**: Quest engine — `QuestActCheckGuard` act
- **Discovered via**: stub-act audit (`scorecard-explorations/stub-acts.md` §1), M1-2, roadmap priority 1

## Symptom

`QuestActCheckGuard.RunAct` returned `true` unconditionally. Any quest whose progress
component contains a guard check (escort/protect objective — "keep NPC X alive") could
advance and auto-complete without the guard being alive, present, or even spawned.
Players see the escort objective tick off instantly with no escort happening.

Live data check on prod compact.sqlite3 (r208088): **6 acts / 6 components / 6 quest
contexts** use `QuestActCheckGuard`:

| act_id | detail_id | guard npc_id | guard npc (npcs.name) | comp_id | kind | ctx_id | ctx name (quest_contexts.name) | lvl |
|--------|-----------|--------------|----------------------|---------|------|--------|--------------------------------|-----|
| 5639   | 45        | 2964         | 비나타의 사냥개 래피 (Vinata's hound Rapi) | 3971 | 5 (Report) | 745 | *(missing quest_contexts row)* | — |
| 11365  | 55        | 6059         | 염색한 갈색 산양 (dyed brown goat, quest-summon) | 6306 | 4 (Progress) | 1421 | *(missing quest_contexts row)* | — |
| 12289  | 95        | 3138         | 클리페 (Clipe) | 6547 | 2 (Start) | 1313 | 말동무 (companion) | 27 |
| 12779  | 96        | 4617         | 율리한 (Yulihan) | 5064 | 4 (Progress) | 1033 | 기억과 쇠 골렘 (memory & iron golem) | 9 |
| 13429  | 102       | 7548         | 가우타마 (Gautama) | 8802 | 4 (Progress) | 1897 | (구 불볕황야)사라진 가우타마 (vanished Gautama) | 13 |
| 20951  | 116       | 9846         | 사티쉬 (Satish) | 15273 | 4 (Progress) | 3656 | 뜨거운 물이 좋아 (loves hot water) | 21 |

Notes from the data:
- 5 of 6 guard NPCs are `npc_kind_id` 1/2 (normal/monster), non-merchant, mostly
  `no_exp='t'` — consistent with scripted escort targets. Guard 6059 is explicitly a
  quest-summon (`comment3: '퀘스트 소환용'`, dyed goat).
- Contexts 745 and 1421 have **no `quest_contexts` row** (broken ref — components
  3971/6306 exist and are loaded; the context row itself is missing). These two quests
  are additionally unreachable by normal quest flow; the guard fix still applies to
  their acts. 116 components total reference missing contexts (known data-hygiene
  family, tracked informational by BUG-007's verifier).

## Root cause

`QuestActCheckGuard.cs` (AAEmu.Game/Models/Game/Quests/Acts/QuestActCheckGuard.cs:9-15):

```csharp
public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
{
    Logger.Warn(...);
    // TODO: This seems to be related to escort quests where you need to protect the NPC
    // TODO: Implement fail mechanics if they die?
    return true;
}
```

No lookup of the guard NPC, no alive check — the act is a pass-through. Escort/protect
objectives therefore always succeed (worst stub class: silent false positive).

## Fix (smallest change, matches sibling patterns)

`QuestActCheckGuard.RunAct` now:

1. Resolves the guard NPC via the owner's world — `character.ParentWorld.GetNpcByTemplateId(NpcId)`
   (same lookup `Quest.UseSkill`/`SetNpcAggro` use for component NPCs, Quest.cs:276/293;
   `WorldInstance.GetNpcByTemplateId` returns the first live instance of that template).
2. Returns **true only when the NPC resolves and `!IsDead`** (`Unit.IsDead` = `Hp <= 0`).
3. Returns **false when the NPC cannot be resolved** (despawned/missing/not spawned —
   conservative: a missing guard must not pass), and **false when the owner is not a
   `Character`** (defensive; same cast pattern as Quest.cs).

No changes to `InitializeAction`/`FinalizeAction` (act inherits base behavior) or to
`quest_act_check_guards` loading (QuestManager.cs:620-630, `npc_id` → `NpcId`).

## Files changed

- `AAEmu.Game/Models/Game/Quests/Acts/QuestActCheckGuard.cs` (RunAct implemented)
- `AAEmu.UnitTests/Game/Models/Game/Quests/Acts/QuestActCheckGuardTests.cs` (new, 3 tests)

## Tests

New `QuestActCheckGuardTests` (MethodName_Scenario_ExpectedResult), real `Character` +
`WorldInstance` + `Npc` (parent world set via backing field — the property setter
requires the DI `WorldManager.Instance`, unavailable in unit tests):

- `RunAct_GuardAliveInWorld_ReturnsTrue` — passes before and after
- `RunAct_GuardDeadInWorld_ReturnsFalse` — **failed before fix** (stub returned true), passes after
- `RunAct_GuardNotSpawned_ReturnsFalse` — **failed before fix** (stub returned true), passes after

## Verification

- Full gate (`./scripts/gate.sh`): Release build 0 errors, compiler-check 0 errors,
  **1085/1085 tests pass** (develop baseline 1082 + 3 new)
- Fail-before evidence: with the old stub reverted, `RunAct_GuardDeadInWorld_ReturnsFalse`
  and `RunAct_GuardNotSpawned_ReturnsFalse` fail (2 failed / 1 passed)

No upstream PR (lane gate). Commit identity: Tai <tai@asslorde.com>.
