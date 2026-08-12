-- Author: Tai - 2026/08/11
-- Prune 6 remaining orphan unit_reqs gate rows (kind 31 CompleteQuestContext)
-- pointing at missing quest contexts — verifier UNIT_REQS 0-WARN acceptance.
--
-- Audit: scorecard-explorations/unit-reqs-layer.md (t_c87c5deb) §7.3 rows
-- #12/#14/#15/#16/#17/#18 — verdicts: PRUNE row 19206 / 19208 / 19209 /
-- 19210 / 27692 / 27695. "Dead-to-dead or dead-chain-to-dead-shell edges;
-- optional-but-clean; zero player impact either way."
--
-- All 6 value1 ids have NO quest_contexts row, have surviving component
-- bodies (quest_components.quest_context_id = value1), and belong to chains
-- already ruled drop in data-defects.md §4/§7:
--   19206 -> 1961 (gates quest 2140 — cat-34 chain link, dropped chain)
--   19208 -> 2141 (gates quest 2142)
--   19209 -> 2142 (gates quest 2143)
--   19210 -> 2143 (gates quest 2144 — live-but-unreachable chain link)
--   27692 -> 3233 (gates quest 3234 — isolated 2-quest chain, both sides dead)
--   27695 -> 3235 (gates quest 3236 — same)
--
-- The verifier UNIT_REQS layer (QuestSanityVerifier.VerifyUnitReqs,
-- fix/verifier-unit-reqs @ 1143fc238, merged to develop) classifies these as
-- WARN (orphan: quest body survives, context row gone). Pruning them reaches
-- the 0-WARN post-prune state; the 4 INFO collision rows (16832/16853/
-- 18576/18578) remain by design (id owned by other entity tables, not a
-- quest dep).
--
-- Drift: exactly -6 rows (unit_reqs 13354 -> 13348) on compact.sqlite3
-- (md5 78b3bdbf038db3b927056106efdf91af).
--
-- Do NOT touch: kind-1 level rows (value1=14), row 22921 (doodad ref),
-- row 45718 (item ref), ExceptComplete rows 20455/44930/44931.

DELETE FROM "unit_reqs"
WHERE "id" = 19206
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9910
  AND "kind_id" = 31
  AND "value1" = 1961;

DELETE FROM "unit_reqs"
WHERE "id" = 19208
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9916
  AND "kind_id" = 31
  AND "value1" = 2141;

DELETE FROM "unit_reqs"
WHERE "id" = 19209
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9919
  AND "kind_id" = 31
  AND "value1" = 2142;

DELETE FROM "unit_reqs"
WHERE "id" = 19210
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 9922
  AND "kind_id" = 31
  AND "value1" = 2143;

DELETE FROM "unit_reqs"
WHERE "id" = 27692
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 13788
  AND "kind_id" = 31
  AND "value1" = 3233;

DELETE FROM "unit_reqs"
WHERE "id" = 27695
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 13796
  AND "kind_id" = 31
  AND "value1" = 3235;
