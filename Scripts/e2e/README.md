# M2b-E2E boot orchestration (E2E-1)

Deterministic Login + Game + MySQL boot/reset for the E2E bot harness. Scripts
only — no game code. The stack runs the **real repo binaries** with the real
config precedence and the same SQL seeds as prod; nothing is stubbed.

## Files

| File | Purpose |
|------|---------|
| `e2e-boot.sh`   | ONE COMMAND: MySQL + Login + Game up with canonical data |
| `e2e-reset.sh`  | teardown + clean re-boot + byte-identical baseline proof (exit 0 only when proven) |
| `e2e-stack.sh`  | helpers: `status` \| `logs` \| `db-up` \| `db-down` \| `db-reset` |
| `e2e-common.sh` | shared boot phases + helpers (sourced, not executed) |
| `docker-compose.yaml` | MySQL 8.0.36 only (servers run as host processes so E2E-3 can restart them at process level) |

## Usage

```bash
cd /root/aaemu-dev/Scripts/e2e

./e2e-boot.sh                 # boot the full stack (idempotent; adopts an already-running stack)
./e2e-boot.sh --provision-data  # first-time: rsync canonical game data from the aaemu box, then boot
E2E_REBUILD=1 ./e2e-boot.sh   # force re-publish of Login/Game binaries from the repo build

./e2e-reset.sh                # teardown + clean re-boot + baseline proof (cycle isolation)
./e2e-stack.sh status         # ports + processes + sqlite md5s
E2E_ROOT=/path ./e2e-boot.sh  # override stack root (default /root/aaemu-e2e)
```

`E2E_BRIDGE=1` opts into the BotDriveBridge (port 1260) — **E2E-2's flag**, off
by default. Boot orchestration works against the base repo build without it.

## Port table

| Port | Owner | Notes |
|------|-------|-------|
| 3306 | e2e MySQL container (`e2e-db-1`) | bound 127.0.0.1 only |
| 1234 | Login (internal, login<->game) | |
| 1237 | Login (client-facing) | launcher default |
| 1239 | Game | |
| 1250 | Game stream | |
| 1260 | BotDriveBridge (E2E-2) | only when `E2E_BRIDGE=1` |
| 1280 | Game WebApi | bound by the game at boot (upstream behavior) |

Foreign port holders hard-fail with the offending pids; the stack's own
processes are adopted. `docker compose ps` **lies** about exit codes (see the
aaemu-server skill) — port checks here are `/dev/tcp` probes + `/proc` owner
verification, not compose state.

## Required build steps / prerequisites

1. .NET 10 SDK on the dev box (openclaw): `dotnet --version` → 10.x
2. Canonical game data, once: `./e2e-boot.sh --provision-data` (rsyncs
   `root@192.168.0.165:/root/AAEmu/.server_files/AAEmu.Game/` → `$E2E_ROOT/runtime/game-data/`).
   This is the ~114MB `compact.sqlite3` + the 24GB `game_pak` (symlinked, not
   duplicated) + Configurations.
3. Docker with compose v2 for the MySQL container.
4. First boot publishes Login + Game (Release) into `$E2E_ROOT/runtime/`;
   afterwards binaries are reused until `E2E_REBUILD=1`.
5. Post-publish log-cap guard: every prepare re-runs
   `$E2E_ROOT/ensure-log-caps.sh` so a publish (which copies `NLog.config`
   from the still-uncapped repo tree) can never clobber the capped runtime
   configs (Sequence/25MB/20, Info default, TRACE via `AAEMU_E2E_LOG_LEVEL`).
   Same guard is wired into `E2eStack.EnsureServerBinaries` (t_a54574e9).

## Cycle isolation contract (what reset proves)

1. Teardown: Login + Game stopped; MySQL volume wiped (`down -v`).
2. Runtime `compact.sqlite3` restored byte-identical from the canonical copy.
3. Clean re-boot: MySQL re-seeded from `SQL/aaemu_login.sql` +
   `SQL/aaemu_game.sql`, then login, then game.
4. Proof:
   - **MySQL seed baseline**: dump hash taken at SEED state (before any server
     starts) must equal the stored first-cycle baseline. The game writes a
     runtime `accounts` row (account_id 0, `DateTime.UtcNow`) at boot, so a
     POST-boot dump is *never* byte-identical — the seed state is the canonical
     MySQL baseline; server runtime writes are expected and excluded by
     construction.
   - **sqlite baseline**: runtime md5 == canonical md5, verified again AFTER
     boot (the game only reads `compact.sqlite3`).

## Pitfalls learned (2026-08)

- **`mysqladmin ping` is not a readiness check.** The MySQL entrypoint answers
  ping from its temp init server; Login then connects into the seed window and
  dies (`Reading from the stream has failed` / EndOfStreamException). Wait on a
  real data query over TCP (`SELECT COUNT(*) FROM aaemu_login.users`) instead —
  it only passes once the seed is applied on the real server. Same for the
  compose healthcheck (it drives the `healthy` flag).
- **`mysqldump` needs credentials** (`-e MYSQL_PWD=` via compose exec, never on
  argv) or it exits 1045 and `set -e` kills the whole script mid-reset.
- **`--skip-dump-date`** is required for byte-stable dump hashes.
- The E2E test runner (`AAEmu.IntegrationTests/E2e/E2eStack.cs`) shares the
  layout with these scripts — if one changes the runtime layout or config
  generation, keep the other in sync (search `GameLocalConfig`/`LoginLocalConfig`).
  NOTE for E2E-3: E2eStack's own DB wait is ping-based and has the same seed
  race — fix it there before relying on it.
- `.env` holds the MySQL root password (generated once, `chmod 600`); it lives
  in `$E2E_ROOT`, never committed. If you delete `.env`, the next boot
  regenerates it and wipes the stale volume.

## No secrets

The committed files contain no credentials: `DB_PASSWORD` is read from
`$E2E_ROOT/.env` (runtime-generated) or injected via `docker compose --env-file`.
