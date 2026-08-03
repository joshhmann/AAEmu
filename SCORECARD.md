# ArcheAge 1.2 Feature Completeness Scorecard

Generated from: compact.sqlite3 r208022 (679 tables) vs AAEmu develop (95 managers).

## Legend
- **Tables**: canonical sqlite tables in the domain
- **Data-wired**: tables referenced by any .cs (server reads this data)
- **Managers**: game systems present in code

## Domain scorecard

| Domain | Tables | Data-wired | % | Managers |
|--------|--------|-----------|------|----------|
| misc | 206 | 114 | 55% | — |
| doodads | 135 | 123 | 91% | DoodadIdManager, DoodadManager |
| quests | 85 | 70 | 82% | QuestIdManager, QuestManager |
| items | 53 | 31 | 58% | — |
| npcs | 18 | 12 | 67% | NpcManager |
| fx-visuals | 15 | 0 | 0% | — |
| slaves | 15 | 9 | 60% | SlaveManager |
| instances | 14 | 14 | 100% | — |
| buffs | 10 | 7 | 70% | — |
| skills | 10 | 5 | 50% | SkillManager, SkillTlIdManager |
| spheres | 10 | 9 | 90% | SphereQuestManager |
| equipment | 9 | 6 | 67% | — |
| housing | 8 | 3 | 38% | HousingIdManager, HousingManager, HousingTldManager |
| plots | 6 | 6 | 100% | PlotManager |
| characters | 5 | 3 | 60% | CharacterIdManager, CharacterManager |
| crafting | 5 | 4 | 80% | — |
| siege | 5 | 0 | 0% | — |
| loot | 4 | 4 | 100% | — |
| mates | 4 | 0 | 0% | MateIdManager, MateManager |
| merchants | 4 | 2 | 50% | — |
| premium | 4 | 0 | 0% | — |
| ranks | 4 | 0 | 0% | — |
| specialty-trade | 4 | 3 | 75% | — |
| towerdefense | 4 | 4 | 100% | — |
| transfers | 4 | 4 | 100% | TransferManager |
| zones | 4 | 3 | 75% | ZoneManager |
| auction | 3 | 0 | 0% | AuctionIdManager, AuctionManager |
| bubbles | 3 | 1 | 33% | — |
| models | 3 | 0 | 0% | ModelManager |
| moulds | 3 | 0 | 0% | — |
| shipyards | 3 | 2 | 67% | ShipyardIdManager, ShipyardManager |
| world | 3 | 0 | 0% | WorldIdManager, WorldManager |
| achievements | 2 | 2 | 100% | — |
| battlefields | 2 | 1 | 50% | — |
| combat | 2 | 1 | 50% | — |
| race-tracks | 2 | 0 | 0% | — |
| sounds | 2 | 0 | 0% | — |
| appellations | 1 | 1 | 100% | — |
| express-text | 1 | 1 | 100% | — |
| fishing | 1 | 1 | 100% | — |
| gimmicks | 1 | 1 | 100% | GimmickIdManager, GimmickManager |
| music | 1 | 0 | 0% | MusicIdManager, MusicManager |
| taxation | 1 | 1 | 100% | TaxationsManager |

## Zero-data-wired domains (data exists, server ignores it)

- **fx-visuals** (15 tables): fx_cam_fovs, fx_cgas, fx_cgfs, fx_chrs, fx_decals, fx_group_fx_items...
- **siege** (5 tables): siege_items, siege_plans, siege_settings, siege_ticket_offense_prices, siege_zones
- **mates** (4 tables): mate_equip_pack_groups, mate_equip_pack_items, mate_equip_packs, mate_equip_slot_packs
- **premium** (4 tables): premium_benefits, premium_configs, premium_grades, premium_points
- **ranks** (4 tables): rank_reward_links, rank_rewards, rank_scope_links, rank_scopes
- **auction** (3 tables): auction_a_categories, auction_b_categories, auction_c_categories
- **models** (3 tables): model_attach_point_strings, model_bindings, model_quest_cameras
- **moulds** (3 tables): mould_pack_items, mould_packs, moulds
- **world** (3 tables): world_groups, world_spec_configs, world_var_defaults
