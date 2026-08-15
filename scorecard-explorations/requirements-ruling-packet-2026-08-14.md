# Josh Requirement Ruling — 3 NOT RECOVERABLE requirement slots (M1 ×2, M2 ×1)

Date: 2026-08-14 · Prepared by: Nei (tracking) · Escalated by: Rei gate t_ec7f0c19 · Source: retrofit t_730b04bd

**Status:** RULED (2026-08-15) · **Constraint:** STOP LINE (AAEmu cap at M5.2) stands — this is requirements governance, exempt. No milestone status changes result from this packet; H stays UNKNOWN everywhere.

## Ruling record (authority: Josh, 2026-08-15, card t_905cffc4)

Verbatim owner ruling (2026-08-15, Josh on card t_905cffc4):

> Q1 ACCEPT-ENUMERATED. Q2 CONFIRM-LANE-B. Q3 RATIFY-95. These are standing requirement rulings only; they do not reopen any milestone. Nei: record the ruling and provenance in the packet/ROADMAP/board; Rei: re-touch the three slots to complete closure grading.

| Q | Ruling | Effect (recorded 2026-08-15, t_905cffc4) |
|---|---|---|
| Q1 | **ACCEPT-ENUMERATED** | M1 closure graded on the enumerated defect set (BUG-006..012 @ 94f498fc) + widened fixes; later defects → live backlog, not M1 reopens. Now **REQ-M1-11**. |
| Q2 | **CONFIRM-LANE-B** | No M1 peripheral-quest requirement; Lane B owns maintenance. Now **REQ-M1-12**. |
| Q3 | **RATIFY-95** | Reference ≥95% is the standing bar for M2c/M2d; both bands met it (100% PASS-or-doc-SKIP). Now **REQ-M2-10**. |

Amendments applied: ROADMAP.md (3 slots + summary table), scorecard-explorations/progression-board.md (2 rows), all marked `(Josh ruling 2026-08-15, t_905cffc4)`, committed on fork branch `docs/milestone-requirement-rulings`. These are standing requirement rulings only — they do not reopen any milestone; no status changes; H stays UNKNOWN.

## Why this packet exists

The Milestone Requirements + DoD standard (t_730b04bd, merged @ 75ac8df12) reconstructed 69 requirements across M1–M5.2, all provenance-marked `(reconstructed 2026-08-14)`, zero silently invented. Three slots could NOT be recovered — no source basis exists in the record (ROADMAP.md, progression-board.md, task history) to reconstruct a finite, gradeable requirement. Per gate doctrine (no self-deciding requirements), the Rei gate (t_ec7f0c19, CLOSED-WITH-CAVEATS on all 8 groups, 0 REOPEN) escalated these to Josh for a requirement ruling.

All three slots sit on milestones that otherwise closed cleanly. The ruling decides what the standing requirement IS — not whether the work was done. Evidence basis below is verified against origin/develop @ 75ac8df12.

---

## Q1 — M1 completeness bar for "shared engine defects" beyond the enumerated list

**Slot (ROADMAP.md:254, retrofit):** "REQUIREMENT NOT RECOVERABLE: a completeness bar for 'shared engine defects' beyond the enumerated list — M1 scope is explicitly 'trimmed, not exhaustive'; no finite defect enumeration exists in the record."

**Evidence:**
- M1's engine-defect requirement is the enumerated set: BUG-006..012 merged @ 94f498fc, plus widened fixes (t_d8a8c798, t_60a559ab) and registered drops (M2a purge playbook). M1 DoD row: "engine-path implementation ✅ merged @ 94f498fc (BUG-006..012) + widened fixes".
- M1 scope prose is explicit: "trimmed, not exhaustive" — the defect list was never intended as a closed universe.
- No finite enumeration of "all shared engine defects" exists in any record (ROADMAP, task_runs, register). A completeness bar over an open-ended class is not finitely gradeable — any number would be unfalsifiable (defects are discovered, not enumerated).

**Question for Josh:** Is M1 closure acceptable on the enumerated defect set (BUG-006..013 + widened backlog), or must M1 carry a completeness bar?

