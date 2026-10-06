#!/usr/bin/env bash
# jev-shadow-harvest.sh — arm the JevShadow opt-in on an ISOLATED E2E lane and
# collect the `jev-shadow/v3` arbitration dataset the offline review needs.
#
# The writer itself lives in the game process
# (AAEmu.Game/Core/Managers/Bots/JevShadow.cs). It is armed ONLY by env:
#   AAEMU_JEV_SHADOW=1|true        master switch, default OFF
#   AAEMU_JEV_SHADOW_SAMPLE=<n>    write every Nth eligible wake (<1 reads as 1)
#   AAEMU_JEV_SHADOW_PATH=<path>   output JSONL; default Logs/jev-shadow.jsonl
#                                  under the game app base directory
#
# This script only orchestrates: it boots the lane's Login+Game (reusing
# Scripts/e2e/e2e-common.sh — same binaries, same config precedence, PID-scoped
# teardown, never pkill), holds the window, stops Game gracefully, waits for the
# writer thread to settle, and then VERIFIES the produced file. It prints no
# result it has not measured.
#
# Usage:
#   Scripts/jev-shadow-harvest.sh --help
#   E2E_ROOT=/root/aaemu-e2e-jev COMPOSE_PROJECT_NAME=jev \
#   E2E_INTERNAL_PORT=27327 E2E_LOGIN_PORT=27330 E2E_GAME_PORT=27332 \
#   E2E_STREAM_PORT=27340 E2E_WEBAPI_PORT=27360 E2E_BRIDGE_PORT=27350 \
#   E2E_DB_PORT=47320 DB_HOST_PORT=47320 E2E_GAME_HOST=127.0.0.1 E2E_BRIDGE=1 \
#   AAEMU_PRESENCE_DEMO=1 AAEMU_PRESENCE_BOT_COUNT=10 AAEMU_PRESENCE_MAX_BOTS=10 \
#   AAEMU_QUEST_BOOTSTRAP_ENABLED=1 AAEMU_NEEDS_FARM_ENABLED=1 \
#   AAEMU_JEV_SHADOW_PATH=/root/aaemu-e2e-jev/runtime/game/Logs/jev-shadow.jsonl \
#   Scripts/jev-shadow-harvest.sh --minutes 30 --dry-run
#
#
# EXIT CODES (unambiguous, and the same in both modes unless noted):
#   0  --dry-run : every prerequisite for booting the lane is present.
#                 Nothing was started and nothing was written.
#      real run  : the dataset was produced AND verified (file exists, is
#                 non-empty, and EVERY row carries schema jev-shadow/v3).
#   1  a runtime failure: a check or a boot step failed for a reason this
#      script reports (md5 drift, game process not found, early exit).
#   2  REFUSED before starting anything: unmet prerequisites, bad/missing
#      environment, an invalid output path, an existing dataset without
#      --force, the production-host guard, or an unknown argument.
#
# Note that 0 does NOT mean "dataset produced" in --dry-run mode: a dry run
# produces no dataset by definition. It means the lane is runnable.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
E2E_COMMON="$REPO_ROOT/Scripts/e2e/e2e-common.sh"
PROD_ROOT="/root/AAEmu"                 # prod compose root on the .165 box (WORKFLOW.md)
PROD_HOST="root@192.168.0.165"          # label only; this script never SSHes
SCHEMA="jev-shadow/v3"                  # JevShadow.SchemaVersion. v3 adds
                                         # target_id to every candidate row
                                         # (hard rejections, legal
                                         # alternatives, and the incumbent),
                                         # so a legal loser is joinable to the
                                         # objective it named. v2 and v1
                                         # datasets are historical and are
                                         # REJECTED by this harvester.
CANONICAL_SQLITE_MD5="78b3bdbf038db3b927056106efdf91af"

MINUTES=30
DRAIN_SECONDS=30
FORCE=0
ALLOW_PROD_HOST=0
KEEP=0
DRY_RUN=0
OUT=""
SAMPLE=""

