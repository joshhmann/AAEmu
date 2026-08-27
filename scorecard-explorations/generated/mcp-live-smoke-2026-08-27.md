# MCP live stdio smoke — 2026-08-27

## Verdict

**Protocol smoke:** PASS (both stdio servers initialized and listed tools).
**Live gameplay smoke:** BLOCKED (Game WebApi never started).

**UNAVAILABLE / not a gameplay success.** Both MCP sidecars started as generic newline-delimited JSON-RPC processes and completed protocol initialization plus `tools/list`. The isolated MySQL stack became healthy, and the Login server became ready on its shifted port. The Game process reached the normal data-loading phases but exited before starting WebApi with this exact fatal reason:

```text
[FATAL] Program - No client worlds data has been found, please check the readme.txt file inside the ClientData folder for more info.
```

The asset inventory for this fresh worktree also reported the client and `compact.sqlite3` as missing. The compact reference was not downloaded or modified. Because WebApi never listened on `127.0.0.1:1380`, no authenticated actor request reached HTTP, no bot was adopted, and no real action lifecycle/trace/state assertion can be claimed. This is an infrastructure/asset blocker, not a gameplay result.

## Isolation and setup

- Worktree: `/root/aaemu-dev/.worktrees/mcp-live-smoke`
- Base: `origin/develop` at `8a22dcb4df597f3a1eeedb36e465696375615943`
- Isolated Compose project: `aaemu-mcp-live-20260827`
- Temporary E2E root: `/tmp/aaemu-mcp-live-20260827` (removed after teardown)
- MySQL host port: `33306` (container `3306`)
- Login host ports: public `1337`, internal `1334`
- Game host ports: game `1339`, stream `1350`, WebApi `1380`
- Runtime API enable/token were supplied only through the isolated Game process environment; the token is intentionally not recorded.
- MySQL health: Compose reported `Healthy`; seed query returned `aaemu_login.users = 0`, `aaemu_game.characters = 0`.

The first Login attempt exposed a separate configuration prerequisite (`GameServers` required). A temporary ignored `AAEmu.Login/Config.Local.json` with one shifted game-server listing was then used. Login became ready. The first Game attempt from the repository root also hit a working-directory-only `Data/Path` lookup; retrying from `AAEmu.Game` loaded 43 managers and then reached the definitive missing-client-world fatal above.

## Exact generic MCP protocol evidence

### Management sidecar (`AAEmu.BotControl`)

Requests sent (the notification intentionally has no response):

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bot_status","arguments":{}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"bot_list","arguments":{}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"bot_add","arguments":{"name":"McpLiveSmoke01"}}}
```

Exact replies:

```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{},"serverInfo":{"name":"aaemu-bot-control","version":"1.0.0"}}}
{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"bot_list","description":"List all registered player bots (structured snapshot: name, id, state, fidelity, position).","inputSchema":{"type":"object","properties":{}}},{"name":"bot_status","description":"Bot registry + embodied state summary (registered/active counts and the full snapshot).","inputSchema":{"type":"object","properties":{}}},{"name":"bot_add","description":"Add/provision a player bot by name (idempotent adopt-or-create; optional spawn home x/y/z).","inputSchema":{"type":"object","properties":{"name":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["name"]}},{"name":"bot_remove","description":"Remove a player bot by name or numeric id (deactivates, leave-saves, drops the registry entry).","inputSchema":{"type":"object","properties":{"nameOrId":{"type":"string"}},"required":["nameOrId"]}},{"name":"bot_relocate","description":"Relocate a player bot's patrol home to x/y/z (terrain-clamped, route re-armed).","inputSchema":{"type":"object","properties":{"nameOrId":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["nameOrId","x","y","z"]}}]}}
{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"Internal error: Connection refused (127.0.0.1:1380)"}}
{"jsonrpc":"2.0","id":4,"error":{"code":-32603,"message":"Internal error: Connection refused (127.0.0.1:1380)"}}
{"jsonrpc":"2.0","id":5,"error":{"code":-32603,"message":"Internal error: Connection refused (127.0.0.1:1380)"}}
```

HTTP status for calls 3–5: **none**; the TCP connection was refused before an HTTP response. Therefore `bot_status`, `bot_list`, and `bot_add` were exercised at the MCP boundary but did not execute in Game.

### Contract-action sidecar (`AAEmu.BotControlMcp`)

Requests sent:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"observe","arguments":{"bot":"McpLiveSmoke01"}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"00000000-0000-0000-0000-000000000000"}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"trace","arguments":{"bot":"McpLiveSmoke01","limit":5}}}
```

Exact protocol/error replies:

```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"aaemu-bot-actions","version":"1.0.0"}}}
{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"Internal error: Connection refused (127.0.0.1:1380)"}}
{"jsonrpc":"2.0","id":4,"error":{"code":-32603,"message":"Internal error: Connection refused (127.0.0.1:1380)"}}
{"jsonrpc":"2.0","id":5,"error":{"code":-32603,"message":"Internal error: Connection refused (127.0.0.1:1380)"}}
```

The exact `tools/list` reply returned all 19 registered tools: `observe`, `move`, `interact`, `accept_quest`, `turn_in_quest`, `loot`, `use_item`, `mount`, `move_to_unit`, `stop`, `target`, `cast`, `dismount`, `advance_quest`, `turn_in_doodad`, `auto_turn_in`, `interrupt`, `action_status`, and `trace`. Its schemas included required fields for each tool (for example `observe.bot`, `move.bot/x/y/z`, `action_status.traceId`, and `trace.bot`).

HTTP status for calls 3–5: **none**; all failed before HTTP due to the unavailable WebApi. Consequently there is no real `trace_id`, no lifecycle state beyond the absent server, no trace record, and no observed world state.

## Protocol-only smoke and cleanup

The required raw smoke script passed independently of the Game server:

```text
$ bash /root/aaemu-dev/.worktrees/mcp-live-smoke/Scripts/mcp-stdio-smoke.sh
MCP stdio protocol smoke passed: 19 tools
```

After evidence capture, the temporary Login/Game `Config.Local.json` files and compact symlink in the fresh worktree were removed, and the isolated Compose project was torn down with volumes. No main tree or survivor worktree was changed. The next live task is to provide the client `game_pak`/world assets, start Game on the shifted ports, and repeat the bounded management → observe → action_status → trace → state-assertion workflow with exact HTTP evidence.
