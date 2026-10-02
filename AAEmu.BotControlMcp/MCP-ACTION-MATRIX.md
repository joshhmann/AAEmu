# Gameplay actor to MCP coverage matrix

Audit sources: `AAEmu.Game/Core/Managers/Bots/IGameplayActor.cs` (actor contract), `AAEmu.Game/Services/WebApi/Controllers/BotActionController.cs` (authenticated actor routes), and `AAEmu.UnitTests/BotControl/BotControlActionMcpTests.cs` (sidecar contract tests). A route is considered exposed only when `BotActionController` has an authenticated `/api/actors/*` endpoint that enqueues the matching actor action. Management routes are intentionally excluded from this sidecar.

| `IGameplayActor` action | Authenticated WebApi endpoint | MCP tool | Coverage |
|---|---|---|---|
| `Observe` | `POST /api/actors/observe` | `observe` | `Call_observe_PostsObserveEndpoint`; generic exact mapping |
| `MoveTo` | `POST /api/actors/move` | `move` | `Call_move_PostsCoordinatesWithOptionalArgs`; generic exact mapping |
| `NavigateTo` | — | — | Deferred for MCP: implementation is landed, but no authenticated actor WebApi route |
| `MoveToUnit` | `POST /api/actors/move_to_unit` | `move_to_unit` | `Call_move_to_unit_PostsTargetAndSpeed`; generic exact mapping |
| `Stop` | `POST /api/actors/stop` | `stop` | `Call_stop_PostsBotOnly`; generic exact mapping |
| `SetTarget` | `POST /api/actors/target` | `target` | `Call_target_PostsTarget`; generic exact mapping |
| `Cast` | `POST /api/actors/cast` | `cast` | `Call_cast_PostsSkillAndTarget`; generic exact mapping |
| `CastAt` | — | — | Deferred: no actor WebApi route |
| `Interact` | `POST /api/actors/interact` | `interact` | `Call_interact_PostsDoodad`; generic exact mapping |
| `InteractWith` | `POST /api/actors/interact_with` | `interact_with` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `Loot` | `POST /api/actors/loot` | `loot` | `Call_loot_PostsLootOwner`; generic exact mapping |
| `UseItem` | `POST /api/actors/use_item` | `use_item` | `Call_use_item_PostsTemplateAndOptionalTarget`; generic exact mapping |
| `Equip` | `POST /api/actors/equip` | `equip` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `PartyInvite` | — | — | Deferred: no actor WebApi route |
| `PartyAccept` | — | — | Deferred: no actor WebApi route |
| `ExpeditionCreate` | — | — | Deferred: no actor WebApi route |
| `ExpeditionInvite` | — | — | Deferred: no actor WebApi route |
| `ExpeditionAccept` | — | — | Deferred: no actor WebApi route |
| `ExpeditionLeave` | — | — | Deferred: no actor WebApi route |
| `TradeOffer` | `POST /api/actors/trade_offer` | `trade_offer` | Route added 2026-09-13 with the 44-tool catalog; previously mis-recorded as deferred |
| `TradePutup` | `POST /api/actors/trade_putup` | `trade_putup` | Route added 2026-09-13 with the 44-tool catalog; previously mis-recorded as deferred |
| `TradeLockOk` | `POST /api/actors/trade_lock_ok` | `trade_lock_ok` | Route added 2026-09-13 with the 44-tool catalog; previously mis-recorded as deferred |
| `Mount` | `POST /api/actors/mount` | `mount` | `Call_mount_PostsMate`; generic exact mapping |
| `Dismount` | `POST /api/actors/dismount` | `dismount` | `Call_dismount_PostsOptionalMate`; generic exact mapping |
| `BoardVehicle` | `POST /api/actors/board_vehicle` | `board_vehicle` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `UnboardVehicle` | `POST /api/actors/unboard_vehicle` | `unboard_vehicle` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `Harvest` | `POST /api/actors/harvest` | `harvest` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `Craft` | `POST /api/actors/craft` | `craft` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `DriveVehicle` | `POST /api/actors/drive_vehicle` | `drive_vehicle` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `PackPickup` | `POST /api/actors/pack_pickup` | `pack_pickup` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `PutDown` | `POST /api/actors/put_down` | `put_down` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `LoadPackOntoVehicle` | `POST /api/actors/load_pack_onto_vehicle` | `load_pack_onto_vehicle` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `Plant` | `POST /api/actors/plant` | `plant` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `BuildHouse` | — | — | Deferred: no actor WebApi route |
| `DepositMoney` | `POST /api/actors/deposit_money` | `deposit_money` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `WithdrawMoney` | `POST /api/actors/withdraw_money` | `withdraw_money` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `DepositItem` | `POST /api/actors/deposit_item` | `deposit_item` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `WithdrawItem` | `POST /api/actors/withdraw_item` | `withdraw_item` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `AcceptQuest` | `POST /api/actors/accept_quest` | `accept_quest` | `Call_accept_quest_PostsQuestAndAcceptor`; generic exact mapping |
| `AdvanceQuest` | `POST /api/actors/advance_quest` | `advance_quest` | `Call_advance_quest_PostsQuest`; generic exact mapping |
| `TurnInQuest` | `POST /api/actors/turn_in_quest` | `turn_in_quest` | `Call_turn_in_quest_PostsNpcAndReward`; generic exact mapping |
| `DiscoverQuests` | `POST /api/actors/discover_quests` | `discover_quests` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `TurnInAtDoodad` | `POST /api/actors/turn_in_doodad` | `turn_in_doodad` | `Call_turn_in_doodad_PostsDoodad`; generic exact mapping |
| `AutoTurnInQuest` | `POST /api/actors/auto_turn_in` | `auto_turn_in` | `Call_auto_turn_in_PostsQuest`; generic exact mapping |
| `Talk` | `POST /api/actors/talk` | `talk` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `DiscoverSelfQuests` | `POST /api/actors/discover_self_quests` | `discover_self_quests` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `Buy` | `POST /api/actors/buy` | `buy` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `Sell` | `POST /api/actors/sell` | `sell` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `Repair` | `POST /api/actors/repair` | `repair` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `SellSpecialty` | `POST /api/actors/sell_specialty` | `sell_specialty` | `CallTool_MapsEveryRegisteredToolToExactWireRequest`; queue dispatch regression |
| `PostAuction` | — | — | Deferred: no actor WebApi route |
| `BuyAuction` | — | — | Deferred: no actor WebApi route |
| `Interrupt(Guid)` | `POST /api/actors/interrupt` | `interrupt` | `Call_interrupt_PostsTraceId`; generic exact mapping |

