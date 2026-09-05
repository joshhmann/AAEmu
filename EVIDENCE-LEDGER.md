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
| **M3a** Homestead shell (CLOSED 08-10, Rei ACCEPT t_449875bd) | ✅ @ 4d0427b96 | — (merged; no deploy record) | ✅ M3aExitScenarioTests rigs | ✅ 2 scripted actors, ONE session (placement→construction→crops→storage→furniture); HOUSING-01/FARM-01 C/W/A=2 (proxy) | — (single-session by design; persistence = M3b) | — | ⚠️ UNKNOWN — H stays UNKNOWN until Josh runs it |
| **M3b** Property persistence (CLOSED 08-11, EXIT t_accb1c63 PASS) | ✅ M3b-1..4 merged (5dc7c2fbd…) | — | ✅ M3bExitPersistenceE2eTests | ✅ EXIT E2E f5b00c686 PASS 7m08s | ✅ N=3 crash cycles incl. kill -9 mid-save + container kill, 16 rows/boot, no loss/dup; autosave p95 1301ms < 2000ms @ 25 bots + 2 homesteads; PROPERTY-01 R=2 | — | ⚠️ UNKNOWN |
| **M4** Trade/craft/transport (EXIT RECORD 08-12, Rei gate t_97e59ffc) | ✅ on release/m4-exit (f28b93fc1/e4af04a49/2907f46ff); unit gate 1778/0/1 | ⏳ release merge + deploy pending Josh GO (deployment-lane follow-up) | ✅ M4ExitIntegratedSessionTests + per-object restart E2E rigs | ✅ 4 scripted actors, real engine paths: harvest→craft→pack→load→travel→sell→repeat; negatives incl. LevelLowToUse, 801 despawn, StoreCantSellSameZone; CRAFT-01/PACK-01/SLAVE-01 R=2 (proxy) | ✅ M4_2TradePackRestart PASS 2m12s (kill -9); M4Vehicles PASS 3m09s (2× kill -9); M3bExit E2E PASS 7m03s; merged-tree re-run 1/1+1/1+M2b 5/5 (t_abe87eaf) | — (convoy-volume = M6 soak lane) | ⚠️ UNKNOWN — H unknown; playtest of integrated release deferred to deployment-lane follow-up after Josh GO |
| **M5** Gameplay Actor Contract (merged tree @ 75ac8df12 — Rei gate t_ec7f0c19: CLOSED-WITH-CAVEATS ceiling, formal close pending in gate lane) | ✅ full 11-action surface on fork develop — v1 34cf33cb + A1 c6d8f93a0 (ExecutionBoundary thread-affinity, compiled in ALL configs) + B1 761d1e81a: Observe/Move/Stop/Target/Cast/AcceptQuest/TurnInQuest/Interact/Loot/UseItem/Mount/Dismount all in GameplayActor.cs; ActorIdempotency + effect ledger (retry), ActorAuditRecord (trace) | — (contract surface; deploy story follows consumers) | ✅ per-action rigs on merged tree (GameplayActorTestRig + per-action classes) | ✅ full-route live replay t_15787275 @ 106d0a7e9 — 16/16 quests, lifecycle 53/53, REAL mount chain, 34/34 criteria, machine-readable traces; gates 1850/0/1 → 2054/0/1 → 2074/0/1 | — (no restart scenario owned by the contract exit; restart legs live in M2/M3b/M4) | — | ⚠️ UNKNOWN — H stays UNKNOWN (proxy/bot evidence only; feel gates belong to later phases — STOP LINE, cap M5.2) |
| **M5 current decision primitive** (source/test HEAD `da0fdc61a72a15111fddc8ac627a164a5f050558`; proposal `263ecc66c474ca1c5f4b085e86ef3e47f49fd1`) | ✅ `BotDecisionProposal` / `BotDecisionSelector` / `BotDecisionCycle` | — | ✅ scoped `LevelingLoop` quest-accept consumer | ✅ `BotDecisionProposalTests` 5/5; immutable observed context, legality before preference, bounded candidates, deterministic fixed-priority/personality/tie-break, terminal postcondition, existing `GameplayActor` dispatch | — decision primitive only; broad M5 policy remains open | — | ⚠️ UNKNOWN — not universal bot autonomy or H/UAT |
| **M5.1** Economic extension (MERGED — all Rei-gated) | ✅ Plant (t_b1d7c430) · PackPickup/PutDown (t_64ecf525) · Buy/Sell (t_8741b03d) · Deposit/Withdraw (f760256a0) · Harvest (ebff582a8) · BoardVehicle (e7e7ef0fe) · Craft (dab91ecb0) · LoadPackOntoVehicle (6c2429ae0) · DriveVehicle (6edbf0cbb) — real engine paths (doodad.Use, CharacterCraft.Craft, BindSlave/Seat.LoadPassenger, PackVehicleService→AttachDoodadAtPoint, VehicleMovementModel CSMoveUnitPacket); control-plane API/MCP sidecar/Lane D consumer (t_7b6d7a4a, t_446228b5, t_52b2b084) | — | ✅ per-action rigs on merged tree | ✅ per-action tests on merged tree (count @ 75ac8df12, t_c2dd474b): Deposit/Withdraw 21 · BoardVehicle 21 · Buy/Sell 30 · Pack 17 · Plant 14 · LoadPackOntoVehicle 14 · Harvest 8; Rei re-audit gate-time baseline 13/14/7/21/240 (t_ec7f0c19); post-merge gate 2074/0/1 | — (N/A for contract actions; gap flag: attached-pack-on-slave restart assertion MISSING — t_1b82b33f, tai) | — | ⚠️ UNKNOWN — H stays UNKNOWN (REQ-M5.1-5 live E2E leg parked t_eaee04ee @ STOP LINE) |
| **M5.2** Housing.Build (MERGED 08-14 — Rei-gated) | ✅ @ 3396d9ef1 (t_94761d55, Rei t_ebf36737 ACCEPT 3/3) — BuildHouse over the REAL HousingManager.Build engine path (exact CSCreateHousePacket handler call); scope t_2625be99 Housing.Build-FIRST locked | — | ✅ 13 canonical-rig tests | ✅ HouseBuild 14/14 post rig-fix (447c78ffe, t_18bbe650); post-merge gate 2074/0/1 | — (N/A) | — | ⚠️ UNKNOWN — H stays UNKNOWN |
| **M5.3** Core-surface close — Observe · Move · Stop · Target · Cast (SPEC'D 2026-08-16, t_d837ee0b; IMPL AUTHORIZED 2026-08-17, t_5189977b; ✅ IMPL COMPLETE + MERGED 2026-08-17, Rei gate t_5fa9bd73 ACCEPT) | ✅ dossier (t_5189977b) → Move rework @ 8e9c0713a (t_3cac48d4: real 1.2 client-authored movement path — VehicleMovementModel.ApplyUnitMove / CSMoveUnitPacket family, SCOneUnitMovementPacket broadcasts per leg; REPLACES the v1 silent-Transform-write caveat) → Observe/Stop/Target/Cast + exit @ 7b9e81d7f (t_c73d6293: canonical-rig verification, exit trace) → rework @ 6b4ffe1d2 (t_09e1c671: SetTarget broadcast on engine resolve→assign→broadcast order + ExecutionBoundary) — merged-tree evidence: full gate 2102/0/1, targeted 13/13 + 1/1 + 30/30 + 5/5 | — | ✅ dossier (REQ-M5.3-1) committed + cited + flagged; per-action contract tests on merged tree (REQ-M5.3-10); exit scenario trace (REQ-M5.3-11) | — (pending impl — superseded: impl LANDED 08-17, see implemented cell) | — (N/A — no persistence surface) | ⚠️ UNKNOWN — H stays UNKNOWN (accepted historical evidence preserved; SEPARATE open items, not re-opened impl: September Move changes — trapezoidal profile a38484f9e, corner-blending branch 5fdb7a385 unmerged — need their own geometry acceptance; formal whole-milestone closure caveat stays with the Rei gate lane) |
| **M6** Deterministic playerbot framework (exit soak GREEN 08-11; reconciliation open) | ✅ hotfix chain + BotAppearanceFactory (91b308d71) + parity seeding (45cd3f3a9, live-verified 34 actabilities/skills/bag) + GM cmds P0 (t_7b4f9423) + E2E harness + presence overlay in-repo | ✅ presence-demo overlay live (hotfix3) — 3 citizens at Josh's spawn, zone 179; sighting ACCEPTED 08-09. ⚠️ adopt-heal look-collapse (t_555ed207): fix pushed cdf6d4a62, awaiting Rei gate; prod re-provision pending | ✅ E2E harness (real Login+Game+MySQL) | ✅ 10-bot correctness PASS; 25-bot stability PASS (H2 1.00); M2bE2e 5/5 (t_2ee39438) | ⏳ DEFERRED — B4 restart persistence (playerbot_metadata + 2-checkpoint restart test) not yet executed; A1 boundary + observability + G0-1 merge-to-develop outstanding (per M6 EXIT RECORD) | ✅ 6h/10-bot soak GREEN 08-11 (t_35167e60): 360-min, ALL 9 budgets PASS, 0 failures — verdict preserved as "passed revised approved budgets" (physics budget recalibrated t_18fccd09; GC fix t_eecc5604 merged first per Josh's ruling) | ⏳ DEFERRED (informal partial) — Josh sighting ACCEPTED 08-09 (wire-confirmed t_509ef8c2); rendered screenshots pending Josh's client; batched feel/visual/fun verdicts deferred until bot functional + restart gates green (decision contract) |

