# PlayerBot Capability-Surface Matrix (code audit, develop @ 2026-09-18)

READ-ONLY audit. No file was modified; no server or test was run. Every claim below
was read from the cited source. Anything not found in code is marked UNKNOWN —
nothing is inferred.

Sources:

- `AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs` (queue + `BotActionKind` + `ExecuteKind`)
- `AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs` (contract) + `GameplayActor.cs` (implementation)
- `AAEmu.Game/Services/WebApi/Controllers/BotActionController.cs` (WebAPI)
- `AAEmu.BotControlMcp/ActionMcpServer.cs` (MCP sidecar) + `MCP-ACTION-MATRIX.md` (stale, see §B)
- `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs` (bridge / fixture surface)
- `AAEmu.Game/Models/Game/Bots/PlayerBotController.cs` (engine-direct helpers)
- Reference: `AAEmu.IntegrationTests/E2e/Q0ActorAcceptQuestPilotTests.cs` (Q0 pilot),
  `AAEmu.IntegrationTests/E2e/BotControlActionMcpE2eTests.cs` (MCP live test)

EvidenceClass legend (per instruction; autonomy = production logic chose, GOAP not required):

- CAPABILITY — externally requested, actor-traversing validated request with
  lifecycle + audit + idempotency ledger. Production-safe external control.
- ENGINE — engine-direct call with no actor wrapper (controller event-fire, direct
  manager call, direct field mutation). Real engine, but not actor evidence.
- SCRIPTED — a test harness / script chose and drove it (bridge ops, scenario
  drivers, ephemeral `new GameplayActor()` calls). Engine-true where noted, but
  never autonomy evidence.
- AUTONOMY — production scheduler / GOAP logic chose the action at runtime
  (`BotRoamStepExecutor` live actor legs, needs-farm wake legs).

Terminal-contract legend: `Q` = queue lifecycle
(Requested→Accepted→Running→Completed|Rejected(reason)|Interrupted|TimedOut,
idempotency-dedupe, pollable via `GET /api/actors/actions/{traceId}`);
`S` = synchronous `ActorRequest` (State/Result/Failure/Detail, caller-held);
`F` = fire-and-forget (`{fired:true}` regardless of engine outcome, no terminal state);
`R` = read response (no lifecycle); `N/A` = no contract.

Audit legend: `Y` = durable `ActorAuditRecord` (queue actor trace, `GET /api/actors/trace`);
`E` = record emitted on an ephemeral `new GameplayActor()` whose trace is discarded
(request object returned, no durable trail); `N` = none.

## (A) Matrix

### A1. Queue-dispatched actor capabilities (the canonical seam)

Every row here is: WebAPI `POST /api/actors/*` → `BotActionSpec(Kind)` →
`BotActionCommandQueue.ExecuteKind` → `IGameplayActor` method → real engine tail
(`GameplayActor.cs` contains zero `NotImplemented`/`NotSupported`/TODO-implement
throws — the only `throw` is the constructor null-guard; verified by search).
All rows: DirectMutation=N, FixtureOnly=N (production control surface, gated by
`AAEMU_BOT_CTRL` + `X-Auth-Token`), Terminal=`Q`, Audit=`Y`, EvidenceClass=CAPABILITY,
MisleadingName=N — except where noted.

