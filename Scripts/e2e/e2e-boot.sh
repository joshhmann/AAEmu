#!/bin/bash
# e2e-boot.sh — ONE COMMAND: MySQL + Login + Game up with canonical data.
#
#   ./e2e-boot.sh                 boot the full stack (idempotent: adopts an
#                                 already-running e2e stack)
#   ./e2e-boot.sh --provision-data  first-time: rsync canonical game data from
#                                 the aaemu box (read-only), then boot
#   E2E_REBUILD=1 ./e2e-boot.sh   force re-publish of Login/Game binaries
#
# Deterministic contract: boot order MySQL -> login (:1237/:1234) -> game
# (:1239/:1250) + bridge (:1260); every wait is a health loop with a hard
# timeout; foreign port holders hard-fail with the offending pids; runtime
# layout is assembled to match the E2E test runner (E2eStack.cs) byte for
# byte. Teardown + byte-identical baseline proof: e2e-reset.sh.
set -euo pipefail
source "$(cd "$(dirname "$0")" && pwd)/e2e-common.sh"

case "${1:-}" in
    --provision-data)
        if [ ! -f "$CANONICAL_SQLITE" ]; then
            e2e_log "provisioning canonical game data (read-only rsync from the aaemu box) ..."
            mkdir -p "$GAME_DATA_DIR"
            rsync -a root@192.168.0.165:/root/AAEmu/.server_files/AAEmu.Game/ "$GAME_DATA_DIR/"
            [ -f "$CANONICAL_SQLITE" ] || e2e_fail "rsync did not produce $CANONICAL_SQLITE"
        else
            e2e_log "canonical data already present — skipping provision"
        fi
        ;;
    --help|-h)
        echo "usage: $0 [--provision-data]"
        echo "       E2E_REBUILD=1 $0   force re-publish of server binaries"
        echo "       E2E_ROOT=/path $0  override stack root (default /root/aaemu-e2e)"
        exit 0
        ;;
    "")
        ;;
    *)
        e2e_fail "unknown argument: $1 (see $0 --help)"
        ;;
esac

e2e_full_boot
# Post-publish log-cap guard runs inside e2e_prepare (e2e-common.sh):
#   $E2E_ROOT/ensure-log-caps.sh when present, else the repo copy at
#   Scripts/e2e/ensure-log-caps.sh (t_dde9846f fallback for clean hosts)
# so an E2E_REBUILD=1 publish can never clobber the capped runtime NLog.configs
# (t_a54574e9 — thinpool massacre prevention).
e2e_log "BOOT OK — pids: login $(cat "$PID_DIR/login.pid" 2>/dev/null || echo -) game $(cat "$PID_DIR/game.pid" 2>/dev/null || echo -)"
e2e_log "logs: $LOG_DIR/login.log $LOG_DIR/game.log | status: $(dirname "$0")/e2e-stack.sh status"
