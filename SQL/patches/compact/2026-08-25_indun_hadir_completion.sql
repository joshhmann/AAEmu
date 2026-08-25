-- Author: ox-alpha agent run (INDUN-01) - 2026/08/25
-- INDUN-01 minimal completion hook for Hadir Farm (zone group 46).
--
-- Context (dossier scorecard-explorations/mechanics/indun-domain.md):
-- The low-level dungeons (45 Burnt Castle, 46 Hadir Farm, 47 Sal Temple,
-- 50 Deadmine, 51 Howling Abyss, 52 Cradle) ship with ZERO scripted events:
-- no indun_events rows exist for zone_group_id 46, so a final-boss kill never
-- marks the room cleared. This patch adds an additive NpcKilled -> SetRoomCleared
-- chain modeled on the Nachashgar (55) rows (event 19 -> action 39), so that
-- killing a Hadir boss inside the dungeon world fires the engine's standard
-- IndunEventNpcKilleds -> DoIndunActions -> IndunActionSetRoomCleareds chain
-- (AAEmu.Game/Models/Game/Indun/Events/IndunEventNpcKilleds.cs,
--  AAEmu.Game/Models/Game/Indun/Actions/IndunActionSetRoomCleareds.cs).
--
-- Shape (verified against compact.sqlite3 + Data/Worlds/instance_hadir_farm):
--   zones:            id=169 zone_key=241 group_id=46 name='instance_hadir_farm'
--   indun_zones:      zone_group_id=46 '하디르의 농장' level 31-55 max 5 party_only=f
--   doodad_func_enter_instances id=7 -> zone_id=169 (portal doodad almighty 4115,
--                     func group 9981, func skill 17731 '하디르의 농장 진입')
--   dungeon interior: world template 'instance_hadir_farm'; its npc_spawns.json
--                     carries the two Hadir bosses npcs 10166 ('하디르', LEVEL 35,
--                     at 806.1/623.0) and 10167 ('하디르', LEVEL 35, at 764.1/658.7 —
--                     the one standing by exit doodad 4927, i.e. the last room).
--                     Both are wired to the completion action.
--
-- Ids are chosen in the 460x range (all tables max out well below 1000 today:
-- indun_events<=80, indun_actions<=112, indun_event_npc_killeds<=17,
-- indun_action_set_room_cleareds<=14, indun_rooms<=15, indun_room_spheres<=15)
-- to stay collision-free against upstream data growth.
--
-- Runtime application convention (matches SQL/patches/compact usage): applied
-- manually to the RUNTIME copy of compact.sqlite3, never to the canonical file.
-- For the e2e stack (paths per E2eStack.cs):
--   sqlite3 /root/aaemu-e2e/runtime/game/Data/compact.sqlite3 \
--     < SQL/patches/compact/2026-08-25_indun_hadir_completion.sql
-- The script is idempotent (pinned-id delete+insert), so re-applying corrects
-- rather than duplicates.
--
-- Drift when applied once: +7 rows total (one per INSERT below).

-- Room geometry: center doodad is the Hadir entrance portal itself (4115);
-- only referenced at runtime by NoAliveChInRooms scanning, which this patch does
-- NOT add -- kept non-null purely because IndunGameData.Load() reads the joined
-- columns unconditionally (GameData/IndunGameData.cs #region Rooms).
DELETE FROM indun_room_spheres WHERE id = 4601;
INSERT INTO indun_room_spheres (id, center_doodad_id, radius)
VALUES (4601, 4115, 100);

DELETE FROM indun_rooms WHERE id = 4601;
INSERT INTO indun_rooms (id, zone_group_id, name, shape_id, shape_type)
VALUES (4601, 46, 'hadir_farm_main', 4601, 'IndunRoomSphere');

DELETE FROM indun_action_set_room_cleareds WHERE id = 4601;
INSERT INTO indun_action_set_room_cleareds (id, indun_room_id)
VALUES (4601, 4601);

DELETE FROM indun_actions WHERE id = 4601;
INSERT INTO indun_actions (id, zone_group_id, name, detail_id, detail_type, next_action_id)
VALUES (4601, 46, 'hadir_farm_room_clear', 4601, 'IndunActionSetRoomCleared', 0);

-- One NpcKilled condition per boss; both fire the same completion action.
DELETE FROM indun_event_npc_killeds WHERE id IN (4601, 4602);
INSERT INTO indun_event_npc_killeds (id, npc_id) VALUES (4601, 10166);
INSERT INTO indun_event_npc_killeds (id, npc_id) VALUES (4602, 10167);

DELETE FROM indun_events WHERE id IN (4601, 4602);
INSERT INTO indun_events (id, zone_group_id, name, condition_id, condition_type, start_action_id)
VALUES (4601, 46, 'hadir_boss_10166_killed_completion', 4601, 'IndunEventNpcKilled', 4601);
INSERT INTO indun_events (id, zone_group_id, name, condition_id, condition_type, start_action_id)
VALUES (4602, 46, 'hadir_boss_10167_killed_completion', 4602, 'IndunEventNpcKilled', 4601);