| Capability | WebAPI | MCP | Bridge op | QueueKind | GameplayActor method | Engine subsystem | DirectMutation | FixtureOnly | Terminal contract | Audit | EvidenceClass | MisleadingName |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Observe | `POST /api/actors/observe` | `observe` | — | Observe | `Observe()` | WorldManager/region lists/char state (direct query, no packets) | N | N | Q (completes immediately, still audited) | Y | CAPABILITY | N |
| Move (absolute) | `POST /api/actors/move` | `move` | — | Move | `MoveTo(dest, speed, timeout, key)` | Transform stepping per Tick | N | N | Q; `TimedOut(Navigation)` on budget expiry | Y | CAPABILITY | N |
| Move to unit | `POST /api/actors/move_to_unit` | `move_to_unit` | — | MoveToUnit | `MoveToUnit(objId, speed, timeout, key)` | Transform stepping; target pos resolved at request time | N | N | Q | Y | CAPABILITY | N |
| Routed navigate (navmesh A*) | — (no route) | — | — | Navigate | `NavigateTo(dest, speed, timeout, key)` | GeoData navmesh A* + waypoint stepping; straight-line fallback | N | N | Q | Y | CAPABILITY | N — but externally unreachable (queue-only) |
| Routed navigate to unit | — (no route) | — | — | NavigateToUnit | `NavigateToUnit(objId, speed, timeout, key)` | Same as Navigate, target pos at request time | N | N | Q | Y | CAPABILITY | N — but externally unreachable (queue-only) |
| Stop | `POST /api/actors/stop` | `stop` | — | Stop | `Stop()` | Interrupts running request (Interrupted, "stop requested"); no-op idle | N | N | Q | Y | CAPABILITY | N |
| Target | `POST /api/actors/target` | `target` | — | Target | `SetTarget(objId)` | `Unit.CurrentTarget` | N | N | Q | Y | CAPABILITY | N |
| Cast (unit target) | `POST /api/actors/cast` | `cast` | `drive cast` uses actor directly, NOT this route (see §B) | Cast | `Cast(skillId, targetObjId, key)` | `Character.UseSkill` (CSStartSkillPacket learned-skill branch) | N | N | Q | Y | CAPABILITY | N |
| Interact (doodad, explicit skill) | `POST /api/actors/interact` | `interact` | `farm interact` uses actor directly, NOT this route (see §B) | Interact | `Interact(doodadObjId, skillId, key)` | `Doodad.Use` (skill branch, or skill-less loot-func branch when skillId=0) | N | N | Q | Y | CAPABILITY | PARTIAL — name collides with NPC "interaction"; this verb is doodad-only (see §E) |
| InteractWith (doodad, derived skill) | `POST /api/actors/interact_with` | `interact_with` | — | InteractWith | `InteractWith(doodadObjId, key)` | Derived use-skill via `DoodadManager.GetFunc` matching → `Skill.Use`/InteractionEffect chain, else direct `Doodad.Use`; fail-closed observable-delta post-check | N | N | Q | Y | CAPABILITY | PARTIAL — same doodad-only caveat (see §E) |
| Talk (NPC quest credit) | `POST /api/actors/talk` | `talk` | `drive talk` is controller event-fire, NOT this route (see §B) | Talk | `Talk(npcObjId, key)` | `QuestManager.DoTalkMadeEvents` per active quest w/ talk-family objective (CSQuestTalkMadePacket 0x0da path); range pre-flight + delta post-check | N | N | Q | Y | CAPABILITY | N — this IS the NPC-talk production path (see §E) |
| Loot | `POST /api/actors/loot` | `loot` | `drive loot` uses actor directly, NOT this route (see §B) | Loot | `Loot(ownerObjId, key)` | `LootingContainer.OpenBag` lootAll (CSLootOpenBagPacket path); engine consumes grants, retries can't duplicate | N | N | Q | Y | CAPABILITY | N |
| UseItem | `POST /api/actors/use_item` | `use_item` | `mail use` uses actor directly, NOT this route (see §B) | UseItem | `UseItem(templateId, targetObjId, key)` | `Skill.Use` w/ `SkillItem` caster (CSStartSkillPacket SkillItem branch) | N | N | Q | Y | CAPABILITY | N |
| Equip | `POST /api/actors/equip` | `equip` | — | Equip | `Equip(templateId, key)` | `Inventory.SplitOrMoveItem` (CSSwapItemsPacket Inventory→Equipment; slot from `GetAllowedGearSlots`) | N | N | Q | Y | CAPABILITY | N |
| Mount | `POST /api/actors/mount` | `mount` | — | Mount | `Mount(mateObjId, key)` | `MateManager.MountMate` (CSMountMatePacket; needs real GameConnection) | N | N | Q | Y | CAPABILITY | N |
| Dismount | `POST /api/actors/dismount` | `dismount` | — | Dismount | `Dismount(mateObjId, key)` | `MateManager.UnMountMate` (CSUnMountMatePacket) | N | N | Q | Y | CAPABILITY | N |
| AcceptQuest | `POST /api/actors/accept_quest` | `accept_quest` | `drive accept` is controller-direct, NOT this route (see §B) | AcceptQuest | `AcceptQuest(questId, acceptorType, acceptorId, key)` | `CharacterQuests.AddQuest` (Q0 pilot path) | N | N | Q | Y | CAPABILITY | N |
| AdvanceQuest | `POST /api/actors/advance_quest` | `advance_quest` | `drive advance` is controller-direct, NOT this route | AdvanceQuest | `AdvanceQuest(questId, key)` | Quest step machine `RunCurrentStep` evaluation | N | N | Q | Y | CAPABILITY | N |
| TurnInQuest (NPC) | `POST /api/actors/turn_in_quest` | `turn_in_quest` | `drive report` is controller-direct, NOT this route | TurnInQuest | `TurnInQuest(questId, npcObjId, reward, key)` | `QuestManager.DoReportEvents` (CSCompleteQuestContextPacket path) + one step advance | N | N | Q | Y | CAPABILITY | N |
| TurnInDoodad | `POST /api/actors/turn_in_doodad` | `turn_in_doodad` | `drive reportDoodad` is controller-direct, NOT this route | TurnInDoodad | `TurnInAtDoodad(questId, doodadObjId, reward, key)` | `DoReportEvents` doodad branch | N | N | Q | Y | CAPABILITY | N |
| AutoTurnIn | `POST /api/actors/auto_turn_in` | `auto_turn_in` | `drive autoTurnIn` is controller-direct, NOT this route | AutoTurnIn | `AutoTurnInQuest(questId, reward, key)` | `DoReportEvents` third branch (no world target) | N | N | Q | Y | CAPABILITY | N |
| DiscoverQuests | `POST /api/actors/discover_quests` | `discover_quests` | — | DiscoverQuests | `DiscoverQuests(targetObjId, key)` | Offer linkage (ConAcceptNpc/Doodad/Kill) filtered by real AddQuest pre-conditions; query, no mutation | N | N | Q (immediate, still audited) | Y | CAPABILITY | N |
| DiscoverSelfQuests | `POST /api/actors/discover_self_quests` | `discover_self_quests` | — | DiscoverSelfQuests | `DiscoverSelfQuests(key)` | Item/sphere/level channels w/ real pre-conditions; query, no mutation | N | N | Q (immediate, still audited) | Y | CAPABILITY | N |
| DepositMoney | `POST /api/actors/deposit_money` | `deposit_money` | — | DepositMoney | `DepositMoney(amount, key)` | `Character.ChangeMoney` (CSDepositMoneyPacket path) | N | N | Q | Y | CAPABILITY | N |
| WithdrawMoney | `POST /api/actors/withdraw_money` | `withdraw_money` | — | WithdrawMoney | `WithdrawMoney(amount, key)` | `Character.ChangeMoney` (CSWithdrawMoneyPacket path) | N | N | Q | Y | CAPABILITY | N |
| DepositItem | `POST /api/actors/deposit_item` | `deposit_item` | — | DepositItem | `DepositItem(templateId, key)` | `Inventory.SplitOrMoveItem` (CSSwapItemsPacket Inventory→Bank) | N | N | Q | Y | CAPABILITY | N |
| WithdrawItem | `POST /api/actors/withdraw_item` | `withdraw_item` | — | WithdrawItem | `WithdrawItem(templateId, key)` | `Inventory.SplitOrMoveItem` (CSSwapItemsPacket Bank→Inventory) | N | N | Q | Y | CAPABILITY | N |
| Plant | `POST /api/actors/plant` | `plant` | `farm plant` uses actor directly, NOT this route (see §B) | Plant | `Plant(seedTemplate, pos, zRot, scale, key)` | `DoodadManager.CreatePlayerDoodad` (CSCreateDoodadPacket path) | N | N | Q | Y | CAPABILITY | N |
| Harvest | `POST /api/actors/harvest` | `harvest` | `farm harvest` uses actor directly, NOT this route | Harvest | `Harvest(doodadObjId, key)` | `Doodad.Use` w/ data-driven harvest skill (phase-func derived, no hardcoded crop ids) | N | N | Q | Y | CAPABILITY | N |
| Craft (one step) | `POST /api/actors/craft` | `craft` | — | Craft | `Craft(craftId, doodadObjId, timeout, key)` | `CharacterCraft.Craft` count=1 (CSExecuteCraft path); Running while engine queue active | N | N | Q | Y | CAPABILITY | N |
| Buy | `POST /api/actors/buy` | `buy` | — | Buy | `Buy(merchantObjId, templateId, count, key)` | CSBuyItemsPacket branch (`GetGoods` + `AcquireDefaultItem` + `ChangeMoney`) | N | N | Q | Y | CAPABILITY | N |
| Sell | `POST /api/actors/sell` | `sell` | — | Sell | `Sell(merchantObjId, itemId, key)` | CSSellItemsPacket branch (BuyBack container + `ChangeMoney`) | N | N | Q | Y | CAPABILITY | N |
| Repair | `POST /api/actors/repair` | `repair` | `mail repair` uses actor directly, NOT this route | Repair | `Repair(npcObjId, itemId, key)` | `Character.DoRepair` (CSRepairAllEquipmentsPacket path) | N | N | Q | Y | CAPABILITY | N — but grouped under bridge `mail` cmd (misleading grouping, see §B) |
| SellSpecialty | `POST /api/actors/sell_specialty` | `sell_specialty` | — | SellSpecialty | `SellSpecialty(merchantObjId, key)` | `SpecialtyManager.SellSpecialty` (CSSellBackpackGoodsPacket path) | N | N | Q | Y | CAPABILITY | N |
| TradeOffer | `POST /api/actors/trade_offer` | `trade_offer` | — | TradeOffer | `TradeOffer(charObjId, key)` | `TradeManager.CanStartTrade` + `StartTrade` (CSCanStart/StartTradePacket) | N | N | Q | Y | CAPABILITY | N |
| TradePutup | `POST /api/actors/trade_putup` | `trade_putup` | — | TradePutup | `TradePutup(templateId, count, key)` | `TradeManager.AddItem` (CSPutupTradeItemPacket path) | N | N | Q | Y | CAPABILITY | N |
| TradeLockOk | `POST /api/actors/trade_lock_ok` | `trade_lock_ok` | — | TradeLockOk | `TradeLockOk(key)` | `TradeManager.LockTrade` + `ConfirmTrade` (CSTradeLock/Ok paths) | N | N | Q | Y | CAPABILITY | N |
| PackPickup | `POST /api/actors/pack_pickup` | `pack_pickup` | — | PackPickup | `PackPickup(doodadObjId, key)` | `RecoverItem.Execute` w/ recover skill 11361 (CSLootOpenBagPacket pack path) | N | N | Q | Y | CAPABILITY | N |
| PutDown | `POST /api/actors/put_down` | `put_down` | — | PutDown | `PutDown(packTemplateId, key)` | Pack use-skill via SkillItem branch (`PutDownBackpackEffect`); async plot, Tick polls Backpack→System transition | N | N | Q | Y | CAPABILITY | N |
| LoadPackOntoVehicle | `POST /api/actors/load_pack_onto_vehicle` | `load_pack_onto_vehicle` | — | LoadPackOntoVehicle | `LoadPackOntoVehicle(slaveObjId, placedPackObjId, key)` | `PackVehicleService` → SlaveManager attach seam (snap-to-cargo-point) | N | N | Q | Y | CAPABILITY | N |
| BoardVehicle | `POST /api/actors/board_vehicle` | `board_vehicle` | — | BoardVehicle | `BoardVehicle(vehicleObjId, attachPoint, key)` | Slave `BindSlave` / transfer seat bond / glider Backpack equip (NOT the mate path) | N | N | Q | Y | CAPABILITY | N |
| UnboardVehicle | `POST /api/actors/unboard_vehicle` | `unboard_vehicle` | — | UnboardVehicle | `UnboardVehicle(vehicleObjId, key)` | `SlaveManager.UnbindSlave` / seat unbond / `TakeoffBackpack` | N | N | Q | Y | CAPABILITY | N |
| DriveVehicle | `POST /api/actors/drive_vehicle` | `drive_vehicle` | — | DriveVehicle | `DriveVehicle(vehicleObjId, dest, speed, timeout, key)` | `VehicleMovementModel` (CSMoveUnitPacket path; Transform never assigned) | N | N | Q; arrival radius 0.5f like Move | Y | CAPABILITY | N |
| Interrupt (control op, not gameplay) | `POST /api/actors/interrupt` | `interrupt` | — | Interrupt | `Interrupt(traceId)` → bool | Cancels running request by trace id; idempotent | N | N | Q (returns bool result, audited as control entry) | Y | CAPABILITY | PARTIAL — `ExecuteKind→ToActionType` maps Interrupt to `ActorActionType.Stop`, so the audit taxonomy shows Stop for a control op |
| Lifecycle reads | `GET /api/actors/actions/{traceId}`, `GET /api/actors/trace?bot=&limit=` | `action_status`, `trace` | `quest observe` reads executor live-actor trace (scripted observation) | — (reads, not kinds) | `AuditTrace` / queue snapshot store | Queue entry snapshots (API threads never touch the actor) | N | N | R | Y (they expose it) | CAPABILITY | N |

