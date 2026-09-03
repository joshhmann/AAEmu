#!/usr/bin/env bash
# AAEmu fork — archaeology-specific pre-merge checks.
# Usage: ./scripts/archaeology-cycle.sh
#
# Runs, from the repo root:
#   1. Release build of AAEmu.ArchaeologyMcp
#   2. Release build of AAEmu.UnitTests
#   3. All archaeology-focused unit tests (AAEmu.UnitTests.ArchaeologyMcp)
#   4. Scripts/mcp-archaeology-smoke.sh (deterministic read-only MCP smoke;
#      AAPak tools report their unconfigured errors when ARCHEAGE_PAK_PATH
#      is unset — this script never claims to run AAPak).
#
# Read-only: builds write only to bin/obj; no source, data, or config is
# mutated. stderr from dotnet is preserved (not swallowed).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "== 1/4 Release build: AAEmu.ArchaeologyMcp =="
dotnet build --configuration Release AAEmu.ArchaeologyMcp/AAEmu.ArchaeologyMcp.csproj

echo "== 2/4 Release build: AAEmu.UnitTests =="
dotnet build --configuration Release AAEmu.UnitTests/AAEmu.UnitTests.csproj

echo "== 3/4 Archaeology-focused unit tests =="
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj \
  --configuration Release --no-build \
  --treenode-filter "/*/AAEmu.UnitTests.ArchaeologyMcp/*/*"

echo "== 4/4 MCP archaeology stdio smoke =="
bash ./Scripts/mcp-archaeology-smoke.sh

echo "== ARCHAEOLOGY CYCLE DONE =="
