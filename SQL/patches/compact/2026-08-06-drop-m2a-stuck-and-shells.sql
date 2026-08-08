-- Author: Hyraxknot Division - 2026/08/06 (executed 2026/08/08)
-- M2a data drop: 117 quest_contexts (26 engine-stuck templates + 91 zero-component
-- shells) — old Sunny Wilderness / reserve / Hadir-farm clusters.
--
-- Decision: Josh, 2026-08-08 chat ("Option A is nice") — DROP BOTH clusters per
-- scorecard-explorations/dropped-content-register.md authority (2026-08-05: dead
-- content = drop, not code). Full evidence: kanban t_f105ee3b comment 2848 +
-- scorecard-explorations/runnability.md SKIP rollup (136 SKIP, band census
-- 2026-08-06, t_3c6b60d7) + register sections §6/§7 (this drop).
--
-- Cluster A (26 engine-stuck templates, zone 22 old_e_sunny_wilderness):
--   A1 (5, carry REAL NPC dialogue — keep as restore pointer in register §6):
--     1867, 1898, 1904, 1908, 2054 — ltd='t', no report act; engine can never
--     leave Progress (QuestStep.cs:127-129 ltd force + NewQuestCode.cs:69-78
--     HackFix needs Score>0 AND no Ready step — none qualify).
--   A2 (20 act-less, cat 32): 5575, 5578, 5579, 5584, 5589, 5596, 5597, 5601,
--     5603, 5604, 5608, 5619, 5630, 5632, 5636, 5637, 5640, 5643, 5644, 5645
--     (5 also score=100: 5584/5603/5632/5640/5645).
--   A3 (1): 5641 (ltd='f', score=100, zero acts — score never met).
--   Zero accept surfaces on all 26 (0 item_accept_quests / doodad_func_quests /
--   accept_quest_effects / quest_act_con_accept_components) -> unreachable in
--   live play. 0 external next_component refs into the cluster.
--
-- Cluster B (91 zero-component shells):
--   B1 (82): 2148–2229 "하다보니(reserve)" — cat 34, zone 1, lvl 1, ms 5,
--     ltd='f' — deliberate reserve shells.
--   B2 (9): 3748, 3750–3757 Hadir's Farm instance-cutscene shells — zone 169,
--     cat 63, lvl 1, ms 5. Their 9 sphere_quests rows (725→3748, 727–734→
--     3750–3757) MUST be pruned with the drop (data-defects.md said "zero refs"
--     — wrong; verified 2026-08-06 + 2026-08-08).
--
-- Dependency surface (handled below):
--   unit_reqs 18563 (QuestComponent 8808, kind 31 CompleteQuestContext,
--     value1 1898) — live quest 1899's Start gate on dropped 1898 -> PRUNE.
--   unit_reqs 17467/18460/18462 (Skill kind 37 PreCompleteQuestContext gates
--     -> 2054/1867) -> PRUNE (orphan-drop precedent t_0ac25620).
--   Kind-35 Skill unit_reqs (38519/38559/38561/38563/38565/38955/38959/38968/
--   38969/38973/38976/40175/40335/40383) are AreaSphere refs (real spheres
--   2148–2189) — id collisions, NOT quest deps — UNTOUCHED.
--   Kinds 15/16/20/27 Skill unit_reqs (24582/36413/15246/22407/22908/23878/
--   25172) are buff/sphere refs — id collisions — UNTOUCHED.
--   Quest 1899 itself: census-PASS (vacuous), out of scope — flagged in
--   register §6 as future-look, NOT dropped.
--
-- Guards: every DELETE is pinned to the full verified row shape (id + composite
-- key ANDs) — verified 2026-08-08 on compact.sqlite3 (md5
-- 78b3bdbf038db3b927056106efdf91af). Act-detail rows 848/878/885/532/533 are
-- referenced ONLY by the 5 cluster quest_acts (verified — no sharing).
-- quest_act_obj_aliases 148/211/1381/1837 are reference name rows, NOT deleted.
--
-- Drift: on compact.sqlite3 (md5 78b3bdbf...):
--   quest_contexts               4876 -> 4759   (-117)
--   quest_components            17851 -> 17768  ( -83)
--   quest_acts                  26886 -> 26881  (  -5)
--   quest_act_obj_item_gathers   2484 ->  2481  (  -3)
--   quest_act_obj_monster_hunts   896 ->   894  (  -2)
--   quest_component_texts        6738 ->  6728  ( -10)
--   quest_chat_bubbles          13268 -> 13241  ( -27)
--   sphere_quests                1079 ->  1070  (  -9)
--   unit_reqs                   13354 -> 13350  (  -4)