| **M6 current reconciliation** (source/test HEAD `da0fdc61a72a15111fddc8ac627a164a5f050558`; current mirror 2026-09-05) | ✅ M6 loop/cancellation/isolation/stage recorded | — | ✅ | ✅ focused lifecycle evidence 105/105; corrected Tier3 readiness 1/1 at `4721cbd306cbf346bfe38b7373d5adf479b6231f` (1000 seeded, 50 embodied, 950 dormant, materialized 50) | ✅ B4 restart loop recorded; owned cleanup zero in corrected rehearsal | ⏳ six-hour natural dormant-timer stage RAN 2026-08-30 (FULL 360.00003-min window, RSS-fail diagnostic — not a pass); soak #2 @ `322390b32` in flight, report pending; quiescence-budget leg only — business-state timers clause planned, no probe assertion yet | ⚠️ UNKNOWN — A/R readi…
| **M7 current reconciliation** (source/test HEAD `da0fdc61a72a15111fddc8ac627a164a5f050558`) | ✅ adventurer/party loop slices | — | ✅ | ✅ **147/147**: primary 36/36 + actor support 111/111 | — no current restart-persistence closure for the broad loop | — | ⚠️ UNKNOWN — A/R rig/proxy only; no current live client or H |
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
- **M4 loop reconciliation (2026-08-28):** Current source/test HEAD
  `6ff68e1bb4a6afe08441308acb9a485b5133c42e` records the clean ordinary
  `Character` loop **gather/harvest → craft pack → carry/place → load owned
  vehicle → drive normal route → unload → sell specialty pack for reward →
  repeat**, with per-object restart/persistence as applicable.
  `SellSpecialty` uses the canonical
  `CSSellBackpackGoodsPacket → SpecialtyManager.SellSpecialty` path, with
  ordinary merchant/pack checks, pack-consumption postcondition,
  same-zone/no-pack refusal, repeat-cycle, and idempotency coverage.
  Focused results: `M4ExitIntegratedSessionTests` 2/2,
  `EconomyDayCycleScenarioRigTests` 4/4, and
  `M3aM4ReplayScenarioRigTests` 2/2. Full normal-clone gate: 2498 total /
  2497 passed / 0 failed / 1 skipped; compiler 0/0; MCP 39 tools. The skip
  `Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG`
  and `AAEMU_E2E_DB_PASSWORD`; forced rebuild report 1067 warnings / 0
  errors. Replay remains ordered scripted/fixture proxy, direct setup
  shortcuts are not authentic acceptance, shared E2E reset is unsafe, and
  Player/Bot plus human/client QAT closure remains open.
