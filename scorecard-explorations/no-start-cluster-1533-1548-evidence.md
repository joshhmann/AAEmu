# QUEST_NO_START cluster 1533–1548 — fail-before + pass-after evidence (M1 rig)

**Card:** t_d5e088ed (rig, fail-before) → t_5140fb35 (fix, pass-after) · **Mechanic:** QUEST-01 · **Zone:** global (cluster is zone 1 `w_gweonid_forest_1` + 22/1 stragglers) · **Status:** DROPPED 2026-08-05 (Josh decision — data-level drop, not code)

**Data provenance:** canonical 1.2 reference `compact.sqlite3`, md5 `78b3bdbf038db3b927056106efdf91af` (same hash unit-reqs-layer.md cites as canonical 1.2; the `78b3bdbf0383db3b927056106efdf91af` variant in the verifier/data-defects docs is a transcription typo — no file with that hash exists on the box). Read-only; the rig never writes to it.

## Verdict

**The cluster can never be accepted — proven, not assumed.** Every one of the 23 quests has components but **zero Start-kind components**, and **zero accept surfaces** (item / effect / doodad / self-start act / unit_reqs gate) reference them. The engine's own `Quest.StartQuest()` returns `false` for a quest without a Start step (`AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:42-56`), and `Quest.CreateQuestSteps()` never creates a Start step when no Start component exists (`NewQuestCode.cs:16-36`). The verifier's `QUEST_NO_START` finding fires for all 23 — it is masked to INFO by the allowlist (`QuestSanityVerifier.cs:84-109, 191-194, 289-294`), which is why the census stays green. **Green ≠ runnable.**

## Cluster inventory (23 quests; headline range 1533–1548 in bold)

| quest | cat | zone | lvl | comps | kinds | Start comps | acts |
|---|---|---|---|---|---|---|---|
| **1533** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1535** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1536** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1537** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1538** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1539** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1540** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1541** | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1542** | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1543** | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1544** | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1545** | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1546** | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1547** | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| **1548** | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1549 | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1551 | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1552 | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1553 | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1554 | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1640 | 28 | 1 | 0 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1830 | 1 | 22 | 0 | 1 | 8 (Reward) | 0 | (act-less) |
| 1831 | 1 | 1 | 0 | 3 | 4, 6, 8 (Progress/Ready/Reward) | 0 | (act-less) |

Kinds key: 2=Start, 4=Progress, 6=Ready, 8=Reward (`QuestComponentKind`, `AAEmu.Game/Models/Game/Quests/Static/QuestComponentKind.cs`).

- **1534 and 1550 are NOT quests** — no `quest_contexts` row, no components (pure id gaps inside the headline range; nothing is ever loaded for them).
- **No names in data:** zero `quest_context_texts` rows for the cluster (the Korean 1.0 tutorial titles from the earlier audit snapshot are gone from the canonical 1.2 reference).
- All 23 load into the engine (only category 45 is skipped by `QuestManager.LoadQuestContexts`, `QuestManager.cs:570-572`; the cluster is category 28/1).

## Accept-surface census — all zero

| surface | rows referencing cluster |
|---|---|
| `item_accept_quests` | 0 |
| `accept_quest_effects` | 0 |
| `doodad_func_quests` | 0 |
| `quest_act_con_accept_components` (self-start act targets) | 0 |
| `unit_reqs` kind 31/32/33/37 gates from live quest components | 0 |

There is no item, effect, doodad, NPC-accept act, or completion gate anywhere that can start one of these quests — and even if something tried, the engine has no Start step to run.

## Engine-level proof

```
Quest.CreateQuestSteps()   NewQuestCode.cs:16-36  steps are built per component kind
                                                   → no Start kind ⇒ no Start step
Quest.StartQuest()         NewQuestCode.cs:42-56  QuestSteps.TryGetValue(Start) fails
                                                   → Logger.Warn + return false
```

The rig drives the **real** `Quest.StartQuest()` against a Quest constructed from the real template (mocked owner/managers, same rigging as `QuestScenarioDriver`) and asserts `false` for all 23 quests.