### A2. Actor methods with NO queue kind and NO WebAPI/MCP (actor-internal / bridge-scripted only)

All reachable today only via direct `new GameplayActor(character)` calls
(bridge/scenario code, synchronous, ephemeral trace → Audit=`E`, Terminal=`S`)
or via GOAP/scheduler internals. EvidenceClass=SCRIPTED unless production logic
chose the call (then AUTONOMY — none of these rows is autonomy by itself).

| Capability | WebAPI | MCP | Bridge op | QueueKind | GameplayActor method | Engine subsystem | DirectMutation | FixtureOnly | Terminal contract | Audit | EvidenceClass | MisleadingName |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CastAt (position-target skill, e.g. fishing 21571) | — | — | — | null | `CastAt(skillId, position, key)` | `Skill.Use` w/ `SkillCastPositionTarget` (CSStartSkillPacket Pos branch) | N | N | S | E | SCRIPTED (unreachable externally today) | N |
| AutoAttack start | — | — | — | null | `AutoAttack(targetObjId, key)` | `UseAutoAttackSkillTask` (Skill 2 melee / 4 ranged) | N | N | S | E | SCRIPTED | N |
| AutoAttack stop | — | — | — | null | `StopAutoAttack(key)` | Attack-task cancel | N | N | S | E | SCRIPTED | N |
| DismissMate | — | — | — | null | `DismissMate(tlId, key)` | `Character.Mates.DespawnMate` (CSRemoveMatePacket path) | N | N | S | E | SCRIPTED | N |
| InteractNpc (ambient dialogue) | — | — | — | null | `InteractNpc(npcObjId, key)` | CSStartInteraction/CSInteractNPC dialogue + opportunistic talk credit | N | N | S | E | SCRIPTED | Y-ADJACENT — easily confused with `Interact` (doodad) and `Talk` (quest credit); see §E |
| PlayCinema | — | — | — | null | `PlayCinema(cinemaId, key)` | `Character.Events.OnCinemaStarted/Ended` (0x0cf/0x0ce paths) | N | N | S | E | SCRIPTED | N |
| PartyInvite | — | — | scenario driver (`HandlePartyFollowAssistScenario`, `HandlePartySpikeScenario` call `new GameplayActor().PartyInvite/PartyAccept` directly) | null | `PartyInvite(charObjId, key)` / `PartyAccept(key)` | `TeamManager.AskToJoin` / `ReplyToJoinTeam` | N | Y (scenario setup) | S | E | SCRIPTED | N |
| ExpeditionCreate/Invite/Accept/Leave | — | — | `HandleExpeditionFormationScenario` (driver) | null | `ExpeditionCreate/Invite/Accept/Leave` | `ExpeditionManager.CreateExpedition/Invite/ReplyInvite/Leave` | N | Y (scenario setup) | S | E | SCRIPTED | N |
| PostAuction / BuyAuction | — | — | `auction post`, `auction buy` (actor-direct, scripted) | null | `PostAuction(itemId, start, buyout, dur, key)` / `BuyAuction(lotId, price, key)` | `AuctionManager.PostLotOnAuction` / `BidOnAuctionLot` | N | Y (E2E rig) | S | E | SCRIPTED (engine-true, not autonomy) | N |
| BuildHouse | — | — | `housing build` (actor-direct + fabricated null-session connection, scripted) | null | `BuildHouse(designId, designItemTemplate, pos, zRot, key)` | `HousingManager.Build` (CSCreateHousePacket path) | N (connection fabrication is test-only scaffolding, not state mutation) | Y (E2E rig) | S | E | SCRIPTED (engine-true, not autonomy) | N |
| Navigate/NavigateToUnit dispatch | — | — | — | Navigate / NavigateToUnit (queue-only, no WebAPI) | `NavigateTo` / `NavigateToUnit` | Navmesh A* (see A1) | N | N | Q | Y | CAPABILITY | Y — `BotActionKind.NavigateToUnit` exists and dispatches, but has NO WebAPI/MCP route; silent gap, see §C |

