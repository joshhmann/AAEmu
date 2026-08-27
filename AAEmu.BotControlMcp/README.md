# AAEmu.BotControlMcp — contract-action MCP sidecar

MCP stdio sidecar exposing the game's **authenticated actor-action API**
(`/api/actors/*`) as client-neutral MCP tools. Any MCP client (Claude,
Cursor, Gemini, Codex, or another implementation) can spawn the same process
and use the same newline-delimited JSON-RPC contract.

- **Transport:** MCP stdio (newline-delimited JSON-RPC 2.0)
- **Backend:** HTTP calls to the game's WebApi `/api/actors/*` endpoints,
  authenticated with the shared `X-Auth-Token`
- **Separate process:** zero code inside the game process beyond the HTTP
  client. Requests go through the API's enqueue-only queue — a crashed MCP
  client cannot wedge the world (the pending action completes or times out
  server-side per its lifecycle).
- **No management ops:** `bot_add/remove/list/relocate/status` stay on the
  management MCP surface (`AAEmu.BotControl`) — deliberately not duplicated
  here.

The source-grounded actor/API/tool/test matrix is maintained in
[`MCP-ACTION-MATRIX.md`](MCP-ACTION-MATRIX.md). This sidecar currently exposes
every safe actor route in `BotActionController`; actor methods without an
authenticated `/api/actors/*` route are explicitly deferred there.

## Tools (complete current surface)

| Tool | Maps to | Purpose |
|------|---------|---------|
| `observe` | `POST /api/actors/observe` | Observation snapshot (position, targets, nearby) |
| `move` | `POST /api/actors/move` | Walk to an absolute position |
| `interact` | `POST /api/actors/interact` | Interact with a doodad |
| `discover_quests` | `POST /api/actors/discover_quests` | Discover offers from a nearby NPC or doodad |
| `discover_self_quests` | `POST /api/actors/discover_self_quests` | Discover self-perceivable quest offers |
| `interact_with` | `POST /api/actors/interact_with` | Use a doodad's derived interaction skill |
| `talk` | `POST /api/actors/talk` | Credit NPC talk through the quest event path |
| `equip` | `POST /api/actors/equip` | Equip a bagged item by template |
| `deposit_money` | `POST /api/actors/deposit_money` | Deposit copper from inventory into bank |
| `withdraw_money` | `POST /api/actors/withdraw_money` | Withdraw copper from bank into inventory |
| `deposit_item` | `POST /api/actors/deposit_item` | Deposit an item stack from bag into bank |
| `withdraw_item` | `POST /api/actors/withdraw_item` | Withdraw an item stack from bank into bag |
| `plant` | `POST /api/actors/plant` | Plant a seed/young tree at a world position |
| `harvest` | `POST /api/actors/harvest` | Harvest a mature crop doodad |
| `craft` | `POST /api/actors/craft` | Craft ONE recipe step at a workbench |
| `buy` | `POST /api/actors/buy` | Buy an item from an NPC merchant |
| `sell` | `POST /api/actors/sell` | Sell an item to an NPC merchant |
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

## Client-neutral workflow

All clients should use the same evidence loop; the MCP client is only a
transport and orchestration layer:

1. `observe` the actor and record the returned `trace_id` and snapshot.
2. Call one action tool with the request shape shown in the table.
3. Poll `action_status` with that `trace_id` until the lifecycle is terminal
   (`Completed`, `Rejected`, `Interrupted`, or `TimedOut`).
4. Retrieve `trace` for the bot to correlate the audit record and state
   changes.
5. Assert the resulting observable state from `observe`, the action payload,
   or the game's ordinary API.
6. If the expected state change is absent, record the exact tool arguments,
   trace response, and API error as a blocker; do not replace it with a
   client-side shortcut.

The same sequence works when Claude, Cursor, Gemini, Codex, or another MCP
client spawns this stdio server. Raw JSON-RPC is also suitable for
deterministic CI smoke tests without a live game server.

## Deferred actor actions

The first MCP expansion batch now exposes `DiscoverQuests`, `DiscoverSelfQuests`,
`InteractWith`, `Talk`, and `Equip` through authenticated actor routes. Other
real actor methods (farming/crafting/pack actions, economy,
party/expedition, trade, auction, vehicle, and bank actions) remain deferred
in `MCP-ACTION-MATRIX.md` because they do not yet have an authenticated
`/api/actors/*` enqueue endpoint. No fake route, hidden state, or management
alias is exposed.


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

## Registering with an MCP client

Configure the client's stdio server entry using its normal MCP settings. The
command and environment are client-neutral; for example, a generic JSON
configuration is:

```json
{
  "mcpServers": {
    "aaemu_bot_actions": {
      "command": "dotnet",
      "args": ["run", "--project", "/root/aaemu-dev/AAEmu.BotControlMcp", "--no-launch-profile"],
      "env": {
        "AAEMU_BOT_CTRL_URL": "http://127.0.0.1:1280",
        "AAEMU_BOT_CTRL_TOKEN": "<shared secret>"
      }
    }
  }
}
```

Claude, Cursor, Gemini, Codex, and other MCP clients can all spawn this same
stdio process. A published binary is preferable for persistent registrations:

```bash
dotnet publish AAEmu.BotControlMcp -c Release -o /opt/aaemu-bot-actions
```

Point the client's `command` at
`/opt/aaemu-bot-actions/AAEmu.BotControlMcp` while retaining the same
environment variables.

## Bounded live smoke (requires an isolated Game WebApi)

The protocol smoke in `Scripts/mcp-stdio-smoke.sh` is deterministic and does
not require a game server. When an isolated Game WebApi is running with a
registered bot and a token, use this raw stdio sequence as the next live
smoke task:

```bash
export AAEMU_BOT_CTRL_URL=http://127.0.0.1:1280
export AAEMU_BOT_CTRL_TOKEN='<token supplied out-of-band; never commit it>'
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"observe","arguments":{"bot":"McpBot01"}}}' \
  '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"move","arguments":{"bot":"McpBot01","x":15572,"y":15364,"z":126.5,"speed":2,"timeoutSec":20}}}' \
  | dotnet run --project AAEmu.BotControlMcp --no-launch-profile
```

Before the actor call, verify the registered bot through the separate
management MCP (not this sidecar):

```bash
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"bot_list","arguments":{}}}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bot_status","arguments":{}}}' \
  | dotnet run --project AAEmu.BotControl --no-launch-profile
```

Use the `trace_id` from each enqueue acknowledgement for a subsequent
`action_status` call, then query `trace` and assert the changed position with
`observe`. For setup/teardown, use `AAEmu.BotControl`'s separate
`bot_status`/`bot_list` management tools; they are intentionally not exposed
by this sidecar. A live run must record exact JSON-RPC replies and HTTP
status/body evidence, and must stop on a missing state assertion rather than
claiming success.

`Scripts/mcp-integrated-e2e-benchmark.py` runs the same bounded workflow and
also calls `discover_self_quests`, recording its enqueue acknowledgement,
`action_status`, and a follow-up `trace`. It refuses to guess a mutating
doodad target; pass `--safe-doodad-obj-id <objId>` only when an independently
verified safe nearby doodad is available to exercise `interact_with`. The
benchmark's direct bridge observation remains an independent state check.

## Security

- The game-side API is **disabled by default** (`AAEMU_BOT_CTRL=1` or
  `Bots.EnableBotControl` required) — prod never exposes it unless enabled.
- Every HTTP request must carry `X-Auth-Token` matching
  `AAEMU_BOT_CTRL_TOKEN` (env secret). The sidecar adds it automatically
  from its own env.
- Never put the token in shared config files or logs.