usage() {
    # --help works without a configured lane; the real env contract is checked
    # after option parsing.
    local prod_root="${PROD_ROOT:-/root/AAEmu}"
    cat <<EOF
jev-shadow-harvest.sh — collect the jev-shadow/v3 arbitration dataset on an
isolated E2E lane, then verify it. Prepares/runs the bot shadow harvest; the
harvest is the only thing that starts a server, and only inside the lane root.

USAGE
  E2E_ROOT=<isolated-absolute-lane> COMPOSE_PROJECT_NAME=<isolated-project> \\
  E2E_INTERNAL_PORT=<port> E2E_LOGIN_PORT=<port> E2E_GAME_PORT=<port> \\
  E2E_STREAM_PORT=<port> E2E_WEBAPI_PORT=<port> E2E_BRIDGE_PORT=<port> \\
  E2E_DB_PORT=<port> DB_HOST_PORT=<same-port> E2E_GAME_HOST=127.0.0.1 \\
  E2E_BRIDGE=1 AAEMU_PRESENCE_DEMO=1 AAEMU_PRESENCE_BOT_COUNT=<n> \\
  AAEMU_PRESENCE_MAX_BOTS=<n> AAEMU_QUEST_BOOTSTRAP_ENABLED=1 \\
  AAEMU_NEEDS_FARM_ENABLED=1 AAEMU_JEV_SHADOW_PATH=<lane-jsonl-path> \\
    Scripts/jev-shadow-harvest.sh [flags]

REQUIRED ENVIRONMENT (missing values are listed and the run refuses)
  E2E_ROOT                    absolute lane (shared /root/aaemu-e2e is refused;
                              $prod_root requires --allow-prod-host)
  COMPOSE_PROJECT_NAME        isolated project, not the shared default e2e
  E2E_*_PORT, DB_HOST_PORT    explicit, distinct non-default ports; DB ports must match
  E2E_GAME_HOST               127.0.0.1 (no remote host)
  E2E_BRIDGE                  1 (bot bridge enabled on its remapped port)
  AAEMU_PRESENCE_DEMO         must be 1/true — the bot-population master switch
  AAEMU_PRESENCE_BOT_COUNT    requested citizen count
  AAEMU_PRESENCE_MAX_BOTS     clamp ceiling for that count
  AAEMU_QUEST_BOOTSTRAP_ENABLED  quest leg arbitration (the emitted rows)
  AAEMU_NEEDS_FARM_ENABLED    needs/farm leg arbitration (the emitted rows)
  AAEMU_JEV_SHADOW_PATH       output JSONL path (or pass --out instead)

SET BY THIS SCRIPT (validated, not inherited)
  AAEMU_JEV_SHADOW=1          inherited by Login and Game; only Game has the writer
  AAEMU_JEV_SHADOW_SAMPLE     default 1 (every eligible wake)
  WebApiNetwork__Port         matches the explicit E2E_WEBAPI_PORT

FLAGS
  --minutes <n>        harvest window in minutes (default 30)
  --sample <n>         AAEMU_JEV_SHADOW_SAMPLE (default 1 = every eligible wake)
  --out <path>         dataset path; relative paths resolve under the game app
                       base directory (<lane>/runtime/game). Must stay inside it.
  --force              allow overwriting an existing NON-EMPTY dataset
  --keep               leave the lane booted after the run (skip teardown)
  --drain-seconds <n>  wait after the graceful stop before reading the file
  --allow-prod-host   explicit opt-in for $prod_root on $PROD_HOST;
                       still requires an explicit project and remapped ports
  --dry-run            verify prerequisites and exit non-zero if the lane is NOT
                       runnable. Runs the same env-contract, output-path and
                       lane-data checks as a real run, then stops: it starts no
                       server, provisions nothing, and writes no dataset.

WHAT IT VERIFIES AFTER A REAL RUN
  the file exists, is non-empty, reports line count + byte size, and its FIRST
  line parses as JSON carrying "schema": "jev-shadow/v3". Anything it cannot verify
  it reports as unverified and exits non-zero.

WHAT --dry-run VERIFIES (BEFORE starting anything)
  the explicit isolated root/project/port contract, no running lane Login/Game,
  no foreign listeners on the required ports (an existing lane DB may remain),
  the environment and output-path contracts, the lane log-cap guard, and that
  <lane-root>/runtime/game-data/Data/compact.sqlite3 matches the canonical md5.
  Any unmet item is listed by name and the exit code is nonzero.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help)   usage; exit 0 ;;
        --minutes)   MINUTES="${2:-}"; shift 2 ;;
        --sample)    SAMPLE="${2:-}"; shift 2 ;;
        --out)       OUT="${2:-}"; shift 2 ;;
        --drain-seconds) DRAIN_SECONDS="${2:-}"; shift 2 ;;
        --force)     FORCE=1; shift ;;
        --keep)      KEEP=1; shift ;;
        --allow-prod-host) ALLOW_PROD_HOST=1; shift ;;
        --dry-run)   DRY_RUN=1; shift ;;
        *) echo "error: unknown argument '$1' (see $0 --help)" >&2; exit 2 ;;
    esac
done

log()  { echo "[jev-harvest] $*"; }
fail() { echo "error: $*" >&2; exit 1; }

[[ -f "$E2E_COMMON" ]] || fail "missing $E2E_COMMON (run from a full repo checkout)"

