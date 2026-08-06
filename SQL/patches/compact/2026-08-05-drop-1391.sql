-- Author: Kai - 2026/08/05
-- Drop quest context 1391 (마을을 지켜라, "Protect the Village") — QUEST_NO_COMPONENTS.
--
-- Decision: data-defects.md §6 verdict (c) drop, confirmed by Josh (2026-08-05 chat:
-- "Unblock granted, if they're orphans we prob don't need to code em in."); registered
-- in scorecard-explorations/dropped-content-register.md §1 (t_5a61cee3 impl).
--
-- Shape (verified on compact.sqlite3, md5 78b3bdbf038db3b927056106efdf91af):
--   quest_contexts id=1391: name='마을을 지켜라'  category_id=27  zone_id=0  LEVEL=0
--     milestone_id=5  let_it_done='t'  score=0  grade_id=1  (deliberate dummy shell)
--   quest_components WHERE quest_context_id=1391 -> 0 rows
--   quest_acts      (via those components)        -> 0 rows
--   quest_act_con_accept_components → 1391        -> 0 rows
--   item_accept_quests / doodad_func_* / game_schedule_quests /
--   quest_act_obj_complete_quests / quest_act_obj_conditions /
--   accept_quest_effects → 1391                   -> 0 rows each
--   unit_reqs value1=1391: only row 33609 (owner Skill 17113, kind 35) — a sphere
--     reference, NOT a quest gate (engine keys by owner_type/owner_id); left in place.
--
-- The template has no components at all: no accept path exists, the engine can never
-- start it (Quest.StartQuest() false, NewQuestCode.cs:44-48), and it is dead content
-- reachable only by DB query. Nothing references it as a dependency.
--
-- Guards: the DELETE is pinned to the full verified shape (id + every identifying
-- column) so it can never take out a different row. Drift: exactly -1 row on
-- compact.sqlite3 (md5 78b3bdbf...): quest_contexts 4876 -> 4875.
--
-- Companion change (this branch, fix/no-components-1391): 1391 removed from the
-- verifier allowlist (QuestSanityVerifier.cs:93, "dummy shells" group) so a
-- regression — an empty template re-added for 1391 — re-reports at WARN instead of
-- being masked to INFO.

DELETE FROM "quest_contexts"
WHERE "id" = 1391
  AND "name" = '마을을 지켜라'
  AND "category_id" = 27
  AND "zone_id" = 0
  AND "LEVEL" = 0
  AND "milestone_id" = 5
  AND "let_it_done" = 't'
  AND "score" = 0;
