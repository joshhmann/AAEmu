#!/bin/bash
# e2e-common.sh — shared helpers for the M2b-E2E boot/reset control surface.
#
# Layout mirrors AAEmu.IntegrationTests/E2e/E2eStack.cs EXACTLY, so the shell
# scripts and the test-runner stack are interchangeable:
#   E2E_ROOT/runtime/login      published AAEmu.Login + configs
#   E2E_ROOT/runtime/game       published AAEmu.Game + Data copy + ClientData symlink
#   E2E_ROOT/runtime/game-data  canonical data (rsync from the aaemu box once)
#   E2E_ROOT/logs/{login,game}.log
#   E2E_ROOT/.env               DB_PASSWORD (generated once, never regenerated)
#
# Do not edit Config.Local.json generation here AND in E2eStack.cs — keep the
# two in sync (search for GameLocalConfig/LoginLocalConfig).

set -euo pipefail

E2E_ROOT="${E2E_ROOT:-/root/aaemu-e2e}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.yaml"
COMPOSE_PROJECT="e2e"

ENV_FILE="$E2E_ROOT/.env"
LOG_DIR="$E2E_ROOT/logs"
PID_DIR="$E2E_ROOT/pids"
STATE_DIR="$E2E_ROOT/state"
RUNTIME_DIR="$E2E_ROOT/runtime"
LOGIN_DIR="$RUNTIME_DIR/login"
GAME_DIR="$RUNTIME_DIR/game"
GAME_DATA_DIR="$RUNTIME_DIR/game-data"
CANONICAL_SQLITE="$GAME_DATA_DIR/Data/compact.sqlite3"
RUNTIME_SQLITE="$GAME_DIR/Data/compact.sqlite3"

PORT_LOGIN=1237      # login client-facing
PORT_LOGIN_INT=1234  # login <-> game internal
PORT_GAME=1239
PORT_STREAM=1250
PORT_BRIDGE=1260     # BotDriveBridge (game process, E2E-2 scope — OFF by default)

# E2E_BRIDGE=1 opts into the BotDriveBridge (port 1260): the game config gets
# the Bots section and boot waits for the bridge. Default off — this card
# (boot orchestration) must work against the base repo build; E2E-2 flips it.
E2E_BRIDGE="${E2E_BRIDGE:-0}"

DB_PASSWORD=""

e2e_log()  { echo "[e2e] $*"; }
e2e_fail() { echo "[e2e] ERROR: $*" >&2; exit 1; }

# ---------------------------------------------------------------- env

# Ensures .env exists; returns 0 if it was JUST created (caller should then
# wipe any stale MySQL volume so the new password takes effect).
e2e_ensure_env() {
    mkdir -p "$E2E_ROOT"
    if [ ! -f "$ENV_FILE" ]; then
        echo "DB_PASSWORD=e2e_$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')" > "$ENV_FILE"
        chmod 600 "$ENV_FILE"
        DB_PASSWORD="$(sed -n 's/^DB_PASSWORD=//p' "$ENV_FILE")"
        e2e_log "generated $ENV_FILE (keep it — it is the MySQL root password for this stack)"
        return 0
    fi
    DB_PASSWORD="$(sed -n 's/^DB_PASSWORD=//p' "$ENV_FILE")"
    [ -n "$DB_PASSWORD" ] || e2e_fail "$ENV_FILE is missing DB_PASSWORD"
    return 1
}

e2e_db_volume_exists() { docker volume ls -q 2>/dev/null | grep -qx "$COMPOSE_PROJECT""_db_data"; }

# ---------------------------------------------------------------- compose

e2e_compose() { docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }

e2e_db_running() { [ -n "$(e2e_compose ps -q db 2>/dev/null)" ]; }

e2e_db_up() {
    e2e_compose up -d db
    e2e_log "waiting for MySQL (seed-complete, not just ping) ..."
    local i
    for i in $(seq 1 120); do
        # Query a seeded table over TCP — mysqladmin ping alone succeeds during
        # the entrypoint's temp-server init phase and Login then connects into
        # the seed window and dies ("Reading from the stream has failed").
        # This check only passes once seeds are applied on the real server.
        if e2e_compose exec -T db mysql -h 127.0.0.1 -u root -p"$DB_PASSWORD" -N \
            -e "SELECT COUNT(*) FROM aaemu_login.users LIMIT 1" >/dev/null 2>&1; then
            e2e_log "MySQL healthy (aaemu_login/aaemu_game seeded from SQL/)"
            return 0
        fi
        sleep 2
    done
    e2e_fail "MySQL seed did not complete — see: docker compose -p $COMPOSE_PROJECT -f $COMPOSE_FILE logs db"
}

