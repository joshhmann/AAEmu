#!/usr/bin/env bash
# ============================================================================
# UNIT_REQS_MISSING_CONTEXT census rig — fail-before/pass-after evidence for the
# drop-8-orphaned-contexts prune (quest_contexts 745, 1421, 1954-1958, 2140)
#
# Mirrors the QuestSanityVerifier UNIT_REQS predicate
# (AAEmu.Game/Core/Managers/QuestSanityVerifier.cs, VerifyUnitReqs):
#   QuestComponent-owned unit_reqs rows with a POSITIVE quest-context kind
#   (31 CompleteQuestContext / 32 ProgressQuestContext / 33 ReadyQuestContext /
#   37 PreCompleteQuestContext) whose value1 has NO quest_contexts row.
# Missing contexts are classified: surviving quest body => WARN (orphan),
# no body but id owned by another entity table => INFO (collision).
#
# On the prod reference (compact.sqlite3 md5 78b3bdbf038db3b927056106efdf91af)
# the predicate finds 21 rows / 17 distinct missing contexts. THIS card's scope
# is the 5 orphan WARN rows on dropped contexts 1955/1956/1957/1958/2140
# (gated quests 1956/1957/1958/1959-live/2141):
#   19197 comp 9780 -> 1955   (gates quest 1956)
#   19198 comp 9783 -> 1956   (gates quest 1957)
#   19205 comp 9786 -> 1957   (gates quest 1958)
#   19201 comp 9789 -> 1958   (gates quest 1959 — LIVE, unreachable without the
#                              dropped chain; the only non-allowlisted gate)
#   19207 comp 9913 -> 2140   (gates quest 2141)
# The patch also prunes unit_reqs 16064 (Skill-owned kind-32 gate on 745 — NOT
# in the verifier predicate, owner_type Skill) and the 2 dangling sphere accept
# rows (sphere_quests 418 -> 1421, sphere_accept_quest_quests 3 -> 1956).
#
# Usage:
#   unit_reqs_missing_context_census.sh [path-to-compact.sqlite3] [--apply-fix]
#                                       [--scope=N]
#     default DB: ./AAEmu.Game/Data/compact.sqlite3 (repo layout)
#     default scope: the card's 5 rows above (19197,19198,19201,19205,19207)
#     --scope=0: verdict the FULL predicate (still fails pass-after — the
#                remaining rows are the sibling drop inventory: 1961/2141-2143
#                chain links, 3233/3235 orphans, 1832/1848/1882/1921/2053
#                collisions, 6586×5 already covered by the 2026-08-04 overlay)
#     --apply-fix: copy the DB, apply SQL/patches/compact/
#                  2026-08-05-drop-8-orphaned-contexts.sql in the copy,
#                  re-run the census (pass-after), then clean up. The copy is
#                  validated against the patch's pinned drift (6 unit_reqs +
#                  2 sphere rows gone; 16000 untouched).
#
# Exit code: 1 when the scoped census finds card rows (fail), 0 when clean
# (pass). Read-only against the source DB.
# ============================================================================
set -u

DB="${1:-./AAEmu.Game/Data/compact.sqlite3}"
PATCH="${PATCH:-./SQL/patches/compact/2026-08-05-drop-8-orphaned-contexts.sql}"
APPLY_FIX=0
SCOPE="19197,19198,19201,19205,19207"
for arg in "${@:2}"; do
    case "$arg" in
        --apply-fix) APPLY_FIX=1 ;;
        --scope) echo "error: --scope needs 0 (full predicate) — card scope is the default" >&2; exit 2 ;;
        --scope=*) SCOPE="${arg#--scope=}" ;;
    esac
done

CARD_ROWS="19197,19198,19201,19205,19207"

CENSUS_SQL='SELECT r.id, r.owner_id, r.kind_id, r.value1,
                   qc.quest_context_id AS gated_quest,
                   EXISTS(SELECT 1 FROM quest_components b
                          WHERE b.quest_context_id = r.value1) AS has_body
            FROM unit_reqs r
            LEFT JOIN quest_components qc ON qc.id = r.owner_id
            WHERE r.owner_type = '"'"'QuestComponent'"'"'
              AND r.kind_id IN (31, 32, 33, 37)
              AND r.value1 NOT IN (SELECT id FROM quest_contexts)
            ORDER BY r.value1, r.id;'

run_census() {
    local db="$1"
    echo "== census on: $db  (md5 $(md5sum "$db" | cut -d' ' -f1))"
    local rows
    rows=$(sqlite3 -header -column "$db" "$CENSUS_SQL")
    if [ -z "$rows" ]; then
        echo "RESULT: PASS — 0 UNIT_REQS_MISSING_CONTEXT rows (full predicate clean)"
        return 0
    fi
    echo "FULL PREDICATE ($(echo "$rows" | tail -n +3 | wc -l | tr -d ' ') rows):"
    echo "$rows"
    echo
    if [ "$SCOPE" = "0" ]; then
        echo "RESULT: FAIL — UNIT_REQS_MISSING_CONTEXT rows above (out-of-scope inventory remains; see header)"
        return 1
    fi
    # Card scope: rows whose unit_reqs id is one of the card's 5 (comma list).
    local scoped scoped_count
    scoped=$(echo "$rows" | awk -v ids=",$SCOPE," 'NR<=2 {next} index(ids, ","$1",") {print}')
    scoped_count=$(echo "$scoped" | sed '/^$/d' | wc -l | tr -d ' ')
    if [ -n "$scoped" ]; then
        echo "CARD SCOPE rows ($CARD_ROWS):"
        echo "$scoped"
        echo
        echo "RESULT: FAIL — $scoped_count card rows still reference dropped quest contexts (gate can never pass)"
        return 1
    fi
    echo "RESULT: PASS — 0 card-scope rows (19197/19198/19201/19205/19207 all pruned)"
    return 0
}

if [ "$APPLY_FIX" = "1" ]; then
    if [ ! -f "$PATCH" ]; then
        echo "error: patch not found: $PATCH" >&2
        exit 2
    fi
    TMP=$(mktemp -d)
    trap 'rm -rf "$TMP"' EXIT
    cp "$DB" "$TMP/fixed.sqlite3"
    echo "== applying $PATCH to copy =="
    sqlite3 "$TMP/fixed.sqlite3" < "$PATCH"
    echo "== drift check on copy =="
    sqlite3 -header -column "$TMP/fixed.sqlite3" \
        "SELECT 'unit_reqs pruned', COUNT(*) FROM unit_reqs WHERE id IN (16064,19197,19198,19201,19205,19207);
         SELECT 'sphere_quests 418', COUNT(*) FROM sphere_quests WHERE id = 418;
         SELECT 'sphere_accept 3', COUNT(*) FROM sphere_accept_quest_quests WHERE id = 3;
         SELECT 'unit_reqs 16000 (must stay)', COUNT(*) FROM unit_reqs WHERE id = 16000;"
    echo
    run_census "$TMP/fixed.sqlite3"
    exit $?
fi

run_census "$DB"
