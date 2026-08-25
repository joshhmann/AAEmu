# INDUN-01 Domain Dossier (2026-08-24 exploration)

Scorecard row at writing: W=1 Graphify-only, never verified end-to-end.

## Verdict: far beyond a stub — structured implementation exists

- **Core**: `IndunManager` (entry requirements: level/party/ticket/visit-count; per-party `Dungeon` objects on real isolated `WorldInstance`s via `CreateWorldInstance`; queue-during-load; solo→party conversion; kick-on-leave; 24h solo expiry; access-flag reset; in-memory 4h cooldowns).
- **Data**: `indun_zones` (20), `indun_events` (70), `indun_actions` (104), rooms/spheres — loaded via `IndunGameData`. Portal doodad funcs (`EnterInstance/EnterSysInstance/ExitIndun/RemoveInstance`) loaded from client data.
- **Scripting**: generic NPC-killed → door-phase → room-cleared chains cover Nachashgar (55), easy (66), Immortal Isle (62/64), library wings (73-76). **Low-level dungeons (45 Burnt Castle, 46 Hadir Farm, 47 Sal Temple, 50 Deadmine, 51 Howling Abyss, 52 Cradle) have ZERO scripted events** — trash-pull-to-boss with no completion trigger.
- **System instances**: `arche_mall_world(14)` in logs = config-driven AutoCreate (`Configurations/Dungeons.json`), not player-dungeon evidence.
- **Packets**: no dedicated enter packet (rides portal doodad interaction — canonical-correct); `CSInstanceLoadedPacket` functional; `CSUnknownInstancePacket` stub; channel dialog TODO.

## Gaps (why W=1 was fair)
1. No proof a real party can enter/clear/exit — tests cover only tick-subscription.
2. Low-level dungeons lack completion data/hooks entirely.
3. Edges: cooldowns memory-only (lost on restart), `RestoreItemTime` dead code, `DungeonLoaderTask` blocking sleeps, channel-select TODO.

## Recommended proof target
**Hadir Farm (46) or Burnt Castle Armory (45)** — smallest surface, no scripted events needed:
1. Bot-party E2E through the real portal doodad (reuse ProvisionBotParty/PartySpikeScenario seams).
2. Minimal completion hook (final-NPC-killed → complete) via `SQL/patches/compact/` row or engine fallback.
3. Entry-count persistence = optional QoL deviation to document.
Estimated S for one dungeon (entry/party/isolation exist); M with cooldown persistence; L only for Nachashgar-style full phase scripting.

## Bot runnability
Feasible today: party invite/follow/assist + PartySpikeScenario combat are proven; straight-line movement OK for courtyard-style interiors (Hadir Farm), risky for tunnels (Deadmine). Needs a portal-use step (doodad interact contract action or test seam) + room-sphere presence for `AreaClearTick`.
