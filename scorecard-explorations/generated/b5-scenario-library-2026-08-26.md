# B5 — Behavioral Scenario Library (G3-B5)

*Generated: 2026-08-26 · worktree `.worktrees/b5lib` @ branch `feat/b5-scenario-library` · source-of-truth for loop stage 6 (regression-validation as registry lookup)*

## Registry shape

The executable index of record is the `SCENARIOS` associative array + `ORDER` list in
[`Scripts/e2e/bot-regression-pass.sh`](../../Scripts/e2e/bot-regression-pass.sh) (lines 31–41):
`key → fully-qualified xUnit test class`, each class a `[Collection("e2e")]` live-stack test under
`AAEmu.IntegrationTests/E2e/`. This document is the **metadata half** of that registry: one entry per
key, frozen field set (contract / inputs / observables / failure attribution / evidence / matrix rows).
The two halves are joined by the scenario key; `bot-regression-pass.sh` runs every registered key in
`ORDER` order (or a `SCENARIOS="…"` subset) and exits with the failure count.

> **Why not `BotScenarioTemplates`:** that code-defined registry
> (`AAEmu.Game/Core/Managers/Bots/BotScenarioTemplates.cs`) indexes *in-game quest-drive templates*
> (`level22-gate`, …) executed headless by `BotScenarioRunner`. The seven regression scenarios are a
> different layer — full-stack xUnit tests driving a real server through the gated E2E bridge — so the
> registry stays where the keys already resolve: the shell map (executable) + this doc (contract metadata).

### Registering a new scenario (the "free" path)

