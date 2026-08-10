-- Author: Hyraxknot Division - 2026/08/09 (execution card t_267a3279)
-- WI-11a data drop: 155 quest_contexts (88 tutorial stubs + 60 Dwarf main-story
-- skeleton + 3 title quests + 3 cat-1 test/unused + 1 Cradle act-less).
--
-- Decision: Josh, 2026-08-09 ~21:15 PDT (delegated Kimi nightwatch, comment on
-- t_724ccab2): Q1 GO / Q2 NO-GO (keep 315/1576/1728/2046) / Q3 GO (B1+D1 one block)
-- / Q4 GO / Q5 GO / Q6 GO / Q7-Q10 DEFER to WI-11b (t_8ec705f0). Full authority:
-- scorecard-explorations/dropped-content-register.md §9 +
-- scorecard-explorations/wi-11a-band0-null-triage.md §3/§4/§6.
--
-- Per-set drops:
--   A1 (88 tutorial stubs, cat 45, zone 1, LEVEL 0, ms 5): 2584, 2586, 2589-2606,
--     2609, 2612, 2614, 2616, 2620-2683 — 0 comps / 0 acts (no downstream rows).
--   B1 (33 Dwarf main-story ltd, cat 93): 5040, 5773, 5781-5811.
--   D1 (27 Dwarf main-story placeholders, cat 93): 3484-3490, 3492-3502, 3562-3563,
--     3565-3568, 3992, 4408, 5980.
--   B1+D1 = 60 quests / 242 comps / 18 acts — one self-contained kind-31 chain
--     (5980→3484→…→5811); root 5980 has no prerequisites.
--   B2 (3 title quests, cat 82, ms 8000001): 8000001-8000003 — 6 comps / 15 acts.
--   B3 (3 cat-1 test/unused): 1835, 1836, 1895 — 8 comps / 0 acts; 1836 gates on
--     1832 (EXTERNAL predecessor, untouched — dependent, not depended-on).
--   B4 (1 Cradle act-less, cat 27, zone 16): 5678 — 3 comps / 0 acts.
--
-- Dependency surface (drift-checked 2026-08-09 on canonical compact.sqlite3,
-- md5 78b3bdbf038db3b927056106efdf91af):
--   unit_reqs: 123 rows owned by the 259 dropped comps (60 kind-31 chain gates,
--     61 kind-1, 2 kind-3) — all pruned; incl. 18500 (1836's comp → 1832 external
--     predecessor gate, pruned with its owner).
--   Skill kind-27 gate rows → dropped quests (PRUNED, rows only — skills stay):
--     11099 (skill 12050 → 5806, per register §9c), 18167 (skill 11686 → 3495),
--     21977 (skill 14353 → 3487) — 18167/21977 are drift-found same-class rows
--     (triage named only 11099; M2a precedent pruned Skill gates to dropped quests).
--     24869 (skill 12586 → 2640) LEFT per triage §3 — inert residue class, A1
--     explicitly has no downstream rows to prune.
--   Verified zero: external QuestComponent gates into the set (59 refs, all owned
--     in-set), sphere_quests, items.loot_quest_id, doodad_func_quests,
--     accept_quest_effects, sphere_accept_quest_quests, successive refs.
--   item_accept_quests: 3 rows owned by B2 (items 8000007-8000009 → quests
--     8000001-8000003) — die with the quests; items themselves untouched.
--   All 33 act-detail rows (Con/Supply family) exclusively referenced by the 33
--   drop quest_acts — no sharing (verified).
--
-- Guards: every DELETE is pinned to the full verified row shape (id + composite
-- key ANDs) — follow 2026-08-06-drop-m2a-stuck-and-shells.sql.
--
-- Drift (BEFORE -> AFTER on canonical sqlite):
--   quest_contexts                       4,876 ->   4,721  (-155)
--   quest_components                    17,851 ->  17,592  (-259)
--   quest_acts                          26,886 ->  26,853  (-33)
--   quest_act_con_accept_npcs            3,519 ->   3,510  (-9)
--   quest_act_con_report_npcs            4,342 ->   4,333  (-9)
--   quest_act_con_accept_items             486 ->     483  (-3)
--   quest_act_supply_exps                1,210 ->   1,207  (-3)
--   quest_act_supply_coppers             1,715 ->   1,712  (-3)
--   quest_act_con_auto_completes         1,386 ->   1,383  (-3)
--   quest_act_supply_appellations          288 ->     285  (-3)
--   item_accept_quests                     367 ->     364  (-3)
--   unit_reqs                           13,354 ->  13,228  (-126)

