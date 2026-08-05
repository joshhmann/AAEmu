-- Author: Tai - 2026/08/05
-- Prune 5 false unit_reqs gates on quest context 6586.
--
-- Audit: scorecard-explorations/unit-reqs-layer.md (t_c87c5deb) — 6586 is an
-- id-space collision (0 quest_components, no quest_contexts row; id reused by
-- npc 6586 토벌대장 캐치미 / doodad 6586 핏물먹이 주술사의 격류방출), NOT a quest
-- context. These kind-31 (CompleteQuestContext) rows on the Start components of
-- the 5 lvl-51 God's Shield stage-1 quests (6587/6589/6592/6594/6597) demand
-- completion of a quest that cannot exist, permanently blocking the components.
--
-- Pruning the 5 rows unblocks the quests (accept-NPC wiring intact:
-- 사우락/자일/그렌델). The gate context itself is unrecoverable from data —
-- there is no quest body to restore.
--
-- Drift: exactly -5 rows (unit_reqs 13354 -> 13349) on compact.sqlite3
-- (md5 78b3bdbf038db3b927056106efdf91af).

DELETE FROM "unit_reqs"
WHERE "id" IN (46598, 46603, 46609, 46613, 46619)
  AND "owner_type" = 'QuestComponent'
  AND "kind_id" = 31
  AND "value1" = 6586;
