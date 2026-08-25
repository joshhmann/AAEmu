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

## Addendum 2026-08-25 — PB-003 canonical verification: exit-portal DATA GAP REFUTED

Canonical re-verification of the "zone 46 has no exit portal data" premise (ruling:
reference data first, sibling inference only as fallback) **refutes the blocker**:

1. **Spawn rows exist** — static doodad spawns are NOT stored in compact.sqlite3 at all
   (by design; AGENTS.md rule 3). They live in `Data/Worlds/<world>/doodad_spawns.json`,
   loaded by `SpawnManager.LoadDoodadSpawns` (`AAEmu.Game/Core/Managers/World/SpawnManager.cs`).
   `Data/Worlds/instance_hadir_farm/doodad_spawns.json` ships BOTH exit portals:
   - UnitId **4289** @ (552.2, 499.8, 133.5) Yaw 89.3565 — VERIFIED in-tree since a667174c1.
   - UnitId **4927** @ (763.3, 709.5, 141.5) Yaw -36.4199, FuncGroupId 12276 — same file.
2. **Templates + funcs verified** in compact.sqlite3:
   - `doodad_almighties` 4289/4927 = '하디르의 농장 출구', model `dungeon.exit_portal_small`.
   - `doodad_func_groups`: 10546 (4289, kind Start) → `doodad_funcs` 12785 =
     `DoodadFuncExitIndun` func_id 12, skill **17733** ('하디르의 농장 퇴장');
     12276 (4927, Start, invisible) → SkillHit → phase 12277 → `doodad_funcs` 12937 =
     ExitIndun func_id 13, skill 17733. ReturnPointId NULL/0 on both → engine path
     `DoodadFuncExitIndun.Use` → `IndunManager.RequestLeaveInstance` → restore
     `MainWorldPosition` (captured at entry). Sibling dungeons follow the identical
   pattern (Burnt Castle 5723, Cradle 5759, Sal Temple 4139/4535 — all static JSON spawns).
3. **Runtime proof**: the indun-party E2E stack's own log
   (`/root/aaemu-e2e/logs/game-restart.log` 22:54:13) reads
   `Spawning 2 Doodads in world 100-instance_hadir_farm(12)` … `Doodads spawned: 2`.
   The prior EXIT-PATH-EVIDENCE detail ("almighty 4289/4927 not spawned") was an
   unprobed assertion, contradicted by that log; only NPCs were enumerated in-scenario.
4. **No SQL patch authored** — there is nothing to insert: compact.sqlite3 has no
   doodad-spawn table, and the canonical spawn store already carries both portals.
   The completion patch `SQL/patches/compact/2026-08-25_indun_hadir_completion.sql`
   (events 4601/4602) remains the only compact overlay zone 46 needs.
5. **PB-003 status**: unchanged (OPEN) pending the party-clear-then-exit E2E below;
   its DATA-layer attribution should be corrected to E2E-coverage when flipped.

**Exact follow-up E2E assertion**: after COMPLETION (bosses 10166+10167 dead), from
inside world instance 100-instance_hadir_farm the leader injects CSStartSkillPacket
skill **17733** against the portal doodad spawned from template **4289**
(group 10546) → expect DoodadFuncExitIndun.Use → RequestLeaveInstance → character
relocated to main_world (world id 0) at the MainWorldPosition captured at entry;
assert via SCSetPositionPacket / position probe ZoneId != 241 && Instance == 0 for
BOTH party members (kick-on-leave semantics for the follower must also hold).

**Addendum 2026-08-25 (same day, later): the assertion above RAN GREEN.** Isolated
stack `pb003acc` (E2E_ROOT=/root/aaemu-e2e-pb003, worktree `.worktrees/pb003-exit`),
test `AAEmu.IntegrationTests.E2e.IndunExitE2eTests` — 11/11 stages PASS:
entry via skill 17731 → bosses 10166+10167 dead → completion events 4601/4602
fired → each member cast skill 17733 at live exit-portal doodad 4289 →
SCLoadInstancePacket(world 0, zone 179) → bridge charPos confirms main_world at
the exact pre-entry anchor (15578.0, 15382.1), instance 0, both members.
Report: `/root/aaemu-e2e-pb003/logs/indun-exit-e2e-report.json`. PB-003 flipped to
FIXED in playerbot-blockers.md with layer correction DATA → E2E-coverage.