Note: `MCP-ACTION-MATRIX.md` (repo doc) claims TradeOffer/TradePutup/TradeLockOk
are "Deferred: no actor WebApi route". That is STALE: the controller now exposes
`POST /api/actors/trade_offer|trade_putup|trade_lock_ok`, the queue dispatches
all three, and the sidecar registers all three tools. The doc is wrong; the code
above is authoritative.

### A3. Bridge `drive` ops (bypass surface — `new PlayerBotController(character)`, NOT the actor)

Header comment claims the bridge "NEVER bypasses the quest engine" — that is true
only of the quest *engine* (`AddQuest`/`DoReportEvents` gates still evaluate).
It bypasses the *actor* (`GameplayActor`: no lifecycle, no audit, no idempotency
ledger, no execution-boundary marshal). All rows: WebAPI=—, MCP=—, QueueKind=null,
GameplayActor=BYPASS, FixtureOnly=Y (E2E-only loopback TCP, `EnableE2EBridge`),
Audit=N, EvidenceClass=SCRIPTED (harness chose) or ENGINE (direct engine call).

| Capability | Bridge op | GameplayActor method | Engine subsystem | DirectMutation | Terminal contract | Audit | EvidenceClass | MisleadingName |
|---|---|---|---|---|---|---|---|---|
| Quest accept (bypass) | `drive accept` | BYPASS → `PlayerBotController.AcceptQuest` → `CharacterQuests.AddQuest` | Quest engine (gates evaluate) | N | F (`{accepted:bool}`) | N | SCRIPTED | Y — the Q0 headline: same engine tail as the pilot but NOT actor evidence (no lifecycle/audit/idempotency) |
| Quest advance (bypass) | `drive advance` | BYPASS → `controller.Advance` → step machine | Quest step machine | N | F | N | SCRIPTED | Y (mirrors `advance_quest` name) |
| Kill credit injection | `drive kill` / `killGroup` / `gather` / `useItem` / `talk` / `interact` / `enterSphere` / `express` / `levelUp` / `aggro` / `zoneKill` / `cinemaStarted` / `cinemaEnded` | BYPASS → `controller.KillNpc/...` → `Character.Events.OnMonsterHunt/...` event-fire | UnitEvents surface (handlers run, but no world action occurred — no kill, no gather, no talk) | N | F (`{fired:true}`) | N | ENGINE (event injection, not gameplay) | Y — `talk`/`interact`/`useItem`/`cast`-shaped names imply the actor verbs `Talk`/`Interact`/`UseItem`/`Cast`; they are synthetic event fires. `drive talk` ≠ `Talk` (no `DoTalkMadeEvents`, no range gate, no delta post-check) |
| Turn-in (bypass) | `drive report` / `reportDoodad` / `autoTurnIn` | BYPASS → `controller.ReportTurnIn/ReportDoodadTurnIn/AutoTurnIn` → `QuestManager.DoReportEvents` | Quest turn-in engine | N | F (`{reported:true}`) | N | SCRIPTED | Y (mirrors `turn_in_quest`/`turn_in_doodad`/`auto_turn_in`) |
| Cast (actor-traversing, scripted) | `drive cast` | `new GameplayActor(character).Cast(...)` — traverses actor synchronously | `Character.UseSkill` | N | S | E (trace discarded) | SCRIPTED (engine-true, not autonomy; same-request ledger empty so same-key dedupe does not apply across calls) | Y — named identically to the actor action but skips queue/WebAPI; must never be cited as external-control evidence |
| Loot (actor-traversing, scripted) | `drive loot` | `new GameplayActor(character).Loot(...)` — traverses actor synchronously | `LootingContainer.OpenBag` | N | S | E (trace discarded) | SCRIPTED (engine-true, caller-delta diff is real) | Y — same reason as `drive cast` |
| Read-only diagnostics | `drive questState/invCount/isActive/hasCompleted/charState/charPos/npcObjId/doodadObjId` | BYPASS (reads / world lookups) | State reads | N | R | N | SCRIPTED (fixture observation; Q0 uses these correctly as fixtures) | N |
| Staging / direct mutation | `drive setLevel` (`character.Level =`), `drive stock` (`StockInventory`+`AcquireDefaultItem`), `drive teleportToNpc` / `farm place` / `quest place` / `homestead move` (`TeleportWithRegionSync` + `MarkDirty`) | BYPASS | Field assignment / inventory acquisition / transform relocate | Y | F/R | N | SCRIPTED (setup-only, disclosed in code comments) | PARTIAL — `teleportToNpc`/`place`/`move` move the body without traversal; code comments disclose setup-only, but the names read as capabilities |
| Farm rig | `farm rig` (`StockInventory` + `character.LaborPower =`) | BYPASS | Inventory + labor field | Y | F | N | SCRIPTED | N (clearly a rig) |
| Farm plant/interact/harvest (actor-traversing, scripted) | `farm plant` → `actor.Plant`; `farm interact` → `actor.Interact(objId, skill)`; `farm harvest` → `actor.Harvest` | Traverses actor synchronously | Doodad engine (see A1) | N | S (Err-mapped on non-Completed) | E | SCRIPTED (engine-true, not autonomy) | Y — same names as WebAPI verbs, different seam (no queue/audit/ledger) |
| Farm `use` | — (NO `farm use` op exists in code; task-listed `use` lives at `mail use` → `actor.UseItem`, actor-traversing scripted) | UNKNOWN for farm scope | — | — | — | — | UNKNOWN | N/A — flagging so no one cites a nonexistent op |
| Farm/quest autonomy drivers | `farm needs` (wake), `quest wake`/`observe` | Executor's LIVE actor (not a fresh instance) — audit read from `BotRoamStepExecutor` state | Scheduler + GOAP legs (buy/travel/plant/wait/harvest/replant decided by production code) | N | R (observation of scheduler steps) | Y (live-actor trace; the only bridge path that surfaces durable audit) | AUTONOMY (scheduler chose the legs; the wake itself is scripted) | Y — `needs`/`wake` sound like actions; they enroll + wake + observe, issuing no gameplay action themselves |
| Mail rig / tuning | `mail rig` (`character.Money =`, `AcquireDefaultItem`, `EquipItem.Durability/RuneId/Temper*=`), `mail delay` (sets `MailManager.NormalMailDelay` server-wide!) | BYPASS | Fields / global tuning | Y | F | N | SCRIPTED | Y — `mail delay` is a server-global mutation behind a per-bot-looking op |
| Mail repair/use (actor-traversing, scripted) | `mail repair` → `actor.Repair`; `mail use` → `actor.UseItem` (comment explicitly contrasts with `drive useItem` event-fire) | Traverses actor synchronously | `Character.DoRepair` / SkillItem branch | N | S | E | SCRIPTED (engine-true) | Y — domain grouping: repair/use are not mail operations |
| Mail wound | `mail wound` (`character.ReduceCurrentHp(...)`) | BYPASS (honest engine HP path per comment, but direct call) | `Unit.ReduceCurrentHp` w/ packets + death logic | Y (HP directly reduced; no damage source) | F | N | ENGINE | N (comment is explicit) |
| Auction rig/reads | `auction rig` (`Money =` + StockInventory), `lots/search/mails/char` (reads) | BYPASS | Fields / manager stores | Y (rig) / N (reads) | F/R | N | SCRIPTED | N |
| Housing construct | `housing construct` (`house.AddBuildAction()` loop to CurrentStep -1) | BYPASS — direct `House` mutation, NOT `GameplayActor` (no actor method exists for construct) | `House` build-step state | Y | F | N | ENGINE | Y — sits beside actor-traversing `housing build`; implies engine-path construction but skips `HousingManager`/craft pipeline (`CraftEffect` Building group is only mimicked, not driven) |
| Housing decorate | `housing decorate` (`HousingManager.DecorateHouse` direct) | BYPASS (engine call, no actor) | HousingManager (+ `DecoLimitEvaluator` gate) | N | F | N | ENGINE | N (comment names the packet path) |
| Homestead claim/construct | `homestead claim/claimRetry`, `construct/constructRetry` via `SurveyAndPlacePlotAction` / `ConstructPlotAction.CreateActorRequest(runtime, actor)` | Traverses actor synchronously via GOAP action objects (not via queue) | `HousingManager.Build` / build-skill cast | N | S | E | SCRIPTED (GOAP-shaped but harness-invoked; autonomy only if the scheduler chose it) | PARTIAL — GOAP action shape looks like autonomy; the choice was the test's |
| Dominion declare | `dominion declare` (`DominionManager.Declare` direct) | BYPASS | DominionManager persist + broadcast | Y (manager store + DB, no gameplay preconditions) | F | N | ENGINE | N (doc comment discloses direct-manager route) |
| Provisioning / lifecycle | `provision` (`HeadlessSession.Provision`), `deactivate`, `seedDormant`, `EnsureFreshBotRow` (raw `DELETE FROM playerbot_audit/metadata/doodads/items/item_containers/quests/completed_quests/characters/users`) | BYPASS | Session provisioning + raw MySQL deletes | Y | F | N | SCRIPTED | N |
| Save trigger | `save` (marks all houses dirty + `SaveManager.DoSave`) | BYPASS | Persistence pass | Y (dirty flags forced) | F | N | SCRIPTED | N (comment is explicit) |

