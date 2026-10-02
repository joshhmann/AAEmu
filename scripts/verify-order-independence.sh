#!/usr/bin/env bash
# AAEmu fork — order-independence / per-process-determinism verifier for
# AAEmu.UnitTests.
#
# Usage:
#   ./scripts/verify-order-independence.sh                  # Mode A, N=2
#   ./scripts/verify-order-independence.sh 3                # Mode A, N=3
#   REPEAT=5 ./scripts/verify-order-independence.sh         # Mode A, N=5
#   ./scripts/verify-order-independence.sh --isolate PvpFlaggingRigTests
#   ./scripts/verify-order-independence.sh --isolate --max-classes 20
#   ./scripts/verify-order-independence.sh --help
#
# WHY THIS EXISTS
#   A single full-suite run cannot see two defect classes found by inspection
#   on 2026-10-01:
#     1. Cold-start singleton throw. A test that dereferences
#        SkillManager.Instance without seeding it throws from Singleton.OnInit
#        when it runs first in a process, but PASSES in the full suite because
#        a sibling test happened to construct the singleton earlier. TUnit
#        1.54 schedules nondeterministically: [NotInParallel] +
#        ParallelLimiter<Limit=1> serialize but do NOT sequence, and the
#        assembly declares no [Collection].
#     2. Hash-collision aliasing. A rig derived Character.Id from
#        string.GetHashCode(), randomized per process, so which names collide
#        CHANGED ON EVERY RUN — the same command failed roughly 1 run in 10.
#        A single full-suite run was not merely incomplete there, it was
#        actively unreliable: you cannot detect that defect by repeating it.
#
# WHAT MODE A PROVES
#   The suite yields an identical PASS/FAIL verdict set across N runs in
#   SEPARATE processes — so no verdict depends on TUnit's per-process
#   scheduling or on the per-process randomized string hash seed, to the
#   resolution of N samples.
# WHAT MODE B PROVES
#   For each inspected class, every test in it yields the same verdict alone
#   in its own process as it does inside the full suite. A test that fails
#   alone but passes in the suite carries an undeclared ordering contract on
#   its siblings — the §9.3 signature.
#
# WHAT THIS PROVES NOTHING ABOUT
#   Gameplay correctness. Live server/client behaviour. The
#   AAEmu.IntegrationTests or AAEmu.Login.IntegrationTests lanes. Defects
#   rarer than the 1-in-N sampling rate. A defect that is deterministic BOTH
#   alone and in the suite: it fails both runs identically, so this script's
#   diff stays empty and only the run's own failed count sees it. And with
#   --max-classes, Mode B only speaks for the classes it actually ran: a
#   class whose ordering contract needs the whole suite is outside any
#   single-class isolation run by construction.
#
# Read-only: no tracked source, test or data file is modified. Writes only to
# --log-dir and --out (default /tmp/aaemu-order-independence — the repo has
# no Logs/ ignore entry, so an in-repo log dir would be permanent untracked
# noise; pass --log-dir/--out deliberately if you want a durable in-repo
# record). dotnet stderr is preserved in the per-run logs.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# ---------------------------------------------------------------- defaults
# The literal command line is captured BEFORE the parser consumes "$@", so the
# report records exactly what was typed. REPEAT_FROM_ENV records that the
# count came from the environment rather than argv, because "the command
# line" and "what the script actually honoured" are different facts and a
# report that conflates them is not evidence.
MODE="repeat"
REPEAT="${REPEAT:-2}"
REPEAT_FROM_ENV="${REPEAT:-}"
FILTER=""
MAX_CLASSES=0
# Per-method isolation. Default OFF (it costs one process per test) but
# auto-ON when a single class is named, because that is the only granularity
# that catches a cold-start singleton throw inside a class whose other
# methods construct the singleton first. This is not a guess: verified
# against a reintroduced-hazard copy on 2026-10-02, where the class-level run
# was green 11/11 and the one cold-start test threw on its own.
ISOLATE_METHODS="${VOI_ISOLATE_METHODS:-}"
LOG_DIR="${VOI_LOG_DIR:-/tmp/aaemu-order-independence}"
OUT="${VOI_OUT:-}"
BUILD="auto"
WANT_HELP=0
VOI_ARGV="$0"
for a in "$@"; do
  VOI_ARGV="$VOI_ARGV $a"
done
usage() {
  cat <<'USAGE'
verify-order-independence.sh — order-independence / per-process determinism
                              verifier for AAEmu.UnitTests

  ./scripts/verify-order-independence.sh [N] [options]
  REPEAT=N ./scripts/verify-order-independence.sh [options]

OPTIONS
  --isolate [CLASS]        Mode B. Run each matching test class ALONE in its
                           own process and diff it against the full suite.
                           CLASS is a class-name substring used as
                           --treenode-filter "/*/*/<CLASS>/*".
                           Omitting CLASS means ALL classes and is SLOW: one
                           `dotnet test` process per class (measured ~6 s each
                           on this box; 400 classes today). The class count,
                           the test count and the time estimate are printed
                           BEFORE the fan-out starts, and the resolved class
                           list is written to a file whose path is printed, so
                           the cost is never a surprise. For one class use
                           --isolate <ClassName> (seconds).
  --isolate-methods        Mode B only. ALSO run every test of each class
                           alone, one process per test.
  --no-isolate-methods     Mode B only. Class-level isolation only. This is
                           cheaper but CANNOT catch a cold-start singleton
                           throw masked by a sibling method in the same class
                           — see the note below. Default: ON when a single
                           class is named, OFF for the all-classes sweep.
  --max-classes N          Mode B only. Stop after N classes. 0 = no cap.
                           A capped run speaks only for the classes it ran.

WHY METHOD ISOLATION EXISTS (not theoretical — measured 2026-10-02)
  Against a copy with the 2026-10-01 cold-start guard removed,
  `PvpFlaggingRigTests` as a CLASS passed 11/11 green, while
  `DoDie_MultiParticipantAssists_ComprehensiveDamageHealAndCc` run ALONE
  threw "SkillManager has no parameterless constructor" from
  Singleton.OnInit. The class's own other ten methods construct the
  singleton first, so class-level isolation reproduces the original defect
  exactly: green in isolation, red only per method. Mode B therefore runs
  both granularities.

  MODE A OPTIONS
  --repeat N, or bare N    Mode A. Number of full-suite processes. 1 is
                           accepted but proves nothing and says so; use 2+.
  --log-dir DIR            Keep per-run logs and trx files here. NEVER
                           deleted: a verdict with no artifact is not
                           evidence. Default /tmp/aaemu-order-independence
  --out FILE               JSON summary path. Default
                           <log-dir>/order-independence-report.<stamp>.json
  --build | --no-build     Force a Release build first, or force reuse of the
                           existing bin/. Default: build once, then reuse.
                           --no-build is correctness-neutral for the property
                           under test: both paths execute the same compiled
                           TUnit assembly in a fresh process; --no-build only
                           skips MSBuild. It does mean a stale bin/ tests
                           stale code, so the report records the flag and the
                           assembly mtime.
  --help                   This text. Works under `set -u` with no arguments.

EXIT CODE
  0  no order-dependence or per-process nondeterminism detected
  1  a defect was detected: a test that flips between runs, or an
     inconsistency between isolated and full-suite execution
  2  the verifier could not run (build failure, no tests discovered,
     unparseable output) — an infrastructure failure is NOT a green gate
USAGE
}

