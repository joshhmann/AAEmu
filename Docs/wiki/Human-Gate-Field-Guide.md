# Human-Gate Field Guide (H evidence)

- Audience: Josh (owner/operator of every H gate) and the contributors supporting a human run
- Last verified against: `develop` on 2026-09-12 (`c7351b242e4088e0db5ef7926659a948312cb2fb`); canonical `compact.sqlite3` md5 `78b3bdbf038db3b927056106efdf91af`
- Prerequisites: SSH access to the testing server `root@192.168.0.165`, and a client + launcher — Josh's desktop is already set up; the play surface is **the .165 test server** (see [§3](#3-play-surface-the-165-test-server))
- Execution order: run sections in order. Do not start a worksheet row before §2 preflight passes.

This page is the operator document for **this human-gate wave** (not a project-wide
M1→M10 execution barrier): exact commands, the milestone-by-milestone scenarios, the
restart checkpoints, the evidence files to attach, and the rule for choosing the next
implementation slice from the results. Sequencing and preflight below apply to this
wave; unrelated bot/engineering lanes proceed under their own prerequisites. It adds
**no new gate and no new automation** — `Scripts/e2e/run-qa-suite.sh`
and `Docs/wiki/Soak-Runs.md` stay the source of truth for lane automation and
isolation, and `ROADMAP.md` / `SCORECARD.md` stay the source of truth for
contracts and grading.

## 0. The rules that make a result an H result

Copy these; they are the whole point of the wave.

- H means **an actual player completing the curated scenario** — "`H` means an
  ACTUAL PLAYER completing the curated scenario — never a bot or scripted
  actor" (`SCORECARD.md:42-44`).
- Scripted/bot evidence is recorded under **A (automated)** with an explicit
  proxy/bot-functional label and "is NEVER recorded as `H=2`"
  (`SCORECARD.md:44-46`).
- "`H` stays `U` (UNKNOWN) until Josh runs the curated scenario"
  (`SCORECARD.md:46-47`).
- Grades are `U` unassessed, `0` absent/broken, `1` partial or proxy, `2`
  verified for the named curated scope, `N/A` **only with a written reason**;
  never averaged; the weakest required dimension wins (`SCORECARD.md:35-40`).
- Ledger distinction: "H UNKNOWN (no verdict attempted) vs DEFERRED (recorded
  Josh-owned decision) kept distinct" (`ROADMAP.md:36`).
- Closure ceiling: "A milestone with any DEFERRED or UNKNOWN class may close at
  most CLOSED-WITH-CAVEATS — never CONFIRMED-CLOSED." (`ROADMAP.md:664-667`)
- Verdict vocabulary per gate: **PASS / FAIL / CAVEAT**, same convention as
  `Docs/JOSH-QAT-WAVE4.md` §0. "Nothing here claims a verdict" until Josh runs it.

What therefore must **never** be entered in this worksheet's H column: a
`gate.sh` run, an archaeology cycle, a QA/soak lane PASS, a bot regression
report, a kill-9 restart E2E, or an automated M8 acceptance artifact. Those are
A/C/R/S evidence and go in their own columns.

## 1. Execution order

