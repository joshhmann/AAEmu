-- Slice-1 dominion persistence (DOMINION-01): one row per declared dominion.
-- Mutable ownership/tax state; everything else about sieges is read-only
-- reference data in compact.sqlite3 (siege_zones / siege_settings / siege_plans).
CREATE TABLE IF NOT EXISTS `dominions` (
	`zone_group_id` INT UNSIGNED NOT NULL COMMENT 'zone_groups.id of the owned zone',
	`expedition_id` INT UNSIGNED NOT NULL DEFAULT '0' COMMENT 'Owning expedition Id',
	`expedition_name` VARCHAR(128) NOT NULL DEFAULT '' COMMENT 'Owning expedition name (denormalized for display)',
	`tax_rate` INT NOT NULL DEFAULT '50' COMMENT 'Tax rate set by the owner expedition',
	`declared_at` DATETIME NULL DEFAULT NULL COMMENT 'When the dominion was declared',
	PRIMARY KEY (`zone_group_id`) USING BTREE
)
COMMENT='Declared dominions per castle zone group'
COLLATE='utf8mb4_general_ci'
ENGINE=InnoDB
;
