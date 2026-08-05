# ACT_REF_MISSING_QUEST — 2145→2146 fail-before evidence rig

**Card:** M1 rig: ACT_REF_MISSING_QUEST 2145→2146 self-start — fail-before evidence (t_0d743f43)
**Date:** 2026-08-05 · **Branch:** fix/act-ref-2145-rig
**Mechanic:** QUEST-01 · **Zone:** global · **Verdict:** FAIL-BEFORE proven → PASS-AFTER proven

## 1. What this is

A reproducible test rig proving that quest **2145** (다용도 옷감을 만들어보세요,
"make versatile fabric") carries a self-start act whose target — quest context
**2146** — can **never be found**: 2146 has no `quest_contexts` row, so the
loaders never create its template and `QuestManager.GetTemplate(2146)` always
returns null. The verifier's `ACT_REF_MISSING_QUEST` finding fires for quest
2145 on the raw reference data, and stops firing once the documented fix
(delete the dangling act) is applied. The rig has two independent layers:

| Layer | Artifact | Proves |
|---|---|---|
| xUnit rig (real verifier code) | `AAEmu.UnitTests/Game/Core/Managers/QuestActRefMissingQuestRigTests.cs` | `QuestSanityVerifier.VerifyLoadedState` fires `ACT_REF_MISSING_QUEST` for quest 2145 on the prod-shaped topology (Reward comp 9927, act 89 → 2146, 2146 absent from the loaded template dict), and reports clean after the dangling act is removed |
| SQL census rig (data-level mirror) | `Scripts/quest_act_ref_missing_census.sh` | the same predicate against the real `compact.sqlite3` rows, before and after the deletion |

The rig models the verifier predicate exactly as implemented
(`AAEmu.Game/Core/Managers/QuestSanityVerifier.cs` → `VerifyLoadedState`):

```csharp
case QuestActConAcceptComponent acceptComponent
    when !questTemplates.ContainsKey(acceptComponent.QuestContextId):
    → Finding(Error, "ACT_REF_MISSING_QUEST",
        "… references missing quest context {id} — self-start target can never be found")
```

## 2. Ground truth (reference data)

`compact.sqlite3` md5 **78b3bdbf0383db3b927056106efdf91af** (same reference verified
in `scorecard-explorations/data-defects.md` §4). The dangling rows:

| quest | quest name | component | kind | accept-act | `quest_act_con_accept_components` row | target | target has `quest_contexts` row? |
|---|---|---|---|---|---|---|---|
| **2145** | **다용도 옷감을 만들어보세요** | **9927** | **Reward(8)** | **89** | **89 → quest_context_id 2146** | **2146** | **no (0 rows)** |
| 1960 | 여행자의 조잡한 공구상자를 설치해보세요 | 9794 | Reward(8) | 75 | 75 → quest_context_id 1961 | 1961 | no (0 rows) |

Quest 2145's full component shape (all `next_component` = 0):
9925 Start(2) · 9926 Progress(4) · 9927 Reward(8). The Start comp carries the
**valid** self-start act 88 → 2145 (target IS loaded — this is the M1-2 watch
pattern working as intended); the Reward comp carries the **dangling** act 89 →
2146. Sibling context 2146 has 3 components of its own (9928/9929/9930) plus
accept-acts 89/90 — all orphaned, never loaded (its acts include 90, which the
census correctly ignores because quest 2146 is not a loaded quest).

The whole 1960/2145 pair is an abandoned cat-34 crafting chain
(1954→…→1960→1961→…→2143→2144→2145→2146); every chain root is gated on an
orphaned context, so nothing in the chain is reachable (data-defects.md §4,
verdict **(c) drop**).

## 3. Rig layer 1 — xUnit (real verifier)

`dotnet run --project AAEmu.UnitTests/AAEmu.UnitTests.csproj` (full suite, 2026-08-05):

```
Test run summary: Passed! - AAEmu.UnitTests.dll (net10.0|x64)
  total: 1210
  failed: 0
  succeeded: 1210
  skipped: 0
  duration: 21s 078ms
```

The two rig tests (`QuestActRefMissingQuestRigTests`, both included in the 1210):

- `VerifyLoadedState_Quest2145_RawData_FailActRefMissingQuest`
  — prod topology (Start comp 9925 with self-start act 88 → 2145, Reward comp
  9927 with dangling act 89 → 2146, **no** 2146 in the loaded quest dict) ⇒
  exactly 1 `ACT_REF_MISSING_QUEST` finding for quest 2145, message names
  "component 9927", "act 89", "quest context 2146", "self-start target can
  never be found". Severity is **Info** because quest 2145 is in the verifier
  allowlist (cat-34 chain classified dead — data-defects.md §4); the finding
  CODE still fires, which is the fail-before proof. Also asserts
  `quests.ContainsKey(2146) == false` — the runtime truth behind
  `GetTemplate(2146) == null`. (Non-allowlisted quests with the same shape
  report Error — covered by `VerifyLoadedState_ConAcceptComponentMissingQuest_ReportsError`.)