| # | Step | Section | Blocking output |
|---|---|---|---|
| P | Preflight: SHA / canonical md5 / assets / lane + prod safety | [§2](#2-preflight-p0p5) | preflight record with SHA + md5 + inventory verdict |
| 1 | Local automation checks (gate, archaeology, isolated lane) | [§4](#4-local-automation-checks) | artifact directory + `summary.txt` |
| 2 | Play surface: the .165 test server | [§3](#3-play-surface-the-165-test-server) | deployed, SHA-stamped, 1237/1239/1250 reachable |
| 3 | Milestone worksheets M1 → M8 | [§5](#5-human-gate-worksheet) | one completed row per gate |
| 4 | Restart / recovery checkpoints | [§6](#6-restart-and-recovery-checkpoints) | per-checkpoint observed state |
| 5 | Evidence capture + results protocol | [§7](#7-evidence-capture) / [§8](#8-results-protocol-and-next-course-rule) | worksheet + SHA + lane returned to the project |

## 2. Preflight (P0–P5)

Run from `/root/aaemu-dev` on the **dev host**. Record the actual output of each
step; do not summarize.

**P0 — pin the tested revision.**

```bash
git rev-parse HEAD          # tested SHA (record verbatim)
git status --porcelain      # uncommitted changes = record and stop if under AAEmu.*/
git rev-parse --abbrev-ref HEAD   # expect develop
```

**P1 — canonical reference data.** The value is a hard gate: a mismatch refuses
the lane instead of silently testing other data.

```bash
md5sum AAEmu.Game/Data/compact.sqlite3
# expect 78b3bdbf038db3b927056106efdf91af
```

**P2 — client / launcher inventory.** Do this before any download; the script
never fetches multi-GB content and skips what is present.

```bash
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh
# optional, GitHub-only, safe to auto-fetch:
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh --fetch-launcher
```

Pass requires all three `[OK]`: client (`game_pak` + `bin32/archeage.exe`),
`compact.sqlite3`, launcher (`AAEmu.Launcher.exe`). A missing client or launcher
**stops the human wave at preflight** — record it and do not substitute a bot or
scripted run for an H gate.

**P3 — lane choice and port safety.** A lane is fully isolated: own root
`/root/aaemu-e2e-<lane>`, own port block, own DB port, own compose project, PID-verified
teardown, and **pkill is banned** (`Docs/wiki/Soak-Runs.md`, "Safety rules (HARD)").
Derivation is deterministic from the lane name
(`Scripts/e2e/run-qa-suite.sh:235-246`):

```text
L_CRC      = cksum(lane)              # lowercased, [^a-z0-9_-] → '-'
APP_BASE   = 20000 + (L_CRC % 10000)
login      = APP_BASE        login_int = APP_BASE
game       = APP_BASE + 5    stream    = APP_BASE + 13
bridge     = APP_BASE + 23   webapi    = APP_BASE + 43
db         = 40000 + (L_CRC % 20000)
```

Protected production ports that a lane may never derive:
`1234 1237 1239 1250 1260 1280 3306` (`Scripts/e2e/run-qa-suite.sh:53`).

**P4 — production untouched.** Read-only confirmation, both hosts.

```bash
ssh root@192.168.0.165 'ss -ltn | grep -E ":(1237|1239|1250|3306)\b"'
ssh root@192.168.0.165 'docker ps --format "{{.Names}} {{.Status}}"'
```

Record that `/root/AAEmu` on .165 is **not modified** — source is read-only
rsync only, and no lane may reuse its ports.

**P5 — confirm the run host split.** The launcher runs on the dev host side
(Windows client host); only the isolated lane runs remotely. `run-qa-suite.sh`
**must be launched from the dev-host checkout** and refuses to run on .165
(`Scripts/e2e/run-qa-suite.sh:56-65`).

### Preflight record — executed 2026-09-12 (this session)

| Item | Observed |
|---|---|
| Tested SHA | `c7351b242e4088e0db5ef7926659a948312cb2fb` (develop) — **SHA alone is not sufficient provenance: the working tree was dirty** |
| Dirty tree (tracked) | 7 modified files, all run artifacts: `scorecard-explorations/generated/{bot-template-rig.md, leveling-loop-2026-08-25.jsonl, m1m2-replay-rig.md, m5.3-core-surface-exit.jsonl, m5.3-core-surface-exit.md, m7-adventurer-spike.jsonl, m7-adventurer-spike.md}` |
| Dirty tree (untracked) | **`AAEmu.IntegrationTests/E2e/B4FarmSoakE2eTests.cs`** (806 lines; never committed — `git log --all` is empty for that path; the csproj glob compiles it, so it is present in every build/lane/soak below), plus non-source notes (`ROADMAP_SCORECARD_SUMMARY.md`, `UPSTREAM_BRANCHES_AND_50_ARCHAEOLOGY_FINDINGS.md`, `V5_NAVMESH_AND_LOCALIZATION_ANALYSIS.md`, `aaemu-dev-docs.zip`, `soak-m8v6.console.log`, `.worktrees/`) and this guide's own files |
| Canonical md5 | `78b3bdbf038db3b927056106efdf91af` (unchanged) |
| Client inventory (dev host) | **`[MISSING] client`** and **`[MISSING] launcher`** — expected: the client lives on Josh's desktop, which is already set up (see §3) |
| .165 test server at preflight | `1237`, `1239`, `1250` listening, `aaemu-*` containers healthy — tree was at `8a4721775` (Sep 6), 47 runtime commits behind `c7351b242` |
| Verdict | Automated steps below ran green; §3 records the subsequent deployment of `c7351b242` to .165 |

**Provenance caveat (applies to every automated result in §4).**
`Scripts/e2e/run-qa-suite.sh` rsyncs the **working tree**, not a git checkout: it
copies `$REPO_ROOT/` with `--delete` and only excludes `bin/`, `obj/`,
`TestResults/`, `.server_files`, `.client_files`, `.vs/`, `.worktrees`,
`.git/worktrees`, `aaemu-dev-docs.zip`, `.publish-stamp`
(`Scripts/e2e/run-qa-suite.sh:347-367`). Uncommitted and **untracked** tracked-glob
sources are therefore copied in — verified, not inferred: the untracked
`AAEmu.IntegrationTests/E2e/B4FarmSoakE2eTests.cs` is present byte-identical
(md5 `2f21a4a20c0844c324d7a24b2ae826ce`) in the dev-host tree and in **both**
lane checkouts (`/root/aaemu-e2e-m8v6-soak/repo/…` and
`/root/aaemu-e2e-human-gate-preflight/repo/…`) on .165.

Consequences for evidence:
- A class report's `source_revision` names the SHA, yet the farm class source is
  **absent from that revision** (`git log --all` empty for that path). The SHA
  alone is not complete provenance.
- Reproduce the farm soak only from a tree that also carries that file: commit
  it, or record `sha256`/`md5` of the file next to the run SHA.
- For the human wave, resolve this before playing: play from a **clean** tree at
  a recorded SHA, or attach the untracked file's hash to the record. Never
  present `41906276b`/`c7351b242` alone as complete provenance.

Do **not** clean, stash, or delete these pre-existing unrelated files to make a
run "clean" — that is the owner's tree, not the run's. Record, don't remove.

## 3. Play surface — the .165 test server

**Status (2026-09-12): DEPLOYED; server-side ready — human world-entry PENDING.**
`root@192.168.0.165` is the shared **test server** (it also carries the small
presence demo); the dev host offloads work to it. Josh's client + launcher are
already set up on his desktop, so the play surface is the server, not a local
stack.

**Do not read "server-side ready" as "verified playable."** Everything below is
server-side and protocol-level evidence: ports listen, the GameServer registers,
the stack is healthy. **No launcher login, world-list selection, or in-world
entry was performed as part of that deployment-verification session.** Separately, Josh reports informal play far enough to run trade packs to build a home (report 2026-09-12; play SHA/date/setup unknown — positive context, not a curated PASS, not post-deploy login evidence). §3 is closed only when Josh logs in — record the
first successful login (character visible in-world) as the §3 completion
evidence, and keep H = `U` until the worksheet scenarios are actually run.

### 3.1 What the client actually reaches

| Fact | Value |
|---|---|
| Login domain | `archeage.asslorde.com` (launcher `serverIPAddress`) |
| Resolution | **75.3.243.94 = .165's public IP** (split-horizon: LAN → .165 via Unbound, WAN → public) |
| Login port | **1237** — the launcher has no port field, so **the server must serve login on 1237**; lane ports (27xxx) are not launcher-targetable |
| Game / stream | **1239** / **1250**, forwarded on OPNsense |
| Advertised game host | Login `GameServers__0__Host` = `archeage.asslorde.com`, port `1239` (compose env; `Config.json` keeps `GameServers: []`), logged as `Game Server 1: AAEmu.Game -> 75.3.243.94:1239` |
| Launcher settings | `settings.aelcf`: `pathToGame` → client `bin32/archeage.exe`, `serverIPAddress` → `archeage.asslorde.com`, `loginType` → `trino_1_2` (`.agents/skills/aaemu-setup/REFERENCE.md:64-68`) |
| Accounts | `AutoAccount: true` — first login creates the account |

Reachability verified 2026-09-12: 1237/1239/1250 all connect from the dev host on
LAN, and `75.3.243.94:1237` answers on the WAN path the launcher uses.

### 3.2 Deployment performed (2026-09-12)

Target revision: **`c7351b242e4088e0db5ef7926659a948312cb2fb`** (= `develop` =
`origin/develop`; runtime-identical to the v6-soaked `41906276b`, its only child
being the docs commit).

| Step | Evidence |
|---|---|
| Rollback snapshots FIRST (thinpool/GC can eat old layers) | `aaemu-game:presence-demo-rollback-pre-c7351b242` (was `c9f0a49ae9ee`), `aaemu-login:rollback-pre-c7351b242` (was `0d240ed11035`) |
| Source fast-forwarded | `git fetch fork` then `git merge --ff-only c7351b242` → `git rev-parse HEAD` = `c7351b242…` |
| Pre-build guards | glibc runtime `mcr.microsoft.com/dotnet/runtime:10.0` present (BUG-001 musl guard = 1); log caps intact (game/login 50m×3, db/adminer 25m×3); E2E bridge OFF (no `E2E_BRIDGE_ENABLED`, no `EnableE2EBridge`, 1260 closed); **no new `SQL/` updates** in the delta |
| Images built | `docker compose --env-file /root/AAEmu/.env -p aaemu -f docker-compose.yaml build login game` → game `sha256:e028cbecdb1863…`, login `sha256:6ebf1677a366…` |
| Pinned tag | `docker tag aaemu-game:0.3.0.0-alpha aaemu-game:presence-demo` (overlay pins that name; skipping this silently keeps the OLD image) → `presence-demo` now `sha256:e028cbecdb1863…` |
| Both services recreated | `… -f docker-compose.yaml -f docker-compose.presence.yaml up -d --no-deps --force-recreate login game` |

**Which image carries the fix (attribution corrected).** `a5b7cafb1` has the
subject `fix(login)`, but its *files* are
`AAEmu.Game/Core/Network/Login/LoginNetwork.cs` and `LoginProtocolHandler.cs` —
that is **Game**'s link to Login, so the guard ships in the **game** image. There
is **no `AAEmu.Login` source change** in the deploy delta
(`git log 8a4721775..c7351b242 -- AAEmu.Login` is empty). The `aaemu-login` image
was rebuilt anyway so the pair is not mixed-revision, but do **not** describe the
Login image as carrying this fix.

**Guard verification (defensible form).** Two independent checks: (1) the pinned
checkout `git show c7351b242:AAEmu.Game/Core/Network/Login/LoginNetwork.cs`
contains the guard (`_stopping`/`IsStopping` present, 4 matches), and (2) the game
image was rebuilt from that exact checkout and tagged
`sha256:e028cbecdb1863…`. Note an earlier probe that extracted the DLL and grepped
for the guard string returned 0 and was **method-invalid** — .NET stores literals
as UTF-16, so an ASCII grep is a false negative; the remote image also lacks
`strings`/`python3`. Do not cite that probe as verification either way; rely on
source-at-pinned-SHA plus build provenance, or an encoding-aware probe with a
known-present control string.

### 3.3 Post-deploy health (observed)

| Signal | Value |
|---|---|
| Containers | `aaemu-game-1` healthy, `aaemu-login-1` healthy (recreated 19:12Z) |
| GameServer registration | `GameController - Registered GameServer GameServerId { Value = 1 } (AAEmu.Game)` at 19:13:04; game side `Successfully registered on LoginServer` |
| Boot | `GameService - Server started! Took 00:00:53.6`; QuestSanity OK |
| Ports | 1237 / 1239 / 1250 listening; MySQL 3306 loopback only |
| Errors | **0 `[ERROR]`**, 0 FATAL since boot |
| Presence bots | 250 provisioned, adopting `presence.roam` continuously |
| CPU (steady) | **~30–34% of one core** (3× 10 s jiffy samples), on a 4-core / 16 GB box; RAM ~6.5 GB used |
| Tick/physics budget warnings | **7 boot-window + 1 isolated post-warmup.** Boot window 19:13:03–19:13:27Z: one 1513 ms, one 649 ms, one 397 ms region tick, two 417 ms physics. Then **zero for 2+ minutes**, then **one isolated 117 ms region tick at 19:15:47Z** (over the 100 ms budget, `deferred 0 characters`). The 117 ms event is real and post-warmup — recorded, not smoothed over |
| MySQL (play-surface dependency) | `playerbot_metadata` 250 rows, `playerbot_audit` 0 rows, `aaemu_login.users` 253 — tables self-healed on boot as expected |

Reading the CPU numbers correctly: `ps %CPU` is a **lifetime average** and read
~79–89% right after boot; per-window jiffy sampling showed ~33% steady-state.
Do not quote `ps` for load claims.

**Playability caveat (unresolved, recorded not hidden).** The presence demo runs
`AAEMU_PRESENCE_HUNT=1`, 250 bots, home `(14460, 14400, 109)`, radius 45, in
**zone 0 (main world)**. Whether that overlaps Josh's **Solzreed** (zones 9/124/125)
quest-mob fields is **not proven** — a coordinate/spawn-table check was attempted
and abandoned as inconclusive, and the canonical route for such a question is the
read-only archaeology MCP, not ad-hoc SQLite probing. Treat bot competition for
quest mobs as **UNVERIFIED**; if the M1 gate shows mobs already dead or contested,
record it as a FAIL with coordinates and hand it to the owner rather than
changing the presence profile mid-wave.

### 3.4 Operators: do not disturb mid-wave

The deployed login has `Network.NumConnections: 10` and `AutoAccount: true`.
While Josh is playing, avoid restarting the `game`/`login` containers, running
concurrent heavy lanes on .165, or recreating the stack — that invalidates the
session and the worksheet row.

### 3.5 Deploy command (for any future revision)

Follow `Docs/wiki/Docker-Installation-Guide.md` § "Production redeploy (CT 133)"
— the plain `docker-update-local.sh` is explicitly **not** valid there (it drops
`-p aaemu`, `--env-file`, the presence overlay, and the rollback snapshot). The
recipe is: snapshot rollback tags FIRST → `git merge --ff-only <SHA>` → verify
the glibc guard + log caps + bridge-off → `build` → retag to
`aaemu-game:presence-demo` → `up -d --no-deps --force-recreate login game` →
re-run the §3.3 health checks.

Rollback: retag the snapshot back to `aaemu-game:presence-demo` (and
`aaemu-login:0.3.0.0-alpha`) and force-recreate `login game`.

## 4. Local automation checks

Run these before asking Josh to play, so a human FAIL is attributable to the
game and not to a broken build. Record SHA, counts, skips, smokes, and md5.

```bash
# Tier 1 — fast gate (Release build + script compiler + full unit suite + 2 MCP smokes)
./scripts/gate.sh
./scripts/gate.sh HalcyonaSkirmishRigTests     # targeted class filter, same runner

# archaeology MCP cycle — run ALONGSIDE gate for any change that invoked archaeology
./scripts/archaeology-cycle.sh

# Tier 2 — isolated .165 lanes (launched from the DEV HOST)
./Scripts/e2e/run-qa-suite.sh --tier smoke
./Scripts/e2e/run-qa-suite.sh --tier qa                                  # B1 housing / B2 farm / B6 merchant
./Scripts/e2e/run-qa-suite.sh --tier qa --target B6MerchantConservationE2eTests
./Scripts/e2e/run-qa-suite.sh --tier qa --target all                     # every non-soak E2E class
./Scripts/e2e/run-qa-suite.sh --tier soak                                # B4 PACK/SLAVE/FARM + Dominion + Village full-day
./Scripts/e2e/run-qa-suite.sh --tier soak --target B4PackSoakE2eTests --minutes 360
./Scripts/e2e/run-qa-suite.sh --tier smoke --lane human-gate-preflight   # named lane (this wave's convention)
```

Args: `--tier {smoke|qa|soak}`, `--target <FQCN|ClassSimpleName|all>`,
`--lane <name>`, `--minutes <n>`, `--keep` (debug: skip teardown), `--artifacts <local-dir>`.
Defaults per tier and the full class arrays are in
`Scripts/e2e/run-qa-suite.sh:97-167`. `--minutes >= 360` additionally arms
`A5_TIER3_SIX_HOUR=1`.

**Artifacts** (local, per run): `soak-artifacts/<lane>/<timestamp>/`
(`ARTIFACTS_BASE` override available) containing at least

| File | Content |
|---|---|
| `git-head-sha.txt` | tested SHA, written after the run |
| `summary.txt` | `PASS_CNT FAIL_CNT HEAD_SHA ARTIFACTS` |
| `qa-class-<Class>-<ts>.log` / `.list-tests.log` | per-class run log + discovery listing |
| class report JSON/MD | e.g. `b4-pack-soak-report.json`, `b4-farm-soak-report.json`, `b4-slave-soak-report.json`, `dominion-soak-cycle-report.json`, `village-fullday-restart-report.json`, `rowboat-e2e-report.json` |
| `game.log`, `login.log` | lane server logs |

Preserve for any result you cite: **SHA**, lane name, derived ports, per-class
verdict, the class report JSON, and the md5 line from the lane console
(`== game-data md5 verified (78b3bdbf…)`).

### Recorded 2026-09-12 (this session)

| Check | Result |
|---|---|
| `./scripts/gate.sh` (run 1) | **RED — do not call this build green**: 3082 total / 3080 passed / **1 failed** / 1 skipped, exit 2; fail `Skirmish_PeaceZone_CountsKillsButAwardsNoHonor` (17 ms); skip `Provision_Activate_Persist_Deactivate_RoundTrip`; build 0 errors; ScriptCompiler 0/0; MCP smokes skipped by gate design on RC≠0. Full log preserved: `soak-artifacts/human-gate-preflight/20260912-115131/aaemu-gate-tests.apD05p.log` |
| `./scripts/gate.sh HalcyonaSkirmishRigTests` | GREEN 4/4 — the failed class passes in isolation, matching the known cross-class singleton-race flake family (Q1). Classification: regression **not** established; flake profile matched, but only a full re-run at the same SHA (run 2) settles it. Full log: `soak-artifacts/human-gate-preflight/20260912-115131/aaemu-gate-tests.FmWNWy.log` |
| `./scripts/gate.sh` (run 2, zero code delta) | **GREEN: 3082 total / 3081 passed / 0 failed / 1 skipped**; skip identity `Provision_Activate_Persist_Deactivate_RoundTrip`; MCP stdio smoke 39 tools PASS; archaeology gate smoke 24 tools / 679 tables read-only PASS. Full log: `soak-artifacts/human-gate-preflight/20260912-115131/aaemu-gate-tests.TR1rUO.log` |
| Isolated lane `--tier smoke --lane human-gate-preflight` | **PASS** (exit 0), ports login 27456 / int 27453 / game 27458 / stream 27466 / bridge 27476 / webapi 27496 / db 57453, compose `e2e_human-gate-preflight`; artifacts `soak-artifacts/human-gate-preflight/20260912-115131/` with `git-head-sha.txt`, `summary.txt` (`1 0 c7351b242…`), class log + list-tests log, `game.log`/`login.log`, `rowboat-e2e-report.json` (`verdict: PASS`, `milestone: SHIPS-01 slice 1 (live stack)`, `characterObjId 22011`); teardown PID-verified, ports free |
| Production after the lane | unchanged: `aaemu-*` containers still up 2 days; lane ports 0 bound |

**Smoke-lane artifact gap (record it, don't paper over it).** The lane artifact
directory does **not** carry the full evidence contract from
`Docs/wiki/Soak-Runs.md`: there is no env/ports file and no md5 file, and
`rowboat-e2e-report.json` has no `source_sha` field. Those facts were printed on
the lane console and preserved as
`soak-artifacts/human-gate-preflight/20260912-115131/lane-preflight-20260912.log`
(lane root, derived ports, compose project, `canonical compact.sqlite3 md5:
78b3bdbf038db3b927056106efdf91af`, `canonical md5 verified`). When citing a lane
run, attach that console log — the md5 gate and port block are not recoverable
from the artifact tree alone.

**Gate evidence preservation.** `./scripts/gate.sh` only tails the runner; the
full log (including the skipped test's identity) is written by the script to a
**volatile** `mktemp` path, `/tmp/aaemu-gate-tests.<rand>.log`
(`scripts/gate.sh:16, 25, 28`). Copy that file somewhere durable before citing a
gate result — this session's three logs live in the lane artifact directory
above. The five-line tail alone is not sufficient evidence, and a `/tmp` path is
not a durable citation.

**M8 v6 soak — supporting A/S evidence (not H).** The user-reported 2026-09-12
soak and its artifacts are in place at
`soak-artifacts/m8v6-soak/20260912-083553/`: lane `m8v6-soak`, tested revision
`41906276b609008db43d5e3f867b8e56707ebef0` (ancestor of current HEAD; the only
commit after it is the docs commit `c7351b242`), `summary.txt`
`5 0 41906276b609008db43d5e3f867b8e56707ebef0 …`, and 5/5 PASS class reports:

| Class | Report | Verdict | Notes |
|---|---|---|---|
| `B4PackSoakE2eTests` | `b4-pack-soak-report.json` / `.md` | PASS | kills 1 × kill-9 pre-window; window 9.0 min wall / 7.0 steady |
| `B4SlaveSoakE2eTests` | `b4-slave-soak-report.json` / `.md` | PASS | vehicle lifecycle incl. restart + re-summon |
| `B4FarmSoakE2eTests` | `b4-farm-soak-report.json` / `.md` | PASS | 747 s |
| `DominionSoakCycleE2eTests` | `dominion-soak-cycle-report.json` + `.jsonl` trace | PASS | declare → tax → phase-cron → kill-9 reload; siege combat descoped by design |
| `VillageFullDayRestartE2eTests` | `village-fullday-restart-report.json` | PASS | **2 bots** (`m8vffarmer`, `m8vfcrafter`), **1** kill-9 restart, 5 asserted rows (playerbot_metadata byte-equal, audit sink byte-equal, characters/position equal, accounts labor equal, items multiset equal) |

Scope honesty for the last row: it is a **supporting C5/re-soak data point**, not
the full M8 exit contract (which names 2 farmers / 1 crafter / 2 haulers /
3 adventurers, ≥3 restarts, auditable economy at 25 embodied). Do not cite it as
M8 contract closure and do not cite it as H. The authoritative M8 runtime record
remains the 2026-09-08 day-scale C5 re-soak (24/24 ledger, 4/4 kill-9, tested
`a4d35fee8`) described in `STATUS.md`.

**SHA boundary — v6 is historical, not a verification of this checkout.** The v6
artifacts are stamped `41906276b…`; the current checkout is `c7351b242…` (v6's
child, a docs-only commit). Two distinct consequences:

1. v6 verifies the tree it ran (plus the dirty-file caveat above) — **not** the
   current checkout and **not** any deployment. Treat it as historical context
   until the current SHA is separately run.
2. Never merge evidence across the boundary: a human wave must run at one
   recorded SHA, and the v6 numbers may appear only in a notes/context column,
   labelled with `41906276b` and the lane name.

Per the plan's contingency, the guide only **records** the v6 artifact; the
authoritative `STATUS.md`/`ROADMAP.md` M8 record is left exactly as it stands
(2026-09-08 QUALIFIED exit at `a4d35fee8`, H UNKNOWN). Do not rewrite the
roadmap's qualified record from a console log, and do not promote v6 into
`SCORECARD.md` H evidence.

## 5. Human-gate worksheet

### 5.0 Row template

One row per gate; copy this table into the evidence directory (§7) and fill it in
while playing. **H verdict** is one of `U` (not run / no verdict), `1` (partial
or proxy — never used for a real player attempt), `2` (verified for the named
curated scope), `0` (absent/broken), or `N/A` with a written reason.

| Field | Meaning |
|---|---|
| Gate | milestone/gate id (M1, M2, M3a, …) |
| SHA / lane | exact tested revision + play surface (§3) |
| Setup mode | one of `fresh` (clean character, nothing granted) / `checkpoint` (snapshot of a legitimately progressed character — attach the snapshot id/origin) / `synthetic` (state granted by fixture, manifest, or scenario setup) / `GM-assisted` (cheat or kit used — list every command verbatim). Anything but `fresh` is **not** progression evidence for the skipped steps. |
| Cadence | `REALTIME` (retail rates) or `FAST_SIM` (accelerated — list each rate knob and value, e.g. `ExpRate 10`, `GrowthRate 50`). Bot evidence under FAST_SIM is A/R only and never conflates with REALTIME/retail progression; H rows close only in REALTIME. |
| Location | zone + coordinates at start |
| Actions | the ordered player actions performed |
| Expected client-visible result | what should appear in the client |
| Observed | what actually appeared, verbatim (include fails/crashes) |
| Persistence checkpoint | logout / relog / restart step performed, and what survived |
| Verdict | PASS / FAIL / CAVEAT |
| H | `U` / `0` / `1` (actual partial human evidence only — never bot/proxy) / `2` / `N/A` + one-line reason. This wave awards no H=1 or H=2. |

Rules: GM assistance is allowed **only** for setup the row does not test, and
only when recorded verbatim in the `Setup mode` field — never for the
behavior/transition under test (helping the tested step is a FAIL, not a
shortcut). Record coordinates and item ids rather than descriptions; a FAIL
stops the row and starts a bug note — do not "work around" it silently.

### Setup acceleration: cheats, kits, and rates

The rule is *setup vs behavior*: anything that moves you to the test faster is
legal; anything that performs the tested behavior for you voids the row. The
cheats write real state with no audit (`Money`/`LaborPower` added directly,
XP recomputed to a target level), so they are invisible to ledgers — that is
why the row must carry the full record.

- **Humans:** `.kit hytest`, `.teleport`, `.level`, `.labor`, `.gold`,
  `.exprate`, `.growthrate` are all legal to *reach* a gate. Expanded Josh
  ruling (supersedes the travel-only limit recorded 2026-08-17): any command is
  allowed as setup, recorded verbatim in the row — but **any progression or
  economy criterion the command bypasses stays unverified** (e.g. a `.level`'d
  character cannot evidence M1 leveling; a `.gold`'d purse cannot evidence M4
  payout reconciliation).
- **Bots: never.** A cheated bot cannot evidence progression, economy, or
  ownership — its money/XP/materials appear from nowhere. Purpose-built test bots
  earn their state instead: **fresh** (level 1, natural progression),
  **checkpoint** (snapshot of a legitimately progressed bot — attach snapshot
  id/origin), or **seeded synthetic** (provisioned for a narrow subsystem gate,
  mechanically marked synthetic and never cited for loop behavior).
- **One character per row.** Cheat residue persists; a `.level`'d/`.gold`'d
  character reused in a later payout-reconciliation row breaks the ledger with an
  unexplained opening balance. Start clean or checkpoint per gate.
- **Rates preserve bot evidence only when labelled.** `.exprate`/`.growthrate`
  are real server multipliers, but a bot run under them is **FAST_SIM**, not
  REALTIME — record every knob and value, and never conflate it with retail
  progression. Labor/craft/schedule have no rate knobs today (verified); do not
  promise 10–50× behavior for them, and do not change shared-server rates without
  a named mode and rollback.

### Bot track: M1B–M8B beside the human milestones (acceptance-track shorthand, not new milestone IDs)

The goal is bots running M1–M9 through the same world systems, as a parallel
acceptance surface alongside the human track (M1H–M8H) and a mixed track (human
+ bots cooperating without cheats/bypasses). Per milestone, the bot track means:

| Gate | Bot-track requirement | Prereq state today |
|---|---|---|
| M1B | Playerbot completes the Solzreed route from level 1, no GM shortcuts | `LevelingLoopScenario` closes bounded legs (rig evidence); full-route decision closure is still the open PB-002 question |
| M3B | Playerbot acquires prereqs, places/builds/uses/persists a home | Contract actions verified; end-to-end live proof open |
| M4B | Playerbot completes the trade lifecycle and reconciles payout | Rigs + scenarios exist; live CONSENTED payout proof open |
| M7B | Human + bots cooperate in one party | Mixed-worksheet prereq unverified: no live party-consent path (above) — blocks this worksheet only, not unrelated automation |
| M8B | Bot population autonomously sustains the village | Mixed-worksheet prereq unverified: no human-observable deployed profession roster — blocks this worksheet only, not unrelated automation/scenario work |

For M8 the loop proof sits in automation (day-cycle scenarios, kill-9 equality,
ledger reconciliation); the human verifies coexistence and feel. Neither side may
cite the other: a seeded roster is not progression proof, and a green soak is not
H.

### What H is, and what bots are, per gate

Two different questions, kept apart:

- **H asks what you see, feel, and achieve through the client**: the route
  completes, the reward lands, the movement feels right, the payout reconciles.
  That and only that can move H.
- **Bot-loop correctness** (a bot harvesting, hauling, partying) is **A/R/L
  evidence** — proven by scenario reports and `playerbot_metadata`/audit
  equality — *unless the milestone explicitly requires a living-world
  observation*, in which case what you observe goes in the notes column and the
  loop is proven by the automation underneath.

Concretely:

- **M1 → M5 economy/quest/housing/boat gates: bots are NOT visual acceptance
  criteria.** You play the contract; the only bot-related verdict is the
  negative one (they must not contaminate your evidence). M1 (§5.1) must be run
  with the 250 hunting bots disabled or relocated away from Solzreed — they home
  to your spawn with `HUNT=1` and competing for foxes/boars invalidates
  kill-credit evidence. Record which you did.
- **M7: blocked prereq, then three *party* behaviors you can touch.** Until a
  consenting roster exists, do not run this section. Once it does: your invite
  is accepted, they follow without stacking glitches, they assist your marked
  target and regroup after a wipe. `PartyInvite`/`PartyAccept` live on the M5
  actor contract, which automation drives — no code shows an ordinary presence
  roamer auto-accepting a player's invite, so a bot that ignores your invite is
  a **failed prerequisite**: stop the row, record the ref/ignore verbatim as a
  **FAIL/blocker**, and rerun after consent is provisioned. Never a pass.
- **M8: you visually observe farmer/crafter/hauler/adventurer activity, but the
  loop is proven by the automation** (day-cycle report + ledger/metadata
  equality). Your H contribution is the other half the automation cannot supply:
  the village feels alive and nothing about the bots deletes, duplicates, or
  blocks your own state.

### Bot prereqs: what actually exists today (verified in source)

- `/bot add <name>` provisions/adopts a bot through the production
  `HeadlessSession` path (idempotent); `list` / `here` / `go` / `to` / `remove`
  manage them (all admin-gated at access level 100).
- **Party behavior comes only through a scenario/actor path or a bot configured
  for that interaction.** Do not promise spontaneous invite acceptance from the
  250 roamers.
- **Professions have no live assignment path.** `RecordProfession()` is called
  only from the automated village-cycle seams (`BotDriveBridge`), never from a
  chat command. M7 party-join consent and the M8 2-farmer / 1-crafter / 2-hauler /
  3-adventurer roster are therefore **unverified for these mixed/human worksheets**: do not run
  the M7 party worksheet or the M8 human-observed day scenario until the specific provisioning path
  is implemented or a seeded roster is configured and verified. This blocks those worksheets only —
  existing automated/scenario work and future fresh-character progression tests proceed subject to
  their own prerequisites. This is a scoped-card gap, named here instead of silently absorbed
  into the wave.
- **The one operator-side prerequisite you can act on now is M1 hunting
  isolation**: disable or relocate the hunting profile for the M1 quest run.

### 5.1 M1 — Quest and progression spine: curated Solzreed route

- Contract: **REQ-M1-10** human playtest verdict on the curated Solzreed route
  (`ROADMAP.md:783-784`); the checklist is `Docs/wiki/Golden-Route-Solzreed.md:193-199`.
- Current recorded state: automated evidence CLOSED; human playtest OPEN —
  deferred gate #1 / Open Decision #1, **H UNKNOWN** (`ROADMAP.md:798, 848-853`;
  `PROJECT-CONTROL.md` M1 row "Unknown (H open)").
- Reset: fresh character creation (no GM repair). Fast-forward kit is allowed for
  travel only (`.kit hytest` / `.teleport` — level/labor/gold/portal conveniences
  per the recorded Josh ruling).
- Character: new **Nuian**, spawn Solzreed (zones 9/124/125), level 1.
- Steps and expected results (verbatim checklist, each an evidence item):

| # | Player step | Expected client-visible result |
|---|---|---|
| M1-a | Notice board + errands: 250, 251, 329, 330, 2239, 252, 324, 325, 2531, 2532 | Kills credit, gathered items consumed, quest rewards land; level 1 → 3-4 |
| M1-b | Village chain 254 → 255 → 256 → 257 → 259 (+ fan 260 → 261) | Chain advances with no stuck step; level 5-6 |
| M1-c | Shepherd arc 265, 266 (watch the LetItDone **report** behaviour live), then 354 | Both complete via the report act, not auto-advance |
| M1-d | Mount chain 4292 → 4294 → 4295 | Character receives first mounts — items **8159 / 8160 / 8161** (Lilyut horses) — can summon them from the items; reward item **18649** (pet-heal potion) arrives |
| M1-e | Mid-route logout + restart, then relog | Quest log state resumes; special attention to timed quests **350 / 4292** (blocker class D) |
| M1-f | Bloody Hand arc 2255 → … → 2266 | Arc completes end to end; level 4 → 10 |
| M1-g | Bounty-board kills + journal turn-in (kill-acceptor family, BUG-006) | Kills credit and the journal turn-in works |

- Known non-H blockers to distinguish from live failures: 250 / 265 / 266 are
  harness/manifest artifacts and 350 / 4292 are the timed-quest persistence
  defect (`Golden-Route-Solzreed.md:160-190`). Report a live failure only if the
  client itself misbehaves.
- H verdict: per row, `U` until run.

### 5.2 M2 — Golden-path baseline, original two-player leg

- Contract: **REQ-M2-5** — "two players attempt the entire route from the
  reproducible reset state; every blocker captured with stage, repro, evidence,
  and its owning M3/M4 card" (`ROADMAP.md:919-923`). Two **accounts/clients**, one
  person is acceptable (`Docs/JOSH-QAT-WAVE4.md` `[TWO-CLIENT]` convention).
- Route under test (loop definition): clean reset → ordinary golden-path
  quest/progression → required first-mount/baseline state → restart/clean-state
  persistence verification (`ROADMAP.md:971-975`).
- Reset: documented reset/seed procedure, third-party reproducible (REQ-M2-4).
- Current recorded state: original two-player baseline is a **Josh-owned deferred
  gate #2**; bot baseline is proxy and never H=2; **H UNKNOWN**
  (`ROADMAP.md:952-967`; `PROJECT-CONTROL.md` M2 row).
- Expected: both players reach the mount/baseline state; every blocker recorded
  with stage + repro + evidence + owning card.
- H verdict: `U` until run.

### 5.3 M3a — Homestead shell (two players, one session)

- Exit condition (verbatim): "two players establish adjacent homesteads and use
  the curated objects during ONE uninterrupted session." (`ROADMAP.md:1100-1101`)
- Current recorded state: COMPLETE on **bot-functional/proxy** evidence; M3a
  contract replay = deferred gate #3; **H UNKNOWN** (`ROADMAP.md:1091, 1103-1112`).
- Steps: place homestead (adjacent spacing enforced — overlap must be rejected);
  build through the normal construction path; plant → grow → harvest crops; use
  storage + furniture; second player demonstrates ownership/permission behaviour
  on the first player's plot.
- Persistence checkpoint: none required in-session (persistence is M3b's class,
  `ROADMAP.md:1093-1094`).
- Expected client-visible: placement rejection on overlap, decoration limit
  enforced, crops grow and harvest, coffer/furniture open with correct contents.
- H verdict: `U` until run.

### 5.4 M3b — Property persistence and recovery

- Exit condition (verbatim): "the same two homesteads survive repeated logout,
  restart, crash-recovery, and re-entry tests WITHOUT state loss or
  duplication." (`ROADMAP.md:1158-1160`)
- Scale bar: N≥3 crash cycles; autosave p95 < 2 s at 2 homesteads + 25 bots
  (`ROADMAP.md:1114-1210`).
- Current recorded state: COMPLETE with R=2 evidence; player/H UAT open —
  `PROJECT-CONTROL.md` M3b row "Unknown (engineering/re-entry gate; H open)".
- Steps: decorate/plant → logout → relog → assert state; restart the game
  process → relog → assert; kill -9 during a save window → restart → assert;
  harvest mature crops after recovery and confirm no duplication.
- Expected client-visible: same furniture/crops/rotation/attachments/storage
  contents before and after each cycle; no orphans, no duplicates.
- Failure notes must name the cycle (logout / clean restart / kill-9 in save).
- H verdict: `U` until run.

### 5.5 M4 — Trade, crafting and transport integrity

- Exit test (verbatim): "group harvests real materials → crafts pack → loads
  vehicle → travels defined route → unloads + sells → correct reward → repeats
  after restart." plus M2 release validation — **four players** complete one
  integrated session from a clean reset state **without GM repair**
  (`ROADMAP.md:1267-1272`; REQ-M4-5 `ROADMAP.md:1236-1239`).
- Current recorded state: historical integrated release CLOSED on scripted proxy;
  human playtest is **deferred gate #4**, "deployment-lane playtest after Josh
  GO"; **H UNKNOWN** (`ROADMAP.md:1251-1258, 1325-1342`).
- Steps: gather → craft pack → place/pickup → load onto owned vehicle → drive the
  route → unload → sell → confirm reward and labor/mail payout → restart →
  repeat once.
- Expected client-visible: pack attaches to the vehicle, route travel renders,
  sale pays the corridor reward, and the reward is not duplicated after restart.
- Cross-checks to capture: labor (−60/pack) and mail payout (124540/pack,
  SpecialtyManager) conservation (`ROADMAP.md:2269-2270, 2328` — deferred gates
  #3/#4 rows).
- H verdict: `U` until run.

### 5.6 M5 / M5.3 — actor contract surface and movement fidelity (feel)

- M5 core human-feel is recorded **N/A** for the core contract; feel belongs to
  later phases and M5.1/M5.2/M5.3 record **H UNKNOWN — never H=2 from scripted
  evidence** (`ROADMAP.md:1416-1423, 1634-1760`).
- Open human-relevant scope today: geometry/fidelity **regrade** against the
  changed movement profile (trapezoidal profile `a38484f9e`; corner-blending
  dropped → Q3 outcome, `ROADMAP.md:19-36`).
- Steps: walk/strafe/turn with mouse-look; mouse-move steering at low and high
  speed; approach and stop at a target (arrival radius); drive a vehicle through
  a corner; open a door/NPC interaction and stop the actor mid-action.
- Expected client-visible: heading and velocity change smoothly with no
  teleport/snap, no rubber-banding, stops where expected, no camera fighting.
- Record feel verbatim (what looked wrong, where, coordinates) — this is the
  qualitative half that automation cannot cover.
- H verdict: `U` until run.

### 5.7 M6 — deterministic playerbot framework (restart gate; H N/A)

- Exit test: 10 bots run 6 hours with no unrecovered loops, no inventory
  duplication, no runaway combat, no DB corruption, no tick-budget overrun
  (`ROADMAP.md:2046-2048`).
- Player-close is recorded **N/A (restart gate)** with the written reason that M6
  is a restart/soak gate, not a client-feel gate
  (`PROJECT-CONTROL.md` M6 row; full M6 exit label still not claimed).
- Consequence: H = `N/A` applies to the operational restart/soak criterion with the reason above.
  Separate recorded M6 visibility/feel caveats live in the ledger — this N/A does not claim M6
  has no human-related acceptance anywhere and does not overwrite its ledger rows.
  If a bot-visibility observation is performed, log it
  in the notes column and keep H = `N/A` with the reason above.

### 5.8 M7 — Adventurer and party bots (Playerbots Alpha)

- Exit test (verbatim): "one human + three bots complete the curated leveling
  route and a selected group encounter." (`ROADMAP.md:2404-2405`)
- Current recorded state: adventurer and party feature lists COMPLETE; M7 exit
  OPEN; **H UNKNOWN — Josh confirms feel** (`ROADMAP.md:2364-2538`;
  `PROJECT-CONTROL.md` M7 row "Unknown (H/client gate open)").
- Reset: clean route start; human character plus three bot party members.
- Steps: invite/join the party, follow the leader, rally, assist on a target,
  avoid a pull, wait, resurrect after a death, mount-travel between legs; then the
  selected group encounter.
- Expected client-visible: bots accept party invites, follow without stacking
  glitches, assist on the marked target, regroup after a wipe, and travel mounted
  with the human; the group encounter is cleared as a group.
- **Party-join prereq (blocked/unverified today — do not run this section until
  it is resolved):** the contract expects *accepted* party behavior. Provision
  three bots, then invite them; if any bot ignores or refuses the invite, that
  is a **failed prerequisite, not gate evidence**: stop the row, record the
  refusal verbatim (which bot, which ask, what — if anything — it did) as a
  **FAIL/blocker**, and rerun only after a valid provisioning path (scenario
  actor or seeded consenting roster) is implemented or configured and verified.
  Do not treat the refusal as a PASS of anything.
- Note: the one-human gate may use the M5 stand-in rule **for function**; the feel
  verdict is Josh's and cannot be substituted (`ROADMAP.md:578-595`).
- H verdict: `U` until run (and `U` because the prereq is unverified, not because
  the gate was attempted).

### 5.9 M8 — Living Village: day-scale human scenario

- Exit test (verbatim): "a village with 2 farmers, 1 crafter, 2 haulers, 3
  adventurers + human-owned homes/farms operates a full day across multiple
  restarts with an auditable economy." The exit test "runs at 25 embodied
  concurrent within G1's numeric budgets." (`ROADMAP.md:2855-2873`)
- Current recorded state: **QUALIFIED exit (runtime evidence) 2026-09-08** via the
  day-scale C5 re-soak (24/24 ledger, 4/4 kill-9 restarts, scheduler VALID,
  physics/autosave PASS, DB-volume INVALID by design); C1/C2 **H UNKNOWN**; M8
  exit feel has **no recorded deferral**, so it stays **UNKNOWN (not DEFERRED)**
  (`ROADMAP.md:175-192`; `STATUS.md` 2026-09-08 entry).
- **Automated context, explicitly not H:** the C5 re-soak, the A5/G1 scale gates,
  and the 2026-09-12 v6 soak (§4) are A/C/R/S evidence. Cite them as context in
  the notes column only.
- Reset: clean village reset with the human's own home/farm in the village; note
  the exact reset procedure used.

> [!WARNING]
> **M8-a Field Notice (PB-FARM-01):** In unpatched builds, three code traps in `BotRoamStepExecutor.cs` cause farmer bots to freeze or wander off (3m merchant limit, 150m soil horizon, and maturation patrol fallthrough). Until the code fix lands, ensure the bot spawns within 2m of the seed merchant with initial seeds/copper, or set `GrowthRateMultiplier=500` and `BotRoamRadius=5.0` in `Config.Local.json` to prevent wandering. See `PLAYERBOT_PROGRESSION_AND_TESTING_FRAMEWORK.md` §3.4.

- Day-scale scenario (human observes a shared village day; record each leg):

| # | Village leg | Expected client-visible result |
|---|---|---|
| M8-a | Farmer 1 and farmer 2 each work their own plot | Each harvests only owned mature crops, deposits yield, replants; a foreign/occupied plot is refused with a visible reason |
| M8-b | Crafter converts inputs at a workstation | Materials are consumed and the product appears; a missing input fails closed with no phantom output; a busy workstation holds |
| M8-c | Hauler 1 and hauler 2 craft → load pack → drive route → specialty sale → deposit → **return home** | Pack attaches and rides the vehicle, the corridor sale pays, proceeds are deposited, and each hauler returns to its own home anchor |
| M8-d | Adventurers run their route in the same village | Routes continue without wedging; leveling/combat proceeds in view |
| M8-e | Human plays alongside (own home/farm) | Human-owned property and village activity coexist; nothing about the bots deletes, duplicates, or blocks the human's state |
| M8-f | Human witnesses the economy being auditable | Sale/payout outcomes are explicable from what was observed (no invisible money or items) |
| M8-g | Full-day pacing, ≥3 restarts interleaved | Village resumes after each restart with schedules/homes/professions/inventory intact; the human sees continuity — **this is the feel half automation cannot supply** |

- Persistence checkpoints: after each of the ≥3 restarts — schedules, home
  anchors, professions, inventory, and ledger continuity.
- H verdict: `U` until run; per-leg notes are what makes the eventual verdict
  defensible.

### 5.10 Sub-track human/client gates (run opportunistically; keep labels honest)

These are recorded in the control matrix with their own evidence labels; none is
H=2 today. Run them when the surface exists, and record `U` / `DEFERRED` exactly
as the register does — "never silently convert them into passes".

| Track | Human/client ask | Recorded state |
|---|---|---|
| PB-001 routed navigation | exercise interior + cross-region routes live; Josh assesses movement feel | Unknown (live/H open) |
| PB-002 autonomous progression | broad route acceptance beyond the bounded loop | H UNKNOWN (no feel verdict attempted) |
| PB-005 NPC grounding | Josh runs the grounding tour and records coordinates/screenshots; engineering then classifies cave/deck/submerged + duplicate rows | N/A as an audit outcome; **H tour required** |
| PB-007 WAR-HONOR | deferred (>251 hostile kills + conflict timer); do not reuse the handshake pass | explicitly **DEFERRED** |
| Mail | real-client return opcode capture + ownership UI checks, COD, expiry/bounce | Unknown (client/UAT open) |
| Dominion | real declare-trigger UI, later combat/siege slices | Unknown (client/UAT open) |
| Ships | re-run W4-6 B1–B6: steering, disembark/despawn, passenger view, shipyard restart | Unknown (client/UAT open) |
| INDUN-01 instance dungeon | bot-party clear-then-exit (Hadir Farm 46) through the real portal doodad | Unknown (H open) |
| Undefined-mechanics rows (AGGRO-PACK-01, AUCTION-BANK-DOODAD-01, RESPAWN-LADDER-01, NPC-INTERACTION-01, BOOK-01) | player-visible behaviour checks per the discovery rows | all `W=0/A=0/H=U` |

## 6. Restart and recovery checkpoints

Run these inside a worksheet row (they are the persistence checkpoint field), and
use the same order every time so results are comparable.

| # | Checkpoint | How | Expected |
|---|---|---|---|
| R1 | Logout → relog | client logout, wait for character save, relog | quest log, inventory, position, property unchanged; no duplicate items |
| R2 | Clean game-process restart | stop the game process, start it again, relog | same as R1; world registration recovers; no "character already exists" errors |
| R3 | Kill -9 during a save window | `kill -9` the game process while a save is in flight, restart, relog | no loss and no duplication; the character file/DB row is consistent |
| R4 | DB container restart | restart the MySQL container on the **play surface**, then the game | game reconnects; state intact; **never** during a live worksheet row unless the gate is specifically testing recovery |
| R5 | Timed-quest focus | be mid-**350** or mid-**4292** at R1/R2 | quest resumes with its timer; this is the known class-D defect area — record the exact divergence if it fails |

R2/R3 on the deployed test server are **owner-gated**: they restart the shared
stack and interrupt anyone playing, so they run only inside a worksheet row that
tests recovery, with the deploy/rollback tags from §3.2 to hand. Do them
lane-scoped on the lane host (`Scripts/e2e/e2e-stack.sh status|logs`,
`Scripts/e2e/e2e-reset.sh`, `Scripts/e2e/e2e-snapshot.sh`) or by killing the game
process **by PID after verifying `/proc/<pid>/cwd` is under the intended root**.
Unscoped `pkill -f AAEmu.Game` is banned — an unscoped pkill already killed an
unrelated lane's processes once (`STATUS.md`, 2026-09-06 INCIDENT entry).

Automated restart evidence you may cite as context (never as H):
`M51AttachedPackRestartE2eTests`, `EconomyDayCycleE2eTests`,
`B4BotRestartPersistenceE2eTests`, `DominionRestartPersistenceE2eTests`,
`M3bExitPersistenceE2eTests`, `VillageFullDayRestartE2eTests`.

## 7. Evidence capture

Create one directory per human wave on the dev host:

```bash
mkdir -p human-evidence/<yyyy-mm-dd>-<gate>-<sha7>/
```

Attach, per gate:

1. The **worksheet** copy (§5.0 template) with one row per gate/leg.
2. `git-head-sha.txt` — exact tested SHA plus **clean-tree proof**:
   `git status --porcelain -- 'AAEmu.*'` must be empty (this catches untracked
   tracked-glob sources like `AAEmu.IntegrationTests/E2e/B4FarmSoakE2eTests.cs`,
   which lanes rsync; §2 provenance caveat). Not empty → record the extra files
   with their md5s next to the SHA, or restart from a clean checkout at the
   recorded SHA.
3. Play surface identity: the deployed revision (`git rev-parse HEAD` on
   `/root/AAEmu`), the image digests, and the `GameServers` host/port the client
   used (§3.1).
4. Client-visible evidence: screenshots named `<gate>-<leg>-<n>.png`
   (quest log, reward prompt, mount summon, property after restart, sale payout).
5. Server-side logs for the same window: `game.log`, `login.log` from the play
   surface; lane logs stay under `soak-artifacts/<lane>/<timestamp>/`.
6. Any automated artifact cited as context, referenced by path — never copied into
   the H column.
7. The **first divergence** for each FAIL: timestamp, character, zone/coords,
   what was clicked, what appeared instead, and the suspected class
   (engine bug / content / harness / environment).
8. **PlayerTrace capture & task evaluation**: When collecting human action traces,
   use the interactive PlayerTrace Task Dashboard (`http://192.168.0.187:8085` or
   `python3 Scripts/playertrace-coverage/dashboard_server.py`) for step-by-step
   instructions, one-click copy, and required checklists. Validate completed traces
   using `python3 Scripts/playertrace-coverage/task_evaluator.py <trace> --task-id <id>`
   or via the dashboard's `🔍 Evaluate Trace` button to verify mandatory packet
   assertions and lifecycle integrity before attaching. Full task creation schemas
   and assertion rules are documented in `Scripts/playertrace-coverage/TASK_SPEC_AND_EVALUATION_GUIDE.md`.

Rules: report verbatim output, not paraphrase; keep the old evidence when a leg
is re-run (append a new dated row instead of editing history); never mix evidence
from two SHAs in one row.

## 8. Results protocol and next-course rule

1. **One row per gate.** Josh records; nobody else fills the H column. Do not
   award `H=2` from bot/script/soak/QA evidence.
2. **Return** the completed worksheet plus the exact tested SHA and the play
   surface (§3) used for it, with the artifact directory from §7.
3. **Reconcile** observed FAILs against the current register in `ROADMAP.md`
   (current register at the top of the file; older paragraphs are history), then
   against `SCORECARD.md`'s dimension rows. Every non-`U` grade must link to the
   evidence artifact (`SCORECARD.md:36-40`).
4. **Choose the next course as the smallest evidence-backed implementation
   slice for the affected loop:** fix the human-blocking defect the gates exposed first
   (a live FAIL that stops the golden path outranks new work in its lane); link the
   concurrent bot-progression and M9 lanes from the roadmap for unaffected work. R1 remains
   an existing proposed next substrate slice, not the mandatory outcome of every worksheet.
   **No new milestone and no scope expansion** from a worksheet run; a missing
   or broken mechanic becomes its own scoped card, not a widened wave.
5. **Keep the SHA boundary.** Automated evidence cited as context must name its
   own tested SHA and lane (`41906276b` + `m8v6-soak` for the v6 soak), and must
   never be presented as verification of the SHA the human played. One worksheet
   row = one SHA.
6. **Do not pre-claim.** Do not claim an unperformed H run, a deployment without a receipt,
   or an M8 exit from a narrower soak. Engineering/deployment evidence and owner-directed vision
   corrections update records independently; only H promotion waits for the relevant human result.
   updates wait for the returned worksheet (§8, rule 7).
7. **Update the authoritative records in the same wave** the results are
   accepted: `ROADMAP.md` (contracts/status lines), `STATUS.md` (current
   checkpoint), `SCORECARD.md` (mechanic dimensions), `EVIDENCE-LEDGER.md`
   (evidence-state transitions), and the affected `PROJECT-CONTROL.md` matrix row.
   Only an actual Josh/client run establishes H/UAT (`PROJECT-CONTROL.md:212-225`).
8. **Deployment pointer.** The `ROADMAP.md` correction register keeps the .165
   deploy pointer current; update it when the play surface changes, and never
   record the presence-demo image source as confirmed when it is unstamped.

## Appendix A — command reference

```bash
# preflight
git rev-parse HEAD && md5sum AAEmu.Game/Data/compact.sqlite3
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh [--fetch-launcher]

# local gates
./scripts/gate.sh [ClassFilter]
./scripts/archaeology-cycle.sh

# isolated lanes (DEV HOST only)
./Scripts/e2e/run-qa-suite.sh --tier smoke --lane human-gate-preflight
./Scripts/e2e/run-qa-suite.sh --tier qa [--target <Class>|all]
./Scripts/e2e/run-qa-suite.sh --tier soak [--target <Class>] [--minutes <n>]

# lane inspection (safe, read-only, lane-scoped)
ssh root@192.168.0.165 'ss -ltn | grep -E ":(1237|1239|1250|3306)\b"'   # prod listeners
ssh root@192.168.0.165 'docker ps --format "{{.Names}} {{.Status}}"'    # prod containers
ls soak-artifacts/<lane>/<timestamp>/

# play surface (Option A, §3.2) — start Login before Game
dotnet run --project AAEmu.Aspire.AppHost --launch-profile http
# or Path B: host MySQL, then standalone Login, then standalone Game
```

## Appendix B — ports and lane layout

| Port | Owner | Notes |
|---|---|---|
| 1237 | Login public (client) | launcher/login entry |
| 1234 | Login internal | game registration; conflict here shows as client **Maintenance** |
| 1239 | Game public | client world connection |
| 1250 | Game stream | UCC/emblems |
| 1280 | Game Web API | optional |
| 3306 | MySQL | host MySQL (Path B) / container (Aspire) |
| 20000–29999 | lane app block | `login`, `login_int`, `game`, `stream`, `bridge`, `webapi` |
| 40000–59999 | lane DB block | lane-unique MySQL host port |

Lane root: `/root/aaemu-e2e-<lane>` on .165 (repo, runtime, game-data, logs,
`.env`); compose project `e2e_<lane>`; artifacts return to
`soak-artifacts/<lane>/<timestamp>/` on the dev host.

## Appendix C — forbidden actions

- Running `run-qa-suite.sh` on .165 (dev-host-only guard, exit 2).
- Rebuilding `game`/`login` on .165 without first snapshotting the rollback tags
  (§3.2), or using `docker-update-local.sh` there (it drops `-p aaemu`,
  `--env-file`, the presence overlay, and the rollback snapshot).
- Unscoped `pkill`/process kills, touching a sibling lane's ports/DB/compose
  project, or destructive DB operations outside the lane's own volume.
- `--keep` as a "human lane" (§3.2): isolated lanes stay A/C/R/S evidence; the
  human surface is the deployed test server.
- Restarting/recreating `game`/`login` on .165 while Josh is playing (§3.4), or
  changing the presence profile mid-wave.
- Recording H from any automated artifact; or converting `U`/`DEFERRED` rows into
  passes because a related bot/soak run is green.

## Related

- [Home](Home)
- [Soak-Runs](Soak-Runs) — lane safety, tiers, artifacts, .165 execution model
- [Golden Route — Solzreed](Golden-Route-Solzreed) — M1 route + playtest checklist
- [Project Status](Project-Status)
- [Installation & Setup](Installation-&-Setup) · [Aspire Development Guide](Aspire-Development-Guide) · [Working with the Config.json files and server listings](Working-with-the-Config.json-files-and-server-listings) · [Client](Client)
- Repo records: `ROADMAP.md` · `STATUS.md` · `SCORECARD.md` · `PROJECT-CONTROL.md` · `Docs/JOSH-QAT-WAVE4.md`
