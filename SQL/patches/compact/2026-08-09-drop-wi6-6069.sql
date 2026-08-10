-- Author: Hyraxknot Division - 2026/08/09 (card t_6810ebd4, execution)
-- WI-6 data drop: quest_contexts 6069 (거침없이 춤추는 격투의 칼날 —
-- "The Uninhibited Dancing Blade of Combat"), an unreachable ltd quest.
--
-- Decision: Josh, 2026-08-09 ~21:15 PDT (t_6f950108 comment, delegated Kimi
-- nightwatch): "GO drop — zero accept surfaces, objective can never credit —
-- dead data. Execute per M2a playbook: guarded SQL patch, quest-scoped rows
-- only, shared act-detail rows untouched, register §8 entry with provenance."
-- Authority: scorecard-explorations/dropped-content-register.md §8 (finalized
-- on t_6f950108, commit 7bc6a1f65). Full evidence:
-- scorecard-explorations/band-41-50-ltd-triage-wi6.md §4 + attached packet on
-- t_6f950108.
--
-- Quest shape: ltd='t', score 0, zone 1, ms 14, cat 55, lvl 50;
--   Start comp 26119 (NO acts) -> Progress 26120 (QuestActObjAbilityLevel 7,
--   ability 1 @ lvl 50 — no event hookup, objective never credits) ->
--   Ready comp 26121 (no acts) -> Reward comp 26122 (QuestActSupplyItem 4002
--   -> item 30757). Zero accept surfaces across all 5 tables (0
--   item_accept_quests / doodad_func_quests / accept_quest_effects /
--   sphere_quests / quest_act_con_accept_components) -> unreachable in live
--   play; no completion path can ever be entered.
--
-- Dependency surface (handled below): 0 external refs point into the drop
--   set (verified 2026-08-09): 0 unit_reqs kind-31 gates -> 6069, 0
--   items.loot_quest_id -> 6069, 0 sphere_quests -> 6069, 0
--   next_component refs into comps 26119-26122, 0 quest_chat_bubbles with
--   next_bubble pointing into the deleted bubble set from outside it.
--
-- ⚠ ACT-DETAIL ROWS 7 + 4002 KEPT (register §8 instruction; M2a collision
--   pitfall — register §6/§7): quest_act_obj_ability_levels id 7 and
--   quest_act_supply_items id 4002 are NOT deleted. Audit note for the gate:
--   on this DB snapshot each is referenced by exactly ONE quest_act (35730 /
--   35732, both in the drop set) — the "ability 7 -> 15 quests / supply 4002
--   -> 5106" sharing figures in the evidence packet were id-space collisions
--   across act-detail tables, not shared detail rows. Retained anyway per the
--   register's explicit instruction (restore pointer + zero risk: unreferenced
--   detail rows are inert).
--
-- Guards: every DELETE is pinned to the full verified row shape (id +
-- composite-key ANDs) — verified 2026-08-09 on compact.sqlite3 (md5
-- 78b3bdbf038db3b927056106efdf91af). Drop surface: quest_contexts −1 (6069)
-- / quest_components −4 (26119/26120/26121/26122) / quest_acts −2
-- (35730/35732) / unit_reqs −1 (45196) / quest_component_texts −3
-- (13884/13885/13891) / quest_chat_bubbles −4 (27584/27585/27586/27593).
--
-- Drift: on compact.sqlite3 (md5 78b3bdbf...):
--   quest_contexts         4876 -> 4875   (-1)
--   quest_components      17851 -> 17847  (-4)
--   quest_acts            26886 -> 26884  (-2)
--   unit_reqs             13354 -> 13353  (-1)
--   quest_component_texts  6738 ->  6735  (-3)
--   quest_chat_bubbles    13268 -> 13264  (-4)

-- ============================================================================
-- 1. quest_chat_bubbles (4 — owned by Start comp 26119 + Ready comp 26121)
-- ============================================================================
DELETE FROM "quest_chat_bubbles"
WHERE "id" IN (27584, 27585, 27586, 27593)
  AND "quest_component_id" IN (26119, 26121)
  AND "npc_id" = 502
  AND "chat_bubble_kind_id" = 1;

