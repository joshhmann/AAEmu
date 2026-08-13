# HYRAXKNOT — AAEmu Progression Board

**Updated:** 2026-08-12 · **Owner:** Aya (director) / Nei (tracking) · **Data basis:** census + board, all Rei-gated · **G1 gate PASSED** (WI-12, merged develop @ 7f5c179f7) · **M1-M3 audit 2026-08-11** (t_5b1f5494): PASS WITH NOTES · **Bot-backtrack 0.1–0.3 (2026-08-12)**: 7-state evidence ledger (t_547ef82d), H grades reconciled — proxy/bot evidence ≠ H=2, H stays UNKNOWN until Josh runs it — + 5 deferred validation gates explicit (t_4ec066d3), M6 B4 restart replay PASS (t_9340e85d); M6 soak verdict preserved verbatim "passed revised approved budgets"

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
| **M1** | Solzreed golden route + engine defects (BUG-007→013) | ✅ **CLOSED (automated evidence)** — engine-health release live @ 94f498fc; human playtest verdict still open (Open Decision #1, pending Josh — C5; explicit deferred gate #1) |
| **M2 / G1 gate** | Quest coverage to 100% — every live context PASS or registered-drop or doc-SKIP, zero unexplained | ✅ **GATE PASSED 2026-08-10** — 4,573 PASS / 0 FAIL / 14 doc-SKIP over 4,587 census quests (4,579 live); gate.sh 1495/0/1 @ 7f5c179f7; original two-player human baseline = deferred gate #2 (Josh-owned) |
| **M2a** | Band 1–20 census ≥95% | ✅ **DONE** — final: 1,169 PASS / 0 FAIL / 0 doc-SKIP (560 + 609) |
| **M2b** | Playerbot repeatability pilot (Solzreed) | ✅ **DONE** — 30/30 |
| **M2b-E2E** | Live-server bot harness (Login+Game+MySQL) | ✅ **DONE** — harness + gates: 10-bot correctness PASS, 25-bot stability PASS (08-09); Scripts/e2e + presence overlay in-repo (06e6fcb4a, 615c3719c) |
| **M2c** | Band 21–30 sweep | ✅ **DONE** — 847/847 PASS (absorbed into G1 gate; see ROADMAP G1) |
| **M2d** (proposed) | Band 41–50 sweep | ✅ **DONE** — 1,589 PASS / 2 doc-SKIP (3419/4967 kept-by-ruling); absorbed into G1 gate |
| **M3a** | Homestead shell — two players, adjacent homesteads, curated objects, ONE session | ✅ **CLOSED 2026-08-10 on scripted-actor (proxy) evidence** — Rei gate t_449875bd ACCEPT (t_72c787c8); merged @ 4d0427b96; M3aExitScenarioTests (M5-stand-in, 2 scripted actors, 16m adjacency); HOUSING-01 / FARM-01 C/W/A = 2; **H = U (reconciled 2026-08-12 — proxy/bot-functional only; M3a contract replay = deferred gate #3)** |
| **M3b** | Property persistence — N≥3 crash cycles, no loss/dup, save budget | ✅ **CLOSED 2026-08-11** — M3b-1..4 merged (5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea); EXIT E2E f5b00c686 PASS 7m08s (kill -9 mid-save + container kill hard-asserted, 16 rows/boot); autosave p95 1301ms < 2000ms @ 25 bots + 2 homesteads; PROPERTY-01 R = 2 (t_accb1c63), W = 2 |
| **M4** | Trade, crafting and transport integrity | ✅ **MERGED + DEPLOYED (2026-08-12)** — pinned audited SHA **95bb1c78e** (M4 EXIT integrated release, t_97e59ffc, **Rei gate PASS** t_abe87eaf); prod CT 133 live as `aaemu-game:presence-demo` @ 6d5a07cf49a5 (Mai, t_442f3016) — startup PASS 37 min (0 restarts, 0 FATAL, 3/3 bots roaming, real client accepted); manifest `deploy/m4-manifest` @ 03d3442bd (deliberately NOT develop — fork develop carries M5-lane content); gates: unit 1778/0/1 + M4ExitIntegratedSessionTests (harvest → craft pack → slave cargo → 3-leg route → sell, 2× 124540 mails) + restart kill -9 PASS; CRAFT-01 / PACK-01 / SLAVE-01 C/W/A = 2, **H = U (reconciled — M4 economic/navigation replay = deferred gate #4)**; human playtest of the integrated release pending Josh GO |
| **M6-light** | Bot roam/safety/behaviors | ✅ **DONE** (t_5aec3250) |
| **M6** | Deterministic playerbot framework | 🔶 hotfix chain **DONE** — 3 citizens live at Josh's spawn (terrain-Z + patrol-home + factory looks + restart-idempotent); e2e 10/25-bot gates PASS; GM cmds P0 (t_7b4f9423); M6.6 parity seeding landed (t_747a1c44/t_120bb6c9); **adopt-heal look-collapse FIXED + MERGED (960ef8479, Rei ACCEPT t_c310b5ce) + prod re-provision DONE (t_26a0ef77)**; **physics slow-thread regression CLOSED** — GC tuning fix merged (105b4d5ed, SustainedLowLatency, 459ms class eliminated) + budget recalibrated 0→0.1/min (t_18fccd09, merged 78753185e) + race-fix chain merged (eb6f637e0, t_35167e60); **6h/10-bot soak GREEN 08-12 (t_35167e60: 360.0-min, ALL 9 budgets PASS, 0 failures)** — ROADMAP M6 EXIT RECORD filed (1588da87b), verdict preserved verbatim **"passed revised approved budgets"** (ledger + record untouched); **B4 restart-persistence replay PASS 08-12 (Phase 3 t_9340e85d**: 2-checkpoint bot-world restart, roster byte-identical, drift ≤0.03m, 1850/0/1 fresh-clone gate); H = **UNKNOWN** (human-feel: informal Josh sighting only — deferred gates, never H=2 from bot/scripted evidence) |
| **M6 full** | 1,000-citizen living world | ⏳ queued — scheduler-driven soak still open (PlayerBotScheduler NOT enabled in the recorded soak runs) + G2 ladder + B4 playerbot_metadata store follow-up (Phase 3 decision) |
| **M5** | Gameplay Actor Contract | 🔶 **A1 + B1 COMPLETE on develop — B2 + exit tests open** — A1 marshal seam (bots step on the game loop; c6d8f93a0) + B1 core action surface (Interact · Loot · UseItem · Mount/Dismount · AcceptQuest · TurnInQuest through real engine paths; merged 761d1e81a; Rei gates t_d06d8dd9 / t_ebfc9b35) — merged-tree re-verify 1850/0/1 (Phase 3). Remaining: B2 = M5.1 economic-actions wave (7 contract-action cards, todo), threading-boundary verification, M5 core exit test. Control-plane API t_7b6d7a4b **blocked** (contract-backed surface replacing BotDriveBridge); MCP sidecar t_446228b5 + first consumer t_52b2b084 queued |

---

## This Week's Catch (the fleet as QA)

- **M4 EXIT integrated release MERGED + DEPLOYED to prod (2026-08-12)** — pinned 95bb1c78e (Rei gate PASS t_abe87eaf), live as presence-demo image 6d5a07cf49a5 on CT 133 (Mai t_442f3016); crafting integrity + trade packs + vehicle lifecycle in one playable release; human playtest pending Josh GO.
- **M5 A1 + B1 landed on develop** — A1 marshal seam + B1 six-action contract surface through real engine paths (c6d8f93a0 / 761d1e81a); B2 economic actions + control-plane API/MCP sidecar queued.
- **Bot-backtrack program Phases 0.1–0.3 complete (2026-08-12)** — 7-state milestone evidence ledger (t_547ef82d), H grades reconciled (proxy ≠ H=2; H stays U), 5 deferred validation gates explicit (t_4ec066d3), M6 B4 restart-persistence replay PASS (t_9340e85d); soak verdict + history preserved verbatim; Phases 1 (M1/M2 replay, t_61a0eebb) + 2 (M3a/M4 economic replay, t_b4f455b0) queued behind M5.1.
- **M6 exit record closed** — physics-warning regression RCA'd as budget-calibration class → GC tuning fix (105b4d5ed) + budget recalibration (78753185e) + session/item race-fix chain merge (eb6f637e0) → **soak #4 GREEN 360-min / 9 budgets / 0 failures** (t_35167e60); ROADMAP M6 EXIT RECORD filed.
- **Adopt-heal look-collapse FIXED + shipped** (Rei-caught, t_555ed207) — force-stamp removed, factory looks survive reboot (960ef8479, Rei ACCEPT); prod Citizen rows re-provisioned (t_26a0ef77).
- **ZoneKill engine bug fixed** (census-caught; upstream byte-identical): faction-0 credit dead path → landed + Rei-gated (t_497b51d8 @ ca3307a1) — faction 0 = no filter, 0..0 = any level; t5 20 FAIL → 20 PASS, dailies 5900/5923/5924 PASS; all tiers 213P/0F/25S.
- **Kill-acceptor family unstuck**: BUG-006 (t_71e48494) — 380 quests with Kill start components can now start; deployed prod @ 3040508.
- **M2b-E2E RESOLVED**: deterministic boot/reset harness (06e6fcb4a) + presence compose overlay (615c3719c) committed in-repo; 10-bot correctness PASS + 25-bot stability PASS (08-09).
- **Presence demo live at Josh's spawn**: 3 citizens embody + roam zone 179 (15572/15364/126.5); in-client sighting ACCEPTED — Josh saw bots 08-09, Rei wire-confirmed (17× 0x69 / 164× 0x6C, t_509ef8c2).
- **M6.6 player parity landed**: bots carry 34 actabilities + bag byte-identical to human baseline (t_747a1c44/t_120bb6c9); GM bot commands P0 deployed (t_7b4f9423).
- **G1 gate PASSED (2026-08-10)**: full M2 quest census green — 4,573 PASS / 0 FAIL / 14 documented SKIP, zero unexplained (WI-12, merged @ 7f5c179f7).

---

## Open Decisions (Josh)

1. **M1 playtest verdict** — after you walk Solzreed (your pace, your receipts). Deferred gate #1 (bot-backtrack).
2. **M4 integrated release human playtest** — M4 is merged + prod-deployed (pinned 95bb1c78e); playable release awaiting your GO (deployment-lane follow-up).
3. ~~**Soak exit report**~~ — ✅ closed 2026-08-12: regression RCA'd (budget-calibration class), GC fix + recalibration + race-fix chain merged, **soak #4 GREEN 360-min / 9/9 budgets / 0 failures** (t_35167e60); ROADMAP M6 EXIT RECORD filed; verdict preserved verbatim "passed revised approved budgets". M6 full still needs a scheduler-driven soak + G2 ladder.
4. **Sit-pose in-game check** — backlog (Tue 08-11 or later).
5. **AAEMU authority envelope** — draft validated hermetic 17/17 (t_5999b370 done); activation card t_b1002aad blocked on your approval to flip live config.

*Progress = forward motion with receipts. Every row above is evidence-gated. Bots prove function; Josh proves feel — H verdicts stay UNKNOWN until Josh runs it (ledger: EVIDENCE-LEDGER.md, 7-state, t_547ef82d).*
