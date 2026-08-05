# COMPONENT_NEXT_MISSING — 776/777 fail-before evidence rig

**Card:** M1 rig: COMPONENT_NEXT_MISSING 776/777 — fail-before evidence (t_07e6c255)
**Date:** 2026-08-05 · **Branch:** fix/next-missing-776-777-rig
**Mechanic:** QUEST-01 · **Zone:** global · **Verdict:** FAIL-BEFORE proven → PASS-AFTER proven

## 1. What this is

A reproducible test rig proving that quests **776** (해적과 오크, "Pirates and Orcs")
and **777** (오크의 그늘 아래, "Under the Orc's shadow") — plus the same-defect
quest **330** — fail the `COMPONENT_NEXT_MISSING` verifier check on the raw
reference data, and pass once the 3-row data fix is applied. The rig has two
independent layers:

| Layer | Artifact | Proves |
|---|---|---|
| xUnit rig (real verifier code) | `AAEmu.UnitTests/Game/Core/Managers/QuestNextComponentRigTests.cs` | `QuestSanityVerifier.VerifyLoadedState` fires `COMPONENT_NEXT_MISSING` (Warn) on the prod-shaped 776/777/330 topology, and reports clean after the fix |
| SQL census rig (data-level mirror) | `Scripts/quest_next_missing_census.sh` | the same predicate against the real `compact.sqlite3` table rows, before and after the 3 UPDATEs |

The rig models the verifier predicate exactly as implemented
(`AAEmu.Game/Core/Managers/QuestSanityVerifier.cs` → `VerifyLoadedState`):

```csharp
if (component.NextComponent != 0 && !quest.Components.ContainsKey(component.NextComponent))
    → Finding(Warn, "COMPONENT_NEXT_MISSING", ...)
```

## 2. Ground truth (reference data)

`compact.sqlite3` md5 **78b3bdbf038db3b927056106efdf91af** (same reference verified
in `scorecard-explorations/data-defects.md` §3). The three dangling rows:

| quest | quest name | component | kind | next_component | target exists as ANY quest_component? | target in same quest? |
|---|---|---|---|---|---|---|
| 330 | 나를 찾는 사람 | 1520 | Start(2) | 3543 | no (0 rows) | no |
| **776** | **해적과 오크** | **3480** | **Start(2)** | **4370** | **no (0 rows)** | **no** |
| **777** | **오크의 그늘 아래** | **3488** | **Progress(4)** | **3487** | **no (0 rows)** | **no** |

Sibling components of each quest (all with next_component = 0):
330 → 1521 (Ready), 1522 (Reward) · 776 → 3482 (Progress), 3483 (Ready), 3484 (Reward) ·
777 → 3485 (Start), 11591 (Ready), 11592 (Reward), 21238 (Progress).

## 3. Rig layer 1 — xUnit (real verifier)

`dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj` (full suite, 2026-08-05):

```
Test run summary: Passed!
  total: 1210
  failed: 0
  succeeded: 1210
  skipped: 0
```

The two rig tests (`QuestNextComponentRigTests`, both included in the 1210):

- `VerifyLoadedState_ProdQuests776_777_330_RawData_FailComponentNextMissing`
  — raw prod topology ⇒ exactly 3 `COMPONENT_NEXT_MISSING` findings, one per
  quest (776→comp 3480 next 4370, 777→comp 3488 next 3487, 330→comp 1520 next 3543),
  all at **Warn** severity (quests are **not** allowlisted — no Info downgrade),
  messages name the dangling target ids.
- `VerifyLoadedState_ProdQuests776_777_330_DataFixApplied_Pass`
  — same topology with the 3 UPDATEs from
  `SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql`
  (1520→1521, 3480→3482, 3488→11591) ⇒ **zero findings** (report fully clean).

## 4. Rig layer 2 — SQL census (real data)

`Scripts/quest_next_missing_census.sh /tmp/compact.sqlite3 --apply-fix`
(script copies the DB before applying the fix — source stays read-only):

```
== census on: /tmp/compact.sqlite3  (md5 78b3bdbf038db3b927056106efdf91af)
RESULT: FAIL — COMPONENT_NEXT_MISSING rows:
quest  component  next_target  quest_name
-----  ---------  -----------  ----------
330    1520       3543         나를 찾는 사람
776    3480       4370         해적과 오크
777    3488       3487         오크의 그늘 아래

>> fix applied to copy (3 UPDATEs from SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql)
== census on: /tmp/next-missing-census.iH1UOG.sqlite3  (md5 5a2d233a7756f7d1699d0fb3a0bf971c)
RESULT: PASS — 0 COMPONENT_NEXT_MISSING rows
```