# ---------------------------------------------------------------- arg parse
# Shift-based, so "$@" is always the unconsumed remainder and the loop
# condition is `[ "$#" -gt 0 ]` — no array, no unbound-index hazard, and
# --help with zero arguments is the same code path as every other invocation.
while [ "$#" -gt 0 ]; do
  case "${1:-}" in
    --help|-h)
      WANT_HELP=1
      ;;
    --isolate)
      MODE="isolate"
      # An optional bare word after --isolate is the class filter; an option
      # or the end of the line means "all classes".
      if [ "$#" -gt 1 ] && [ "${2:-}" = "${2#--}" ]; then
        FILTER="$2"
        shift
      fi
      ;;
    --isolate-methods)
      MODE="isolate"
      ISOLATE_METHODS=1
      ;;
    --no-isolate-methods)
      MODE="isolate"
      ISOLATE_METHODS=0
      ;;
    --max-classes)
      MODE="isolate"
      if [ "$#" -gt 1 ]; then
        MAX_CLASSES="${2:-}"
        shift
      fi
      ;;
    --repeat)
      if [ "$#" -gt 1 ]; then
        REPEAT="${2:-}"
        shift
      fi
      ;;
    --log-dir)
      if [ "$#" -gt 1 ]; then
        LOG_DIR="${2:-$LOG_DIR}"
        shift
      fi
      ;;
    --out)
      if [ "$#" -gt 1 ]; then
        OUT="${2:-$OUT}"
        shift
      fi
      ;;
    --no-build) BUILD="no" ;;
    --build) BUILD="yes" ;;
    --) ;;
    -*)
      echo "verify-order-independence.sh: unknown option '${1:-}'" >&2
      usage >&2
      exit 2
      ;;
    *) REPEAT="${1:-$REPEAT}" ;;
  esac
  shift
done

if [ "$WANT_HELP" -eq 1 ]; then
  usage
  exit 0
fi

# Method isolation defaults ON when exactly one class is named (seconds of
# extra cost, and the granularity that actually catches the §9.3 class) and
# OFF for the all-classes sweep (one extra process per test is prohibitive).
if [ -z "$ISOLATE_METHODS" ]; then
  if [ -n "$FILTER" ]; then
    ISOLATE_METHODS=1
  else
    ISOLATE_METHODS=0
  fi
fi

case "$REPEAT" in
  ''|*[!0-9]*)
    echo "verify-order-independence.sh: REPEAT must be a positive integer, got '$REPEAT'" >&2
    exit 2
    ;;
esac
[ "$REPEAT" -ge 1 ] || { echo "REPEAT must be >= 1, got '$REPEAT'" >&2; exit 2; }
case "$MAX_CLASSES" in
  ''|*[!0-9]*)
    echo "verify-order-independence.sh: --max-classes must be a non-negative integer, got '$MAX_CLASSES'" >&2
    exit 2
    ;;
