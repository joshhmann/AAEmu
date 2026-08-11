# Bot Control Gateway (API + MCP)

Programmatic bot management for fleet testing (P1 t_2ea94a20): the same
operations as the GM `/bot` commands (add / remove / list / relocate) as a
token-gated HTTP API + MCP tools, so sisters can drive bots from their own
tools — no game client, no GM login.

```
sisters (Hermes) ──► AAEmu.BotControl (MCP stdio) ──► WebApi /api/bots* ──► BotAdminService ──► PlayerBotManager + provisioning + lifecycle
        tools: bot_add / bot_remove / bot_list / bot_relocate / bot_status         (the SAME core the /bot GM commands call)
```

One control core, two frontends: the GM command surface (in-game) and the
API/MCP surface (programmatic) share `BotAdminService` — no parallel bot
path, single execution boundary.

## Endpoints

| Method | Path | Body | Effect |
|--------|------|------|--------|
| GET | `/api/bots` | — | Structured list (name, id, state, fidelity, x/y/z) |
| GET | `/api/bots/status` | — | Registered/active counts + full snapshot |
| POST | `/api/bots` | `{"name": "...", "x":?, "y":?, "z":?}` | Add/provision a bot (idempotent adopt-or-create; optional spawn home) |
| POST | `/api/bots/remove` | `{"nameOrId": "..."}` | Remove by name or id (leave-save, no orphan rows) |
| POST | `/api/bots/relocate` | `{"nameOrId": "...", "x":, "y":, "z":}` | Relocate patrol home (terrain-clamped, route re-armed) |

Responses: `{"success": bool, "message": "...", "bots": [...]?}` — `bots`
present on list/status.

## Enablement

**Disabled by default — prod never exposes the surface unless explicitly
enabled** (same posture as `AAEMU_PRESENCE_DEMO`).

Enable (either):
- env `AAEMU_BOT_CTRL=1` (or `true`), or
- runtime config `Bots.EnableBotControl: true` in `Config.Local.json`

Token (env secret, required):
- env `AAEMU_BOT_CTRL_TOKEN=<secret>`, or
- config fallback `Bots.BotControlToken`

Every request must present the token: header `X-Auth-Token: <secret>`.
Without a configured token the API fails closed (401 on everything).

| Scenario | AAEMU_BOT_CTRL | AAEMU_BOT_CTRL_TOKEN |
|---|---|---|
| **prod** | unset (default) | unset — endpoint 404s |
| **test / local stack** | `1` | set per-environment |
| **sister profile** | n/a (game-side) | the same secret, in the MCP server env |

## MCP server

`AAEmu.BotControl/` — thin stdio MCP server (newline-delimited JSON-RPC 2.0)
wrapping the control API. Tools: `bot_list`, `bot_status`, `bot_add`,
`bot_remove`, `bot_relocate`. It never touches bot code — all mutations
execute inside the game process.

```bash
export AAEMU_BOT_CTRL_URL=http://127.0.0.1:1280   # game WebApi (default)
export AAEMU_BOT_CTRL_TOKEN=<secret>
dotnet run --project AAEmu.BotControl
```

Register in a sister profile's `~/.hermes/config.yaml` (native MCP client):

```yaml
mcp_servers:
  aaemu_bot_control:
    command: "/opt/aaemu-botcontrol/AAEmu.BotControl"
    env:
      AAEMU_BOT_CTRL_URL: "http://127.0.0.1:1280"
      AAEMU_BOT_CTRL_TOKEN: "<secret>"
```

Publish once: `dotnet publish AAEmu.BotControl -c Release -o /opt/aaemu-botcontrol`.
After a Hermes restart the tools appear as `mcp_aaemu_bot_control_bot_*`.
Full usage + raw JSON-RPC smoke: `AAEmu.BotControl/README.md`.

## Security notes

- API disabled by default; token-gated with fixed-time comparison; 404 when
  disabled (surface hidden), 401 on missing/bad token.
- Token lives in env (or local-only `Config.Local.json`) — never in shared
  config, never in logs, never in this wiki.
- Ops are the same GM-class operations (add/remove/relocate bots) — the API
  is a GM-equivalent surface, only reachable by whoever holds the secret.
- The separate **contract control plane** (t_7b6d7a4b, M5 contract actions:
  observe/move/interact/accept_quest) stays clean of admin verbs; management
  ops live here.

## Files

- `AAEmu.Game/Core/Managers/Bots/BotAdminService.cs` — shared core
  (`ListStatus()` additive snapshot for the API)
- `AAEmu.Game/Services/WebApi/Controllers/BotControlController.cs` — routes
- `AAEmu.Game/Services/WebApi/Controllers/BotControlSettings.cs` — gate/token
- `AAEmu.Game/Services/WebApi/Models/BotControlModels.cs` — DTOs
- `AAEmu.BotControl/` — MCP server
- `AAEmu.UnitTests/Game/Services/WebApi/BotControlApiTests.cs`,
  `AAEmu.UnitTests/BotControl/BotControlMcpTests.cs` — rigs
