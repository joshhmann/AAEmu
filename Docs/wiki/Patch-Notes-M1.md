# Patch Notes — M1: The Quest-and-Progression Spine

**Build:** `b1e2231c` (M1 core + M1 widened backlog, deployed 2026-08-05)
**Target:** ArcheAge 1.2 (r208022) — ArcheAge Slums

---

## What M1 is

M1 is the quest-and-progression spine: make the game's quests actually
*work* — accept, progress, complete, reward, persist — and make the golden
route from character creation to your first house playable end to end.

**Headline:** 153/153 quests on the runnability census (Solzreed golden
route T1 97/97) — every one of them starts, progresses, completes, rewards,
and persists.

---

## New & Fixed

### Quest engine reliability
- **Kill-accept quests fixed** — quests that require kills now accept and
  track kills correctly (no more silent "kill 10 wolves" that never counts).
- **Stub-act cleanup** — quest acts that silently auto-completed or stalled
  are repaired; quests no longer skip ahead or freeze mid-chain.
- **Doodad / interaction objectives fixed** — quests that ask you to use or
  phase a doodad (e.g. *To the Land for Eternity*, *Closing the Gate*) now
  resolve properly.
- **Quest timing** — quests using game-time checks now read server time
  correctly (UnixTime fixes + spell/ability seeds).
- **2,746 dangling act-alias rows repaired** — quest data now loads clean,
  so broken references can't silently kill a quest line.

### Quest chains repaired (the silent dead-ends)
- **Quests 330 / 776 / 777** — `next_component` references pointed nowhere,
  so parts of their chains could never progress. They now chain correctly
  (applied as an in-memory data overlay at server start).
- **Quest 2145 (crafting chain)** — a self-start trigger referenced a quest
  that no longer exists; the dangling trigger is pruned. Sibling 1960→1961
  cleaned the same way. The chain can't dead-end on a phantom reference.

### World behavior
- **NPCs stop walking into hills** — slope/step gating on NPC movement.
- **Floating NPCs grounded** — spawn-height defects corrected (99 rows; no
  more elves hovering 90m in the air).
- **NPC aggro line-of-sight fixed** — mobs no longer aggro through walls.
- **Sit-pose fallback** — NPCs that should sit now sit (no T-pose
  statue-ing).

### Data hygiene (nothing you'll miss)
- **23 legacy tutorial quests removed** — 1.0-era shells from the old Nuian
  starter zone (never reachable by any accept path; the 1.2 opening replaced
  them). Officially dropped and registered.
- **8 orphaned quest contexts removed** — quest IDs that referenced nothing
  and were reachable by nothing.
- **Quest 1391 removed** — an empty template (milestone bookkeeping shell)
  that could never be accepted.
- Every dropped ID is recorded in the **Dropped Content Register** with its
  reason and restore path — nothing disappears without a paper trail.

### For the operators
- **Quest sanity verifier at startup** — the server now cross-checks quest
  data at boot and reports broken patterns (WARN/INFO with ids) instead of
  failing silently later.
- **Runnability census** — automated scenario tests drive every census
  quest start→progress→complete→reward→persist and publish the report.
- **Verifier allowlists unmasked** — dead content is now *reported honestly*
  (WARN) rather than silently hidden; the census says what it means.

---

## What to do when you log in

1. **Play the golden route**: create a character → starter quests in
   Solzreed → get your mount → farm → build a house → craft a trade pack →
   transport it → sell → come home.
2. **Try the fixed chains**: any quest that used to stall — kill quests,
   doodad/interaction quests, the crafting chain around 2145 — should now
   progress to reward.
3. **Watch the world**: NPCs walk on the ground, mobs aggro by sight, sitters
   sit.

---

## Known & intentional

- Census WARN count rose 14 → 35 at this build **on purpose**: the 23
  tutorial shells + 1391 are no longer allowlisted, so the verifier reports
  them honestly (they were never reachable in-game).
- The harness census covers 153 of 4775 quests today; coverage expansion
  (14 more act families) rides in **M2**.

## Rollback

`git switch --detach <previous-sha>` on the aaemu box + service-aware
rebuild. Previous prod: `f5e5aa98` (Round-2 NPC fixes).
