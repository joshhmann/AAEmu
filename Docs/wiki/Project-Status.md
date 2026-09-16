# Project Status — "ArcheAge Slums" fork milestones

- Audience: Contributors, players, and testers
- Last verified against: `develop` on 2026-09-12 (documentation revision `c7351b242`; no fresh gameplay verification)
- Prerequisites: None

Status of the fork's milestone plan (joshhmann/AAEmu). The canonical plan
lives in the repo: ROADMAP.md; this page is the short public status.

## Current direction (2026-09-14)

Development is at M8 — Living Village, transitioning toward M9 — Emergent world
systems. M8 has a qualified engineering/runtime exit (24/24 ledger cycles, 4/4 kill-9
restarts, scheduler VALID, physics/autosave PASS, one accepted 1112 ms transient,
DB-volume INVALID by design; tested revision `a4d35fee8`), not human acceptance.
On 2026-09-14, live human golden action traces were deployed to `.165` covering crop harvest,
husbandry chick placement/gather, merchant buy/sell, mounts, and NPC interaction.
The `PlayerBot Target Architecture — Consolidated v1 Candidate` was ratified, adopting
Option A (Minimal Extension), 14 locked invariants, 7-value failure routing, and entering
Phase 0 (Evidence/Integrity) prior to the Two-Potatoes slice.
Current records: fork `develop`
[ROADMAP](https://github.com/joshhmann/AAEmu/blob/develop/ROADMAP.md) ·
[STATUS](https://github.com/joshhmann/AAEmu/blob/develop/STATUS.md) ·
[SCORECARD](https://github.com/joshhmann/AAEmu/blob/develop/SCORECARD.md) ·
[PROJECT-CONTROL](https://github.com/joshhmann/AAEmu/blob/develop/PROJECT-CONTROL.md) ·
[PLAYERBOT-FRAMEWORK](https://github.com/joshhmann/AAEmu/blob/develop/PLAYERBOT_PROGRESSION_AND_TESTING_FRAMEWORK.md) ·
[TARGET-ARCHITECTURE-V1](https://github.com/joshhmann/AAEmu/blob/develop/PLAYERBOT_TARGET_ARCHITECTURE_CONSOLIDATED_V1.md).

## HISTORICAL snapshot — 2026-08-05 (do not use as current; current direction is above)
### Milestones (historical)

| # | Milestone | State |
|---|-----------|-------|
| M0 | Foundation — workflow lane gate, kanban templates, gate.sh, scorecards, graphify graph, shared division skill, BUG-006 fix merged | ✅ COMPLETE (2026-08-03) |
| M1 | Quest & progression spine — shared engine fixes + curated golden route (Solzreed) | 🔶 IN FLIGHT |
| M2 | Golden-path specification + reproducible baseline (Solzreed locked as golden zone) | ⏳ next |
| M3a/b | Homestead shell, then property persistence & recovery | ⏳ |
| M4 | Trade, crafting, transport integrity | ⏳ |
| M5 | Gameplay Actor Contract (normalize, not invent) | ⏳ |
| M6 | Deterministic playerbot framework (headless bot sessions) | ⏳ |
| M7 | Adventurer & party bots (Playerbots Alpha) | ⏳ |
| M8 | Living Village — the first true vision release | ⏳ |

## M0 — Foundation (done, 2026-08-03)

Workflow v4 with the one-way upstream gate (pull updates in; never push or PR out),
kanban task templates, `scripts/gate.sh` (Release build + compiler-check +
full test suite), the 679-table technical scorecard (SCORECARD.md) +
exploration reports, the graphify semantic code graph (~17.5k nodes), and
the shared division skill on all worker profiles.

## M1 — Quest and progression spine (in flight)

Fix shared quest-engine defects first, then drive the selected golden route.
Progress to date:

- BUG-006 kill-acceptor quests (380 quests) — fixed, merged to fork develop;
  production deploy pending owner decision.
- BUG-007 quest sanity verifier — startup cross-check in QuestManager.Load
  (unknown act types, broken refs, orphaned rows); 14 tests.
- BUG-008 QuestActCheckGuard — silent auto-complete fixed (guard must exist
  and be alive); 3 tests.
- BUG-009 item-group gather/use objectives — 4 live quests + test quest
  unstalled; 14 tests.
- BUG-010 UnixTime clamp — every timer quest restored correctly; 8 tests.
  Full gate: 1129/1129 (2026-08-04).
- Scenario harness (M1-5b): engine-level full-lifecycle quest driver
  (START→PROGRESS→READY→REWARD→PERSIST) in AAEmu.UnitTests.
- Runnability census, date-scoped (do not mix these numbers — different runs):
  - `Quest-Runnability-Census.md` (generated 2026-08-05 03:20Z): T1 Solzreed
    97/97 PASS; all tiers 153/153 driven (33 SKIP not driven).
  - `Quest-Test-Harness.md` (2026-08-05): Solzreed golden zone 86/97 PASS,
    11 FAIL, 0 SKIP; fix-family set 21 PASS / 8 FAIL / 6 SKIP.
  - `Golden-Route-Solzreed.md` Sources line (2026-08-05): 88/97 T1 pass.
  - The three T1 figures (97 vs 86 vs 88) come from different harness runs on
    the same date and are unresolved — recorded here as-is, not reconciled.
- Solzreed locked as the golden zone (zones 9/124/125, 97 quest contexts);
  curated Nuian opening chain documented — see Golden-Route-Solzreed.
- Known M1 blockers from the census (11 T1 FAILs, classes per
  Golden-Route-Solzreed §4):
  - Class A — harness manifest artifacts (LetItDone quests complete via the
    report act in-game; harness expected auto-advance): 265, 266, 269, 294,
    299, 303, 2248 (T2: 1033, 3656, 5489).
  - Class B — harness rig artifact (reward exp on a rigged character without
    ability trees; live play unaffected; latent `AddActiveExp` guard gap):
    250 (T2: 6578, 6600, 6615).
  - Class C — harness event limitation (single ItemUse event vs ×3 objective
    count): 295.
  - Class D — real persistence defect: timed quests fail the
    WriteData→ReadData round-trip (byte mismatch): 350, 4292. 4292 sits on
    the mount chain — the M1 exit goal. Engine fix tracked.
- Remaining M1: doodad phase/interaction objectives (quests 922/3889/3447),
  triage of the 11 zone fails (current census), BUG-006 deploy decision.

## The golden route concept

The project builds ONE dependable classic-ArcheAge life loop first, then
expands outward: create character → starter progression (Solzreed) → unlock
mount → acquire farm → plant & harvest → build house → craft trade pack →
transport → sell → return home. All work is judged against that path
("the golden path is the product"). Bots will later master the same slice
(M5-M8). M2 defines and baselines it; M3 repairs homestead integrity, and M4
is the first integrated human-playable release gate.

## Related

- Golden-Route-Solzreed · Quest-Test-Harness · Home
- Repo: ROADMAP.md · STATUS.md · SCORECARD.md · VISION.md · LIVING-WORLD.md