-- ============================================================================
-- 1. quest_chat_bubbles (27 — all unshared, owned by the 15 A-cluster comps)
-- ============================================================================
DELETE FROM "quest_chat_bubbles"
WHERE "id" IN (13756,13757,13758,13759,13760,13761,13762,13763,13833,13834,
               13835,13836,13837,13838,13958,13959,13960,13961,13978,13979,
               13980,13981,13982,14002,14003,14004,14005)
  AND "quest_component_id" IN (8593,8594,8595,8804,8805,8806,8835,8836,8837,
                               8851,8852,8853,9447,9448,9449);

-- ============================================================================
-- 2. quest_component_texts (10 — kind 4 objective texts on 10 A-cluster comps)
-- ============================================================================
DELETE FROM "quest_component_texts"
WHERE "id" IN (6725,6726,6851,6852,6893,6894,6900,6901,6908,6909)
  AND "quest_component_text_kind_id" = 4
  AND "quest_component_id" IN (9449,9448,8594,8595,8836,8837,8805,8806,8852,8853);

-- ============================================================================
-- 3. act-detail rows (5 — referenced only by the 5 cluster quest_acts below)
-- ============================================================================
DELETE FROM "quest_act_obj_item_gathers"
WHERE "id" IN (848,878,885)
  AND "item_id" IN (15887,15985,16018)
  AND "count" IN (12,3,5);

DELETE FROM "quest_act_obj_monster_hunts"
WHERE "id" IN (532,533)
  AND "npc_id" IN (4926,4855)
  AND "count" IN (3,8);

-- ============================================================================
-- 4. quest_acts (5 — the only acts in the whole drop set)
-- ============================================================================
DELETE FROM "quest_acts"
WHERE "id" IN (13105,13355,13419,13431,13439)
  AND "quest_component_id" IN (9448,8594,8836,8805,8852)
  AND "act_detail_type" IN ('QuestActObjItemGather','QuestActObjMonsterHunt')
  AND "act_detail_id" IN (848,878,885,532,533);

-- ============================================================================
-- 5. quest_components (83 — all owned by the 117 dropped contexts)
-- ============================================================================
DELETE FROM "quest_components"
WHERE "id" IN (8593,8594,8595,8596,8804,8805,8806,8807,8835,8836,8837,8838,
               8851,8852,8853,8854,9447,9448,9449,9450,24054,24055,24056,24063,
               24064,24065,24066,24067,24068,24081,24082,24083,24096,24097,24098,
               24117,24118,24119,24120,24121,24122,24132,24133,24134,24138,24139,
               24140,24141,24142,24143,24153,24154,24155,24186,24187,24188,24219,
               24220,24221,24225,24226,24227,24237,24238,24239,24240,24241,24242,
               24249,24250,24251,24252,24253,24254,24258,24259,24260,24261,24262,
               24263,24264,24265,24266)
  AND "quest_context_id" IN (1867,1898,1904,1908,2054,5575,5578,5579,5584,5589,
                             5596,5597,5601,5603,5604,5608,5619,5630,5632,5636,
                             5637,5640,5641,5643,5644,5645)
  AND "component_kind_id" IN (2,4,6,8);

-- ============================================================================
-- 6. sphere_quests (9 — Hadir farm accept triggers; quest_trigger 3=start, 4=?)
-- ============================================================================
DELETE FROM "sphere_quests"
WHERE "id" IN (725,727,728,729,730,731,732,733,734)
  AND "quest_id" IN (3748,3750,3751,3752,3753,3754,3755,3756,3757)
  AND "quest_trigger_id" IN (3,4);

-- ============================================================================
-- 7. unit_reqs (4 — dangling quest gates; full shape pinned)
-- ============================================================================
DELETE FROM "unit_reqs"
WHERE "id" = 18563
  AND "owner_id" = 8808
  AND "owner_type" = 'QuestComponent'
  AND "kind_id" = 31
  AND "value1" = 1898
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 17467
  AND "owner_id" = 13444
  AND "owner_type" = 'Skill'
  AND "kind_id" = 37
  AND "value1" = 2054
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 18460
  AND "owner_id" = 13589
  AND "owner_type" = 'Skill'
  AND "kind_id" = 37
  AND "value1" = 1867
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 18462
  AND "owner_id" = 13593
  AND "owner_type" = 'Skill'
  AND "kind_id" = 37
  AND "value1" = 1867
  AND "value2" = 0;

-- ============================================================================
-- 8. quest_contexts (117)
-- ============================================================================
-- A1: 5 old-zone quests with real NPC dialogue (restore pointer — register §6).
DELETE FROM "quest_contexts"
WHERE "id" IN (1867,1898,1904,1908,2054)
  AND "zone_id" = 22
  AND "milestone_id" = 5
  AND "let_it_done" = 't'
  AND "score" = 0
  AND "category_id" IN (18,1);

