-- Author: Tai - 2026/08/05
-- Drop 8 orphaned quest_contexts (745, 1421, 1954-1958, 2140) — prune the 6 dangling
-- unit_reqs gate rows + 2 dangling sphere accept rows.
--
-- Audit: scorecard-explorations/data-defects.md §4/§6/§7 (t_7416ea48) +
-- scorecard-explorations/unit-reqs-layer.md (t_c87c5deb, f5d95c29) +
-- decide-8-orphaned-contexts.md (t_850791ad). Decision: DROP per §6 (operator,
-- 2026-08-05). All 8 contexts have NO quest_contexts / quest_context_texts rows
-- (0 rows — verified) — they are true orphans whose quest bodies survive under
-- the missing context ids. There is nothing to restore: "fixing" would fabricate
-- context rows for the abandoned cat-34 crafting chain
-- (1954->1955->1956->1957->1958->(1959 live)->...->2140->2141->...->(2144 live)),
-- already ruled dead. The bodies never load; the gate rows below can never pass.
--
-- This is a pure prune of the dangling reference rows:
--   unit_reqs (6 rows — the gate rows referencing the dropped contexts):
--     16064  Skill          12912  kind 32  value1 745   (745 in-progress gate on
--                                                         skill 가방 줍기's sibling;
--                                                         16000 same-shape already
--                                                         pruned by the 2026-08-04
--                                                         overlay — NOT re-pruned here)
--     19197  QuestComponent 9780   kind 31  value1 1955  (gates quest 1956)
--     19198  QuestComponent 9783   kind 31  value1 1956  (gates quest 1957)
--     19205  QuestComponent 9786   kind 31  value1 1957  (gates quest 1958)
--     19201  QuestComponent 9789   kind 31  value1 1958  (gates quest 1959 — LIVE,
--                                                         but unreachable without
--                                                         the dropped chain)
--     19207  QuestComponent 9913   kind 31  value1 2140  (gates quest 2141)
--   sphere_quests (1 row):           418  quest 1421 accept (dangling — context gone)
--   sphere_accept_quest_quests (1):  3    quest 1956 accept (dangling — context gone)
--
-- Guards: every row is pinned to its full verified shape (id + every relevant
-- column) — verified 2026-08-05 on compact.sqlite3 (md5
-- 78b3bdbf038db3b927056106efdf91af). 16000 (Skill 12913 kind 32 value1 745) is
-- intentionally NOT in the prune list — already covered by
-- 2026-08-04-fix-quest-data-defects.sql. 34172 (Skill 17308 kind 35 value1 1421)
-- is an AreaSphere ref (sphere 1421 exists — '펫 등짐 테스트' TEST sphere), not a
-- quest-context dep — NOT pruned.
--
-- Drift: on compact.sqlite3 (md5 78b3bdbf...):
--   unit_reqs                  13354 -> 13348   (-6)
--   sphere_quests               1079 ->  1078   (-1)
--   sphere_accept_quest_quests     3 ->     2   (-1)

DELETE FROM "unit_reqs"
WHERE "id" = 16064
  AND "owner_type" = 'Skill'
  AND "owner_id" = 12912
  AND "kind_id" = 32
  AND "value1" = 745;

DELETE FROM "unit_reqs"
WHERE "id" = 19197
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9780
  AND "kind_id" = 31
  AND "value1" = 1955;

DELETE FROM "unit_reqs"
WHERE "id" = 19198
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9783
  AND "kind_id" = 31
  AND "value1" = 1956;

DELETE FROM "unit_reqs"
WHERE "id" = 19205
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9786
  AND "kind_id" = 31
  AND "value1" = 1957;

DELETE FROM "unit_reqs"
WHERE "id" = 19201
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9789
  AND "kind_id" = 31
  AND "value1" = 1958;

DELETE FROM "unit_reqs"
WHERE "id" = 19207
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9913
  AND "kind_id" = 31
  AND "value1" = 2140;

DELETE FROM "sphere_quests"
WHERE "id" = 418
  AND "quest_id" = 1421
  AND "quest_trigger_id" = 1;

DELETE FROM "sphere_accept_quest_quests"
WHERE "id" = 3
  AND "sphere_accept_quest_id" = 2
  AND "quest_id" = 1956;

-- ============================================================================
-- Verification
-- ============================================================================
-- BEFORE (run against the pre-fix copy):
--   SELECT id, owner_type, owner_id, kind_id, value1 FROM unit_reqs WHERE id IN (16064,19197,19198,19201,19205,19207);
--     -> 16064|Skill|12912|32|745   19197|QuestComponent|9780|31|1955
--        19198|QuestComponent|9783|31|1956   19201|QuestComponent|9789|31|1958
--        19205|QuestComponent|9786|31|1957   19207|QuestComponent|9913|31|2140
--   SELECT * FROM sphere_quests WHERE id = 418;                    -> 418|1421|1
--   SELECT * FROM sphere_accept_quest_quests WHERE id = 3;         -> 3|2|1956
-- AFTER (run against the post-fix copy — expected rows below):
--   SELECT COUNT(*) FROM unit_reqs WHERE id IN (16064,19197,19198,19201,19205,19207);  -> 0
--   SELECT COUNT(*) FROM sphere_quests WHERE id = 418;                                 -> 0
--   SELECT COUNT(*) FROM sphere_accept_quest_quests WHERE id = 3;                      -> 0
--   SELECT COUNT(*) FROM unit_reqs WHERE id = 16000;  (unchanged — covered by the 2026-08-04 overlay) -> 1