-- ============================================================================
-- 2. quest_component_texts (3 — kind 4 objective texts on 26120/26121)
-- ============================================================================
DELETE FROM "quest_component_texts"
WHERE "id" IN (13884, 13885, 13891)
  AND "quest_component_text_kind_id" = 4
  AND "quest_component_id" IN (26120, 26121);

-- ============================================================================
-- 3. quest_acts (2 — the only acts in the whole drop set; detail rows 7/4002
--    are SHARED-STYLE and retained per register §8 — do NOT touch)
-- ============================================================================
DELETE FROM "quest_acts"
WHERE "id" IN (35730, 35732)
  AND "quest_component_id" IN (26120, 26122)
  AND "act_detail_type" IN ('QuestActObjAbilityLevel', 'QuestActSupplyItem')
  AND "act_detail_id" IN (7, 4002);

-- ============================================================================
-- 4. unit_reqs (1 — level gate owned by Start comp 26119; full shape pinned)
-- ============================================================================
DELETE FROM "unit_reqs"
WHERE "id" = 45196
  AND "owner_id" = 26119
  AND "owner_type" = 'QuestComponent'
  AND "kind_id" = 1
  AND "value1" = 50
  AND "value2" = 0;

-- ============================================================================
-- 5. quest_components (4 — all owned by quest 6069)
-- ============================================================================
DELETE FROM "quest_components"
WHERE "id" IN (26119, 26120, 26121, 26122)
  AND "quest_context_id" = 6069
  AND "component_kind_id" IN (2, 4, 6, 8)
  AND "next_component" = 0;

-- ============================================================================
-- 6. quest_contexts (1)
-- ============================================================================
DELETE FROM "quest_contexts"
WHERE "id" = 6069
  AND "name" = '거침없이 춤추는 격투의 칼날'
  AND "zone_id" = 1
  AND "milestone_id" = 14
  AND "let_it_done" = 't'
  AND "score" = 0
  AND "category_id" = 55
  AND "LEVEL" = 50;

-- ============================================================================
-- Verification
-- ============================================================================
-- BEFORE (run against the pre-fix copy):
--   SELECT COUNT(*) FROM quest_contexts;          -> 4876
--   SELECT COUNT(*) FROM quest_components;        -> 17851
--   SELECT COUNT(*) FROM quest_acts;              -> 26886
--   SELECT COUNT(*) FROM unit_reqs;               -> 13354
--   SELECT COUNT(*) FROM quest_component_texts;   -> 6738
--   SELECT COUNT(*) FROM quest_chat_bubbles;      -> 13268
-- AFTER (expected):
--   SELECT COUNT(*) FROM quest_contexts;          -> 4875
--   SELECT COUNT(*) FROM quest_components;        -> 17847
--   SELECT COUNT(*) FROM quest_acts;              -> 26884
--   SELECT COUNT(*) FROM unit_reqs;               -> 13353
--   SELECT COUNT(*) FROM quest_component_texts;   -> 6735
--   SELECT COUNT(*) FROM quest_chat_bubbles;      -> 13264
--   SELECT COUNT(*) FROM quest_contexts WHERE id = 6069;                    -> 0
--   SELECT COUNT(*) FROM quest_components WHERE quest_context_id = 6069;    -> 0
--   SELECT COUNT(*) FROM quest_acts WHERE id IN (35730, 35732);             -> 0
--   SELECT COUNT(*) FROM unit_reqs WHERE id = 45196;                        -> 0
--   SELECT COUNT(*) FROM quest_component_texts
--     WHERE quest_component_id IN (26119,26120,26121,26122);                -> 0
--   SELECT COUNT(*) FROM quest_chat_bubbles
--     WHERE quest_component_id IN (26119,26120,26121,26122);                -> 0
--   SELECT COUNT(*) FROM quest_act_obj_ability_levels WHERE id = 7;         -> 1 (UNCHANGED)
--   SELECT COUNT(*) FROM quest_act_supply_items WHERE id = 4002;            -> 1 (UNCHANGED)