The census SQL mirrors the verifier predicate per quest:

```sql
SELECT qc.quest_context_id AS quest, qc.id AS component,
       qc.next_component AS next_target, q.name AS quest_name
FROM quest_components qc
LEFT JOIN quest_contexts q ON q.id = qc.quest_context_id
WHERE qc.next_component != 0
  AND NOT EXISTS (SELECT 1 FROM quest_components s
                  WHERE s.id = qc.next_component
                    AND s.quest_context_id = qc.quest_context_id)
ORDER BY qc.quest_context_id, qc.id;
```

## 5. Why this matters (fail-before framing)

- **Before the data fix** (current develop: verifier refinement merged, data
  fix pending at next data sync): the check fires on all three quests every
  census — `COMPONENT_NEXT_MISSING` rows for 776/777/330. On develop today
  these are Warn-severity findings (refinement t_a6a55c26, Error→Warn), and the
  allowlist (132 ids) explicitly does **not** cover 776/777/330.
- **After the data fix** (`SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql`
  at the next data sync, or the additive QuestDataOverlay it mirrors at runtime):
  all three quests report clean — the rig's pass-after state is exactly the
  post-fix census.

## 6. Provenance

- Classification: `scorecard-explorations/data-defects.md` §3 (t_7416ea48)
- Fix patch: `SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql` (t_abf740ee)
- Verifier: `AAEmu.Game/Core/Managers/QuestSanityVerifier.cs`
  (COMPONENT_NEXT_MISSING in `VerifyLoadedState`; severity refinement @61bef4c0,
  merged into develop)
- Data overlay decision: data-defects.md §3/§7 (t_25744130)

## 7. Fix branch — pass-after evidence (t_cdbf231b)

**Branch:** fix/next-missing-776-777 (rig f1503b3f + overlay mechanism carried from
fix/verifier-data-overlay @ 7a1ef90a/368fd254, rebased onto develop ffa4bbeb)

**Mechanism (NOT a second fix — the existing one, carried):** the 3-row correction
lands as the additive startup sanitizer `QuestDataOverlay`
(`AAEmu.Game/Core/Managers/QuestDataOverlay.cs`), applied by `QuestManager.Load`
right after `LoadQuestComponents` — 1520→1521, 3480→3482, 3488→11591. The
reference `compact.sqlite3` is never edited (upstream alignment rule 3); the SQL
patch `2026-08-04-fix-quest-data-defects.sql` remains the data-sync mirror.
Drift rows Warn, never throw (sanitizer policy matches the verifier).

**Pass-after evidence (all re-run on this branch, 2026-08-05):**

1. **Real load path, real reference DB** — `QuestDataCensusTests` boots
   `QuestManager.Load()` against the canonical compact.sqlite3 (md5
   78b3bdbf038db3b927056106efdf91af, 4775 quests / 17720 components / 19047 acts)
   exactly like GameService startup, then asserts the verifier report has **0**
   `COMPONENT_NEXT_MISSING` findings. Result (TRX-captured):

   ```
   [QuestCensus] 0 ERR / 11 WARN / 136 INFO across 4775 quests / 17720 components / 19047 acts
   ```
   The report contains **zero** COMPONENT_NEXT_MISSING rows; the 3 former
   findings (330/776/777) are gone. Remaining WARN/INFO findings are other
   backlog items (QUEST_NO_COMPONENTS 96, QUEST_NO_START 23, UNIT_REQS_* 21,
   ACT_REF_MISSING_QUEST 2 — separate M1 cards, explicitly out of scope).

2. **Full gate** — `./scripts/gate.sh` green: Release build OK, compiler-check
   0 errors / 0 warnings, test suite **1214 passed / 0 failed / 0 skipped**
   (1210 develop suite + 2 rig tests + 3 `QuestDataOverlayTests` + 1 real-data
   census test — the census test ran, not skipped, because the canonical DB was
   present under the test host's Data/ dir).

3. **SQL census** — `Scripts/quest_next_missing_census.sh --apply-fix` against
   the canonical DB: fail-before exactly 3 rows (330 comp 1520→3543, 776 comp
   3480→4370, 777 comp 3488→3487), pass-after 0 rows on the fixed copy — the
   runtime overlay mirrors exactly this SQL patch.
