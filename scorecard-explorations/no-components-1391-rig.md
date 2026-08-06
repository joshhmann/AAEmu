# QUEST_NO_COMPONENTS 1391 — fail-before rig evidence

**Card:** t_6c5430e6 (M1 widened backlog, ROADMAP.md §M1 — Josh 2026-08-04)
**Date:** 2026-08-05 · **Branch:** fix/no-components-1391-rig
**Rig:** `AAEmu.UnitTests/Game/Quests/Scenario/Quest1391NoComponentsRigTests.cs`
**Data:** prod `compact.sqlite3` (md5 `78b3bdbf038db3b927056106efdf91af`; same file on
192.168.0.165 and the /tmp copies) — READ-ONLY reference, untouched.

## Verdict

**Quest 1391 ("마을을 지켜라", category 27, zone 0, level 0) is BROKEN: its template
has no components at all, so the engine can never accept or run it.** The rig proves
it at three levels (data, lifecycle, verifier). The verifier allowlist currently
masks the defect to INFO (`QuestSanityVerifier.cs:93`), which is why the live census
stays green while the quest stays permanently dead — the exact silent-defect class
this rig exists to expose.

## 1. Data-level ground truth (compact.sqlite3, read-only)

```sql
SELECT * FROM quest_contexts WHERE id=1391;
-- id=1391  name='마을을 지켜라'  category_id=27  zone_id=0  LEVEL=0
-- let_it_done='t'  score=0  milestone_id=5  use_quest_camera='t'  grade_id=1

SELECT count(*) FROM quest_components WHERE quest_context_id=1391;   -- 0 rows
SELECT count(*) FROM quest_acts
  JOIN quest_components qc ON quest_acts.quest_component_id = qc.id
  WHERE qc.quest_context_id=1391;                                    -- 0 rows
```

Zero components → zero acts. No accept path exists anywhere: no Start component, no
`QuestActConAccept*` act, so no NPC/doodad/sphere/item can offer quest 1391. It is not
self-starting either (no `QuestActConAcceptComponent`), and nothing else references it
as a dependency — the only `unit_reqs` row touching 1391 (`id 33609`, owner Skill 17113,
kind 35) is a sphere reference, not a quest gate.

## 2. Engine-level fail-before (scenario harness, pre-fix run)

The rig drives quest 1391's real shape (empty template) through the M1-5 scenario
harness (`QuestScenarioDriver`). The engine cannot accept the quest:

- `Quest.CreateQuestSteps()` — `NewQuestCode.cs:34-35` — produces an **empty**
  `QuestSteps` map and logs `Quest 1391 does not seem to have any components!`
- `Quest.StartQuest()` — `NewQuestCode.cs:44-48` — `QuestSteps.TryGetValue(Start)`
  fails → returns **false** ("Tried to start a quest without a starter component").
- Harness verdict (driver `QuestScenarioDriver.cs:700-710`): the accept path throws
  `StartQuest() returned false - quest has no Start component` and the run reports:

```
Quest 1391 (마을을 지켜라 (no-components rig)): Fail
  [Fail] START - accept failed: System.InvalidOperationException: StartQuest()
  returned false - quest has no Start component
```

Test evidence (`dotnet test --treenode-filter "/*/*/Quest1391NoComponentsRigTests/*"`):

```
Test run summary: Passed!  total: 3  failed: 0  succeeded: 3  skipped: 0
```

| test | asserts | result |
|---|---|---|
| `Quest1391_TemplateShape_ZeroComponentsZeroActs` | template components == 0 | Pass |
| `Quest1391_Lifecycle_CannotStart_FailsAtStart` | verdict Fail, START stage, reason "StartQuest() returned false … no Start component" | Pass |
| `Quest1391_Verifier_NoComponentsFindingMaskedToInfoByAllowlist` | QUEST_NO_COMPONENTS finding fired, severity Info (allowlist-masked), no Warn | Pass |

Per the fail-before convention (M1-5a, t_1e0e9717): the rig asserts the CURRENT broken
behavior and passes — this is the pre-fix baseline. The fix card flips it.

## 3. Verifier cross-check

