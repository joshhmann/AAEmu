#!/usr/bin/env bash
# eval_brain_revision.sh — mechanical GENERALIZATION evaluator (no judgment in scoring path).
# Usage: ./tools/eval_brain_revision.sh --parent <sha> --child <sha> --suite heldout-v1 --runs 3
# Compares parent vs child on the held-out xUnit scenario suite; scorer exits 0=PASS, 1=FAIL, 2=INVALID.
# Uses a SEPARATE WORKTREE per revision so the untracked harness (this script, scorer,
# suite JSONs, Heldout*.cs) is never stashed away mid-run. Worktrees removed on exit.
set -u
PARENT=""; CHILD=""; SUITE="heldout-v1"; RUNS=3
while [ $# -gt 0 ]; do case "$1" in
  --parent) PARENT="$2"; shift 2;;
  --child) CHILD="$2"; shift 2;;
  --suite) SUITE="$2"; shift 2;;
  --runs) RUNS="$2"; shift 2;;
  *) echo "unknown arg: $1" >&2; exit 2;;
esac; done
[ -z "$PARENT" ] && { echo "missing --parent" >&2; exit 2; }
[ -z "$CHILD" ] && { echo "missing --child" >&2; exit 2; }
SUITE_DIR="heldout-v1"
[ -d "$SUITE_DIR" ] || { echo "suite dir missing: $SUITE_DIR" >&2; exit 2; }
command -v python3 >/dev/null || { echo "python3 missing" >&2; exit 2; }
REPO_ROOT="$(git rev-parse --show-toplevel)"
OUT="$REPO_ROOT/results/eval-$PARENT-vs-$CHILD"
mkdir -p "$OUT/parent" "$OUT/child"
WT_BASE="$(mktemp -d)"
cleanup() { git worktree remove --force "$WT_BASE/parent" 2>/dev/null; git worktree remove --force "$WT_BASE/child" 2>/dev/null; rm -rf "$WT_BASE"; }
trap cleanup EXIT
run_side() {
  local rev="$1" dest="$2"
  local wt="$WT_BASE/$dest"
  git worktree add -q --detach "$wt" "$rev" || { echo "cannot checkout $rev" >&2; return 2; }
  # Copy the harness (untracked in repo) into the worktree so the old revision can run it.
  mkdir -p "$wt/tools" "$wt/heldout-v1"
  cp "$REPO_ROOT/tools/eval_brain_revision.sh" "$REPO_ROOT/tools/score_brain.py" "$wt/tools/"
  cp "$REPO_ROOT/heldout-v1/"*.json "$wt/heldout-v1/" 2>/dev/null || true
  mkdir -p "$wt/AAEmu.UnitTests/Game/Core/Managers/Bots"
  cp "$REPO_ROOT/AAEmu.UnitTests/Game/Core/Managers/Bots/HeldoutScenarioTests.cs" \
     "$REPO_ROOT/AAEmu.UnitTests/Game/Core/Managers/Bots/HeldoutRecoveryTests.cs" \
     "$wt/AAEmu.UnitTests/Game/Core/Managers/Bots/" 2>/dev/null || { echo "held-out tests missing in worktree" >&2; return 2; }
  # Resolve the test project per worktree: older revisions name it
  # AAEmu.UnitTests/AAEmu.UnitTests.csproj, newer ones AAEmu.UnitTests.csproj.
  CSPROJ=""
  for cand in AAEmu.UnitTests/AAEmu.UnitTests.csproj AAEmu.UnitTests.csproj; do
    if [ -f "$wt/$cand" ]; then CSPROJ="$cand"; break; fi
  done
  [ -z "$CSPROJ" ] && { echo "no test project in worktree at $rev" >&2; return 2; }
  ( cd "$wt" && dotnet build "$CSPROJ" -c Release --nologo -v q >"$OUT/$dest/build.log" 2>&1 ) || { echo "build failed at $rev (see $OUT/$dest/build.log)" >&2; tail -20 "$OUT/$dest/build.log" >&2; return 2; }
  # Pilot data: the BeforeTest hook requires compact.sqlite3 in the test
  # output dir; worktrees lack it, so seed the canonical DB post-build.
  PDB="$REPO_ROOT/AAEmu.Game/Data/compact.sqlite3"
  [ -f "$PDB" ] || PDB="$REPO_ROOT/AAEmu.UnitTests/bin/Release/net10.0/Data/compact.sqlite3"
  if [ -f "$PDB" ]; then
    for outdir in "$wt"/AAEmu.UnitTests/bin/Release/net10.0/Data; do
      mkdir -p "$outdir" && cp "$PDB" "$outdir/compact.sqlite3" 2>/dev/null || true
    done
  fi
  local n=0
  while [ $n -lt "$RUNS" ]; do
    n=$((n+1))
    # NOTE: combined '|' treenode-filters under-collect, so each held-out
    # class runs separately; logs are concatenated for collect.
    for cls in HeldoutScenarioTests HeldoutRecoveryTests; do
      ( cd "$wt" && dotnet test --project "$CSPROJ" -c Release --no-build \
        --treenode-filter "'/*/*/$cls/*'" \
        --report-trx >"$OUT/$dest/run$n.$cls.log" 2>&1 ) || true
    done
    cat "$OUT/$dest"/run$n.*.log >"$OUT/$dest/run$n.log"
    # Frozen gate: fixed neighbor set run once per revision; failures count as regressions.
    if [ ! -f "$OUT/$dest/frozen.log" ]; then
      ( cd "$wt" && dotnet test --project "$CSPROJ" -c Release --no-build \
        --treenode-filter "'/*/*/QuestObjectiveCombatTests/*'" \
        --report-trx >"$OUT/$dest/frozen.log" 2>&1 ) || true
    fi
    python3 tools/score_brain.py collect --log "$OUT/$dest/run$n.log" --frozen-log "$OUT/$dest/frozen.log" --out "$OUT/$dest/run$n.json" || { echo "collect failed run $n at $rev" >&2; return 2; }
  done
}
run_side "$PARENT" parent || exit 2
run_side "$CHILD" child || exit 2
python3 tools/score_brain.py compare --suite "$SUITE_DIR" --parent-dir "$OUT/parent" --child-dir "$OUT/child" --runs "$RUNS"