esac
case "$FILTER" in
  */**)
    echo "verify-order-independence.sh: --isolate takes a bare class name, not a treenode-filter path: '$FILTER'" >&2
    exit 2
    ;;
esac

# ---------------------------------------------------------------- provenance
START_EPOCH_MS="$(date -u +%s%3N)"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
ISOLATION_NOTE="not requested"
SELF="$(realpath "${BASH_SOURCE[0]}")"
SELF_REL="$(realpath --relative-to="$repo_root" "$SELF" 2>/dev/null || printf 'scripts/verify-order-independence.sh')"
# VOI_ARGV was captured verbatim above, before the parser consumed "$@".
if [ -n "$REPEAT_FROM_ENV" ]; then
  VOI_ARGV="$VOI_ARGV (REPEAT env was $REPEAT_FROM_ENV)"
fi

case "$LOG_DIR" in /*) ;; *) LOG_DIR="$repo_root/$LOG_DIR" ;; esac
mkdir -p "$LOG_DIR"
if [ -z "$OUT" ]; then
  OUT="$LOG_DIR/order-independence-report.$STAMP.json"
fi
case "$OUT" in /*) ;; *) OUT="$repo_root/$OUT" ;; esac
mkdir -p "$(dirname "$OUT")"
DEFAULT_OUT="$LOG_DIR/order-independence-report.$STAMP.json"

# ---------------------------------------------------------------- trx reader
# One shared parser for every run's trx. TRX is the authoritative source:
# it carries the class name (the treenode-filter key, and the only way to
# disambiguate a method name shared by two classes — 40+ of them exist in
# this assembly) plus a per-test outcome. The console log carries neither
# reliably, and its "skipped" line is the only place the skip REASON
# survives in plain text, so both are read.
#
# TUnit reports a skipped test as outcome="NotExecuted" with the reason in
# <DebugTrace>Skipped: ...</DebugTrace>; "Skipped" is the TRX vocabulary but
# TUnit does not emit it. Both are accepted.
trx_to_json() {
  python3 - "$1" <<'PY'
import json
import re
import sys

path = sys.argv[1]
with open(path, encoding="utf-8", errors="replace") as fh:
    doc = fh.read()

classes = {}
defs = re.search(r"<TestDefinitions>(.*?)</TestDefinitions>", doc, re.S)
if defs:
    for chunk in re.findall(r"<UnitTest\b[^>]*>.*?</UnitTest>", defs.group(1), re.S):
        uid = re.search(r'id="([^"]+)"', chunk)
        tm = re.search(r'<TestMethod\b[^>]*className="([^"]*)"[^>]*name="([^"]*)"', chunk)
        if uid and tm:
            classes[uid.group(1)] = (tm.group(1), tm.group(2))

results = []
for m in re.finditer(r"<UnitTestResult\b([^>]*?)(?:/>|>(.*?)</UnitTestResult>)", doc, re.S):
    attrs, inner = m.group(1), m.group(2) or ""
    tid = re.search(r'testId="([^"]+)"', attrs)
    name = re.search(r'testName="([^"]+)"', attrs)
    outcome = re.search(r'outcome="([^"]+)"', attrs)
    if not (tid and outcome):
        continue
    cls, method = classes.get(tid.group(1), ("", name.group(1) if name else ""))
    short = cls.rsplit(".", 1)[-1] if cls else ""
    note = ""
    trace = re.search(r"<DebugTrace>(.*?)</DebugTrace>", inner, re.S)
    if trace:
        note = trace.group(1).strip()
    results.append({
        "outcome": outcome.group(1),
        "class": short,
        "method": method,
        "id": f"{short}.{method}" if short else method,
        "note": note,
    })
results.sort(key=lambda r: r["id"])
json.dump({"trx": path, "results": results}, sys.stdout)
PY
}

# ---------------------------------------------------------------- test runner
# run_tests <label> <extra dotnet-test args...>
# Never returns nonzero: a failing suite is a RESULT to report, not a reason
# to lose the report. Per-run state lands in RUN_TOTAL / RUN_PASSED /
# RUN_FAILED / RUN_SKIPPED / RUN_RC and in <label>.<stamp>.{log,rc,json}.
RUN_TOTAL=0
RUN_PASSED=0
RUN_FAILED=0
RUN_SKIPPED=0
RUN_RC=0
run_tests() {
  local label="$1"
  shift
  local log="$LOG_DIR/$label.$STAMP.log"
  local results="$LOG_DIR/$label.$STAMP.results"
  mkdir -p "$results"
  local cmd=(dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj
             --configuration Release --report-trx --report-trx-filename "$label.trx"
             --results-directory "$results" --no-progress "$@")
  printf '  [%s] %s\n' "$label" "${cmd[*]}"
  RUN_RC=0
  "${cmd[@]}" >"$log" 2>&1 || RUN_RC=$?
  printf '%s\n' "$RUN_RC" >"$log.rc"
  RUN_TOTAL="$(grep -m1 -E '^  total: ' "$log" | tr -dc '0-9' || true)"
  RUN_PASSED="$(grep -m1 -E '^  succeeded: ' "$log" | tr -dc '0-9' || true)"
  RUN_FAILED="$(grep -m1 -E '^  failed: ' "$log" | tr -dc '0-9' || true)"
  RUN_SKIPPED="$(grep -m1 -E '^  skipped: ' "$log" | tr -dc '0-9' || true)"
  RUN_TOTAL="${RUN_TOTAL:-0}"; RUN_PASSED="${RUN_PASSED:-0}"
  RUN_FAILED="${RUN_FAILED:-0}"; RUN_SKIPPED="${RUN_SKIPPED:-0}"
  local trx
  trx="$(ls -1 "$results"/*.trx 2>/dev/null | head -1 || true)"
  if [ -n "$trx" ] && [ -s "$trx" ]; then
    trx_to_json "$trx" >"$log.json"
  else
    printf '{"trx": null, "results": []}\n' >"$log.json"
  fi
  echo "  [$label] total=$RUN_TOTAL passed=$RUN_PASSED failed=$RUN_FAILED skipped=$RUN_SKIPPED rc=$RUN_RC"
}

jqp() { python3 -c "$1" "${@:2}"; }
ids_with_outcome() {
  jqp 'import json,sys
d=json.load(open(sys.argv[1]))
print("\n".join(sorted({r["id"] for r in d["results"] if r["outcome"]==sys.argv[2]})))' "$@"
}
ids_all() {
  jqp 'import json,sys
d=json.load(open(sys.argv[1]))
print("\n".join(sorted({r["id"] for r in d["results"]})))' "$@"
}
has_trx() { jqp 'import json,sys
d=json.load(open(sys.argv[1]))
print(1 if d["trx"] else 0)' "$1"; }

# skip_reason <trx.json> <console.log> <Class.Method>
# TUnit writes the skip reason into the trx <DebugTrace>; the console log's
# indented line after `skipped <name> (...)` carries the same text. Prefer the
# trx (unambiguous per-test), fall back to the log, and never print an empty
# reason: a verdict with an unexplained skip is not evidence.
skip_reason() {
  jqp 'import json,re,sys
reason=""
try:
    d=json.load(open(sys.argv[1]))
    for r in d["results"]:
        if r["id"]==sys.argv[3] and r.get("note"):
            reason=r["note"]
            break
except Exception:
    pass
if not reason:
    try:
        lines=open(sys.argv[2],encoding="utf-8",errors="replace").read().splitlines()
    except OSError:
        lines=[]
    method=sys.argv[3].rsplit(".",1)[-1]
    pat=re.compile(r"^skipped %s \(\d" % re.escape(method))
    for i,line in enumerate(lines):
        if pat.match(line):
            for follow in lines[i+1:i+4]:
                if follow.startswith("  from ") or not follow.strip():
                    break
                reason=follow.strip()
                break
            break
if reason.lower().startswith("skipped:"):
    reason=reason[len("skipped:"):].strip()
print(reason or "(reason not recorded by the runner)",end="")' "$1" "$2" "$3"
}

# ---------------------------------------------------------------- build
BUILD_ARGS=()
BUILD_STATUS="skipped (--no-build)"
BUILD_LOG=""
if [ "$BUILD" != "no" ]; then
  BUILD_LOG="$LOG_DIR/build.$STAMP.log"
  echo "== Release build (once; later runs use --no-build) =="
  BUILD_STATUS="succeeded"
  dotnet build --configuration Release AAEmu.UnitTests/AAEmu.UnitTests.csproj \
    >"$BUILD_LOG" 2>&1 || true
  if grep -qE ': error ' "$BUILD_LOG"; then
    BUILD_STATUS="failed"
  fi
  tail -3 "$BUILD_LOG" || true
  echo "  build log: $BUILD_LOG"
  if [ "$BUILD_STATUS" = "failed" ]; then
    echo "BUILD FAILED — infrastructure failure, not a verdict." >&2
    exit 2
  fi
  BUILD_ARGS+=(--no-build)
  echo
fi

# ---------------------------------------------------------------- header
MODE_LABEL="$MODE"
[ "$MODE" = "isolate" ] && [ -n "$FILTER" ] && MODE_LABEL="isolate:$FILTER"
echo "== order-independence verifier =="
echo "  mode           : $MODE_LABEL"
echo "  repeat (Mode A): $REPEAT"
echo "  filter         : ${FILTER:-<all classes>}"
GIT_HEAD="$(git -C "$repo_root" rev-parse --verify HEAD 2>/dev/null | head -1 || true)"
GIT_HEAD="${GIT_HEAD:-UNKNOWN-no-commits-in-this-repository}"
echo "  head           : $GIT_HEAD (${#GIT_HEAD} chars)"
echo "  dirty paths    : $(git -C "$repo_root" status --porcelain 2>/dev/null | wc -l | tr -d ' ')"
echo "  compact md5    : $(md5sum "$repo_root/AAEmu.Game/Data/compact.sqlite3" 2>/dev/null | cut -d' ' -f1 || echo absent)"
echo "  dotnet         : $(dotnet --version 2>/dev/null || echo unknown)  host=$(hostname 2>/dev/null || echo unknown)"
echo "  assembly mtime : $(stat -c '%y' "$repo_root/AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll" 2>/dev/null | cut -d'.' -f1 || echo absent)"
echo "  script         : $SELF_REL  sha256=$(sha256sum "$SELF" 2>/dev/null | cut -d' ' -f1 || echo unknown)"
echo "  log dir        : $LOG_DIR"
echo "  json report    : $OUT"
echo

# ---------------------------------------------------------------- mode A
ORDER_DEPENDENT=0
INFRA_FAIL=0
run_mode_a() {
  local i base only_this only_first both
  echo "== Mode A: $REPEAT full-suite run(s), each in its own process =="
  if [ "$REPEAT" -lt 2 ]; then
    echo "  WARNING: REPEAT=$REPEAT cannot detect nondeterminism; an intermittent"
    echo "  defect needs >= 2 independent processes. This run proves nothing."
  fi
  echo
  for i in $(seq 1 "$REPEAT"); do
    run_tests "modeA-run$i" "${BUILD_ARGS[@]+"${BUILD_ARGS[@]}"}"
    base="$LOG_DIR/modeA-run$i.$STAMP"
    [ "$(has_trx "$base.log.json")" = "1" ] || INFRA_FAIL=1
    ids_with_outcome "$base.log.json" Failed | sort -u >"$base.failed"
    ids_with_outcome "$base.log.json" NotFound | sort -u >>"$base.failed"
    { ids_with_outcome "$base.log.json" Skipped
      ids_with_outcome "$base.log.json" NotExecuted; } | sort -u >"$base.skipped"
    ids_all "$base.log.json" | sort -u >"$base.all"
    # A failure in ANY run is a defect. The diff below is what adds
    # "nondeterministic" on top; a deterministically red suite is not green.
    # -s is wrong: an EMPTY failing set is written as a single blank line
    # (ids_with_outcome prints "\n".join([])), so the file is non-empty even
    # when zero tests failed and every green suite would be reported as a
    # defect. Test the CONTENT — does any non-whitespace line exist?
    if grep -q '[^[:space:]]' "$base.failed"; then ORDER_DEPENDENT=1; fi
    echo
  done

  echo "== Per-run results =="
  printf '%-5s %-7s %-7s %-7s %-7s %-4s %-9s %s\n' \
    run total passed failed skipped rc duration log
    for i in $(seq 1 "$REPEAT"); do
      base="$LOG_DIR/modeA-run$i.$STAMP"
      printf '%-5s %-7s %-7s %-7s %-7s %-4s %-9s %s\n' \
        "$i" \
        "$(grep -m1 -E '^  total: ' "$base.log" | tr -dc '0-9')" \
        "$(grep -m1 -E '^  succeeded: ' "$base.log" | tr -dc '0-9')" \
        "$(grep -m1 -E '^  failed: ' "$base.log" | tr -dc '0-9')" \
        "$(grep -m1 -E '^  skipped: ' "$base.log" | tr -dc '0-9')" \
        "$(cat "$base.log.rc")" \
        "$(grep -m1 -E '^  duration: ' "$base.log" | sed 's/^  duration: //' | cut -c1-9)" \
        "$base.log"
    done
  echo

  echo "== Skip identity (AGENTS.md requires name AND reason) =="
  for i in $(seq 1 "$REPEAT"); do
    base="$LOG_DIR/modeA-run$i.$STAMP"
    echo "  run $i:"
    if [ ! -s "$base.skipped" ]; then
      echo "    (no skipped tests)"
    else
      while IFS= read -r t; do
        [ -n "$t" ] || continue
        # Reason from the trx (<DebugTrace>Skipped: ...</DebugTrace>), which is
        # where TUnit puts it; fall back to the console log's skip line.
        reason="$(skip_reason "$base.log.json" "$base.log" "$t")"
        echo "    $t"
        echo "      reason: $reason"
      done <"$base.skipped"
    fi
  done
  echo

  echo "== Failing-set diff across runs =="
  if [ "$REPEAT" -lt 2 ]; then
    echo "  (REPEAT=$REPEAT: fewer than 2 runs, no comparison possible)"
  else
    for i in $(seq 2 "$REPEAT"); do
      base1="$LOG_DIR/modeA-run1.$STAMP"
      basei="$LOG_DIR/modeA-run$i.$STAMP"
      only_this="$(comm -13 "$base1.failed" "$basei.failed" || true)"
      only_first="$(comm -23 "$base1.failed" "$basei.failed" || true)"
      both="$(comm -12 "$base1.failed" "$basei.failed" || true)"
      # The flip check is on the FULL outcome set, not just Failed: a test
      # that goes Passed -> Skipped (or vice versa) across processes is just
      # as order-dependent as one that starts failing, and a Failed-only
      # diff would miss it.
      outcome_map1="$base1.outcomes"
      outcome_mapi="$basei.outcomes"
      jqp 'import json,sys
d=json.load(open(sys.argv[1]))
print("\n".join(r["id"] + "\t" + r["outcome"] for r in d["results"]))' \
        "$base1.log.json" | sort >"$outcome_map1"
      jqp 'import json,sys
d=json.load(open(sys.argv[1]))
print("\n".join(r["id"] + "\t" + r["outcome"] for r in d["results"]))' \
        "$basei.log.json" | sort >"$outcome_mapi"
      verdict_flips="$(join -t$'\t' -j1 "$outcome_map1" "$outcome_mapi" \
        | awk -F'\t' '$2 != $3 {print $1 ": " $2 " -> " $3}')"
      echo "  run 1 vs run $i"
      echo "    failed only in run $i  (flips between runs — nondeterministic/order-dependent):"
      if [ -z "$only_this" ]; then echo "      (none)"; else printf '%s\n' "$only_this" | sed 's/^/      /'; fi
      echo "    failed only in run 1  (same hazard, opposite face):"
      if [ -z "$only_first" ]; then echo "      (none)"; else printf '%s\n' "$only_first" | sed 's/^/      /'; fi
      echo "    failed in both        (deterministic failures):"
      if [ -z "$both" ]; then echo "      (none)"; else printf '%s\n' "$both" | sed 's/^/      /'; fi
      echo "    any outcome flip      (any verdict change across processes):"
      if [ -z "$verdict_flips" ]; then echo "      (none)"; else printf '%s\n' "$verdict_flips" | sed 's/^/      /'; fi
      [ -z "$only_this" ] && [ -z "$only_first" ] && [ -z "$verdict_flips" ] \
        || ORDER_DEPENDENT=1
    done
  fi
  echo
}

# bucket_run <isolated.json> <Class> <fullsuite.json> <scope> <logname> [<Method>]
# Emits one "bucket<TAB>Class<TAB>Class.Method<TAB>log" row per test the
# isolated run executed, plus a row per full-suite test IN SCOPE that the
# isolated run never executed. Both non-consistent buckets are the same
# property measured two ways:
#   fails-alone-passes-in-suite  the test only works with siblings present
#   passes-alone-fails-in-suite  the test only works inside the full suite
#
# <Method> is required for scope=method. Without it a single-test run would
# report every OTHER test in the class as "not executed here" and therefore
# as failing-in-isolation — a fabricated 10-test defect out of a 1-test run.
# The scope of a method pass is exactly that one test.
bucket_run() {
  jqp 'import json,sys
iso=json.load(open(sys.argv[1]))
short=sys.argv[2]
full=json.load(open(sys.argv[3]))
scope=sys.argv[4]
src=sys.argv[5]
only=sys.argv[6] if len(sys.argv) > 6 else ""
fullmap={r["id"]: r["outcome"] for r in full["results"]}
bad={"Failed","NotFound"}
prefix=short + "."
scoped=[i for i in fullmap if i == prefix + only] if only \
    else [i for i in fullmap if i.startswith(prefix)]
scoped=set(scoped)
mine={r["id"] for r in iso["results"] if r["id"] in scoped}
if not mine:
    # The filter matched nothing in scope (parameterized name, renamed
    # method, runner quirk). Reporting the scope as "fails alone" would be a
    # fabricated defect, so emit one explicit row and let the caller report
    # the run as inconclusive.
    print("no-tests-matched\t"+short+"\t(no test ran under this filter)\t"+src)
    raise SystemExit(0)
rows=[]
for r in sorted(iso["results"], key=lambda r: r["id"]):
    if r["id"] not in mine:
        continue
    here=r["outcome"] in bad
    there=fullmap.get(r["id"]) in bad
    if here and not there:
        rows.append("fails-alone-passes-in-suite\t"+short+"\t"+r["id"]+"\t"+src)
    elif there and not here:
        rows.append("passes-alone-fails-in-suite\t"+short+"\t"+r["id"]+"\t"+src)
    else:
        rows.append("consistent\t"+short+"\t"+r["id"]+"\t"+src)
for t in sorted(scoped - mine):
    if fullmap[t] in bad:
        rows.append("passes-alone-fails-in-suite\t"+short+"\t"+t+"\t"+src)
    else:
        rows.append("fails-alone-passes-in-suite\t"+short+"\t"+t+"\t"+src)
print("\n".join(rows))' "$@"
}

# ---------------------------------------------------------------- mode B
ISOLATION_PROCESSES=0
METHOD_RUNS=0
run_mode_b() {
  local classes_file="$LOG_DIR/isolate-classes.$STAMP.txt"
  local buckets_file="$LOG_DIR/isolate-buckets.$STAMP.tsv"
  local class_count n cls short base full_json
  echo "== Mode B: isolation diff =="
  echo "-- baseline: full suite in a fresh process --"
  run_tests "modeB-fullsuite" "${BUILD_ARGS[@]+"${BUILD_ARGS[@]}"}"
  full_json="$LOG_DIR/modeB-fullsuite.$STAMP.log.json"
  [ "$(has_trx "$full_json")" = "1" ] || INFRA_FAIL=1
  echo

  # Class inventory straight from the baseline trx: the authoritative class
  # names, no source scraping, no extra process.
  if [ -n "$FILTER" ]; then
    jqp 'import json,sys
d=json.load(open(sys.argv[1]))
short=sys.argv[2]
print("\n".join(sorted({r["class"] for r in d["results"] if short in r["class"]})))' \
      "$full_json" "$FILTER" >"$classes_file"
  else
    jqp 'import json,sys
d=json.load(open(sys.argv[1]))
print("\n".join(sorted({r["class"] for r in d["results"]})))' "$full_json" >"$classes_file"
  fi
  class_count="$(grep -c . "$classes_file" || true)"
  class_count="${class_count:-0}"
  test_count="$(jqp 'import json,sys
d=json.load(open(sys.argv[1]))
print(len({r["id"] for r in d["results"]}))' "$full_json")"
  test_count="${test_count:-0}"

  echo "-- class inventory --"
  echo "   filter         : ${FILTER:-<all classes>}"
  echo "   classes found  : $class_count"
  echo "   tests in scope : $test_count"
  if [ "$ISOLATE_METHODS" -eq 1 ]; then
    echo "   method passes  : ON (one extra process per test)"
  else
    echo "   method passes  : OFF (--isolate-methods to enable; this CANNOT"
    echo "                    catch a cold-start throw masked by a sibling"
    echo "                    method inside the same class)"
  fi
  if [ "$MAX_CLASSES" -gt 0 ] && [ "$class_count" -gt "$MAX_CLASSES" ]; then
    echo "   capped at      : $MAX_CLASSES (--max-classes; this run speaks only for those)"
  fi
  if [ -z "$FILTER" ] && [ "$MAX_CLASSES" -eq 0 ] && [ "$class_count" -gt 20 ]; then
    echo "   ESTIMATE       : ~6 s per class x $class_count classes"
    if [ "$ISOLATE_METHODS" -eq 1 ]; then
      echo "                    + ~6 s per test x $test_count tests (method passes ON)"
    fi
    echo "                    This is the slow path and it is opt-in. One class in"
    echo "                    seconds (method isolation included):"
    echo "                      ./scripts/verify-order-independence.sh --isolate PvpFlaggingRigTests"
  fi
  if [ "$class_count" -eq 0 ]; then
    echo "   no class matched '${FILTER}'; nothing to isolate. Check the spelling:"
    echo "   (--list-tests | grep -i <name>; the filter is '/*/*/<Class>/*')." >&2
    ISOLATION_NOTE="no class matched the filter"
    INFRA_FAIL=1
    return 0
  fi
  echo "   class list     : $classes_file"
  echo "   per-class logs : $LOG_DIR/isolate-<Class>.$STAMP.log"
  echo

  : >"$buckets_file"
  n=0
  while IFS= read -r cls; do
    [ -n "$cls" ] || continue
    if [ "$MAX_CLASSES" -gt 0 ] && [ "$n" -ge "$MAX_CLASSES" ]; then
      break
    fi
    n=$((n + 1))
    short="$cls"
    printf '  [%2d/%d] %s\n' "$n" "$class_count" "$short"

    # Pass 1: the class alone. Catches defects that need the REST OF THE
    # SUITE, but not ones that only need a SIBLING METHOD in the same class.
    run_tests "isolate-$short" --treenode-filter "/*/*/$short/*" \
      "${BUILD_ARGS[@]+"${BUILD_ARGS[@]}"}"
    bucket_run "$LOG_DIR/isolate-$short.$STAMP.log.json" "$short" "$full_json" \
      class "isolate-$short.$STAMP.log" >>"$buckets_file"
    ISOLATION_PROCESSES=$((ISOLATION_PROCESSES + 1))

    # Pass 2: each test ALONE, in its own process. This is the pass that
    # catches the 2026-10-01 §9.3 signature, and it is NOT optional
    # decoration: verified against a reintroduced-hazard copy on 2026-10-02,
    # `PvpFlaggingRigTests` as a CLASS passed 11/11 green while
    # `DoDie_MultiParticipantAssists_ComprehensiveDamageHealAndCc` ALONE threw
    # "SkillManager has no parameterless constructor" from Singleton.OnInit.
    # Class-level isolation cannot see that: the class's own other ten
    # methods construct the singleton first. Only per-method isolation can.
    if [ "$ISOLATE_METHODS" -eq 1 ]; then
      while IFS= read -r method; do
        [ -n "$method" ] || continue
        run_tests "iso1m-$short-$method" \
          --treenode-filter "/*/*/$short/$method" \
          "${BUILD_ARGS[@]+"${BUILD_ARGS[@]}"}"
        bucket_run "$LOG_DIR/iso1m-$short-$method.$STAMP.log.json" "$short" \
          "$full_json" method "iso1m-$short-$method.$STAMP.log" "$method" \
          >>"$buckets_file"
        METHOD_RUNS=$((METHOD_RUNS + 1))
        ISOLATION_PROCESSES=$((ISOLATION_PROCESSES + 1))
      done < <(jqp 'import json,sys
d=json.load(open(sys.argv[1]))
short=sys.argv[2]
print("\n".join(sorted(r["method"] for r in d["results"] if r["class"]==short)))' \
        "$full_json" "$short")
    fi
  done <"$classes_file"

  # A test can be reported by BOTH the class pass and the method pass. The
  # merged view deduplicates per (bucket, class, test) so a defect found by
  # two passes is counted once. A test in EITHER defect bucket is a defect
  # regardless of which pass saw it.
  local consistent alone_fail alone_pass compared no_match
  no_match="$(grep -c $'^no-tests-matched\t' "$buckets_file" || true)"
  no_match="${no_match:-0}"
  consistent="$(cut -f1,3 "$buckets_file" | sort -u \
    | grep -c $'^consistent' || true)"
  alone_fail="$(cut -f1,3 "$buckets_file" | sort -u \
    | grep -c $'^fails-alone-passes-in-suite' || true)"
  alone_pass="$(cut -f1,3 "$buckets_file" | sort -u \
    | grep -c $'^passes-alone-fails-in-suite' || true)"
  consistent="${consistent:-0}"; alone_fail="${alone_fail:-0}"; alone_pass="${alone_pass:-0}"
  compared=$((consistent + alone_fail + alone_pass))
  if [ "$no_match" -gt 0 ]; then
    INFRA_FAIL=1
    echo "  WARNING: $no_match isolated run(s) executed ZERO tests of their class."
    echo "  A filter that matches nothing is NOT evidence of consistency:"
    grep $'^no-tests-matched\t' "$buckets_file" | cut -f2,4 \
      | awk -F'\t' '{printf "    %s  [%s]\n", $1, $2}'
    echo
  fi
  echo
  echo "== Isolation buckets =="
  echo "  classes run              : $n of $class_count"
  echo "  method passes            : $METHOD_RUNS (0 = disabled)"
  echo "  distinct tests compared  : $compared"
  echo "  consistent               : $consistent"
  echo "  fails alone, passes suite: $alone_fail   <-- latent order dependence"
  echo "  passes alone, fails suite: $alone_pass   <-- same hazard, opposite face"
  echo
  if [ "$alone_fail" -gt 0 ]; then
    echo "  fails alone, passes in full suite  <-- LATENT ORDER DEPENDENCE:"
    grep $'^fails-alone-passes-in-suite\t' "$buckets_file" | sort -u -t$'\t' -k3 \
      | awk -F'\t' '{printf "    %s  [seen in %s: %s]\n", $3, $2, $4}'
    echo "    first exception line per finding, from the log named above:"
    { grep $'^fails-alone-passes-in-suite\t' "$buckets_file" || true; } \
      | sort -u -t$'\t' -k3 \
      | while IFS=$'\t' read -r _bucket _cls test_id src_log; do
          printf '      %s\n' "$test_id"
          if [ ! -f "$LOG_DIR/$src_log" ]; then
            echo "        (log missing: $LOG_DIR/$src_log)"
            continue
          fi
          # Match the failed line by the bare method name, fixed-string, so a
          # regex metacharacter in a test name cannot break the extraction.
          # awk exits 0 whether or not it matched, so `set -e` is safe.
          awk -v m="${test_id##*.}" '
            index($0, "failed ") == 1 && index($0, m) { hit = 1; print "        " $0; next }
            hit && index($0, "  from ") == 1 { exit }
            hit && NF > 0 { print "        " $0; n++; if (n >= 3) exit }
            END { exit 0 }
          ' "$LOG_DIR/$src_log" || true
        done || true
  fi
  if [ "$alone_pass" -gt 0 ]; then
    echo "  passes alone, fails in full suite  <-- same hazard, opposite face:"
    grep $'^passes-alone-fails-in-suite\t' "$buckets_file" | sort -u -t$'\t' -k3 \
      | awk -F'\t' '{printf "    %s  [seen in %s: %s]\n", $3, $2, $4}'
  fi
  if [ "$alone_fail" -gt 0 ] || [ "$alone_pass" -gt 0 ]; then
    ORDER_DEPENDENT=1
  fi
  ISOLATION_NOTE="$n of $class_count classes isolated, $ISOLATION_PROCESSES dotnet test processes, $METHOD_RUNS method-level passes, $compared distinct tests compared"
}

