#!/bin/bash
# e2e-reset.sh — teardown + clean re-boot + byte-identical baseline proof.
#
# Cycle-isolation contract (success_signal of E2E-1):
#   1. stop Login + Game, wipe the MySQL volume (fresh SQL re-seed)
#   2. restore the runtime compact.sqlite3 from the canonical copy
#   3. clean re-boot (prepare -> MySQL seed -> login -> game)
#   4. VERIFY:
#      a. MySQL dump hash at SEED state (pre-server, pure SQL/ content —
#         deterministic) matches the stored first-cycle baseline. The game
#         writes a runtime `accounts` row with DateTime.UtcNow at boot, so a
#         POST-boot dump is never byte-identical — the seed state is the
#         canonical MySQL baseline, the servers' runtime writes are expected.
#      b. runtime sqlite md5 == canonical sqlite md5 (the game only READS
#         compact.sqlite3; Data is a read-only mount on prod).
#
# Exit code is 0 only when both baselines are proven byte-identical.
set -euo pipefail
source "$(cd "$(dirname "$0")" && pwd)/e2e-common.sh"

[ -f "$CANONICAL_SQLITE" ] || e2e_fail "canonical data missing at $CANONICAL_SQLITE — run e2e-boot.sh --provision-data once"

mkdir -p "$STATE_DIR"

# --- 1. teardown -----------------------------------------------------------
e2e_log "teardown: stopping Login + Game ..."
e2e_kill_server Game
e2e_kill_server Login
rm -f "$PID_DIR"/*.pid

e2e_log "teardown: MySQL down + volume wipe (fresh seed next boot) ..."
e2e_compose down -v

# --- 2. restore canonical sqlite (byte-identical) --------------------------
e2e_log "restoring canonical sqlite -> runtime ..."
mkdir -p "$(dirname "$RUNTIME_SQLITE")"
cp "$CANONICAL_SQLITE" "$RUNTIME_SQLITE"
[ "$(md5sum < "$CANONICAL_SQLITE")" = "$(md5sum < "$RUNTIME_SQLITE")" ] \
    || e2e_fail "sqlite restore is NOT byte-identical — aborting"

# --- 3. clean re-boot -------------------------------------------------------
e2e_log "clean re-boot (prepare -> MySQL seed -> login -> game) ..."
e2e_prepare
e2e_boot_db

# --- 4a. MySQL SEED baseline (pre-server: deterministic SQL/ content) -------
e2e_log "capturing MySQL seed-state dump hash (servers not started yet) ..."
MYSQL_HASH="$(e2e_mysql_dump_hash)"
BASELINE_FILE="$STATE_DIR/mysql-baseline.sha256"
if [ ! -f "$BASELINE_FILE" ]; then
    echo "$MYSQL_HASH" > "$BASELINE_FILE"
    e2e_log "MySQL seed baseline established (first clean cycle): $MYSQL_HASH"
else
    PREV="$(cat "$BASELINE_FILE")"
    if [ "$PREV" = "$MYSQL_HASH" ]; then
        e2e_log "MySQL seed baseline MATCH: $MYSQL_HASH"
    else
        e2e_log "MySQL seed baseline DIVERGED: stored $PREV != now $MYSQL_HASH" >&2
        exit 1
    fi
fi

# --- 4b. boot servers, then verify sqlite post-boot --------------------------
e2e_boot_servers
e2e_log "post-boot sqlite baseline verification ..."
if ! e2e_baseline_report; then
    exit 1
fi

e2e_log "RESET OK — cycle isolated: sqlite byte-identical to canonical (pre/post boot), MySQL seed re-seeded deterministically"
e2e_log "next clean cycle: re-run $0 (evidence: sqlite md5 + $BASELINE_FILE)"