- `VerifyLoadedState_Quest2145_DanglingActRemoved_Pass`
  — same topology with the dangling act 89 removed (data-defects.md §4 minimal
  action: delete `quest_act_con_accept_components` id 89 + `quest_acts` row
  14121) ⇒ **zero** `ACT_REF_MISSING_QUEST` findings; the self-start act 88
  stays and still resolves.

## 4. Rig layer 2 — SQL census (real data)

`Scripts/quest_act_ref_missing_census.sh /tmp/compact.sqlite3 --apply-fix`
(script copies the DB before applying the fix — source stays read-only):

```
== census on: /tmp/compact.sqlite3  (md5 78b3bdbf0383db3b927056106efdf91af)
FULL PREDICATE (all loaded quests):
quest  quest_name             component  act_row  accept_act_id  missing_target
-----  ---------------------  ---------  -------  -------------  --------------
1960   여행자의 조잡한 공구상자를 설치해보세요  9794       14072    75             1961
2145   다용도 옷감을 만들어보세요         9927       14121    89             2146

RESULT: FAIL — quest 2145 has ACT_REF_MISSING_QUEST rows (self-start target can never be found)

>> fix applied to copy (data-defects.md §4: delete dangling act 89 + quest_acts 14121)
== census on: /tmp/act-ref-missing-census.La8Fxm.sqlite3  (md5 c967444a0aa7fde0f99e9e64419bdd6d)
FULL PREDICATE (all loaded quests):
quest  quest_name             component  act_row  accept_act_id  missing_target
-----  ---------------------  ---------  -------  -------------  --------------
1960   여행자의 조잡한 공구상자를 설치해보세요  9794       14072    75             1961

RESULT: PASS — quest 2145 has NO ACT_REF_MISSING_QUEST rows
```

The census SQL mirrors the verifier predicate (act on a loaded quest whose
`quest_act_con_accept_components.quest_context_id` has no `quest_contexts` row):

```sql
SELECT q.id AS quest, q.name AS quest_name, c.id AS component,
       a.id AS act_row, a.act_detail_id AS accept_act_id,
       cac.quest_context_id AS missing_target
FROM quest_acts a
JOIN quest_components c ON c.id = a.quest_component_id
JOIN quest_contexts q ON q.id = c.quest_context_id
JOIN quest_act_con_accept_components cac ON cac.id = a.act_detail_id
WHERE a.act_detail_type = 'QuestActConAcceptComponent'
  AND NOT EXISTS (SELECT 1 FROM quest_contexts t
                  WHERE t.id = cac.quest_context_id)
ORDER BY q.id, a.id;
```

Default scope is the card's quest (2145); `--scope 0` verdicts the full
predicate (both rows). The remaining 1960→1961 row is the sibling defect of
the same chain — same classification, separate fix decision (no card yet).

## 5. Why this matters (fail-before framing)

- **Before the fix** (current develop): quest 2145's Reward comp 9927 carries
  accept-act 89 whose target 2146 has no `quest_contexts` row. The loaders
  (`LoadQuestContexts`) only create templates from `quest_contexts` rows, so
  2146 is never in `_questTemplates` — `QuestManager.GetTemplate(2146)`
  returns null on every call, forever. The verifier's `ACT_REF_MISSING_QUEST`
  finding fires for 2145 on every census (currently downgraded to Info by the
  allowlist — the finding is real, the quest is just classified dead).
- **After the fix** (child card t_60a559ab): deleting the dangling act
  (`quest_act_con_accept_components` id 89 + `quest_acts` row 14121) removes
  the impossible self-start target from the loaded state — the rig's pass-after
  state is exactly the post-fix census. The alternative documented fix
  (restore context 2146) also clears the finding; either way the rig's
  pass-after assertion is the acceptance evidence.

## 6. Provenance

- Classification: `scorecard-explorations/data-defects.md` §4 (t_7416ea48)
- Fix decision: data-defects.md §4 verdict (c) drop / minimal act deletion
  (child card t_60a559ab)
- Verifier: `AAEmu.Game/Core/Managers/QuestSanityVerifier.cs`
  (ACT_REF_MISSING_QUEST in `VerifyLoadedState`; allowlist @61bef4c0)
- Sibling rig (same pattern, COMPONENT_NEXT_MISSING): t_07e6c255
