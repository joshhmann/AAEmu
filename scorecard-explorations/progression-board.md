# HYRAXKNOT — AAEmu Progression Board

**Updated:** 2026-08-11 · **Owner:** Aya (director) / Nei (tracking) · **Data basis:** census + board, all Rei-gated · **G1 gate PASSED** (WI-12, merged develop @ 7f5c179f7) · **M1-M3 audit 2026-08-11** (t_5b1f5494): PASS WITH NOTES — tracking refreshed per C4

---

## The Game vs What We've Proven

| Scope | Quests | Status |
|---|---:|---|
| **Total quest_contexts rows** | 4,876 | reference data |
| Registered drops (decided, register §1–9) | 305 | 297 with rows excluded from denominator; 8 orphaned contexts (745/1421/1954–1958/2140) have no row — census-SKIP |
| **Band 1–10 (Solzreed…)** | 668 (560 non-dropped) | **100.0%** — 560 PASS / 0 doc-SKIP / 0 FAIL |
| **Band 11–20 (Gweonid, Lilyut…)** | 626 (609 non-dropped) | **100.0%** — 609 PASS / 0 doc-SKIP / 0 FAIL |
| **Band 21–30** | 847 | **100.0%** — 847 PASS / 0 doc-SKIP / 0 FAIL |
| **Band 31–40** | 643 | **100.0%** — 643 PASS / 0 doc-SKIP / 0 FAIL |
| **Band 41–50** | 1,592 (1,591 non-dropped; 6069 dropped WI-6) | **100.0%** — 1,589 PASS / 2 doc-SKIP (3419/4967 kept-by-ruling) / 0 FAIL |
| **Band 51–55** | 268 | **100.0%** — 268 PASS / 0 doc-SKIP / 0 FAIL |
| Lvl-99 straggler 3465 | 1 | ✅ PASS |
| **Band 0/null** | 60 | **100.0%** — 56 PASS / 4 doc-SKIP (A2 keeps) / 0 FAIL — D2 13P, D3 12P, D4 22P (8000004 flipped PASS post-BUG-014), D5 9P, A2 4 doc-SKIP |

**G1 GATE PASSED 2026-08-10:** 4,579 live contexts = 4,573 PASS + 6 kept-by-ruling doc-SKIP + 0 FAIL — **4,573/4,573 runnable, 100.0% PASS-or-doc-SKIP, zero unexplained**; full gate 1495 total / 0 failed / 1 env-gated skip on merged develop @ 7f5c179f7. Denominator: 4,876 rows − 297 registered drops = 4,579 live; all 14 SKIPs documented (8 orphans + 2 ltd + 4 no-components, Josh-ruled).

---

## Milestones

