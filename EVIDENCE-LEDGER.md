# Milestone Evidence Ledger — ArcheAge Slums (fork joshhmann/AAEmu)

Canonical per-milestone evidence state. Program: bot backtrack (t_ad6df90b,
Phase 0.1 card t_547ef82d). Created 2026-08-12 · Maintainer: Nei ·
Reviewed by: Rei (gate t_ee64e86b) before Phase 1/2/3 execution.
Phase 0.2 (t_4ec066d3) reconciles ROADMAP / SCORECARD / STATUS /
progression-board to THIS ledger.

## The 7 evidence states

| # | State | Earned by |
|---|-------|-----------|
| 1 | implemented | code merged to fork develop (or recorded on its branch for unreleased work) |
| 2 | deployed | reachable on a running server (prod release / presence overlay / release branch pending Josh GO) |
| 3 | bot-replay-ready | scripted replay rig exists on the tree |
| 4 | bot-replay-passed | scripted rig passed — function proven (M5-stand-in rule) |
| 5 | restart-passed | restart/persistence scenario passed (standing 3-scenario rule) |
| 6 | soak-passed | duration/load soak within approved numeric budgets |
| 7 | human-feel-accepted | Josh's feel verdict (H) — NEVER inferred from bot/scripted evidence; H=2 only after Josh runs the feel gate |

Cell values: ✅ PASS · 🔶 PARTIAL · ⏳ DEFERRED (recorded, Josh-owned) ·
⚠️ UNKNOWN (no verdict attempted) · — (not claimed / N/A).

## Rules (never-erase discipline)

- **Earned evidence is never deleted.** Cell advances are appended to the
  change log with citations; superseded states stay in the register.
- **H unknown ≠ deferred.** UNKNOWN = no human verdict attempted; DEFERRED =
  a recorded decision to postpone (with the owning card/decision).
- **Scripted-actor evidence is proxy/bot-functional**, labeled as such —
  it can never flip state 7.
- Every state transition cites a card id / commit / date. No citation = no
  transition.

## Ledger

