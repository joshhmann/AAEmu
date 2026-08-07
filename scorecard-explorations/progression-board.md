# HYRAXKNOT — AAEmu Progression Board

**Updated:** 2026-08-06 · **Owner:** Aya (director) · **Data basis:** census + board, all Rei-gated

---

## The Game vs What We've Proven

| Scope | Quests | Status |
|---|---:|---|
| **Total 1.2 quest contexts** | 4,876 | reference data |
| Dropped content (decided, registered) | 32 | 23 no-start shells + 8 orphans + dummy 1391 |
| **Band 1–10 (Solzreed…)** | 660 (668 − dropped) | **100.0%** — 560 PASS / 100 documented-SKIP |
| **Band 11–20 (Gweonid, Lilyut…)** | 626 | **100.0%** — 609 PASS / 17 documented-SKIP |
| **Band 21–30** | 847 | **IN PROGRESS** — engine fix running (ZoneKill) |
| Band 31–40 | 643 | queued (M2c follow-through) |
| Band 41–50 | 1,592 | heavy lift (M2d — dailies/repeatables) |
| Band 51–55 | 268 | nearly clean (library) |
| Band 0/unknown | 231 | sweep last (tutorial/legacy) |

**Proven runnable today: 1,233 quests (25% of the game) with 0 unexplained FAILs.**

---

## Milestones

| Milestone | Meaning | Status |
|---|---|---|
| **M1** | Solzreed golden route + engine defects (BUG-007→013) | ✅ Core delivered — **playtest pending Josh** |
| **M2a** | Band 1–20 census ≥95% | ✅ **DONE** — 1233P/0F/136S, bar met, Rei gate wired |
| **M2b** | Playerbot repeatability pilot (Solzreed) | ✅ **DONE** — 30/30 cycles, 0 leaks, cat-34 fix landed |
| **M2b-E2E** | Live-server bot harness (Login+Game+MySQL) | 🔶 **BLOCKED** — 2×160-turn exhaustion, needs decomposition |
| **M2c** | Band 21–30 sweep | 🔶 **IN PROGRESS** — Wave 3 harness done; **ZoneKill engine fix running** |
| **M2d** (proposed) | Band 41–50 sweep | ⏳ queued |
| **M6-light** | Bot roam/safety/behaviors | ✅ **DONE** (t_5aec3250) |
| **M6 full** | Playerbot world (living bots) | ⏳ next after E2E + M2c |

---

## This Week's Catch (the fleet as QA)

- **Census caught a real engine bug**: QuestActObjZoneKill faction-0 credit dead path — 95/106 ZoneKill quests never credit live. Upstream byte-identical. **Fix running** (t_497b51d8).
- Cat-34 dailies: root-caused (completed-flag gate + reset family excluded Task(6)) → **one-liner landed**, true 1.2 midnight dailies.
- Audit flow: transport-stall class killed (deterministic no-agent classifier live).

---

## Open Decisions (Josh)

1. **M1 playtest verdict** — after you walk Solzreed (your pace, your receipts).
2. **Batch deploy GO** — Round-2 fixes waiting at the gate (prod @ bddd426e).
3. **Sit-pose in-game check** — backlog till Tuesday (or later today).
4. **E2E decomposition plan** — my handle: split into boot-orchestration / network-bridge / E2E-runner cards.

*Progress = forward motion with receipts. Every row above is evidence-gated.*
