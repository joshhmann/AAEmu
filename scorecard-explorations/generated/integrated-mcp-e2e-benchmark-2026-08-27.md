# Integrated MCP + direct-E2E benchmark — 2026-08-27

## Verdict

**MCP live path: PASS.** A real local Login/Game stack reached WebApi with a temporary out-of-band Bot Control token. Both generic stdio sidecars initialized, listed their tools, and sent authenticated HTTP calls. Management `bot_add` provisioned `McpIntegrated01`; a second `bot_add` exercised idempotent adopt behavior. The action sidecar completed `observe` and a bounded two-metre `move`; both traces were returned by `trace`.

**Independent direct-E2E cross-check: PASS (DB-row evidence).** The existing server-side persistence path was queried for the same normalized character name after the MCP run:

```text
1	Mcpintegrated01	1	0	179	15582.631	15385.232	126.446
```

This is an ordinary `aaemu_game.characters` row (`id=1`, `name=Mcpintegrated01`, managed account `account_id=1`, `world_id=0`, `zone_id=179`). It independently confirms that management provisioning created durable character state for the actor named by MCP. The row position is a persistence sample, not an assertion that the roaming actor's in-memory transform remains there after the move.

**Authenticated wire cross-check: BLOCKED by lifecycle boundary.** The existing `BotDriveBridge` `drive/charPos` diagnostic was attempted for the same bot and returned `bot 'McpIntegrated01' is not in the world (no active networked session)`. This is expected for `BotAdminService.Add`: managed bot accounts are client-login-blocked and are embodied headlessly, not through `BotNetworkSession`. Therefore this run does not claim a client-authenticated packet/state transition. The DB row is the selected independent fact; a future wire leg needs a separately authenticated ordinary player session and must not pretend it is the managed bot session.

No Navigation timeout occurred. The short move completed with `detail=arrived`; no failure was masked.

## Starting conditions and isolation

- Worktree: `/root/aaemu-dev/.worktrees/integrated-mcp-e2e-benchmark`
- Base: `origin/develop` at `12ff5b504` (the worktree was created from the current origin/develop ref).
- MySQL: isolated Compose project `aaemu-mcp-integrated-20260827`, host `127.0.0.1:23306`, seeded from repository SQL.
- Login: `2337` public, `2334` internal.
- Game: `2339` game, `2350` stream, `2380` WebApi, `2360` existing BotDriveBridge.
- Temporary runtime root: `/tmp/aaemu-mcp-integrated-20260827` (not repository state).
- `game_pak` was linked from `/root/hl-cp-test/ClientData/game_pak`; `compact.sqlite3` was linked from `/root/hl-cp-test/Data/compact.sqlite3`. Neither external asset was copied, downloaded, or modified.
- Game started with `AAEMU_BOT_CTRL=1` and an out-of-band temporary `AAEMU_BOT_CTRL_TOKEN`; the token is intentionally not recorded here or in the transcript.
- Game log reached `Server started!`, bound game/stream ports, registered on Login, and reported `WebApi server started on *:2380`.

## Exact MCP JSON-RPC transcript

The captured fresh-DB run used the same sequence as the reusable driver; reproduction is:

```bash
AAEMU_BOT_CTRL_URL=http://127.0.0.1:2380 \
AAEMU_BOT_CTRL_TOKEN='<temporary token supplied out-of-band>' \
python3 Scripts/mcp-integrated-e2e-benchmark.py \
  --transcript /tmp/aaemu-mcp-integrated-20260827/reusable-transcript.jsonl \
  --bridge-port 2360
```

The following is the complete newline-delimited transcript (calls are `->`; replies are `<-`; the notification intentionally has no reply). The action sidecar's `tools/list` response is retained byte-for-byte in the transcript, including all 19 tool schemas.