if [ "$MODE" = "repeat" ]; then
  run_mode_a
else
  run_mode_b
fi

# ---------------------------------------------------------------- report
# Shape follows the lane reports under /root/aaemu-e2e-q0/logs/ (scenario,
# stamp, ...evidence..., verdict, verdictLine, wallSeconds) plus what
# AGENTS.md requires of every Tier report: 40-char HEAD, command, dirty
# state, per-run counts, and the skip identity.
VOI_STAMP="$STAMP" \
VOI_REPO_ROOT="$repo_root" \
VOI_HEAD="$GIT_HEAD" \
VOI_DIRTY="$(git -C "$repo_root" status --porcelain 2>/dev/null | wc -l | tr -d ' ')" \
VOI_DIRTY_PATHS="$(git -C "$repo_root" status --porcelain 2>/dev/null | head -300 || true)" \
VOI_COMPACT_MD5="$(md5sum "$repo_root/AAEmu.Game/Data/compact.sqlite3" 2>/dev/null | cut -d' ' -f1 || echo absent)" \
VOI_DOTNET="$(dotnet --version 2>/dev/null || echo unknown)" \
VOI_HOST="$(hostname 2>/dev/null || echo unknown)" \
VOI_MODE="$MODE_LABEL" \
VOI_REPEAT="$REPEAT" \
VOI_FILTER="$FILTER" \
VOI_MAX_CLASSES="$MAX_CLASSES" \
VOI_BUILD_STATUS="$BUILD_STATUS" \
VOI_BUILD_LOG="$BUILD_LOG" \
VOI_BUILD_MODE="$BUILD" \
VOI_ORDER_DEPENDENT="$ORDER_DEPENDENT" \
VOI_ISOLATION_PROCESSES="$ISOLATION_PROCESSES" \
VOI_METHOD_RUNS="$METHOD_RUNS" \
VOI_ISOLATE_METHODS="$ISOLATE_METHODS" \
VOI_INFRA_FAIL="$INFRA_FAIL" \
VOI_SCRIPT_SHA256="$(sha256sum "$SELF" 2>/dev/null | cut -d' ' -f1 || echo unknown)" \
VOI_SCRIPT_PATH="$SELF_REL" \
VOI_ISOLATION_NOTE="$ISOLATION_NOTE" \
VOI_LOG_DIR="$LOG_DIR" \
VOI_DEFAULT_OUT="$DEFAULT_OUT" \
VOI_OUT_PATH="$OUT" \
VOI_WALL_SECONDS="$(( $(date -u +%s%3N) - START_EPOCH_MS ))" \
export VOI_STAMP VOI_REPO_ROOT VOI_HEAD VOI_DIRTY VOI_DIRTY_PATHS VOI_COMPACT_MD5 \
       VOI_ISOLATION_PROCESSES VOI_METHOD_RUNS VOI_ISOLATE_METHODS \
       VOI_DOTNET VOI_HOST VOI_MODE VOI_REPEAT VOI_FILTER VOI_MAX_CLASSES \
       VOI_BUILD_STATUS VOI_BUILD_LOG VOI_BUILD_MODE VOI_ORDER_DEPENDENT \
       VOI_INFRA_FAIL VOI_SCRIPT_SHA256 VOI_SCRIPT_PATH VOI_ISOLATION_NOTE \
       VOI_LOG_DIR VOI_DEFAULT_OUT VOI_OUT_PATH VOI_ARGV VOI_WALL_SECONDS

