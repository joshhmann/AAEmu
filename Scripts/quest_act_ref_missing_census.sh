#!/usr/bin/env bash
# ============================================================================
# Quest ACT_REF_MISSING_QUEST census rig — fail-before evidence for 2145 → 2146
#
# Mirrors the QuestSanityVerifier ACT_REF_MISSING_QUEST predicate
# (AAEmu.Game/Core/Managers/QuestSanityVerifier.cs, VerifyLoadedState):
#   case QuestActConAcceptComponent acceptComponent
#       when !questTemplates.ContainsKey(acceptComponent.QuestContextId):
# i.e. a QuestActConAcceptComponent act (on a LOADED quest — one with a
# quest_contexts row) whose quest_context_id has no quest_contexts row, so the
# target quest template is never created and the self-start target can never
# be found. On the prod reference (compact.sqlite3 md5
# 78b3bdbf0383db3b927056106efdf91af) exactly 2 rows fail:
#   1960  comp 9794 accept-act 75 -> 1961   (sibling — same dead cat-34 chain)
#   2145  comp 9927 accept-act 89 -> 2146   (THIS card)
#
# Usage:
#   quest_act_ref_missing_census.sh [path-to-compact.sqlite3] [--apply-fix] [--scope N]
#     default DB: ./AAEmu.Game/Data/compact.sqlite3 (repo layout)
#     --scope N: verdict only on quest N (default 2145 — the card's quest;
#                pass 0 to verdict the full predicate, both rows)
#     --apply-fix: copy the DB, delete the dangling act 89 + its quest_acts
#                  row 14121 (data-defects.md §4 minimal action) in the copy,
#                  re-run the census (pass-after), then clean up.
#
# Exit code: 1 when the scoped census finds ACT_REF_MISSING_QUEST rows (fail),
#            0 when clean (pass). Read-only against the source DB.
# ============================================================================
set -u

DB="${1:-./AAEmu.Game/Data/compact.sqlite3}"
APPLY_FIX=0
SCOPE=2145
for arg in "${@:2}"; do
    case "$arg" in
        --apply-fix) APPLY_FIX=1 ;;
        --scope) echo "error: --scope needs a quest id (0 = all)" >&2; exit 2 ;;
        --scope=*) SCOPE="${arg#--scope=}" ;;
    esac
done

CENSUS_SQL='SELECT q.id AS quest, q.name AS quest_name, c.id AS component,
                   a.id AS act_row, a.act_detail_id AS accept_act_id,
                   cac.quest_context_id AS missing_target
            FROM quest_acts a
            JOIN quest_components c ON c.id = a.quest_component_id
            JOIN quest_contexts q ON q.id = c.quest_context_id
            JOIN quest_act_con_accept_components cac ON cac.id = a.act_detail_id
            WHERE a.act_detail_type = '"'"'QuestActConAcceptComponent'"'"'
              AND NOT EXISTS (SELECT 1 FROM quest_contexts t
                              WHERE t.id = cac.quest_context_id)
            ORDER BY q.id, a.id;'

run_census() {
    local db="$1"
    echo "== census on: $db  (md5 $(md5sum "$db" | cut -d' ' -f1))"
    local rows scope_sql
    rows=$(sqlite3 -header -column "$db" "$CENSUS_SQL")
    if [ -z "$rows" ]; then
        echo "RESULT: PASS — 0 ACT_REF_MISSING_QUEST rows"
        return 0
    fi
    echo "FULL PREDICATE (all loaded quests):"
    echo "$rows"
    if [ "$SCOPE" != "0" ]; then
        scope_sql="$CENSUS_SQL"  # reuse full output; filter below
        if echo "$rows" | awk -v q="$SCOPE" 'NR>2 && $1==q {found=1} END{exit !found}'; then
            echo
            echo "RESULT: FAIL — quest $SCOPE has ACT_REF_MISSING_QUEST rows (self-start target can never be found)"
            return 1
        fi
        echo
        echo "RESULT: PASS — quest $SCOPE has NO ACT_REF_MISSING_QUEST rows"
        return 0
    fi
    echo
    echo "RESULT: FAIL — ACT_REF_MISSING_QUEST rows above"
    return 1
}

rc=0
run_census "$DB" || rc=1

if [ "$APPLY_FIX" = "1" ]; then
    TMP="$(mktemp /tmp/act-ref-missing-census.XXXXXX.sqlite3)"
    trap 'rm -f "$TMP"' EXIT
    cp "$DB" "$TMP"
    sqlite3 "$TMP" <<'SQL'
-- data-defects.md §4 minimal action for quest 2145: delete the dangling
-- ConAcceptComponent act (accept-act 89 -> 2146) + its quest_acts row.
DELETE FROM quest_acts WHERE id = 14121;                    -- quest 2145 comp 9927 act 89
DELETE FROM quest_act_con_accept_components WHERE id = 89; -- dangling accept act -> 2146
SQL
    echo
    echo ">> fix applied to copy (data-defects.md §4: delete dangling act 89 + quest_acts 14121)"
    run_census "$TMP" || rc=1
fi

exit "$rc"
