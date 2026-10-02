#!/usr/bin/env bash
# ============================================================================
# lint-evidence-claims.sh — check that evidence claims made in the repo's
# documents are backed by artifacts that actually exist on disk.
#
# The rule already exists (AGENTS.md, "Evidence honesty and anti-overclaim gate
# (2026-09-16)" and "Evidence record and gate classification" at AGENTS.md:365):
# every Tier 1/2/3 report MUST include the exact `git rev-parse HEAD` SHA,
# command, environment, counts and artifact path. Nothing enforced it, so a
# document could assert a verdict that no artifact backs and nobody would
# notice. This script is the missing enforcement.
#
# Checks — each finding is {file, line, kind, detail}:
#   CLAIMED-BUT-MISSING              a markdown claim cites an artifact path
#                                    that does not exist on disk
#   GREEN-WITHOUT-SHA                a lane JSON report records a PASS* verdict
#                                    but no 40-hex provenance field
#   ARTIFACT-PREDATES-CLAIM          a claim cites an artifact AND a 40-hex SHA,
#                                    and the artifact's mtime is older than that
#                                    commit's author date (minus tolerance)
#   TRX-CLAIM-CONTRADICTED           a doc claims *.trx do not exist, but a
#                                    repo-wide glob finds them
#   MISSING-DIRTY-FLAG               a lane JSON report records no dirty flag or
#                                    dirty-file count (review)
#   VERDICT-WITHOUT-ARTIFACT-CITED   verdict wording with no artifact path cited
#                                    anywhere in the same section (review)
#
# Read-only. It writes exactly one file — the --out report, default
# /tmp/lint-evidence-claims-<UTC>.json — and prints its path: an unlogged
# verdict is the exact failure this script exists to catch, so its own result
# is always recorded. Nothing else is written, no dependency beyond python3 and
# coreutils, no network, no containers.
#
# Usage:
#   scripts/lint-evidence-claims.sh [flags]
#
# Exit: 0 clean · 1 fatal findings · 2 usage/environment error.
# ============================================================================
set -euo pipefail

readonly PROG="${0##*/}"
readonly VERSION="1.0.0"

die() { printf '%s: %s\n' "$PROG" "$*" >&2; exit 2; }

usage() {
  cat <<EOF
$PROG — lint documented evidence claims against artifacts on disk.

Scans tracked markdown and the configured lane log trees, then writes a
machine-readable JSON report (verdict, findings, counts, provenance of the run
itself). Read-only apart from that single report file.

USAGE
  $PROG [flags]

FLAGS
  --repo-root DIR        repo to scan (default: parent of this script's dir)
  --lane-root DIR        lane base whose '*-lane' siblings are scanned
                         (repeatable; default: /root/aaemu-e2e, so every
                         /root/aaemu-e2e*/logs tree is included)
  --lane-logs DIR        scan this exact lane log directory (repeatable; added
                         to the dirs discovered under --lane-root)
  --md-path PATH         markdown subtree or single file to scan, relative to
                         --repo-root unless absolute (repeatable; default:
                         repo-root *.md, Docs/, scorecard-explorations/, .agents/)
  --out FILE             JSON report path
                         (default: /tmp/lint-evidence-claims-<UTC>.json;
                         inside --repo-root an untracked report is left
                         untracked on purpose, never staged or committed.
                         Use '-' to write nothing — the path is then not printed.)
  --window-days N        judge reports newer than N days only (default 90;
                         0 disables the window). Report age uses file mtime.
  --sha-tolerance-hours N   how much older than its commit an artifact may be
                         before ARTIFACT-PREDATES-CLAIM fires; absorbs clock
                         skew between authoring host and lane host (default 48)
  --md-glob PATTERN      markdown filename glob (repeatable; default: *.md)
  --no-contradiction-checks
                         skip the repo-wide *.trx / harvest-dataset globs
  -h, --help             print this help, exit 0
  --version              print the version, exit 0

EXIT CODES
  0  no fatal findings
  1  at least one CLAIMED-BUT-MISSING, GREEN-WITHOUT-SHA,
     ARTIFACT-PREDATES-CLAIM or TRX-CLAIM-CONTRADICTED finding
  2  usage or environment error

WHAT IT DOES NOT DO
  It proves artifacts EXIST and RECORD provenance (SHA, dirty state). It does
  NOT validate that an artifact's internal verdict is truthful, and it cannot
  prove a test actually exercised what the claim says. A report carrying a SHA
  is not thereby correct; a PASS this script does not challenge may still be
  wrong. Review findings never change the exit status.
EOF
}

# --- defaults ---------------------------------------------------------------
REPO_ROOT=""
declare -a LANE_ROOTS=("/root/aaemu-e2e")
declare -a LANE_LOGS=()
declare -a MD_PATHS=()
declare -a MD_GLOBS=("*.md")
OUT=""
WINDOW_DAYS=90
SHA_TOLERANCE_HOURS=48
CONTRADICTION_CHECKS=1
INVOKED_AS="$0"
declare -a ORIG_ARGV=("$@")

# --- argument parsing -------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)     usage; exit 0 ;;
    --version)     printf '%s %s\n' "$PROG" "$VERSION"; exit 0 ;;
    --repo-root)   REPO_ROOT="${2:-}"; shift 2 ;;
    # --lane-root REPLACES the default on first use, then appends. Without
    # that, a fixture run would silently keep auditing every real lane.
    --lane-root) if [[ ${#LANE_ROOTS[@]} -eq 1 && "${LANE_ROOTS[0]}" == "/root/aaemu-e2e" ]]; then LANE_ROOTS=("${2:-}"); else LANE_ROOTS+=("${2:-}"); fi; shift 2 ;;
    --lane-logs)   LANE_LOGS+=("${2:-}"); shift 2 ;;
    --md-path)     MD_PATHS+=("${2:-}"); shift 2 ;;
    --md-glob)     MD_GLOBS+=("${2:-}"); shift 2 ;;
    --out)         OUT="${2:-}"; shift 2 ;;
    --window-days) WINDOW_DAYS="${2:-}"; shift 2 ;;
    --sha-tolerance-hours) SHA_TOLERANCE_HOURS="${2:-}"; shift 2 ;;
    --no-contradiction-checks) CONTRADICTION_CHECKS=0; shift ;;
    --) shift; break ;;
    *)  printf '%s: unknown flag: %s\n' "$PROG" "$1" >&2; exit 2 ;;
  esac
