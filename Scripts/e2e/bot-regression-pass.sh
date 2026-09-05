#!/usr/bin/env bash
# ============================================================================
# bot-regression-pass.sh — playerbots-as-test-force (M5-stand-in rule)
#
# Runs EVERY proven live bot scenario against the isolated E2E stack in
# sequence and prints a pass/fail summary. Each scenario is an independently
# green E2E test; this composes them into one regression sweep so a single
# command answers "are the bots still working on this build?".
#
# Usage:
#   E2E_REBUILD=1 ./Scripts/e2e/bot-regression-pass.sh          # fresh runtime
#   ./Scripts/e2e/bot-regression-pass.sh                        # reuse runtime
#   SCENARIOS="fishing duels" ./...                     # subset by exact key
#
# Scenarios (key → test class):
#   goldenroute  M1M2ContractReplayE2eTests      golden-route quest chain
#   economy      EconomyDayCycleE2eTests         buy→plant→harvest→craft→sell→deposit + ledger restart reconciliation
#   fishing      FishingVerificationE2eTests     plot-809 cast loop, labor/worm/loot
#   duels        DuelFactionSwapE2eTests         challenge→accept→flag spawn→faction swap
#   transfers    TransferRideE2eTests            board gondola → ride → disembark
#   packrestart  M51AttachedPackRestartE2eTests  attached-pack-on-slave across kill -9
#   partyspike   PartySpikeE2eTests              3-bot rally/assist/kill elite
#
# Evidence per scenario: $E2E_ROOT/logs/*.json (paths printed per line).
# Exit code: number of failed scenarios.
# ============================================================================
set -uo pipefail

cd "$(dirname "$0")/../.."

declare -A SCENARIO_CLASS=(
  [goldenroute]="AAEmu.IntegrationTests.E2e.M1M2ContractReplayE2eTests"
  [economy]="AAEmu.IntegrationTests.E2e.EconomyDayCycleE2eTests"
  [fishing]="AAEmu.IntegrationTests.E2e.FishingVerificationE2eTests"
  [duels]="AAEmu.IntegrationTests.E2e.DuelFactionSwapE2eTests"
  [transfers]="AAEmu.IntegrationTests.E2e.TransferRideE2eTests"
  [packrestart]="AAEmu.IntegrationTests.E2e.M51AttachedPackRestartE2eTests"
  [partyspike]="AAEmu.IntegrationTests.E2e.PartySpikeE2eTests"
)

# NOTE: the map is NOT named SCENARIOS — that name is the operator's
# subset env (space-separated exact keys). Naming the assoc array SCENARIOS
# shadows the env and silently runs every scenario.
ORDER=(goldenroute economy fishing duels transfers packrestart partyspike)

REQUESTED="${SCENARIOS:-${ORDER[*]}}"

E2E_ROOT="${E2E_ROOT:-/root/aaemu-e2e}"

echo "[bot-pass] republishing game runtime from current tree..."
dotnet publish AAEmu.Game/AAEmu.Game.csproj -c Release -o "$E2E_ROOT/runtime/game" --nologo | tail -1

PASS=()
FAIL=()
STARTED=$(date -u +%Y%m%d-%H%M%S)

for key in ${ORDER[*]}; do
  # exact-token match so SCENARIOS="fishing duels" picks exactly those two
  [[ " $REQUESTED " == *" $key "* ]] || continue
  class="${SCENARIO_CLASS[$key]}"
  echo ""
  echo "=================================================================="
  echo "[bot-pass] SCENARIO: $key ($class)"
  echo "=================================================================="
  if dotnet test --project AAEmu.IntegrationTests --filter-class "$class" 2>&1 | tail -20; then
    PASS+=("$key")
    echo "[bot-pass] ✓ $key PASS"
  else
    FAIL+=("$key")
    echo "[bot-pass] ✗ $key FAIL"
  fi
done

echo ""
echo "=================================================================="
echo "[bot-pass] SUMMARY ($STARTED): ${#PASS[@]} passed / ${#FAIL[@]} failed"
[[ ${#PASS[@]} -gt 0 ]] && echo "[bot-pass]   PASS: ${PASS[*]}"
[[ ${#FAIL[@]} -gt 0 ]] && echo "[bot-pass]   FAIL: ${FAIL[*]}"
echo "[bot-pass] evidence: $E2E_ROOT/logs/*report*.json"
exit ${#FAIL[@]}
