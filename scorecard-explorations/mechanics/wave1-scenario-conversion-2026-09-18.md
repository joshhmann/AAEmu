> Provenance: reaudit workstreams A/B, 2026-09-18, HEAD d0bea9e6a011d97d60157718996cfea7013ea7af.
> Tracked copy: scorecard-explorations/mechanics/wave1-scenario-conversion-2026-09-18.md (authoritative; /tmp original ephemeral).
> Body below is byte-identical to the /tmp source.

# Scenario Conversion — human-performs gates → bot-driven scenarios (Wave One, Workstream B)

Status: DESIGN / analysis only. No repo edits, no commits, no builds/tests run by this task.
Date: 2026-09-18. Repo: `/root/aaemu-dev` @ worktree HEAD (dirty; Wave One wave files present).
Input inventory: `/tmp/actor_audit.md` (sibling, read-only actor audit), `/tmp/step1_plan.md`,
`/tmp/step3_inventory.md`, `/tmp/step5_inventory.md`, `/tmp/step6_inventory.md`,
`/tmp/step7_inventory.md`, `/tmp/step8_inventory.md`,
`scorecard-explorations/mechanics/step8-bot-loop-dossier-2026-09-17.md`,
`Docs/wiki/Human-Gate-Field-Guide.md` §5, `ROADMAP.md:90-108`, `PROJECT-CONTROL.md:267-290`,
and direct source/test reads listed per block.

## Doctrine applied (absolute)

- **PlayerBots are the actors under test.** A capability is proved only when a PlayerBot
  (a real `Character` driven by `GameplayActor` over `IBotStepExecutor`/production
  services, or a real login session via `BotNetworkSession`) performs the action through
  the real engine path. Idling/roaming = deployment/liveness only.
- **A rig actor is not a bot.** `GameplayActorTestRig.CreateActor` (`AAEmu.UnitTests/Game/Core/Managers/Bots/GameplayActorTestRig.cs:713`, wrapping
  `HeadlessSession.Create` = the DB-row-less fixture path, `AAEmu.Game/Models/Game/Bots/HeadlessSession.cs:27-45`)
  produces a synthetic actor. Any result from it is **SYNTHETIC-RIG (A/R)**.
- **A bridge-op run is a scripted actor on a live server** — better than a rig (real
  engine, real MySQL, real world) but still not "a bot decided and did it".
  Labelled **BRIDGE-OP-LIVE** everywhere below and never conflated with the bot doing it.
- **`H` stays a review verdict.** Human review is required only where a visual/manual
  judgement is genuinely the deliverable (feel, client rendering, legality-of-content
  eyes). A human *executing* a scenario is never evidence that a bot capability works.
- **No new breadth.** Every block below converts an existing gate to a bot actor; none
  adds gameplay.

### Actor labels used

| Label | Meaning |
|---|---|
| REAL-BOT | `BotNetworkSession.ConnectAsync` (real login/world entry, `AAEmu.IntegrationTests/E2e/BotNetworkSession.cs:66`; "NO auth bypass" contract `:29-33`) and/or a production-provisioned headless bot taking the action |
| BRIDGE-OP-LIVE | `BotDriveClient` → `BotDriveBridge` op on the live lane; the op builds the request from the production GOAP action but the human-authored script supplies it |
| SYNTHETIC-RIG | `GameplayActorTestRig.CreateActor` / fixture `IScenarioWorldAdapter` |
| UNIT-ONLY | no actor at all (planner/search/observation-projection proof) |

### Evidence conventions (used in every "Captured evidence" field)

- Reports: `$E2E_ROOT/logs/<name>.json` (+ `.md`), where `E2E_ROOT` is the lane root
  (default `/root/aaemu-e2e`, `AAEmu.IntegrationTests/E2e/E2eStack.cs:25`); lane evidence is
  rsynced back to `soak-artifacts/<lane>/<timestamp>/` (`Scripts/e2e/run-qa-suite.sh:502-504`).
- Traces: `<name>-trace.jsonl` = per-action `ActorAuditRecord` rows with real server
  timestamps (`BotDriveBridge.cs:1200-1215` merges them per actor).
- Lane identity: `E2eStack.SourceRevision` (`E2eStack.cs:29-41`) + lane/env (`E2E_ROOT`,
  ports, `COMPOSE_PROJECT_NAME`) recorded alongside every report.

---

# A. Conversions — human-performs gates with a bot-driven replacement

## A1. M1 — Quest and progression spine (curated Solzreed route)

- **Gate as recorded (human-performs):** Field Guide §5.1 (`Docs/wiki/Human-Gate-Field-Guide.md:520-547`),
  contract REQ-M1-10; "automated evidence CLOSED; human playtest OPEN", H UNKNOWN.
- **Actor under test:** **fresh PlayerBot**, level 1 Nuian, Solzreed (zones 9/124/125) —
  `HeadlessSession.Provision` (production path, `HeadlessSession.cs:145-147`) driven by the
  production scheduler (`PlayerBotScheduler` → `BotGoalArbiterStepExecutor` → `BotRoamStepExecutor`,
  `Program.cs:324-332`), quest work through `QuestDecisionScenario` /
  `LevelingLoopScenario.RunAsScenario` (`LevelingLoopScenario.cs:511`).
- **Real engine/runtime path exercised (exact methods):**
  `GameplayActor.AcceptQuest` (`GameplayActor.cs:783`) → `PlayerBotController.AcceptQuest`
  (`AAEmu.Game/Models/Game/Bots/PlayerBotController.cs:40-42`) → `CharacterQuests.AddQuest` +
  `RunCurrentStep`; progress via the engine `UnitEvents` surface;
  `GameplayActor.AdvanceQuest` (`GameplayActor.cs:827`) → `Quest.RunCurrentStep`;
  `GameplayActor.TurnInQuest` (`GameplayActor.cs:845`) → `PlayerBotController.ReportTurnIn`
  (`:126-140`) → `QuestManager.DoReportEvents` (the exact `CSCompleteQuestContextPacket` path);
  discovery via `GameplayActor.DiscoverQuests` (`GameplayActor.cs:1162`) /
  `DiscoverSelfQuests` (`:1250`); travel via `GameplayActor.NavigateTo`
  (`GameplayActor.cs:312`) → `NavigateToInternal` (`:335-398`) → `PathNode.FindPath`
  (GeoData-gated) with `StartMove` fallback, applied by `GameplayActor.Tick` →
  `ApplyCharacterMove` (`:4248-4293`); combat via `SetTarget` (`:502`) + `Cast` (`:530`).
- **Setup / preconditions (labeled, fixture-disclosed):**
  `fresh=true` character (no GM kit, no money, no level grant) — this is the point of the
  row: level 1 → 10 by ordinary play. Disclosed setup limited to (i) staging the character
  at the canonical starter spawn, (ii) enabling the deployment knobs the run needs
  (`AAEMU_NEEDS_FARM_ENABLED`, wildlife-hunt profile), (iii) `GrowthRate` untouched for
  questing. **Nothing that performs a tested step** may be granted.