```text
management -> {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
management <- {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{},"serverInfo":{"name":"aaemu-bot-control","version":"1.0.0"}}}
management -> {"jsonrpc":"2.0","method":"notifications/initialized"}
management -> {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
management <- {"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"bot_list","description":"List all registered player bots (structured snapshot: name, id, state, fidelity, position).","inputSchema":{"type":"object","properties":{}}},{"name":"bot_status","description":"Bot registry + embodied state summary (registered/active counts and the full snapshot).","inputSchema":{"type":"object","properties":{}}},{"name":"bot_add","description":"Add/provision a player bot by name (idempotent adopt-or-create; optional spawn home x/y/z).","inputSchema":{"type":"object","properties":{"name":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["name"]}},{"name":"bot_remove","description":"Remove a player bot by name or numeric id (deactivates, leave-saves, drops the registry entry).","inputSchema":{"type":"object","properties":{"nameOrId":{"type":"string"}},"required":["nameOrId"]}},{"name":"bot_relocate","description":"Relocate a player bot's patrol home to x/y/z (terrain-clamped, route re-armed).","inputSchema":{"type":"object","properties":{"nameOrId":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["nameOrId","x","y","z"]}}]}}
management -> {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bot_status","arguments":{}}}
management <- {"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"{\"Success\":true,\"Message\":\"0 registered (0 active)\",\"Bots\":[]}"}],"isError":false}}
management -> {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"bot_list","arguments":{}}}
management <- {"jsonrpc":"2.0","id":4,"result":{"content":[{"type":"text","text":"{\"Success\":true,\"Message\":\"0 registered\",\"Bots\":[]}"}],"isError":false}}
management -> {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"bot_add","arguments":{"name":"McpIntegrated01"}}}
management <- {"jsonrpc":"2.0","id":5,"result":{"content":[{"type":"text","text":"{\"Success\":true,\"Message\":\"Bot 'McpIntegrated01' (id 1) added \u2014 Full fidelity, roaming around 15578/15382/126.\",\"Bots\":null}"}],"isError":false}}
management -> {"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"bot_add","arguments":{"name":"McpIntegrated01"}}}
management <- {"jsonrpc":"2.0","id":6,"result":{"content":[{"type":"text","text":"{\"Success\":true,\"Message\":\"Bot 'McpIntegrated01' (id 1) is already present and active.\",\"Bots\":null}"}],"isError":false}}
management -> {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"bot_status","arguments":{}}}
management <- {"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"{\"Success\":true,\"Message\":\"1 registered (1 active)\",\"Bots\":[{\"Name\":\"Mcpintegrated01\",\"Id\":1,\"State\":\"Active\",\"Fidelity\":\"Full\",\"X\":15578.042,\"Y\":15382.122,\"Z\":126.484}]}"}],"isError":false}}
management -> {"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"bot_list","arguments":{}}}
management <- {"jsonrpc":"2.0","id":8,"result":{"content":[{"type":"text","text":"{\"Success\":true,\"Message\":\"1 registered\",\"Bots\":[{\"Name\":\"Mcpintegrated01\",\"Id\":1,\"State\":\"Active\",\"Fidelity\":\"Full\",\"X\":15578.042,\"Y\":15382.122,\"Z\":126.484}]}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
actions <- {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"aaemu-bot-actions","version":"1.0.0"}}}
actions -> {"jsonrpc":"2.0","method":"notifications/initialized"}
actions -> {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
actions <- {"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"observe","description":"Observation snapshot of a registered bot (position, targets, nearby entities). POST /api/actors/observe.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"}},"required":["bot"]}},{"name":"move","description":"Walk a bot to an absolute position (bounded, terrain-aware). POST /api/actors/move.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"speed":{"type":"number"},"timeoutSec":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","x","y","z"]}},{"name":"interact","description":"Interact with a doodad (skillId 0 = skill-less branch). POST /api/actors/interact.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"doodadObjId":{"type":"number"},"skillId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","doodadObjId"]}},{"name":"accept_quest","description":"Accept a quest through the real AddQuest gate. POST /api/actors/accept_quest.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"questId":{"type":"number"},"acceptorType":{"type":"string"},"acceptorId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","questId","acceptorType","acceptorId"]}},{"name":"turn_in_quest","description":"Turn in a quest at an NPC. POST /api/actors/turn_in_quest.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"questId":{"type":"number"},"npcObjId":{"type":"number"},"selectedReward":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","questId"]}},{"name":"loot","description":"Loot a corpse/bag owner (loot-all). POST /api/actors/loot.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"lootOwnerObjId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","lootOwnerObjId"]}},{"name":"use_item","description":"Use an inventory item (targetObjId 0 = self). POST /api/actors/use_item.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"itemTemplateId":{"type":"number"},"targetObjId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","itemTemplateId"]}},{"name":"mount","description":"Mount an owned mate. POST /api/actors/mount.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"mateObjId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","mateObjId"]}},{"name":"move_to_unit","description":"Walk to a unit's current position. POST /api/actors/move_to_unit.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"targetObjId":{"type":"number"},"speed":{"type":"number"},"timeoutSec":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","targetObjId"]}},{"name":"stop","description":"Stop the bot's running request (no-op when idle). POST /api/actors/stop.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"}},"required":["bot"]}},{"name":"target","description":"Set the bot's current target. POST /api/actors/target.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"targetObjId":{"type":"number"}},"required":["bot","targetObjId"]}},{"name":"cast","description":"Cast a known skill at a unit. POST /api/actors/cast.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"skillId":{"type":"number"},"targetObjId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","skillId","targetObjId"]}},{"name":"dismount","description":"Dismount (mateObjId 0 = current mount). POST /api/actors/dismount.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"mateObjId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot"]}},{"name":"advance_quest","description":"One step-machine advance on an active quest. POST /api/actors/advance_quest.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"questId":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","questId"]}},{"name":"turn_in_doodad","description":"Turn in a quest at a doodad. POST /api/actors/turn_in_doodad.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"questId":{"type":"number"},"doodadObjId":{"type":"number"},"selectedReward":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","questId","doodadObjId"]}},{"name":"auto_turn_in","description":"Auto-complete turn-in (no world target). POST /api/actors/auto_turn_in.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"questId":{"type":"number"},"selectedReward":{"type":"number"},"idempotencyKey":{"type":"string"}},"required":["bot","questId"]}},{"name":"interrupt","description":"Cancel a running request by its API trace id. POST /api/actors/interrupt.","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"traceId":{"type":"string"}},"required":["bot","traceId"]}},{"name":"action_status","description":"Poll one action's lifecycle by trace id (GET /api/actors/actions/{traceId}) \u2014 the async response channel for every enqueued action.","inputSchema":{"type":"object","properties":{"traceId":{"type":"string"}},"required":["traceId"]}},{"name":"trace","description":"Per-bot audit trail, newest first (GET /api/actors/trace?bot=..&limit=..).","inputSchema":{"type":"object","properties":{"bot":{"type":"string"},"limit":{"type":"number"}},"required":["bot"]}}]}}
actions -> {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"observe","arguments":{"bot":"McpIntegrated01"}}}
actions <- {"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"{\"success\":true,\"message\":\"accepted\",\"trace_id\":\"48b67a84-9a72-4898-9639-c88f797a4220\",\"bot\":\"Mcpintegrated01\",\"action\":\"Observe\",\"state\":\"Requested\"}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"48b67a84-9a72-4898-9639-c88f797a4220"}}}
actions <- {"jsonrpc":"2.0","id":4,"result":{"content":[{"type":"text","text":"{\"trace_id\":\"48b67a84-9a72-4898-9639-c88f797a4220\",\"actor_id\":1,\"bot\":\"Mcpintegrated01\",\"action\":\"Observe\",\"state\":\"Requested\",\"failure\":null,\"detail\":null,\"requested_at\":\"2026-08-27T17:12:31.5851801Z\",\"started_at\":null,\"completed_at\":null,\"state_changes\":[\"Requested\"],\"audit\":null,\"result_payload\":null}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"48b67a84-9a72-4898-9639-c88f797a4220"}}}
actions <- {"jsonrpc":"2.0","id":5,"result":{"content":[{"type":"text","text":"{\"trace_id\":\"48b67a84-9a72-4898-9639-c88f797a4220\",\"actor_id\":37293,\"bot\":\"Mcpintegrated01\",\"action\":\"Observe\",\"state\":\"Completed\",\"failure\":null,\"detail\":\"completed\",\"requested_at\":\"2026-08-27T17:12:31.5976791Z\",\"started_at\":\"2026-08-27T17:12:31.59768Z\",\"completed_at\":\"2026-08-27T17:12:31.5977905Z\",\"state_changes\":[\"Requested\",\"Accepted (observe)\",\"Running (query)\",\"Completed (completed)\"],\"audit\":{\"trace_id\":\"9d2d1c60-2e18-4d0e-80bf-12a91bcc36b8\",\"actor_id\":37293,\"action\":\"Observe\",\"target_id\":0,\"requested_at\":\"2026-08-27T17:12:31.5976791Z\",\"started_at\":\"2026-08-27T17:12:31.59768Z\",\"completed_at\":\"2026-08-27T17:12:31.5977905Z\",\"result\":\"Completed\",\"failure\":null,\"detail\":\"completed\",\"state_changes\":[\"Requested\",\"Accepted (observe)\",\"Running (query)\",\"Completed (completed)\"],\"target_hp_before\":null,\"target_hp_after\":null,\"effect_observed\":null,\"effect_wait_ms\":null},\"result_payload\":{\"ActorId\":37293,\"Position\":{\"X\":15579.981,\"Y\":15384.062,\"Z\":126.445694},\"CurrentTargetObjId\":0,\"Hp\":1416,\"MaxHp\":1416,\"Mp\":1166,\"MaxMp\":1166,\"NearbyCharacterObjIds\":[],\"NearbyNpcObjIds\":[],\"NearbyDoodadObjIds\":[],\"ActiveQuestIds\":[]}}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"trace","arguments":{"bot":"McpIntegrated01","limit":10}}}
actions <- {"jsonrpc":"2.0","id":20,"result":{"content":[{"type":"text","text":"{\"success\":true,\"message\":\"1 trace record(s) for 'McpIntegrated01'\",\"trace\":[{\"trace_id\":\"48b67a84-9a72-4898-9639-c88f797a4220\",\"actor_id\":37293,\"bot\":\"Mcpintegrated01\",\"action\":\"Observe\",\"state\":\"Completed\",\"failure\":null,\"detail\":\"completed\",\"requested_at\":\"2026-08-27T17:12:31.5976791Z\",\"started_at\":\"2026-08-27T17:12:31.59768Z\",\"completed_at\":\"2026-08-27T17:12:31.5977905Z\",\"state_changes\":[\"Requested\",\"Accepted (observe)\",\"Running (query)\",\"Completed (completed)\"],\"audit\":{\"trace_id\":\"9d2d1c60-2e18-4d0e-80bf-12a91bcc36b8\",\"actor_id\":37293,\"action\":\"Observe\",\"target_id\":0,\"requested_at\":\"2026-08-27T17:12:31.5976791Z\",\"started_at\":\"2026-08-27T17:12:31.59768Z\",\"completed_at\":\"2026-08-27T17:12:31.5977905Z\",\"result\":\"Completed\",\"failure\":null,\"detail\":\"completed\",\"state_changes\":[\"Requested\",\"Accepted (observe)\",\"Running (query)\",\"Completed (completed)\"],\"target_hp_before\":null,\"target_hp_after\":null,\"effect_observed\":null,\"effect_wait_ms\":null},\"result_payload\":{\"ActorId\":37293,\"Position\":{\"X\":15579.981,\"Y\":15384.062,\"Z\":126.445694},\"CurrentTargetObjId\":0,\"Hp\":1416,\"MaxHp\":1416,\"Mp\":1166,\"MaxMp\":1166,\"NearbyCharacterObjIds\":[],\"NearbyNpcObjIds\":[],\"NearbyDoodadObjIds\":[],\"ActiveQuestIds\":[]}}]}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"move","arguments":{"bot":"McpIntegrated01","x":15582.0,"y":15384.1,"z":126.4,"speed":1.0,"timeoutSec":8}}}
actions <- {"jsonrpc":"2.0","id":21,"result":{"content":[{"type":"text","text":"{\"success\":true,\"message\":\"accepted\",\"trace_id\":\"ee344fcd-c9c0-44e7-9b1e-be8b5408011b\",\"bot\":\"Mcpintegrated01\",\"action\":\"Move\",\"state\":\"Requested\"}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"ee344fcd-c9c0-44e7-9b1e-be8b5408011b"}}}
actions <- {"jsonrpc":"2.0","id":22,"result":{"content":[{"type":"text","text":"{\"trace_id\":\"ee344fcd-c9c0-44e7-9b1e-be8b5408011b\",\"actor_id\":1,\"bot\":\"Mcpintegrated01\",\"action\":\"Move\",\"state\":\"Requested\",\"failure\":null,\"detail\":null,\"requested_at\":\"2026-08-27T17:12:32.6260451Z\",\"started_at\":null,\"completed_at\":null,\"state_changes\":[\"Requested\"],\"audit\":null,\"result_payload\":null}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"ee344fcd-c9c0-44e7-9b1e-be8b5408011b"}}}
actions <- {"jsonrpc":"2.0","id":23,"result":{"content":[{"type":"text","text":"{\"trace_id\":\"ee344fcd-c9c0-44e7-9b1e-be8b5408011b\",\"actor_id\":37293,\"bot\":\"Mcpintegrated01\",\"action\":\"Move\",\"state\":\"Running\",\"failure\":null,\"detail\":null,\"requested_at\":\"2026-08-27T17:12:32.6294363Z\",\"started_at\":\"2026-08-27T17:12:32.6294369Z\",\"completed_at\":null,\"state_changes\":[\"Requested\",\"Accepted (move)\",\"Running (walking)\"],\"audit\":null,\"result_payload\":null}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"ee344fcd-c9c0-44e7-9b1e-be8b5408011b"}}}
actions <- {"jsonrpc":"2.0","id":24,"result":{"content":[{"type":"text","text":"{\"trace_id\":\"ee344fcd-c9c0-44e7-9b1e-be8b5408011b\",\"actor_id\":37293,\"bot\":\"Mcpintegrated01\",\"action\":\"Move\",\"state\":\"Completed\",\"failure\":null,\"detail\":\"arrived\",\"requested_at\":\"2026-08-27T17:12:32.6294363Z\",\"started_at\":\"2026-08-27T17:12:32.6294369Z\",\"completed_at\":\"2026-08-27T17:12:34.077998Z\",\"state_changes\":[\"Requested\",\"Accepted (move)\",\"Running (walking)\",\"Completed (arrived)\"],\"audit\":{\"trace_id\":\"39565ad3-0a6a-4072-bf2e-ff186341ab42\",\"actor_id\":37293,\"action\":\"Move\",\"target_id\":0,\"requested_at\":\"2026-08-27T17:12:32.6294363Z\",\"started_at\":\"2026-08-27T17:12:32.6294369Z\",\"completed_at\":\"2026-08-27T17:12:34.077998Z\",\"result\":\"Completed\",\"failure\":null,\"detail\":\"arrived\",\"state_changes\":[\"Requested\",\"Accepted (move)\",\"Running (walking)\",\"Completed (arrived)\"],\"target_hp_before\":null,\"target_hp_after\":null,\"effect_observed\":null,\"effect_wait_ms\":null},\"result_payload\":null}"}],"isError":false}}
actions -> {"jsonrpc":"2.0","id":40,"method":"tools/call","params":{"name":"trace","arguments":{"bot":"McpIntegrated01","limit":20}}}
actions <- {"jsonrpc":"2.0","id":40,"result":{"content":[{"type":"text","text":"{\"success\":true,\"message\":\"2 trace record(s) for 'McpIntegrated01'\",\"trace\":[{\"trace_id\":\"ee344fcd-c9c0-44e7-9b1e-be8b5408011b\",\"actor_id\":37293,\"bot\":\"Mcpintegrated01\",\"action\":\"Move\",\"state\":\"Completed\",\"failure\":null,\"detail\":\"arrived\",\"requested_at\":\"2026-08-27T17:12:32.6294363Z\",\"started_at\":\"2026-08-27T17:12:32.6294369Z\",\"completed_at\":\"2026-08-27T17:12:34.077998Z\",\"state_changes\":[\"Requested\",\"Accepted (move)\",\"Running (walking)\",\"Completed (arrived)\"],\"audit\":{\"trace_id\":\"39565ad3-0a6a-4072-bf2e-ff186341ab42\",\"actor_id\":37293,\"action\":\"Move\",\"target_id\":0,\"requested_at\":\"2026-08-27T17:12:32.6294363Z\",\"started_at\":\"2026-08-27T17:12:32.6294369Z\",\"completed_at\":\"2026-08-27T17:12:34.077998Z\",\"result\":\"Completed\",\"failure\":null,\"detail\":\"arrived\",\"state_changes\":[\"Requested\",\"Accepted (move)\",\"Running (walking)\",\"Completed (arrived)\"],\"target_hp_before\":null,\"target_hp_after\":null,\"effect_observed\":null,\"effect_wait_ms\":null},\"result_payload\":null},{\"trace_id\":\"48b67a84-9a72-4898-9639-c88f797a4220\",\"actor_id\":37293,\"bot\":\"Mcpintegrated01\",\"action\":\"Observe\",\"state\":\"Completed\",\"failure\":null,\"detail\":\"completed\",\"requested_at\":\"2026-08-27T17:12:31.5976791Z\",\"started_at\":\"2026-08-27T17:12:31.59768Z\",\"completed_at\":\"2026-08-27T17:12:31.5977905Z\",\"state_changes\":[\"Requested\",\"Accepted (observe)\",\"Running (query)\",\"Completed (completed)\"],\"audit\":{\"trace_id\":\"9d2d1c60-2e18-4d0e-80bf-12a91bcc36b8\",\"actor_id\":37293,\"action\":\"Observe\",\"target_id\":0,\"requested_at\":\"2026-08-27T17:12:31.5976791Z\",\"started_at\":\"2026-08-27T17:12:31.59768Z\",\"completed_at\":\"2026-08-27T17:12:31.5977905Z\",\"result\":\"Completed\",\"failure\":null,\"detail\":\"completed\",\"state_changes\":[\"Requested\",\"Accepted (observe)\",\"Running (query)\",\"Completed (completed)\"],\"target_hp_before\":null,\"target_hp_after\":null,\"effect_observed\":null,\"effect_wait_ms\":null},\"result_payload\":{\"ActorId\":37293,\"Position\":{\"X\":15579.981,\"Y\":15384.062,\"Z\":126.445694},\"CurrentTargetObjId\":0,\"Hp\":1416,\"MaxHp\":1416,\"Mp\":1166,\"MaxMp\":1166,\"NearbyCharacterObjIds\":[],\"NearbyNpcObjIds\":[],\"NearbyDoodadObjIds\":[],\"ActiveQuestIds\":[]}}]}"}],"isError":false}}
bridge -> {"cmd":"drive","bot":"McpIntegrated01","op":"charPos"}
bridge <- {"ok":false,"error":"bot \u0027McpIntegrated01\u0027 is not in the world (no active networked session) \u2014 table []"}
```

