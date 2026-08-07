#!/bin/bash
# M2b-E2E stack helpers — the harness boot/status/stop/reset scripts.
# The E2E runner (AAEmu.IntegrationTests/M2bE2eTests) drives the full cycle;
# these scripts are the human-facing control surface.
set -euo pipefail

E2E_ROOT="${E2E_ROOT:-/root/aaemu-e2e}"
COMPOSE_FILE="$(cd "$(dirname "$0")" && pwd)/docker-compose.yaml"
ENV_FILE="$E2E_ROOT/.env"

cmd="${1:-help}"
shift || true

mkdir -p "$E2E_ROOT"
if [ ! -f "$ENV_FILE" ]; then
    echo "DB_PASSWORD=e2e_$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')" > "$ENV_FILE"
    echo "[e2e] generated $ENV_FILE"
fi

case "$cmd" in
    db-up)
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d db
        echo "[e2e] waiting for MySQL (seed-complete, not just ping)..."
        for i in $(seq 1 120); do
            if docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T db mysql -h 127.0.0.1 -u root -p"$(grep DB_PASSWORD "$ENV_FILE" | cut -d= -f2)" -N -e "SELECT COUNT(*) FROM aaemu_login.users LIMIT 1" >/dev/null 2>&1; then
                echo "[e2e] MySQL healthy (aaemu_login/aaemu_game seeded)"
                exit 0
            fi
            sleep 2
        done
        echo "[e2e] MySQL seed did not complete" >&2
        exit 1
        ;;
    db-down)
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" down
        ;;
    db-reset)
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" down -v
        ;;
    status)
        echo "== ports =="
        for port in 3306 1237 1239 1250 1260; do
            if ss -ltn 2>/dev/null | grep -q ":$port "; then
                echo "  :$port LISTENING"
            else
                echo "  :$port closed"
            fi
        done
        echo "== processes =="
        pgrep -af "AAEmu.(Login|Game).dll" || echo "  (no server processes)"
        echo "== canonical sqlite md5 =="
        md5sum "$E2E_ROOT/runtime/game-data/Data/compact.sqlite3" 2>/dev/null || echo "  (data not synced — run: rsync -a root@192.168.0.165:/root/AAEmu/.server_files/AAEmu.Game/ $E2E_ROOT/runtime/game-data/)"
        echo "== runtime sqlite md5 (must equal canonical) =="
        md5sum "$E2E_ROOT/runtime/game/Data/compact.sqlite3" 2>/dev/null || echo "  (runtime not provisioned — run the E2E tests)"
        ;;
    logs)
        tail -n 50 "$E2E_ROOT/logs/login.log" 2>/dev/null || echo "(no login log)"
        echo "-----"
        tail -n 50 "$E2E_ROOT/logs/game.log" 2>/dev/null || echo "(no game log)"
        ;;
    *)
        echo "usage: $0 {db-up|db-down|db-reset|status|logs}"
        ;;
esac
