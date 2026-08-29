#!/usr/bin/env bash
# ============================================================================
# A5 Tier-3 six-hour dormant-timer soak rig.
#
# Runs the G2 A5 Tier-3 dormant-timer acceptance probe for a full six-hour
# window (A5_TIER3_SIX_HOUR=1, 360 minutes, 60s samples) against a dedicated
# E2E stack (shifted ports, its own compose project + E2E root) so a 6h run
# never collides with a live dev stack.
#
# The exact dotnet invocation is taken from the repo's own docs:
#   ROADMAP.md:30 and scorecard-explorations/mechanics/playerbot-capability-matrix.md:23
#
# Usage:
#   run-a5-sixhour-soak.sh
#   A5_TIER3_SIX_HOUR_MINUTES=180 run-a5-sixhour-soak.sh   # override window
#   E2E_ROOT=/custom/root run-a5-sixhour-soak.sh           # override E2E root
#
# Exit code: the dotnet test's exit code (0 = pass). 130 on interrupt.
# ============================================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# --- Configurable env (defaults from the verified command) -----------------
A5_TIER3_SIX_HOUR="${A5_TIER3_SIX_HOUR:-1}"
A5_TIER3_SIX_HOUR_MINUTES="${A5_TIER3_SIX_HOUR_MINUTES:-360}"
A5_TIER3_SIX_HOUR_SAMPLE_SECONDS="${A5_TIER3_SIX_HOUR_SAMPLE_SECONDS:-60}"
A5_DORMANT_COUNT="${A5_DORMANT_COUNT:-1000}"
E2E_ROOT="${E2E_ROOT:-/root/aaemu-e2e-a5-tier3-sixhour}"
E2E_LOGIN_PORT="${E2E_LOGIN_PORT:-4237}"
E2E_GAME_PORT="${E2E_GAME_PORT:-4239}"
E2E_STREAM_PORT="${E2E_STREAM_PORT:-4250}"
E2E_BRIDGE_PORT="${E2E_BRIDGE_PORT:-4260}"
E2E_INTERNAL_PORT="${E2E_INTERNAL_PORT:-4234}"
E2E_WEBAPI_PORT="${E2E_WEBAPI_PORT:-4280}"
E2E_DB_PORT="${E2E_DB_PORT:-43306}"
DB_HOST_PORT="${DB_HOST_PORT:-43306}"
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-aaemu_a5_t3_sixhour}"
E2E_REBUILD="${E2E_REBUILD:-1}"

REPORT_PATH="$E2E_ROOT/logs/g2-a5-tier3-sixhour-report.json"
LOG_DIR="$E2E_ROOT/logs"
LOG_PATH="$LOG_DIR/soak-run-$(date +%Y%m%d-%H%M%S).log"

# --- Pre-flight -------------------------------------------------------------
echo "== A5 Tier-3 six-hour dormant-timer soak pre-flight"

# dotnet present (warn if < 10)
if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: 'dotnet' not found on PATH" >&2
    exit 1
fi
dotnet_version="$(dotnet --version)"
echo "   dotnet: $dotnet_version"
if [[ "$dotnet_version" < "10" ]]; then
    echo "   warning: dotnet < 10 detected ($dotnet_version); the E2E stack targets net10.0"
fi

# test project exists
if [[ ! -f "$REPO_ROOT/AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj" ]]; then
    echo "error: test project not found: $REPO_ROOT/AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj" >&2
    exit 1
fi
echo "   test project: AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj"

# docker available (E2E stack boots MySQL via compose)
if ! docker info >/dev/null 2>&1; then
    echo "error: docker is not available ('docker info' failed). The E2E stack boots MySQL via compose; a 6h soak cannot run without it." >&2
    exit 1
fi
echo "   docker: available"

# shifted ports free — a 6h run must not collide
busy_ports=()
for port in "$E2E_LOGIN_PORT" "$E2E_GAME_PORT" "$E2E_STREAM_PORT" "$E2E_BRIDGE_PORT" \
            "$E2E_INTERNAL_PORT" "$E2E_WEBAPI_PORT" "$E2E_DB_PORT"; do
    if ss -ltn 2>/dev/null | awk '{print $4}' | grep -q "[:.]${port}$"; then
        busy_ports+=("$port")
    fi
done
if [[ "${#busy_ports[@]}" -gt 0 ]]; then
    echo "error: port(s) already bound: ${busy_ports[*]}. A 6h soak must not collide with an existing stack; free them and retry." >&2
    exit 1
fi
echo "   ports: all shifted ports free (${E2E_LOGIN_PORT}, ${E2E_GAME_PORT}, ${E2E_STREAM_PORT}, ${E2E_BRIDGE_PORT}, ${E2E_INTERNAL_PORT}, ${E2E_WEBAPI_PORT}, ${E2E_DB_PORT})"

# previous report present? ask before overwriting
if [[ -f "$REPORT_PATH" ]]; then
    echo "   previous report found: $REPORT_PATH"
    if command -v python3 >/dev/null 2>&1; then
        prev_passed="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("passed"))' "$REPORT_PATH" 2>/dev/null || echo "?")"
    elif command -v jq >/dev/null 2>&1; then
        prev_passed="$(jq -r '.passed' "$REPORT_PATH" 2>/dev/null || echo "?")"
    else
        prev_passed="?"
    fi
    echo "   previous run passed: ${prev_passed:-?}"
    printf "   overwrite and continue? [y/N] "
    read -r answer
    if [[ "$answer" != "y" && "$answer" != "Y" ]]; then
        echo "   aborted (no overwrite)."
        exit 1
    fi