### A4. `PlayerBotController` methods (engine-direct helpers — NEVER actor evidence)

`AAEmu.Game/Models/Game/Bots/PlayerBotController.cs` (additive quest-drive-only
helper; holds no quest state). Every method below calls the quest engine or
inventory directly with no request, no lifecycle, no audit, no idempotency, no
execution-boundary assertion. WebAPI=—, MCP=—, QueueKind=null, Audit=N,
EvidenceClass=ENGINE or SCRIPTED depending on caller (bridge → SCRIPTED).

Quest gates (real, but bypass-shaped): `AcceptQuest` → `CharacterQuests.AddQuest`;
`AcceptFromNpc` → `AddQuestFromNpc`; `AcceptFromDoodad` → `AddQuestFromDoodad`;
`Advance` → step machine; `ReportTurnIn/ReportDoodadTurnIn/AutoTurnIn` →
`QuestManager.DoReportEvents`. Reads: `IsActive/HasCompleted/ActiveQuest/
InventoryCount`. Event injection (no world action occurs):
`KillNpc/KillNpcGroup/GatherItem/UseItem/TalkToNpc/InteractWithDoodad/
EnterSphere/ExpressEmotion/LevelUp/AggroNpc/ZoneKill/CinemaStarted/CinemaEnded`
→ `Character.Events.On*`. Setup: `StockInventory` → `Bag.AcquireDefaultItem`.