done

[[ -n "$REPO_ROOT" ]] || REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ -d "$REPO_ROOT" ]] || die "--repo-root is not a directory: '${REPO_ROOT:-}'"
REPO_ROOT="$(cd "$REPO_ROOT" && pwd)"
command -v git >/dev/null 2>&1 || die "git not found on PATH"
command -v python3 >/dev/null 2>&1 || die "python3 not found on PATH"

[[ "$WINDOW_DAYS" =~ ^[0-9]+$ ]] || die "--window-days must be a non-negative integer"
[[ "$SHA_TOLERANCE_HOURS" =~ ^[0-9]+$ ]] || die "--sha-tolerance-hours must be a non-negative integer"

if [[ ${#MD_PATHS[@]} -eq 0 ]]; then
  MD_PATHS=("." "Docs" "scorecard-explorations" ".agents")
fi

# A lane root names a *family* of lanes: /root/aaemu-e2e is itself a lane, and
# /root/aaemu-e2e-<name> are its siblings. Scanning only "$root" would silently
# cover one lane and under-report, so every sibling root is scanned too.
declare -a RESOLVED_LANE_DIRS=()
declare -a LANE_BASES=()
for root in "${LANE_ROOTS[@]}"; do
  [[ -n "$root" && -d "$root" ]] || continue
  [[ -d "$root/logs" ]] && LANE_BASES+=("$(cd "$root" && pwd)")
  parent="$(dirname "$root")"
  prefix="$(basename "$root")"
  while IFS= read -r sibling; do
    [[ -n "$sibling" && "$sibling" != "$root" ]] && LANE_BASES+=("$sibling")
  done < <(find "$parent" -maxdepth 1 -type d -name "${prefix}*" 2>/dev/null | sort)
done
for base in "${LANE_BASES[@]}"; do
  RESOLVED_LANE_DIRS+=("$base/logs")
done
for d in "${LANE_LOGS[@]}"; do
  [[ -n "$d" && -d "$d" ]] && RESOLVED_LANE_DIRS+=("$(cd "$d" && pwd)")
done

# --- provenance of THIS run (recorded in the report, never asserted in prose) --
GIT_HEAD="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || printf 'unknown')"
GIT_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || printf 'unknown')"
DIRTY_COUNT="$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null | grep -c . || true)"
DIRTY_PATHS="$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null || true)"
if [[ "$DIRTY_COUNT" -eq 0 ]]; then
  DIRTY_LABEL="clean"
elif [[ -n "$GIT_HEAD" && -z "$DIRTY_PATHS" ]]; then
  DIRTY_LABEL="clean"
else
  DIRTY_LABEL="dirty (${DIRTY_COUNT} path(s))"
fi

START_EPOCH="$(date -u +%s)"
START_ISO="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
# The invocation is recorded from the ORIGINAL argv, captured before the
# argument loop consumed it — otherwise every run reports "no args", which is
# exactly the provenance gap this script exists to catch.
COMMAND_LINE="$INVOKED_AS"
if [[ ${#ORIG_ARGV[@]} -gt 0 ]]; then
  printf -v COMMAND_LINE '%s %s' "$INVOKED_AS" "${ORIG_ARGV[*]}"
fi

if [[ -z "${OUT:-}" ]]; then
  OUT="/tmp/lint-evidence-claims-$(date -u +%Y%m%dT%H%M%SZ).json"
elif [[ "$OUT" == "-" ]]; then
  OUT="-"                          # documented: write nothing, print nothing
else
  case "$OUT" in
    /*) : ;;
    *)  OUT="$(pwd)/$OUT" ;;
  esac
  # A linter that dirties the tree it audits is worse than no linter.
  case "$OUT" in
    "$REPO_ROOT"/*|"$REPO_ROOT") die "--out must not write inside --repo-root ($OUT); pick a path outside the repo" ;;
  esac
  mkdir -p "$(dirname "$OUT")"
fi

ARGS_FILE="$(mktemp)"
trap 'rm -f "$ARGS_FILE"' EXIT
declare -a NONEMPTY_LANE_DIRS=()
for d in "${RESOLVED_LANE_DIRS[@]}"; do
  [[ -n "$d" ]] && NONEMPTY_LANE_DIRS+=("$d")
done
# Every argument is serialized through one JSON file, so spaces, empty arrays
# and shell quoting can never reach the analysis program as broken syntax.
set -- "$REPO_ROOT" "$START_ISO" "$START_EPOCH" "$GIT_HEAD" "$GIT_BRANCH" \
  "$PROG" "$VERSION" \
  "$DIRTY_COUNT" "$DIRTY_LABEL" "$COMMAND_LINE" "$OUT" \
  "$WINDOW_DAYS" "$SHA_TOLERANCE_HOURS" "$CONTRADICTION_CHECKS" \
  "${NONEMPTY_LANE_DIRS[@]}" "--md-path-sep--" "${MD_PATHS[@]}" \
  "--md-glob-sep--" "${MD_GLOBS[@]}"

python3 - "$ARGS_FILE" "$@" <<'PYARGS'
import json
import sys

v = sys.argv[2:]
nkeys = 14
keys = ("repo_root", "start_iso", "start_epoch", "git_head", "git_branch",
        "tool", "tool_version",
        "dirty_count", "dirty_label", "command_line", "out", "window_days",
        "sha_tolerance_hours", "contradiction_checks")
payload = dict(zip(keys, v[:nkeys]))
tail = v[nkeys:]
end = tail.index("--md-path-sep--")
lane_dirs = tail[:end]
tail = tail[end + 1:]
end = tail.index("--md-glob-sep--")
md_paths, md_globs = tail[:end], tail[end + 1:]
payload["lane_dirs"] = lane_dirs
payload["md_paths"] = md_paths
payload["md_globs"] = md_globs or ["*.md"]
with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump(payload, fh)
PYARGS

# ---------------------------------------------------------------------------
# The analysis itself runs in one python3 process: per-file cost is far higher
# than in shell, and a single process emits the report atomically.
# ---------------------------------------------------------------------------
set +e
python3 - "$ARGS_FILE" <<'PYEOF'
import fnmatch
import glob as globmod
import json
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime, timezone

with open(sys.argv[1], encoding="utf-8") as fh:
    args = json.load(fh)
START_TS = float(args["start_epoch"])
TOOL = args["tool"]
TOOL_VERSION = args["tool_version"]

REPO_ROOT = args["repo_root"]
LANE_DIRS = args["lane_dirs"]
MD_PATHS = args["md_paths"]
MD_GLOBS = args["md_globs"]
WINDOW_DAYS = int(args["window_days"])
SHA_TOL_HOURS = int(args["sha_tolerance_hours"])
TOLERANCE = SHA_TOL_HOURS * 3600.0
WINDOW_CUT = (time.time() - WINDOW_DAYS * 86400) if WINDOW_DAYS > 0 else None
CONTRADICTIONS = bool(int(args["contradiction_checks"]))
OUT_PATH = args["out"]

FATAL_KINDS = ("CLAIMED-BUT-MISSING", "GREEN-WITHOUT-SHA", "ARTIFACT-PREDATES-CLAIM",
               "TRX-CLAIM-CONTRADICTED")
REVIEW_KINDS = ("MISSING-DIRTY-FLAG", "VERDICT-WITHOUT-ARTIFACT-CITED")
ALL_KINDS = FATAL_KINDS + REVIEW_KINDS

findings = []
counts = Counter()


def add(kind, file, line, detail):
    findings.append({"file": file, "line": line, "kind": kind, "detail": detail})
    counts[kind] += 1


SHA_RE = re.compile(r"[0-9a-f]{40}")
SHA_FULL_RE = re.compile(r"^[0-9a-f]{40}$")
SHA_KEY_RE = re.compile(r"sha|rev|commit|head", re.I)
DIRTY_KEY_RE = re.compile(r"dirty", re.I)
VERDICT_KEYS = ("verdict", "gateVerdict", "arbiterGateVerdict")
GREEN_RE = re.compile(r"^\s*PASS\b|^\s*PASS[-/]", re.I)

# Evidence-looking artifacts: run/gate reports, traces, results, archives, lane
# logs. Game-data JSON (npc_spawns.json, Config.json, ...) is not evidence and
# must never be reported as a missing artifact.
# Candidates are extracted by tokenizing on whitespace rather than by one
# regex over the line: a greedy regex happily swallows a markdown table's
# trailing "|", the ellipsis of a shortened name ("-report.json") or a leading
# "<", and every one of those becomes a false "missing artifact".
ARTIFACT_EXT_RE = re.compile(r"\.(?:json|jsonl|trx|zip|log|csv|txt)\Z", re.I)
LANE_LOGS_RE = re.compile(r"^(?:\.?\.?/)?logs/[A-Za-z0-9_.\-/]+$")
LANE_LOGS_PREFIX_RE = re.compile(r"^/?E2E_ROOT/logs/[A-Za-z0-9_.\-/]+$", re.I)
CITE_RE = re.compile(
    r"(?:https?://|git@|file:|[A-Za-z]:\\)[^\s|)\]\"'>]*"
    r"|[A-Za-z0-9_.\-~/%$]*[/~][A-Za-z0-9_.\-/]*"
    r"|[A-Za-z0-9_.\-]+\.(?:json|jsonl|trx|zip|log|csv|txt|tar|gz)"
    r"|lane-logs/[A-Za-z0-9_.\-/]+"
    r"|Logs/[A-Za-z0-9_.\-/]+",
    re.I)
# Game data and configuration are inputs, never evidence of a run.
NOT_EVIDENCE_RE = re.compile(
    r"config|npc_spawns|doodad_spawns|worldgates|spawns|graph|chartemplate|"
    r"battlefields|housing_bindings|slave_attach|manifest|packages|loot|dungeons|"
    r"precache|excludes|npc_behavior|juris|charac|respawn|portal|road|route|"
    r"decks?_volumes|world\.json$|items\.json$|obstacles\.json$|navprobe|"
    r"corridor-probe|aaemu\.log$|json\.log$|readme\.txt$|packet-coverage|"
    r"census-meta|git-head-sha|quest_accept_and_turnin", re.I)
LANE_PLACEHOLDER_RE = re.compile(r"^(?:<[^>]*>|\$\{?\w+\}?|%s|.*\*.*)$")
VERDICT_WORD_RE = re.compile(
    r"\bPASS(?:ED)?\b|\bVERIFIED\b|\bVERIFIES\b|\bPROVED\b|\bPROVEN\b|"
    r"\bCONFIRMED\b|\bGREEN\b", re.I)
FALSE_NEGATIVE_RE = re.compile(
    r"\bnone\s+exist\b|\bnone\s+of\s+which\b|\b(?:none|no)\s+(?:such\s+)?"
    r"(?:exist|exists|present|were\s+found|was\s+found|on\s+disk)\b|"
    r"\bdo(?:es)?\s+not\s+exist\b|\bnot\s+found\b|\bzero\s+(?:such|trx|files)\b",
    re.I)
HARVEST_DATASET_GLOBS = ["*-harvest*.jsonl", "*jev-shadow*.jsonl", "*shadow*.jsonl",
                         "*-report*.jsonl"]
HARVEST_SEARCH_SUBDIRS = ["", "Logs", "runtime/game/Logs"]


# ---------------------------------------------------------------------------
# markdown inventory
# ---------------------------------------------------------------------------
def git_ls(*extra):
    cmd = ["git", "-C", REPO_ROOT, "ls-files", "-z"] + list(extra)
    try:
        raw = subprocess.run(cmd, capture_output=True, text=True, check=False).stdout
    except OSError:
        raw = ""
    return [p for p in raw.split("\0") if p]


tracked_md = git_ls("--", "*.md")
untracked_md = git_ls("--others", "--exclude-standard", "--", "*.md")
md_inventory = sorted(set(tracked_md) | set(untracked_md))


def in_scope(rel):
    for base in MD_PATHS:
        b = base.rstrip("/")
        if b in ("", "."):
            ok = "/" not in rel
        elif rel == b or rel.startswith(b + "/"):
            ok = True          # a subtree scope covers its whole subtree
        else:
            ok = False
        if ok and any(fnmatch.fnmatch(os.path.basename(rel), g) for g in MD_GLOBS):
            return True
    return False


def md_files():
    for rel in md_inventory:
        if in_scope(rel):
            full = os.path.join(REPO_ROOT, rel)
            if os.path.isfile(full):
                yield rel, full


# ---------------------------------------------------------------------------
# candidate extraction and path resolution
# ---------------------------------------------------------------------------
TOKEN_TRIM = "`*_.,;:()<>[]{}|'\"\\"

# A finding needs CITATION INTENT, not merely a filename-shaped token. The
# repo is full of legitimate placeholder names in prose and setup instructions
# ("With valid `items.json`", "dotnet run --project AAEmu.Login"). Counting
# those as unbacked evidence would make the gate un-greenable, and an
# un-greenable gate gets switched off — which is how enforcement dies.
INTENT_LABEL_RE = re.compile(
    r"artifact|evidence|verdict|proof|proven|output|result|"
    r"capture[ds]?\b|telemetry|transcript|harvest|dataset|pointer|"
    r"archive|soak|repro|record|dossier|audit|ledger", re.I)
VERDICT_TOKEN_RE = re.compile(
    r"\bPASS(?:ED|BEHAVIOR|[-/]\w*)?\b|\bFAIL(?:ED)?\b|\bGREEN\b|\bRED\b|"
    r"\bVERIFIED\b|\bUNKNOWN\b|\bINCONCLUSIVE\b", re.I)
# "evidence", "report" and friends as a standalone label, or "X.log" used as
# an artifact name. "with a valid items.json" and "dotnet build AAEmu.Game"
# match neither, which is exactly the separation we want.
ARTIFACT_WORD_RE = re.compile(r"\b[a-z0-9_.\-]*\.(?:log|trx|jsonl)\b", re.I)
EXAMPLE_LABEL_RE = re.compile(r"\be\.?g\.?i,?\b|\bexample\b|\bsample\b", re.I)
RUN_COMMAND_RE = re.compile(r"(^\s*\$\s)|(\bdotnet\b)|(\bdocker\b)|(\bcd\b)|(\bsudo\b)")
# A citation must be a complete file path. "/logs/game*.log" is a glob, not a
# path; only the '*' has to go, never the leading directories.
GLOB_SUFFIX_RE = re.compile(r"\*[^/\\]*$")
TABLE_ROW_RE = re.compile(r"^\s*\|")
COMPLETE_PATH_RE = re.compile(r"[/~]")


def path_shape_ok(token):
    """A citation names a file, not a fragment of one."""
    if token.startswith("-") or token.endswith("-"):
        return False                   # "-trace.jsonl": an elided sibling name

    return bool(COMPLETE_PATH_RE.search(token))


def is_citation_line(line):
    """Does this line present its tokens as evidence, or merely mention them?"""
    stripped = line.lstrip()
    if RUN_COMMAND_RE.search(stripped):
        return False                   # a command, not a citation
    if stripped.startswith(("#", ">", "```")):
        return False                   # heading, quote, code fence
    if EXAMPLE_LABEL_RE.search(stripped):
        return False                   # "e.g. items.json" — an example name
    if re.search(r"\bAAEmu\.[A-Za-z]+[A-Za-z.]*", stripped):
        return False                   # a solution/project path, not evidence
    return bool(INTENT_LABEL_RE.search(stripped)
                or VERDICT_TOKEN_RE.search(stripped)
                or ARTIFACT_WORD_RE.search(stripped)
                or (TABLE_ROW_RE.match(line) and ARTIFACT_EXT_RE.search(stripped)))



def strip_ellipsis(tok):
    """'/root/.omp/.../jev-shadow.jsonl' is an abbreviation, not a citation."""
    if "..." in tok or "…" in tok:
        head = tok.split("...")[0].split("…")[0]
        return head if head.endswith("/") else ""
    return tok


def extract_candidates(line):
    """Artifact paths mentioned on one markdown line."""
    out = []
    for token in CITE_RE.findall(line):
        raw = strip_ellipsis(token.strip(TOKEN_TRIM))
        if not raw or len(raw) > 200:
            continue                      # abbreviated with '...' — not a path
        if raw.startswith(("http://", "https://", "git@", "file:", "#", "mailto:")):
            continue
        if LANE_PLACEHOLDER_RE.match(raw) or "?" in raw:
            continue                      # template or glob: not a real citation
        # "/logs/game*.log" is a glob the doc means as a family of files; the
        # file part is the checkable citation, the directories are not a glob.
        if "*" in raw:
            raw = GLOB_SUFFIX_RE.sub("", raw)
            if not raw:
                continue
        if raw.startswith("<") or raw.endswith(">"):
            continue                      # placeholder token, e.g. <RUNTIME_DIR>
        if raw in ("E2E_ROOT", "$E2E_ROOT", "${E2E_ROOT}", "E2E_ROOT/logs"):
            continue                      # documented placeholder, resolved below
        raw = raw.lstrip("$")              # $E2E_ROOT/logs/x -> E2E_ROOT/logs/x
        raw = raw.replace("${", "").replace("}", "")
        looks_artifact = (ARTIFACT_EXT_RE.search(raw)
                          or LANE_LOGS_RE.match(raw)
                          or LANE_LOGS_PREFIX_RE.match(raw)
                          or "/logs/" in raw or raw.startswith("Logs/")
                          or raw.startswith(("lane-logs/", "./lane-logs/")))
        if not looks_artifact:
            continue
        if raw.endswith(("/", ".", "..")):
            continue                      # a directory, not an artifact file
        if not path_shape_ok(raw):
            continue                      # "-trace.jsonl", "251.json": a name
                                       # fragment, not a citable file path
        if NOT_EVIDENCE_RE.search(os.path.basename(raw)):
            continue                      # game data / config, not run evidence
        out.append(raw)
    return out


def looks_like_path(token):
    """A bare 'game.log' or 'x-report.json' is a name, not a location."""
    return "/" in token or "~" in token


# Prose routinely cites "E2E_ROOT/logs/<file>" or a bare file name and then
# gives the concrete lane elsewhere; indexing every lane's log tree once makes
# those citations checkable instead of false alarms.
basename_index = set()
for lane_dir in LANE_DIRS:
    for root, _dirs, files in os.walk(lane_dir):
        for name in files:
            basename_index.add(name)

resolve_cache = {}


LANE_BASES = sorted({os.path.dirname(d) for d in LANE_DIRS})
E2E_ROOT_PREFIX_RE = re.compile(r"^\$?\{?E2E_ROOT\}?/([A-Za-z0-9_.\-/]+)$", re.I)


def resolve(base_rel, raw):
    """Resolve a cited path against every documented convention. None = absent.

    Doc conventions that must all resolve, or the finding is a false alarm:
      - absolute lane paths, plus the /root/aaemu-e2e-<lane>/logs/x ->
        /root/aaemu-e2e/logs/x sibling-lane alias the docs use interchangeably
      - lane-root-relative: /logs/x or logs/x -> <lane>/logs/x for every lane
      - $E2E_ROOT/<rest> -> <lane>/<rest> for every lane (not just logs/)
      - a bare file name that exists anywhere under the lane log trees
      - the markdown file's own directory, then the repo root
    """
    if raw in resolve_cache:
        return resolve_cache[raw]
    cands = []
    lane_relative = False
    er = E2E_ROOT_PREFIX_RE.match(raw)
    if er:
        # $E2E_ROOT/<rest>: try the rest under every lane root and logs tree.
        rest = er.group(1)
        for base in LANE_BASES:
            cands.append(os.path.join(base, rest))
        for lane_dir in LANE_DIRS:
            cands.append(os.path.join(lane_dir, rest))
            cands.append(os.path.join(lane_dir, os.path.basename(rest)))
    elif raw.startswith("/"):
        cands.append(raw)
        parts = raw.split("/")
        if len(parts) > 3 and parts[2].startswith("aaemu-e2e"):
            cands.append("/root/aaemu-e2e/" + "/".join(parts[3:]))
        tail = "/".join(parts[1:])
        lane_relative = bool(LANE_LOGS_RE.match(tail)) and raw != "/root/aaemu-e2e" + tail
    else:
        cands.append(os.path.join(REPO_ROOT, base_rel, raw))
        cands.append(os.path.join(REPO_ROOT, raw))
        if LANE_LOGS_RE.match(raw):
            lane_relative = True
    # Lane-root-relative citations: try every lane's logs tree.
    if lane_relative:
        tail_name = raw.split("logs/", 1)[-1]
        for lane_dir in LANE_DIRS:
            cands.append(os.path.join(lane_dir, tail_name))
    hit = None
    for c in cands:
        if os.path.exists(c):
            hit = c
            break
    if hit is None and not looks_like_path(raw) and raw in basename_index:
        hit = raw                      # exists somewhere in the lane log trees
    resolve_cache[raw] = hit
    return hit


def artifact_mtime(target):
    """mtime of a resolved path, or None when only the bare name was matched."""
    if target is None or "/" not in target:
        return None
    try:
        return os.path.getmtime(target)
    except OSError:
        return None

git_sha_cache = {}


def commit_author_ts(sha):
    if sha in git_sha_cache:
        return git_sha_cache[sha]
    out = subprocess.run(["git", "-C", REPO_ROOT, "log", "-1", "--format=%aI", sha],
                         capture_output=True, text=True, check=False).stdout.strip()
    ts = None
    if out:
        try:
            ts = datetime.fromisoformat(out).timestamp()
        except ValueError:
            ts = None
    git_sha_cache[sha] = ts
    return ts


# ---------------------------------------------------------------------------
def utc(ts):
    return datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# CHECK 1 + 3 — cited-artifact existence and staleness
# ---------------------------------------------------------------------------
scanned_md = 0
skipped_lines = 0
cited_lines = 0
missing_seen = set()
predates_seen = set()
contradiction_claim_sites = []

for rel, full in md_files():
    try:
        text = open(full, encoding="utf-8", errors="replace").read()
    except OSError:
        continue
    scanned_md += 1
    lines = text.splitlines()
    for i, line in enumerate(lines, 1):
        # A "none exist" claim is often wrapped mid-sentence across two lines
        # ("I searched for `*.trx` ... **none exist**"), so judge the pair.
        window2 = " ".join(lines[i - 1:i + 1])
        if re.search(r"trx", window2, re.I) and FALSE_NEGATIVE_RE.search(window2):
            site = {"file": rel, "line": i, "text": window2.strip()[:240]}
            if site not in contradiction_claim_sites:
                contradiction_claim_sites.append(site)
        candidates = extract_candidates(line)
        if not candidates:
            continue
        if not is_citation_line(line):
            skipped_lines += 1
            continue                      # mentioned, not cited as evidence
        cited_lines += 1
        for raw in candidates:
            target = resolve(rel, raw)
            if target is None:
                key = (rel, raw)
                if key not in missing_seen:
                    missing_seen.add(key)
                    add("CLAIMED-BUT-MISSING", rel, i,
                        "cited artifact path does not exist: %s" % raw)
                continue
            # CHECK 3: an artifact paired with a SHA it is meant to evidence.
            mtime = artifact_mtime(target)
            if mtime is None:
                continue
            window = "\n".join(lines[max(0, i - 4):i + 3])
            for sha in SHA_RE.findall(window):
                ats = commit_author_ts(sha)
                if ats is None:
                    continue
                if mtime < ats - TOLERANCE:
                    key = (rel, raw, sha)
                    if key not in predates_seen:
                        predates_seen.add(key)
                        add("ARTIFACT-PREDATES-CLAIM", rel, i,
                            "artifact %s (mtime %s) is older than commit %s (author date %s) "
                            "by more than the %sh tolerance — evidence predates the change "
                            "it is cited for"
                            % (raw, utc(mtime), sha, utc(ats), SHA_TOL_HOURS))
                    break

# ---------------------------------------------------------------------------
# CHECK 4 — verdict review list (heuristic, never affects exit status)
# ---------------------------------------------------------------------------
for rel, full in md_files():
    try:
        text = open(full, encoding="utf-8", errors="replace").read()
    except OSError:
        continue
    lines = text.splitlines()
    sec_level = None
    sec_start = 0
    sec_has_artifact = False
    for i, line in enumerate(lines, 1):
        hm = re.match(r"^(#{1,6})\s+", line)
        if hm:
            level = len(hm.group(1))
            if sec_level is None or level <= sec_level:
                sec_level = level
                sec_start = i
                sec_has_artifact = bool(extract_candidates(line)) and is_citation_line(line)
            continue
        if sec_level is None:
            continue
        if extract_candidates(line) and is_citation_line(line):
            sec_has_artifact = True
            continue
        if sec_has_artifact:
            continue
        if not VERDICT_WORD_RE.search(line):
            continue
        if len(line) > 300 or line.lstrip().startswith(("|", ">", "-", "*", "<!--", "```")):
            continue
        if not re.search(r"[.;:)]|\bthat\b|\bwhich\b|\bwith\b", line):
            continue
        add("VERDICT-WITHOUT-ARTIFACT-CITED", rel, i,
            "verdict wording, no artifact path cited in section starting line %d "
            "(heuristic review item — verify by hand)" % sec_start)

# ---------------------------------------------------------------------------
# CHECK 2 — lane JSON report provenance
# ---------------------------------------------------------------------------
def collect_prov(obj, prov_keys, dirty_keys, depth=0):
    if depth > 14:
        return
    if isinstance(obj, dict):
        for k, v in obj.items():
            if SHA_KEY_RE.search(k):
                if isinstance(v, str) and SHA_FULL_RE.match(v):
                    prov_keys.append(k)
                elif isinstance(v, (dict, list)):
                    collect_prov(v, prov_keys, dirty_keys, depth + 1)
            if DIRTY_KEY_RE.search(k):
                dirty_keys.append(k)
            if isinstance(v, (dict, list)):
                collect_prov(v, prov_keys, dirty_keys, depth + 1)
    elif isinstance(obj, list):
        for v in obj[:128]:
            collect_prov(v, prov_keys, dirty_keys, depth + 1)


reports_seen = 0
reports_in_window = 0
reports_unparsable = 0
reports_with_prov = 0
reports_with_dirty = 0
for lane_dir in LANE_DIRS:
    for root, _dirs, files in os.walk(lane_dir):
        for name in sorted(files):
            if not name.endswith(".json"):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, REPO_ROOT)
            try:
                mtime = os.path.getmtime(path)
            except OSError:
                continue
            in_window = WINDOW_CUT is None or mtime >= WINDOW_CUT
            try:
                obj = json.load(open(path, encoding="utf-8", errors="replace"))
            except Exception:
                reports_unparsable += 1
                continue
            if not isinstance(obj, dict):
                continue
            reports_seen += 1
            if in_window:
                reports_in_window += 1
            verdict = None
            for vk in VERDICT_KEYS:
                if isinstance(obj.get(vk), str):
                    verdict = obj[vk]
                    break
            prov_keys, dirty_keys = [], []
            collect_prov(obj, prov_keys, dirty_keys)
            if prov_keys:
                reports_with_prov += 1
            if dirty_keys:
                reports_with_dirty += 1
            if not in_window or verdict is None:
                continue
            if GREEN_RE.match(verdict) and not prov_keys:
                add("GREEN-WITHOUT-SHA", rel, 0,
                    "verdict=%s but no 40-hex provenance field (keys matching "
                    "sha/rev/commit/head); AGENTS.md requires git rev-parse HEAD on every "
                    "Tier 1/2/3 report" % verdict.strip()[:120])
            if not dirty_keys:
                add("MISSING-DIRTY-FLAG", rel, 0,
                    "no dirty flag or dirty-file count recorded; AGENTS.md requires "
                    "SHA + dirty state on every Tier 1/2/3 report")

# ---------------------------------------------------------------------------
# CHECK 5 — known-false-claim probe (*.trx and harvest dataset existence)
# ---------------------------------------------------------------------------
checks = []
if CONTRADICTIONS:
    trx_files = []
    for root, dirs, files in os.walk(REPO_ROOT):
        dirs[:] = [d for d in dirs if d != ".git"]
        for name in files:
            if not name.endswith(".trx"):
                continue
            p = os.path.join(root, name)
            try:
                st = os.stat(p)
            except OSError:
                continue
            rec = {"path": p, "bytes": st.st_size,
                   "mtime_utc": datetime.fromtimestamp(st.st_mtime, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}
            try:
                counters = ET.parse(p).getroot().find(
                    ".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
                if counters is not None:
                    rec["counters"] = {k: v for k, v in counters.attrib.items()
                                       if k in ("total", "executed", "passed", "failed", "notExecuted")}
            except Exception as exc:  # unparsable trx is still a found trx
                rec["parse_error"] = type(exc).__name__
            trx_files.append(rec)
    for site in contradiction_claim_sites:
        if trx_files:
            add("TRX-CLAIM-CONTRADICTED", site["file"], site["line"],
                "document asserts no *.trx exist, but %d found on disk (largest %s, %d bytes)"
                % (len(trx_files), max((t["path"] for t in trx_files),
                                       key=lambda p: os.path.getsize(p)),
                   max(t["bytes"] for t in trx_files)))
    checks.append({
        "kind": "trx-glob",
        "searched_root": REPO_ROOT,
        "claim_sites": contradiction_claim_sites,
        "files_found_count": len(trx_files),
        "files_found": sorted(trx_files, key=lambda t: -t["bytes"]),
    })

    searched = []
    found_datasets = []
    for lane_dir in LANE_DIRS:
        for sub in HARVEST_SEARCH_SUBDIRS:
            d = os.path.join(lane_dir, sub) if sub else lane_dir
            if not os.path.isdir(d):
                continue
            searched.append(d)
            for pattern in HARVEST_DATASET_GLOBS:
                found_datasets.extend(globmod.glob(os.path.join(d, pattern)))
    dataset_sites = []
    for rel, full in md_files():
        try:
            text = open(full, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r"(jev-shadow|harvest).*jsonl", line, re.I) and \
                    re.search(r"Logs/|default", line, re.I):
                dataset_sites.append({"file": rel, "line": i, "text": line.strip()[:240]})
    checks.append({
        "kind": "harvest-dataset-glob",
        "searched_dirs": sorted(set(searched)),
        "dataset_sites": dataset_sites,
        "files_found": sorted(set(found_datasets)),
        "note": "A documented harvest workflow with no dataset file anywhere is unrun "
                "tooling, not evidence. Recorded, never scored as a failure.",
    })

# ---------------------------------------------------------------------------
# emit
# ---------------------------------------------------------------------------
elapsed = round(time.time() - START_TS, 3)
fatal = {k: counts.get(k, 0) for k in FATAL_KINDS if counts.get(k, 0)}
review = {k: counts.get(k, 0) for k in REVIEW_KINDS if counts.get(k, 0)}
verdict = "FAIL" if fatal else ("REVIEW" if review else "PASS")

report = {
    "tool": TOOL,
    "tool_version": TOOL_VERSION,
    "schema": "evidence-claims/v1",
    "verdict": verdict,
    "generated_utc": args["start_iso"],
    "command_line": args["command_line"],
    "git_head": args["git_head"],
    "git_branch": args["git_branch"],
    "dirty_file_count": args["dirty_count"],
    "dirty_state": args["dirty_label"],
    "runtime_seconds": elapsed,
    "inputs": {
        "repo_root": REPO_ROOT,
        "lane_log_dirs": LANE_DIRS,
        "markdown_scopes": MD_PATHS,
        "markdown_globs": MD_GLOBS,
        "window_days": WINDOW_DAYS,
        "sha_tolerance_hours": SHA_TOL_HOURS,
        "contradiction_checks": CONTRADICTIONS,
    },
    "counts": {k: counts.get(k, 0) for k in ALL_KINDS},
    "fatal_counts": fatal,
    "review_counts": review,
    "stats": {
        "markdown_files_scanned": scanned_md,
        "cited_artifact_lines": cited_lines,
        "lines_with_paths_but_no_citation_intent": skipped_lines,
        "lane_report_files_parsed": reports_seen,
        "lane_report_files_in_window": reports_in_window,
        "lane_report_files_unparsable": reports_unparsable,
        "lane_report_files_with_provenance_field": reports_with_prov,
        "lane_report_files_with_dirty_flag": reports_with_dirty,
    },
    "findings": sorted(findings, key=lambda f: (f["kind"], f["file"], f["line"])),
    "contradiction_checks": checks,
    "limits": [
        "Proves cited artifacts EXIST and RECORD provenance (40-hex SHA, dirty flag). "
        "It does NOT validate that an artifact's internal verdict is truthful.",
        "Cannot prove a test actually exercised what its claim says. A report with a "
        "SHA is not thereby correct; a PASS this script does not challenge may still be wrong.",
        "CLAIMED-BUT-MISSING resolution is heuristic: a lane-relative citation "
        "(/root/aaemu-e2e-<lane>/logs/x) also resolves under /root/aaemu-e2e/logs/x, so some "
        "genuinely missing artifacts resolve to a sibling lane and are not reported.",
        "ARTIFACT-PREDATES-CLAIM compares filesystem mtimes to commit author dates; "
        "file copies and clock skew produce false hits, hence the tolerance window.",
        "VERDICT-WITHOUT-ARTIFACT-CITED is a noisy review heuristic, not proof of a "
        "defect; it never affects exit status.",
    ],
}

if OUT_PATH and OUT_PATH != "-":
    report["report_path"] = OUT_PATH
    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    with open(OUT_PATH, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)
        fh.write("\n")

print(json.dumps(report, indent=2))
sys.exit(1 if fatal else 0)
PYEOF

RC=$?
set -e
if [[ "$RC" -ne 0 && "$RC" -ne 1 ]]; then
  die "analysis failed (python exit $RC)"
fi
exit "$RC"