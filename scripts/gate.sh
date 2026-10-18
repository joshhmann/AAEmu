#!/bin/bash
# AAEmu fork — full local test gate (mirrors upstream CI)
# Usage: ./scripts/gate.sh            # everything
#        ./scripts/gate.sh QuestManager   # class-name filter (treenode-filter)
#        ./scripts/gate.sh "Game.Core.Managers"  # namespace substring filter
set -euo pipefail
cd "$(dirname "$0")/.."

LOG_DIR="$(mktemp -d /tmp/aaemu-gate.XXXXXX)"
echo "Full gate logs: $LOG_DIR"

echo "== 1/5 Release build =="
dotnet build --configuration Release --verbosity normal AAEmu.slnx 2>&1 | tee "$LOG_DIR/build.log"

echo "== 2/5 compiler-check (in-game scripts must compile) =="
dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check 2>&1 | tee "$LOG_DIR/compiler-check.log"

FILTER="${1:-}"
OUT_FILE="$LOG_DIR/tests.log"
TEST_ARGS=(dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build)
if [ -n "$FILTER" ]; then
  # MTP runner (global.json: Microsoft.Testing.Platform) uses treenode-filter.
  # Class-name match: /*/*/<Class>/* ; namespace match: /*/*/<Namespace>/*
  TEST_ARGS+=(--treenode-filter "/*/*/${FILTER}/*")
fi

echo "== 3/5 Unit tests =="
RC=0
"${TEST_ARGS[@]}" 2>&1 | tee "$OUT_FILE" || RC=$?

echo "== Failing tests =="
if grep -E '^ *failed ' "$OUT_FILE"; then
  :
else
  echo "(no failed-test lines matched — full log: $OUT_FILE)"
fi

if [ -z "$FILTER" ] && [ $RC -eq 0 ]; then
  echo "== 4/5 MCP stdio protocol smoke =="
  bash ./Scripts/mcp-stdio-smoke.sh 2>&1 | tee "$LOG_DIR/botcontrol-smoke.log"

  echo "== 5/5 MCP archaeology gate smoke =="
  bash ./Scripts/mcp-archaeology-gate-smoke.sh 2>&1 | tee "$LOG_DIR/archaeology-smoke.log"
fi

echo "== GATE DONE =="
exit $RC