Trace IDs from this transcript:

| operation | trace ID | terminal state | lifecycle evidence |
|---|---|---|---|
| `observe` | `48b67a84-9a72-4898-9639-c88f797a4220` | `Completed` | `Requested → Accepted (observe) → Running (query) → Completed (completed)` |
| `move` | `ee344fcd-c9c0-44e7-9b1e-be8b5408011b` | `Completed` | `Requested → Accepted (move) → Running (walking) → Completed (arrived)` |

The MCP action response's `actor_id=37293` is the in-memory actor object id. The management response reports the durable character id as `1`; these are different engine identifiers and are not conflated.

## Evidence boundary

| Layer | What this run proves | What it does not prove |
|---|---|---|
| MCP stdio | Generic sidecar startup, JSON-RPC initialize/notification/tools-list, tool argument mapping, and exact request/reply correlation. | A game server or gameplay path by protocol-only smoke alone. |
| Authenticated Game WebApi | Token-gated management and action HTTP reached the real Game process; add/adopt, observe, action lifecycle, move, and trace all executed. | Client rendering, client packet decoding, human feel, or broad navigation reliability. `move` is one short route only. |
| Direct server-side DB | Same normalized bot character has a durable ordinary `characters` row with world/zone/position fields. | Immediate in-memory transform after movement; restart recovery; wire packet semantics. |
| Existing BotDriveBridge | The diagnostic route is present and the request was attempted; its explicit no-network-session error identifies the lifecycle boundary. | Authenticated network evidence for a client-login-blocked managed bot. |

