# Project Status — "ArcheAge Slums" fork milestones

- Audience: Contributors, players, and testers
- Last verified against: documentation-reviewed 2026-10-11 at `develop` / `aa057ffc3bb4596f4a6972eb4f012994970e163a` plus inherited projection/E131 docUI dirty work; no fresh gameplay verification
- Prerequisites: None

Status of the fork's milestone plan (joshhmann/AAEmu). The canonical plan
lives in the repo: ROADMAP.md; this page is the short public status.

## Current PlayerBot policy

Start with the [new-session bootstrap](../../START-HERE.md#new-session-bootstrap)
and [current session state](../../.omp/state/CURRENT.md#session-resume).
The user-approved direction is reuse-first supported goal-to-goal autonomy, grounded in
the [E132 current developed capability baseline](../../ROADMAP.md#current-developed-capability-baseline),
not exhaustive human parity or a module-by-module AzerothCore port.
The [supported-capability contract](../../PROJECT-CONTROL.md#playerbot-supported-capability-contract)
retains all seven criteria, including recovery from an interruption **or** failed action.
The [integration direction](../../PROJECT-CONTROL.md#playerbot-integration-direction),
[current ownership contract](../../PROJECT-CONTROL.md#current-playerbot-ownership-contract),
[core authorization](../../PROJECT-CONTROL.md#server-core-integration-authorization)
and [approved extension roadmap](../../ROADMAP.md#playerbot-extension-roadmap)
govern reuse and necessary shared-core repairs; future cards, gates and campaigns are
not completed or allocated by this documentation update.

The next [bounded priority](../../ROADMAP.md#next-bounded-playerbot-priority) remains
finite existing-owner goal A → eligible goal B, including the same NPC: preserve
the `AdvanceEnter` guard/counter and repair only an observed failure.
[Current checkpoint/allocation limits](../../STATUS.md#current-playerbot-policy-checkpoint)
promote no runtime capability, grade, milestone or H verdict. Historical A5 closure
and human evidence retain their named scope; farm/private-plot ownership is not a
universal quest-autonomy gate. The detailed Activity-Spine campaign remains unratified.
E130 recovery branch `review/branch-recovery-2026-10-10` at
`8024926a718a11f754abd2d92716e1c421fc22ce` remains unmerged pending the user's decision.


## Historical first bot product journey (2026-09-16; not current dispatch)

One ordinary starter earns, owns and farms a small scarecrow plot. This is not a
house/construction-pack or ten-bot village exit. The
[current route and dependencies](../../ROADMAP.md#first-owned-small-plot-journey)
include canonical prerequisite quests and level-10 eligibility; mapped data is not
proof of live execution. All implementation follows the
[common contract](../../.kanban-templates/implementation.md). Required gaps must be explicit.
This retained future livelihood journey is not a universal prerequisite to
supported quest autonomy; the historical queue below is not current allocation.

## Historical high-priority correction (2026-09-16; not current dispatch)

The [corrective queue](../../ROADMAP.md#high-priority-corrective-queue--2026-09-16)
recorded the then-priority before additional homestead/GOAP breadth. Its ten-bot
homestead report was synthetic orchestration evidence, not proof of actual gameplay.
The requirement to state missing/unproved loop legs remains valid: a synthetic pass
cannot close a real loop. Seven corrective briefs were recorded OPEN; this docs
update does not implement them or make them current allocation. Existing independently
verified mechanics and human evidence retain their scope.

## Historical delivery direction (2026-09-15; superseded queue)

This planning record described two outcomes: dependable human-playable 1.2.4
journeys and persistent PlayerBots using those same rules, with human playability,
bot parity/autonomy, and population/society lanes. Its
[roadmap queue](../../ROADMAP.md#near-term-slice-queue) began with operational
safety, a bounded human corridor, shared action parity, then seeded execution →
unseeded livelihood → real producer/consumer interaction. Retain the named human
requirements and [agent slice contract](../../PROJECT-CONTROL.md#delivery-contract),
but do not dispatch this old parity-first queue instead of the current policy above.
The record was a planning update, not fresh gameplay verification or a grade promotion.

## Recorded milestone horizon (2026-09-14)

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

## Historical golden route concept (named human journey, not universal bot gate)

The project builds ONE dependable classic-ArcheAge life loop first, then
expands outward: create character → starter progression (Solzreed) → unlock
mount → acquire farm → plant & harvest → build house → craft trade pack →
transport → sell → return home. All work is judged against that path
("the golden path is the product"). Bots will later master the same slice
(M5-M8). M2 defines and baselines it; M3 repairs homestead integrity, and M4
is the first integrated human-playable release gate.

This retained golden-path requirement describes its historical human/milestone scope.
It does not require every supported bot quest to acquire a farm or finish all human
activities before goal-to-goal autonomy can be accepted under the current contract.

## Related

- Golden-Route-Solzreed · Quest-Test-Harness · Home
- Repo: ROADMAP.md · STATUS.md · SCORECARD.md · VISION.md · LIVING-WORLD.md