-- ============================================================================
-- 1. act-detail rows (33 — exclusively referenced by the 33 drop quest_acts)
-- ============================================================================
DELETE FROM "quest_act_con_accept_npcs"
WHERE "id" IN (4492,4493,4508,4509,4510,4511,4512,4513,4528)
  AND "npc_id" IN (13873,13874,13875,13876,13877,14101,14147)
;

DELETE FROM "quest_act_con_report_npcs"
WHERE "id" IN (4746,4747,4767,4768,4769,4770,4771,4772,4775)
  AND "npc_id" IN (13873,13874,13875,13876,13877,13915,14101)
  AND "use_alias" IN ('f','t')
;

DELETE FROM "quest_act_con_accept_items"
WHERE "id" IN (8000001,8000003,8000004)
  AND "item_id" IN (8000007,8000008,8000009)
  AND "cleanup" = 't'
;

DELETE FROM "quest_act_supply_exps"
WHERE "id" IN (8000001,8000002,8000003)
  AND "exp" = 0
;

DELETE FROM "quest_act_supply_coppers"
WHERE "id" IN (8000001,8000002,8000003)
  AND "amount" = 0
;

DELETE FROM "quest_act_con_auto_completes"
WHERE "id" IN (8000001,8000002,8000003)
;

DELETE FROM "quest_act_supply_appellations"
WHERE "id" IN (8000001,8000002,8000003)
  AND "appellation_id" IN (8000001,8000002,8000003)
