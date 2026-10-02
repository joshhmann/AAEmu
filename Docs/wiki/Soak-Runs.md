# Soak-Runs (E2E QA / soak / smoke on the testing server)

## Policy

All E2E **soak** and **QA** runs happen on the **testing server
`root@192.168.0.165`** in isolated lanes under `/root/aaemu-e2e-<lane>`. The
launcher itself runs on the dev host; only the isolated lane runs remotely. The
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
Run these commands from the **DEV HOST** in your repo checkout (`/root/aaemu-dev`). The suite rsyncs the working tree to .165 and builds there — **never invoke it on .165 itself**, which has no dev checkout.

```bash
# launched locally; execution is isolated on .165; artifacts rsynced back
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

## Bot brain shadow harvest (`jev-shadow/v2`)

The Jev-vs-our-brain A/B replay needs per-decision arbitration records.
`JevShadow` (`AAEmu.Game/Core/Managers/Bots/JevShadow.cs`) is that recorder — one
JSONL row per sampled bot wake, schema `jev-shadow/v2`, carrying the brain's
candidate set (`goal`/`action`/`priority`/`personality_weight`/`reason`), the
incumbent's own pick (`selected_goal`/`selected_action`/`selected_candidate`),
and the optional per-brain frozen inputs (`travel`/`survival`/`needs`/`combat`).
It is a **copy** surface: it never decides, never mutates a `Character` or the
world, and has no runtime authority. Design + experiment spec:
[`scorecard-explorations/mechanics/bot-brain-wiring-audit.md`](../../scorecard-explorations/mechanics/bot-brain-wiring-audit.md).

### Env vars (read by the game process, default OFF)

| Var | Default | Meaning |
|------|---------|---------|
| `AAEMU_JEV_SHADOW` | unset (**OFF**) | `1`/`true`/`True` arms the recorder |
| `AAEMU_JEV_SHADOW_SAMPLE` | `1` | write every Nth eligible wake; values `< 1` read as `1` |
| `AAEMU_JEV_SHADOW_PATH` | `Logs/jev-shadow.jsonl` under the game app base dir | output JSONL |

`JevShadow.Enabled` / `SampleEvery` / `FilePath` are static properties
initialised from these env vars at class-init. While the flag is off every
entry point is a static flag read and a return — zero allocation, byte-identical
wakes. The default compose configs do not arm the flag: no `docker-compose*.yaml`
in the tree sets it. The harvest script below sets it explicitly, for its own
run only. `.env.example` documents the three vars with every one of them
commented out (`# AAEMU_JEV_SHADOW=1`), so that file arms nothing.

### Reading one row: rejections, legal losers, incumbent

`jev-shadow/v2` separates the three kinds of proposal so none has to be inferred
from another. Two are arrays; the incumbent is a single object:

| Field | JSON type | Means |
|-------|-----------|-------|
| `candidates[]` | array | **HARD-precondition rejections** — proposed, then refused as illegal |
| `legal_alternatives[]` | array | **legal losers** — permitted, but out-ranked in preference order |
| `selected_candidate` | object or `null` | the **incumbent** the game actually took, as its own score row |

`candidate_count` is **selector truth**, not a length of either array: how many
proposals the selector weighed = hard rejections + all legal proposals (winner
included). `rejection_count` is exactly `candidates.length`. So
`candidates: []` with `candidate_count: 2` is a legitimate shape — nothing
was illegal; two proposals were legal and one won.

**`null` vs `[]`.** A collected wake always serializes an array, empty when
there was none of that kind, so "collected, and there were none" never reads the
same as "not collected". `legal_alternatives: null` means no selector result was
captured for that wake (nothing was selected); `[]` means a selection ran and the
winner was the only legal proposal. `selected_candidate: null` likewise means no
incumbent was chosen.

**Worked example** — row 4 of a 5-row isolated-lane dataset
(`/root/aaemu-e2e-t2-sol/runtime/game/Logs/jev-shadow-live-recorder-02.jsonl`,
SHA256 `6d5f8bc8f59b5069a997a72f55dd31bd3cb1c6d00a25c186c1ca740382fe08a9`,
scenario `quest-bootstrap-decision`). The excerpt below is a projection of that
row, wrapped so it is valid JSON:

> **This dataset is historical `jev-shadow/v1`, captured before the version bump.**
> Its `"schema"` field really does say `jev-shadow/v1`, and it is deliberately
> **not** relabelled — rewriting a captured artifact would falsify the record.
> It is shown here because it happens to exhibit the corrected two-proposal
> semantics (the count fix and `legal_alternatives` both landed in the build that
> produced it), but that makes it a **mislabeled v1**: the harvester rejects it and
> it must **not** be fed to the v2 replay. Use a newly captured `v2` run as v2
> evidence.

```json
{
  "candidates": [],
  "candidate_count": 2,
  "rejection_count": 0,
  "selected_candidate": {"goal":"quest.turn-in","action":"TurnInQuest","priority":30,"weight":0,"reason":"selected"},
  "legal_alternatives": [{"goal":"quest.accept","action":"AcceptQuest","priority":17,"weight":1,"reason":"legal; not selected"}]
}
```

