# Stub-Act Audit — quest acts that silently auto-complete or stall

**Author:** Tai · **Date:** 2026-08-04 · **Data:** prod compact.sqlite3 (r208088, 1.2.4.13) @ 192.168.0.165
**Scope:** all 48 quest act classes × all 66 `quest_acts.act_detail_type` values (26,886 acts, 17,851 components, 4,876 quest contexts).
Companion: M1-1 verdict (quest_act_obj_aliases dormant) — see quests.md.

## 1. Real stubs (need a fix or a deliberate decision)

| Class | Live acts | Live comps | Live ctxs | Behavior | Class |
|---|---|---|---|---|---|
| `QuestActCheckGuard` | 6 | 6 | 6 | `return true` unconditionally; TODO "Implement fail mechanics if they die" (QuestActCheckGuard.cs:12-14) | **SILENT AUTO-COMPLETE** — escort/protect objectives always pass |
| `QuestActObjItemGroupGather` | 7 | 7 | 7 | `return base.RunAct(...)` → QuestActTemplate.RunAct logs Error "not implemented!" and returns **false** (QuestActTemplate.cs:95-99; the doc comment explicitly says "descendents should never call base()") | **STALL** — objective never completes |
| `QuestActObjItemGroupUse` | 2 | 2 | 2 | same `base.RunAct` pass-through | **STALL** |

Note the asymmetry: CheckGuard is a *false positive* (quest finishes without the guard objective being real), ItemGroup* is a *false negative* (quest never advances). Both are invisible to the player in different ways.

## 2. Unverified (returns true, plausible by design — needs one spot-check)

| Class | Live acts | Live comps | Live ctxs | Note |
|---|---|---|---|---|
| `QuestActConAcceptComponent` | 337 | 335 | 274 | `return true` with TODO "don't do any actual checks" (QuestActConAcceptComponent.cs:20). Class comment says it's the self-referencing starter pattern (quests offered via `engage_combat_give_quest_id`); if the quest is only offered server-side, true is correct. **Watch item** — verify one live instance before touching. |

## 3. By design / functional (NOT stubs — do not "fix" these)

| Class | Live acts | Why it's fine |
|---|---|---|
| `QuestActConAutoComplete` | 1,386 | Auto-complete condition act — returns true by definition |
| `QuestActCheckTimer` | 45 | Timer act: RunAct true by design; the real logic is InitializeAction (starts quest timer) + OnTimerExpired (fails quest). Correctly wired. |
| `QuestActCheckCompleteComponent` | 14 | Proper check: `RunComponent()` on the target component; false + Error log if missing (QuestActCheckCompleteComponent.cs:19-29) |
| `QuestActCheckSphere` | 1 | Sphere-entry objective via OnEnterSphere/OnExitSphere + objective count |
| `QuestActCheckDistance` | 1 | Same check_* pattern (no stub markers) |
| `QuestActObjCompleteQuest` | 53 | `HasQuestCompleted` check, objective set once (QuestActObjCompleteQuest.cs:26-29) |
| `QuestActObjAlias` | 0 | Client-only display act (see M1-1 verdict — dormant in 1.2 data) |

## 4. Partial (functional but with known semantic gaps — already separate M1 tasks)

| Class | Live acts | Live ctxs | Gap |
|---|---|---|---|
| `QuestActObjInteraction` | 293 | 277 | `TODO Verify: Is Phase here what is actually used to move the Doodad to that phase` (QuestActObjInteraction.cs:60) — golden-route quests 922/3447; this is its own M1 task |
| `QuestActSupplyHonorPoint` | 46 | 46 | TODO "calculate modifiers" (QuestActSupplyHonorPoint.cs:22) — grants honor, missing modifier math |

## 5. Data hygiene (informational for the sanity verifier)

- **7,607 / 26,886 act rows (28%) are ORPHANED** — `quest_acts.quest_component_id` has no matching row in `quest_components` (17,851 rows). Top orphans: QuestActConReportNpc 1,499 · QuestActObjItemGather 973 · QuestActSupplyItem 792 · QuestActObjTalk 511 · QuestActCheckCompleteComponent 500. They are never instantiated (loader builds `_actsByComponent` from components) → dead data, no crash. Sanity verifier: report count as informational.
- **Dead types in 1.2 data** (class + detail table exist, ZERO live rows): `QuestActConFail`, `QuestActObjCondition`, `QuestActObjEffectFire`, `QuestActObjDoodadPhaseCheck`, `QuestActConAcceptItemEquip`, `QuestActConAcceptBuff`, `QuestActSupplySkill`, `QuestActSupplyInteraction`, `QuestActConAcceptNpcEmotion`, `QuestActObjDistance` — benign; verifier should list them informational, not error.

## 6. Reproducible queries (prod box)

```sql
-- live act rows per type (inner join drops orphans)
SELECT a.act_detail_type, COUNT(*), COUNT(DISTINCT a.quest_component_id),
       COUNT(DISTINCT qc.quest_context_id)
FROM quest_acts a JOIN quest_components qc ON a.quest_component_id=qc.id
GROUP BY a.act_detail_type ORDER BY 2 DESC;
-- orphaned act rows
SELECT COUNT(*) FROM quest_acts a LEFT JOIN quest_components qc
  ON a.quest_component_id=qc.id WHERE qc.id IS NULL;
-- pull the actual quest ids for the fix cards:
-- (join contexts → names via quest_names / quest_context_texts as needed)
```

## 7. Recommended order (roadmap priority: engine defects → golden route → corruption → peripheral)

1. **QuestActCheckGuard (6 quests)** — decide + implement: escort-guard alive check (or must-return-false). Silent auto-complete is the worst class.
2. **ItemGroup gather/use (9 quests)** — implement group-item objective logic or wire events; currently stall.
3. **QuestActConAcceptComponent (274 ctxs)** — one live spot-check to confirm the true-return is correct (likely yes).
4. **Sanity verifier (M1-3)** — validate `act_detail_type` → class registry; log any `base.RunAct` "not implemented" hits at load; orphan + dead-type counts informational.
5. **QuestActObjInteraction phase semantics** — existing M1 doodad task (922/3889/3447).
