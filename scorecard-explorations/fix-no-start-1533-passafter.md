# fix/no-start-1533 — pass-after evidence (t_43927087)

**Card:** t_43927087 (implement Start-act fix 1533–1548 + pass-after evidence) · **Branch:** `fix/no-start-1533` · **Mechanic:** QUEST-01 · **Date:** 2026-08-05

**Fix shape (as decided, Josh 2026-08-05; root cause: `docs/fix-no-start-1533-rootcause.md`):**
data-level drop of a dead legacy cluster — guarded SQL patch + verifier allowlist removal,
never raw edits to the canonical `compact.sqlite3` (read-only reference).

## Cluster quest ids (23)

**1533, 1535–1549, 1551–1554, 1640, 1830, 1831**
(1534 and 1550 are pure id gaps — no `quest_contexts` row exists, nothing is ever loaded for them.)

Every one of these had components but **zero Start-kind components** and **zero accept
surfaces**, so the engine could never accept them (`Quest.StartQuest()` returns false,
`NewQuestCode.cs:42-56`). After the drop, none of them load; the verifier no longer has
anything to flag.

## What shipped on the branch (commits carried from the pipeline)

| commit | change |
|---|---|
| `655be2e1` | `SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql` — guarded DELETEs: 23 quest_contexts + 25 quest_components + 42 quest_acts wiring rows, every row pinned to its verified shape |
| `33f01ff3` | `QuestSanityVerifier.cs` — the 23 ids REMOVED from `BuildAllowlist` (132 → 109) so a regression that re-adds the rows re-reports `QUEST_NO_START` at WARN, not the old Info mask |
| `c980f0ec` | `QuestNoStartClusterTests` flipped to the post-drop contract (data-state-aware, 8 tests incl. drop-patch-on-copy proof) |
| `1ab0d91d` | docs: evidence pass-after, data-defects §5 EXECUTED, dropped-content-register §2, runnability.md census regen |
| `80a482bf` | `docs/fix-no-start-1533-rootcause.md` — root-cause + fail-before baseline (t_0a59cc6c) |

## Pass-after evidence (fresh runs, 2026-08-05, canonical DB read-only)

Data provenance: canonical 1.2 `compact.sqlite3`, md5 `78b3bdbf038db3b927056106efdf91af`
(working copy in the card workspace; the box source was never opened for writes).

### 1. Verifier census — fail-before → pass-after (`Scripts/quest_no_start_census.sh`)

Fail-before on the canonical copy — **23 quests, exit 1** (matches the documented cluster exactly):

```
quest  category_id  zone_id  components  start_comps
-----  -----------  -------  ----------  -----------
1533   28           1        1           0
1535   28           1        1           0
... (1536–1549, 1551–1554, 1640: cat 28, zone 1, 1 comp, 0 start) ...
1830   1            22       1           0
1831   1            1        3           0
RESULT: FAIL — QUEST_NO_START quests above (can never be accepted)
EXIT=1
```

Pass-after — `--apply-fix` applies the shipped patch to a **temp copy** and re-runs the census:

```
>> fix applied to copy: .../2026-08-05-drop-no-start-cluster.sql (23 contexts / 25 components / 42 acts)
== census on: /tmp/no-start-census.XXXXXX.sqlite3
RESULT: PASS — 0 QUEST_NO_START quests
```

(The `--apply-fix` process exit stays 1 while the source copy still carries the rows —
the sticky rc reflects the fail-before source; the patched copy itself reports PASS/0,
and the in-process drop-patch rig test below proves the post-drop state. On a DB that
already has the patch applied, the census exits 0.)

### 2. Drop-patch drift (patch applied to a pristine copy)

`quest_contexts` 4876 → 4853, `quest_components` 17851 → 17826, `quest_acts` 26886 → 26844
(drift exactly −23/−25/−42 as documented in the patch header); 0 cluster remnants, 0 orphaned
acts; shared act-detail tables untouched; 9/9 `unit_reqs` Skill/AiEvent collision rows intact.

### 3. Regression coverage (flipped rig + verifier tests)

- `./scripts/gate.sh QuestNoStartClusterTests` → **8/8 pass** (post-drop contract:
  cluster quests fully absent on patched state or provably never-acceptable on pre-drop
  reference, allowlist no longer contains any cluster id,
  `DropPatch_WhenAppliedToReferenceCopy_RemovesClusterEntirely` executes the shipped patch
  against a copy of the canonical DB and asserts complete removal with no orphans).
- `./scripts/gate.sh QuestSanityVerifierTests` → **28/28 pass** (dropped ids asserted ABSENT
  from the allowlist, allowlist count 132 → 109, unmasked WARN behavior pinned).

### 4. Full fast gate — `./scripts/gate.sh`

```
== 1/3 Release build ==        Time Elapsed 00:00:02.94 (incremental; 0 errors)
== 2/3 compiler-check ==       ScriptCompiler - Compile done (0 errors, 0 warnings)
                               Program - Compilation successful
== 3/3 Tests ==                total: 1223   failed: 0   succeeded: 1223   skipped: 0
== GATE DONE ==
```

### 5. Reference DB untouched

`compact.sqlite3` md5 after all census/gate runs: `78b3bdbf038db3b927056106efdf91af` —
byte-identical to the canonical hash. No raw edits, ever; the drop is a deploy-time SQL
patch + verifier change.

## Evidence commit

Committed on `fix/no-start-1533` as `60d1c2a8` (2026-08-05), pushed fork-only
(`joshhmann/AAEmu` — upstream is intake-only, no PR). Restore pointer if the cluster is ever
rebuilt: `scorecard-explorations/dropped-content-register.md` §2.

## References

- `docs/fix-no-start-1533-rootcause.md` — root cause + fail-before baseline
- `scorecard-explorations/no-start-cluster-1533-1548-evidence.md` — full rig + evidence (t_d5e088ed/t_5140fb35)
- `SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql` — the drop patch
- `scorecard-explorations/data-defects.md` §5, `scorecard-explorations/dropped-content-register.md` §2
