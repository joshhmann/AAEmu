-- Slice-M shipyard frame persistence: one row per placed (half-built) ship frame.
-- Completed ships persist via Slave.Save (slaves table); frame templates and
-- build steps stay read-only reference data in compact.sqlite3
-- (shipyards / shipyard_steps). ShipyardIdManager already seeds used frame ids
-- from `shipyards`.`id`, so this table doubles as the id reservation source.
CREATE TABLE IF NOT EXISTS `shipyards` (
	`id` INT UNSIGNED NOT NULL COMMENT 'Placed frame Id (ShipyardData.Id)',
	`template_id` INT UNSIGNED NOT NULL COMMENT 'shipyards.id in compact.sqlite3',
	`owner_id` INT UNSIGNED NOT NULL COMMENT 'Owning character DB Id (ShipyardData.Type2)',
	`owner_name` VARCHAR(128) NOT NULL DEFAULT '' COMMENT 'Owning character name (denormalized for display)',
	`faction_id` INT UNSIGNED NOT NULL DEFAULT '0' COMMENT 'Owning faction (ShipyardData.Type3)',
	`step` INT NOT NULL DEFAULT '0' COMMENT 'Client-visible build step (ShipyardData.Step; 1000 = launch ceremony in flight)',
	`actions` INT NOT NULL DEFAULT '0' COMMENT 'Client-visible build actions (ShipyardData.Actions)',
	`hp` INT NOT NULL DEFAULT '0' COMMENT 'Live frame Hp (decay damage accumulator)',
	`x` FLOAT NULL DEFAULT NULL,
	`y` FLOAT NULL DEFAULT NULL,
	`z` FLOAT NULL DEFAULT NULL,
	`yaw` FLOAT NULL DEFAULT NULL COMMENT 'Facing (ShipyardData.zRot)',
	`zone_id` INT UNSIGNED NOT NULL DEFAULT '0' COMMENT 'Zone key at placement',
	`spawned` DATETIME NULL DEFAULT NULL COMMENT 'Placement time; drives the 3-day decay clock',
	PRIMARY KEY (`id`) USING BTREE
)
COMMENT='Placed half-built ship frames'
COLLATE='utf8mb4_general_ci'
ENGINE=InnoDB
;
