# M2b-E2E — live-server bot harness

> Status: **LIVE** (2026-08-07, branch `feat/m2b-e2e`, E2E-3 verified). Bots roll against a
> REAL Login + Game + MySQL stack over the real network path — true
> end-to-end quest testing on the live world, not a test rig.

## What this is

The M2b pilot proved the quest-drive controller at test-rig level (real
QuestManager + real data, no network). This harness closes the gap: headless
bot sessions over the **real network path** (Login :1237 → world cookie →
Game :1239 → enter world → character create/select → spawn → notify in-game),
driven through the **real quest engine** on the **live world**, with the
pilot's calibrated golden-route manifests as the curriculum.

Every bot is an **ordinary Character record** (AGENTS.md #9) with a **real
GameConnection** that exists only because the real login server authenticated
the account and issued a cookie (AGENTS.md #10). There is **no auth bypass,
no direct session injection, no direct DB writes, no quest-engine bypass**:
every mutation flows through the same surfaces a real client uses
(CharacterQuests.AddQuest, the UnitEvents surface, QuestManager.DoReportEvents
— the exact path CSCompleteQuestContextPacket takes).

## Architecture

```
AAEmu.IntegrationTests/M2bE2eTests.cs   — the E2E runner (xUnit, MTP)
├── E2e/E2eStack.cs          — boot orchestration: MySQL compose + real
│                              Login/Game binaries + config + teardown
├── E2e/BotNetworkSession.cs — real login/enter-world flow over real TCP
│                              (Trion auth → world cookie → X2EnterWorld →
│                              create/select → spawn → notify in-game)
├── E2e/BotTcpLink.cs        — wire-level framing for the 1.2 protocols
├── E2e/E2eQuestDriver.cs    — pilot-calibrated stage drive over the bridge
├── E2e/E2eQuestManifest.cs  — reads the committed scenario manifests (t1/*)
├── E2e/BotDriveClient.cs    — JSON/TCP client for the server-side bridge
AAEmu.Game/Models/Game/Bots/
├── BotDriveBridge.cs        — loopback-only JSON/TCP test-control surface
│                              (config-gated, DISABLED in prod config)
└── BotE2EBridgeBootstrap.cs — assembly-load startup (no-op when disabled)
Scripts/e2e/
├── e2e-stack.sh             — human control surface (db-up/db-down/status/logs)
└── docker-compose.yaml      — MySQL 8 container, same SQL seeds as the
                              repo-root compose (aaemu_login/aaemu_game)
```

## Quick start

```bash
# 1. Provision the canonical game data once (16GB ClientData + Data + configs):
rsync -a root@192.168.0.165:/root/AAEmu/.server_files/AAEmu.Game/ /root/aaemu-e2e/runtime/game-data/

# 2. MySQL (container, SQL-seeded aaemu_login/aaemu_game):
Scripts/e2e/e2e-stack.sh db-up

# 3. Run the harness (publishes Login+Game binaries, boots both servers,
#    drives the golden route, writes the metrics dump):
dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
  --configuration Release --filter-class AAEmu.IntegrationTests.M2bE2eTests
# NOTE: this project's MTP runner does NOT accept --treenode-filter (it
# discovers 0 tests and exits 8) — --filter-class is the only working
# filter. Add `--output Detailed` for per-test results + durations.

# 4. Inspect:
Scripts/e2e/e2e-stack.sh status     # ports + processes + sqlite md5s
Scripts/e2e/e2e-stack.sh logs       # login.log / game.log tails
# metrics dump lands in scorecard-explorations/m2b-e2e-metrics.md
```

Env overrides: `E2E_ROOT` (default `/root/aaemu-e2e`), `E2E_REBUILD=1` to
force re-publish of the server binaries, `E2E_WIRE_DUMP=<path>` to log raw
wire bytes (Trion auth -> world cookie -> enter world) from BotTcpLink.

## What each test proves

| Test | Proves |
|---|---|
| `E2e_Stack_Boot_Deterministic_And_CanonicalBaseline` | deterministic boot: all 3 servers + the bridge on the real ports; runtime compact.sqlite3 is byte-identical to the canonical copy at boot |
| `E2e_GoldenRoute_RealNetworkFlow_Metrics` | N bots × M cycles through the REAL login/enter-world flow; golden-route drive (16 quests); cycle pass rate, state-cleanup (0 leaks), session release on disconnect |
| `E2e_RestartPersistence_TwoCheckpoints_FullStateMatch` | mid-quest state (accepted + progressed, NOT turned in) survives a REAL game-server process restart: MySQL rows persist, reconnect restores byte-identical quest state, the reconnect finishes the quest through the real turn-in path |
| `E2e_SeededDefect_FailBefore_PassAfter` | the rig catches real defects: a seeded data-level quest defect (quest 251 report NPC → non-existent template) FAILS the drive with a repro trace; reverting to the canonical copy (byte-identical) goes green |
| `E2e_CrossCycleIsolation_BaselineByteIdentical` | fresh-bot state is identical across cycles (level/active/completed/inventory) and the runtime sqlite never drifts from canonical |

## Metrics (observed 2026-08-07, E2E-3 verification)

| Metric | Definition | Green bar | Observed |
|---|---|---|---|
| Cycle pass rate | bot × cycle golden-route drives passing (16/16 quests) | 100% | **4/4 cycles** (2 bots × 2 cycles) |
| State-cleanup | leaks over the run: active quests after cycle, missing completed flags, unreleased sessions | 0 leaks | **0** |
| Restart persistence | mid-quest checkpoints surviving a real server restart, byte-identical restore | 2/2 checkpoints | **2/2** (quests 254, 266) |
| Cross-cycle isolation | runtime compact.sqlite3 md5 == canonical md5 | byte-identical | **byte-identical** |
| Seeded-defect rig | fail-before (defect → E2E fails w/ repro trace) then pass-after (revert → green) | fail-before fails, pass-after green | **fail-before FAILED as designed, pass-after green** |

Run evidence: `scorecard-explorations/m2b-e2e-metrics.md` (deterministic —
no wall-clock), `$E2E_ROOT/run26.log` (5/5 green, 9m27s) and
`$E2E_ROOT/run28-detailed.log` (5/5 green, per-test durations) — full-suite
reruns of the same harness, 2026-08-07 (E2E-3).

## How to add quests/zones to the route

1. **Manifest must exist and be calibrated.** The E2E driver reads the
   committed scenario manifests: `AAEmu.UnitTests/Game/Quests/Scenario/Manifests/t1/<questId>.json`.
   If the quest is new to the census, generate its manifest first
   (`python3 tools/quest-scenario/gen-manifests.py <canonical.sqlite3>`), run
   the unit-tier harness until it's PASS, then re-commit the manifest.
2. **Add the quest id to `GoldenRoute`** in `M2bE2eTests.cs`, in playable
   order (respect kind-31 prereq chains — the mount chain 4292→4294→4295
   must stay in order).
3. **Zones:** the route is Solzreed (zone 9/124/125). A new zone needs its
   own route array (chain data from `report-zone.py`), the zone's quest ids
   in manifests (t1 is zone-agnostic — the harness drives any quest whose
   manifest exists), and — if the zone's NPCs/doodads are needed for the
   real turn-in path (`report`/`reportDoodad` resolve **live world objIds**)
   — the zone must be loaded by the game server's world data, which it is by
   default (all zones load at boot).
4. **Keep expectations honest:** a new quest that can't complete in the live
   world (no spawner for its report NPC, unobtainable objective item) will
   fail the drive with a repro trace — that is the harness working as a
   regression canary. File the defect per the regression-harness contract;
   do not fake-pass it.

## Safety & composition rules (locked)

- **Additive only** — the bridge, the session wrapper, and the runner are
  new files; the only touched existing file is `HeadlessSession.cs` (one
  additive property + one factory) and the test csproj (one ProjectReference).
- **No auth bypass** — bots authenticate through the real login server; the
  game server sees ordinary clients.
- **No direct DB writes from the test** — characters are created through
  `CSCreateCharacterPacket` → the server's real create handler; the only DB
  writes the harness makes are teardown (deleting its own bot rows between
  cycles) and the seeded-defect patch (runtime sqlite COPY only).
- **Canonical data is read-only** — `compact.sqlite3` is copied to the
  runtime dir; the seeded-defect rig patches the runtime copy, never the
  canonical file, and restores it to byte-identical before pass-after.
- **Bridge is config-gated** — `"Bots": {"EnableE2EBridge": true}` only in
  the E2E runtime Config.Local.json (or `E2E_BRIDGE_ENABLED=1` env); prod
  config never sets it. Bound to 127.0.0.1 only.
- **Composition rule intact** — no parallel character/inventory/quest/
  economy implementation anywhere in the harness.

## Known limits (watch items)

- Stream (1250) join is fire-and-forget for the quest drive (a failure is
  logged, not fatal).
- The drive fires synthetic gameplay events over the engine's UnitEvents
  surface (the same surface the world pipeline uses) — it does not simulate
  a human walking to the NPC. World reachability of objectives remains a
  playtest concern (per the census contract).
- Reward assertions cover declared SupplyItem items; XP/copper and
  level-based default rewards are not asserted (pilot scope).
- **One stack, one suite at a time (concurrency hazard, 2026-08-07).** The
  rig is single-stack: a concurrent E2E run (e.g. a kanban worker session
  still alive after completing its card, re-driving the stack) races it —
  the bridge's session registry is per-process (GameConnectionTable), so a
  bot in game A is invisible to game B's bridge ("not in the world" on every
  drive), and a concurrent `docker compose down -v` under a live game breaks
  its DB pool (PacketMarshaler NREs at bot connect). Observed: run24 raced
  t_718e1115's still-alive retry session and failed 4/5; run25 (theirs) and
  run26/28 (clean) all green on the same code. Before a run: confirm no
  foreign `dotnet test` / stray AAEmu server processes, and that no other
  session owns the stack.