`QuestSanityVerifier.VerifyLoadedState` fires `QUEST_NO_COMPONENTS` for quest 1391
(`QuestSanityVerifier.cs:181-187`, message "template has no components — can never be
accepted or run"), but the allowlist (`QuestSanityVerifier.cs:89-93`, group "dummy
shells", data-defects.md §6) downgrades it to INFO. The finding is real; the census
just can't see it. Classification history: data-defects.md §6 verdict **(c) drop** for
the dummy shell — the fix card must decide restore-vs-drop with Josh (see §5).

## 4. Player-visible impact

A player can never receive or hold quest 1391. No acceptor exists (no accept act in
any component — there are no components), and no other quest's reward/act references
it as a start target. If any NPC, item, or event were wired to grant it, the grant
would fail at `StartQuest()` and the quest would silently never appear. It is dead
content in the live quest table — reachable only by DB query.

## 5. Fix contract (for the downstream fix card)

1. **Decision first (Josh/data-defects §6):** restore the template from the canonical
   client data, or drop quest 1391 (delete `quest_contexts` row 1391). This rig
   documents the defect; it does not pick the fix.
2. **If restore:** SQL patch on compact.sqlite3 — `quest_components` +
   `quest_acts` + `quest_act_*` rows for quest 1391 matching the canonical client
   shape (category 27 event quest, zone 0, lvl 0, let_it_done) — following the
   pattern of `SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql`.
3. **Either way:** remove 1391 from the verifier allowlist
   (`QuestSanityVerifier.cs:93`) so a regression re-reports at WARN.
4. **Flip the rig:** extend the manifest with the restored component shape and update
   `Quest1391_Lifecycle_CannotStart_FailsAtStart` to assert PASS (drop
   `Quest1391_TemplateShape_ZeroComponentsZeroActs` only if the quest is dropped).

## 6. Gate evidence

- Rig class: 3/3 Pass (above).
- Scenario suite: `QuestScenarioTests` 12/12 Pass; `QuestScenarioTierTests` 1/1 Pass
  (census regenerated identical: 153 PASS / 0 FAIL / 33 SKIP — no headline change).
- Verifier suite: `QuestSanityVerifierTests` 27/27 Pass.
- Full gate `./scripts/gate.sh`: build 0 errors + compiler-check 0 errors + full
  test run (see card completion metadata for the full-gate numbers).

## 7. Pass-after evidence (fix card t_5a61cee3, branch fix/no-components-1391)

**Decision executed: DROP** (Josh 2026-08-05: "Unblock granted, if they're orphans we
prob don't need to code em in." — data-defects.md §6 verdict (c) drop;
dropped-content-register.md §1). Implemented as a data drop + allowlist removal, NOT
code that keeps dead content alive:

1. **`SQL/patches/compact/2026-08-05-drop-1391.sql`** — guarded `DELETE` of
   `quest_contexts` row 1391, pinned to the full verified shape (id + name +
   category_id + zone_id + LEVEL + milestone_id + let_it_done + score). All
   reference tables checked: 0 rows in quest_components/quest_acts/accept acts/
   item_accept_quests/doodad_func_*/game_schedule_quests/complete_quests/conditions/
   accept_quest_effects; the only unit_reqs touch (id 33609, owner Skill 17113,
   kind 35) is a sphere reference, not a quest gate — left in place.
2. **Verifier allowlist** — 1391 removed from the "dummy shells" group
   (`QuestSanityVerifier.cs:93`); the allowlist drops 132 → 131 ids
   (`Allowlist_ContainsClassifiedShells` updated: 1391 asserts absent).
3. **Rig flipped** — `Quest1391NoComponentsRigTests` now asserts the pass-after:
   loaded state has no 1391 template and the verifier reports nothing for it;
   regression guard: an empty 1391 template re-reports QUEST_NO_COMPONENTS at WARN
   (was allowlist-masked INFO pre-fix). The two fail-before tests asserting the
   broken shape were retired per the fix contract §5.4 (drop path).

**Data drift verified on a copy of canonical compact.sqlite3 (md5
78b3bdbf038db3b927056106efdf91af, READ-ONLY reference untouched):**

```
-- before                          -- after applying the patch
SELECT count(*) FROM quest_contexts;           4876 -> 4875   (exactly -1)
SELECT count(*) FROM quest_contexts WHERE id=1391;   1 -> 0
quest_components/quest_acts for 1391:             0 -> 0   (nothing to cascade)
```

**Test evidence (this branch):**

| test | asserts | result |
|---|---|---|
| `Quest1391_Dropped_TemplateAbsentFromLoadedState` | no 1391 template in loaded state, zero verifier findings for 1391 | Pass |
| `Quest1391_Dropped_Regression_EmptyTemplateReReportsWarn` | 1391 ∉ allowlist; empty-template regression fires QUEST_NO_COMPONENTS at Warn (no Info mask) | Pass |
| `Allowlist_ContainsClassifiedShells` | 1391 absent from allowlist, count 131 | Pass |

Gate: `./scripts/gate.sh` green — Release build 0 errors, compiler-check 0 errors,
full unit suite (see card completion metadata for numbers). Fork-only push
(fix/no-components-1391); no upstream PR (WORKFLOW.md v4 lane gate). Pending Rei
gate (t_70ae1bba) → census re-run (t_e239aa09).