-- A2: 20 act-less cat-32 quests (5 also score=100).
DELETE FROM "quest_contexts"
WHERE "id" IN (5575,5578,5579,5584,5589,5596,5597,5601,5603,5604,5608,5619,
               5630,5632,5636,5637,5640,5643,5644,5645)
  AND "zone_id" = 22
  AND "milestone_id" = 5
  AND "let_it_done" = 't'
  AND "category_id" = 32
  AND "score" IN (0,100);

-- A3: 5641 (ltd='f', score=100 — score can never be met).
DELETE FROM "quest_contexts"
WHERE "id" = 5641
  AND "zone_id" = 22
  AND "milestone_id" = 5
  AND "let_it_done" = 'f'
  AND "score" = 100
  AND "category_id" = 32;

-- B1: 82 reserve shells.
DELETE FROM "quest_contexts"
WHERE "id" BETWEEN 2148 AND 2229
  AND "name" = '하다보니(reserve)'
  AND "category_id" = 34
  AND "zone_id" = 1
  AND "LEVEL" = 1
  AND "milestone_id" = 5
  AND "let_it_done" = 'f';

-- B2: 9 Hadir farm cutscene shells (zone 169 instance_hadir_farm).
DELETE FROM "quest_contexts"
WHERE "id" IN (3748,3750,3751,3752,3753,3754,3755,3756,3757)
  AND "zone_id" = 169
  AND "category_id" = 63
  AND "LEVEL" = 1
  AND "milestone_id" = 5
  AND "let_it_done" = 'f';

-- ============================================================================
-- Verification
-- ============================================================================
-- BEFORE (run against the pre-fix copy):
--   SELECT COUNT(*) FROM quest_contexts;                 -> 4876
--   SELECT COUNT(*) FROM quest_components;               -> 17851
--   SELECT COUNT(*) FROM quest_acts;                     -> 26886
--   SELECT COUNT(*) FROM quest_act_obj_item_gathers;     -> 2484
--   SELECT COUNT(*) FROM quest_act_obj_monster_hunts;    -> 896
--   SELECT COUNT(*) FROM quest_component_texts;          -> 6738
--   SELECT COUNT(*) FROM quest_chat_bubbles;             -> 13268
--   SELECT COUNT(*) FROM sphere_quests;                  -> 1079
--   SELECT COUNT(*) FROM unit_reqs;                      -> 13354
-- AFTER (expected):
--   SELECT COUNT(*) FROM quest_contexts;                 -> 4759
--   SELECT COUNT(*) FROM quest_components;               -> 17768
--   SELECT COUNT(*) FROM quest_acts;                     -> 26881
--   SELECT COUNT(*) FROM quest_act_obj_item_gathers;     -> 2481
--   SELECT COUNT(*) FROM quest_act_obj_monster_hunts;    -> 894
--   SELECT COUNT(*) FROM quest_component_texts;          -> 6728
--   SELECT COUNT(*) FROM quest_chat_bubbles;             -> 13241
--   SELECT COUNT(*) FROM sphere_quests;                  -> 1070
--   SELECT COUNT(*) FROM unit_reqs;                      -> 13350
--   SELECT COUNT(*) FROM quest_contexts WHERE id IN (1867,1898,1904,1908,2054,
--     5575,5578,5579,5584,5589,5596,5597,5601,5603,5604,5608,5619,5630,5632,
--     5636,5637,5640,5641,5643,5644,5645)               -> 0
--   SELECT COUNT(*) FROM quest_contexts WHERE id BETWEEN 2148 AND 2229; -> 0
--   SELECT COUNT(*) FROM quest_contexts WHERE id IN (3748,3750,3751,3752,3753,
--     3754,3755,3756,3757);                             -> 0
--   SELECT COUNT(*) FROM unit_reqs WHERE id IN (18563,17467,18460,18462); -> 0
--   SELECT COUNT(*) FROM unit_reqs WHERE id IN (38519,38559,38561,38563,38565,
--     38955,38959,38968,38969,38973,38976,40175,40335,40383);  -> 14 (UNCHANGED
--     — kind-35 AreaSphere collisions, intentionally not pruned)
--   SELECT COUNT(*) FROM sphere_quests WHERE id IN (725,727,728,729,730,731,
--     732,733,734);                                     -> 0
--   SELECT COUNT(*) FROM quest_chat_bubbles WHERE quest_component_id IN
--     (8593,8594,8595,8804,8805,8806,8835,8836,8837,8851,8852,8853,9447,9448,
--     9449);                                            -> 0
