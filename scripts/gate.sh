#!/bin/bash
# AAEmu fork — full local test gate (mirrors upstream CI)
# Usage: ./scripts/gate.sh            # everything
#        ./scripts/gate.sh quest      # build + only quest tests
#        ./scripts/gate.sh <filter>   # build + filtered tests
set -e
cd "$(dirname "$0")/.."

echo "== 1/3 Release build =="
dotnet build --configuration Release AAEmu.slnx 2>&1 | tail -2

echo "== 2/3 compiler-check (in-game scripts must compile) =="
dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check 2>&1 | tail -2

echo "== 3/3 Tests =="
FILTER="${1:-}"
if [ -n "$FILTER" ]; then
  dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build \
    --filter "FullyQualifiedName~$FILTER" 2>&1 | tail -4
else
  dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build 2>&1 | tail -4
fi

echo "== GATE DONE =="
