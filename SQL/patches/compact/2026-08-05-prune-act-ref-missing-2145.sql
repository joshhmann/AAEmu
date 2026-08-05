-- Author: Tai - 2026/08/05
-- Prune 2 dangling ConAcceptComponent acts: quest 2145 -> 2146 (ACT_REF_MISSING_QUEST)
-- and sibling quest 1960 -> 1961 (same defect, same dead cat-34 chain).
--
-- Audit: scorecard-explorations/data-defects.md §4 + §7 (verdict (c) drop / minimal
-- act deletion; t_7416ea48) and scorecard-explorations/act-ref-2145-rig.md
-- (t_0d743f43, t_60a559ab). The Reward comps of the live quests carry
-- QuestActConAcceptComponent acts whose quest_context_id has NO quest_contexts
-- row: 2145's Reward comp 9927 -> act 89 -> context 2146, and 1960's Reward
-- comp 9794 -> act 75 -> context 1961. The contexts are orphaned mid-chain links
-- of the abandoned cat-34 crafting chain (1954->...->1960->1961->...->2145->2146);
-- nothing in the chain is reachable (roots gated on orphans), so the targets are
-- never loaded and the self-start targets can never be found
-- (QuestManager.GetTemplate(2146/1961) always null).
--
-- This is the documented minimal action from data-defects.md §4: delete ONLY the
-- two dangling acts + their quest_acts rows. The orphan contexts (1961, 2146, and
-- the rest of the chain) are NOT touched — they stay in place under the drop
-- verdict, keeping the block recoverable if the crafting chain is ever wanted.
-- The valid self-start acts (2145's Start comp 9925 accept-act 88 -> 2145, 1960's
-- Start comp 9792 accept-act 66 -> 1960) are untouched and still resolve.
--
-- Guards: each row is pinned to its full verified shape (id + every relevant
-- column) — verified 2026-08-05 on compact.sqlite3 (md5
-- 78b3bdbf0383db3b927056106efdf91af). Only these 2 quest_acts rows reference
-- accept components 75/89 (checked: no other act rows point at them).
--
-- Drift: exactly -2 rows per table on compact.sqlite3 (md5 78b3bdbf...):
--   quest_act_con_accept_components 384 -> 382
--   quest_acts                       26886 -> 26884

DELETE FROM "quest_acts"
WHERE "id" IN (14072, 14121)                       -- quest 1960 comp 9794 act 75 / quest 2145 comp 9927 act 89
  AND "quest_component_id" IN (9794, 9927)
  AND "act_detail_type" = 'QuestActConAcceptComponent'
  AND "act_detail_id" IN (75, 89);

DELETE FROM "quest_act_con_accept_components"
WHERE "id" IN (75, 89)
  AND "quest_context_id" IN (1961, 2146);