python3 <<'PY' >"$LOG_DIR/verdict.$STAMP.txt"
import json
import os
import re
import subprocess

env = os.environ
stamp = env["VOI_STAMP"]
log_dir = env["VOI_LOG_DIR"]
mode = env["VOI_MODE"]
repeat = int(env["VOI_REPEAT"])
flt = env["VOI_FILTER"]
head = env["VOI_HEAD"]

def env_int(name, default=0):
    try:
        return int(env.get(name, "") or default)
    except ValueError:
        return default

def read_lines(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return [ln.strip() for ln in fh if ln.strip()]
    except OSError:
        return []

def read_json(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None

def skip_reason(results, body, test_id):
    """Skip reason: TUnit's trx <DebugTrace> first, console log second."""
    for r in results:
        if r["id"] == test_id and r.get("note"):
            note = r["note"].strip()
            if note.lower().startswith("skipped:"):
                note = note[len("skipped:"):].strip()
            if note:
                return note
    lines = body.splitlines()
    method = test_id.rsplit(".", 1)[-1]
    pat = re.compile(r"^skipped %s \(\d" % re.escape(method))
    for i, line in enumerate(lines):
        if pat.match(line):
            for follow in lines[i + 1:i + 4]:
                if follow.startswith("  from ") or not follow.strip():
                    break
                return follow.strip()
            return "(reason not printed by the runner)"
    return "(skip reason not recorded by the runner)"

def run_record(idx, base, log, kind):
    body = ""
    if os.path.exists(log):
        with open(log, encoding="utf-8", errors="replace") as fh:
            body = fh.read()

    def count(*fields):
        """Runner summary counters. TUnit's Microsoft.Testing.Platform summary
        spells these total/failed/succeeded/skipped; the shell table below
        asks for the same names. Anything unparsed stays None so the report
        shows a gap rather than a fabricated 0."""
        for field in fields:
            m = re.search(r"^  %s: (\d+)\s*$" % field, body, re.M)
            if m:
                return int(m.group(1))
        return None

    m = re.search(r"^  duration: (.*)$", body, re.M)
    duration = m.group(1).strip() if m else ""
    j = read_json(log + ".json")
    results = (j or {}).get("results") or []
    failing = sorted({r["id"] for r in results if r["outcome"] in ("Failed", "NotFound")})
    skipped = sorted({r["id"] for r in results
                      if r["outcome"] in ("Skipped", "NotExecuted")})
    try:
        rc = int(open(log + ".rc", encoding="utf-8").read().strip())
    except (OSError, ValueError):
        rc = None
    outcomes = {}
    for r in results:
        outcomes[r["outcome"]] = outcomes.get(r["outcome"], 0) + 1
    return {
        "run": idx, "kind": kind, "base": base,
        "total": count("total"), "passed": count("succeeded", "passed"),
        "failedCount": count("failed"), "skipped": count("skipped"),
        "rc": rc, "duration": duration, "outcomes": outcomes,
        "log": log, "trx": (j or {}).get("trx"),
        "failingSet": failing,
        "skipIdentities": [{"test": s, "reason": skip_reason(results, body, s)}
                           for s in skipped],
    }


def outcomes_by_test(path):
    """{Class.Method: Outcome} from a run's trx sidecar."""
    j = read_json(path) or {}
    return {r["id"]: r["outcome"] for r in j.get("results") or []}

runs = []
if mode.startswith("isolate"):
    base = "modeB-fullsuite." + stamp
    log = os.path.join(log_dir, base + ".log")
    runs.append(run_record(0, base, log, "full-suite baseline"))
    classes_found = 0
    try:
        classes_found = len(read_lines(os.path.join(log_dir, "isolate-classes." + stamp + ".txt")))
    except OSError:
        pass
    per_class = []
    for row in read_lines(os.path.join(log_dir, "isolate-buckets." + stamp + ".tsv")):
        parts = row.split("\t")
        if len(parts) < 3:
            continue
        bucket, cls, test = parts[0], parts[1], parts[2]
        seen_in = parts[3] if len(parts) > 3 else "unknown"
        # A test can be reported by both the class pass and the method pass.
        # Deduplicate per (bucket, class, test) so a defect found twice is
        # counted once, and keep every log it was seen in.
        rec = next((r for r in per_class if r["class"] == cls), None)
        if rec is None:
            rec = {"class": cls, "consistent": 0, "failsAlonePassesSuite": [],
                   "passesAloneFailsSuite": []}
            per_class.append(rec)
        if bucket == "no-tests-matched":
            rec["noTestsMatchedRuns"] = rec.get("noTestsMatchedRuns", 0) + 1
            rec.setdefault("noTestsMatchedLogs", []).append(seen_in)
            continue
        if bucket == "consistent":
            rec["consistent"] += 1
            continue
        key = ("failsAlonePassesSuite" if bucket == "fails-alone-passes-in-suite"
               else "passesAloneFailsSuite")
        for entry in rec[key]:
            if entry["test"] == test:
                entry["seenIn"].append(seen_in)
                break
        else:
            rec[key].append({"test": test, "seenIn": [seen_in]})
    per_class.sort(key=lambda r: r["class"])
    isolation = {
        "note": env["VOI_ISOLATION_NOTE"],
        "filter": flt or None,
        "maxClasses": env_int("VOI_MAX_CLASSES"),
        "methodIsolation": env_int("VOI_ISOLATE_METHODS") == 1,
        "methodLevelPasses": env_int("VOI_METHOD_RUNS"),
        "isolatedProcesses": env_int("VOI_ISOLATION_PROCESSES"),
        "runsThatMatchedZeroTests": sum(r.get("noTestsMatchedRuns", 0) for r in per_class),
        "classesFound": classes_found,
        "classesRun": len(per_class),
        "perClass": per_class,
        "buckets": {
            "failsAlonePassesSuite": sorted(
                ({"test": e["test"], "class": r["class"], "seenIn": e["seenIn"]}
                 for r in per_class for e in r["failsAlonePassesSuite"]),
                key=lambda e: e["test"]),
            "passesAloneFailsSuite": sorted(
                ({"test": e["test"], "class": r["class"], "seenIn": e["seenIn"]}
                 for r in per_class for e in r["passesAloneFailsSuite"]),
                key=lambda e: e["test"]),
            "consistent": sum(r["consistent"] for r in per_class),
        },
    }
    diff = {"note": "not computed in Mode B", "compared": 0,
            "onlyInRunN": [], "onlyInRun1": [], "inBoth": []}
else:
    for i in range(1, repeat + 1):
        base = "modeA-run%d.%s" % (i, stamp)
        log = os.path.join(log_dir, base + ".log")
        runs.append(run_record(i, base, log, "full suite"))
    classes_found = 0
    isolation = None
    diff = {"onlyInRunN": [], "onlyInRun1": [], "inBoth": [],
            "outcomeFlips": [], "compared": max(0, repeat - 1)}
    # runs[0] is run 1, so consecutive pairs are (0,1), (1,2), ... — that is
    # range(repeat-1), NOT range(1, repeat), which would index runs[repeat].
    for i in range(max(0, repeat - 1)):
        a, b = runs[i], runs[i + 1]
        for t in sorted(set(b["failingSet"]) - set(a["failingSet"])):
            if t not in diff["onlyInRunN"]:
                diff["onlyInRunN"].append(t)
        for t in sorted(set(a["failingSet"]) - set(b["failingSet"])):
            if t not in diff["onlyInRun1"]:
                diff["onlyInRun1"].append(t)
        for t in sorted(set(a["failingSet"]) & set(b["failingSet"])):
            if t not in diff["inBoth"]:
                diff["inBoth"].append(t)
        # A test that changes verdict in ANY direction across processes is
        # order-dependent, not just one that starts failing. Passed <-> Skipped
        # is the same class of defect and a Failed-only diff misses it.
        amap = outcomes_by_test(a["log"] + ".json")
        bmap = outcomes_by_test(b["log"] + ".json")
        for t in sorted(set(amap) & set(bmap)):
            if amap[t] != bmap[t]:
                diff["outcomeFlips"].append(
                    {"test": t, "runA": a["run"], "outcomeA": amap[t],
                     "runB": b["run"], "outcomeB": bmap[t]})

order_dep = env_int("VOI_ORDER_DEPENDENT", 0) == 1
infra = env_int("VOI_INFRA_FAIL", 0) == 1

if infra:
    verdict = "INCONCLUSIVE-INFRA"
elif order_dep:
    verdict = "FAIL-ORDER-DEPENDENT"
else:
    verdict = "PASS-ORDER-INDEPENDENT"

verdict_line = "{v} mode={m} repeat={r} filter={f} build={b} orderDependent={o} infraFail={i}: ".format(
    v=verdict, m=mode, r=repeat, f=flt or "<all>", b=env["VOI_BUILD_STATUS"],
    o=str(order_dep).lower(), i=str(infra).lower())
verdict_line += "; ".join(
    "run{r} {t}/{p}/{f}/{s} (total/passed/failed/skipped) rc={rc} {d}".format(
        r=x["run"], t=x["total"], p=x["passed"], f=x["failedCount"], s=x["skipped"],
        rc=x["rc"], d=x["duration"]) for x in runs)
verdict_line += "; skipped=" + json.dumps([
    {"run": x["run"], **s} for x in runs for s in x["skipIdentities"]])
verdict_line += "; diffOnlyInRunN=" + json.dumps(diff["onlyInRunN"])
verdict_line += "; diffOnlyInRun1=" + json.dumps(diff["onlyInRun1"])
if isolation:
    verdict_line += "; isolationFailsAlone=" + json.dumps(isolation["buckets"]["failsAlonePassesSuite"])
    verdict_line += "; isolationPassesAlone=" + json.dumps(isolation["buckets"]["passesAloneFailsSuite"])
    verdict_line += "; isolationConsistent=" + str(isolation["buckets"]["consistent"])
verdict_line += "; proves order-independence + per-process determinism of AAEmu.UnitTests ONLY"

report = {
    "scenario": "verify-order-independence-" + ("modeB" if mode.startswith("isolate") else "modeA"),
    "stamp": stamp,
    "verdict": verdict,
    "verdictLine": verdict_line,
    "provenance": {
        "head": head,
        "headLength": len(head),
        "headIs40CharSha1": len(head) == 40,
        "dirtyFileCount": env_int("VOI_DIRTY"),
        "dirtyFileCountRecorded": True,
        "dirtyPaths": [p for p in env.get("VOI_DIRTY_PATHS", "").splitlines() if p.strip()],
        "compactSqlite3Md5": env["VOI_COMPACT_MD5"],
        "command": env.get("VOI_ARGV", ""),
        "argv": env.get("VOI_ARGV", "").split(" "),
        "buildMode": env["VOI_BUILD_MODE"],
        "dotnetSdk": env["VOI_DOTNET"],
        "host": env["VOI_HOST"],
        "repoRoot": env["VOI_REPO_ROOT"],
        "scriptPath": env["VOI_SCRIPT_PATH"],
        "scriptSha256": env["VOI_SCRIPT_SHA256"],
        "readOnly": "no tracked source, test or data file is modified; writes only logDir + this report",
    },
    "build": {"status": env["VOI_BUILD_STATUS"], "log": env["VOI_BUILD_LOG"] or None},
    "modeA": None if isolation else {
        "repeat": repeat,
        "runs": runs,
        "failingSets": [{"run": x["run"], "failing": x["failingSet"]} for x in runs],
        "diff": diff,
    },
    "modeB": isolation,
    "skips": [{"run": x["run"], **s} for x in runs for s in x["skipIdentities"]],
    "logDir": log_dir,
    "jsonReport": env["VOI_OUT_PATH"],
    "defaultJsonReport": env["VOI_DEFAULT_OUT"],
    "scope": {
        "proves": [
            "Mode A: the AAEmu.UnitTests PASS/FAIL verdict set is identical across N separate processes, so no verdict depends on TUnit's per-process scheduling or on the randomized per-process string hash seed, to the resolution of N samples",
            "Mode B (class pass): each inspected class's verdict set is identical running the class alone and running it inside the full suite",
            "Mode B (method pass, when enabled): each inspected test's verdict is identical running that test alone in its own process and running it inside the full suite — the granularity that catches a cold-start singleton throw masked by a sibling method",
        ],
        "doesNotProve": [
            "gameplay correctness, or live server/client behaviour of any kind",
            "anything about the AAEmu.IntegrationTests or AAEmu.Login.IntegrationTests lanes, which this script never runs",
            "a defect that is deterministic BOTH alone and in the full suite: it fails identically in both, the diff stays empty, and only the run's own failed count reveals it",
            "defects rarer than the 1-in-N sampling rate — a 1-in-10 flake needs REPEAT>=10 to have a real chance of being sampled, and N=2 catches roughly 20% of such defects at best",
            "with --max-classes, any class not actually run",
            "with method isolation disabled (--no-isolate-methods), any cold-start defect masked by a sibling method in the same class",
        ],
        "readOnly": "this script builds (bin/obj only) and runs tests; it never edits a tracked source, test or data file, never stages, and never touches containers or remote hosts",
    },
    "wallSeconds": env_int("VOI_WALL_SECONDS", 0) / 1000.0,
}

out_path = env["VOI_OUT_PATH"]
default_path = env["VOI_DEFAULT_OUT"]
os.makedirs(os.path.dirname(out_path), exist_ok=True)
with open(out_path, "w", encoding="utf-8") as fh:
    json.dump(report, fh, indent=1)
if os.path.abspath(out_path) != os.path.abspath(default_path):
    os.makedirs(os.path.dirname(default_path), exist_ok=True)
    with open(default_path, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=1)
print(verdict_line)
PY

VERDICT_LINE="$(head -1 "$LOG_DIR/verdict.$STAMP.txt" 2>/dev/null || echo 'verdict text unavailable')"

echo "== Verdict =="
echo "  $VERDICT_LINE"
echo
echo "  HONEST SCOPE — what this run does and does not establish"
echo "  PROVES:"
if [ "$MODE" = "repeat" ]; then
  echo "    * Mode A: the AAEmu.UnitTests PASS/FAIL verdict set is identical across"
  echo "      $REPEAT separate processes. So no verdict depends on TUnit's"
  echo "      per-process scheduling or on the randomized per-process string hash"
  echo "      seed, to the resolution of $REPEAT samples."
fi
if [ "$MODE" = "isolate" ]; then
  echo "    * Mode B (class pass): the classes listed above behave identically"
  echo "      alone and inside the full suite. 'consistent' means no detected"
  echo "      order dependence for THAT class, not that the class is correct."
  if [ "$ISOLATE_METHODS" -eq 1 ]; then
    echo "    * Mode B (method pass, $METHOD_RUNS processes): each test above also"
    echo "      behaves identically alone in its own process. THIS is the pass"
    echo "      that catches a cold-start singleton throw masked by a sibling"
    echo "      method in the same class — class-level isolation cannot, because"
    echo "      the class's other methods construct the singleton first."
  else
    echo "    * Method passes were DISABLED for this run, so a cold-start defect"
    echo "      masked by a sibling method in the same class would NOT appear."
  fi
fi
echo "  DOES NOT PROVE:"
echo "    * gameplay correctness, or live server/client behaviour of any kind."
echo "    * anything about the AAEmu.IntegrationTests or"
echo "      AAEmu.Login.IntegrationTests lanes; this script never runs them."
echo "    * a defect that is deterministic both alone and in the full suite: it"
echo "      fails identically in both, the diff stays empty, and only the run's"
echo "      own failed count reveals it."
if [ "$MODE" = "repeat" ] && [ "$REPEAT" -lt 10 ]; then
  echo "    * defects rarer than 1-in-$REPEAT. A 1-in-10 flake needs REPEAT>=10"
  echo "      to be sampled with any real probability; N=2 catches ~20% of them."
fi
echo "    * (Mode B) any class not actually run, e.g. under --max-classes."
echo "    * a green here is evidence of order-independence, NOT of correctness."
echo
echo "== Artifacts (kept, never deleted) =="
echo "  json report : $OUT"
[ "$OUT" != "$DEFAULT_OUT" ] && echo "  canonical   : $DEFAULT_OUT"
echo "  log dir     : $LOG_DIR"
for f in "$LOG_DIR"/*.log; do
  [ -e "$f" ] && echo "  log         : $f"
done
echo

if [ "$INFRA_FAIL" -ne 0 ]; then
  echo "== INCONCLUSIVE: a run produced no parsable trx, or the filter matched"
  echo "   no class. That is a verifier/infrastructure failure, not a green gate."
  exit 2
fi
if [ "$ORDER_DEPENDENT" -ne 0 ]; then
  echo "== ORDER-DEPENDENCE DETECTED =="
  exit 1
fi
if [ "$MODE" = "repeat" ]; then
  echo "== ORDER-INDEPENDENT: unit suite, $REPEAT separate processes, identical verdict set =="
else
  echo "== ORDER-INDEPENDENT: $ISOLATION_PROCESSES isolated process(es), $METHOD_RUNS method pass(es), no flips =="
fi
exit 0
