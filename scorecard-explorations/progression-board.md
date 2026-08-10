# HYRAXKNOT — AAEmu Progression Board

**Updated:** 2026-08-09 · **Owner:** Aya (director) / Nei (tracking) · **Data basis:** census + board, all Rei-gated

---

## The Game vs What We've Proven

| Scope | Quests | Status |
|---|---:|---|
| **Total 1.2 quest contexts** | 4,876 | reference data |
| Dropped content (decided, registered) | 32 | 23 no-start shells + 8 orphans + dummy 1391 |
| **Band 1–10 (Solzreed…)** | 660 (668 − dropped) | **100.0%** — 560 PASS / 100 documented-SKIP |
| **Band 11–20 (Gweonid, Lilyut…)** | 626 | **100.0%** — 609 PASS / 17 documented-SKIP |
| **Band 21–30** | 847 | **IN PROGRESS** — kill-acceptor landed (t_71e48494) + ZoneKill engine fix landed (t_497b51d8, Rei-gated); full-band census re-run pending |
| Band 31–40 | 643 | queued (M2c follow-through) |
| Band 41–50 | 1,592 | heavy lift (M2d — dailies/repeatables) |
| Band 51–55 | 268 | nearly clean (library) |
| Band 0/unknown | 231 | sweep last (tutorial/legacy) |

**Proven runnable today: 1,233 quests (25% of the 4,876-quest surface) with 0 unexplained FAILs.**

---

## Milestones

| Milestone | Meaning | Status |
|---|---|---|
| **M1** | Solzreed golden route + engine defects (BUG-007→013) | ✅ **CLOSED** — Josh signoff; engine-health release live @ 94f498fc |
| **M2a** | Band 1–20 census ≥95% | ✅ **DONE** — 1233P/0F/136S; 1–10 = 100% (560P/100S over 660), 11–20 = 100% (609P/17S over 626) |
| **M2b** | Playerbot repeatability pilot (Solzreed) | ✅ **DONE** — 30/30 |
| **M2b-E2E** | Live-server bot harness (Login+Game+MySQL) | ✅ **DONE** — harness + gates: 10-bot correctness PASS, 25-bot stability PASS (08-09); Scripts/e2e + presence overlay in-repo (06e6fcb4a, 615c3719c) |
| **M2c** | Band 21–30 sweep | 🔶 **IN PROGRESS** — kill-acceptor landed (t_71e48494); ZoneKill engine fix landed (t_497b51d8); full-band census re-run pending |
| **M2d** (proposed) | Band 41–50 sweep | ⏳ queued |
| **M6-light** | Bot roam/safety/behaviors | ✅ **DONE** (t_5aec3250) |
| **M6** | Deterministic playerbot framework | 🔶 hotfix chain **DONE** — 3 citizens live at Josh's spawn (terrain-Z + patrol-home + factory looks + restart-idempotent); e2e 10/25-bot gates PASS; GM cmds P0 (t_7b4f9423); M6.6 parity seeding landed (t_747a1c44/t_120bb6c9); **6h/10-bot soak RUNNING** (t_1ed9881f, started 15:26 PDT); adopt-heal look-collapse fix in Rei review (t_555ed207) |
| **M6 full** | 1,000-citizen living world | ⏳ next after soak exit + M2c |

---

## This Week's Catch (the fleet as QA)

- **ZoneKill engine bug fixed** (census-caught; upstream byte-identical): faction-0 credit dead path → landed + Rei-gated (t_497b51d8 @ ca3307a1) — faction 0 = no filter, 0..0 = any level; t5 20 FAIL → 20 PASS, dailies 5900/5923/5924 PASS; all tiers 213P/0F/25S.
- **Kill-acceptor family unstuck**: BUG-006 (t_71e48494) — 380 quests with Kill start components can now start; deployed prod @ 3040508.
- **M2b-E2E RESOLVED**: deterministic boot/reset harness (06e6fcb4a) + presence compose overlay (615c3719c) committed in-repo; 10-bot correctness PASS + 25-bot stability PASS (08-09).
- **Presence demo live at Josh's spawn**: 3 citizens embody + roam zone 179 (15572/15364/126.5); in-client sighting ACCEPTED — Josh saw bots 08-09, Rei wire-confirmed (17× 0x69 / 164× 0x6C, t_509ef8c2).
- **M6.6 player parity landed**: bots carry 34 actabilities + bag byte-identical to human baseline (t_747a1c44/t_120bb6c9); GM bot commands P0 deployed (t_7b4f9423).
- **Adopt-heal look-collapse** (Rei-caught, t_555ed207): fix pushed (cdf6d4a62, E2E 3/3) — in Rei review; prod re-provision pending deploy.

---

## Open Decisions (Josh)

1. **M1 playtest verdict** — after you walk Solzreed (your pace, your receipts).
2. **Soak exit report** — 6h/10-bot soak running (t_1ed9881f); pass/fail report + M6 exit verdict due ~21:26 PDT 08-09.
3. **Sit-pose in-game check** — backlog (Tue 08-11 or later).

*Progress = forward motion with receipts. Every row above is evidence-gated.*
