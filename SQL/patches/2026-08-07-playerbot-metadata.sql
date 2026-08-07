-- ============================================================================
-- playerbot_* metadata schema — additive, fork-only (P1 slice #7, t_afbce6a0)
-- ============================================================================
-- Owner: Hyraxknot Division (Tai) — ARCHITECTURE_REVIEW deliverable 10, card 7.
-- Spec §13: bot-only metadata lives in additive tables; gameplay state rides
-- the normal Character lifecycle (SaveManager + leave-save). These tables are
-- NEVER written per-AI-step — writes go through IBotPersistence dirty-flush
-- (batched periodic + mandatory on deactivate/downgrade/shutdown).
--
-- Design rules (locked in ARCHITECTURE_REVIEW.md deliverable 4, H4):
--   * Additive only: CREATE TABLE IF NOT EXISTS, no ALTER, no DROP, no FK
--     constraints — safe on any existing game DB, including prod.
--   * One row per bot citizen for state tables (PK = character_id); the
--     schedule table is the only one-to-many (PK = auto id).
--   * fidelity + pressure_state use the spec fidelity names:
--     Dormant(0) / Reduced(1) / Full(2) for fidelity; Normal(0) / High(1) /
--     Critical(2) for population pressure (ROADMAP M6.5, spec §7/§14).
--
-- NOTE (application path): the game's MySqlDatabaseUpdater scans SQL/updates/
-- only; this file lives in SQL/patches/ per the card's target lock. Deploy
-- must apply it (or mirror it into SQL/updates/) before bot slices boot.
-- The rig proves the DDL applies clean on a live MySQL.
-- ============================================================================

-- One profile row per bot citizen (mirrors characters.id; account kept for
-- convenience — real accounts are provisioned by the M6.0 slice).
CREATE TABLE IF NOT EXISTS `playerbot_profile` (
  `character_id`     int unsigned NOT NULL,
  `account_id`       int unsigned NOT NULL DEFAULT 0,
  `fidelity`         tinyint NOT NULL DEFAULT 0 COMMENT 'BotFidelity: 0=Dormant, 1=Reduced, 2=Full',
  `behavior_profile` varchar(64) NOT NULL DEFAULT 'idle' COMMENT 'Behavior stack profile name (idle/roam/questdrive/...)',
  `schedule_enabled` tinyint(1) NOT NULL DEFAULT 1,
  `last_seen`        datetime NOT NULL DEFAULT '0001-01-01 00:00:00' COMMENT 'Last time the bot was embodied (UTC)',
  `created_at`       datetime(0) NOT NULL DEFAULT CURRENT_TIMESTAMP(0),
  `updated_at`       datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  PRIMARY KEY (`character_id`) USING BTREE
) ENGINE = InnoDB CHARACTER SET = utf8 COLLATE = utf8_general_ci COMMENT = 'PlayerBot citizen profile metadata' ROW_FORMAT = DYNAMIC;

-- One-to-many schedule windows per bot (daily activity windows, server-local
-- time; day_mask bit 0=Monday .. bit 6=Sunday, all bits set = every day).
CREATE TABLE IF NOT EXISTS `playerbot_schedule` (
  `id`            int unsigned NOT NULL AUTO_INCREMENT,
  `character_id`  int unsigned NOT NULL,
  `day_mask`      tinyint unsigned NOT NULL DEFAULT 127,
  `start_time`    time NOT NULL DEFAULT '00:00:00',
  `end_time`      time NOT NULL DEFAULT '23:59:59',
  `activity_type` varchar(32) NOT NULL DEFAULT 'idle',
  `params`        text NULL COMMENT 'Activity-specific parameters (future slices serialize here)',
  `enabled`       tinyint(1) NOT NULL DEFAULT 1,
  `created_at`    datetime(0) NOT NULL DEFAULT CURRENT_TIMESTAMP(0),
  `updated_at`    datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  PRIMARY KEY (`id`) USING BTREE,
  KEY `idx_playerbot_schedule_character` (`character_id`) USING BTREE
) ENGINE = InnoDB CHARACTER SET = utf8 COLLATE = utf8_general_ci COMMENT = 'PlayerBot schedule windows' ROW_FORMAT = DYNAMIC;

-- Current activity state per bot (bounded: one row per citizen, counters
-- roll up history — never an unbounded log; spec §13 metadata only).
CREATE TABLE IF NOT EXISTS `playerbot_activity` (
  `character_id`  int unsigned NOT NULL,
  `activity_type` varchar(32) NOT NULL DEFAULT 'idle',
  `state`         tinyint NOT NULL DEFAULT 0 COMMENT 'BotActivityState: 0=Idle, 1=Running, 2=Completed, 3=Failed, 4=Interrupted',
  `started_at`    datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  `ended_at`      datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  `cycles`        int unsigned NOT NULL DEFAULT 0 COMMENT 'Completed cycles of the current activity',
  `failure_count` int unsigned NOT NULL DEFAULT 0,
  `last_error`    varchar(255) NULL,
  `updated_at`    datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  PRIMARY KEY (`character_id`) USING BTREE
) ENGINE = InnoDB CHARACTER SET = utf8 COLLATE = utf8_general_ci COMMENT = 'PlayerBot current activity state' ROW_FORMAT = DYNAMIC;

-- Home / return anchor per bot (dormant bots park here; return path when the
-- schedule window closes or the bot is deactivated).
CREATE TABLE IF NOT EXISTS `playerbot_home` (
  `character_id`         int unsigned NOT NULL,
  `world_id`             int unsigned NOT NULL DEFAULT 0,
  `zone_id`              int unsigned NOT NULL DEFAULT 0,
  `x`                    float NOT NULL DEFAULT 0,
  `y`                    float NOT NULL DEFAULT 0,
  `z`                    float NOT NULL DEFAULT 0,
  `yaw`                  float NOT NULL DEFAULT 0,
  `return_on_combat_exit` tinyint(1) NOT NULL DEFAULT 1,
  `updated_at`           datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  PRIMARY KEY (`character_id`) USING BTREE
) ENGINE = InnoDB CHARACTER SET = utf8 COLLATE = utf8_general_ci COMMENT = 'PlayerBot home / return anchor' ROW_FORMAT = DYNAMIC;

-- Per-bot named flag bitmask (64 bits; named assignments come with the slices
-- that define them — e.g. M5 actor lifecycle flags).
CREATE TABLE IF NOT EXISTS `playerbot_memory_flags` (
  `character_id` int unsigned NOT NULL,
  `flags`        bigint unsigned NOT NULL DEFAULT 0,
  `last_updated` datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  PRIMARY KEY (`character_id`) USING BTREE
) ENGINE = InnoDB CHARACTER SET = utf8 COLLATE = utf8_general_ci COMMENT = 'PlayerBot persistent flag bitmask' ROW_FORMAT = DYNAMIC;

-- PopulationDirector state per bot (the only fidelity authority — spec §7/§14).
CREATE TABLE IF NOT EXISTS `playerbot_population_state` (
  `character_id`       int unsigned NOT NULL,
  `fidelity`           tinyint NOT NULL DEFAULT 0 COMMENT 'BotFidelity: 0=Dormant, 1=Reduced, 2=Full',
  `pressure_state`     tinyint NOT NULL DEFAULT 0 COMMENT 'BotPressureState: 0=Normal, 1=High, 2=Critical',
  `last_transition_at` datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  `transition_count`   int unsigned NOT NULL DEFAULT 0,
  `updated_at`         datetime(0) NOT NULL DEFAULT '0001-01-01 00:00:00',
  PRIMARY KEY (`character_id`) USING BTREE
) ENGINE = InnoDB CHARACTER SET = utf8 COLLATE = utf8_general_ci COMMENT = 'PlayerBot population/fidelity director state' ROW_FORMAT = DYNAMIC;