Nothing was hard-rejected (`candidates: []`, `rejection_count: 0`). Two
proposals were legal: `TurnInQuest` at priority 30 won and became the
incumbent, and `AcceptQuest` at priority 17 was a legal loser. `candidate_count`
is 2 because both were weighed — the shape a `candidates`-only reading
mis-reports as "one candidate".

### Evidence: staged networked bot, scheduler wakes

The worked row is **scheduler-wake evidence from a staged networked bot**. It
came from `QuestPerceptionLoopE2eTests` on the `t2sol` lane (report
`quest-perception-loop-report.json`, `PASS`, scenario `quest-perception-loop`):
one bot logged in over the real login flow, was staged once at the start hub as
disclosed setup, and its wakes ran through the production
`PlayerBotScheduler` → `BotRoamStepExecutor.StepAsync` path with
`AAEMU_JEV_SHADOW=1` and `AAEMU_NEEDS_FARM_ENABLED=1`. **No presence-demo flags
were set** — this was a single staged test bot, not a presence population.

The **bot was staged, not human**: no human played anything and no client was
connected. These rows are **not** evidence of general autonomy and **not** of an
optimizer — the choice was made by the shipped selector over its shipped
candidate set, with no learned or search component involved. They show that the
arbitration ran and the recorder captured it. **`H` stays UNKNOWN.**

### Live `v2` capture: `t2sol` lane, 2026-10-02

The worked row above is `v1`. A **`v2`** dataset now exists on the same `t2sol`
lane and is the artifact to feed a v2 replay:

`/root/aaemu-e2e-t2-sol/runtime/game/Logs/jev-shadow-v2-quest-perception-01.jsonl`
— SHA256 `3aa299631b08af1103126ece71fcd9b9406241e5f85eb1f9dd7b16d1721ed514`,
source/test HEAD `626548de92b8dca8af1f0f92e22d37c9fbe6074e` (dirty working tree,
81 paths — inherited uncommitted work, so the SHA names the base revision only),
scenario `quest-bootstrap-decision`, `AAEMU_JEV_SHADOW_SAMPLE=1`, output path as
above. **4 rows, all carrying `"schema": "jev-shadow/v2"`** — no mixed-schema
content, so this is a genuine v2 dataset and not another mislabeled v1.

Row 3 is the two-legal-proposal shape: `TurnInQuest` at priority **30** selected,
with `AcceptQuest` at priority **17** as the legal loser. The quest-perception
test itself passed **1/1**.

**Caveats — what this capture does not establish:**

- The bot was **staged** at the start hub as disclosed setup, as in the run above;
  it is not a human-played session.
- **One bot, 4 decision wakes.** This is not a wake census — it is a smoke capture
  proving the v2 schema and the arbitration record, not behavioural breadth.
- No replay was run against it, and no `H` acceptance exists. `H` stays UNKNOWN.

The historical `v1` dataset and its section above are unchanged.

### Running it

`Scripts/jev-shadow-harvest.sh` boots an **isolated E2E lane** (real Login+Game
binaries, lane-unique ports and compose project, PID-scoped teardown). Both
processes inherit the shadow switch; only Game has the recorder. It refuses an
already-running lane server or occupied game port before boot. Choose ports
that are free on this host and provision the isolated lane's game data first:

```bash
# Export the same isolated settings for both the read-only preflight and harvest.
export E2E_ROOT=/root/aaemu-e2e-jev COMPOSE_PROJECT_NAME=jev
export E2E_INTERNAL_PORT=28327 E2E_LOGIN_PORT=28330 E2E_GAME_PORT=28332
export E2E_STREAM_PORT=28340 E2E_BRIDGE_PORT=28350 E2E_WEBAPI_PORT=28360
export E2E_DB_PORT=48320 DB_HOST_PORT=48320 E2E_GAME_HOST=127.0.0.1 E2E_BRIDGE=1
export AAEMU_PRESENCE_DEMO=1 AAEMU_PRESENCE_BOT_COUNT=10 AAEMU_PRESENCE_MAX_BOTS=10
export AAEMU_QUEST_BOOTSTRAP_ENABLED=1 AAEMU_NEEDS_FARM_ENABLED=1
export AAEMU_JEV_SHADOW_PATH=/root/aaemu-e2e-jev/runtime/game/Logs/jev-shadow.jsonl
./Scripts/jev-shadow-harvest.sh --minutes 30 --dry-run
# Only after the preflight succeeds; the script stops its own servers afterward:
./Scripts/jev-shadow-harvest.sh --minutes 30 --sample 1 --drain-seconds 30
```

`--minutes <n>`, `--sample <n>`, `--out <path>`, `--force` (allow overwriting
an existing **non-empty** dataset), `--keep` (leave the lane up), `--drain-seconds
<n>`, `--dry-run`, `--allow-prod-host` (explicit production opt-in; it still
requires explicit project and remapped ports). `--help` prints the full surface.