fi

# --- Logging ----------------------------------------------------------------
mkdir -p "$LOG_DIR"
{
    echo "== A5 Tier-3 six-hour dormant-timer soak"
    echo "start: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "git HEAD: $(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo "unknown")"
    echo "env:"
    echo "  A5_TIER3_SIX_HOUR=$A5_TIER3_SIX_HOUR"
    echo "  A5_TIER3_SIX_HOUR_MINUTES=$A5_TIER3_SIX_HOUR_MINUTES"
    echo "  A5_TIER3_SIX_HOUR_SAMPLE_SECONDS=$A5_TIER3_SIX_HOUR_SAMPLE_SECONDS"
    echo "  A5_DORMANT_COUNT=$A5_DORMANT_COUNT"
    echo "  E2E_ROOT=$E2E_ROOT"
    echo "  E2E_LOGIN_PORT=$E2E_LOGIN_PORT E2E_GAME_PORT=$E2E_GAME_PORT E2E_STREAM_PORT=$E2E_STREAM_PORT"
    echo "  E2E_BRIDGE_PORT=$E2E_BRIDGE_PORT E2E_INTERNAL_PORT=$E2E_INTERNAL_PORT E2E_WEBAPI_PORT=$E2E_WEBAPI_PORT"
    echo "  E2E_DB_PORT=$E2E_DB_PORT DB_HOST_PORT=$DB_HOST_PORT"
    echo "  COMPOSE_PROJECT_NAME=$COMPOSE_PROJECT_NAME E2E_REBUILD=$E2E_REBUILD"
    echo "  report: $REPORT_PATH"
    echo "  log: $LOG_PATH"
} | tee "$LOG_PATH"

# --- Interrupt handling -----------------------------------------------------
interrupted=0
on_interrupt() {
    interrupted=1
    echo
    echo "== soak interrupted — report may be partial"
    echo "   log: $LOG_PATH"
    exit 130
}
trap on_interrupt INT TERM
trap 'rc=$?; if [ "$interrupted" -eq 0 ]; then echo; echo "== soak finished (exit $rc)"; echo "   report: $REPORT_PATH"; if [ -f "$REPORT_PATH" ]; then if command -v python3 >/dev/null 2>&1; then python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print('   passed:', d.get('passed')); print('   sixHourDormantTimersLeg:', d.get('sixHourDormantTimersLeg'))" "$REPORT_PATH" 2>/dev/null || echo "   (could not parse report)"; elif command -v jq >/dev/null 2>&1; then echo "   passed: $(jq -r '.passed' "$REPORT_PATH" 2>/dev/null)"; echo "   sixHourDormantTimersLeg: $(jq -r '.sixHourDormantTimersLeg' "$REPORT_PATH" 2>/dev/null)"; else echo "   passed: $(grep -o '"passed"[^,]*' "$REPORT_PATH" | head -1)"; echo "   sixHourDormantTimersLeg: $(grep -o '"sixHourDormantTimersLeg"[^,]*' "$REPORT_PATH" | head -1)"; fi; else echo "   (no report written)"; fi; echo "   log: $LOG_PATH"; fi; exit "$rc"' EXIT

# --- Run --------------------------------------------------------------------
echo "== starting soak (this runs ~6 hours)"
echo "   log: $LOG_PATH"

set +e
A5_TIER3_SIX_HOUR="$A5_TIER3_SIX_HOUR" \
A5_TIER3_SIX_HOUR_MINUTES="$A5_TIER3_SIX_HOUR_MINUTES" \
A5_TIER3_SIX_HOUR_SAMPLE_SECONDS="$A5_TIER3_SIX_HOUR_SAMPLE_SECONDS" \
A5_DORMANT_COUNT="$A5_DORMANT_COUNT" \
E2E_ROOT="$E2E_ROOT" \
E2E_LOGIN_PORT="$E2E_LOGIN_PORT" \
E2E_GAME_PORT="$E2E_GAME_PORT" \
E2E_STREAM_PORT="$E2E_STREAM_PORT" \
E2E_BRIDGE_PORT="$E2E_BRIDGE_PORT" \
E2E_INTERNAL_PORT="$E2E_INTERNAL_PORT" \
E2E_WEBAPI_PORT="$E2E_WEBAPI_PORT" \
E2E_DB_PORT="$E2E_DB_PORT" \
DB_HOST_PORT="$DB_HOST_PORT" \
COMPOSE_PROJECT_NAME="$COMPOSE_PROJECT_NAME" \
E2E_REBUILD="$E2E_REBUILD" \
dotnet test --project "$REPO_ROOT/AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj" --configuration Release \
    --filter-method AAEmu.IntegrationTests.E2e.G2.A5Tier3AcceptanceProbeTests.Probe_A5Tier3DormantTimers_SixHour \
    2>&1 | tee -a "$LOG_PATH"
test_rc="${PIPESTATUS[0]}"
set -e

echo "== dotnet test exited with code $test_rc"
exit "$test_rc"
