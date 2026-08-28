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
| **M1** Quest/progression spine (CLOSED, automated evidence) | ✅ | ✅ prod release @ 94f498fc (08-04) | ✅ exit-test harness | ✅ 153/153 runnable, 0 FAIL, 33 SKIP / 186 quests; gate 1148/1148 · ✅ bounded autonomous PlayerBot loop `LevelingLoopScenario` 254→255 (`Observe → Discover → legal lowest-level choice → objective pursuit → turn-in → re-discover`), focused test 1/1 and `LevelingLoopScenarioRigTests` 7/7 at source/test baseline `7a572c08a32162988dedbf400bd9f8b608fb1974d`, existing report `leveling-loop-2026-08-25.md` · ✅ `M1M2ReplayScenario` fixture replay 16/16 quests with 55 actor records, ordered/scripted, fixture `Level=6` setup and no real-mount criterion; historical **full-route live PASS** remains proxy evidence (m1m2-replay, lifecycle 53/53, 76.6s, t_15787275, 2026-08-13) — neither closes full-route autonomous decision parity | ✅ retroactive via M2 baseline t_cca63225 + live probe t_92a41fe6 2/2 | — | ⏳ DEFERRED — playtest verdict open (Open Decision #1, C5) · H UNKNOWN |
| **M2** Golden-path baseline (DONE on historical G1 evidence; current loop proxy/open) | ✅ @ 7f5c179f7 | — (no separate deploy record) | ✅ census harness + M2b-E2E | ✅ historical G1: 4,573 PASS / 0 FAIL / 14 doc-SKIP; full gate 1495/0/1; M2b pilot 30/30 · ✅ historical control-plane contract replay: rig 16/16 quests incl. mount chain, contract actions only; full-route live PASS remains proxy evidence (t_15787275) | ✅ historical automated t_c6eb12ec/t_1998cfd8; restart t_cca63225/t_c069bacd + probe t_92a41fe6; clean-host t_52755daa/t_819930ef | — | ⏳ DEFERRED — original two-player/no-GM human baseline remains Josh-owned/open; current A/R deterministic reconciliation at source/test HEAD `ba530bcebec12af2bc7dc0db7451a535665bbed3`: focused aggregate 32/32 passed, 0 failed, 0 skipped (`HeadlessSessionProvisioningTests` 8/8, `M1M2ReplayScenarioRigTests` 3/3, `M1M2ReplayCastWindowRigTests` 1/1, `PlayerbotPilotTests` 6/6, `QuestScenarioTests` 12/12, `QuestScenarioTierTests` 1/1, `QuestDataCensusTests` 1/1); `PlayerbotPilotTests` 30/30 cycles and restart 2/2 are ordered-manifest/contract proxy evidence, `M1M2ReplayScenario` is fixed 16-quest ordering with a no-real-mount criterion, and `QuestScenarioTierTests` observed 4463 PASS / 110 FAIL / 14 SKIP over 4587 (T1 fail 6280), not M2 full closure; Player closes loop = Unknown/H open; Bot closes loop autonomously = Unknown/Open; no live/client/H claim |
| **M3a** Homestead shell (CLOSED 08-10, Rei ACCEPT t_449875bd) | ✅ @ 4d0427b96 | — (merged; no deploy record) | ✅ M3aExitScenarioTests rigs | ✅ 2 scripted actors, ONE session (placement→construction→crops→storage→furniture); HOUSING-01/FARM-01 C/W/H/A=2 (proxy) | — (single-session by design; persistence = M3b) | — | ⚠️ UNKNOWN — H stays UNKNOWN until Josh runs it |
| **M3b** Property persistence (CLOSED 08-11, EXIT t_accb1c63 PASS) | ✅ M3b-1..4 merged (5dc7c2fbd…) | — | ✅ M3bExitPersistenceE2eTests | ✅ EXIT E2E f5b00c686 PASS 7m08s | ✅ N=3 crash cycles incl. kill -9 mid-save + container kill, 16 rows/boot, no loss/dup; autosave p95 1301ms < 2000ms @ 25 bots + 2 homesteads; PROPERTY-01 R=2 | — | ⚠️ UNKNOWN |
| **M4** Trade/craft/transport (EXIT RECORD 08-12, Rei gate t_97e59ffc) | ✅ on release/m4-exit (f28b93fc1/e4af04a49/2907f46ff); unit gate 1778/0/1 | ⏳ release merge + deploy pending Josh GO (deployment-lane follow-up) | ✅ M4ExitIntegratedSessionTests + per-object restart E2E rigs | ✅ 4 scripted actors, real engine paths: harvest→craft→pack→load→travel→sell→repeat; negatives incl. LevelLowToUse, 801 despawn, StoreCantSellSameZone; CRAFT-01/PACK-01/SLAVE-01 R=2 (proxy) | ✅ M4_2TradePackRestart PASS 2m12s (kill -9); M4Vehicles PASS 3m09s (2× kill -9); M3bExit E2E PASS 7m03s; merged-tree re-run 1/1+1/1+M2b 5/5 (t_abe87eaf) | — (convoy-volume = M6 soak lane) | ⚠️ UNKNOWN — H unknown; playtest of integrated release deferred to deployment-lane follow-up after Josh GO |
| **M5** Gameplay Actor Contract (merged tree @ 75ac8df12 — Rei gate t_ec7f0c19: CLOSED-WITH-CAVEATS ceiling, formal close pending in gate lane) | ✅ full 11-action surface on fork develop — v1 34cf33cb + A1 c6d8f93a0 (ExecutionBoundary thread-affinity, compiled in ALL configs) + B1 761d1e81a: Observe/Move/Stop/Target/Cast/AcceptQuest/TurnInQuest/Interact/Loot/UseItem/Mount/Dismount all in GameplayActor.cs; ActorIdempotency + effect ledger (retry), ActorAuditRecord (trace) | — (contract surface; deploy story follows consumers) | ✅ per-action rigs on merged tree (GameplayActorTestRig + per-action classes) | ✅ full-route live replay t_15787275 @ 106d0a7e9 — 16/16 quests, lifecycle 53/53, REAL mount chain, 34/34 criteria, machine-readable traces; gates 1850/0/1 → 2054/0/1 → 2074/0/1 | — (no restart scenario owned by the contract exit; restart legs live in M2/M3b/M4) | — | ⚠️ UNKNOWN — H stays UNKNOWN (proxy/bot evidence only; feel gates belong to later phases — STOP LINE, cap M5.2) |
| **M5.1** Economic extension (MERGED — all Rei-gated) | ✅ Plant (t_b1d7c430) · PackPickup/PutDown (t_64ecf525) · Buy/Sell (t_8741b03d) · Deposit/Withdraw (f760256a0) · Harvest (ebff582a8) · BoardVehicle (e7e7ef0fe) · Craft (dab91ecb0) · LoadPackOntoVehicle (6c2429ae0) · DriveVehicle (6edbf0cbb) — real engine paths (doodad.Use, CharacterCraft.Craft, BindSlave/Seat.LoadPassenger, PackVehicleService→AttachDoodadAtPoint, VehicleMovementModel CSMoveUnitPacket); control-plane API/MCP sidecar/Lane D consumer (t_7b6d7a4a, t_446228b5, t_52b2b084) | — | ✅ per-action rigs on merged tree | ✅ per-action tests on merged tree (count @ 75ac8df12, t_c2dd474b): Deposit/Withdraw 21 · BoardVehicle 21 · Buy/Sell 30 · Pack 17 · Plant 14 · LoadPackOntoVehicle 14 · Harvest 8; Rei re-audit gate-time baseline 13/14/7/21/240 (t_ec7f0c19); post-merge gate 2074/0/1 | — (N/A for contract actions; gap flag: attached-pack-on-slave restart assertion MISSING — t_1b82b33f, tai) | — | ⚠️ UNKNOWN — H stays UNKNOWN (REQ-M5.1-5 live E2E leg parked t_eaee04ee @ STOP LINE) |
| **M5.2** Housing.Build (MERGED 08-14 — Rei-gated) | ✅ @ 3396d9ef1 (t_94761d55, Rei t_ebf36737 ACCEPT 3/3) — BuildHouse over the REAL HousingManager.Build engine path (exact CSCreateHousePacket handler call); scope t_2625be99 Housing.Build-FIRST locked | — | ✅ 13 canonical-rig tests | ✅ HouseBuild 14/14 post rig-fix (447c78ffe, t_18bbe650); post-merge gate 2074/0/1 | — (N/A) | — | ⚠️ UNKNOWN — H stays UNKNOWN |
| **M5.3** Core-surface close — Observe · Move · Stop · Target · Cast (SPEC'D 2026-08-16, t_d837ee0b; IMPL AUTHORIZED 2026-08-17, t_5189977b) | 🔶 v1 impls on develop since 34cf33cb2 (t_4f11a519) — canonical fidelity UNVERIFIED; Move known non-conforming (silent Transform write, GameplayActor.cs:2253-2259: ApplyPosition — no broadcast, no UnitMoveType path); **canonical dossier COMMITTED 2026-08-17** (scorecard-explorations/mechanics/m5-core-actions-canonical.md, t_5189977b — movement/targeting/cast canon, every claim DV-code/DV-data/RD-wiki flagged); impl cards t_3cac48d4/t_c73d6293 follow | — | 📋 dossier (REQ-M5.3-1) committed + cited + flagged; per-action contract tests pending impl (REQ-M5.3-10) | — (pending impl) | — (N/A — no new persistence) | — (N/A — M6 soak lane) | ⚠️ UNKNOWN — H stays UNKNOWN |
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
  save-observation seam t_1329a833; REQ-M3b-9 admin repair tooling evidence
  (PropertyRepairScanner/Service + /house_repair GM command @ 5981246ea/
  99edc67a, 13/13 scanner tests, Rei gate PASS run 1892) cited in the exit
  record + ROADMAP (t_c2dd474b, 2026-08-14).
- **M3 loop reconciliation (2026-08-28):** M3a is the ordinary
  `Character` loop **place/build → plant/harvest → storage/coffer/furniture
  state → observable ownership/contents result**; M3b restart persistence is
  separate. The prior exact source/test baseline `b9a72825f` recorded the M3
  focused aggregate **178/178**: M3a exit 1/1, M3b furniture 4/4, phase
  restart 10/10, property policy 11/11, and repair scanner 13/13. Current
  source/test HEAD `a77ef878d8fcba297c32c0228e712e0695cc4887` includes source
  commit `1a3f13dc1`; `HousingStorageFurnitureTests` is 13/13, including
  authorized-owner opening and unauthorized refusal before `OpenedBy`
  mutation. Bot/property replay remains ordered scripted/fixture proxy;
  fixture `SetPosition`/service preparation is not acceptance, and Player/H
  UAT plus live-client evidence remain open.
- **M4:** ROADMAP §M4 EXIT RECORD (2026-08-12, t_97e59ffc); merged-tree
  provenance t_abe87eaf (E2E_REBUILD=1, re-published from exact merge commit);
  A2 convoy gate t_921a7be5 (Rei ACCEPT, merged f9572e1a8); unit gate 1778/0/1;
  restart E2Es M4_2TradePackRestart / M4Vehicles / M3bExitPersistence.
- **M5:** ROADMAP §M5 (+ 08-09 audit: B1/B2 split, threading-boundary A1);
  full 11-action surface merged — v1 34cf33cb + A1 c6d8f93a0 (ExecutionBoundary
  thread-affinity, compiled in ALL configs) + B1 761d1e81a; verified on
  origin/develop @ 75ac8df12 (Rei re-audit t_ec7f0c19, 2026-08-14); M5-14
  full-route live replay t_15787275 @ 106d0a7e9 (16/16 quests, lifecycle
  53/53, REAL mount chain, 34/34 criteria); forward gates t_446228b5 (MCP
  sidecar) + t_52b2b084 (first consumer Lane D, JOSH GO 08-11); 08-12 snapshot
  superseded — preserved in change log 2026-08-14.
- **M5.1:** ROADMAP §M5.1; salvage merges f760256a0 / ebff582a8 / e7e7ef0fe /
  7a01ff57c / dab91ecb0 / 6c2429ae0 / 6edbf0cbb (all Rei-gated); real engine
  paths verified t_ec7f0c19; per-action tests on merged tree (D/W 21 ·
  BoardVehicle 21 · Buy/Sell 30 · Pack 17 · Plant 14 · LoadPack 14 · Harvest 8,
  count @ 75ac8df12, t_c2dd474b); post-merge gate 2074/0/1; REQ-M5.1-5 live
  E2E leg parked t_eaee04ee (STOP LINE); attached-pack-on-slave restart
  assertion t_1b82b33f (tai).
- **M5.2:** ROADMAP §M5.2; Housing.Build merged @ 3396d9ef1 (t_94761d55, Rei
  t_ebf36737 ACCEPT 3/3); 13 canonical-rig tests; HouseBuild 14/14 post
  rig-fix (447c78ffe, t_18bbe650); post-merge gate 2074/0/1; scope t_2625be99.
- **M5.3:** ROADMAP §M5.3 (spec t_d837ee0b, 2026-08-16); five
  core actions spec'd dossier-first (canonical dossier
  scorecard-explorations/mechanics/m5-core-actions-canonical.md required
  BEFORE implementation); v1 impls on develop since 34cf33cb2 (t_4f11a519)
  unverified — Move known non-conforming (GameplayActor.ApplyPosition,
  GameplayActor.cs:2253-2259: silent Transform write, no broadcast);
  Observe/SetTarget/Cast v1 shapes engine-true, verification pending.
  **CANONICAL DOSSIER COMMITTED 2026-08-17 (t_5189977b, REQ-M5.3-1):**
  m5-core-actions-canonical.md — (a) client-authored CSMoveUnitPacket path
  + SCOneUnitMovementPacket broadcasts + Stopping halt semantics, (b) the
  real target-set path via CSChangeTargetPacket → Unit.CurrentTarget +
  SCTargetChangedPacket, (c) cast pipeline (casting_time → CastTask schedule
  → Cast → SCSkillStarted/SCSkillEnded packets, mana/cooldown consumption,
  move-interrupt rules); every claim flagged DV-code/DV-data/RD-wiki.
  **IMPL AUTHORIZED 2026-08-17 (Josh lift on t_5189977b)** — impl cards
  t_3cac48d4/t_c73d6293 may run; M6-full/Phase-3 remain capped; review gate
  t_a844e2b1, acceptance gate t_5fa9bd73.
- **M6:** ROADMAP §M6 EXIT RECORD (t_35167e60, merge eb6f637e0, gate 1592/0);
  soak attempts: #1 crash 19min (soak-failure semantics defined 08-09),
  attempt-3 6h operational PASS / physics-budget FAIL (t_1ed9881f) → RCA +
  GC fix t_eecc5604 (merged, per Josh's ruling) → budget recalibration
  t_18fccd09 (≤0.1/min + no-sustained-slow clause; stage-specific SoakBudgets
  for idle soak) → soak #4 GREEN (360.0-min, 9/9 budgets); sighting t_509ef8c2;
  parity t_747a1c44 / t_120bb6c9 / audit t_98415169; adopt-heal t_555ed207;
  GM cmds t_7b4f9423; M6.6 requirements in ROADMAP (74151e060).

## Change log (append-only)

- 2026-08-17 — M5.3 DOSSIER (REQ-M5.3-1, t_5189977b): canonical dossier
  committed — scorecard-explorations/mechanics/m5-core-actions-canonical.md
  (movement/targeting/cast ground truth; every claim flagged DV-code /
  DV-data / RD-wiki with file:line or compact.sqlite3 queries; no invented
  mechanics; gaps recorded as [GAP]). **IMPL LIFT (Josh)**: M5.3
  implementation authorized; M6-full/Phase-3 remain capped. progression-board
  STOP LINE + M5/M5.3 rows updated; M5.3 ledger row moved out of SPEC-ONLY.
  Docs-only; no statuses beyond the lift; H stays UNKNOWN; fork-only push.
  Gate: acceptance t_5fa9bd73 (REI).
- 2026-08-16 — M5.3 SPEC (t_d837ee0b): M5.3 row ADDED to the ledger —
  SPEC-ONLY placeholders (Observe · Move · Stop · Target · Cast), no
  evidence claimed: v1 impls on develop since 34cf33cb2 (t_4f11a519)
  unverified (Move non-conforming — silent Transform write,
  GameplayActor.cs:2173-2179), dossier-first rework spec'd, implementation
  parked at M5.2 cap until Josh GO. Docs-only; no statuses changed; H stays
  UNKNOWN; STOP LINE respected (spec authorized 2026-08-16, implementation
  still capped). Review gate t_a844e2b1.
- 2026-08-14 — LEDGER CURRENCY (t_c2dd474b, filed by Rei gate t_ec7f0c19):
  M5 row refreshed to the merged tree @ 75ac8df12; M5.1 + M5.2 rows ADDED
  (engine-path ✅, bot-replay ✅ with per-action counts, restart — N/A with
  gap flag t_1b82b33f for M5.1, H UNKNOWN). Superseded M5 snapshot preserved
  verbatim: "🔶 PARTIAL — UseItem/Mount/Dismount slice merged to develop @
  a335e1672 (t_a5edc1e6); Interact/Loot + contract layer on
  feat/bot-actor-surface-b1 (unmerged); M5.1 economy actions not filed
  (t_f947d9ab); MCP sidecar contract tools in flight (t_446228b5, tai);
  🔶 branch-level rigs (B1Actions/B1ContractLayer tests); merged-tree replay
  not ready; exit tests not run on merged tree; threading-boundary A1
  mandatory at exit". Also: REQ-M3b-9 evidence citation added to ROADMAP
  §M3b exit record + summary table + progression-board (gap flag cleared —
  PropertyRepairScanner/Service + /house_repair GM command @ 5981246ea/
  99edc67a, 13/13 scanner tests, Rei gate PASS run 1892). No statuses
  changed; H stays UNKNOWN; STOP LINE respected (capped at M5.2).
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
