# BUG-007 — Quest data defects fail silently: add startup sanity verifier (M1-3)

**Status:** FIXED — branch `feat/quest-sanity-verifier` (2026-08-04)
**Type:** Engine defect visibility (M1 — shared engine defects, roadmap priority 3)
**Impact:** structural quest defects (missing act handlers, uninstantiated acts, broken
references, known stubs) previously surfaced only as Trace logs during load (or not at
all — silent auto-complete/stall at runtime). Now every defect class is collected and
logged loudly at startup, server continues to boot (fail policy: never throw).

## Root cause

- `LoadBaseQuestActs` skips orphaned act rows with a **Trace** log (QuestManager.cs:452)
- `LoadDetailQuestActTemplates` silently skips detail rows whose act/component can't be
  resolved (`GetComponentByActTemplate` null → `continue`)
- Unimplemented act types stay as base `QuestActTemplate` — runtime `RunAct` logs Error
  and returns false **only when that quest step is reached by a player**
- Known stubs (M1-2 audit: QuestActCheckGuard silent-pass, ItemGroup gather/use stall)
  and broken references (missing next_component / target quest / item group) have no
  load-time signal at all

Net effect: the server boots "healthy" while thousands of quests are broken in silence.

## Fix (smallest change, matches sibling loader behavior)

New `AAEmu.Game/Core/Managers/QuestSanityVerifier.cs` (static, pure functions — no I/O
in the loaded-state check, fully unit-testable):

1. **`VerifyLoadedState(...)`** — walks every base act row + every loaded quest template:
   - `ACT_UNKNOWN_TYPE` (Error) — act_detail_type has no handler class, act can never run
   - `ACT_UNINSTANTIATED` (Error) — detail id has no quest_act_xxx row; objective silently missing
   - `ACT_DETACHED` (Error) — detail instance wired to a different component than its act row
   - `QUEST_NO_COMPONENTS` (Warn), `QUEST_NO_START` (Warn)
   - `COMPONENT_NEXT_MISSING` (Error), `ACT_NEXT_MISSING` (Error, CheckTimer)
   - `ACT_REF_MISSING_COMPONENT` / `ACT_REF_MISSING_QUEST` / `ACT_REF_MISSING_COMPLETE_QUEST` (Error)
   - `ACT_GROUP_MISSING` (Error, ItemGroupGather/Use)
   - `ACT_STUB_KNOWN` (Warn) — M1-2 stub catalog (CheckGuard, ItemGroup gather/use)
   - `ACT_WATCH` (Info) — ConAcceptComponent self-start pattern count (M1-2 watch item)
2. **`VerifyData(connection, registeredTypes)`** — SQL-level hygiene the loaders skip:
   - `DATA_ORPHAN_ACTS` / `DATA_ORPHAN_COMPONENTS` (Info) — dead rows never instantiated
   - `DATA_UNKNOWN_TYPE` (Error) — DB act types with no handler class (by full data scan)
   - `DATA_ALIAS_USE` (Info/Warn) — live re-verification of the M1-1 verdict
     (0 use_alias=1 rows → dormant; >0 → alias resolution missing = real defect)
3. **`LogReport(...)`** — per-finding logging at severity + loud summary line.
   **Fail policy: never throws** (matches loaders; data contains thousands of orphaned
   rows — hard-fail would brick server start).

Wired at the end of `QuestManager.Load()` (QuestManager.cs:265-274); result exposed as
`QuestManager.Instance.LastSanityReport`.

## Evidence

- Build: Release 0 errors (1 new warning CA1859 fixed before commit)
- Tests: **14 new** `QuestSanityVerifierTests` (every finding class + clean state),
  fail-before = missing (feature, not regression), all green; full gate: see gate.sh
  result in the completion record
- Expected prod-data findings (from M1-1/M1-2 audits, will be logged at next boot):
  7,607 orphaned act rows (Info) · 10 dead act types with 0 live rows · 6 CheckGuard
  quests (stub Warn) · 9 ItemGroup quests (stub Warn) · 337 ConAcceptComponent acts
  (watch Info) · alias dormancy confirmed (Info)

## Verification (prod)

Deploy per WORKFLOW.md §Deploy (exact-SHA ff-only), then:
1. Boot log must contain `[QuestSanity] SUMMARY:` with the findings above
2. `grep QuestSanity /root/AAEmu/logs/*` — error/warn counts match the audits
3. Server must continue to boot normally (fail policy: log-only)

## Files

- `AAEmu.Game/Core/Managers/QuestSanityVerifier.cs` (new, 330 lines)
- `AAEmu.Game/Core/Managers/QuestManager.cs` (+LastSanityReport property, +verifier call)
- `AAEmu.UnitTests/Game/Core/Managers/QuestSanityVerifierTests.cs` (new, 14 tests)
- `SCORECARD.md` (quests row note), `ISSUES.md` (BUG-007 index row)

No upstream PR (lane gate). Commit identity: Tai <tai@asslorde.com>.