1. Add an xUnit test class `XxxE2eTests` under `AAEmu.IntegrationTests/E2e/`, `[Collection("e2e")]`,
   `[Trait("Category","e2e")]`; drive everything through the REAL login flow / bridge ops / packet
   paths — no direct DB writes (AGENTS.md #9/#10).
2. Add one line to the `SCENARIOS` array + `ORDER` list in `Scripts/e2e/bot-regression-pass.sh`.
3. Append one entry below with all seven fields, including its capability-matrix row link(s).
4. Failure attribution must resolve to the §17 taxonomy (`ActorFailureReason`,
   `AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs:973`): WrongDecision / Navigation /
   RejectedAction / StateTransition / Persistence / Starvation / FidelityError — never "bot got stuck".

---

## Entries (7)

### goldenroute → `AAEmu.IntegrationTests.E2e.M1M2ContractReplayE2eTests`

- **Test:** `M1M2ContractReplay_HeadlessPass_WithTraceEvidence` ([Fact], file `M1M2ContractReplayE2eTests.cs:37-90`)
- **Behavioral contract:** replay the canonical M1(quest)+M2(mount) exit spine headless via the
  IGameplayActor contract only. Server-side `RunMinSlice` (`M1M2ReplayScenario.cs:250-392`): rig Level=6,
  stock item 4058×3 → ACCEPT quest 251 at NPC 3512 → ADVANCE (gather act settles preseeded bag) →
  TURNIN at real NPC 3512 → criterion `m1-quest-251-completed` → M2 slice: stock Lilyut horse 8159 →
  UseItem → Mount → Dismount (stages MOUNT:ITEM/RIDE/DISMOUNT).
- **Inputs:** bridge cmd `scenario` template `m1m2-min-slice`, bot `m1m2replay`, `fresh:true`, timeout 300 s;
  quest 251 / acceptor+reporter NPC 3512 / preseed 4058×3 / mount item 8159.
- **Observable outcomes:** `response.passed==true`; quest 251 completed-and-inactive cross-checked against
  actor `Observe()`; mount segment DISCRIMINATED pass (real mate mounted+dismounted) or declared
  limitation `NoMateMaterialized` (rig note, never claimed as pass); every contract action reaches
  `ActorLifecycleState.Completed`.
- **Failure attribution:** server-side FailStage=`RUN` + human reason (accept/advance/turn_in refused,
  report NPC unresolvable, still-active-after-turn-in) surfaced in the assert message + report JSON.
- **Evidence:** `$E2E_ROOT/logs/m1m2-contract-replay-report.json`, `…-trace.jsonl`.
- **Matrix rows:** Quests (L11).

### economy → `AAEmu.IntegrationTests.E2e.EconomyDayCycleE2eTests`

- **Test:** `EconomyCycle_LedgerReconciles_AcrossGameProcessRestart` (L49–138)
- **Behavioral contract:** bot `M8Economy` runs template `m8-economy-cycle-v0`, 2 cycles:
  BUY seeds → PLANT → GROW → HARVEST → CRAFT → SELL → DEPOSIT; then `bridge {"cmd":"save"}` flushes to
  MySQL, ledger snapshot polled until persisted; game process kill -9 restart; persisted ledger must be
  FULLY equal after restart.
- **Inputs:** e2e collection fixture stack; ids live inside bridge template `m8-economy-cycle-v0`;
  env `E2E_ROOT`.
- **Observable outcomes:** passed=true; stage prefixes ×2 cycles (BUY-SEEDS-0 … DEPOSIT-MONEY-0/1);
  zero failed ledger criteria (currency/bank/stage-sums/labor/seeds EXACT reconciliation);
  bankMoney>0; MySQL matched ledger ≤180 s; post-restart equality on money, money2, ordered
  `(slot_type,template_id,SUM(count))` multiset.
- **Failure attribution:** bridge response `failStage`/`failReason`/`evidence`; missing stage prefix named;
  failing criteria enumerated.
- **Evidence:** `$E2E_ROOT/logs/m8-economy-cycle-report.json`, `…-reconcile.md`.
- **Matrix rows:** Vendors (L13), Crafting (L14), Farming (L15), Banking/storage (L23).

### fishing → `AAEmu.IntegrationTests.E2e.FishingVerificationE2eTests`

- **Test:** `Cast_FishingSkillAtWater_BiteLoop_OnLiveServer_EndToEnd` (L88–268)
- **Behavioral contract:** networked bot performs the canonical plot-809 cast loop: rig level 10 + rod 27308
  + worms 27142×60 → teleport near NPC 3480 (White Arden shore) → ≤20 cast attempts of skill 21571 at the
  nearest freshwater school (6447) via direct CSStartSkillPacket wire injection → observe worm −1,
  labor −5, actability 7 XP, loot grant through engine-true observables.
- **Inputs:** constants FishingSkillId=21571 / WormItemId=27142 / RodItemId=27308 / ActabilityId=7 /
  ShoreNpc 3480 / school templates 6447/6448; env `E2E_FISHING_ATTEMPTS`, `E2E_ROOT`.
- **Observable outcomes:** on BITE cast — labor delta ∈ (2,12) exclusive, fishing actability strictly up,
  ≥1 new item template vs baseline, worm consumed; always — 0 `Unhandled exception`/`|FATAL|` appended
  to game.log; stand-off ≤120 m after teleport.
- **Failure attribution:** per-cast classification refused / plot-not-started / no-bite / BITE;
  all-no-bite mapped onto plot-809 stages (dispatch vs ApplyReagents 10880 vs 확률 poll vs loot 10860);
  plot-error log lines hard-fail tagged `PLOT-RUNTIME FINDING`.
- **Evidence:** `$E2E_ROOT/logs/fishing-e2e-report.json`, `…-summary.md`.
- **Matrix rows:** Fishing (L18).

### duels → `AAEmu.IntegrationTests.E2e.DuelFactionSwapE2eTests`

- **Test:** `Duel_ChallengeAccept_StartBroadcastWithFlag_OnLiveServer_EndToEnd` (L41–114)
- **Behavioral contract:** two same-race bots log in at the same spawn → A injects
  CSChallengeDuelPacket(B) → B receives SCDuelChallengedPacket ≤10 s → B injects CSStartDuelPacket(accept)
  → combat flag spawns mid-waypoint from REAL geodata, factions swap Red/Blue, DuelStartTask fires at 3 s
  → SCDuelStartedPacket ≤15 s on either link → cleanup cancel (error 507).
- **Inputs:** bots `DuelistA`/`DuelistB`, accounts `e2eduelista/b`; raw CS packet bodies over the
  authenticated game link.
- **Observable outcomes:** both InWorld with distinct CharacterIds; challenged frame ≤10 s;
  started frame ≤15 s (hard); SCDuelState carries non-zero combat-flag doodad ObjId (observational).
- **Failure attribution:** staged asserts distinguish challenge vs started phase; failure message points at
  flag-spawn/geodata/faction-swap path + game-restart.log Warn lines. Rig-half transitions covered by
  `DuelManagerRigTests`.
- **Evidence:** `$E2E_ROOT/logs/duel-faction-swap-report.json`.
- **Matrix rows:** Duels (L19).

### transfers → `AAEmu.IntegrationTests.E2e.TransferRideE2eTests`

- **Test:** `Board_RideRouteSegment_Disembark_OnLiveServer_EndToEnd` (L73–289)
- **Behavioral contract:** read-only bridge `transfers` dump enumerates live transfers + seats → pick a
  bondable seat (checks TlId first-resolve shadowing) → ONE CSBoardingTransferPacket → SCBondDoodadPacket
  echoes charObjId+seatObjId ≤15 s → ride leg sends NO movement packets; position sampled via MySQL
  characters.x/y/z until >15 m displacement → CSUnbondDoodadPacket mirrors UnboardVehicle → SCUnbondDoodadPacket
  ≤10 s; final sample <25 m from last boarded sample (no teleport) and >15 m from boarding point
  (no snap-back).
- **Inputs:** bot account `e2etransferrider`; `transfer_spawns.json` world data (boot log must show
  `Spawning N Transfers` >0); env `E2E_TRANSFER_RIDE_SECONDS` (default 300).
- **Observable outcomes:** ≥2 position samples, rideDisplacement >10 m, bond/unbond frames with matching
  bc objIds, drift/snap-back bounds, 0 unhandled exceptions/FATAL in run tail.
- **Failure attribution:** honest-failure contract — writes `transfer-ride-e2e-BLOCKER.md` embedding the
  full dump, seat table, TlId-shadowing diagnosis hint, first 50 BoardingTransfer refusal log lines.
- **Evidence:** `$E2E_ROOT/logs/transfer-ride-e2e-report.json`, `…-transfers-dump.json`, `…-BLOCKER.md`.
- **Matrix rows:** Trade packs (L17, vehicle-borne pack transit — gondola wording INFERENCE).

### packrestart → `AAEmu.IntegrationTests.E2e.M51AttachedPackRestartE2eTests`

- **Test:** `M51_AttachedPackOnSlave_LoadedViaRealContractAction_SurvivesKill9_ByteEqual` (L80; gap-flag t_1b82b33f)
- **Behavioral contract:** bridge `scenario` template `m3a-m4-replay` fresh:true → farm→craft→pack→vehicle;
  LOAD-PACK fires GameplayActor.LoadPackOntoVehicle → PackVehicleService.TryLoadCarriedPack → retail
  snap-to-cargo-point; REAL save pass flushes; snapshot MySQL attachment state; kill -9 ONLY the game
  process (MySQL survives); re-snapshot must be byte-equal.
- **Inputs:** slave template 60 (farm wagon, attach points 9–12), pack doodad 6068/phase 15677, trade pack
  item 26488 (slot_type 255), storage markers 3446/4893; bot `m51packload`.
- **Observable outcomes:** pre-shape asserts (exactly 1 slaves row; exactly ONE pack binding row; markers
  present; pack items/container rows; no shared cargo slot); post-restart byte-equality per column incl.
  plant_time ±2 s tolerance and snapped transform <0.001f epsilon; scoped cleanup restores shared stack.
- **Failure attribution:** scenario failStage/failReason/evidence verbatim in assert message + report;
  restart divergences carry per-column messages naming what clobbered what.
- **Evidence:** `$E2E_ROOT/logs/m51-attached-pack-restart-report.json`.
- **Matrix rows:** Trade packs (L17); persistence asserts also back Farming (L15) / Banking (L23).

### partyspike → `AAEmu.IntegrationTests.E2e.PartySpikeE2eTests`

- **Test:** `PartySpike_OnLiveServer_PartyOfThreeKillsEliteEndToEnd` (L41–167)
- **Behavioral contract:** 3-bot party rallies on a leader, assists, and kills an elite through the REAL
  damage path (cast damage through Npc.DoDie — no rig fake), driven via bridge scenario template
  `m7-party-spike`.
- **Inputs:** live stack via `E2eStack.EnsureUp()`; bridge scenario cmd; game.log offset for tail scans.
- **Observable outcomes:** response.passed=true with criteria satisfied; kill observed end-to-end;
  0 unhandled/FATAL in scanned log tail.
- **Failure attribution:** failStage/failure/failReason from scenario response in assert messages +
  report JSON; missing/failed criteria named; H (feel) dimension stays UNKNOWN (proxy/bot-functional).
- **Evidence:** `$E2E_ROOT/logs/m7-party-spike-report.json`, `…-trace.jsonl`.
- **Matrix rows:** Parties (L21), Combat (L10).

---

## Capability-matrix coverage map

Source matrix: [`scorecard-explorations/mechanics/playerbot-capability-matrix.md`](../mechanics/playerbot-capability-matrix.md) (17 rows, L9–25).

| Matrix row | Covered by | Gap |
|---|---|---|
| Movement (L9) | — | **GAP — zero scenarios** (PB-001 open courtyards only) |
| Combat (L10) | partyspike | |
| Quests (L11) | goldenroute | |
| Loot (L12) | — | **GAP — zero scenarios** |
| Vendors (L13) | economy | |
| Crafting (L14) | economy | |
| Farming (L15) | economy | |
| Housing (L16) | — | **GAP — zero scenarios** |
| Trade packs (L17) | packrestart, transfers | |
| Fishing (L18) | fishing | |
| Duels (L19) | duels | |
| Expeditions (L20) | — | **GAP — zero scenarios** |
| Parties (L21) | partyspike | |
| Indun/dungeons (L22) | — | **GAP — zero scenarios** |
| Banking/storage (L23) | economy | |
| Chat/social presence (L24) | — | **GAP — zero scenarios** |
| Schedules & goal arbitration (L25) | — | **GAP — zero scenarios** |

Named coverage gaps (new systems registering here close them): Movement, Loot, Housing,
Expeditions, Indun, Chat/social presence, Schedules & goal arbitration.

## Leakage gate (cross-reference)

Every entry above drives bots exclusively through player-shaped surfaces; the three test-only seams it
must NOT touch are audited with negative tests in
[`b5-leakage-audit-2026-08-26.md`](b5-leakage-audit-2026-08-26.md).
