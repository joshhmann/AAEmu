-- ============================================================================
-- Fix: quest data defects — 3 next_component + unit_reqs 16000 + 2 item_accept rows
-- Author: Tai · 2026-08-04 · card t_abf740ee
-- Source: scorecard-explorations/data-defects.md (t_7416ea48 classification,
--         verified against prod compact.sqlite3 md5 78b3bdbf038db3b927056106efdf91af)
--
-- IMPORTANT — when this takes effect:
--   compact.sqlite3 is a READ-ONLY reference (upstream alignment rule 3, and
--   AGENTS.md: "Prefer SQL/patches/compact/ only for intentional compact.sqlite3
--   fixups"). These changes land against the reference at the NEXT DATA SYNC,
--   when the game's compact.sqlite3 is regenerated/refreshed from source with
--   this patch applied. Until then, runtime behaviour is governed by the
--   additive overlay (fix/verifier-data-overlay, QuestDataOverlay) + verifier
--   severity refinement (fix/verifier-refinement) already on the fork.
--
-- NOTE on location (deviation from card wording, deliberate): the card said
--   SQL/updates/, but MySqlDatabaseUpdater executes every pending
--   *aaemu_game*.sql against MySQL aaemu_game at Game startup and the server
--   HARD-STOPS on failure — and quest_components/unit_reqs/item_accept_quests
--   do not exist in the MySQL schema (quest templates live only in
--   compact.sqlite3). A script in SQL/updates/ would break Game boot. This is
--   an intentional compact.sqlite3 fixup, so it lives in SQL/patches/compact/
--   per AGENTS.md.
--
-- Contents:
--   1. 3 cosmetic quest_components.next_component UPDATEs (deprecated field;
--      the engine never reads next_component — alignment only, silences the
--      census COMPONENT_NEXT_MISSING rows. No runtime behaviour change.)
--        quest 330 comp 1520: next 3543 (exists in no quest) -> 1521 (Ready)
--        quest 776 comp 3480: next 4370 (exists in no quest) -> 3482 (Progress)
--        quest 777 comp 3488: next 3487 (exists in no quest) -> 11591 (Ready)
--   2. DELETE unit_reqs row 16000 — unblocks quest 2951's supply gate:
--      owner comp 12913 (quest 2951, live kind-32 quest) had a kind-32 gate on
--      value1 745, an ORPHANED quest_context (no quest_contexts row). 745 can
--      never be in progress, so 2951's Supply step stalled forever. Removing
--      the dangling requirement row opens the gate.
--   3. DELETE 2 dangling item_accept_quests rows: items 26756 -> quest 5133 and
--      34820 -> quest 6420, both orphaned quest_contexts. Using the items
--      silently did nothing; the grant rows are dead weight.
--
-- Verification queries (before/after counts) are at the bottom of this file.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1) next_component alignment (3 rows)
-- ----------------------------------------------------------------------------
UPDATE quest_components SET next_component = 1521 WHERE id = 1520;
UPDATE quest_components SET next_component = 3482 WHERE id = 3480;
UPDATE quest_components SET next_component = 11591 WHERE id = 3488;

-- ----------------------------------------------------------------------------
-- 2) unblock quest 2951's supply gate (1 row)
-- ----------------------------------------------------------------------------
DELETE FROM unit_reqs WHERE id = 16000;

-- ----------------------------------------------------------------------------
-- 3) dangling item-accept grants for orphaned quests (2 rows)
-- ----------------------------------------------------------------------------
DELETE FROM item_accept_quests WHERE quest_id IN (5133, 6420);

-- ============================================================================
-- Verification
-- ============================================================================
-- BEFORE (run against the pre-fix copy):
--   SELECT id, quest_context_id, next_component FROM quest_components WHERE id IN (1520,3480,3488);
--     -> 1520|330|3543   3480|776|4370   3488|777|3487   (targets exist in no quest)
--   SELECT id, owner_id, owner_type, kind_id, value1 FROM unit_reqs WHERE id = 16000;
--     -> 16000|12913|Skill|32|745   (745 = orphaned quest_context; gate never satisfiable)
--   SELECT id, item_id, quest_id FROM item_accept_quests WHERE quest_id IN (5133,6420);
--     -> 177|26756|5133   547|34820|6420   (both quest_contexts orphaned)

-- AFTER (run against the post-fix copy — expected rows below):
--   SELECT id, quest_context_id, next_component FROM quest_components WHERE id IN (1520,3480,3488);
--     -> 1520|330|1521   3480|776|3482   3488|777|11591   (all 3 targets exist)
--   SELECT COUNT(*) FROM unit_reqs WHERE id = 16000;
--     -> 0
--   SELECT COUNT(*) FROM item_accept_quests WHERE quest_id IN (5133,6420);
--     -> 0