# ---------------------------------------------------------------- processes

# Pids of e2e server processes: a REAL dotnet host (argv[0] = dotnet) whose
# args name the dll, with cwd under runtime/. cwd tolerance: a runtime dir
# wiped while the process lived resolves as "... (deleted)" — strip the
# suffix so those orphans are still found and killed by the preflight
# instead of being invisible to it (2026-08-07 incident: a cwd-deleted
# instance escaped e2e_kill_server and double-bound :1239).
e2e_find_proc() {
    local name="$1" pid cwd cmd argv0
    for d in /proc/[0-9]*; do
        pid="${d#/proc/}"
        [ -r "/proc/$pid/cmdline" ] || continue
        cmd="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null)"
        case "$cmd" in
            *"AAEmu.${name}.dll"*) ;;
            *) continue ;;
        esac
        argv0="${cmd%% *}"
        case "${argv0##*/}" in dotnet) ;; *) continue ;; esac
        cwd="$(readlink "/proc/$pid/cwd" 2>/dev/null || true)"
        cwd="${cwd% (deleted)}"
        case "$cwd" in "$RUNTIME_DIR"/*) echo "$pid" ;; esac
    done
}

# Owning pids of a listening TCP port (empty when free).
e2e_port_owner() {
    local port="$1"
    ss -ltnp 2>/dev/null | awk -v p="$port" '
        $4 ~ (":" p "$") {
            for (i = 1; i <= NF; i++)
                if ($i ~ /pid=/) { split($i, a, "pid="); sub(/[^0-9].*$/, "", a[2]); print a[2] }
        }'
}

# True when the pid is a REAL dotnet-hosted AAEmu server process: argv[0] is
# the dotnet host AND some argument names AAEmu.Game.dll / AAEmu.Login.dll.
# Deliberately strict — a shell whose cmdline merely CONTAINS the dll name
# (e.g. a monitoring `pgrep -af AAEmu.Game.dll`) must never be mistaken for
# a server: the first sweep draft did exactly that and killed its own caller
# mid-boot (found in testing, 2026-08-07).
e2e_is_aaemu_proc() {
    local pid="$1" argv0 a
    [ -r "/proc/$pid/cmdline" ] || return 1
    argv0="$(head -c 512 "/proc/$pid/cmdline" 2>/dev/null | tr '\0' ' ' | awk '{print $1}')"
    case "${argv0##*/}" in dotnet) ;; *) return 1 ;; esac
    # any later arg may name the dll (relative or absolute path)
    tr '\0' '\n' < "/proc/$pid/cmdline" 2>/dev/null | tail -n +2 | grep -qE 'AAEmu\.(Game|Login)\.dll' || return 1
    return 0
}

# Kill one pid (TERM, KILL after a 2s grace) with a log line.
e2e_kill_pid() {
    local pid="$1" why="$2"
    e2e_log "killing stale pid $pid ($why)"
    kill "$pid" 2>/dev/null || true
    sleep 2
    if kill -0 "$pid" 2>/dev/null; then
        e2e_log "force-killing pid $pid ($why)"
        kill -9 "$pid" 2>/dev/null || true
    fi
}

# The port list each service is expected to own (bridge conditional).
e2e_ports_of() {
    local name="$1"
    case "$name" in
        Login) echo "$PORT_LOGIN_INT $PORT_LOGIN" ;;
        Game)  echo "$PORT_GAME $PORT_STREAM" $([ "$E2E_BRIDGE" = 1 ] && echo "$PORT_BRIDGE") ;;
    esac
}