## (B) Bypassing / misleading paths (do-not-cite list)

1. `drive accept` → `PlayerBotController` (BYPASS). Same engine tail as Q0
   (`CharacterQuests.AddQuest`) but no `GameplayActor`, no queue, no audit, no
   idempotency. Must never be cited as actor-capability evidence. (Q0 pilot header
   states this explicitly.)
2. `drive talk/interact/useItem/kill/gather/...` → synthetic `Character.Events.On*`
   fires. No NPC was talked to, no doodad touched, no item used, nothing died.
   `drive talk` in particular must not be confused with `GameplayActor.Talk`
   (`DoTalkMadeEvents` + range gate + delta post-check).
3. `drive cast` / `drive loot` / `farm plant|interact|harvest` / `mail repair|use` /
   `auction post|buy` / `housing build` traverse a synchronous ephemeral
   `new GameplayActor(character)`: engine-true mutations, but no queue lifecycle,
   durable audit discarded, per-instance idempotency ledger always empty (same-key
   retries across calls are NOT deduped). SCRIPTED, not CAPABILITY, not AUTONOMY.
4. `housing construct` (`house.AddBuildAction()` loop) mutates `House` directly,
   skipping the craft/build pipeline — the most misleading op on the bridge
   because it neighbors the engine-true `housing build`.
