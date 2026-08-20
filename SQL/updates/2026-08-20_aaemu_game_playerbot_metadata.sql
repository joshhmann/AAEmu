-- ----------------------------------------
-- B4 playerbot_metadata store (M6 deferred gate #5):
-- per-bot personality/profession/home/schedule/behavior/planner state,
-- keyed by characters.id. Runtime self-heal (PlayerBotMetadataStore.
-- EnsureSchema) ships the same CREATE TABLE for unmanaged environments.
-- ----------------------------------------

CREATE TABLE IF NOT EXISTS `playerbot_metadata` (
  `character_id` INT UNSIGNED NOT NULL PRIMARY KEY,
  `personality` VARCHAR(255) NOT NULL DEFAULT '',
  `profession` VARCHAR(64) NOT NULL DEFAULT '',
  `has_home` TINYINT(1) NOT NULL DEFAULT 0,
  `home_world_id` INT UNSIGNED NOT NULL DEFAULT 0,
  `home_zone_id` INT UNSIGNED NOT NULL DEFAULT 0,
  `home_x` FLOAT NOT NULL DEFAULT 0,
  `home_y` FLOAT NOT NULL DEFAULT 0,
  `home_z` FLOAT NOT NULL DEFAULT 0,
  `schedule` TEXT NULL,
  `behavior_config` TEXT NULL,
  `planner_state` TEXT NULL,
  `updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
