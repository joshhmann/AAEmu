# SHIPS-01 Slice 1 — Rowboat live-stack E2E verdict (2026-08-25)

Executor: `rowboat-e2e` workstream agent. Stack: isolated E2E_ROOT=/root/aaemu-e2e-boat,
COMPOSE_PROJECT_NAME=boatacc, ports login 2241 / game 2243 / stream 2253 / bridge 2263 /
internal 2235 / webapi 2283 / db 23307. Repo: joshhmann/AAEmu `develop` @ `bfbea4093`
(origin head at run time; dossier was written against `214bed8`). Test:
`AAEmu.IntegrationTests.E2e.RowboatE2eTests` (new, uncommitted in `.worktrees/rowboat`).

## Owner question: "are boats working?"

**NO — clean FAIL with precise diagnosis.** The full player-facing summon chain works over
real wire (stock scroll → real CSStartSkillPacket use-item cast → skill 15802 → SpawnSlave
special effect → SCSlaveCreatedPacket on the bot's own authenticated game link), but the
sailing physics layer is dead end-to-end: the hull spawns ~100 m in mid-air with permanently
zero velocity and **zero SCOneUnitMovementPacket(Ship) frames ever reach the client**.

## Stage table

| # | Stage | Verdict | Evidence |
|---|---|---|---|
| 1a | STOCK summon scroll | PASS | bridge `stock` item 15817 ('나룻배 소환 주문서', `item_summon_slaves`→slave 15); persisted instance id 16777230 via ordinary save |
| 1b | Open-sea positioning | PASS | bridge `teleportToNpc` 13763 ('굶주린 가루다', first registry spawner at open-sea z≈0.05); landed (3090.0, 29778.0, 0.05) via MySQL characters.x/y/z |
| 2 | SUMMON (real item-use path) | PASS | CSStartSkillPacket(skill 15802, SkillCasterType.Item, instance id) → 5 s cast logged (`StartSkill: Id 15802`) → `Special effects: SpawnSlave value1 0…` → `SCSlaveCreatedPacket owner=22011 tl=14 slaveObj=44127 creator='Rowboater'`; MySQL `slaves` row written by `Slave.Save` |
| 2b | Physics stream for slave | **FAIL** | no SCOneUnitMovementPacket(Ship) frame within 20 s window (and none for the whole session) |
| 3 | BIND driver | not reached | blocked by 2b |
| 4 | HELM throttle/steer/displacement/sign-flip | not reached | blocked by 2b |
| 5 | UNBIND (CSDiscardSlave) | not reached | blocked by 2b |
| 6 | DESPAWN + leak check | not reached | blocked by 2b |

## Root-cause evidence (passive diagnostics, since reverted from engine)

Server-side truth from `/root/aaemu-e2e-boat/runtime/game/Logs/Server.log`:

```
21:23:37 [INFO] PhysicsManager - [ship-diag] broadcasting Rowboat obj=44127 pos=(3088.3,29773.3,99.7) vel=(0,0,0)
21:23:53 [INFO] PhysicsManager - [ship-diag] registered=1 slaveBodies=1 registeredInSnapshot=1 worldMismatch=0 portal=0 inactive=0 ticked=1
```

1. The hull IS registered (`AddShip Rowboat -> main_world`, DEBUG line), present in the
   physics snapshot, active (never sleeps: `Hull.DeactivationTime = TimeSpan.MaxValue`),
   past PortalTime, world-matched, and **ticked every loop**; `SendUpdatedMovementData`
   runs and calls `slave.BroadcastPacket(...)` each tick.
2. Yet the replicated position is frozen at `(3088.3, 29773.3, 99.7)` with `vel=(0,0,0)`
   for the entire session — the hull never falls, drifts, or responds.
3. Z ≈ 99.7 is the smoking gun: the boat branch sets spawn height from
   `world.Water.GetWaterSurface(...)`, which found no water body at this open-sea
   coordinate and fell back toward `PhysicsManager.DefaultWaterLevel = 100f`
   (PhysicsManager.cs:35) — i.e. the boat summons 100 m ABOVE the ocean where the
   character itself stands at z = 0.05.
4. Zero `SCOneUnitMovementPacket` frames arrive at the bot despite the broadcast call
   executing per-tick — the replication hop (`WorldManager.GetAround(slave)` receiver
   resolution or packet encode/send on this path) is broken for Slave units. Note
   `PacketLogLevel.Off` on this packet suppresses its own wire logging, which masked
   this failure until the bot-side tap proved silence.

## Layer attribution

- **SERVER (primary):** ship physics↔world integration non-functional on live stack —
  frozen hull (no gravity/buoyancy effect) and no client-visible movement stream.
- **SERVER/DATA (contributing):** water-surface query returns the DefaultWaterLevel(100)
  fallback at genuine ocean coordinates (character floats at z≈0 there), so boat summons
  place hulls ~100 m high. Geodata/water-area loading around (3090, 29778) vs the
  fallback constant needs a follow-up probe.
- **BOT-SIDE:** none — all C2S packets were accepted and acted on by handlers (verified
  by server logs and DB rows). Packet shapes validated against server parsers.

## PB ledger

Confirmed defect → appended as **PB-005** to `scorecard-explorations/playerbot-blockers.md`.

## Reproduce

```bash
cd /root/aaemu-dev/.worktrees/rowboat
E2E_REBUILD=1 \
E2E_ROOT=/root/aaemu-e2e-boat COMPOSE_PROJECT_NAME=boatacc DB_HOST_PORT=23307 \
E2E_LOGIN_PORT=2241 E2E_GAME_PORT=2243 E2E_STREAM_PORT=2253 E2E_BRIDGE_PORT=2263 \
E2E_INTERNAL_PORT=2235 E2E_WEBAPI_PORT=2283 E2E_DB_PORT=23307 \
dotnet test --project AAEmu.IntegrationTests \
  --filter-class AAEmu.IntegrationTests.E2e.RowboatE2eTests
# JSON stage log (overwritten per run): /root/aaemu-e2e-boat/logs/rowboat-e2e-report.json
# Blocker details:                     /root/aaemu-e2e-boat/logs/rowboat-e2e-BLOCKER.md
```

Notes: `--filter-class` needs the fully-qualified class name. Debug-level engine lines go
to `runtime/game/Logs/Server.log` (NLog file target), NOT to the captured stdout
`logs/game.log`; the `AAEMU_E2E_LOG_LEVEL` env knob did not take effect through NLog's
`${environment}` minlevel in practice — patching the runtime NLog.config rule to
`minlevel="Debug"` works. compact.sqlite3 touched read-only throughout; engine code left
pristine (temporary `[ship-diag]` log instrumentation used to obtain §Root-cause evidence
was removed after capture; re-add if the defect is taken up).