# ---------------------------------------------------------------- env contract
# Every one of these controls where the servers boot or what population and
# wakes they record. An absent lane/port value must never pick a shared default.
REQUIRED_VARS=(E2E_ROOT COMPOSE_PROJECT_NAME E2E_INTERNAL_PORT E2E_LOGIN_PORT
               E2E_GAME_PORT E2E_STREAM_PORT E2E_WEBAPI_PORT E2E_BRIDGE_PORT
               E2E_DB_PORT DB_HOST_PORT E2E_GAME_HOST E2E_BRIDGE
               AAEMU_PRESENCE_DEMO AAEMU_PRESENCE_BOT_COUNT
               AAEMU_PRESENCE_MAX_BOTS AAEMU_QUEST_BOOTSTRAP_ENABLED
               AAEMU_NEEDS_FARM_ENABLED)
missing=()
for v in "${REQUIRED_VARS[@]}"; do
    [[ -n "${!v:-}" ]] || missing+=("$v")
done
# AAEMU_JEV_SHADOW_PATH is required too, but --out is an equivalent supply.
if [[ -z "${AAEMU_JEV_SHADOW_PATH:-}" && -z "$OUT" ]]; then
    missing+=("AAEMU_JEV_SHADOW_PATH (or --out <path>)")
fi
if [[ ${#missing[@]} -gt 0 ]]; then
    echo "error: refusing to run — missing required environment:" >&2
    for m in "${missing[@]}"; do echo "          - $m" >&2; done
    echo >&2
    echo "Required (all must be exported in this shell):" >&2
    echo "  E2E_ROOT COMPOSE_PROJECT_NAME E2E_GAME_HOST E2E_BRIDGE" >&2
    echo "  E2E_INTERNAL_PORT E2E_LOGIN_PORT E2E_GAME_PORT E2E_STREAM_PORT" >&2
    echo "  E2E_WEBAPI_PORT E2E_BRIDGE_PORT E2E_DB_PORT DB_HOST_PORT" >&2
    echo "  AAEMU_PRESENCE_DEMO AAEMU_PRESENCE_BOT_COUNT AAEMU_PRESENCE_MAX_BOTS" >&2
    echo "  AAEMU_QUEST_BOOTSTRAP_ENABLED AAEMU_NEEDS_FARM_ENABLED" >&2
    echo "  AAEMU_JEV_SHADOW_PATH (or --out)" >&2
    echo >&2
    echo "Nothing was started and nothing was written. See $0 --help." >&2
    exit 2
fi

# Both lane processes inherit this switch; only Game hosts the JevShadow writer.
# Discard an inherited 0 so the Game cannot appear to harvest while flag-off.
export AAEMU_JEV_SHADOW=1
SAMPLE="${SAMPLE:-${AAEMU_JEV_SHADOW_SAMPLE:-1}}"
[[ "$SAMPLE" =~ ^[0-9]+$ ]] || fail "--sample/AAEMU_JEV_SHADOW_SAMPLE must be a positive integer (got '$SAMPLE')"
[[ "$SAMPLE" -ge 1 ]] || fail "--sample/AAEMU_JEV_SHADOW_SAMPLE must be >= 1 (got '$SAMPLE')"
export AAEMU_JEV_SHADOW_SAMPLE="$SAMPLE"
[[ "$COMPOSE_PROJECT_NAME" != e2e ]] || fail "COMPOSE_PROJECT_NAME=e2e is the shared stack; use a distinct isolated project"
[[ "$COMPOSE_PROJECT_NAME" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || fail "COMPOSE_PROJECT_NAME must be a valid isolated compose project"
[[ "$E2E_GAME_HOST" == 127.0.0.1 ]] || fail "E2E_GAME_HOST must be 127.0.0.1 (no remote host)"
[[ "$E2E_BRIDGE" == 1 ]] || fail "E2E_BRIDGE must be 1 for this isolated harvest"

PORT_VARS=(E2E_INTERNAL_PORT E2E_LOGIN_PORT E2E_GAME_PORT E2E_STREAM_PORT
           E2E_WEBAPI_PORT E2E_BRIDGE_PORT E2E_DB_PORT)
declare -A seen_ports=()
for v in "${PORT_VARS[@]}"; do
    port="${!v}"
    [[ "$port" =~ ^[1-9][0-9]{0,4}$ ]] && (( port <= 65535 )) ||
        fail "$v must be an explicit TCP port 1..65535 (got '$port')"
    case "$port" in
        1234|1237|1239|1250|1260|1280|3306) fail "$v=:$port selects a shared/default port" ;;
    esac
    [[ -z "${seen_ports[$port]:-}" ]] || fail "$v=:$port overlaps ${seen_ports[$port]}"
    seen_ports[$port]="$v"
done
[[ "$DB_HOST_PORT" == "$E2E_DB_PORT" ]] ||
    fail "DB_HOST_PORT must equal E2E_DB_PORT so compose and the game use the same isolated DB port"
# Config.Local.json now sets this, and the explicit override wins over any
# inherited WebApiNetwork__Port because Program loads environment after JSON.
export WebApiNetwork__Port="$E2E_WEBAPI_PORT"

case "${AAEMU_PRESENCE_DEMO}" in 1|true|True) ;; *) fail "AAEMU_PRESENCE_DEMO must be 1/true (got '$AAEMU_PRESENCE_DEMO') — no bots, no wakes, no rows" ;; esac
case "${AAEMU_QUEST_BOOTSTRAP_ENABLED}" in 1|true|True) ;; *) fail "AAEMU_QUEST_BOOTSTRAP_ENABLED must be 1/true (got '$AAEMU_QUEST_BOOTSTRAP_ENABLED') — the quest leg is the emitted row" ;; esac
case "${AAEMU_NEEDS_FARM_ENABLED}" in 1|true|True) ;; *) fail "AAEMU_NEEDS_FARM_ENABLED must be 1/true (got '$AAEMU_NEEDS_FARM_ENABLED') — the needs leg is an emitted row" ;; esac
[[ "${AAEMU_PRESENCE_BOT_COUNT}" =~ ^[0-9]+$ ]] || fail "AAEMU_PRESENCE_BOT_COUNT must be a positive integer (got '$AAEMU_PRESENCE_BOT_COUNT')"
[[ "${AAEMU_PRESENCE_MAX_BOTS}"  =~ ^[0-9]+$ ]] || fail "AAEMU_PRESENCE_MAX_BOTS must be a positive integer (got '$AAEMU_PRESENCE_MAX_BOTS')"
[[ "${AAEMU_PRESENCE_BOT_COUNT}" -ge 1 ]] || fail "AAEMU_PRESENCE_BOT_COUNT must be >= 1"
[[ "${AAEMU_PRESENCE_MAX_BOTS}"  -ge 1 ]] || fail "AAEMU_PRESENCE_MAX_BOTS must be >= 1"
[[ "$MINUTES" =~ ^[0-9]+$ ]] || fail "--minutes must be a positive integer (got '$MINUTES')"
[[ "$DRAIN_SECONDS" =~ ^[0-9]+$ ]] || fail "--drain-seconds must be a non-negative integer (got '$DRAIN_SECONDS')"