# Preflight port sweep (2026-08-07 incident: a second e2e-boot.sh booted
# while a crashed worker's game instance still lived; the port-fight loser
# error-looped ~4GB/min into Logs/Error.log → 287GB disk). Guarantee: a
# boot never proceeds while a stale instance survives.
#  - port held by a HEALTHY complete e2e server -> adopted (existing behavior)
#  - port held by any AAEmu/dotnet server proc  -> killed (stale, logged)
#  - port held by anything else                 -> hard fail (foreign service;
#    we never kill a process we cannot identify as AAEmu)
e2e_preflight_sweep() {
    local port name owner pid
    for port in "$PORT_LOGIN_INT" "$PORT_LOGIN" "$PORT_GAME" "$PORT_STREAM" $([ "$E2E_BRIDGE" = 1 ] && echo "$PORT_BRIDGE"); do
        case "$port" in
            "$PORT_LOGIN_INT"|"$PORT_LOGIN") name=Login ;;
            *) name=Game ;;
        esac
        owner="$(e2e_port_owner "$port")"
        [ -z "$owner" ] && continue
        for pid in $owner; do
            if e2e_find_proc "$name" | grep -qx "$pid" \
                && e2e_server_ready "$name" $(e2e_ports_of "$name"); then
                e2e_log "port :$port owned by healthy $name pid $pid — adopting"
                continue
            fi
            if e2e_is_aaemu_proc "$pid"; then
                e2e_kill_pid "$pid" "stale AAEmu instance holding :$port"
            else
                e2e_fail "port :$port is held by foreign pid $pid (not AAEmu) — refusing to kill an unknown service. Free it or run e2e-reset.sh"
            fi
        done
    done
}

# Keep at most ONE healthy instance per service; every other AAEmu server
# process of that service dies. Directly enforces "two e2e-boot.sh
# invocations never leave two game instances bound to :1239" — catches
# duplicates the port sweep cannot see (crash-loopers between binds).
e2e_dedupe_servers() {
    local name keep pid
    for name in Login Game; do
        keep=""
        if e2e_server_ready "$name" $(e2e_ports_of "$name"); then
            keep="$(e2e_find_proc "$name" | head -1)"
        fi
        for pid in $(e2e_find_proc "$name"); do
            [ "$pid" = "$keep" ] && continue
            e2e_kill_pid "$pid" "duplicate $name instance (healthy keep: ${keep:-none})"
        done
    done
}

# Process guard: stray AAEmu.Game/AAEmu.Login server processes (argv[0] =
# dotnet, dll arg, ANY cwd — no runtime-dir requirement) — catches orphans
# whose runtime dir was deleted. The one healthy kept instance per service
# is never touched. Strict matcher: monitoring shells whose cmdline merely
# mentions the dll name are never killed.
e2e_sweep_strays() {
    local d pid cmd svc
    for d in /proc/[0-9]*; do
        pid="${d#/proc/}"
        e2e_is_aaemu_proc "$pid" || continue
        cmd="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null)"
        case "$cmd" in
            *"AAEmu.Game.dll"*)  svc=Game ;;
            *"AAEmu.Login.dll"*) svc=Login ;;
            *) continue ;;
        esac
        if e2e_server_ready "$svc" $(e2e_ports_of "$svc") \
            && [ "$pid" = "$(e2e_find_proc "$svc" | head -1)" ]; then
            continue   # the healthy kept instance
        fi
        e2e_kill_pid "$pid" "stray $svc process (cmdline: ${cmd:0:80})"
    done
}

# Server is up AND owns its expected ports (stale procs fail this).
e2e_server_ready() {
    local name="$1"; shift
    local pid port
    pid="$(e2e_find_proc "$name" | head -1)"
    [ -n "$pid" ] || return 1
    for port in "$@"; do
        e2e_port_owner "$port" | grep -qx "$pid" || return 1
    done
    return 0
}