- **Bot action performed (no human input in-run):** the scheduler wakes the bot; the bot
  perceives (`Observe`/`DiscoverQuests`), picks a legal lowest-level quest, pursues its
  objectives (travel/hunt/gather/talk/use-item), turns it in, re-discovers, and repeats
  until the curated arc is exhausted — with recovery on death (`EnableDeathRecovery`,
  `LevelingLoopScenario.cs:366`) and a mid-route logout/restart checkpoint.
- **Authoritative resulting state:** live `Character` — `Character.Quests.HasQuestCompleted`
  per arc quest, `Character.Level`, bag contents, `character.Transform.World.Position`
  (region-joined), restart-persisted rows in MySQL (`characters`, `character_quests`) read
  back after a real `E2eStack.RestartGameServer()` (`E2eStack.cs:661-690`).
- **Captured evidence:** `$E2E_ROOT/logs/m1b-solzreed-route-report.json` (+ `-trace.jsonl`);
  MySQL pre/post quest-state snapshot; wire frames for locomotion (`SCOneUnitMovementPacket`,
  the `InterzoneLoopE2eTests.cs:384-398` capture pattern); `game.log` window.
- **Human review required?** **No for the capability claim.** Review only if the operator
  wants to spot-check that the route matches the curated list. The H row (Field Guide §5.1)
  remains Josh-owned and unchanged: a bot run never moves H.
- **Conversion deltas (what does NOT yet exist and must be built, in scope):**
  1. The `leveling-loop-perception` template is registered (`BotScenarioTemplates.cs:410,439`)
     but has **no live E2E driver** — only `LevelingLoopScenarioRigTests` (SYNTHETIC-RIG).
     A live driver must run the *production* scheduler loop, not `RunAsScenario`.
  2. Arc coverage beyond the bounded 254→255 leg is unproved (M1-a..M1-g arcs 250/251/265/266/354/
     2255→2266 are not driven end-to-end by any live test).
  3. `M1M2ReplayScenario` mount segment declares **NO REAL MOUNT** headless
     (`M1M2ReplayScenario.cs:668-700`, `NoMateMaterialized`); a real mount leg needs a
     materialized mate, or the criterion stays explicitly not-proved.
- **Already bot-driven (confirm, do not re-invent):** `AdventurerSpikeE2eTests`
  (`:50` `AdventurerSpike_OnLiveServer_ClearsFoxCullEndToEnd`) proves one quest (250)
  end-to-end with **REAL damage** (`Npc.DoDie` → `QuestManager.DoOnMonsterHuntEvents`);
  `Q4LiveHuntLootE2eTests` (`:48`, REAL-BOT) proves real cast-kill + caller-delta loot;
  `QuestDiscoveryE2eTests` (`:34`) proves live discovery through the MCP pipe;
  `InterzoneLoopE2eTests` (`:53`, REAL-BOT) proves the Dewstone chain + on-wire highway
  traversal + mid-route restart resume.

## A2. M2 — Golden-path baseline, two-player leg

- **Gate as recorded (human-performs):** Field Guide §5.2 (`Human-Gate-Field-Guide.md:549-564`),
  REQ-M2-5 "two players attempt the entire route from the reproducible reset state".
- **Actor under test:** **two PlayerBots**, each a real login session
  (`BotNetworkSession.ConnectAsync`) provisioned fresh, driving the golden route; the
  "two players" property is actor count, not hands.