# Expected citizens: ReadBotCount clamps the request to ReadMaxPresenceBots
# (BotPresenceCoordinator.ClampBotCount). Mirror that here so the pre-flight
# number is what the server will actually provision.
if [[ "${AAEMU_PRESENCE_BOT_COUNT}" -gt "${AAEMU_PRESENCE_MAX_BOTS}" ]]; then
    EXPECTED_BOTS="${AAEMU_PRESENCE_MAX_BOTS}"
else
    EXPECTED_BOTS="${AAEMU_PRESENCE_BOT_COUNT}"
fi

# ---------------------------------------------------------------- lane + output
[[ "$E2E_ROOT" = /* ]] || fail "E2E_ROOT must be an absolute isolated lane directory"
E2E_ROOT="$(realpath -m -- "$E2E_ROOT")"
export E2E_ROOT
case "$E2E_ROOT" in
    /|/root/aaemu-e2e|/root/aaemu-e2e/*)
        fail "E2E_ROOT=$E2E_ROOT selects the shared/default lane; use a distinct root" ;;
esac
case "$E2E_ROOT" in
    "$PROD_ROOT"|"$PROD_ROOT"/*)
        if [[ "$ALLOW_PROD_HOST" -ne 1 ]]; then
            echo "error: refusing to run against the production host." >&2
            echo "         E2E_ROOT=$E2E_ROOT is inside $PROD_ROOT on $PROD_HOST." >&2
            echo "         A harvest arms a per-wake decision recorder and writes rows to disk," >&2
            echo "         so production is opt-in only. Use an isolated lane root, or re-run" >&2
            echo "         with --allow-prod-host to take that decision explicitly." >&2
            exit 2
        fi ;;
esac

APP_BASE="$E2E_ROOT/runtime/game"
OUT="${OUT:-$AAEMU_JEV_SHADOW_PATH}"
[[ "$OUT" = /* ]] || OUT="$APP_BASE/$OUT"
OUT="$(realpath -m -- "$OUT")"
OUT_DIR="$(dirname "$OUT")"
OUT_FILE="$(basename "$OUT")"
export AAEMU_JEV_SHADOW_PATH="$OUT"
case "$OUT" in
    "$APP_BASE"/*) ;;
    *) fail "AAEMU_JEV_SHADOW_PATH must resolve inside the lane app base directory ($APP_BASE/...); got '$OUT'" ;;
esac
[[ "$OUT_FILE" == *.jsonl ]] || fail "AAEMU_JEV_SHADOW_PATH should end in .jsonl (got '$OUT')"

# Refuse to clobber: a previous dataset is a harvest result, not scratch.
if [[ -s "$OUT" ]]; then
    if [[ "$FORCE" -eq 1 ]]; then
        log "WARNING: --force — existing dataset will be appended to/overwritten: $OUT"
    else
        log "error: dataset already exists and is non-empty: $OUT ($(wc -c < "$OUT") bytes)"
        log "       re-run with --force to overwrite it, or pass a distinct --out <path>."
        exit 2
    fi
fi

# ---------------------------------------------------------------- pre-flight
TARGET_HOST_LABEL="local ($(hostname)) — isolated lane, not production"
if [[ "$E2E_ROOT" == "$PROD_ROOT" || "$E2E_ROOT" == "$PROD_ROOT"/* ]]; then
    TARGET_HOST_LABEL="$PROD_HOST — PRODUCTION $PROD_ROOT (--allow-prod-host given; decision data will be written to disk there)"
fi

log "== jev-shadow harvest pre-flight"
log "   repo HEAD      : $(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
log "   target host    : $TARGET_HOST_LABEL"
log "   lane root      : $E2E_ROOT"
log "   app base       : $APP_BASE"
log "   dataset out    : $AAEMU_JEV_SHADOW_PATH"
log "   expected bots  : $EXPECTED_BOTS (requested ${AAEMU_PRESENCE_BOT_COUNT}, clamp ${AAEMU_PRESENCE_MAX_BOTS})"
log "   sampling rate  : every ${AAEMU_JEV_SHADOW_SAMPLE} eligible wake(s) (AAEMU_JEV_SHADOW_SAMPLE)"
log "   harvest window : ${MINUTES} min"
log "   drain wait     : ${DRAIN_SECONDS}s after the graceful stop"

# ---------------------------------------------------------------- lane prep
# Sourced BEFORE the dry-run exit so the prerequisite checks below can read the
# lane paths (CANONICAL_SQLITE etc.). e2e-common.sh's top level is pure
# variable assignment plus function definitions — it starts nothing and writes
# nothing — so sourcing it in a dry run is read-only.
# shellcheck source=/dev/null
source "$E2E_COMMON"

# ---- prerequisite checks (run for BOTH the dry run and the real run) -------
#
# These used to sit after the --dry-run exit, so a dry run reported success on a
# lane that could not possibly boot: it printed the env contract and exited 0,
# and the hard stop only appeared minutes later inside e2e_prepare. A dry run
# that says "ready" when the lane is unrunnable is worse than no dry run, so the
# checks now run first and --dry-run exits on their result.
PREREQ_MISSING=()
# Never adopt lane servers: they may lack our writer flags, and teardown must
# not terminate a process this invocation did not start.
for name in Login Game; do
    existing="$(e2e_find_proc "$name")"
    [[ -z "$existing" ]] ||
        PREREQ_MISSING+=("lane $name already running (pid(s) $existing); stop it before harvesting")
done

# The common boot helper permits owned servers; a harvest does not. Check
# listeners independently of process names, including WebAPI (SO_REUSEPORT
# would otherwise allow a second process to bind the same port).
for v in E2E_INTERNAL_PORT E2E_LOGIN_PORT E2E_GAME_PORT E2E_STREAM_PORT \
         E2E_WEBAPI_PORT E2E_BRIDGE_PORT; do
    if [[ -n "$(ss -H -ltn "( sport = :${!v} )")" ]]; then
        PREREQ_MISSING+=("$v=:${!v} already has a listener; harvest requires a free isolated port")
    fi
done

# An existing lane DB is safe to reuse only if its container belongs to this
# compose project, publishes the requested port, and uses this lane's .env.
db_ids="$(docker ps -aq --filter "label=com.docker.compose.project=$COMPOSE_PROJECT_NAME" \
    --filter "label=com.docker.compose.service=db" 2>/dev/null || true)"
if [[ -n "$db_ids" ]]; then
    if [[ "$db_ids" == *$'\n'* || ! -f "$ENV_FILE" ]]; then
        PREREQ_MISSING+=("compose project $COMPOSE_PROJECT_NAME has a DB container not attributable to this lane")
    elif [[ "$(docker port "$db_ids" 3306/tcp 2>/dev/null)" != "127.0.0.1:$E2E_DB_PORT" ]]; then
        PREREQ_MISSING+=("compose project $COMPOSE_PROJECT_NAME DB does not publish isolated port :$E2E_DB_PORT")
    else
        db_password="$(sed -n 's/^DB_PASSWORD=//p' "$ENV_FILE")"
        if [[ -z "$db_password" ]] ||
           ! docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$db_ids" |
               grep -Fxq "MYSQL_ROOT_PASSWORD=$db_password"; then
            PREREQ_MISSING+=("compose project $COMPOSE_PROJECT_NAME DB credentials do not match this lane")
        fi
    fi
fi
if [[ -n "$(ss -H -ltn "( sport = :$E2E_DB_PORT )")" && -z "$db_ids" ]]; then
    PREREQ_MISSING+=("E2E_DB_PORT=:$E2E_DB_PORT has a foreign listener")
fi


# e2e_prepare requires the lane-scoped log-cap guard; a brand-new lane has none.
if [[ ! -f "$E2E_ROOT/ensure-log-caps.sh" ]]; then
    donor=""
    for d in /root/aaemu-e2e /root/aaemu-e2e-a5-tier3-sixhour; do
        [[ -f "$d/ensure-log-caps.sh" ]] && { donor="$d"; break; }
    done
    if [[ -z "$donor" ]]; then
        PREREQ_MISSING+=("lane has no ensure-log-caps.sh and no donor lane was found; copy one from an existing lane: cp <donor>/ensure-log-caps.sh $E2E_ROOT/")
    elif [[ "$DRY_RUN" -eq 1 ]]; then
        # Dry run must WRITE NOTHING, so it reports the self-heal it WOULD do
        # instead of doing it.
        PREREQ_MISSING+=("lane has no ensure-log-caps.sh; the real run self-provisions it from $donor (no action needed for a real run)")
    else
        mkdir -p "$E2E_ROOT"
        cp "$donor/ensure-log-caps.sh" "$E2E_ROOT/ensure-log-caps.sh"
        log "provisioned ensure-log-caps.sh into the lane from $donor"
    fi
fi

# Canonical game data. This is THE prerequisite that blocked the first harvest
# attempt: a brand-new lane has an empty runtime/game-data, and the old code only
# logged a NOTE here and deferred the hard stop to e2e_prepare. It is now a
# first-class failure in BOTH modes, with the remediation named.
if [[ -f "$CANONICAL_SQLITE" ]]; then
    md5="$(md5sum "$CANONICAL_SQLITE" | cut -d' ' -f1)"
    if [[ "$md5" == "$CANONICAL_SQLITE_MD5" ]]; then
        log "canonical game data md5 verified ($md5)"
    else
        PREREQ_MISSING+=("canonical compact.sqlite3 md5 mismatch (got $md5, want $CANONICAL_SQLITE_MD5) at $CANONICAL_SQLITE — refusing to harvest against drifted game data; re-provision from a canonical lane")
    fi
else
    PREREQ_MISSING+=("canonical game data absent: $CANONICAL_SQLITE
       provision it from an existing LOCAL lane (no .165 contact needed):
         Scripts/e2e/e2e-boot.sh --clone-data-from /root/aaemu-e2e
       or point E2E_ROOT at a lane that already has a game-data tree")
fi

if [[ ${#PREREQ_MISSING[@]} -gt 0 ]]; then
    echo "error: lane is NOT runnable — ${#PREREQ_MISSING[@]} unmet prerequisite(s):" >&2
    for m in "${PREREQ_MISSING[@]}"; do
        if [[ "$m" == *$'\n'* ]]; then
            printf '       - %s\n' "$m" >&2
        else
            echo "       - $m" >&2
        fi
    done
    echo >&2
    echo "Nothing was started and nothing was written. Fix the above, then re-run." >&2
    if [[ "$DRY_RUN" -eq 1 ]]; then
        echo "(--dry-run: this is the check that previously reported success.)" >&2
    fi
    exit 2
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
    log "--dry-run: all prerequisites present. Nothing started, nothing written."
    exit 0
fi

# Pre-create the output directory so an unwritable path fails HERE, loudly,
# instead of being swallowed by the writer thread's fault-tolerant append.
mkdir -p "$OUT_DIR"

log "booting lane MySQL -> Login -> Game (both inherit AAEMU_JEV_SHADOW=1; only Game has the writer)"
e2e_prepare
e2e_boot_db
[[ -z "$(e2e_find_proc Login)" && -z "$(e2e_find_proc Game)" ]] ||
    fail "lane Login/Game appeared during preparation; refusing to adopt or terminate it"
e2e_boot_servers

game_pid="$(e2e_find_proc Game | head -1)"
[[ -n "$game_pid" ]] || fail "game server process not found under $RUNTIME_DIR after boot"
log "game server up: pid=$game_pid cwd=$GAME_DIR"

# ---------------------------------------------------------------- harvest window
log "harvesting for ${MINUTES} min — Ctrl-C to stop early (the dataset is still verified)"
sleep_deadline=$(( $(date +%s) + MINUTES * 60 ))
while [[ "$(date +%s)" -lt "$sleep_deadline" ]]; do
    sleep 15
    [[ -e "/proc/$game_pid" ]] || fail "game server exited early (pid $game_pid gone); see $LOG_DIR/game.log — dataset NOT verified"
done

bytes_mid=0
[[ -f "$AAEMU_JEV_SHADOW_PATH" ]] && bytes_mid="$(wc -c < "$AAEMU_JEV_SHADOW_PATH")"
log "window elapsed; dataset currently ${bytes_mid} bytes (may still grow)"

# ---------------------------------------------------------------- graceful stop + drain
#
# JevShadow writes from a BACKGROUND thread ("JevShadowWriter", IsBackground=true):
# a sampled wake only enqueues its serialized line on a bounded queue (1024,
# drop-newest when full), and the writer thread appends one line per item with
# File.AppendAllText. There is no flush join on ANY shutdown path — the writer is
# best-effort by design.
#
# So the harvest must NOT read the file while the writer is mid-flight: a
# partially-written tail and lines still queued at exit are indistinguishable
# from "harvest finished". The order below is therefore mandatory:
#   1. SIGTERM the game server so GameService.StopAsync runs (saves, stops the
#      scheduler, no further wakes are arbitrated -> no new rows are enqueued);
#   2. WAIT for the process to actually exit — only then is the writer gone and
#      the file final;
#   3. WAIT the drain window anyway, so any final appends settle on the volume
#      before the file is read;
#   4. only then count/validate.
# Lines that were queued but never written are lost by design and drops are not
# recorded in the file: the line count below is a LOWER bound on what the lane
# arbitrated, never proof that every eligible wake was captured.
log "stopping game server gracefully (SIGTERM) so the writer can settle"
kill -TERM "$game_pid" 2>/dev/null || true
stopped=0
for _ in $(seq 1 120); do
    if [[ ! -e "/proc/$game_pid" ]]; then stopped=1; break; fi
    sleep 1
done
if [[ "$stopped" -eq 0 ]]; then
    log "game did not exit within 120s — forcing (the dataset may be truncated)"
    kill -9 "$game_pid" 2>/dev/null || true
    sleep 2
else
    log "game server exited cleanly"
fi

log "draining ${DRAIN_SECONDS}s before reading the dataset"
sleep "$DRAIN_SECONDS"

# ---------------------------------------------------------------- teardown
if [[ "$KEEP" -eq 1 ]]; then
    log "--keep: lane left running (login + MySQL still up); stop it yourself with Scripts/e2e/e2e-reset.sh"
else
    log "tearing down lane servers (PID-scoped, never pkill)"
    e2e_kill_server Login
    e2e_compose down >/dev/null 2>&1 || log "compose down reported a non-zero exit (lane containers may already be gone)"
fi

# ---------------------------------------------------------------- VERIFY
# Every claim below is measured from the file on disk. Nothing is inferred.
echo
log "== verification"
if [[ ! -f "$AAEMU_JEV_SHADOW_PATH" ]]; then
    log "UNVERIFIED: no dataset at $AAEMU_JEV_SHADOW_PATH"
    log "  The flag was armed, so an absent file means no eligible wake was"
    log "  arbitrated (or the writer faulted and swallowed it — JevShadow"
    log "  swallows every append fault by design). Check $LOG_DIR/game.log"
    log "  for 'presence demo up — N/N citizen bots roaming' before re-running."
    exit 1
fi

bytes="$(wc -c < "$AAEMU_JEV_SHADOW_PATH")"
if [[ "$bytes" -eq 0 ]]; then
    log "UNVERIFIED: dataset exists but is EMPTY ($AAEMU_JEV_SHADOW_PATH)"
    exit 1
fi
# Rows are appended one-per-line, so only newline-terminated lines are complete
# records. A tail without its trailing newline is a torn write (the writer was
# killed mid-append) — counting it would overstate the harvest.
lines="$(awk 'END{print NR}' "$AAEMU_JEV_SHADOW_PATH")"
if [[ "$bytes" -gt 0 && "$(tail -c 1 "$AAEMU_JEV_SHADOW_PATH" | wc -l)" -eq 0 ]]; then
    complete=$(( lines - 1 ))
    torn=1
    log "   WARNING: file does not end in a newline — the last line is a torn write"
else
    complete="$lines"
    torn=0
fi
log "   path       : $AAEMU_JEV_SHADOW_PATH"
log "   rows       : $complete complete row(s)"
log "   bytes      : $bytes"
log "   bots       : $EXPECTED_BOTS expected, ${MINUTES} min window, every ${AAEMU_JEV_SHADOW_SAMPLE} wake(s)"

if [[ "$complete" -eq 0 ]]; then
    log "UNVERIFIED: dataset has no complete (newline-terminated) rows"
    exit 1
fi

if ! command -v jq >/dev/null 2>&1 && ! command -v python3 >/dev/null 2>&1; then
    log "UNVERIFIED: neither jq nor python3 is available to parse the first line"
    exit 1
fi

first_line="$(head -n 1 "$AAEMU_JEV_SHADOW_PATH")"
if [[ -z "$first_line" ]]; then
    log "UNVERIFIED: first line is empty"
    exit 1
fi
if command -v jq >/dev/null 2>&1; then
    # EVERY row is schema-checked, not just the first: a dataset that opens with
    # a v3 row and then carries older rows (an append onto a historical file, or
    # a lane that restarted against a different build) would otherwise pass a
    # first-line check while mixing two different candidate semantics.
    if ! printf '%s' "$first_line" | jq -e . >/dev/null 2>&1; then
        log "UNVERIFIED: first line is not parseable JSON:"
        printf '%s\n' "${first_line:0:400}" >&2
        exit 1
    fi
    # `x="$(cmd)"` ABORTS under `set -e` when cmd exits non-zero, with no
    # message — a first line that is valid JSON but carries no `schema` made
    # `jq -er` exit 5 and killed the run silently instead of reporting
    # UNVERIFIED. The `|| __UNPARSEABLE__` fallback keeps the report.
    got_schema="$(printf '%s' "$first_line" | jq -er '.schema' 2>/dev/null)" || got_schema="__UNPARSEABLE__"
    if [[ "$got_schema" != "$SCHEMA" ]]; then
        log "UNVERIFIED: dataset schema is '$got_schema', expected '$SCHEMA'"
        log "  (every complete row must carry \"schema\":\"$SCHEMA\")"
        log "  A jev-shadow/v1 or /v2 dataset is HISTORICAL and must not be"
        log "   appended to or produced by this v3 harvest: neither version"
        log "   carries the v3 target_id identity field, and v1's"
        log "   candidate_count means something different. Use a fresh --out."
        log "   (The offline reviewer reads historical v2 on request via its"
        log "   --historic flag; that is separate from harvesting.)"
        exit 1
    fi
    # Same `set -e` hazard as above: a MALFORMED row makes jq exit non-zero,
    # which aborted the script SILENTLY (exit 5, no output). `|| true` on the
    # assignment keeps the run alive so `malformed` below can report it.
    wrong_schema="$(jq -r --arg s "$SCHEMA" \
        'select(type != "object" or .schema != $s) | .schema // "<none>"' \
        "$AAEMU_JEV_SHADOW_PATH" 2>/dev/null | head -3 || true)"
    malformed="$(jq -c . "$AAEMU_JEV_SHADOW_PATH" >/dev/null 2>&1 && echo 0 || echo 1)"
    if [[ "$malformed" != 0 ]]; then
        log "UNVERIFIED: dataset has malformed (unparseable) row(s); trim them before reviewing"
        exit 1
    fi
    if [[ -n "$wrong_schema" ]]; then
        log "UNVERIFIED: mixed-schema dataset — these row schema value(s) are not '$SCHEMA':"
        printf '       - %s\n' $wrong_schema >&2
        exit 1
    fi
    log "   schema     : $SCHEMA (all $complete complete row(s) verified)"
else
    probe="$(printf '%s' "$first_line" | python3 -c 'import json,sys
try:
    d = json.loads(sys.stdin.read())
    print(d.get("schema", ""))
except Exception:
    print("__PARSE_ERROR__")')"
    if [[ "$probe" == "__PARSE_ERROR__" ]]; then
        log "UNVERIFIED: first line is not parseable JSON:"
        printf '%s\n' "${first_line:0:400}" >&2
        exit 1
    fi
    got_schema="$probe"
    if [[ "$got_schema" != "$SCHEMA" ]]; then
        log "UNVERIFIED: dataset schema is '$got_schema', expected '$SCHEMA'"
        log "  (a jev-shadow/v1 or /v2 dataset is historical: not appendable to this v3 harvest)"
        exit 1
    fi
    # No jq: python validates EVERY complete row, so the guarantee matches.
    wrong_schema="$(python3 - "$AAEMU_JEV_SHADOW_PATH" "$SCHEMA" <<'PY' 2>/dev/null | head -3
import json, sys
path, want = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as fh:
    for line in fh:
        line = line.strip()
        if not line:
            continue
        try:
            row = json.loads(line)
        except Exception:
            continue
        if not isinstance(row, dict) or row.get("schema") != want:
            print(row.get("schema", "<none>") if isinstance(row, dict) else "<not-an-object>")
PY
)"
    if [[ -n "$wrong_schema" ]]; then
        log "UNVERIFIED: mixed-schema dataset — these row schema value(s) are not '$SCHEMA':"
        printf '       - %s\n' $wrong_schema >&2
        exit 1
    fi
    malformed=0
    log "   schema     : $SCHEMA (all $complete complete row(s) verified)"
fi
log "   torn rows   : $torn (trailing incomplete line(s))"
log "   malformed   : $malformed (whole-file JSON parse check, every row)"

log "HARVEST VERIFIED: $complete complete row(s), $bytes byte(s), schema=$SCHEMA at $AAEMU_JEV_SHADOW_PATH"
if [[ "$torn" -ne 0 || "$malformed" != 0 ]]; then
    log "  (torn=$torn, malformed=$malformed — trim the trailing incomplete line and any"
    log "   unparseable rows before reviewing; the count above excludes them)"
fi
log "Evidence layer: rig-tier, isolated lane. Synthetic bot arbitration records —"
log "NOT proof of live human playability and NOT proof of bot autonomy (H stays UNKNOWN)."