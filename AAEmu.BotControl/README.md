# AAEmu.BotControl — MCP stdio gateway

Thin MCP server exposing the game's **bot control API** as MCP tools so
sisters (Aya/Tai/Rei/Mai/hx-*) can drive bot operations from their own
tools — no game client, no GM login.

- **Transport:** MCP stdio (newline-delimited JSON-RPC 2.0)
- **Backend:** HTTP calls to the game's WebApi `/api/bots*` endpoints,
  authenticated with the shared `X-Auth-Token`
- **No direct bot code:** every mutation executes inside the game process
  (single execution boundary, no parallel bot path)

## Tools

| Tool | Maps to | Purpose |
|------|---------|---------|
| `bot_list` | `GET /api/bots` | Structured snapshot (name, id, state, fidelity, position) |
| `bot_status` | `GET /api/bots/status` | Registered/active counts + full snapshot |
| `bot_add` | `POST /api/bots` | Add a bot by name (idempotent adopt-or-create), optional x/y/z home |
| `bot_remove` | `POST /api/bots/remove` | Remove by name or id (leave-save, no orphan rows) |
| `bot_relocate` | `POST /api/bots/relocate` | Move patrol home to x/y/z (terrain-clamped) |

## Running

```bash
export AAEMU_BOT_CTRL_URL=http://127.0.0.1:1280   # game WebApi (default)
export AAEMU_BOT_CTRL_TOKEN=<shared secret>        # required — same token the game validates
dotnet run --project AAEmu.BotControl
```

The server is a pure pipe: MCP clients spawn it as a subprocess and speak
JSON-RPC on stdin/stdout.

## Manual smoke (raw JSON-RPC)

```bash
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bot_list","arguments":{}}}' \
  '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"bot_add","arguments":{"name":"McpBot01"}}}' \
  '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"bot_relocate","arguments":{"nameOrId":"McpBot01","x":15572,"y":15364,"z":126.5}}}' \
  '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"bot_remove","arguments":{"nameOrId":"McpBot01"}}}' \
| dotnet run --project AAEmu.BotControl
```

## Registering in Hermes (native MCP client)

Add to `~/.hermes/config.yaml` of the sister profile:

```yaml
mcp_servers:
  aaemu_bot_control:
    command: "dotnet"
    args: ["run", "--project", "/root/aaemu-dev/AAEmu.BotControl", "--no-launch-profile"]
    env:
      AAEMU_BOT_CTRL_URL: "http://127.0.0.1:1280"
      AAEMU_BOT_CTRL_TOKEN: "<shared secret>"
```

Restart Hermes — tools appear as `mcp_aaemu_bot_control_bot_add`,
`mcp_aaemu_bot_control_bot_list`, etc.

> Prefer a published binary over `dotnet run` for a persistent registration:
> `dotnet publish AAEmu.BotControl -c Release -o /opt/aaemu-botcontrol` and
> point `command` at `/opt/aaemu-botcontrol/AAEmu.BotControl`.

## Security

- The game-side API is **disabled by default** (`AAEMU_BOT_CTRL=1` or
  `Bots.EnableBotControl` required) — prod never exposes it unless explicitly
  enabled.
- Every HTTP request must carry `X-Auth-Token` matching
  `AAEMU_BOT_CTRL_TOKEN` (env secret). The MCP server adds it automatically
  from its own env.
- Never put the token in shared config files or logs.