- **M5:** ROADMAP §M5 (+ 08-09 audit: B1/B2 split, threading-boundary A1);
  full 11-action surface merged — v1 34cf33cb + A1 c6d8f93a0 (ExecutionBoundary
  thread-affinity, compiled in ALL configs) + B1 761d1e81a; verified on
  origin/develop @ 75ac8df12 (Rei re-audit t_ec7f0c19, 2026-08-14); M5-14
  full-route live replay t_15787275 @ 106d0a7e9 (16/16 quests, lifecycle
  53/53, REAL mount chain, 34/34 criteria); forward gates t_446228b5 (MCP
  sidecar) + t_52b2b084 (first consumer Lane D, JOSH GO 08-11); 08-12 snapshot
  superseded — preserved in change log 2026-08-14.
- **M5 actor decision/action loop (2026-08-28; source/test checkpoint
  `9ddc322feee4f06c55df9f429e8da3ed573c1b85`):** loop sentence = clean
  ordinary `Character` observes current state, chooses one legal
  objective/action, executes via `IGameplayActor`/normal `Character` services,
  observes terminal state/audit, and retries safely without duplicate effects.
  `GameplayActor`/M5 contract evidence covers lifecycle, single-writer,
  failure taxonomy, timeout/stuck, idempotency, and audit. Focused M5 tests
  aggregate **316/316**: `BotGoalArbiterTests` 14/14,
  `GameplayActorM53CoreSurfaceTests` 13/13,
  `PlayerBotControllerAdapterTests` 5/5,
  `GameplayActorB1ContractLayerTests` 17/17, and `GameplayActorTests` 30/30.
  `LevelingLoopScenario` remains narrow autonomous 254→255 evidence;
  `BotScenarioRunner`/`M1M2ReplayScenario`/`M3aM4ReplayScenario` remain ordered
  proxy replays. Player closure is Unknown/H or client-gated where applicable;
  universal PlayerBot decision closure is Unknown/Open. No ledger state is
  promoted; M5.3 canonical movement caveat/formal regrade and the H boundary
  remain unchanged.
- **M6 current loop and readiness (2026-08-28; source/test HEAD
  `da0fdc61a72a15111fddc8ac627a164a5f050558`):** clean ordinary
  Character/bot dormant → proximity wake/materialize → scheduled action →
  restart identity/inventory/position/metadata preservation → safe
  dematerialization. Focused M6 evidence remains **105/105**. Cancellation
  `950cfd279`, population isolation `c97909f4f`, and opt-in six-hour leg
  `155c82c66` are integrated. The corrected bounded rehearsal at
  `4721cbd306cbf346bfe38b7373d5adf479b6231f` passed 1/1 in 15m20.984s:
  seeded 1000, embodied 50, dormant 950, materialized 50, p95 259.2ms,
  RSS +2.56%, 50 dematerialized, owned cleanup zero. No six-hour execution or
  metrics are claimed; the stage remains pending operator validation.