;
-- ============================================================================
-- 2. item_accept_quests (3 — B2 title quests' own accept rows)
-- ============================================================================
DELETE FROM "item_accept_quests"
WHERE "id" IN (8000001,8000002,8000003)
  AND "item_id" IN (8000007,8000008,8000009)
  AND "quest_id" IN (8000001,8000002,8000003);

-- ============================================================================
-- 3. quest_acts (33 — D1 18 + B2 15; all act-details exclusively owned)
-- ============================================================================
DELETE FROM "quest_acts"
WHERE "id" IN (35033, 35034, 35035, 35036, 35170, 35171, 35172, 35173, 35174, 35175, 35176, 35177,
    35178, 35179, 35180, 35181, 35204, 35205, 8000001, 8000002, 8000003, 8000004, 8000005, 8000007,
    8000008, 8000009, 8000010, 8000011, 8000012, 8000013, 8000014, 8000015, 8000016)
  AND "quest_component_id" IN (24847, 24849, 24851, 24853, 24855, 24857, 24859, 24861, 24863, 24865,
    24867, 24869, 24871, 24873, 24875, 24877, 25761, 25763, 8000001, 8000002,
    8000004, 8000005, 8000007, 8000008)
  AND "act_detail_type" IN ('QuestActConAcceptItem','QuestActConAcceptNpc','QuestActConAutoComplete','QuestActConReportNpc','QuestActSupplyAppellation','QuestActSupplyCopper','QuestActSupplyExp')
  AND "act_detail_id" IN (4492, 4493, 4508, 4509, 4510, 4511, 4512, 4513, 4528, 4746, 4747, 4767,
    4768, 4769, 4770, 4771, 4772, 4775, 8000001, 8000002, 8000003, 8000004);

-- ============================================================================
-- 4. quest_components (259 — all owned by the 155 dropped contexts)
-- ============================================================================
DELETE FROM "quest_components"
WHERE "id" IN (8512, 8513, 8514, 8515, 8535, 8536, 8537, 8785,
    17205, 17206, 17207, 19160, 19161, 19162, 19436, 21822,
    21823, 21824, 24382, 24383, 24384, 24847, 24848, 24849,
    24850, 24851, 24852, 24853, 24854, 24855, 24856, 24857,
    24858, 24859, 24860, 24861, 24862, 24863, 24864, 24865,
    24866, 24867, 24868, 24869, 24870, 24871, 24872, 24873,
    24874, 24875, 24876, 24877, 24878, 24879, 24880, 24881,
    24882, 24883, 24884, 24885, 24886, 24887, 24888, 24889,
    24890, 24891, 24892, 24893, 24894, 24895, 24896, 24897,
    24898, 24899, 24900, 24901, 24902, 24903, 24904, 24905,
    24906, 24907, 24908, 24909, 24910, 24911, 24912, 24913,
    24914, 24915, 24916, 24917, 24918, 24919, 24920, 24921,
    24922, 24923, 24924, 24925, 24926, 24927, 24928, 24929,
    24930, 24931, 24932, 24933, 24934, 24935, 24936, 24937,
    24938, 24939, 24940, 24941, 24942, 24943, 24944, 24945,
    24946, 24947, 24948, 24993, 24994, 24995, 24996, 24997,
    24998, 24999, 25000, 25001, 25002, 25003, 25004, 25005,
    25006, 25007, 25008, 25009, 25010, 25011, 25012, 25013,
    25014, 25015, 25016, 25017, 25018, 25019, 25020, 25021,
    25022, 25023, 25024, 25025, 25026, 25027, 25028, 25029,
    25030, 25031, 25032, 25033, 25034, 25035, 25036, 25037,
    25038, 25039, 25040, 25041, 25042, 25043, 25044, 25045,
    25046, 25047, 25048, 25049, 25050, 25051, 25052, 25053,
    25054, 25055, 25056, 25057, 25058, 25059, 25060, 25061,
    25062, 25063, 25064, 25065, 25066, 25067, 25068, 25069,
    25070, 25071, 25072, 25073, 25074, 25075, 25076, 25077,
    25078, 25079, 25080, 25081, 25082, 25083, 25084, 25085,
    25086, 25087, 25088, 25089, 25090, 25091, 25092, 25093,
    25094, 25095, 25096, 25097, 25098, 25099, 25100, 25101,
    25102, 25103, 25104, 25105, 25106, 25107, 25108, 25109,
    25110, 25111, 25112, 25113, 25114, 25115, 25116, 25117,
    25118, 25761, 25762, 25763, 25764, 8000001, 8000002, 8000004,
    8000005, 8000007, 8000008)
  AND "quest_context_id" IN (1835, 1836, 1895, 2584, 2586, 2589, 2590, 2591, 2592, 2593, 2594, 2595,
    2596, 2597, 2598, 2599, 2600, 2601, 2602, 2603, 2604, 2605, 2606, 2609,
    2612, 2614, 2616, 2620, 2621, 2622, 2623, 2624, 2625, 2626, 2627, 2628,
    2629, 2630, 2631, 2632, 2633, 2634, 2635, 2636, 2637, 2638, 2639, 2640,
    2641, 2642, 2643, 2644, 2645, 2646, 2647, 2648, 2649, 2650, 2651, 2652,
    2653, 2654, 2655, 2656, 2657, 2658, 2659, 2660, 2661, 2662, 2663, 2664,
    2665, 2666, 2667, 2668, 2669, 2670, 2671, 2672, 2673, 2674, 2675, 2676,
    2677, 2678, 2679, 2680, 2681, 2682, 2683, 3484, 3485, 3486, 3487, 3488,
    3489, 3490, 3492, 3493, 3494, 3495, 3496, 3497, 3498, 3499, 3500, 3501,
    3502, 3562, 3563, 3565, 3566, 3567, 3568, 3992, 4408, 5040, 5678, 5773,
    5781, 5782, 5783, 5784, 5785, 5786, 5787, 5788, 5789, 5790, 5791, 5792,
    5793, 5794, 5795, 5796, 5797, 5798, 5799, 5800, 5801, 5802, 5803, 5804,
    5805, 5806, 5807, 5808, 5809, 5810, 5811, 5980, 8000001, 8000002, 8000003)
  AND "component_kind_id" IN (2,4,6,8);

-- ============================================================================
-- 5. unit_reqs (123 owned by drop comps + 3 Skill kind-27 gates to dropped quests)
-- ============================================================================
DELETE FROM "unit_reqs"
WHERE "id" = 11099
  AND "owner_type" = 'Skill'
  AND "owner_id" = 12050
  AND "kind_id" = 27
  AND "value1" = 5806
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 18167
  AND "owner_type" = 'Skill'
  AND "owner_id" = 11686
  AND "kind_id" = 27
  AND "value1" = 3495
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 18500
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 8512
  AND "kind_id" = 31
  AND "value1" = 1832
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 18558
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 8785
  AND "kind_id" = 1
  AND "value1" = 8
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 21977
  AND "owner_type" = 'Skill'
  AND "owner_id" = 14353
  AND "kind_id" = 27
  AND "value1" = 3487
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44290
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24847
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44291
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24847
  AND "kind_id" = 3
  AND "value1" = 3
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44292
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24851
  AND "kind_id" = 31
  AND "value1" = 3484
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44293
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24851
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44294
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24855
  AND "kind_id" = 31
  AND "value1" = 3485
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44295
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24855
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44296
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24859
  AND "kind_id" = 31
  AND "value1" = 3486
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44297
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24859
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44298
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24863
  AND "kind_id" = 31
  AND "value1" = 3487
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44299
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24863
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44300
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24867
  AND "kind_id" = 31
  AND "value1" = 3488
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44301
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24867
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44302
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24871
  AND "kind_id" = 31
  AND "value1" = 3489
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44303
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24871
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44304
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24875
  AND "kind_id" = 31
  AND "value1" = 3490
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44305
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24875
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44306
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24879
  AND "kind_id" = 31
  AND "value1" = 3492
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44307
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24879
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44308
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24883
  AND "kind_id" = 31
  AND "value1" = 3493
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44309
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24883
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44310
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24887
  AND "kind_id" = 31
  AND "value1" = 3494
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44311
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24887
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44312
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24891
  AND "kind_id" = 31
  AND "value1" = 3495
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44313
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24891
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44314
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24895
  AND "kind_id" = 31
  AND "value1" = 3496
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44315
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24895
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44316
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24899
  AND "kind_id" = 31
  AND "value1" = 3497
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44317
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24899
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44318
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24903
  AND "kind_id" = 31
  AND "value1" = 3498
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44319
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24903
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44320
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24907
  AND "kind_id" = 31
  AND "value1" = 3499
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44321
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24907
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44322
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24911
  AND "kind_id" = 31
  AND "value1" = 3500
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44323
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24911
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44324
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24915
  AND "kind_id" = 31
  AND "value1" = 3501
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44325
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24915
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44326
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24919
  AND "kind_id" = 31
  AND "value1" = 3502
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44327
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24919
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44328
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24923
  AND "kind_id" = 31
  AND "value1" = 3562
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44329
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24923
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44330
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24927
  AND "kind_id" = 31
  AND "value1" = 3563
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44331
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24927
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44332
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24931
  AND "kind_id" = 31
  AND "value1" = 3565
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44333
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24931
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44334
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24935
  AND "kind_id" = 31
  AND "value1" = 3566
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44335
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24935
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44336
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24939
  AND "kind_id" = 31
  AND "value1" = 3567
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44337
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24939
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44338
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 17205
  AND "kind_id" = 31
  AND "value1" = 3568
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44339
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 17205
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44340
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 19160
  AND "kind_id" = 31
  AND "value1" = 3992
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44341
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 19160
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44342
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 21822
  AND "kind_id" = 31
  AND "value1" = 4408
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44343
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 21822
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44344
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24945
  AND "kind_id" = 31
  AND "value1" = 5040
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44345
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24945
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44408
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24993
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44409
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24993
  AND "kind_id" = 31
  AND "value1" = 5773
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44410
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24997
  AND "kind_id" = 31
  AND "value1" = 5781
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44411
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24997
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44412
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25001
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44413
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25001
  AND "kind_id" = 31
  AND "value1" = 5782
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44414
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25005
  AND "kind_id" = 31
  AND "value1" = 5783
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44415
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25005
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44416
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25009
  AND "kind_id" = 31
  AND "value1" = 5784
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44417
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25009
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44418
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25013
  AND "kind_id" = 31
  AND "value1" = 5785
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44419
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25013
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44420
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25017
  AND "kind_id" = 31
  AND "value1" = 5786
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44421
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25017
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44422
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25021
  AND "kind_id" = 31
  AND "value1" = 5787
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44423
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25021
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44424
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25026
  AND "kind_id" = 31
  AND "value1" = 5788
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44425
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25026
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44426
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25030
  AND "kind_id" = 31
  AND "value1" = 5789
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44427
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25030
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44428
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25034
  AND "kind_id" = 31
  AND "value1" = 5790
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44429
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25034
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44430
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25038
  AND "kind_id" = 31
  AND "value1" = 5791
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44431
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25038
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44432
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25042
  AND "kind_id" = 31
  AND "value1" = 5792
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44433
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25042
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44434
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25046
  AND "kind_id" = 31
  AND "value1" = 5793
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44435
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25046
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44436
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25050
  AND "kind_id" = 31
  AND "value1" = 5794
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44437
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25050
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44438
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25054
  AND "kind_id" = 31
  AND "value1" = 5795
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44439
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25054
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44440
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25058
  AND "kind_id" = 31
  AND "value1" = 5796
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44441
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25058
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44442
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25062
  AND "kind_id" = 31
  AND "value1" = 5797
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44443
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25062
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44444
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25066
  AND "kind_id" = 31
  AND "value1" = 5798
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44445
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25066
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44446
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25070
  AND "kind_id" = 31
  AND "value1" = 5799
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44447
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25070
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44448
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25074
  AND "kind_id" = 31
  AND "value1" = 5800
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44449
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25074
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44450
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25078
  AND "kind_id" = 31
  AND "value1" = 5801
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44451
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25078
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44452
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25082
  AND "kind_id" = 31
  AND "value1" = 5802
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44453
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25082
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44454
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25086
  AND "kind_id" = 31
  AND "value1" = 5803
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44455
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25086
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44456
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25090
  AND "kind_id" = 31
  AND "value1" = 5804
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44457
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25090
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44458
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25094
  AND "kind_id" = 31
  AND "value1" = 5805
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44459
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25094
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44460
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25099
  AND "kind_id" = 31
  AND "value1" = 5806
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44461
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25099
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44462
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25103
  AND "kind_id" = 31
  AND "value1" = 5807
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44463
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25103
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44464
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25107
  AND "kind_id" = 31
  AND "value1" = 5808
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44465
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25107
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44466
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25111
  AND "kind_id" = 31
  AND "value1" = 5809
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44467
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25111
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44468
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25115
  AND "kind_id" = 31
  AND "value1" = 5810
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44469
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25115
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44822
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25761
  AND "kind_id" = 3
  AND "value1" = 3
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44823
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 25761
  AND "kind_id" = 1
  AND "value1" = 65
  AND "value2" = 0;

DELETE FROM "unit_reqs"
WHERE "id" = 44824
  AND "owner_type" = 'QuestComponent'
  AND "owner_id" = 24847
  AND "kind_id" = 31
  AND "value1" = 5980
  AND "value2" = 0;

-- ============================================================================
-- 6. quest_contexts (155 — per-set guards)
-- ============================================================================
-- A1 (88 tutorial stubs): 88 quests
DELETE FROM "quest_contexts"
WHERE "id" IN (2584, 2586, 2589, 2590, 2591, 2592, 2593, 2594, 2595, 2596,
    2597, 2598, 2599, 2600, 2601, 2602, 2603, 2604, 2605, 2606,
    2609, 2612, 2614, 2616, 2620, 2621, 2622, 2623, 2624, 2625,
    2626, 2627, 2628, 2629, 2630, 2631, 2632, 2633, 2634, 2635,
    2636, 2637, 2638, 2639, 2640, 2641, 2642, 2643, 2644, 2645,
    2646, 2647, 2648, 2649, 2650, 2651, 2652, 2653, 2654, 2655,
    2656, 2657, 2658, 2659, 2660, 2661, 2662, 2663, 2664, 2665,
    2666, 2667, 2668, 2669, 2670, 2671, 2672, 2673, 2674, 2675,
    2676, 2677, 2678, 2679, 2680, 2681, 2682, 2683)
  AND "zone_id" = 1
  AND "category_id" = 45
  AND "LEVEL" = 0
  AND "milestone_id" = 5
  AND "let_it_done" = 'f'
  AND "score" = 0
;

-- B1 (33 Dwarf ltd main-story): 33 quests
DELETE FROM "quest_contexts"
WHERE "id" IN (5040, 5773, 5781, 5782, 5783, 5784, 5785, 5786, 5787, 5788,
    5789, 5790, 5791, 5792, 5793, 5794, 5795, 5796, 5797, 5798,
    5799, 5800, 5801, 5802, 5803, 5804, 5805, 5806, 5807, 5808,
    5809, 5810, 5811)
  AND "zone_id" = 1
  AND "category_id" = 93
  AND "LEVEL" = 0
  AND "milestone_id" = 5
  AND "let_it_done" = 't'
  AND "score" = 0
;

-- D1 (27 Dwarf placeholders): 27 quests
DELETE FROM "quest_contexts"
WHERE "id" IN (3484, 3485, 3486, 3487, 3488, 3489, 3490, 3492, 3493, 3494,
    3495, 3496, 3497, 3498, 3499, 3500, 3501, 3502, 3562, 3563,
    3565, 3566, 3567, 3568, 3992, 4408, 5980)
  AND "zone_id" = 1
  AND "category_id" = 93
  AND "milestone_id" = 5
  AND "score" = 0
  AND ("LEVEL" = 0 OR "LEVEL" IS NULL)
  AND "let_it_done" IN ('f','t')
;

-- B2 (3 title quests): 3 quests
DELETE FROM "quest_contexts"
WHERE "id" IN (8000001, 8000002, 8000003)
  AND "zone_id" = 1
  AND "category_id" = 82
  AND "LEVEL" = 0
  AND "milestone_id" = 8000001
  AND "let_it_done" = 't'
  AND "score" = 0
;

-- B3 (3 cat-1 test/unused): 3 quests
DELETE FROM "quest_contexts"
WHERE "id" IN (1835, 1836, 1895)
  AND "zone_id" = 1
  AND "category_id" = 1
  AND "LEVEL" = 0
  AND "milestone_id" = 5
  AND "let_it_done" = 't'
  AND "score" = 0
;

-- B4 (1 Cradle act-less): 1 quests
DELETE FROM "quest_contexts"
WHERE "id" IN (5678)
  AND "zone_id" = 16
  AND "category_id" = 27
  AND "LEVEL" = 0
  AND "milestone_id" = 5
  AND "let_it_done" = 't'
  AND "score" = 0
;

-- ============================================================================
-- Verification
-- ============================================================================
-- BEFORE (run against the pre-fix copy):
--   SELECT COUNT(*) FROM quest_contexts;
--   SELECT COUNT(*) FROM quest_components;
--   SELECT COUNT(*) FROM quest_acts;
--   SELECT COUNT(*) FROM quest_act_con_accept_npcs;
--   SELECT COUNT(*) FROM quest_act_con_report_npcs;
--   SELECT COUNT(*) FROM quest_act_con_accept_items;
--   SELECT COUNT(*) FROM quest_act_supply_exps;
--   SELECT COUNT(*) FROM quest_act_supply_coppers;
--   SELECT COUNT(*) FROM quest_act_con_auto_completes;
--   SELECT COUNT(*) FROM quest_act_supply_appellations;
--   SELECT COUNT(*) FROM item_accept_quests;
--   SELECT COUNT(*) FROM unit_reqs;
-- AFTER (expected):
--     -> 4,721
--     -> 17,592
--     -> 26,853
--     -> 3,510
--     -> 4,333
--     -> 483
--     -> 1,207
--     -> 1,712
--     -> 1,383
--     -> 285
--     -> 364
--     -> 13,228
-- Spot checks (post-fix):
--   SELECT COUNT(*) FROM quest_contexts WHERE id IN (all 155);      -> 0
--   SELECT COUNT(*) FROM quest_components WHERE quest_context_id IN (155); -> 0
--   SELECT COUNT(*) FROM quest_acts WHERE quest_component_id IN (259);    -> 0
--   SELECT COUNT(*) FROM unit_reqs WHERE id IN (123 owned + 3 skill);     -> 0
--   SELECT COUNT(*) FROM unit_reqs WHERE id = 24869;                      -> 1 (UNCHANGED — inert A1 skill gate)
--   SELECT COUNT(*) FROM quest_contexts WHERE id IN (315,1576,1728,2046); -> 4 (UNCHANGED — Q2 KEPT)
--   SELECT COUNT(*) FROM quest_contexts WHERE id IN (1394,1401,1404,...); -> 22 (UNCHANGED — D4 KEPT)