| Milestone | Meaning | Status |
|---|---|---|
| **M1** | Solzreed golden route + engine defects (BUG-007→013) | ✅ **CLOSED (automated evidence)** — engine-health release live @ 94f498fc; human playtest verdict still open (Open Decision #1, pending Josh — C5) |
| **M2 / G1 gate** | Quest coverage to 100% — every live context PASS or registered-drop or doc-SKIP, zero unexplained | ✅ **GATE PASSED 2026-08-10** — 4,573 PASS / 0 FAIL / 14 doc-SKIP over 4,587 census quests (4,579 live); gate.sh 1495/0/1 @ 7f5c179f7 |
| **M2a** | Band 1–20 census ≥95% | ✅ **DONE** — final: 1,169 PASS / 0 FAIL / 0 doc-SKIP (560 + 609) |
| **M2b** | Playerbot repeatability pilot (Solzreed) | ✅ **DONE** — 30/30 |
| **M2b-E2E** | Live-server bot harness (Login+Game+MySQL) | ✅ **DONE** — harness + gates: 10-bot correctness PASS, 25-bot stability PASS (08-09); Scripts/e2e + presence overlay in-repo (06e6fcb4a, 615c3719c) |
| **M2c** | Band 21–30 sweep | ✅ **DONE** — 847/847 PASS (absorbed into G1 gate; see ROADMAP G1) |
| **M2d** (proposed) | Band 41–50 sweep | ✅ **DONE** — 1,589 PASS / 2 doc-SKIP (3419/4967 kept-by-ruling); absorbed into G1 gate |
| **M3a** | Homestead shell — two players, adjacent homesteads, curated objects, ONE session | ✅ **CLOSED 2026-08-10** — Rei gate t_449875bd ACCEPT (t_72c787c8); merged @ 4d0427b96; M3aExitScenarioTests (M5-stand-in, 2 scripted actors, 16m adjacency); HOUSING-01 / FARM-01 C/W/H/A = 2 |
| **M3b** | Property persistence — N≥3 crash cycles, no loss/dup, save budget | ✅ **CLOSED 2026-08-11** — M3b-1..4 merged (5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea); EXIT E2E f5b00c686 PASS 7m08s (kill -9 mid-save + container kill hard-asserted, 16 rows/boot); autosave p95 1301ms < 2000ms @ 25 bots + 2 homesteads; PROPERTY-01 R = 2 (t_accb1c63) |
| **M6-light** | Bot roam/safety/behaviors | ✅ **DONE** (t_5aec3250) |
| **M6** | Deterministic playerbot framework | 🔶 hotfix chain **DONE** — 3 citizens live at Josh's spawn (terrain-Z + patrol-home + factory looks + restart-idempotent); e2e 10/25-bot gates PASS; GM cmds P0 (t_7b4f9423); M6.6 parity seeding landed (t_747a1c44/t_120bb6c9); 6h/10-bot soak COMPLETE (t_1ed9881f): 10/10 bots full window, 0 crash/disconnect, RSS flat 3418–3453MB — harness verdict **FAIL** on physics-warning budget (0.03/min vs 0, boot-spike WARNs) → regression card t_eecc5604; adopt-heal fix in Rei review (t_555ed207) |
| **M6 full** | 1,000-citizen living world | ⏳ queued — after soak regression RCA (t_eecc5604) + G2 ladder |

---

## This Week's Catch (the fleet as QA)

- **ZoneKill engine bug fixed** (census-caught; upstream byte-identical): faction-0 credit dead path → landed + Rei-gated (t_497b51d8 @ ca3307a1) — faction 0 = no filter, 0..0 = any level; t5 20 FAIL → 20 PASS, dailies 5900/5923/5924 PASS; all tiers 213P/0F/25S.
- **Kill-acceptor family unstuck**: BUG-006 (t_71e48494) — 380 quests with Kill start components can now start; deployed prod @ 3040508.
- **M2b-E2E RESOLVED**: deterministic boot/reset harness (06e6fcb4a) + presence compose overlay (615c3719c) committed in-repo; 10-bot correctness PASS + 25-bot stability PASS (08-09).
- **Presence demo live at Josh's spawn**: 3 citizens embody + roam zone 179 (15572/15364/126.5); in-client sighting ACCEPTED — Josh saw bots 08-09, Rei wire-confirmed (17× 0x69 / 164× 0x6C, t_509ef8c2).
- **M6.6 player parity landed**: bots carry 34 actabilities + bag byte-identical to human baseline (t_747a1c44/t_120bb6c9); GM bot commands P0 deployed (t_7b4f9423).
- **Adopt-heal look-collapse** (Rei-caught, t_555ed207): fix pushed (cdf6d4a62, E2E 3/3) — in Rei review; prod re-provision pending deploy.
- **G1 gate PASSED (2026-08-10)**: full M2 quest census green — 4,573 PASS / 0 FAIL / 14 documented SKIP, zero unexplained (WI-12, merged @ 7f5c179f7).

---

## Open Decisions (Josh)

1. **M1 playtest verdict** — after you walk Solzreed (your pace, your receipts).
2. ~~**Soak exit report**~~ — ✅ filed 2026-08-10 (t_1ed9881f): 6h/10-bot full window clean (0 crash/disconnect, RSS flat) but harness verdict FAIL on physics-warning budget → regression card t_eecc5604 (RCA or budget recalibration); **M6 exit pending that card's outcome.**
3. **Sit-pose in-game check** — backlog (Tue 08-11 or later).

*Progress = forward motion with receipts. Every row above is evidence-gated.*
