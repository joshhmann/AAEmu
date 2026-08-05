-- Author: Tai - 2026/08/05
-- Drop the QUEST_NO_START cluster 1533–1548: 23 legacy 1.0-era tutorial shells.
--
-- Audit: scorecard-explorations/data-defects.md §5 (verdict (c) drop) +
-- scorecard-explorations/no-start-cluster-1533-1548-evidence.md (t_d5e088ed) +
-- scorecard-explorations/dropped-content-register.md §2. Decision: Josh
-- (2026-08-05 chat, msg 1534679020862701689): "Unblock granted, if they're
-- orphans we prob don't need to code em in." Drop = data-level deletion via
-- SQL patch + verifier allowlist removal (QuestSanityVerifier.cs), never code
-- to keep dead content alive.
--
-- The cluster: quests 1533, 1535–1549, 1551–1554, 1640, 1830, 1831 — every
-- one has components but ZERO Start-kind components, and zero accept surfaces
-- (item_accept_quests / accept_quest_effects / doodad_func_quests /
-- quest_act_con_accept_components / unit_reqs kind 31/32/33/37 gates all 0).
-- The engine can never accept them: Quest.CreateQuestSteps() builds no Start
-- step and Quest.StartQuest() returns false (NewQuestCode.cs:42-56). 1534 and
-- 1550 are pure id gaps (no quest_contexts row — nothing to delete).
--
-- Each quest carries exactly one kind-8 (Reward) component with
-- QuestActSupplyCopper + QuestActSupplyExp acts (1830/1831 "UNUSED" are
-- act-less; 1831 has Progress+Ready comps). The act DETAIL rows (shared
-- quest_act_supply_coppers/exps, small ids like 6/7/8) are referenced by many
-- other quests — they are NOT deleted; only the 42 cluster quest_acts rows
-- are unwired.
--
-- Guards: every row pinned to its full verified shape — verified 2026-08-05
-- on compact.sqlite3 (md5 78b3bdbf038db3b927056106efdf91af). No other table
-- references the cluster: 0 quest_context_texts rows, 0 item/effect/doodad
-- accept rows, 0 quest_act_con_accept_components targets, 0 quest_components
-- outside the cluster pointing in (next_component), 0 quest_acts outside the
-- cluster referencing cluster components. The 9 unit_reqs rows with value1 in
-- the cluster are Skill/AiEvent-owned id collisions (kinds 30/23/35 — buff
-- tags/spheres, NOT quest deps) and are deliberately left untouched.
--
-- Drift: exactly -23 / -25 / -42 rows on compact.sqlite3 (md5 78b3bdbf...):
--   quest_contexts    4876 -> 4853
--   quest_components  17851 -> 17826
--   quest_acts        26886 -> 26844

-- 42 quest_acts rows (base wiring rows) for the 25 cluster components.
DELETE FROM "quest_acts"
WHERE "id" IN (10867,10868,10869,10870,10871,10872,10873,10875,10876,10877,
               10878,10879,10880,10881,10882,10883,10884,10885,10886,10887,
               10888,10889,10890,10891,10892,10893,10894,10895,10896,10897,
               10898,10899,10900,10901,10902,10903,10904,10905,10906,10907,
               10910,10911)
  AND "quest_component_id" IN (7738,7739,7740,7741,7742,7743,7744,7745,7746,
                               7747,7748,7749,7750,7751,7752,7753,7754,7755,
                               7756,7757,7758,8492,8494,8495,8496)
  AND "act_detail_type" IN ('QuestActSupplyCopper', 'QuestActSupplyExp');

-- 25 quest_components rows for the 23 cluster contexts (1831 has 3 comps).
DELETE FROM "quest_components"
WHERE "id" IN (7738,7739,7740,7741,7742,7743,7744,7745,7746,7747,7748,7749,
               7750,7751,7752,7753,7754,7755,7756,7757,7758,8492,8494,8495,
               8496)
  AND "quest_context_id" IN (1533,1535,1536,1537,1538,1539,1540,1541,1542,
                             1543,1544,1545,1546,1547,1548,1549,1551,1552,
                             1553,1554,1640,1830,1831);

-- 23 quest_contexts rows.
DELETE FROM "quest_contexts"
WHERE "id" IN (1533,1535,1536,1537,1538,1539,1540,1541,1542,1543,1544,1545,
               1546,1547,1548,1549,1551,1552,1553,1554,1640,1830,1831)
  AND "category_id" IN (28, 1);