| Milestone | implemented | deployed | bot-replay-ready | bot-replay-passed | restart-passed | soak-passed | human-feel |
|---|---|---|---|---|---|---|---|
| **M0** Foundation (CLOSED 08-03, Josh signoff) | ✅ | — (process milestone) | — | — | — | — | ✅ Josh signoff 08-03 (foundation acceptance, not a gameplay feel gate) |
| **M1** Quest/progression spine (CLOSED, automated evidence) | ✅ | ✅ prod release @ 94f498fc (08-04) | ✅ exit-test harness | ✅ 153/153 runnable, 0 FAIL, 33 SKIP / 186 quests; gate 1148/1148 · ✅ control-plane contract replay: rig 16/16 quests, contract actions only; live E2E min slice PASS (quest 251 accept→advance→turn-in, t_61a0eebb, 2026-08-13, proxy) | ✅ retroactive via M2 baseline t_cca63225 + live probe t_92a41fe6 2/2 | — | ⏳ DEFERRED — playtest verdict open (Open Decision #1, C5) · H UNKNOWN |
| **M2** Golden-path baseline (DONE, G1 gate PASSED 08-10) | ✅ @ 7f5c179f7 | — (no separate deploy record) | ✅ census harness + M2b-E2E | ✅ 4,573 PASS / 0 FAIL / 14 doc-SKIP; full gate 1495/0/1; M2b pilot 30/30 · ✅ control-plane contract replay: rig 16/16 quests incl. mount chain, contract actions only; live E2E min slice PASS (REAL mount: item 8159 → mate mounted/dismounted — per-boot objId, trace-authoritative — tightened criterion, t_61a0eebb, 2026-08-13, proxy) | ✅ automated t_c6eb12ec/t_1998cfd8, restart t_cca63225/t_c069bacd + probe t_92a41fe6, clean-host t_52755daa/t_819930ef | — | ⏳ DEFERRED to M4 close (t_46bf9b84) · H UNKNOWN |
| **M3a** Homestead shell (CLOSED 08-10, Rei ACCEPT t_449875bd) | ✅ @ 4d0427b96 | — (merged; no deploy record) | ✅ M3aExitScenarioTests rigs | ✅ 2 scripted actors, ONE session (placement→construction→crops→storage→furniture); HOUSING-01/FARM-01 C/W/H/A=2 (proxy) | — (single-session by design; persistence = M3b) | — | ⚠️ UNKNOWN — H stays UNKNOWN until Josh runs it |
| **M3b** Property persistence (CLOSED 08-11, EXIT t_accb1c63 PASS) | ✅ M3b-1..4 merged (5dc7c2fbd…) | — | ✅ M3bExitPersistenceE2eTests | ✅ EXIT E2E f5b00c686 PASS 7m08s | ✅ N=3 crash cycles incl. kill -9 mid-save + container kill, 16 rows/boot, no loss/dup; autosave p95 1301ms < 2000ms @ 25 bots + 2 homesteads; PROPERTY-01 R=2 | — | ⚠️ UNKNOWN |
| **M4** Trade/craft/transport (EXIT RECORD 08-12, Rei gate t_97e59ffc) | ✅ on release/m4-exit (f28b93fc1/e4af04a49/2907f46ff); unit gate 1778/0/1 | ⏳ release merge + deploy pending Josh GO (deployment-lane follow-up) | ✅ M4ExitIntegratedSessionTests + per-object restart E2E rigs | ✅ 4 scripted actors, real engine paths: harvest→craft→pack→load→travel→sell→repeat; negatives incl. LevelLowToUse, 801 despawn, StoreCantSellSameZone; CRAFT-01/PACK-01/SLAVE-01 R=2 (proxy) | ✅ M4_2TradePackRestart PASS 2m12s (kill -9); M4Vehicles PASS 3m09s (2× kill -9); M3bExit E2E PASS 7m03s; merged-tree re-run 1/1+1/1+M2b 5/5 (t_abe87eaf) | — (convoy-volume = M6 soak lane) | ⚠️ UNKNOWN — H unknown; playtest of integrated release deferred to deployment-lane follow-up after Josh GO |
| **M5** Gameplay Actor Contract (IN PROGRESS — active critical path, untouched by Phase 0) | 🔶 PARTIAL — UseItem/Mount/Dismount slice merged to develop @ a335e1672 (t_a5edc1e6); Interact/Loot + contract layer on feat/bot-actor-surface-b1 (unmerged); M5.1 economy actions not filed (t_f947d9ab); MCP sidecar contract tools in flight (t_446228b5, tai) | — | 🔶 branch-level rigs (B1Actions/B1ContractLayer tests); merged-tree replay not ready | — (exit tests not run on merged tree; threading-boundary A1 mandatory at exit) | — | — | — (first consumer Lane D scoped w/ JOSH GO 08-11, t_52b2b084; feel gates belong to later phases) |
| **M6** Deterministic playerbot framework (exit soak GREEN 08-11; reconciliation open) | ✅ hotfix chain + BotAppearanceFactory (91b308d71) + parity seeding (45cd3f3a9, live-verified 34 actabilities/skills/bag) + GM cmds P0 (t_7b4f9423) + E2E harness + presence overlay in-repo | ✅ presence-demo overlay live (hotfix3) — 3 citizens at Josh's spawn, zone 179; sighting ACCEPTED 08-09. ⚠️ adopt-heal look-collapse (t_555ed207): fix pushed cdf6d4a62, awaiting Rei gate; prod re-provision pending | ✅ E2E harness (real Login+Game+MySQL) | ✅ 10-bot correctness PASS; 25-bot stability PASS (H2 1.00); M2bE2e 5/5 (t_2ee39438) | ⏳ DEFERRED — B4 restart persistence (playerbot_metadata + 2-checkpoint restart test) not yet executed; A1 boundary + observability + G0-1 merge-to-develop outstanding (per M6 EXIT RECORD) | ✅ 6h/10-bot soak GREEN 08-11 (t_35167e60): 360-min, ALL 9 budgets PASS, 0 failures — verdict preserved as "passed revised approved budgets" (physics budget recalibrated t_18fccd09; GC fix t_eecc5604 merged first per Josh's ruling) | ⏳ DEFERRED (informal partial) — Josh sighting ACCEPTED 08-09 (wire-confirmed t_509ef8c2); rendered screenshots pending Josh's client; batched feel/visual/fun verdicts deferred until bot functional + restart gates green (decision contract) |

## Evidence register (citations, append-only)

- **M0:** ROADMAP §M0; STATUS M0 row; Josh signoff 2026-08-03 (workflow v4,
  guidelines, kanban templates, gate.sh verified, scorecard + 3 explorations,
  graphify 17.6k nodes, shared skill ×4 profiles, LIVING-WORLD.md, ROADMAP
  locked-shape — date is canonical).
- **M1:** ROADMAP §M1 status (2026-08-04→08-05, reconciled 08-11); STATUS M1;
  M1-M3 audit t_5b1f5494 (PASS WITH NOTES); engine-health release @ 94f498fc
  (BUG-007/008/009/010/011/012); restart retroevidence t_cca63225 + t_92a41fe6;
  control-plane contract replay t_61a0eebb (2026-08-13: rig 16/16 quests +
  live E2E min slice PASS — quest 251 spine, proxy).
