# AAEmu.BotControlMcp — contract-action MCP sidecar (agent tier)

MCP stdio sidecar exposing the game's **contract-action API** (`/api/actors/*`,
M5 stage 3, t_7b6d7a4b) as MCP tools so Hermes profiles / LLM agents can
drive registered bots as **players** (consumer tier 2). Tools map 1:1 to the
M5 CONTRACT ACTIONS — each tool issues a validated action request with the
full lifecycle, never engine internals.

- **Transport:** MCP stdio (newline-delimited JSON-RPC 2.0)
- **Backend:** HTTP calls to the game's WebApi `/api/actors/*` endpoints,
  authenticated with the shared `X-Auth-Token`
- **Separate process:** zero code inside the game process beyond the HTTP
  client. Requests go through the API's enqueue-only queue — a crashed MCP
  client cannot wedge the world (the pending action completes or times out
  server-side per its lifecycle).
- **No management ops:** `bot_add/remove/list/relocate/status` stay on the
  P1 MCP surface (`AAEmu.BotControl`, t_2ea94a20) — deliberately not
  duplicated here.

## Tools (1:1 with the contract API)

| Tool | Maps to | Purpose |
|------|---------|---------|
| `observe` | `POST /api/actors/observe` | Observation snapshot (position, targets, nearby) |
| `move` | `POST /api/actors/move` | Walk to an absolute position |
| `interact` | `POST /api/actors/interact` | Interact with a doodad |
| `accept_quest` | `POST /api/actors/accept_quest` | Accept a quest (real AddQuest gate) |
| `turn_in_quest` | `POST /api/actors/turn_in_quest` | Turn in a quest at an NPC |
| `loot` | `POST /api/actors/loot` | Loot a corpse/bag owner |
| `use_item` | `POST /api/actors/use_item` | Use an inventory item |
| `mount` | `POST /api/actors/mount` | Mount an owned mate |
| `move_to_unit` | `POST /api/actors/move_to_unit` | Walk to a unit (B1) |
| `stop` | `POST /api/actors/stop` | Stop the running request (B1) |
| `target` | `POST /api/actors/target` | Set the current target (B1) |
| `cast` | `POST /api/actors/cast` | Cast a known skill at a unit (B1) |
| `dismount` | `POST /api/actors/dismount` | Dismount (B1) |
| `advance_quest` | `POST /api/actors/advance_quest` | Step-machine advance (B1) |
| `turn_in_doodad` | `POST /api/actors/turn_in_doodad` | Turn in at a doodad (B1) |
| `auto_turn_in` | `POST /api/actors/auto_turn_in` | Auto-complete turn-in (B1) |
| `interrupt` | `POST /api/actors/interrupt` | Cancel by trace id (B1) |
| `action_status` | `GET /api/actors/actions/{traceId}` | Lifecycle poll (async response channel) |
| `trace` | `GET /api/actors/trace?bot=..&limit=..` | Per-bot audit trail |

Every action tool returns the **enqueue acknowledgement** (`success`,
`trace_id`, `bot`, `action`, `state`). The caller then polls
`action_status` with that trace id for lifecycle transitions
(Requested → Running → Completed/Failed) — the same async-response
pattern the scripted fleet (tier 1) uses over HTTP.

## Running

```bash
export AAEMU_BOT_CTRL_URL=http://127.0.0.1:1280   # game WebApi (default)
export AAEMU_BOT_CTRL_TOKEN=<shared secret>        # required — same token the game validates
dotnet run --project AAEmu.BotControlMcp
```

The server is a pure pipe: MCP clients spawn it as a subprocess and speak
JSON-RPC on stdin/stdout.

## Manual smoke (raw JSON-RPC)

```bash
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"observe","arguments":{"bot":"McpBot01"}}}' \
  '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"<trace_id_from_observe>"}}}' \
| dotnet run --project AAEmu.BotControlMcp
```

## Registering in Hermes (native MCP client)

Add to `~/.hermes/config.yaml` of the sister profile:

```yaml
mcp_servers:
  aaemu_bot_actions:
    command: "dotnet"
    args: ["run", "--project", "/root/aaemu-dev/AAEmu.BotControlMcp", "--no-launch-profile"]
    env:
      AAEMU_BOT_CTRL_URL: "http://127.0.0.1:1280"
      AAEMU_BOT_CTRL_TOKEN: "<shared secret>"
```

Restart Hermes — tools appear as `mcp_aaemu_bot_actions_observe`,
`mcp_aaemu_bot_actions_move`, etc.

> Prefer a published binary over `dotnet run` for a persistent registration:
> `dotnet publish AAEmu.BotControlMcp -c Release -o /opt/aaemu-bot-actions`
> and point `command` at `/opt/aaemu-bot-actions/AAEmu.BotControlMcp`.

## Security

- The game-side API is **disabled by default** (`AAEMU_BOT_CTRL=1` or
  `Bots.EnableBotControl` required) — prod never exposes it unless enabled.
- Every HTTP request must carry `X-Auth-Token` matching
  `AAEMU_BOT_CTRL_TOKEN` (env secret). The sidecar adds it automatically
  from its own env.
- Never put the token in shared config files or logs.
