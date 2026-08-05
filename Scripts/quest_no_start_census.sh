#!/usr/bin/env bash
# ============================================================================
# Quest QUEST_NO_START census rig — fail-before/pass-after evidence for the
# 1533–1548 cluster drop (t_5140fb35)
#
# Mirrors the QuestSanityVerifier QUEST_NO_START predicate
# (AAEmu.Game/Core/Managers/QuestSanityVerifier.cs, VerifyLoadedState):
#   quest has a quest_contexts row (is loaded), has >= 1 quest_components row,
#   and ZERO Start-kind (component_kind_id = 2) components.
# On the prod reference (compact.sqlite3 md5 78b3bdbf038db3b927056106efdf91af)
# exactly 23 quests fail: 1533, 1535–1549, 1551–1554, 1640, 1830, 1831 —
# legacy 1.0-era tutorial shells, never acceptable (no Start step, no accept
# surface). 1534/1550 are pure id gaps (no context row — not quests, not
# counted by this predicate).
#
# Usage:
#   quest_no_start_census.sh [path-to-compact.sqlite3] [--apply-fix]
#     default DB: ./AAEmu.Game/Data/compact.sqlite3 (repo layout)
#     --apply-fix: copy the DB, apply SQL/patches/compact/
#                  2026-08-05-drop-no-start-cluster.sql (the 23-context drop)
#                  in the copy, re-run the census (pass-after), then clean up.
#
# Exit code: 1 when the census finds QUEST_NO_START rows (fail), 0 when clean
#            (pass). Read-only against the source DB.
# ============================================================================
set -u

DB="${1:-./AAEmu.Game/Data/compact.sqlite3}"
APPLY_FIX=0
for arg in "${@:2}"; do
    case "$arg" in
        --apply-fix) APPLY_FIX=1 ;;
        *) echo "error: unknown arg '$arg'" >&2; exit 2 ;;
    esac
done

PATCH="$(cd "$(dirname "$0")/.." && pwd)/SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql"

CENSUS_SQL='SELECT q.id AS quest, q.category_id, q.zone_id, COUNT(c.id) AS components,
                   SUM(CASE WHEN c.component_kind_id = 2 THEN 1 ELSE 0 END) AS start_comps
            FROM quest_contexts q
            JOIN quest_components c ON c.quest_context_id = q.id
            GROUP BY q.id
            HAVING SUM(CASE WHEN c.component_kind_id = 2 THEN 1 ELSE 0 END) = 0
            ORDER BY q.id;'

run_census() {
    local db="$1"
    echo "== census on: $db  (md5 $(md5sum "$db" | cut -d' ' -f1))"
    local rows
    rows=$(sqlite3 -header -column "$db" "$CENSUS_SQL")
    if [ -z "$rows" ]; then
        echo "RESULT: PASS — 0 QUEST_NO_START quests"
        return 0
    fi
    echo "QUEST_NO_START quests (loaded, >=1 component, zero Start comps):"
    echo "$rows"
    echo
    echo "RESULT: FAIL — QUEST_NO_START quests above (can never be accepted)"
    return 1
}

rc=0
run_census "$DB" || rc=1

if [ "$APPLY_FIX" = "1" ]; then
    if [ ! -f "$PATCH" ]; then
        echo "error: drop patch not found at $PATCH" >&2
        exit 2
    fi
    TMP="$(mktemp /tmp/no-start-census.XXXXXX.sqlite3)"
    trap 'rm -f "$TMP"' EXIT
    cp "$DB" "$TMP"
    sqlite3 "$TMP" < "$PATCH"
    echo
    echo ">> fix applied to copy: $PATCH (23 contexts / 25 components / 42 acts)"
    run_census "$TMP" || rc=1
fi

exit "$rc"