- **Real engine/runtime path exercised:** identical to A1 per actor; plus the
  multi-actor convergence surfaces already used by the party/economy scenarios
  (`TeamManager` world-share check, `BotDriveBridge.ProvisionBotParty`'s shared-world poll).
- **Setup / preconditions:** documented reproducible reset procedure (the M2b harness's
  `E2eStack` reset + `CleanupBotRows`); two accounts; no GM repair; `Setup mode = fresh`
  recorded verbatim per row.
- **Bot action performed:** both bots run the route concurrently; **every blocker the
  human row would capture is captured as a bot-side stage failure** with stage + reason
  (`BotScenarioRunner.ScenarioStageVerdict`, `BotScenarioRunner.cs:50-55`), including
  interference between the two bots.
- **Authoritative resulting state:** both characters at the baseline/mount state;
  per-blocker `FailStage`/`FailReason` rows; persisted quest/inventory/mount rows after
  restart.
- **Captured evidence:** `$E2E_ROOT/logs/m2b-golden-route-report.json` +
  `m2b-pilot-metrics`-shaped per-cycle table; `E2e_RestartPersistence_TwoCheckpoints_FullStateMatch`
  already emits checkpoint snapshots (`AAEmu.IntegrationTests/M2bE2eTests.cs:190`).
- **Human review required?** No. The human-only half (does the route *feel* right) is M5,
  not M2; the M2 H row stays U.
- **Already bot-driven (confirm):** `M2bE2eTests` (`AAEmu.IntegrationTests/M2bE2eTests.cs:84`
  `E2e_GoldenRoute_RealNetworkFlow_Metrics`, 2 bots × 2 cycles = 4 concurrent-ish actors;
  `:190` restart two checkpoints; `:329` seeded-defect fail-before/pass-after;
  `:393` cross-cycle isolation) — REAL-BOT via real login + `BotDriveBridge` quest ops.
  `M1M2ContractReplayE2eTests` (`:39`) is BRIDGE-OP-LIVE single-bot.
- **Conversion delta:** raise actor count from 2 sequential-coded bots/cycles to N≥2
  *simultaneous* route runners and emit an explicit per-blocker register; the blocker
  register is the actual M2 deliverable and today only stage verdicts exist.

## A3. M3a — Homestead shell (two players, adjacent, one session)

- **Gate as recorded (human-performs):** Field Guide §5.3 (`Human-Gate-Field-Guide.md:566-580`);
  recorded state "COMPLETE on **bot-functional/proxy** evidence"; dashboard card reads
  "bot-functional COMPLETE" (`Scripts/playertrace-coverage/dashboard_server.py:1664`) — **flag F1**.
- **Actor under test:** **two PlayerBots** with adjacent plots, one continuous session:
  bot A = real login session over `BotNetworkSession` (the step-7/step-8 precedent
  `HomesteadBotLoopStep8E2eTests.cs:71`, `HomesteadClaimPlacePlotE2eTests.cs:53`);
  bot B = second provisioned bot for adjacency + cross-owner permission probes.
- **Real engine/runtime path exercised:**
  place → `GameplayActor.BuildHouse(267, 15596, pos)` (`GameplayActor.cs:3167`, and the GOAP
  request builder `HomesteadActions.cs:296-300`) → `HousingManager.Build`
  (`HousingManager.cs:612`) with `HousingPlacementValidator.ValidatePlacement`
  (zone + garden-radius overlap; `HousingManager.cs:642-655`);
  build-to-completion → `GameplayActor.Interact(house.ObjId, 18553)` (`GameplayActor.cs:1401`,
  house branch `:1407-1432`) → `Character.UseSkill` → `CraftEffect` (single construction
  owner) → `House.AddBuildAction` (`House.cs:172`);
  plant → `GameplayActor.Plant(15659, pos)` (`GameplayActor.cs:3030`);
  grow → engine crop phase tasks; harvest → `GameplayActor.Harvest(doodadObjId)`
  (`GameplayActor.cs:3497`);
  storage → `DoodadManager.OpenCofferDoodad` (`DoodadManager.cs:3131`, permission gate
  `coffer.AllowedToInteract`, single-opener gate) + `Character.Inventory.SplitOrMoveItem`
  (the `CSSwapItemsPacket` move, `GameplayActor.cs:3400`);
  furniture → `HousingManager.DecorateHouse` (`HousingManager.cs:1821`, the
  `CSDecorateHousePacket` path incl. `DecoLimitEvaluator`).
- **Setup / preconditions (labeled):** one `homestead kit` opt-in **before** the loop
  (design 15596 + certs; `HeadlessSession.Provision(..., provisionHomesteadKit:true)` default
  OFF, `HeadlessSession.cs:145-147,292-296`; `BotHomeSubCommand` `kit` path);
  one labeled positional staging (`homestead move`, explicitly NOT traversal proof,
  `BotDriveBridge.cs:3822-3848`); GrowthRate raised so grow→harvest lands inside the run
  (must be recorded verbatim; the M3a/M4 replay precedent does this,
  `M3aM4EconomicReplayE2eTests.cs:28-33`).
- **Bot action performed:** A claims + builds + plants + harvests + uses coffer/furniture;
  B places an adjacent plot (accepted at 16 m), a 10 m attempt is refused (overlap), and B
  attempts to open A's coffer/furniture (must be refused with a visible reason).
- **Authoritative resulting state:** two live `housings` rows (owner, template 267, transform,
  `current_step = -1`), `DoodadCoffer.ItemContainer` contents, decoration doodad rows with
  attachments, `HousingManager.GetAllHouses()` ownership projection, and the engine refusals
  (`HousingPlacementError.OverlapHouse`, permission-deny) with **no new row / no consumed item**.
- **Captured evidence:** `$E2E_ROOT/logs/m3a-shell-report.json` + `-trace.jsonl`; MySQL
  `housings`/`doodads`/`item_containers` snapshots; `game.log` refusal lines.
- **Human review required?** **Yes, narrowly** — the *client-visible* half the Field Guide
  names (coffer window contents render; furniture appears attached at the right transform;
  the overlap rejection surfaces as a client message). That is a rendering/feel judgement,
  stated as: "do the coffer/furniture/placement-refusal visuals match what the engine state
  says". The capability itself is judged from the bot run, not from the review.
- **Already bot-driven (confirm, and fix the label):**
  `HomesteadBotLoopStep8E2eTests` (`:58`) — REAL-BOT claim→construct with recovery/repeat/
  restart (`step8-bot-loop-report.json`), and `HomesteadClaimPlacePlotE2eTests` (`:43`) —
  REAL-BOT/BOT-requested claim with conservation + negatives (`step7-claim-place-plot-report.json`).
  `B1HousingRestartE2eTests` (`:59`) is BRIDGE-OP-LIVE (provision → rig → build/construct/
  decorate ops) but drives the **real** `HousingManager.Build` and `DecorateHouse` and
  asserts byte-equal MySQL rows across a PID-verified kill-9 — it is the strongest housing
  persistence artifact and should be cited as such, with its actor label honest.
- **Conversion deltas:** (i) plant/grow/harvest and coffer/furniture have **no REAL-BOT
  live driver** — M3aExitScenarioTests is SYNTHETIC-RIG
  (`AAEmu.UnitTests/Game/Models/Game/Housing/M3aExitScenarioTests.cs:131-132` `CreateActor`),
  `M3bExitPersistenceE2eTests` is **fixture DB seeding**, and the only real plant/harvest
  live path is the farm lane (`B2FarmRestartE2eTests.cs:114,124,192`,
  `NeedsFarmLoopE2eTests.cs:37` REAL-BOT via scheduler wakes). (ii) `DoodadCoffer` has **no
  actor-contract action** (no coffer member in `IGameplayActor`/`GameplayActor`) — an
  interact-only path or a new bounded action is required. (iii) F1 wording: the record must
  read "COMPLETE (proxy only, H UNKNOWN)".

## A4. M3b — Property persistence and recovery

- **Gate as recorded (human-performs):** Field Guide §5.4 (`Human-Gate-Field-Guide.md:582-597`);
  "COMPLETE with R=2 evidence; player/H UAT open".
- **Actor under test:** **two PlayerBots**, one continuous session per cycle, surviving
  N≥3 crash cycles — the bot must *re-enter the world and re-own its property* after each
  restart, not merely have rows appear.
- **Real engine/runtime path exercised:** the A3 paths, plus restart mechanics
  `E2eStack.RestartGameServer()` = PID-handle kill of only this lane's game tree
  (`E2eStack.cs:661-690`) and re-entry through the real login flow
  (`BotNetworkSession.ConnectAsync`); persistence via the engine `SaveManager` tick and the
  explicit bridge `save` pass (necessary — house rows are not written synchronously with the
  action; step-8 dossier finding 3).
- **Setup / preconditions (labeled):** the A3 kit/staging, disclosed per cycle; the
  kill-in-save window must be *provoked deterministically* (the row-lock + `INNODB_TRX`
  observation seam of `M3bExitPersistenceE2eTests.cs:340-400`) rather than raced.
- **Bot action performed:** decorate/plant → logout/relog → restart → kill-9 mid-save →
  restart → re-enter and harvest mature crops; every leg by the same two bots.
- **Authoritative resulting state:** byte-equal `housings`/`doodads`/`item_containers` rows
  including transform rotation/attachment and `plant_time` (never rewritten); no orphans,
  no duplicates; autosave p95 < 2 s at 2 homesteads + 25 bots.
- **Captured evidence:** `$E2E_ROOT/logs/m3b-restart-report.json`; pre/post MySQL dumps;
  `game-restart.log`; per-cycle verdict names (the Field Guide requires the failure to name
  the cycle).
- **Human review required?** No for persistence. (Client re-entry *feel* is optional and
  does not gate the capability.)
- **Already bot-driven (confirm):** `B1HousingRestartE2eTests.cs:59` and
  `B2FarmRestartE2eTests.cs:77` (byte-equal kills), `M51AttachedPackRestartE2eTests.cs:80`,
  `EconomyDayCycleE2eTests.cs:49`, `VillageFullDayRestartE2eTests.cs:81`,
  `HaulerMidRouteRestartE2eTests.cs:91`, `B4BotRestartPersistenceE2eTests.cs:94`,
  `MailS3RestartE2eTests.cs:39`, `ShipyardFramePersistenceE2eTests.cs:60` — all on live lanes
  with real engine paths.
- **Conversion delta (the real gap):** the recorded R=2 artifact
  `M3bExitPersistenceE2eTests` (`:89`) **seeds its homesteads, doodads and crops by direct
  INSERT** (`:99-125`, `InsertHouse`/`SeedDoodads`/`SeedCrops`) and asserts rows only. It is
  a persistence-integrity proof, **not** proof that a bot's own property survives. The
  replacement must be: the `HomesteadBotLoopStep8` claim→construct loop extended to the
  N≥3 crash cycles with the **bot's own** placed house, decorated by the bot, planted by the
  bot. That is the missing artifact — and it is a conversion, not new breadth.

## A5. M4 — Trade, crafting and transport integrity

- **Gate as recorded (human-performs):** Field Guide §5.5 (`Human-Gate-Field-Guide.md:599-617`);
  REQ-M4-5 "**four players** complete one integrated session from a clean reset state
  without GM repair"; recorded state "scripted proxy CLOSED, human deferred gate #4".
- **Actor under test:** **four PlayerBots** on one live session — hauler ×2 (pack lifecycle),
  crafter ×1 (materials→pack), adventurer/gatherer ×1 (input supply) — each a production
  provisioned bot driving its own legs; the "four players" property is actor count.
- **Real engine/runtime path exercised (per leg, exact methods):**
  gather/harvest → `GameplayActor.Harvest` (`GameplayActor.cs:3497`) / `InteractWith` (`:1459`);
  craft pack → `GameplayActor.Craft` (`:3676`) → `Character.Craft.Craft(...)` →
  `CraftManager` gates (recipe, queue-idle, skill template, bag materials, bench range/labor);
  carry/place → `GameplayActor.PutDown` (`:2222`) / `PackPickup` (`:2150`) →
  `RecoverItem().Execute` (the `CSLootOpenBagPacket` path);
  load → `GameplayActor.LoadPackOntoVehicle` (`:2324`) → `PackVehicleService.TryLoadCarriedPack`
  → `SlaveManager.AttachDoodadAtPoint`;
  board/drive → `BoardVehicle` (`:2433`) → `SlaveManager.BindSlave` (exact `CSBindSlavePacket`),
  `DriveVehicle` (`:2098`), `UnboardVehicle` (`:2601`);
  sell → `GameplayActor.SellSpecialty` (`:2820`) → `SpecialtyManager.SellSpecialty`
  (`SpecialtyManager.cs:247`): pack consumed + `ChangeLabor(-60, Commerce)` (`:348`) +
  `MailForSpeciality.Send` payout (`:322-338`, law `round(base × ratio% × 1.05)`);
  reconcile → mail read (`BotDriveBridge.cs:3187` `mails`) + `DepositMoney` (`:3285`
  → `Character.ChangeMoney(Inventory, Bank)`).
- **Setup / preconditions (labeled):** clean reset state per REQ-M2-4; labor pool + seed
  money are **setup only** and must be recorded (the replay precedent rigs 2000 LP /
  `DefaultSeedMoney`, `M3aM4ReplayScenario.cs:76`, `EconomyDayCycleScenario.cs:87`);
  GrowthRate raised for the crop leg (FAST_SIM, recorded verbatim — Field Guide §5.0 rules);
  **no GM repair of the tested legs**; the payout is the tested behavior and may not be
  granted.
- **Bot action performed:** four bots run one integrated session: gather → craft pack → load
  onto an owned vehicle → drive a defined route → unload → sell → reward lands → restart →
  repeat once.
- **Authoritative resulting state:** pack instance consumed exactly once; labor Δ = −60 per
  pack (canonical `SpecialtyManager.cs:348`); mail payout row with the formula-derived copper
  (124540/pack canonical); bank/item/currency conservation across a kill-9; vehicle attachment
  rows (cargo point, item link, `plant_time` unrewritten).
- **Captured evidence:** `$E2E_ROOT/logs/m4-integrated-report.json` (`m3a-m4-economic-replay-report.json`
  and `specialty-pack-sale-report.json` are the current shapes) + `-trace.jsonl`; MySQL
  ledger snapshot pre/post-restart; `game.log`.
- **Human review required?** No for the capability. The client-visible halves (pack renders
  on the vehicle, route travel renders) belong to M5 feel.
- **Already bot-driven (confirm):**
  `M3aM4EconomicReplayE2eTests.cs:46` (BRIDGE-OP-LIVE — real engine, scripted ops),
  `SpecialtyPackSaleE2eTests.cs:30` (BRIDGE-OP-LIVE, payout-formula criterion),
  `M51AttachedPackRestartE2eTests.cs:80`, `HaulerMidRouteRestartE2eTests.cs:91`,
  `EconomyDayCycleE2eTests.cs:49`, `B4PackSoakE2eTests.cs:114`, `B4SlaveSoakE2eTests.cs:104`
  (budgeted soak, 4 actors, 2 kill-9s), `TransferRideE2eTests.cs:76`
  (REAL-BOT wire ride), `B6MerchantConservationE2eTests.cs:56` (REAL-BOT buy/sell over real
  wire packets with conservation across kill-9).
- **Conversion deltas:** (i) no **REAL-BOT** driver performs the full
  craft→load→drive→sale chain — the six slice composers
  (`HaulerPackCraftLoadCycle.cs:50`, `HaulerDriveRouteCycle.cs:63`, `HaulerSaleDepositCycle.cs:68`,
  `HaulerVendorMultipackCycle.cs:83`, `CrafterWorkstationCycle.cs:67`, `CrafterMerchantCycle.cs:42`)
  have **zero** integration-test references and are exercised only by SYNTHETIC-RIG tests;
  (ii) the specialty payout has no real-bot driver (bridge-op only);
  (iii) the mail-take→deposit of the very same copper has **no driver of any kind** in the
  economy chain: there is no mail member in the actor contract (`IGameplayActor` action enum
  ends at the economy/party/trade actions; grep for TakeMail/MailTake is empty), the
  specialty payout is only *read* (`BotDriveBridge.cs` `mails`, `EconomyDayCycleScenario.cs:1157-1160`),
  and the slice docs bank "operating cash" instead of the payout. The real-packet take path
  itself is proven separately by `MailS3RestartE2eTests.cs:39` (REAL-BOT,
  `CSTakeAttachmentSequentially`), so the conversion is to join that proven path to the
  hauler's payout — not to invent mail semantics.

## A6. M5 / M5.3 — Movement fidelity (feel)

- **Gate as recorded (human-performs, feel):** Field Guide §5.6 (`Human-Gate-Field-Guide.md:619-634`);
  core contract N/A; open scope = geometry/fidelity regrade against the trapezoidal profile.
- **Actor under test:** **PlayerBot** performing the movement script (walk/strafe/turn,
  approach-and-stop, corner drive through a vehicle, door/NPC interaction interrupted
  mid-action) while a **second** bot/session records the wire.
- **Real engine/runtime path exercised:** `GameplayActor.MoveTo` (`GameplayActor.cs:292`) /
  `NavigateTo` (`:312`) / `MoveToUnit` (`:462`) / `NavigateToUnit` (`:323`) →
  `NavigateToInternal` → `PathNode.FindPath` + `ObstacleManager` detour (`:375-390`) →
  `StartMove` (`:405-431`) → `Tick` → `ApplyCharacterMove` (`:4248-4293`) →
  `VehicleMovementModel.ApplyUnitMove` with the trapezoidal profile
  (`ProfiledMoveSpeed`, `GameplayActor.cs:4288`), plus `Stop` (`:477`),
  `Interrupt` (`:761`), `DriveVehicle` (`:2098`).
- **Setup / preconditions:** bot staged at a known corridor; rate knobs untouched (feel must
  be measured at real speed); route recorded.
- **Bot action performed:** the ordered movement script, unmodified; a second session
  captures `SCOneUnitMovementPacket` for the actor's ObjId (the
  `InterzoneLoopE2eTests.cs:329-360` wire-tap pattern).