**`--dry-run` verifies, it does not merely echo the env.** Without an explicit
production opt-in, it requires an isolated root; either way it requires a
non-default project and remapped ports, refuses running lane Login/Game processes
and occupied ports, and checks the dataset path, lane log-cap guard, and canonical
compact SQLite md5. An existing lane DB is accepted only when its port and
credentials match that lane. It starts no servers or writes; unmet conditions
are reported before boot.

**Exit codes.** `0` — with `--dry-run`, all prerequisites checked are present;
on a real run, the dataset was produced and verified. `1` — a direct validation
or runtime failure (for example a shared project/port, invalid path, or failed
boot). `2` — a missing required environment variable, production-host guard,
existing dataset without `--force`, unknown argument, or aggregated preflight
failure (occupied port, drifted canonical data, or running lane server).
`--dry-run` never produces a dataset; its `0` means only that preflight passed.

**Bot population = the in-game `PlayerBotManager`/`PlayerBotScheduler` wake
path.** The presence harvest is one way to populate it, using
`AAEMU_PRESENCE_DEMO` / `AAEMU_PRESENCE_BOT_COUNT` /
`AAEMU_PRESENCE_MAX_BOTS` / `AAEMU_PRESENCE_MANIFEST` to create a multi-bot
population; `BotPresenceCoordinator` then starts `PlayerBotScheduler`, which
leases each bot and runs `BotRoamStepExecutor.StepAsync` — the call chain whose
`StepQuestLeg` / `StepNeedsFarmLeg` call the recorder. Headless (no client, no
human in the loop), repeatable, and already proven by the scheduler stage-1 soak.

That is the multi-bot route, not the only one that yields arbitration rows. A
targeted E2E scenario can also put a single staged bot on the same scheduler
path: `QuestPerceptionLoopE2eTests` did so without presence-demo flags, and the
recorder captured real quest arbitration. The split is scripted setup and wake
cadence on the rig's side; **which actions the scheduler picks are still
normal gameplay arbitration** — the bot accepted and turned in quest 6198
through the production quest leg and real report path. Such rows support the
same arbitration contract as a presence run, from a narrower population.

The WebApi/BotControl actuation surface remains distinct: it drives
*individual* actions (`/api/actors/*`) rather than the brain's own arbitration.

### Verifying the output

The script verifies and prints; it never asserts a success it did not measure:

1. the dataset exists at the resolved path (under the lane app base),
2. it is non-empty,
3. it reports complete (newline-terminated) row count + byte size,
4. **every** row parses as JSON and carries `"schema": "jev-shadow/v2"` — the
   check is whole-file, not first-line, so a dataset that opens with a v2 row and
   then carries v1 rows is rejected as mixed-schema,
5. it reports any torn trailing line and any malformed rows.

### Schema versions: `jev-shadow/v1` vs `jev-shadow/v2`

`v2` is a **behavioural** cutover (2026-10-02), not an additive one:

- `legal_alternatives[]` was added — the proposals that were legal but lost the
  preference order, previously invisible to a replay.
- `candidate_count` was redefined as **selector truth** (hard rejections + every
  legal proposal, winner included). In v1 it was the `ActorRequest` stamp
  `rejections + 1`, which structurally cannot see a legal loser and therefore
  under-reported any wake that had one.

**A v1 reader must not consume a v2 row**: the same field means something
different. `Scripts/jev-shadow-harvest.sh` therefore refuses a v1 dataset
outright and refuses a mixed v1/v2 file, rather than accepting a first line that
happens to be current. Existing v1 datasets under lane `runtime/game/Logs/` are
**historical and stay v1** — they are never rewritten or relabelled; re-harvest to
a fresh `--out` path instead of appending to one.

Anything it cannot verify it prints as `UNVERIFIED` and exits non-zero. **Row
count is a lower bound**, not proof of completeness: the writer is a background
thread draining a bounded 1024-line queue, drop-newest when full, and there is
no flush join on any shutdown path — lines queued at exit are lost by design and
drops are not recorded in the file.

### Evidence honesty — read before quoting a harvest

A harvest produces **synthetic / rig-tier** decision records from an isolated
lane running scripted bots. It is **contract and orchestration evidence only**:

- It is **NOT** proof of live human playability. No human played anything.
- It is **NOT** proof of bot autonomy. The bots ran a loop whose enablement,
  patrol home and scenario pacing are rig-controlled.
- **`H` stays UNKNOWN.** Human acceptance still requires the actual named human
  run in the client (`SCORECARD.md`), per the evidence-honesty gate in
  [`AGENTS.md`](../../AGENTS.md#evidence-honesty-and-anti-overclaim-gate-2026-09-16).
- A large row count is not evidence of a good decision; it is evidence that the
  recorder ran. Whether any pick was *right* is what the offline replay scores.
- Also per the audit: only the legs that actually call the recorder emit rows
  (quest, needs, and the per-brain captures from the same wake). Combat/travel
  arbitration rows depend on those capture call sites being live — treat
  `null` brain blocks as "did not run", not "no data".

Record the run like any other lane: HEAD SHA, lane root, window, expected bot
count, sampling rate, out path, row/byte counts — see **Evidence contract**
above.