Still requiring separate evidence: a real authenticated TCP `BotNetworkSession` packet/state capture for a client-login-allowed account, direct restart/reload assertions, richer DB invariants, human client/launcher confirmation, and scaling/soak measurements. None are inferred from this benchmark.

## PASS / FAIL / BLOCKED criteria and attribution

- **PASS — protocol:** `Scripts/mcp-stdio-smoke.sh` completed with `MCP stdio protocol smoke passed: 19 tools`.
- **PASS — live management:** initial `bot_status`/`bot_list` returned zero registrations; add returned id `1`; repeated add returned `already present and active`; subsequent status/list returned one `Active`, `Full` bot.
- **PASS — live action:** observe and move enqueue acknowledgements returned trace IDs; bounded `action_status` calls reached `Completed`; trace returned the records newest-first.
- **PASS — independent cross-check:** SQL returned the expected named character row after the MCP run.
- **BLOCKED — authenticated wire leg:** bridge returned the explicit `no active networked session` error because managed headless bot accounts are blocked from client login. Attribution is **lifecycle/harness boundary**, not Navigation or Game action failure.
- **FAIL:** none in the exercised MCP or DB legs. If a future move reaches `TimedOut`, record that terminal status, preserve its `failure/detail` and trace, attribute Navigation only when the server reports a navigation failure, and stop rather than retrying or rewriting the result.

