# ArcheAge Slums — Feature Completeness Scorecard (enriched)

Layers: (1) canonical 1.2 data surface (679 sqlite tables), (2) code wiring,
(3) upstream issue tracker (AAEmu/AAEmu open issues, 2026-08-03).

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

## Upstream issue tracker — known gaps

- 67 open issues: 50 bug · 30 quest · 9 missing-data · 4 enhancement · 3 skill

### Quests reported broken (playtest targets, by ID)

1208, 1255, 1256, 1257, 1258, 1259, 1260, 1261, 1262, 1263, 1264, 1265, 1266, 1267, 1268, 1269, 1270, 1271, 1272, 1274, 1275, 1276, 1277, 1278, 1279, 1280, 1281, 1282, 1329, 1450

## Fork fixes (our lane, no upstream PR)

- **BUG-006 — kill-acceptor quests can never start (FIXED on `fix/quest-kill-acceptor`, 2026-08-03).**
  `QuestActConAcceptNpcKill.RunAct` was a copy-paste of the Npc accept check, and no code
  path ever set a Kill acceptor. Added `QuestAcceptorType.Kill`, wired
  `DoOnMonsterHuntEvents` (the Npc.cs death path funnel, Npc.cs:877/986/1019) to start
  matching quests with the Kill acceptor, and fixed `RunAct` to match. Live data check:
  **380 quests** had ALL Start acts as kill-accepts (e.g. 182, 205, 556, 913, 1057, 1208)
  — all now startable on kill. Quest 1119 (upstream #1208) is actually a plain
  Npc-accept quest (Npc 2237), not part of this family.

### System-level bugs (non-quest)

- #696 [skill] [BUG] Tree thinning of old trees doesn't work as intended
- #920 [bug] [BUG] Ezna misses a lot of things
- #949 [bug] [BUG]  Pack that disappeared
- #972 [bug] [BUG] Red farm hauler
- #973 [bug] [BUG] Letter box
- #974 [bug] [BUG] Letter box  Shipping problem
- #978 [skill] [BUG] [Skill] (24353) Fortuna Die does not function correctly
- #985 [question] [BUG] Save character database
- #1011 [bug] [BUG] Harani main quest 3509 invisible quest npc
- #1033 [missing functionality] [BUG] Issue with NavMesh on the main_world, NPCs and monsters not being on the correct "height"
- #1046 [question] [BUG] with item
- #1047 [bug] [BUG] Quest NPC Nubo spawned inside mountain
- #1091 [bug] [BUG] <with farm hauler>
- #1152 [feedback to address] [BUG]  Treasure map
- #1168 [missing functionality] [BUG]  the packs in the merchant ship disappear
- #1170 [bug] [BUG]  Sometimes a player character looses its model and items (It happens randomly)
- #1175 [bug] [BUG] Return to email sender
- #1323 [bug] [BUG] PvP Arena scoring not working
- #1425 [bug] [BUG] NPCs and monsters floating above ground on develop
- #1491 [bug] [BUG] ActiveRegionTick is subscribed synchronously and starves the 100 ms AI tick

## Canonical resources — how 1.2 mechanics actually worked

| Resource | Use | URL |
|----------|-----|-----|
| ArcheAge Fandom Wiki | feature/mechanic reference (trade packs, labor, housing) | https://archeage.fandom.com/wiki/ArcheAge_Wiki |
| Ten Ton Hammer 1.2-era guides | trade pack economy, shipbuilding (2014 era = 1.2) | https://www.tentonhammer.com/guides/archeage-trade-pack-guide |
| AAEmu wiki (in-repo) | project's own component/architecture docs | Docs/wiki/ |
| AAEmu GitHub issues | known-broken list (50 bug / 30 quest / 9 data) | https://github.com/AAEmu/AAEmu/issues |
| compact.sqlite3 | the canonical 1.2 data surface (679 tables) | server: .server_files/AAEmu.Game/Data/ |
| game_pak (r208022) | client-side assets the server references | server: .server_files/AAEmu.Game/ClientData/ |
| ArcheAge Classic / ArcheRage | private-server behavior reference (what "working" looks like) | https://aa-classic.com |
