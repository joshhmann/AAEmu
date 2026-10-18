# Working with Config Files and Server Listings

- Audience: Contributors, players, and testers
- Last verified against: `develop` on August 5, 2026
- Prerequisites: Basic JSON editing and AAEmu project structure familiarity

## Overview

AAEmu now stores login server listings in configuration (`GameServers`) instead
of MySQL `aaemu_login.game_servers`.

This allows consistent behavior across manual, Docker, and Aspire workflows.

## Login server `Config.json` and `Config.Local.json`

The login server reads configuration from JSON files and environment variables.
Use `Config.Local.json` for machine-specific overrides.

### `GameServers` schema

`GameServers` is a list of server entries with this shape:

```json
{
  "GameServers": [
    {
      "Id": 1,
      "Name": "AAEmu.Game",
      "Host": "127.0.0.1",
      "Port": 1239,
      "Hidden": false
    }
  ]
}
```

Field meanings:

- `Id`: unique game server id.
- `Name`: display name in server selection.
- `Host`: address reachable by clients.
- `Port`: client connection port.
- `Hidden`: whether to hide this entry from listing.

### Environment variable mapping (login)

You can define server entries via environment variables:

```text
GameServers__0__ID=1
GameServers__0__Name=AAEmu.Game
GameServers__0__Host=127.0.0.1
GameServers__0__Port=1239
GameServers__0__Hidden=false
```

## Game server configuration and precedence

The game server supports `Config.Local.json` as the final override layer.

Effective load order:

1. `AAEmu.Game/Config.json`
1. `AAEmu.Game/Configurations/*.json` (all matching files)
1. `AAEmu.Game/Config.Local.json` (loaded last)

If the same setting exists in multiple places, `Config.Local.json` wins.

## `game_pak` configuration

Set `game_pak` source in one of these places:

- `AAEmu.Game/Configurations/ClientData.json`, or
- `AAEmu.Game/Config.Local.json` for local override.

For contributor workflows, prefer `Config.Local.json`.

## Bot behavior configuration (all OFF by default)

Bot features gate through the `"Bots"` block in `Config.json` /
`Config.Local.json`, with env-var overrides (env wins). None of these are
set in prod config unless explicitly desired.

| Key / env | Effect |
|-----------|--------|
| `"Bots": { "EnableE2EBridge": true }` / `E2E_BRIDGE_ENABLED` | Test-control TCP bridge on 127.0.0.1:1260 — test environments ONLY, never prod |
| `"Bots": { "EnableChatter": true }` / `AAEMU_BOT_CHATTER_ENABLED` | Proximity greetings + canned bot chatter (per-bot/pair cooldowns, zone budget, combat-suppressed) |
| `"Bots": { "EnableSchedules": true }` / `AAEMU_BOT_SCHEDULES_ENABLED` | Game-clock daily schedules (Home/Work/Travel/Rest) for persistent bots |
| `"Bots": { "PresenceManifest": "<path>" }` / `AAEMU_PRESENCE_MANIFEST` | Data-driven citizen roster (JSON); unset = legacy hardcoded demo citizens |
| `"Bots": { "MaxPresenceBots": N }` / `AAEMU_PRESENCE_MAX_BOTS` | Presence bot clamp (default 10) |
| `"Bots": { "EnableStartupArming": true }` / `AAEMU_STARTUP_ARMING` | The ONE supported way to arm the bot host at boot; absent or `false` = nothing arms (shipped default) |
| `"Bots": { "StartupBotCount": 2 }` / `AAEMU_STARTUP_BOT_COUNT` | Size of the citizen roster the startup switch enrols (default 2, clamped); a re-arm or a restart adopts the same bots by name instead of provisioning duplicates |
| `AAEMU_QUEST_BOOTSTRAP_ENABLED=1` | Opts into the existing quest activity module; does not globally enable bots or assign a quest |
| `AAEMU_PRESENCE_IDLE_WAKE=1` | Enables the existing idle self-wake after enrollment; does not add a discovery scheduler or supply work |

Every arming path — presence demo, the manifest roster, the proximity
bootstrap, admin roam/lab and the E2E bridge — arms the same scheduler owner
(`IPlayerBotScheduler.Arm`), and host shutdown disarms through that same owner;
the startup switch above is the supported deployment front for that one path.
The switch reports what its enrollment actually did (`enrolled N bot(s) of a
roster of up to M`), so an enrollment that found the world already populated
reads as `nothing enrolled (idempotent)` instead of as a roster that came up;
and when a `PresenceManifest` is configured as well, the manifest's roster is
what gets enrolled — `StartupBotCount` then only sets the idempotency threshold
and the roster size the line reports.

Related env-only knobs: `AAEMU_PRESENCE_HOME_X/Y/Z` (demo patrol home
override), `AAEMU_BOT_CHATTER_RADIUS`, `AAEMU_BOT_CHATTER_ZONE_BUDGET`,
`AAEMU_BOT_SCHEDULE_SCAN_SECONDS`.

### Opt-in quest discovery and progress (E-91 checkpoint)

With the quest module opted in, a healthy bot with valid coverage and a genuinely
empty local listing can elect `quest.discovery`. This is exploratory movement
through the existing route/actor/cadence; chosen quest stays zero. Distant spawner
knowledge is not a live NPC or eligible quest. Busy/unknown perception is not an
empty listing. Once ordinary local observation exposes legal work, the same owner
selects a quest and the existing director/runtime executes `quest.progress`.
Running requests, survival, generation changes and death retain their existing
authority; recovery creates fresh requests instead of resuming interrupted ones.

Main reviewed a bounded LIVE330→6198 completion→251 selection run, including
one declared trace-specific Move cancel and ordinary durable rewards. Setup was
natural spawn, level10/starter items, one enrollment wake and explicit opt-in
flags—not a globally enabled default or an unseeded leveling proof. NPC-report
quests do not prove kill/gather, restart, population or human acceptance.
The measured full gate remains RED with 16 unchanged failed names; independent
control/MCP/cycle/report checks passed separately. Not merge-ready; grades and
the standing freeze are unchanged.
See [current status](../../STATUS.md) and
[ownership/evidence scope](../../.omp/state/artifacts/astra-autonomy-2026-10-09/architecture-map.md).

## Migration note from old setup docs

Older instructions referenced `aaemu_login.game_servers` and SQL inserts.
That is no longer the source of truth for server listings.

Use login `GameServers` configuration instead.

## Related

- [Installation & Setup](Installation-&-Setup)
- [Aspire Development Guide](Aspire-Development-Guide)
- [Mini troubleshoot guide](Mini-troubleshoot-guide)
- [FAQ](FAQ)
