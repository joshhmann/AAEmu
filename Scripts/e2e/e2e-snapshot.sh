#!/bin/bash
# e2e-snapshot.sh — seeded-DB snapshot for lanes that need large pre-seeded
# bot rosters (A5/A4/Tier-3/A3 probes). Eliminates per-run seeding:
#
#   ./e2e-snapshot.sh create   [OUT_DIR]   # dump the CURRENT lane's MySQL state
#   ./e2e-snapshot.sh restore  [SNAP_DIR]  # wipe + restore from the snapshot
#   ./e2e-snapshot.sh validate [SNAP_DIR]  # row-count join check against the live DB
#
# Env: E2E_ROOT (default /root/aaemu-e2e), COMPOSE_PROJECT_NAME, DB_HOST_PORT —
# the same overrides e2e-boot.sh takes; the db service must be up.
#
# Snapshot contents: FULL mysqldump of aaemu_login + aaemu_game (the game's
# static data lives in compact.sqlite3, so the MySQL schemas are small even
# with a 1,000-bot roster). Take it right after seeding, BEFORE any probe
# human connects.
#
# Freshness tradeoff (documented per work item): snapshot bots carry stale
# positions/state from when the snapshot was taken. Acceptable because the
# seed path is idempotent-by-adoption: EnsureFreshBotRow wipes a bot's rows
# and HeadlessSession.Provision re-provisions/adopts what matters. Non-bot
# rows in the snapshot are the probe humans of that era — a probe run that
# needs its own human account should use e2e-reset.sh semantics instead, or
# delete the stale human first.
#
# Schema-version stamp: sha256 over the sorted SQL/ update file list. A
# restore REFUSES to run when the stamp differs from this repo's SQL tree —
# apply new SQL updates and re-create the snapshot first.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
E2E_ROOT="${E2E_ROOT:-/root/aaemu-e2e}"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.yaml"
ENV_FILE="$E2E_ROOT/.env"
DEFAULT_SNAP_DIR="$E2E_ROOT/snapshots/seeded-roster"
DB_PASSWORD="$(sed -n 's/^DB_PASSWORD=//p' "$ENV_FILE")"
schema_stamp() {
    (cd "$REPO_ROOT/SQL" && find . -type f -name '*.sql' | sort | xargs -d '\n' sha256sum) \
        | sha256sum | cut -d' ' -f1
}

snap_compose() { docker compose -p "${COMPOSE_PROJECT_NAME:-e2e}" -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }


require_db() {
    snap_compose ps -q db >/dev/null 2>&1 && [ -n "$(snap_compose ps -q db)" ] ||
        { echo "[snapshot] ERROR: db service not up for this lane (run e2e-boot.sh first)" >&2; exit 1; }
}

mysql_exec() {
    snap_compose exec -T -e MYSQL_PWD="$DB_PASSWORD" db mysql -u root "$@"
}

dump_exec() {
    snap_compose exec -T -e MYSQL_PWD="$DB_PASSWORD" db mysqldump -u root \
        --skip-dump-date --single-transaction --routines=false "$@"
}

managed_join_count_sql() {
    # The SAME join the tier3 probe uses as its seed-completeness check.
    echo "SELECT COUNT(*) FROM aaemu_game.characters c JOIN aaemu_login.users u ON u.id = c.account_id WHERE u.username LIKE 'bot_managed_%' AND c.delete_time = '0001-01-01 00:00:00';"
}

cmd="${1:-help}"
case "$cmd" in
    create)
        SNAP_DIR="${2:-$DEFAULT_SNAP_DIR}"
        require_db
        mkdir -p "$SNAP_DIR"
        echo "[snapshot] dumping aaemu_login + aaemu_game ..."
        dump_exec --databases aaemu_login aaemu_game > "$SNAP_DIR/db.sql"
        {
            echo "schema_stamp=$(schema_stamp)"
            echo "created_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
            echo -n "managed_count="
            mysql_exec -N -e "$(managed_join_count_sql)"
        } > "$SNAP_DIR/meta.txt"
        echo "[snapshot] created $SNAP_DIR ($(du -h "$SNAP_DIR/db.sql" | cut -f1))"
        cat "$SNAP_DIR/meta.txt"
        ;;
    restore)
        SNAP_DIR="${2:-$DEFAULT_SNAP_DIR}"
        [ -f "$SNAP_DIR/db.sql" ] || { echo "[snapshot] ERROR: $SNAP_DIR/db.sql missing" >&2; exit 1; }
        require_db
        stamp_now="$(schema_stamp)"
        stamp_snap="$(sed -n 's/^schema_stamp=//p' "$SNAP_DIR/meta.txt")"
        if [ -z "$stamp_snap" ]; then
            echo "[snapshot] ERROR: meta.txt has no schema stamp — refusing blind restore" >&2
            exit 1
        fi
        if [ "$stamp_now" != "$stamp_snap" ]; then
            echo "[snapshot] ERROR: schema stamp mismatch (snapshot=$stamp_snap repo=$stamp_now)." >&2
            echo "  SQL updates landed since this snapshot — re-seed and re-run 'create'." >&2
            exit 1
        fi
        echo "[snapshot] restoring from $SNAP_DIR ..."
        t0=$(date +%s)
        mysql_exec < "$SNAP_DIR/db.sql"   # --databases dump drops+recreates each table
        echo "[snapshot] restored in $(( $(date +%s) - t0 ))s"
        "$0" validate "$SNAP_DIR"
        ;;
    validate)
        SNAP_DIR="${2:-$DEFAULT_SNAP_DIR}"
        require_db
        want="$(sed -n 's/^managed_count=//p' "$SNAP_DIR/meta.txt")"
        got="$(mysql_exec -N -e "$(managed_join_count_sql)")"
        if [ "$got" -ge "$want" ] && [ -n "$want" ]; then
            echo "[snapshot] VALID: managed-bot roster join count $got (snapshot recorded $want)"
        else
            echo "[snapshot] INVALID: managed-bot roster join count $got, expected >= $want" >&2
            exit 1
        fi
        ;;
    help|*)
        echo "usage: $0 {create|restore|validate} [snapshot-dir]"
        echo "  default snapshot dir: $DEFAULT_SNAP_DIR"
        ;;
esac