5. Bridge docstring "NEVER bypasses the quest engine" (BotDriveBridge.cs:41-44)
   is true of quest-engine gates only; it bypasses the actor boundary for ~30 ops.
   Read it narrowly.
6. Stale docs: `AAEmu.BotControlMcp/MCP-ACTION-MATRIX.md` marks Trade* deferred
   (wrong — all three are live in controller + queue + sidecar); its "39-tool"
   claim vs `BotControlActionMcpE2eTests` asserting 24 tools vs 44 tools actually
   registered in `ActionMcpServer.cs` — counts disagree across all three sources.
   Code wins: 44 tools (42 action + `action_status` + `trace`).
7. `BotActionKind.Interrupt` → `ActorActionType.Stop` in `ToActionType`
   (BotActionCommandQueue.cs:915): control op recorded under a gameplay-action
   label in mapped views.
8. No `farm use` op exists; `mail delay` mutates a server-global; `mail repair/use`
   are non-mail verbs under the `mail` cmd; teleport/`place`/`move` ops relocate
   without traversal (comments disclose setup-only — keep the disclosure when citing).

## (C) Canonical external actor-control seam (recommendation)

Canonical seam (already exists, no new API needed for the covered surface):

```mermaid
flowchart LR
    EXT[External caller: WebAPI client or MCP sidecar] --> POST[POST /api/actors/* — BotActionController: validate + enqueue-only, never touches Character/world]
    POST --> Q[BotActionCommandQueue: trace id, lifecycle Requested→Accepted→Running→Completed/Rejected/Interrupted/TimedOut, idempotency ledger, A1 execution-boundary drain]
    Q --> A[IGameplayActor / GameplayActor: validated request + audit record]
    A --> ENG[Engine packet-path call: AddQuest, DoReportEvents, Doodad.Use, Skill.Use, ChangeMoney, ...]
    Q --> POLL[GET /api/actors/actions/{traceId} + GET /api/actors/trace — async response channel + audit trail]
```

Rules: external callers use only `POST /api/actors/*` (or the 1:1 MCP tools in
`AAEmu.BotControlMcp/ActionMcpServer.cs`, themselves enqueue-only HTTP clients —
verified: sidecar holds no engine reference, only `IBotControlClient`). The bridge
is fixture/observation-only: staging (`setLevel`, teleports, rigs), reads, and
scheduler wakes. Scheduler/GOAP autonomy evidence must come from the executor's
live-actor audit (`farm needs` / `quest observe` pattern), never from ephemeral
`new GameplayActor()` traces and never from `PlayerBotController` event-fire.

Real missing seams (genuinely unreachable externally — add ONLY if needed, each is
`BotActionKind` + `ExecuteKind` case + `/api/actors/*` endpoint + MCP tool, no
other plumbing):