**Recommendation (default if no override):** **Accept closure on the enumerated set.** Standing interpretation: REQ-M1's engine-defect requirement = the enumerated defect set as merged + widened backlog; defects found after closure route to the live backlog (Lane B / maintenance), not to M1 reopens. No retroactive completeness bar — it would be ungradeable.

---

## Q2 — M1 peripheral-quest coverage target

**Slot (ROADMAP.md:257, retrofit):** "REQUIREMENT NOT RECOVERABLE: a peripheral-quest coverage target — explicitly out of scope (Lane B maintenance); no bar was ever set."

**Evidence:**
- M1 scope explicitly excludes peripheral quests: golden path is locked to Solzreed (Josh, 2026-08-03); peripheral-quest maintenance is Lane B by standing scope.
- No coverage bar for peripheral quests appears anywhere in the record (ROADMAP M1 Work:/Exit tests:, board rows, task history).
- M1's quest-facing evidence is the census (153/153 → G1 4,573/4,573), which is a pass/fail classification, not a peripheral-coverage target.

**Question for Josh:** Confirm Lane B ownership is the standing answer (no M1 requirement for peripheral-quest coverage)?

**Recommendation (default if no override):** **Ratify "no M1 requirement".** Lane B owns peripheral-quest maintenance by standing scope; retrofitting a bar now would grade M1 on something it never promised. Standing answer: no M1 coverage target; any future peripheral-quest bar is Lane B's to set.

---

## Q3 — M2c/M2d original per-band thresholds beyond reference-level ≥95%

**Slot (ROADMAP.md:410, retrofit):** "REQUIREMENT NOT RECOVERABLE: the original per-band numeric thresholds for M2c/M2d beyond the reference-level '≥95%' (the board's meaning columns carry no threshold; both landed 100% PASS-or-doc-SKIP via G1)."

**Evidence:**
- Board band rows (progression-board.md): Band 21–30 → 847/847 PASS, 100.0%; Band 41–50 → 1,589 PASS / 2 doc-SKIP (3419/4967 kept-by-ruling), 100.0% PASS-or-doc-SKIP.
- The ≥95% reference is the board's own gate language (M2a row: "Band 1–20 census ≥95%"); ROADMAP M2 detail adopts band tables "by reference".
- G1 GATE PASSED 2026-08-10 (t_4221f85c): 4,573 PASS / 0 FAIL / 14 doc-SKIP over 4,579 live — 100% PASS-or-doc-SKIP, zero unexplained.
- No stricter per-band threshold exists in any record; both bands met ≥95% (and 100%) as landed.

**Question for Josh:** Ratify the reference ≥95% (met) as the standing bar for M2c/M2d, or set a stricter one retroactively?

**Recommendation (default if no override):** **Ratify ≥95% as the standing bar.** It is the board's own gate language, both bands met it (100% PASS-or-doc-SKIP), and the 2 M2d doc-SKIPs are already Josh-ruled keeps with provenance. A stricter retroactive bar would reopen M2d on those 2 rulings without new evidence of a gap — not warranted.

---

## Decision format (reply on card t_905cffc4)

Reply with per-question rulings, or accept all defaults:

```
Q1 ACCEPT-ENUMERATED, Q2 CONFIRM-LANE-B, Q3 RATIFY-95
```

Per-question overrides welcome, e.g. `Q1 REQUIRE-BAR` (then specify the bar), `Q3 STRICTER-<pct>` (then specify the number). Defaults (recommended): `Q1 ACCEPT-ENUMERATED · Q2 CONFIRM-LANE-B · Q3 RATIFY-95`.

## Post-ruling actions (on unblock)

1. Record the ruling + provenance in this packet (authority, date, verbatim reply).
2. Amend ROADMAP.md requirement blocks: replace the three NOT RECOVERABLE markers with the ruled requirement text, marked `(Josh ruling 2026-08-14, t_905cffc4)`, and mirror to progression-board.md.
3. Re-run Rei gate touch on the amended slots (t_ec7f0c19 thread) so closure grading can complete.

— Nei (tracking)