- **Authoritative resulting state:** per-frame position/heading/velocity series; arrival
  inside the arrival radius with no overshoot; monotone heading (no snap); zero teleports;
  stop lands and stays; interrupt terminates the leg with the full transition log.
- **Captured evidence:** `$E2E_ROOT/logs/movement-fidelity-report.json` + the raw
  `SCOneUnitMovementPacket` capture (`.jsonl`), plus `movement-stop-interrupt-report.json`
  for the stop/interrupt leg; screenshot/video only if a human review is performed.
- **Human review required?** **Yes — this is the one gate where it genuinely is.** The
  judgement is *perceptual*: does the recorded motion read as smooth/right in the client
  (no rubber-banding, no camera fight, no visually wrong cornering), judged against the
  captured motion series. The automation supplies the objective series; the human verdict is
  about what it looks like when rendered. Neither substitutes for the other.
- **Already bot-driven (confirm):** `MovementStopInterruptE2eTests.cs:38` (BRIDGE-OP-LIVE live
  leg → Running → busy reject → Interrupt, `:84-108`), `InterzoneLoopE2eTests.cs:384-398`
  (REAL-BOT on-wire displacement), `GameplayActorNavigateTests` (UNIT-ONLY, 5/5 contract
  incl. obstacle detour).
- **Conversion delta:** there is **no live-bot movement-fidelity artifact** (no
  trapezoid/arrival/heading series capture); the wire tap proves displacement, not profile
  shape. The conversion is: same wire tap, keep per-frame velocity/heading, and add the
  approach-and-stop + corner-drive segments.