1. `Navigate` / `NavigateToUnit` kinds dispatch in the queue but have no route —
   smallest gap; add `POST /api/actors/navigate|navigate_to_unit` if callers need
   navmesh routing (else `move`/`move_to_unit` suffice).
2. Actor-only verbs with no kind at all: `CastAt`, `AutoAttack`/`StopAutoAttack`,
   `DismissMate`, `InteractNpc`, `PlayCinema`, `PartyInvite`/`PartyAccept`,
   `ExpeditionCreate/Invite/Accept/Leave`, `PostAuction`/`BuyAuction`, `BuildHouse`.
   Until a route exists, these are actor-internal/bridge-scripted only and must not
   be presented as externally controllable capabilities.

No other new API is recommended. In particular: do NOT promote any bridge op to a
production seam, and do NOT wrap `PlayerBotController` in new endpoints — route new
needs through `GameplayActor` + queue.

## (D) Fixture-API classification

Fixture-SAFE (read-only or disclosed setup staging; may appear in test setup and
Q0-style pre-loop fixtures): `drive questState/invCount/isActive/hasCompleted/
charState/charPos/npcObjId/doodadObjId`, `farm find/status/soil`, `auction
lots/search/mails/char`, `mail char/mails/inv/mailbox`, `housing status`,
`homestead zone/positions/observe`, `quest observe`, position staging
(`teleportToNpc`, `farm place`, `quest place`, `homestead move` — disclosed
setup-only, region-synced, never traversal proof), `provision`/`deactivate`/
`seedDormant` (session lifecycle), `save` (deterministic save trigger).

Fixture-MUTATING (writes gameplay/persistence state; label as setup, never as
traversal/autonomy proof): `drive setLevel` (`Level=`), `drive stock`, all `rig`
ops (`Money=`/`LaborPower=`/`AcquireDefaultItem`/equip-field sets), `mail stock`,
`mail wound` (`ReduceCurrentHp`), `mail delay` (server-global!), `dominion
declare`, `housing construct` (`AddBuildAction`), `EnsureFreshBotRow` raw DB
deletes. Engine-true-but-SCRIPTED (mutation via real path, harness-chosen):
§A2 bridge actor-direct calls + scenario `PartyInvite/Accept/SetTarget` calls.

## (E) NPC-interaction verdict + movement-gate handoff

NPC interaction — three distinct verbs, do not conflate:

- Quest credit (CORRECT production path for talk objectives): `GameplayActor.Talk`
  (`IGameplayActor.cs:741`, `GameplayActor.cs:941`) → resolves the LIVE NPC in the
  owning world (`ParentWorld.GetNpc`), pre-flights unresolvable/out-of-range as
  `Rejected(RejectedAction)`, fires `QuestManager.DoTalkMadeEvents` once per ACTIVE
  quest carrying a `QuestActObjTalk`/`QuestActObjTalkNpcGroup` objective for that
  NPC (the per-quest-dialog packet fan-out, incl. NpcGroup membership), advances
  the step machine, and post-checks an observable delta (counters/step/status) —
  talking to an NPC that credits nothing is `Rejected`, never silent success.
- Ambient/conversational: `GameplayActor.InteractNpc` (`:747`, impl `:1044`) →
  CSStartInteraction/CSInteractNPC dialogue path (target broadcast, skill list),
  opportunistically credits talk objectives the same way. Actor-only (no queue
  kind, no WebAPI/MCP) — needs a new seam (gap C.2) before external use.
- NEVER for NPCs: `Interact` (`Doodad.Use` + explicit skill, `:1401`) and
  `InteractWith` (derived use-skill → InteractionEffect/Skill chain or
  `Doodad.Use`, `:1459`) resolve a live DOODAD and drive the doodad phase machine.
  No NPC branch exists in either implementation. `doodad.Use` is not an
  NPC-interaction path.

Movement gate handoff — exact production method + convergence semantics
(`GameplayActor.cs:75,292-310,338-401,4116-4180`):

- Method: `GameplayActor.MoveTo(Vector3 destination, float speed = 5f,
  timeout = null, key = null)` via `POST /api/actors/move` → `BotActionKind.Move`
  (absolute) or `MoveToUnit` for unit-follow; routed variants `NavigateTo` /
  `NavigateToUnit` share the machinery but are queue-only (no WebAPI/MCP).
- Convergence: arrival ⇔ flat (XZ) distance ≤ `ArrivalRadius` (0.5f, const at
  `:75`) AND |ΔZ| ≤ 0.5f. Per-Tick stepping toward the target through the ordinary
  Transform; routed legs additionally dequeue navmesh waypoints, with corner
  blending (intermediate waypoints inside the blend radius are cut, not stopped
  at), trapezoid speed profile, unstick recovery waypoints, and `BroadcastStop()`
  on final arrival. Already-within-radius dispatches a full-lifecycle no-op
  (`Completed`, "already at destination"). Budget default 30s (`DefaultMoveTimeout`);
  expiry → `TimedOut(Navigation)`; stuck detection (no displacement beyond
  `ArrivalRadius` within the window) fails the leg fast. Follow-up gate should
  assert on the `Completed("arrived")` audit record + post-state position within
  the 0.5f flat/vertical box, and must NOT use bridge teleports as movement evidence.