- **M2:** ROADMAP §M2 (redefined 08-10 audit); STATUS M2; G1 gate @ 7f5c179f7
  (t_971d275b / gate card t_4221f85c); baseline legs t_c6eb12ec/t_1998cfd8,
  t_cca63225/t_c069bacd, t_52755daa/t_819930ef; human leg deferral t_46bf9b84;
  control-plane contract replay t_61a0eebb (2026-08-13: rig 16/16 incl.
  mount chain + live E2E min slice PASS — real mount/dismount of mate, proxy).
- **M3a:** ROADMAP §M3a; STATUS M3a; Rei gate t_449875bd ACCEPT; merged @
  4d0427b96; M3aExitScenarioTests (2 scripted actors, 16m adjacency).
- **M3b:** ROADMAP §M3b; STATUS M3b; EXIT gate t_accb1c63 PASS; merges
  5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea; E2E f5b00c686 PASS 7m08s;
  save-observation seam t_1329a833.
- **M4:** ROADMAP §M4 EXIT RECORD (2026-08-12, t_97e59ffc); merged-tree
  provenance t_abe87eaf (E2E_REBUILD=1, re-published from exact merge commit);
  A2 convoy gate t_921a7be5 (Rei ACCEPT, merged f9572e1a8); unit gate 1778/0/1;
  restart E2Es M4_2TradePackRestart / M4Vehicles / M3bExitPersistence.
- **M5:** ROADMAP §M5 (+ 08-09 audit: B1/B2 split, threading-boundary A1);
  develop merge a335e1672 (t_a5edc1e6); forward gates t_446228b5 (MCP sidecar,
  tai) + t_52b2b084 (first consumer Lane D, JOSH GO 08-11) — re-verified live
  2026-08-12 18:19 PT; M5.1 scope filing owned by t_f947d9ab (Phase 0.3).
- **M6:** ROADMAP §M6 EXIT RECORD (t_35167e60, merge eb6f637e0, gate 1592/0);
  soak attempts: #1 crash 19min (soak-failure semantics defined 08-09),
  attempt-3 6h operational PASS / physics-budget FAIL (t_1ed9881f) → RCA +
  GC fix t_eecc5604 (merged, per Josh's ruling) → budget recalibration
  t_18fccd09 (≤0.1/min + no-sustained-slow clause; stage-specific SoakBudgets
  for idle soak) → soak #4 GREEN (360.0-min, 9/9 budgets); sighting t_509ef8c2;
  parity t_747a1c44 / t_120bb6c9 / audit t_98415169; adopt-heal t_555ed207;
  GM cmds t_7b4f9423; M6.6 requirements in ROADMAP (74151e060).

## Change log (append-only)

- 2026-08-13 — BACKTRACK Phase 1 (t_61a0eebb): M1/M2 contract replay via the
  control plane — curated Solzreed golden route (16 quests through the
  first-mount chain) driven headless through IGameplayActor CONTRACT ACTIONS
  ONLY (accept_quest/advance_quest/use_item/turn_in/auto_turn_in/mount).
  Narrowed per Aya directive to the MINIMUM SLICE for the live gate: one
  canonical M1 action (quest 251 accept→advance→turn-in at real NPC 3512) +
  one M2 action (item 8159 → real mate mounted/dismounted — per-boot
  objId, trace-authoritative), live E2E
  PASS 1m31s (E2E_REBUILD=1); rig 3/3 incl. full 16/16 route. Evidence:
  request/response traces (9 records, full lifecycle) + bot-side
  observation deltas (real position movement). Mount criterion tightened
  per kimi memo item 2 — passes only on a REAL mount chain (discriminated
  outcome; no-mate headless is a declared limitation, never a silent pass).
  DECLARED PROVISIONING SHORTCUTS (kimi memo item 3, Rei ruling pending):
  character.Level=6 direct assignment + StockInventory preseed for quest
  objectives — provisioning through the normal items path, in the same
  class as the E2E driver's stock op; not quest-state mutation. GATE
  WAIVER (kimi memo item 4): full gate.sh not re-run per Aya directive —
  scoped rig gate 3/3 run; engine baseline cited from M6 soak + A1/B1
  merged-tree gate (1850/0/1 on develop). Proxy/bot-functional evidence;
  H stays UNKNOWN — no scripted evidence recorded as H=2. Ledger rows M1/M2
  advanced in the bot-replay-passed cell; prior evidence untouched.
- 2026-08-12 — ledger created (Phase 0.1, t_547ef82d) from audit findings:
  M3a/M4 = bot-replay-passed for scripted rigs, H unknown; M1/M2 human legs
  deferred; M6 = soak-passed with B4 restart deferred. Earned evidence from
  M0-M6 EXIT records carried in full — nothing erased.

*Progress = forward motion with receipts. Every cell above is evidence-gated.
Fork-local doc — never in an upstream PR.*
