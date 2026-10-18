#!/bin/bash
# Focused development checks only; ./scripts/gate.sh remains the qualification gate.
# Usage: ./scripts/dev-test.sh unit BotActionCommandQueueTests
#        ./scripts/dev-test.sh integration BotShortRunObservationBudgetTests
set -euo pipefail

if [ "$#" -ne 2 ] || [ -z "$2" ]; then
  echo "Usage: $0 <unit|integration> <class-filter>" >&2
  exit 2
fi
case "$1" in
  unit) PROJECT=AAEmu.UnitTests; FILTER_OPTION=--treenode-filter ;;
  integration) PROJECT=AAEmu.IntegrationTests; FILTER_OPTION=--filter-query ;;
  *) echo "Unknown test project: $1 (expected unit or integration)" >&2; exit 2 ;;
esac
FILTER="$2"
cd "$(dirname "$0")/.."
ARTIFACTS="$PWD/.artifacts/dev-test"
mkdir -p "$ARTIFACTS/logs"
LOG_DIR="$(mktemp -d "$ARTIFACTS/logs/run.XXXXXX")"

echo "DEVELOPMENT CHECK ONLY: static analyzers deferred; compiler diagnostics remain enabled."
echo "Isolated reusable outputs: $ARTIFACTS"
echo "Full development logs: $LOG_DIR"
echo "== 1/2 Build $PROJECT (not qualification) =="
dotnet build "$PROJECT/$PROJECT.csproj" --configuration Release --verbosity minimal \
  --artifacts-path "$ARTIFACTS" -p:RunAnalyzersDuringBuild=false 2>&1 | tee "$LOG_DIR/build.log"

echo "== 2/2 Focused tests: $FILTER =="
# Run the isolated module directly; do not resolve/rebuild normal project outputs.
dotnet test --test-modules "$PROJECT.dll" --root-directory "$ARTIFACTS/bin/$PROJECT/release" \
  "$FILTER_OPTION" "/*/*/${FILTER}/*" --minimum-expected-tests 1 \
  --results-directory "$LOG_DIR/results" 2>&1 | tee "$LOG_DIR/tests.log"

echo "DEVELOPMENT CHECK PASSED — static analysis deferred; NOT a qualification gate."
echo "Before final qualification: ./scripts/gate.sh (plus required domain checks)."