## Reproduction and cleanup commands

The stack uses links for the external assets. A compact setup outline is:

```bash
ROOT=/tmp/aaemu-mcp-integrated-20260827
mkdir -p "$ROOT/runtime/game" "$ROOT/runtime/login" "$ROOT/runtime/game/Data" "$ROOT/runtime/game/ClientData"
ln -s /root/hl-cp-test/Data/compact.sqlite3 "$ROOT/runtime/game/Data/compact.sqlite3"
ln -s /root/hl-cp-test/ClientData/game_pak "$ROOT/runtime/game/ClientData/game_pak"
# Stage only repository server binaries/configuration under $ROOT/runtime.
# Write Config.Local.json with ports 2337/2339/2350/2380/2360/2334 and DB 23306.
docker compose -p aaemu-mcp-integrated-20260827 \
  -f Scripts/e2e/docker-compose.yaml --env-file "$ROOT/.env" up -d db
AAEMU_BOT_CTRL=1 AAEMU_BOT_CTRL_TOKEN='<temporary token>' \
  dotnet "$ROOT/runtime/login/AAEmu.Login.dll"
AAEMU_BOT_CTRL=1 AAEMU_BOT_CTRL_TOKEN='<temporary token>' \
  dotnet "$ROOT/runtime/game/AAEmu.Game.dll"
dotnet build AAEmu.BotControl/AAEmu.BotControl.csproj --nologo
dotnet build AAEmu.BotControlMcp/AAEmu.BotControlMcp.csproj --nologo
bash Scripts/mcp-stdio-smoke.sh
AAEMU_BOT_CTRL_URL=http://127.0.0.1:2380 \
AAEMU_BOT_CTRL_TOKEN='<temporary token>' \
  python3 Scripts/mcp-integrated-e2e-benchmark.py \
    --transcript "$ROOT/reusable-transcript.jsonl" --bridge-port 2360
MYSQL_PWD='<db password>' mysql -h 127.0.0.1 -P 23306 -u root -N \
  -e "SELECT id,name,account_id,world_id,zone_id,ROUND(x,3),ROUND(y,3),ROUND(z,3) FROM aaemu_game.characters WHERE name='Mcpintegrated01';"

docker compose -p aaemu-mcp-integrated-20260827 \
  -f Scripts/e2e/docker-compose.yaml --env-file "$ROOT/.env" down -v
```

Do not put a real Bot Control token in a config file, transcript, shell history shared with other users, or committed evidence.
