# Soak-Runs (E2E QA / soak / smoke on the testing server)

## Policy

All E2E **soak** and **QA** runs happen on the **testing server
`root@192.168.0.165`** in isolated lanes under `/root/aaemu-e2e-<lane>`. The
local dev host runs soaks for **smoke only** (short, throwaway checks); the
local host must stay free for build/gate work and must not be burned by
day-scale or multi-hour windows.

Each lane is fully isolated: its own E2E root, unique port block + DB port,
own `COMPOSE_PROJECT_NAME`, a fresh repo at the **current HEAD SHA**, and
PID-verified teardown. **pkill is banned.** Prod `/root/AAEmu` (ports
`1237/1239/1250/3306`) and sibling lanes are **never** touched.

## Tiers

| Tier | Window | What runs | Budgets |
|------|--------|-----------|---------|
| `smoke` | <5 min | ONE cheap E2E class (stack boots + live bridge handshake) | no |
| `qa` | <20 min | Named functional E2E class (default B1/B2/B6) | no |
| `soak` | long | Budgeted S-runs (B4 PACK/SLAVE/FARM, Dominion cycle, Village full-day) | asserted by the class |

## How to invoke

```bash
# all on .165, isolated lane, artifacts rsynced back
./Scripts/e2e/run-qa-suite.sh --tier smoke                 # cheap lane smoke
./Scripts/e2e/run-qa-suite.sh --tier qa                     # B-series functional
./Scripts/e2e/run-qa-suite.sh --tier qa --target B6MerchantConservationE2eTests
./Scripts/e2e/run-qa-suite.sh --tier qa --target all       # every non-soak E2E class
./Scripts/e2e/run-qa-suite.sh --tier soak                   # B4 PACK/SLAVE/FARM + Dominion + Village full-day
./Scripts/e2e/run-qa-suite.sh --tier soak --target B4PackSoakE2eTests --minutes 360
./Scripts/e2e/run-qa-suite.sh --tier qa --lane smoke-rb --keep   # debug: skip teardown
```

Args: `--tier {smoke|qa|soak}`, `--target <FQCN|'all'>` (default tier-
appropriate), `--lane <name>` (default from target), `--minutes <n>` window
override, `--keep` skip teardown, `--artifacts <local-dir>`.

## Safety rules (HARD)

- **Never** touch prod `/root/AAEmu` on .165 — source is read-only rsync only.
- **Never** derive the prod ports `1237/1239/1250/3306` (script refuses if a
  lane port collides).
- **Never** touch a sibling lane's ports, DB, or compose project. Lane port
  derivation is deterministic per lane name; ports are checked free via `ss`
  before running (`Refuse to run if occupied`).
- **pkill is banned**: teardown kills only processes whose
  `/proc/<pid>/cwd` is under this lane root, then `docker compose -p <our-project>`
  down only.
- Lane-unique DB ports keep sibling DB volumes disjoint (own compose
  project + `.env`).

## Evidence contract

Every run emits, under the local artifacts path (default
`./soak-artifacts/<lane>/<timestamp>/`):

- the tested **HEAD SHA** (`git-head-sha.txt` — recorded before rsync);
- the **env** block (E2E_ROOT / ports / DB port / compose project / E2E_REBUILD=1 / E2E_GAME_HOST=127.0.0.1);
- canonical game-data **md5** `78b3bdbf038db3b927056106efdf91af` (verified before the run; mismatch = refuse);
- per-class logs, the class's report JSON/MD (already written under the lane's `logs/` by the class), `--minutes` budget override when given, artifact paths;
- a `RUN/PASS/FAIL` summary with artifact path + SHA.

Exit code 0 = all classes passed; otherwise the number of failed classes.

## Cleanup

`--keep` leaves the lane up for debugging (no teardown). Default teardown
(PID-verified, lane-scoped) stops our lane's Login/Game and drops our compose
project + DB volume; the lane directory (`/root/aaemu-e2e-<lane>`) and its
remote logs are left in place so evidence can be re-pulled. Sibling lanes are
never touched.