## A7. M7 — Party (one human + three bots) → one bot + three bots

- **Gate as recorded (human-performs):** Field Guide §5.8 (`Human-Gate-Field-Guide.md:650-675`);
  DO-NOT-RUN until a consent path exists; the *party behavior* is the bot capability.
- **Actor under test:** **three PlayerBots** (leader + 2 members), each a production
  provisioned bot, with the fourth seat left empty (the bot-driven replacement for the
  human). No human input in-run.
- **Real engine/runtime path exercised:** invite → `GameplayActor.PartyInvite`
  (`GameplayActor.cs:1761`) → `TeamManager.AskToJoin` (`TeamManager.cs:122`, invitation
  dictionary + `SCAskToJoinTeamPacket`); accept → `GameplayActor.PartyAccept` (`:1805`) →
  `TeamManager.ReplyToJoinTeam` (`TeamManager.cs:173`) with the engine post-check
  (`Character.InParty` + `GetActiveTeamByUnit`); follow → `MoveToUnit` on the leader;
  assist → `SetTarget(leader.CurrentTarget.ObjId)`; rally → `MoveToUnit`; group encounter →
  `Cast` rotation until the elite dies.
- **Setup / preconditions (labeled):** one leader is provisioned and invites the members;
  the **consent** is the capability under test, so it must be exercised through a path that
  is either (a) the production roam auto-accept, or (b) an explicitly labeled scenario
  actor — and the report must say which. Level 20 provisioning for the encounter is setup
  (combat is not under test — the `HandlePartySpikeScenario` precedent sets level 20).
- **Bot action performed:** leader invites; members **accept** at their own scheduler wake
  (production path) or via the contract; members follow, assist the marked target, regroup
  after a wipe, and travel between legs.
- **Authoritative resulting state:** `TeamManager.GetActiveTeamByUnit` team id/owner/member
  set identical across the party; member-to-leader distance converging inside the follow
  distance; member `CurrentTarget.ObjId == leader.CurrentTarget.ObjId`; kill credit on the
  assisted target; membership preserved through member death (`PartyLifecycleFaultMatrixTests`
  unit leg `:81`).
- **Captured evidence:** `$E2E_ROOT/logs/m7-party-report.json` + `-trace.jsonl`
  (`m7-party-follow-assist-report.json` / `m7-party-spike-report.json` are the current
  shapes); `playerbot_audit` rows; `game.log`.
- **Human review required?** **Only for feel** — stacking glitches, camera/aggro visuals
  while following. The *capability* (invite accepted, follow, assist, regroup) is judged
  from the live run. A human verdict here is M7's H row and does not prove the capability.
- **Already bot-driven (confirm + the flag fix):**
  `PartyFollowAssistE2eTests.cs:40` (2 provisioned bots, invite/accept through the contract,
  follow + target copy — BRIDGE-OP-LIVE with the shared `ProvisionBotParty` machinery),
  `PartySpikeE2eTests.cs:44` (3 bots, party gate → rally → engage → real kill),
  `ExpeditionFlowE2eTests.cs:26` (5 bots). **Flag F3 correction:** the Field Guide's claim
  "no code shows a roamer auto-accepting invites"
  (`Human-Gate-Field-Guide.md:500-516`, dashboard `:1694`) is **stale for the roam executor**:
  `BotRoamStepExecutor.StepAsync` auto-accepts a pending invitation and then follows/assists
  (`BotRoamStepExecutor.cs:505-585`), with no config gate — only a scheduler wake is required.
  That makes the M7B prerequisite reachable with production bots; the Field Guide and the
  dashboard card must be corrected, and the replacement scenario must prove the accept at a
  *scheduler-driven* wake (not via a scripted contract call) to justify flipping the row.
- **Conversion delta:** the missing artifact is a **live, scheduler-driven** party run where
  the members accept on their own wake (today both live party tests form the party with
  direct contract calls from the bridge handler, `BotDriveBridge.cs:1292-1297`, `:1614-1630`).
  Also missing: mounted travel between legs (the M1M2 mount limitation applies) and the
  resurrect/regroup leg live.

## A8. M8 — Living Village day-scale scenario

- **Gate as recorded (human-observes + automation-proves):** Field Guide §5.9
  (`Human-Gate-Field-Guide.md:677-712`); DO-NOT-RUN for the human-observed day until a
  deployed profession roster exists; QUALIFIED runtime exit stands.
- **Actor under test:** a **bot village**: 2 farmers + 1 crafter + 2 haulers + 3 adventurers
  at ≥25 embodied concurrent, each a production provisioned bot whose *profession* selects
  its work composer.