- **M6 full-gate provenance:** the prior full gate at source/test
  `0ce518ac03a18de00fff1516aa9e794e8566bee6` remains **2504 total / 2503
  passed / 0 failed / 1 skipped**, compiler **0/0**, MCP **39 tools**; no new
  full gate was run at `da0fdc61`. The sole skip is
  `Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
  `AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`.
- **M7 current loop (2026-08-28; source/test HEAD
  `da0fdc61a72a15111fddc8ac627a164a5f050558`):** ordinary Character/PlayerBot
  discovers/accepts, navigates, chooses legal hostiles, casts, receives kill
  credit, loots, sustains/retreats, completes/repeats; group variant adds party
  invite/follow/assist/death recovery. Focused M7 **147/147** no-fail/no-skip:
  primary **36/36** plus actor support **111/111**. A/R rig/proxy only; no
  current live client or H/UAT. Bounded autonomous LevelingLoop is only
  254→255; broad decision closure remains open.
- 2026-08-28 — M6/M7 reconciliation at source/test HEAD
  `ded008de8d67ece8718e9235fd02503b43ceb6a1`: recorded M6 **105/105**
  lifecycle-focused evidence and M7 **147/147** A/R proxy evidence, with the
  six-hour soak and H/UAT boundaries preserved. No ledger state was promoted.

- 2026-08-28 — M5 actor decision/action loop reconciliation: recorded the
  clean ordinary-Character loop sentence, focused M5 **316/316** evidence,
  narrow `LevelingLoopScenario` autonomy versus ordered proxy replays, and
  Unknown/Open player/bot closure. No source, test, generated-output, or
  ledger-state change; M5.3 canonical movement caveat/formal regrade and H
  boundary preserved. Source/test checkpoint `9ddc322feee4f06c55df9f429e8da3ed573c1b85`.
- **M5 bounded decision primitive (2026-08-28; source/test HEAD
  `da0fdc61a72a15111fddc8ac627a164a5f050558`; proposal
  `263ecc66c474ca1c5f4b085e86ef3e47f49fd1`):** `BotDecisionProposal`,
  `BotDecisionSelector`, and `BotDecisionCycle` are integrated into the
  `LevelingLoop` quest-accept choice. The contract preserves immutable observed
  context, checks hard legality before preference, bounds candidates, selects
  deterministically by fixed priority/personality/tie-break, requires a terminal
  postcondition, and uses existing `GameplayActor` dispatch. Focused
  `BotDecisionProposalTests` pass **5/5**. This is a decision primitive plus a
  scoped quest consumer, not universal bot autonomy; broad M5 policy remains
  open. No ledger state is promoted by this note.
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

- 2026-09-02 — A5 MEMORY-PRESSURE DIAGNOSIS (user/live operational evidence;
  no ledger state promoted, no rows flipped): recorded the memory-pressure
  diagnosis additively in the durable A5 dossier
  `scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md`
  (new section 7) and mirrored the affected authoritative records
  (STATUS / ROADMAP / SCORECARD / PROJECT-CONTROL). Evidence is **user/live
  operational evidence** (not H/human gameplay feel, not independently
  reproduced by this tool call): prod CT133 presence-demo healthy ~6 days,
  no soak, **1,647 physics-slow warnings over 9 days**, simultaneous
  both-world spikes ~500–575 ms with matching values (example 573/574 ms);
  prod Game ~130,228 kB VmSwap on an 8 GB CT with 512 MB zram, swappiness
  60, Game VmData ~4.7 GB, MySQL/Login/Adminer/API sharing the ceiling;
  comparison/contrast soak (CT124, not a matched A/B) 0 KB swap on 48 GB
  RAM and zero warnings in 12 h; user live 573 ms spike **coincided with**
  a .NET BGC thread, ~25 MB RSS drop, and swap-in clustering (single
  reported coincidence, not causal proof). Diagnosis: memory pressure/swap
  + background GC/page faults is a **strongly supported provisional
  infrastructure root cause** (Mai's CT133 diagnosis, user/live
  operational evidence) for the **user-reported current PROD CT133 only**
  — **no longer merely UNKNOWN host scheduling for that environment**;
  the soak-time classification remains UNKNOWN (soak host had 0 swap, no
  in-soak host/GC telemetry — memory/swap does NOT explain the 12 h soak
  breaches). **A5 remains formally OPEN/UNCLOSED** until CT133 memory
  remediation is applied and a comparable post-change run confirms the
  warnings disappear; no H claim, no budget relaxation, no new
  implementation scope. Next action: memory remediation first (preferred
  CT133 → 16 GB; alternatives `DOTNET_GCHeapHardLimit` calibration or
  disabling swap with OOM risk), before/after memory/swap/GC telemetry,
  then rerun the post-remediation soak. A 1-hour calibration-lane
  telemetry run (2026-09-02) is **no new soak result**: host sidecar
  ~3,388 samples, 0 steal/CPU PSI/throttling, physics loop max 62 ms at
  boot and ≤ 40 ms steady, 0 in-window physics-slow warnings. Durable
- 2026-09-02 — A5 POST-REMEDIATION FOLLOW-UP (user/Mai operational
  evidence; additive, no ledger state promoted, no rows flipped):
  recorded the post-change observation additively in the durable A5
  dossier `scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md`
  (new section 8) and mirrored the affected authoritative records
  (STATUS / ROADMAP / SCORECARD / PROJECT-CONTROL). Evidence is
  **user/Mai operational evidence** (not H/human gameplay feel, not
  independently reproduced by this tool call): running Game PID 3057037;
  deployment since 20:06 UTC; ~10.5 h observation; CT host 16 GiB RAM /
  8 GiB swap; effective CT and game-container cgroups
  `memory.max=max`, `memory.swap.max=max`; `memory.events` zero OOM /
  zero max hits; CT 4.2 GB / game container 2.8 GB; Game VmRSS 2.67 GB,
  VmData 4.27 GB, **VmSwap 0 kB** (pre-restart ~129 MB); stack memory
  game 2.6 GiB / db 467.5 MiB / login 43.2 MiB / adminer 8.8 MiB /
  register-api 15 MB; GC trace capture alive 5.3 MB and growing; no GC
  events in ordinary logs because events are in nettrace. Behavior:
  17 physics warnings across the ~10.5 h observation (worst 340 ms) and
  22 spikes in the first 2 h post-restart (worst 807 ms) as distinct
  reported windows/classes; **500 ms+ signature absent in the later
  observed period** (the ~10.5 h window's worst is 340 ms) — the 807 ms
  first-2 h spike predates that absence, which is not claimed for the
  first-2 h window. Classification: **strongly supports** the prod CT133
  memory-pressure/swap hypothesis, **not fully proving** it — residual
  ~300 ms events keep another cause open; historical 12 h soak
  classification remains **UNKNOWN**; **no A5 pass is claimed**; budgets
  unchanged. Next closure criteria: continue GC/nettrace capture,
  correlate residual warnings with GC/thread/process/host telemetry, then
  run a comparable post-change A5 soak with **zero budget breaches** before
  closing A5. **A5 remains formally OPEN/UNCLOSED**; no H claim, no budget
  relaxation, no new implementation scope. Labeled user/Mai operational
  evidence, not H/human gameplay and not independently reproduced here.
  No code/data/client/soak changes, no commit.
- 2026-08-31 — UNDEFINED WORLD-MECHANICS CENSUS (read-only discovery;
- 2026-08-31 — UNDEFINED WORLD-MECHANICS CENSUS (read-only discovery;
  no ledger state promoted, no rows flipped): recorded the durable dossier
  `scorecard-explorations/mechanics/undefined-world-mechanics-2026-08-31.md`
  (data+code evidence only; provenance HEAD
  `0f8254dc3d914193d432fb842169e9bb07075508`, canonical DB md5
  `78b3bdbf038db3b927056106efdf91af` unchanged, target 1.2 r208022; no game
  server/client/authenticated run; H UNKNOWN everywhere) and its additive
  records: four new high-confidence SCORECARD ledger rows, all W=0/A=0/H=U —
  **AGGRO-PACK-01** (truly undefined: `aggro_links` 130 / `npc_aggro_links`
  643, 572 NPCs / 126 packs / 111 multi-member; no pack-membership consumer,
  AI help path is distance+faction heuristic only), **RESPAWN-LADDER-01**
  (data-only/hardcoded: `resurrection_waiting_times` 10 rows with siege
  ladder + 600 s penalty ignored; `CharacterCombat.cs:31-32` hardcoded
  ladder), **AUCTION-BANK-DOODAD-01** (truly undefined: 2+2 func rows,
  spawned kiosk 7983 in `arche_mall_world`, no-op/unloaded funcs, zero
  `banker=1`/`auctioneer=1` NPCs), **NPC-INTERACTION-01** (partial/undefined
  dispatch: `npc_interaction_sets` 111 / `npc_interactions` 114, 142 NPCs,
  loader/model only, hardcoded interaction menu). **BOOK-01** refreshed from
  "wiring unverified" to **verified UNWIRED** (books 72 / pages 1206 /
  contents 1873 / elems 846; `item_open_papers` 551; `ItemImpl.OpenPaper=23`
  no handler, no book packet, no-op doodad). **INDUN-01** formalized as an
  existing-dossier ledger row (NOT a new discovery) per the read-only roadmap
  mechanics-gaps audit — zero ledger rows existed (mechanic-inventory row-22
  "tracked" claim contradicted; real coverage 63/65); row added citing
  `indun-domain.md` + PB-003 exit E2E 11/11 (layer DATA→E2E-coverage) with
  Lane D slices S1-S3 (S4 deferred), H UNKNOWN. NPC-GROUP-01 recorded
  exploration-only (non-player-facing, no row); medium signals
  (common_farms behind PUBLICFARM-01, climates/zone_climates weather-state
  absent, merchant_packs label-only, content_configs/world_var_defaults/
  world_spec_configs unknown) captured without rows; rejected candidates
  listed in the dossier. No scorecard grades promoted, no milestone touched,
  no code/DB/soak/E2E change, no commit. Links: dossier + SCORECARD ledger +
  ROADMAP Lane D tracks + STATUS census note + PROJECT-CONTROL matrix rows.
- 2026-08-31 — ARCHAEOLOGY MCP (greenfield read-only data server): recorded
  the new `AAEmu.ArchaeologyMcp/` MCP stdio server as a current-slice record
  in STATUS / SCORECARD / ROADMAP / PROJECT-CONTROL and the AGENTS project
  map. Greenfield, separate-process, read-only; 24-tool surface (raw
  catalog/files/SQLite, AAPak list/read, search_everything,
  trace_references, find_quest_objectives, typed domain helpers, lookup_row,
  compare_source_data); canonical 679-table `compact.sqlite3` md5
  `78b3bdbf038db3b927056106efdf91af` (unchanged), target 1.2 r208022;
  allowlisted roots + optional env-configured AAPak; strict SQL/progress
  timeout/path/symlink/no-shell controls. Focused evidence is code/tests/
  smoke (A/R/L), not live client/H — H stays UNKNOWN. Client-neutral; does
  not change PB/M7/A5 claims. No code, no commit, no build. Links:
  `AAEmu.ArchaeologyMcp/README.md` and
  `scorecard-explorations/mechanics/archaeology-data-source-inventory.md`.
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

- 2026-08-28 — M5 actor decision/action loop reconciliation: recorded the
  clean ordinary-Character loop sentence, focused M5 **316/316** evidence,
  narrow `LevelingLoopScenario` autonomy versus ordered proxy replays, and
  Unknown/Open player/bot closure. No source, test, generated-output, or
  ledger-state change; M5.3 canonical movement caveat/formal regrade and H
  boundary preserved. Source/test checkpoint `9ddc322feee4f06c55df9f429e8da3ed573c1b85`.
- 2026-09-03 — PB-001 Navigation Toolchain, Dev Mapper, Beyond Solzreed & Obstacle Avoidance:
  Landed `NavigateToUnit` across `IGameplayActor`, `GameplayActor`, and `PlayerBotControllerAdapter`, integrated into `LevelingLoopScenario` (hunt/grind/talk/turn-in).
  Implemented in-game manual walk mode (`DevMapperService` + `/mapper` subcommands) saving `.path` and rich JSON action graphs (`DevMapperServiceTests` 5/5).
  Built bulk navigation toolchain (`redline_to_path.py`, `generate_zone_heatmap.py`, `extract_doodad_obstacles.py`).
  Mapped Western Continent expansion beyond Solzreed: Dewstone Plains (2,745 NPCs), White Arden (948 NPCs), and Marianople (1,692 NPCs), generating arterial highway routes (`highway_solzreed_to_dewstone.path` 10.2 km, `highway_dewstone_to_marianople.path` 4.0 km).
  Implemented `ObstacleManager` indexing 1,395 placed obstacles across 4 zones into a 100m 2D spatial hash grid, wired into `AiGeodataManager.CheckImpossibleWalk` for A* obstacle avoidance (`ObstacleManagerTests` 3/3).
  Full gate at `fc5c9fc1b`: 2,758 total / 2,757 passed / 0 failed / 1 skipped, script compiler 0/0, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- 2026-09-03 — PB-002 Autonomous Inter-Zone Progression & Nui Shrine Death Recovery:
  Wired `TryTransitionToNextZone` into `LevelingLoopScenario`: bots that exhaust current starting-zone quests evaluate their level and transition along arterial highways (Solzreed -> Dewstone Plains -> Marianople), relocating to regional hubs and triggering fresh quest perception sweeps.
  Wired `HandleDeathRecovery` into `LevelingLoopScenario`: bots dying in combat or leveling loops resurrect via the real `CharacterResurrection` engine path at the nearest Nui goddess shrine, relocate to the shrine anchor, and recover HP/MP to safe threshold before resuming.
  Evidence: `LevelingLoopScenarioRigTests` 37/37 green (+2 tests). Full gate: 2,760 total / 2,759 passed / 0 failed / 1 skipped, script compiler 0/0, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- 2026-09-03 — PB-COMBAT Tactical Combat Decision Tree & Class Spacing:
  Implemented deterministic `CombatDecisionTree`: evaluates class roles (melee vs ranged/caster kiting via Ability1 inference), tactical spacing, emergency flee, and priority skill execution.
  Integrated into `LevelingLoopScenario` across `HuntLeg`, `LevelLeg`, and `AbilityLevelLeg`.
  Evidence: `CombatDecisionTreeTests` 5/5 green; `LevelingLoopScenarioRigTests` 37/37 green. Full gate: 2,765 total / 2,764 passed / 0 failed / 1 skipped, script compiler 0/0, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- 2026-09-03 — PB-BAG Autonomous Bag Management, Vendoring & Durability Repair:
  Implemented `BotBagManager`: audits bag capacity/fullness, classifies vendor junk while protecting quest items and potions, and runs bulk vendoring via `actor.Sell`.
  Implemented canonical equipment repair on `GameplayActor.Repair` (`ActorActionType.Repair`) and `BotBagManager.RepairAllEquipment` at blacksmith/merchant NPCs via `Character.DoRepair`. Hardened `Character.DoRepair` and `ItemManager._config` with null safety.
  Integrated into `LevelingLoopScenario` on quest turn-ins at settlement hubs.
  Evidence: `BotBagManagerTests` 4/4 green; `LevelingLoopScenarioRigTests` 37/37 green. Full gate: 2,769 total / 2,768 passed / 0 failed / 1 skipped, script compiler 0/0, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- 2026-09-03 — PB-MOUNT Autonomous Mount Riding on Arterial Highways & Travel Mobility:
  Implemented `BotMountManager`: manages mount summoning, mounting, high-speed travel (~10.5 m/s vs 5.4 m/s foot travel), and dismounting for combat/interaction.
  Engine movement synchronization on `GameplayActor.ApplyCharacterMove`: moves active mount directly via `VehicleMovementModel.ApplyUnitMove(Character, mate, ...)` when mounted, bypassing client-ignore rules while preserving server transform synchronization.
  Integrated into `LevelingLoopScenario.TryTransitionToNextZone` for arterial highway transit between zone hubs.
  Evidence: `BotMountManagerTests` 4/4 green; `LevelingLoopScenarioRigTests` 37/37 green. Full gate: 2,773 total / 2,772 passed / 0 failed / 1 skipped, script compiler 0/0, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- 2026-09-05 — Docs/correlation workstream (read-only, no code): recorded source/test HEAD `9ad5735b2`
  (four 2026-09-04 commits: `da06470ab` SusManager teleport reset, `16112c24c`
  `ExecutionBoundary.RunUnscoped` for skill plots, `a38484f9e` trapezoidal Move profile,
  `9ad5735b2` bot-wildlife crash cluster). Live triage ~05:36 UTC: 7 `threw on target`
  (Effect 15109/skill 16210 IndexOutOfRange) + 0 boundary violations + 0 `moving a bit fast`;
  game container restarted ~46 min prior. A5 calibration correlation: 10 physics-slow warnings vs
  214,900 telemetry samples, zero host contention (steal/throttle 0.00) ⇒ in-process pause/GC
  suspect. A5 Tier-3 6h soak in flight (log header start 2026-09-05T05:21:27Z = 22:21 PDT 2026-09-04,
  HEAD `9ad5735b2`, ETA ~11:21 UTC 2026-09-05);
  report path still holds the stale 2026-08-30 `46129ae` run — no soak-pass claim. No ledger-state
  change; H stays UNKNOWN.
- 2026-09-05 — Docs closeout (code landed, docs-only): `7c0772f12` loot-race regression closed
  (deterministic AlwaysDrop pack, 40x16-thread hammer, mutation-checked fail-pre/pass-post;
  pushed to origin). .165 presence-demo rebuilt on `7c0772f12` (rollback tag
  `rollback-pre-7c0772f12` kept, healthy, 5-min baseline 0/0/0, 250 bots roaming). Live triage new
  findings: Effect 15109/skill 16210 IndexOutOfRange (GetBonuses snapshot-copy race) +
  InvalidOperationException in ClearAggroOfUnit via Npc.DoDie (aggro-table race), Effects
  15109/1134. `322390b32` closed both races (+360/-84: BonusesLock whole-body incl.
  UpdateGearBonuses slot reset, static AggroLock after per-unit proved insufficient,
  BuffToleranceTests hammer + new NpcAggroRaceTests; public API stable; lock-ordering audited
  no-nesting); full gate 2836 pass/1 fail/1 skip (sole fail = known load-dependent PvP honor flake,
  11/11 isolated green), MCP smokes 39 + 24 pass; pushed to origin. Soak #1 died at +72min by
  EXTERNAL kill (zero-failure ticks to the end, healthy game ticks, no exit markers/OOM/disk;
  SIGKILL-class session teardown ~06:33 UTC) — partial evidence only, not a pass; killer unknown.
  Soak #2 rerun ON `322390b32`: orphan `aaemu_a5_t3_sixhour-db-1` cleared, stale Aug-29 report
  renamed `.bak-pre322390b32`, launched detached HUP-proof (session leader, PPID=1), log
  `soak-run-20260905-022834.log`, early ticks [+0/x0/?0] advancing, ETA ~15:28 UTC 2026-09-05;
  calibration/pb007/dev-DB/.165 lanes untouched. A5 stays OPEN: no zero-breach post-change run yet;
  soak #2 is the candidate. No ledger-state change; H stays UNKNOWN.
- 2026-09-05 — Corrections (docs-only; no ledger-state change, earned evidence preserved):
  deployed pointer corrected to `135c4f14e` (source `322390b32`), healthy 250-bot 10-min 0/0/0 per
  director session report (not freshly queried; supersedes the `7c0772f12`/5-min line); full gate
  2836/1/1 is NOT green (1 known load-dependent PvP honor flake; isolated 11/11 is determinism
  evidence only); soak #1 cause UNKNOWN (external-kill wording was an unproved hypothesis; historic
  OOM not excludable); soak #2 window measured from post-warmup baseline (`A5_WARMUP_READY`
  09:33:11Z → ETA ≈ warmup-ready + 6h, formula never a promised clock; supersedes fixed 15:28);
  A5 FINAL triad binding — (shape) + (quiescence-budget; soak #2 is this leg's candidate, per-sample
  runtime-metrics counters) + (actual timer progression, PLANNED, no probe assertion); "preferably
  12-hour" is recommendation-only, never the exit gate; H is separate, never an A5 criterion — H
  states unchanged (DEFERRED stays deferred, UNKNOWN stays unknown); spline `5fdb7a385`
  branch-only/unit-only, geometry unproven; wildlife loot + livestock-doodad-butcher paths already in
  engine (NPC-corpse→butcher link is not a canonical requirement); B5 INDEX DONE `46fe4332d` and
  C1 `62f13fdc7` / C2 `8c198f13d` DONE by historic evidence — new B5 contract is runner evidence
  reliability only, no code fixes. Rows updated by that entry: M6 current-row soak mirror + M5.3-row accepted-08-17 evidence correction; all other rows untouched.
- 2026-09-05 — Ledger-precision note (docs-only; no state advancement, no earned-history deletion):
  M3a bot-replay-passed cell corrected `HOUSING-01/FARM-01 C/W/H/A=2 (proxy)` → `C/W/A=2 (proxy)` — the H grade never belonged in the proxy cell (existing 08-12 H-reconciliation t_547ef82d: proxy/bot evidence ≠ H=2; progression-board row 75 records C/W/A=2 with H=U; H column stays UNKNOWN). M4 deployed cell (`pending Josh GO`) and M6 B4 cell (`DEFERRED — not yet executed`) are dated historic snapshots (08-11/08-12 and 08-20-era wording, preserved above); current addendum states the historical M4 deployment `95bb1c78e` and B4 engineering DONE 2026-08-20 per the board — cited as history, NOT a new grade and NOT a fresh-deploy claim.
- 2026-09-05 — A5 soak #2 PASS (docs-only record; quiescence-budget leg closed, A5 stays OPEN):
  fresh report `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json` — probe G2-A5
  Tier-3 natural dormant-timer soak, runAtUtc 2026-09-05T15:33:11Z, config 1000 dormant/360 min/60 s,
  window 360.00008 FULL (`windowCompleted` true, `windowStatus` FULL), warmup `A5_WARMUP_READY`
  09:33:11Z (134.8 s, baseline 5635.5 MB), RSS growth 6.7 MB (budget 512), DB writes 0
  (114670→114670), SaveP95 2.25 ms/SaveMax 61 ms/0 skips, sampleCount 361, `failures: []`,
  `passed: true`, leg RUN; test binary 6h04m34s, 1/1. Code identity: report stamps `ca7762d7d`
  (roadmap docs merge) because SourceRevision is read at report time; `git diff --stat 322390b32
  ca7762d7d` = 7 markdown-only files (EVIDENCE-LEDGER, ROADMAP, SCORECARD, STATUS,
  navigation-domain, playerbot-capability-matrix, progression-board), zero code — tested binary is
  exactly `322390b32` source; launch header HEAD was `322390b32`. Roadmap meaning: closes (b1) 6h
  quiescence-budget leg with a zero-breach post-change run; still open (a) SHAPE re-shown at the
  fixed tip (last measured 2026-08-26 pre-change), (b2) timer-progression assertion (A5-W2 unbuilt).
  "Preferably 12-hour" remains recommendation-only. H separate, unchanged. Soak #1 (+72min
  external-kill, cause UNKNOWN) stays recorded as partial evidence, not a pass. No other
  ledger-state change.
- 2026-09-05 — A5 b2 prelaunch correction record (no ledger-state change,
  no grade promotion, earned history preserved): pushed `948bf9662` (b2 helper
  build + full UnitTests gate) did NOT prove runtime; prior "canaries mature
  ~67 min at GrowthRate 3600" was wrong 1000x (14.4M ms / 3600 ≈ 4 s).
  Correction committed as `a88f4df20` on `948bf9662`, stack-free verification only (no
  commit/push, no runtime): explicit per-isolated-run `E2E_GROWTH_RATE`
  (default stays 3600), 6h-canary rate 3 → ~80 min post-plant, due checked
  60–120 min INTO window, restart rate 120 → ~2 min, actual wither as the
  `DoodadFuncTimer` delay (not GrowthRate-divided), stack-aware seed IDs,
  owned canary discovery, in-window transfer observations, restart validation.
  Pre-fix RED unit regressions for sizing + restart false-passes; post-fix
  IntegrationTests
  Release build 0 errors + 25 exact-method pure facts pass (23 b2 validators
  + 2 RSS); full UnitTests gate 2844 total / 2843 passed / 0 failed /
  1 skipped, compiler 0/0, MCP 39+24 ran before the final
  IntegrationTests-only cleanup. Runtime NOT RUN: isolated real planting
  2-from-stack, bounded restart, 6h asserted soak; shape re-show also open;
  b1 historical pass unchanged; A5 OPEN; no live deployment / human-feel
  claim.

*Progress = forward motion with receipts. Every cell above is evidence-gated.
Fork-local doc — never in an upstream PR.*