## Verifier interplay

`QuestSanityVerifier.VerifyLoadedState` (`QuestSanityVerifier.cs:191-194`) emits `QUEST_NO_START` for every cluster quest (components exist, no Start component). All 23 ids sit in the allowlist (`QuestSanityVerifier.cs:84-109`), which downgrades the finding to INFO (`:289-294`) — so the startup census reports them as neither failed nor warned. **The census green line is a mask, not a clean bill.** The rig asserts the finding still fires for every cluster quest and that the mask is exactly the allowlist.

## The rig

`AAEmu.UnitTests/Game/Core/Managers/QuestNoStartClusterTests.cs` (7 tests):

| test | proves |
|---|---|
| `EveryClusterQuest_IsLoadedWithComponents` | each of the 23 has a context row + ≥1 component |
| `EveryClusterQuest_HasNoStartComponent` | **zero Start-kind components per quest** |
| `EveryClusterQuest_VerifierEmitsQuestNoStart` | real verifier fires `QUEST_NO_START` for all 23; allowlist is the only mask |
| `EveryClusterQuest_HasNoAcceptPath` | all five accept surfaces are empty |
| `EngineStartQuest_ReturnsFalse_ForEveryClusterQuest` | real `Quest.StartQuest()` refuses all 23 |
| `IdGaps1534And1550_HaveNoTemplate` | 1534/1550 are id gaps (0 context rows, 0 components) |
| `HeadlineRange_ContainsFifteenQuestsAndOneIdGap` | 1533–1548 = 15 loadable quests + 1534 gap |

Run (needs the reference DB at `AAEmu.Game/Data/compact.sqlite3` or `$AAEMU_COMPACT_SQLITE3`):

```bash
dotnet test --project AAEmu.UnitTests --treenode-filter "/*/*/QuestNoStartClusterTests/*"
```

- Without a reference DB the tests **skip with a reason** (CI-friendly) — they never fabricate evidence.
- If the data ever changes so a cluster quest gains a Start component or an accept path, the tests **fail** — the classification is stale and this document must be regenerated. That is the regression contract for the follow-up fix card (data-defects.md §5 verdict: **(c) drop**).

## Fix branch — pass-after evidence (t_5140fb35, DROP 2026-08-05)

**Decision (Josh, 2026-08-05 chat):** *"Unblock granted, if they're orphans we prob don't need to code em in."*
Drop = data-level deletion, not code. Registered in `scorecard-explorations/dropped-content-register.md §2`
(restore pointer: these shells are the skeleton to reuse if a 1.2-era tutorial is ever rebuilt).

**Mechanism (guarded DELETE, no code):** `SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql`
deletes exactly the cluster rows on a copy of the canonical DB (reference stays read-only —
upstream alignment rule 3):

- `quest_contexts` 4876 → **4853** (−23: 1533, 1535–1549, 1551–1554, 1640, 1830, 1831)
- `quest_components` 17851 → **17826** (−25: comps 7738–7758, 8492, 8494–8496)
- `quest_acts` 26886 → **26844** (−42: acts 10867–10911 subset — SupplyCopper/SupplyExp wiring rows)

The shared act DETAIL rows (`quest_act_supply_coppers` / `quest_act_supply_exps`, small shared
ids like 6/7/8) are referenced by many other quests and are **NOT** deleted — only the cluster's
42 `quest_acts` wiring rows are unwired. The 9 `unit_reqs` rows with `value1` in the cluster are
Skill/AiEvent-owned id collisions (kinds 30/23/35 — buff-tag/sphere refs, NOT quest deps) and
are left untouched. 1534/1550 remain pure id gaps (nothing to delete).

**Allowlist removal (regression re-report):** the 23 cluster ids are REMOVED from the verifier
allowlist (`QuestSanityVerifier.cs` BuildAllowlist — previously lines 84–87 + the 1535–1549 /
1551–1554 ranges). If the rows ever come back, `QUEST_NO_START` now reports at **WARN** instead
of being masked to Info — the census can no longer be green while the defect is real.

**Pass-after evidence (all re-run on this branch, 2026-08-05):**