- **Real engine/runtime path exercised:** farmer → `FarmerCycleScenario.Run` (harvest owned
  mature crop → deposit → replant) via `GameplayActor.Harvest/Deposit/Plant`;
  crafter → `CrafterWorkstationCycle.Run` (withdraw → `Craft` → deposit);
  hauler → `HaulerPackCraftLoadCycle` → `HaulerDriveRouteCycle` → `HaulerSaleDepositCycle`
  (craft → board → load → drive → sale → deposit → return-home);
  adventurer → the M1/M7 quest+hunt loops; all under the production scheduler
  (`PlayerBotScheduler` → arbiter → roam executor) with schedules on.
- **Setup / preconditions (labeled):** profession assignment exists **only** through the
  automated seams (`PlayerBotMetadataStore.RecordProfession`, called from
  `BotDriveBridge.cs:1945-1946`, `:2162-2163`); a roster is therefore **seeded** and must be
  declared as such. Home anchors, work anchors, and the growth rate are setup. The
  economy's opening balances are setup; the day's *transactions* are the tested behavior.
- **Bot action performed:** each villager works its own profession across a full game day,
  with ≥3 interleaved restarts, and the village must resume (schedules/homes/professions/
  inventory intact).
- **Authoritative resulting state:** per-villager `playerbot_metadata` (schedule anchors,
  last phase, profession) + inventory + ledger equality across restarts; sale/payout rows
  explicable from observed transactions (no invisible money or items); no duplication.
- **Captured evidence:** `$E2E_ROOT/logs/village-fullday-report.json`
  (`village-fullday-restart-report.json`, `village-day-restart-report.json`,
  `m8-economy-cycle-report.json` are the current shapes) + `playerbot_audit` rows +
  `soak-artifacts/<lane>/<ts>/` budget verdicts.
- **Human review required?** **Yes, for the coexistence/feel half** — the judgement is
  "the village reads as alive and nothing about the bots deletes, duplicates, or blocks my
  own state" (Field Guide M8-e/M8-f/M8-g). That is a visual/manual judgement and cannot be
  automated; it is also not evidence that any bot capability works — the loop is proved by
  the automation underneath.
- **Already bot-driven (confirm):** `VillageFullDayRestartE2eTests.cs:81` (2 villagers,
  full day, kill-9, schedule/profession/inventory/ledger equality),
  `VillageDayCycleRestartE2eTests.cs:60`, `EconomyDayCycleE2eTests.cs:49`,
  `B4FarmSoakE2eTests` / `B4PackSoakE2eTests.cs:114` / `B4SlaveSoakE2eTests.cs:104`
  (budgeted soaks with actors×duration×restarts stated),
  `SchedulerSoakStage1Tests`, `A5Tier3AcceptanceProbeTests` (opt-in 6-hour stage, default
  skipped).
- **Conversion deltas:** (i) the village roster is **2 villagers (Farmer, Crafter)** —
  `VillageProfession { Farmer = 0, Crafter = 1 }` (`VillageDayCycle.cs:37-41`); there is no
  hauler role and no adventurer role in the day composer, so the 2F/1C/2H/3A roster is **not
  composable today**. The full-day composer does chain an evening pass
  (`VillageFullDayCycle.cs:243` → `VillageEveningCycle.Run`) whose legs are
  merchant-sale → deposit → return-home (`VillageEveningCycle.cs:30-45`) — the hauler *shape*
  exists as an evening add-on for the two existing villagers, not as a hauler profession with
  its pack/vehicle lifecycle (that lives in the unreferenced `Hauler*Cycle` composers, A5);
  (ii) the roster is seeded, so M8B is a *seeded-roster* claim only and must say so;
  (iii) `HomesteadActivityModule.Enabled` defaults false with **zero** production setters
  (step-3 inventory), so a "homestead"-profession villager would be denied by arbitration
  today.
- **Flag F4/F7 guards:** QUALIFIED ≠ H (record wording must not bank the runtime exit), the
  PB-FARM-01 traps (3 m merchant range, 150 m soil horizon, maturation patrol fallthrough,
  Field Guide §5.9 warning) remain live code hazards for any farmer leg, and the 25 ambient
  citizens remain **liveness only**.

## A9. F5 — Bounded combat executor ↔ combat competence

- **Issue (flag F5):** `CombatExecutorTests` 9/9 (`AAEmu.UnitTests/Game/Core/Managers/Bots/CombatExecutorTests.cs:35`)
  is on a SYNTHETIC-RIG actor (`GameplayActorTestRig.CreateActor`, `:68,105,148,192,231,259,294,354,388`)
  and proves **timing ownership**, not combat competence; and the executor has **zero
  production callers** (only `CombatExecutor.cs:54,133` and the test file reference it).
- **Actor under test — the bot-driven replacement:** a live `PlayerBot` on the hunt leg
  (the `Q4LiveHuntLootE2eTests` / `AdventurerSpikeE2eTests` shape) whose casts route through
  the executor's learned-skill gate.
- **Real engine/runtime path exercised:** `CombatExecutor.Begin` (`CombatExecutor.cs:88`) /
  `Tick` (`:127`, engine entry at `:160`) → `UseThroughLearnedSkillGate` (`:230-243`) →
  `Skill.Use(..., bypassGcd: false, ...)`
  (`AAEmu.Game/Models/Game/Skills/Skill.cs:90`) so the engine's `SkillLastUsed`/`GlobalCooldown`
  arm actually fires (`Skill.cs:123-159`, stamp `:157`, post-GCD `:643-650`) —
  contrasted with `Unit.UseSkill` which hardcodes `bypassGcd: true` (`Unit.cs:1069-1081`)
  and is what `GameplayActor.Cast` rides (`GameplayActor.cs:530-587`).
- **Setup / preconditions:** bot at the hunt ground with a known skill + target; the
  executor clock is the engine wall clock for the live run (the deterministic
  `TimeProvider` seam stays a unit-level tool).
- **Bot action performed:** the bot casts the same skill twice inside one GCD window; the
  executor must wait out the gate and complete, without consuming retry budget while time is
  stationary, and must not duplicate an effect on retry.
- **Authoritative resulting state:** real damage/HP deltas on the target from **more than one
  kill** across the GCD boundary (not one cast), `GlobalCooldown`/`SkillLastUsed` advanced by
  the engine, no duplicate kill credit or double loot for one cast.
- **Captured evidence:** `$E2E_ROOT/logs/combat-executor-live-report.json` + `-trace.jsonl`
  with per-cast engine results (`SkillResult`) and HP deltas + `game.log` refusal lines.
- **Human review required?** No.
- **Conversion delta:** the executor must be **wired into the production cast path** (today
  the hunt/combat legs call `GameplayActor.Cast`, which bypasses GCD) — otherwise the
  capability stays UNIT-ONLY and the "learned-skill GCD is respected" claim remains unproved
  for any actor. The reverted actor-level enforcement must not be reintroduced; the executor
  owns wait/retry, as recorded (`Goap`/actor must stay unchanged).
- **Wording guard (F5):** the 9/9 must never be cited as combat competence; the report for
  this conversion is the only artifact that can claim the live leg.

---

# B. Confirmations — gates already bot-driven (they stand; actor/path/evidence named)