e2e_wait_tcp() {
    local port="$1" timeout="$2" what="$3" log="${4:-}" svc="${5:-}"
    local deadline=$(( $(date +%s) + timeout ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        if timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port" 2>/dev/null; then
            e2e_log "$what (:$port) is up"
            return 0
        fi
        # disk-fill guard: a server writing >1GiB while we wait is an error
        # loop (2026-08-07: ~4GB/min) — kill it and fail fast instead of
        # waiting out the full timeout and filling the disk.
        if [ -n "$svc" ] && e2e_log_growth_breach "$svc"; then
            e2e_kill_server "$([ "$svc" = login ] && echo Login || echo Game)"
            e2e_fail "$what (:$port) did not open and $svc logs exceeded 1GiB — error loop detected, server killed (disk-fill guard)"
        fi
        sleep 1
    done
    if [ -n "$log" ] && [ -f "$log" ]; then
        e2e_log "$what (:$port) did not open within ${timeout}s — last log lines:"
        tail -n 25 "$log" >&2
    fi
    e2e_fail "$what (:$port) did not open within ${timeout}s"
}

# ---------------------------------------------------------------- log guard

# True when any log under the service's runtime Logs dir or the boot log dir
# exceeds 1GiB — i.e. the server is error-looping (disk-fill guard).
e2e_log_growth_breach() {
    local svc="$1"
    find "$RUNTIME_DIR/$svc/Logs" "$LOG_DIR" -type f -name '*.log' -size +1G 2>/dev/null | grep -q .
}

# Truncate any log > 1GiB under the runtime layout and boot log dir (cheap
# belt: a misbehaving run degrades to annoyance, not disk-fill). Called by
# the preflight after the stale-process sweep and on EXIT of every script
# that sources this file — so even a FAILED run leaves no >1GiB log behind.
e2e_log_guard() {
    local f size
    while IFS= read -r f; do
        size="$(stat -c %s "$f" 2>/dev/null || echo '?')"
        e2e_log "log guard: truncating ${size}B $f"
        : > "$f" 2>/dev/null || e2e_log "log guard: WARNING could not truncate $f"
    done < <(find "$RUNTIME_DIR" "$LOG_DIR" -type f -name '*.log' -size +1G 2>/dev/null)
}
trap 'e2e_log_guard' EXIT
# NOTE: sourcing this file installs the EXIT log-guard trap (bash keeps one
# EXIT trap — the last one wins). Callers that need their own EXIT trap must
# install it AFTER sourcing, or the log guard silently replaces it.

e2e_kill_server() {
    local name="$1" pid
    for pid in $(e2e_find_proc "$name"); do
        e2e_log "stopping $name pid $pid"
        kill "$pid" 2>/dev/null || true
    done
    sleep 2
    for pid in $(e2e_find_proc "$name"); do
        e2e_log "force-stopping $name pid $pid"
        kill -9 "$pid" 2>/dev/null || true
    done
}

e2e_start_server() {
    local name="$1" dir="$2" dll="$3" log="$4"
    mkdir -p "$LOG_DIR" "$PID_DIR"
    : > "$log"   # fresh log per start (same semantics as the test-runner)
    ( cd "$dir" && exec dotnet "$dll" ) >>"$log" 2>&1 </dev/null &
    local pid="$!"
    echo "$pid" > "$PID_DIR/$name.pid"
    e2e_log "$name started pid=$pid log=$log"
}

# ---------------------------------------------------------------- config

# Verbatim mirrors of E2eStack.GameLocalConfig() / LoginLocalConfig().
e2e_write_login_config() {
    mkdir -p "$LOGIN_DIR"
    cat > "$LOGIN_DIR/Config.Local.json" <<EOF
{
  "InternalNetwork": { "Host": "*", "Port": 1234 },
  "Network": { "Host": "*", "Port": 1237, "NumConnections": 10 },
  "Connections": {
    "MySQLProvider": {
      "Host": "127.0.0.1", "Port": "3306", "User": "root",
      "Password": "${DB_PASSWORD}", "Database": "aaemu_login"
    }
  },
  "GameServers": [
    { "Id": 1, "Name": "AAEmu.Game (e2e)", "Host": "127.0.0.1", "Port": 1239 }
  ]
}
EOF
}

e2e_write_game_config() {
    mkdir -p "$GAME_DIR"
    cat > "$GAME_DIR/Config.Local.json" <<EOF
{
  "Network": { "Host": "*", "Port": 1239, "NumConnections": 10 },
  "StreamNetwork": { "Host": "*", "Port": 1250 },
  "LoginNetwork": { "Host": "127.0.0.1", "Port": "1234" },
  "Connections": {
    "MySQLProvider": {
      "Host": "127.0.0.1", "Port": "3306", "User": "root",
      "Password": "${DB_PASSWORD}", "Database": "aaemu_game"
    }
  },
  "ClientData": { "Sources": [ "./ClientData/game_pak" ] },
  "HeightMapsEnable": true
EOF
    if [ "$E2E_BRIDGE" = 1 ]; then
        cat >> "$GAME_DIR/Config.Local.json" <<EOF
  ,
  "Bots": { "EnableE2EBridge": true, "E2EBridgePort": 1260 }
EOF
    fi
    cat >> "$GAME_DIR/Config.Local.json" <<EOF
}
EOF
}

# Assemble the runtime layout (idempotent, mirrors E2eStack.EnsureRuntimeLayout:
# Data is copied once, ClientData is a symlink, configs only-if-missing, and
# Config.Local.json is ALWAYS rewritten with the current DB password).
e2e_provision_layout() {
    mkdir -p "$LOGIN_DIR" "$GAME_DIR" "$LOG_DIR" "$PID_DIR" "$STATE_DIR"

    [ -f "$LOGIN_DIR/Config.json" ] || cp "$REPO_ROOT/AAEmu.Login/Config.json" "$LOGIN_DIR/Config.json"

    if [ ! -f "$RUNTIME_SQLITE" ]; then
        e2e_log "copying canonical Data -> runtime/game/Data (one-time)"
        cp -r "$GAME_DATA_DIR/Data" "$GAME_DIR/Data"
    fi

    if [ ! -L "$GAME_DIR/ClientData" ] || [ ! -d "$GAME_DIR/ClientData" ]; then
        rm -rf "$GAME_DIR/ClientData"
        ln -s "$GAME_DATA_DIR/ClientData" "$GAME_DIR/ClientData"
        e2e_log "ClientData symlinked (16GB pak not duplicated)"
    fi

    [ -d "$GAME_DIR/Configurations" ] || cp -r "$GAME_DATA_DIR/Configurations" "$GAME_DIR/Configurations"
    [ -f "$GAME_DIR/Config.json" ]    || cp "$GAME_DATA_DIR/Config.json" "$GAME_DIR/Config.json"

    e2e_write_login_config
    e2e_write_game_config
}

# ---------------------------------------------------------------- baseline

# md5 comparison canonical vs runtime sqlite. Returns 0 on byte-identical.
e2e_baseline_report() {
    local c r
    c="$(md5sum "$CANONICAL_SQLITE" | cut -d' ' -f1)"
    r="$(md5sum "$RUNTIME_SQLITE" | cut -d' ' -f1)"
    e2e_log "canonical sqlite md5: $c"
    e2e_log "runtime   sqlite md5: $r"
    if [ "$c" = "$r" ]; then
        e2e_log "sqlite baseline: byte-identical MATCH"
        return 0
    fi
    e2e_log "sqlite baseline: DIVERGED (expected after a defect-rig/E2E run — restore with e2e-reset.sh)"
    return 1
}

# Deterministic MySQL dump hash of the two e2e databases (seeded state).
# Credentials via MYSQL_PWD env (-e), never on argv; --skip-dump-date keeps
# the dump byte-stable across cycles. NOTE: without credentials mysqldump
# exits 1045 Access denied — which under `set -e` kills the whole script.
e2e_mysql_dump_hash() {
    e2e_compose exec -T -e MYSQL_PWD="$DB_PASSWORD" db mysqldump -u root \
        --skip-dump-date --databases aaemu_login aaemu_game 2>/dev/null \
        | sha256sum | cut -d' ' -f1
}

# ---------------------------------------------------------------- boot phases

# Phase 0: env + canonical data + port conflicts + binaries + layout.
# Idempotent — safe to re-run; no processes started.
e2e_prepare() {
    local created=0
    if e2e_ensure_env; then created=1; fi

    # canonical data must exist (rsync from the box once)
    if [ ! -f "$CANONICAL_SQLITE" ]; then
        e2e_fail "canonical data missing at $CANONICAL_SQLITE — provision once with:" \
            "rsync -a root@192.168.0.165:/root/AAEmu/.server_files/AAEmu.Game/ $GAME_DATA_DIR/  (or e2e-boot.sh --provision-data)"
    fi

    # a freshly generated .env invalidates any stale MySQL volume's password
    if [ "$created" = 1 ] && e2e_db_volume_exists; then
        e2e_log "new DB_PASSWORD — wiping stale e2e MySQL volume"
        e2e_compose down -v
    fi

    # port conflicts: foreign holders hard-fail; our own procs are adopted
    if ! e2e_db_running && [ -n "$(e2e_port_owner 3306)" ]; then
        e2e_fail "port :3306 is held by a process outside the e2e db container ($(e2e_port_owner 3306))"
    fi

    # preflight (2026-08-07 hardening): kill stale AAEmu instances, adopt a
    # healthy stack, never boot into an occupied port, truncate ballooned
    # logs. Order matters: sweep FIRST (so no live writer survives), then
    # truncate what the dead runs left behind.
    e2e_preflight_sweep
    e2e_dedupe_servers
    e2e_sweep_strays
    e2e_log_guard

    # binaries (publish on first boot; E2E_REBUILD=1 forces)
    if [ ! -f "$LOGIN_DIR/AAEmu.Login.dll" ] || [ ! -f "$GAME_DIR/AAEmu.Game.dll" ] || [ "${E2E_REBUILD:-0}" = 1 ]; then
        e2e_log "publishing Login + Game (Release) ..."
        dotnet publish "$REPO_ROOT/AAEmu.Login/AAEmu.Login.csproj" -c Release -o "$LOGIN_DIR" --nologo
        dotnet publish "$REPO_ROOT/AAEmu.Game/AAEmu.Game.csproj"   -c Release -o "$GAME_DIR"   --nologo
        e2e_log "publish done"
    fi

    e2e_provision_layout
}

# Phase 1: MySQL up with a fresh deterministic seed (volume wipe + SQL/ init).
e2e_boot_db() {
    e2e_db_up
}

# Phase 2: Login + Game up (boot order: MySQL -> login -> game).
e2e_boot_servers() {
    if ! e2e_server_ready Login "$PORT_LOGIN_INT" "$PORT_LOGIN"; then
        e2e_kill_server Login
        e2e_start_server login "$LOGIN_DIR" AAEmu.Login.dll "$LOG_DIR/login.log"
        e2e_wait_tcp "$PORT_LOGIN_INT" 90 "login internal"   "$LOG_DIR/login.log" login
        e2e_wait_tcp "$PORT_LOGIN"     90 "login"            "$LOG_DIR/login.log" login
    else
        e2e_log "login already up — adopting (pid $(e2e_find_proc Login | head -1))"
        e2e_find_proc Login | head -1 > "$PID_DIR/login.pid"
    fi

    if ! e2e_server_ready Game "$PORT_GAME" "$PORT_STREAM" $([ "$E2E_BRIDGE" = 1 ] && echo "$PORT_BRIDGE"); then
        e2e_kill_server Game
        e2e_start_server game "$GAME_DIR" AAEmu.Game.dll "$LOG_DIR/game.log"
        e2e_wait_tcp "$PORT_GAME"   300 "game"            "$LOG_DIR/game.log" game
        e2e_wait_tcp "$PORT_STREAM" 300 "game stream"     "$LOG_DIR/game.log" game
        if [ "$E2E_BRIDGE" = 1 ]; then
            e2e_wait_tcp "$PORT_BRIDGE" 60 "bot drive bridge" "$LOG_DIR/game.log"
        fi
    else
        e2e_log "game already up — adopting (pid $(e2e_find_proc Game | head -1))"
        e2e_find_proc Game | head -1 > "$PID_DIR/game.pid"
    fi

    if [ "$E2E_BRIDGE" = 1 ]; then
        e2e_log "stack up: login :1237 :1234 | game :1239 :1250 | bridge :1260 | MySQL :3306 (e2e db)"
    else
        e2e_log "stack up: login :1237 :1234 | game :1239 :1250 | MySQL :3306 (e2e db)"
    fi
}

# Full boot path shared by e2e-boot.sh and e2e-reset.sh.
e2e_full_boot() {
    e2e_prepare
    e2e_boot_db
    e2e_boot_servers
    e2e_baseline_report || true   # informational on boot; reset enforces
}