1. **SQL census `--apply-fix`** (`Scripts/quest_no_start_census.sh`) against the canonical DB
   (md5 78b3bdbf038db3b927056106efdf91af): fail-before exactly **23 quests** (exit 1) → after
   the drop patch on the copied DB **0 quests, PASS** (exit 0).

2. **Patch drift verification** — patch applied to a pristine copy: `quest_contexts`
   4876 → 4853, `quest_components` 17851 → 17826, `quest_acts` 26886 → 26844 (drift exactly
   −23/−25/−42 as documented in the patch header); 0 cluster remnants, 0 orphaned acts,
   shared detail tables byte-identical, all 9 unit_reqs collision rows intact.

3. **Rig flip** — `QuestNoStartClusterTests` rewritten to the post-drop contract (8 tests):
   data-state-aware — a cluster quest is either fully ABSENT (dropped: 0 contexts/0 comps/0
   acts) or, on a pre-drop reference, provably never-acceptable (zero Start comps, zero accept
   surfaces, real `Quest.StartQuest()` returns false); the allowlist no longer contains any
   cluster id; and `DropPatch_WhenAppliedToReferenceCopy_RemovesClusterEntirely` executes the
   shipped patch against a copy of the canonical DB and asserts complete removal with no
   orphans. 8/8 pass on the canonical reference.

4. **Verifier unit tests** — `QuestSanityVerifierTests` 28/28 pass: 1533-shaped QUEST_NO_START
   now reports WARN (allowlist mask gone), allowlist count 132 → 109, dropped ids asserted
   absent from the allowlist.

5. **Full gate** — `./scripts/gate.sh` green: Release build OK, compiler-check clean, full
   suite green including the flipped rig + verifier tests.

## Appendix — reproducible queries (canonical DB, md5 78b3bdbf038db3b927056106efdf91af)

```sql
-- cluster component shape
SELECT qc.id AS quest, COUNT(*) AS comps,
       GROUP_CONCAT(DISTINCT comp.component_kind_id) AS kinds,
       SUM(CASE WHEN comp.component_kind_id = 2 THEN 1 ELSE 0 END) AS start_comps
FROM quest_contexts qc LEFT JOIN quest_components comp ON comp.quest_context_id = qc.id
WHERE qc.id IN (1533,1535,1536,1537,1538,1539,1540,1541,1542,1543,1544,1545,1546,1547,1548,
                1549,1551,1552,1553,1554,1640,1830,1831)
GROUP BY qc.id ORDER BY qc.id;

-- id gaps inside the headline range
SELECT id FROM quest_contexts WHERE id BETWEEN 1533 AND 1554;  -- 1534 and 1550 absent

-- accept surfaces
SELECT 'item_accept_quests' AS surface, COUNT(*) FROM item_accept_quests WHERE quest_id IN (<cluster>)
UNION ALL SELECT 'accept_quest_effects', COUNT(*) FROM accept_quest_effects WHERE quest_id IN (<cluster>)
UNION ALL SELECT 'doodad_func_quests', COUNT(*) FROM doodad_func_quests WHERE quest_id IN (<cluster>)
UNION ALL SELECT 'con_accept_components', COUNT(*) FROM quest_act_con_accept_components WHERE quest_context_id IN (<cluster>)
UNION ALL SELECT 'unit_reqs live gates', COUNT(*) FROM unit_reqs
  WHERE kind_id IN (31,32,33,37) AND value1 IN (<cluster>) AND owner_type='QuestComponent'
    AND owner_id IN (SELECT id FROM quest_components);

-- act shape inside the Reward components
SELECT qc.quest_context_id AS quest, a.act_detail_type, COUNT(*)
FROM quest_acts a JOIN quest_components qc ON qc.id = a.quest_component_id
WHERE qc.quest_context_id IN (<cluster>) GROUP BY 1, 2;

-- texts (names)
SELECT COUNT(*) FROM quest_context_texts WHERE quest_context_id IN (<cluster>);  -- 0
```

Generated 2026-08-05 against the canonical reference data; regenerated by the rig whenever the data changes.