| Claim | Actor | Engine path (exact) | Evidence artifact | Stands? |
|---|---|---|---|---|
| Wave One 3 — provisioning isolation | bot via production `Provision` | `HeadlessSession.cs:145-147,292-296`; kit default OFF; `HeadlessSessionProvisioningTests` 12/12 | `ROADMAP.md:103` | Yes |
| Wave One 4 — planner state identity | none (unit) | `GoapPlanner.cs:68-134`, `PlanTemplateCache.cs:14`; `GoapPlannerTests` 17/17 | `ROADMAP.md:104`, `/tmp/step4_inventory.md` | Yes, UNIT-ONLY (no autonomy claim) |
| Wave One 5 — authoritative observations | rig actor + live projections | `BotWorldStateProvider.cs:25-178`; `GoapRuntimeTests` 18/18 | `ROADMAP.md:105`, `/tmp/step5_inventory.md` | Yes (projection truthfulness only) |
| Wave One 6 — bounded GCD executor | SYNTHETIC-RIG | `CombatExecutor.cs:230-243` + real `Skill` gate fields | `ROADMAP.md:106`, `CombatExecutorTests` 9/9 | Yes as timing ownership only — see A9 |
| Wave One 7 — one real homestead action | REAL-BOT (real login + bridge-built request) | `GameplayActor.BuildHouse` → `HousingManager.Build` | `HomesteadClaimPlacePlotE2eTests.cs:43`, `step7-claim-place-plot-report.json` | Yes |
| Wave One 8 — one real bot loop | REAL-BOT (single bot, live lane) | claim → construct (`Interact(houseObjId,18553)` → `Character.UseSkill` → `CraftEffect`), recovery, repeat, restart, conservation | `HomesteadBotLoopStep8E2eTests.cs:58`, `step8-bot-loop-report.json`; dossier `scorecard-explorations/mechanics/step8-bot-loop-dossier-2026-09-17.md` | Yes — with F6 guard (setup ≠ traversal; 1 bot ≠ population) |
| Quest spike (fox cull) | REAL-BOT | `Cast` → real `Npc.DoDie` → `QuestManager.DoOnMonsterHuntEvents` | `AdventurerSpikeE2eTests.cs:50`, `m7-adventurer-spike-report.json` | Yes |
| Hunt + caller-delta loot | REAL-BOT | `SetTarget`/`Cast`/`Loot` | `Q4LiveHuntLootE2eTests.cs:48`, `q4-live-hunt-loot-report.json` | Yes |
| Quest discovery | REAL-BOT + MCP pipe | `DiscoverQuests`/`DiscoverSelfQuests` | `QuestDiscoveryE2eTests.cs:34`, `quest-discovery-report.json` | Yes |
| Interzone route + resume | REAL-BOT | `CSMoveUnitPacket` wire + quest engine + restart | `InterzoneLoopE2eTests.cs:53`, `q6-interzone-loop-report.json` | Yes |
| Party follow/assist + spike + expedition | bots via `ProvisionBotParty` | `PartyInvite`/`PartyAccept` → `TeamManager`; follow/assist | `PartyFollowAssistE2eTests.cs:40`, `PartySpikeE2eTests.cs:44`, `ExpeditionFlowE2eTests.cs:26` | Yes (party formation is scripted; see A7 delta) |
| Economy day cycle + pack sale + merchant conservation | bots (bridge-scripted) / REAL-BOT buy-sell | `Craft`/`SellSpecialty`/`DepositMoney`; `CSBuy`/`CSSell` wire | `EconomyDayCycleE2eTests.cs:49`, `SpecialtyPackSaleE2eTests.cs:30`, `B6MerchantConservationE2eTests.cs:56` | Yes at their stated layers |
| Attached-pack + mid-route restart | bots (bridge-scripted) | `LoadPackOntoVehicle` → `SlaveManager.AttachDoodadAtPoint`; kill-9 byte-equality | `M51AttachedPackRestartE2eTests.cs:80`, `HaulerMidRouteRestartE2eTests.cs:91` | Yes |
| Farm loop via scheduler wakes | REAL-BOT | `NeedsFarmActivityModule` (opt-in `AAEMU_NEEDS_FARM_ENABLED`) → `BotRoamStepExecutor.StepNeedsFarmLeg` → real `Plant`/`Harvest` | `NeedsFarmLoopE2eTests.cs:37`, `needs-farm-loop-report.json`; `B2FarmRestartE2eTests.cs:77` | Yes |
| Mail S3, auction, dominion, ships/rowboat, indun, PvP handshake, duel, fishing, butcher, repair, healing, justice, transfers | REAL-BOT (real login + wire packets) | per-domain managers (`MailManager`, `AuctionManager`, `DominionManager`, `SlaveManager`/`ShipyardManager`, `IndunManager`, `Skill`/`Doodad` paths) | `mail-s3-restart-e2e-report.json`, `auction-restart-e2e-report.json`, `dominion-restart-e2e-report.json`, `rowboat-e2e-report.json`, `shipyard-frame-persistence-report.json`, `indun-party-e2e-report.json`, `indun-exit-e2e-report.json`, `pvp-handshake-e2e-report.json`, `duel-faction-swap-report.json`, `fishing-e2e-report.json`, `agriculture-butcher-report.json`, `economy-repair-report.json`, `recovery-heal-report.json`, `justice-crime` report | Yes |
| M6 soak / restart gate | bots (presence roster) | `PlayerBotScheduler` + `PopulationDirector` + `DormantBotRegistry`; H = N/A with written reason | `Human-Gate-Field-Guide.md:636-648`; soak reports under `soak-artifacts/` | Yes (H N/A stands) |
| B4 farm soak | bots (bridge-scripted provisioned sessions) | farm cycle legs via `GameplayActor` + kill-9 byte-equality | `AAEmu.IntegrationTests/E2e/B4FarmSoakE2eTests.cs` (now tracked in-repo; the Field-Guide §2 provenance caveat about it being untracked is resolved) | Yes |
| 25 ambient citizens | liveness only | `BotPresenceCoordinator` roam + `AAEMU_PRESENCE_HUNT` | dashboard `:1629`; `docker-compose.presence.yaml:18-23` | Yes — presence ≠ capability (F7) |

---

# C. Flag dispositions (from `/tmp/actor_audit.md`)