Lifecycle/audit reads are not `IGameplayActor` actions, but are part of the authenticated actor API and remain exposed:

| API read | MCP tool | Coverage |
|---|---|---|
| `GET /api/actors/actions/{traceId}` | `action_status` | `Call_action_status_GetsTraceEndpoint`; generic exact mapping and escaped path argument |
| `GET /api/actors/trace?bot=...&limit=...` | `trace` | `Call_trace_GetsTraceQueryWithLimit`, `Call_trace_WithoutLimit_GetsTraceQuery`; generic exact mapping and escaped bot argument |

`Tick`, `FindByKey`, `ActorId`, `Character`, `ActiveRequest`, and `AuditTrace` are actor internals/engine scheduling or correlation surfaces, not standalone HTTP actions.

**Correction 2026-10-01 (read-only source re-verification):** this paragraph previously said the routes above are "the complete safe **39-tool** MCP surface" and that "Trade" remained deferred. Both halves are STALE:

- **The catalog is 44 tools, not 39.** `ActionMcpServer.cs` `ToolsList()` registers exactly 44 `Tool(...)` entries, and `Scripts/mcp-stdio-smoke.sh:27` asserts `len(tools) == 44` — an assertion that would fail the gate at 39. The committed fix is `d0e2d72d6` (2026-09-13, "fix(tests): expect 44 tools in MCP stdio smoke"). Each tool maps 1:1 onto one authenticated route in `AAEmu.Game/Services/WebApi/Controllers/BotActionController.cs`: 42 `WebApiPost("^/api/actors/<name>$")` routes plus the two `WebApiGet` lifecycle reads (`actions/{traceId}` → `action_status`, `trace` → `trace`) — 44 routes serving 44 tools, with no orphan on either side.
- **Trade is no longer deferred.** `AAEmu.Game/Services/WebApi/Controllers/BotActionController.cs:713,742,772` implement `POST /api/actors/trade_offer`, `/trade_putup` and `/trade_lock_ok`; `ActionMcpServer.cs` exposes them as `trade_offer`, `trade_putup`, `trade_lock_ok`. The rows above are corrected below.
- **This matrix was also missing five rows** that the controller already had: `Repair`, `SellSpecialty`, and the three Trade actions. They are now listed above.

What remains deferred is unchanged and still accurate: `NavigateTo`, `CastAt`, `PartyInvite`/`PartyAccept`, the `Expedition*` family, `BuildHouse`, and `PostAuction`/`BuyAuction` genuinely have no authenticated actor route. No fake route or management alias is added.

Other WebApi controllers do not widen this contract: for example,
`ExpeditionController` exposes `GET /api/expedition/list` as a server-wide
read and has no actor/bot target or authenticated action enqueue semantics.
It is therefore not an MCP actor tool.
