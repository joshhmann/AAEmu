#!/usr/bin/env bash
# ============================================================================
# Quest COMPONENT_NEXT_MISSING census rig — fail-before evidence for 776/777 (330)
#
# Mirrors the QuestSanityVerifier COMPONENT_NEXT_MISSING predicate
# (AAEmu.Game/Core/Managers/QuestSanityVerifier.cs, VerifyLoadedState):
#   component.NextComponent != 0 && !quest.Components.ContainsKey(component.NextComponent)
# i.e. a quest_components row whose next_component is not a component of the
# SAME quest. On the prod reference (compact.sqlite3 md5
# 78b3bdbf038db3b927056106efdf91af) exactly 3 rows fail:
#   330  comp 1520 -> 3543   (target exists in no quest)
#   776  comp 3480 -> 4370   (target exists in no quest)
#   777  comp 3488 -> 3487   (target exists in no quest)
#
# Usage:
#   quest_next_missing_census.sh [path-to-compact.sqlite3] [--apply-fix]
#     default DB: ./AAEmu.Game/Data/compact.sqlite3 (repo layout)
#     --apply-fix: copy the DB, apply the 3 UPDATEs from
#                  SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql
#                  to the copy, re-run the census (pass-after), then clean up.
#
# Exit codes:
#   plain mode:        1 = raw source has COMPONENT_NEXT_MISSING rows (fail-before),
#                      0 = clean. Use this for fail-before evidence.
#   --apply-fix mode:  the pass-after phase is authoritative — 0 = the fixed
#                      copy is clean (fix works), 1 = the fixed copy STILL has
#                      rows (fix incomplete). The raw-source phase still prints
#                      its fail-before result on stdout but does not set the
#                      final exit code. Read-only against the source DB.
# ============================================================================
set -u

DB="${1:-./AAEmu.Game/Data/compact.sqlite3}"
APPLY_FIX=0
[ "${2:-}" = "--apply-fix" ] && APPLY_FIX=1

CENSUS_SQL='SELECT qc.quest_context_id AS quest, qc.id AS component,
                   qc.next_component AS next_target, q.name AS quest_name
            FROM quest_components qc
            LEFT JOIN quest_contexts q ON q.id = qc.quest_context_id
            WHERE qc.next_component != 0
              AND NOT EXISTS (SELECT 1 FROM quest_components s
                              WHERE s.id = qc.next_component
                                AND s.quest_context_id = qc.quest_context_id)
            ORDER BY qc.quest_context_id, qc.id;'

run_census() {
    local db="$1"
    echo "== census on: $db  (md5 $(md5sum "$db" | cut -d' ' -f1))"
    local rows
    rows=$(sqlite3 -header -column "$db" "$CENSUS_SQL")
    if [ -z "$rows" ]; then
        echo "RESULT: PASS — 0 COMPONENT_NEXT_MISSING rows"
        return 0
    fi
    echo "RESULT: FAIL — COMPONENT_NEXT_MISSING rows:"
    echo "$rows"
    return 1
}

rc=0
run_census "$DB" || rc=1

if [ "$APPLY_FIX" = "1" ]; then
    TMP="$(mktemp /tmp/next-missing-census.XXXXXX.sqlite3)"
    trap 'rm -f "$TMP"' EXIT
    cp "$DB" "$TMP"
    sqlite3 "$TMP" <<'SQL'
-- the 3-row data fix, verbatim from SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql
UPDATE quest_components SET next_component = 1521  WHERE id = 1520;  -- quest 330: 3543 -> Ready comp 1521
UPDATE quest_components SET next_component = 3482  WHERE id = 3480;  -- quest 776: 4370 -> Progress comp 3482
UPDATE quest_components SET next_component = 11591 WHERE id = 3488;  -- quest 777: 3487 -> Ready comp 11591
SQL
    echo
    echo ">> fix applied to copy (3 UPDATEs from SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql)"
    # --apply-fix contract: pass-after phase decides the exit code (raw phase
    # above already reported fail-before on stdout; its rc must not stick).
    rc=0
    run_census "$TMP" || rc=1
fi

exit "$rc"