| Flag | Disposition in this conversion |
|---|---|
| **F1** — "bot-functional COMPLETE" beside the M3a human gate (`Field-Guide.md:570-571`, `dashboard_server.py:1664`) | **Word fix + conversion A3.** Rewrite the record/dashboard to "COMPLETE (proxy only, H UNKNOWN)". The bot capability is then carried by A3's two-bot live run (plant/harvest/coffer are the missing actors). |
| **F2** — R=2 vs H UAT boundary (`Field-Guide.md:589-590`) | Keep guarded; **A4** replaces the fixture-seeded `M3bExitPersistenceE2eTests` basis with a bot-owned property cycle (the seeding disclosure stays attached to the historical artifact). |
| **F3** — M7B DO-NOT-RUN until a consent path lands (`Field-Guide.md:664-671`, dashboard `:1694`) | **Correct the record**: the production roam executor already auto-accepts invitations and follows/assists (`BotRoamStepExecutor.cs:505-585`). Then convert the M7 row to a scheduler-driven 3-bot party run (A7). Until that run lands, the row stays DO-NOT-RUN. |
| **F4** — QUALIFIED exit + PB-FARM-01 traps, H UNKNOWN not DEFERRED (`Field-Guide.md:683-695`) | Keep the wording guards; **A8** keeps the automation claim scoped to the seeded roster and records the PB-FARM-01 limits. |
| **F5** — executor timing vs combat competence (`CombatExecutorTests.cs:35-89`) | **A9**: never cite 9/9 as competence; wire the executor into a live hunt leg (production wiring is currently absent). |
| **F6** — setup ≠ traversal; 1 bot ≠ population (step-8 dossier `:21`, `:120`) | Keep as an evidence-scope guard on every block: positional staging is disclosed setup (A3/A4/A7/A8), and single-bot loops never support population claims (A8). |
| **F7** — presence ≠ capability (`dashboard_server.py:1629`) | Keep; A8's village claim rests on the composer runs and ledger equality, never on roaming citizens. |

---

# D. Cross-cutting requirements for every converted scenario

1. **Declared setup vs tested behavior.** Each report carries a `setup` block naming every
   grant, staging position, rate knob, and seeded roster/precondition, and a
   `tested_behavior` block naming what the bot itself did. Anything in `setup` that
   performs a tested step invalidates the run (Field Guide §5.0 rules applied to bots:
   a cheated/kit-granted bot cannot evidence progression, economy, or ownership).
2. **Actor identity in the artifact.** Every trace row must carry the acting bot's
   `character_id` / ObjId (the step-1 finding D7 that "10 robots" traces had
   `actor_id: 0` must not recur) and a per-actor ledger.
3. **Refusals are evidence too.** Each converted scenario includes ≥1 negative case
   observed live (`Rejected` with reason) and asserts no state change from the refusal
   (the step-7 precedent).
4. **Restart where the gate names persistence**, with PID-scoped kills only
   (`E2eStack.RestartGameServer`, `E2eStack.cs:661-690`) and byte-equality (or explicit
   delta laws such as `bankΔ == refund`) as the postcondition.
5. **Layer honesty.** Every artifact prints its layer: `A` (unit), `A/R` (rig),
   `L` (live) — never merged; H is never produced by any of these runs.
6. **No freeze violation.** These conversions reuse existing engine paths and existing
   bot surfaces. Where a conversion needs a new capability to be *honest* (a coffer
   action, a mail-take action, production wiring for the executor), that is named as a
   prerequisite in the block rather than smuggled in.

---

# E. Summary of what is genuinely missing (the conversion backlog)

| # | Missing actor/path | Blocks | Converted by |
|---|---|---|---|
| 1 | Live driver for the production perception/quest loop (registration exists, no E2E) | A1, A2 | A1 |
| 2 | Bot-driven plant / grow / harvest and coffer / furniture legs | A3 | A3 |
| 3 | Bot-owned (not fixture-seeded) property restart cycles | A4 | A4 |
| 4 | Real-bot full pack→load→drive→sale chain; specialty payout; mail-take→deposit | A5 | A5 |
| 5 | Live movement-profile capture (trapezoid/arrival/heading) | A6 | A6 |
| 6 | Scheduler-driven party consent (accept at the member's own wake) + mounted travel + resurrect/regroup live | A7 | A7 |
| 7 | Hauler and adventurer roles in the village composer; non-seeded profession assignment | A8 | A8 |
| 8 | Production wiring of `CombatExecutor` into the bot cast path | A9 | A9 |

No item above is new gameplay breadth: each is the actor/observability needed to make an
already-recorded gate provable by the PlayerBots that the doctrine requires.

---

## Sources read for this document (key)

- `Docs/wiki/Human-Gate-Field-Guide.md` §0, §5.0-§5.10 (lines 18-44, 386-731), §6-§8
- `ROADMAP.md:90-108`, `PROJECT-CONTROL.md:267-290`, `STATUS.md:1-45`, `SCORECARD.md:78-135`
- `Scripts/playertrace-coverage/dashboard_server.py:1624-1708`
- `AAEmu.Game/Core/Managers/Bots/`: `GameplayActor.cs`, `IGameplayActor.cs`,
  `Goap/Actions/HomesteadActions.cs`, `Goap/Actions/FarmingActions.cs`, `CombatExecutor.cs`,
  `BotRoamStepExecutor.cs`, `PlayerBotScheduler.cs`, `BotGoalArbiterStepExecutor.cs`,
  `HomesteadActivityModule.cs`, `NeedsFarmActivityModule.cs`, `VillageDayCycle.cs`,
  `VillageEveningCycle.cs`, `VillageFullDayCycle.cs`, `BotMountManager.cs`,
  `Goap/GoalArbitrator.cs`, `Goap/BotWorldStateProvider.cs`
- `AAEmu.Game/Models/Game/Bots/`: `HeadlessSession.cs`, `BotDriveBridge.cs`,
  `PlayerBotController.cs`, `BotPath.cs`
- `AAEmu.Game/Core/Managers/`: `HousingManager.cs`, `TeamManager.cs`,
  `UnitManagers/DoodadManager.cs`, `World/SpecialtyManager.cs`
- `AAEmu.Game/Models/Game/Skills/Skill.cs`, `AAEmu.Game/Models/Game/Units/Unit.cs`,
  `AAEmu.Game/Core/Packets/C2G/CSOffsets.cs` + house/coffer/decorate handlers
- `AAEmu.IntegrationTests/`: `E2e/` (54 files sampled), `M2bE2eTests.cs`,
  `M3bExitPersistenceE2eTests.cs`, `M3bFurniturePersistenceE2eTests.cs`,
  `M4_2TradePackRestartE2eTests.cs`, `M4VehiclesE2eTests.cs`, `PresenceE2eTests.cs`
- `AAEmu.UnitTests/`: `Game/Core/Managers/Bots/*` (incl. `GameplayActorTestRig.cs`,
  `CombatExecutorTests.cs`, `HomesteadClaimPlacePlotTests.cs`, `Goap/*`),
  `Game/Models/Game/Housing/M3aExitScenarioTests.cs`,
  `Game/Core/Managers/HomesteadPlacementScenarioTests.cs`
- `/tmp/actor_audit.md`, `/tmp/step1_plan.md`, `/tmp/step3_inventory.md`,
  `/tmp/step4_inventory.md`, `/tmp/step5_inventory.md`, `/tmp/step6_inventory.md`,
  `/tmp/step7_inventory.md`, `/tmp/step8_inventory.md`
- `scorecard-explorations/mechanics/step8-bot-loop-dossier-2026-09-17.md`,
  `playerbot-capability-matrix.md`, `first-owned-small-plot-journey.md`,
  `undefined-world-mechanics-2026-08-31.md`, `pb005-tour-log-2026-09-06.md`
