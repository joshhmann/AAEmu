# Partial-Wiring Deep-Dive: housing, auction, specialty-trade, items, mates, models

**Repo:** `/root/aaemu-dev` (fork of AAEmu/AAEmu, ArcheAge 1.2 emulator, .NET 10)
**Method:** grep of all 679 canonical sqlite table names against `AAEmu.Game/**/*.cs` (bin/obj excluded), plus manager/loader source reads. Table counts below are measured from this repo, not the scorecard — see notes where they differ.
**Date:** 2026-08-03

---

## Summary table

| Domain | Tables | Wired | % | Loader / Manager | Biggest missing slice |
|--------|--------|-------|---|------------------|----------------------|
| housing | 9 (+2 item_*) | 4 (+2) | 44% (55% w/ item_*) | `HousingGameData` → `HousingManager` | Deco-limit enforcement, zone validation |
| auction | 3 | 0 | 0% | `AuctionManager` (MySQL `auction_house` only) | Category *name* data only — low impact |
| specialty-trade | 4 | 3 | 75% | `SpecialtyManager` (sqlite) | `specialty_bundles` name table — near-zero impact |
| items | 54 | 32 | 59% | `ItemManager` + `ItemGameData`, `ItemConversionGameData` | Socketing (TODO), proc bindings, recipe books |
| mates | 4 | 0 | 0% | `MateGameData` (mount skills only); `MateManager` is runtime-only | Mate-equip pack data (container/packets already exist) |
| models | 4 | 1 | 25% | `ModelManager` | `model_bindings` attach points — low impact |

---

## 1. Housing — 4/9 tables wired (scorecard says 38%; measured 4/9 = 44% core, 6/11 = 55% incl. item_housings)

### What IS wired

**Tables referenced:**
- `housings` — `GameData/HousingGameData.cs:74` (template load: id, name, category_id, main_model_id, hp, garden_radius, taxation_id, **deco_limit, absolute_deco_limit, housing_deco_limit_id** at `:103-105`, is_sellable, heavy_tax, always_public…); `HousingManager.cs:147` (MySQL `SELECT * FROM housings` for player-owned state); `House.cs:293` (`REPLACE INTO housings` save); `HousingIdManager.cs:14` (id allocator).
- `housing_binding_doodads` — `HousingGameData.cs:115` (bound doodads per house template, joined with `Data/housing_bindings*.json` position files at `:238-282`).
- `housing_build_steps` — `HousingGameData.cs:149` (step → model_id, skill_id, num_actions per house).
- `housing_decorations` — `HousingGameData.cs:177` (decoration designs: doodad_id, allow_on_floor/wall/ceiling, actability_group_id, actability_up, deco_actability_group_id).
- `item_housings` — `HousingGameData.cs:38` (design item → house design).
- `item_housing_decorations` — `HousingGameData.cs:209` (decoration design → item, `restore` flag).

**Runtime behavior (what a player actually gets today):**
- **Place a house: full flow.** `HousingManager.Build()` `HousingManager.cs:474` — validates design item, pays tax (certificates via `FeaturesManager.Fsets.Check(Feature.taxItem)` `:494`, or gold `:534`), consumes design `:546`, spawns house, sets `CurrentStep = 0` if build steps exist else `-1` `:578-581`, sends `SCMyHousePacket` `:591`.
- **Build steps: functional.** `House.cs:62` `_allAction` sums `BuildSteps[].NumActions`; `CurrentStep` setter `House.cs:70-132` swaps `ModelId` per step and spawns/deletes bound doodads; `CraftEffect.cs:92-133` handles `WorldInteractionGroup.Building` — wrong skill aborts `:100-105`, `AddBuildAction()` advances, `SCHouseBuildProgressPacket` broadcast `:115-123`, bound doodads spawned at completion `:126-131`.
- **Decorate: functional.** `HousingManager.DecorateHouse()` `:1520` — validates item ownership, creates decoration doodad from `GetDecorationDesignFromId` `:1546`, supports big-fish weights `:1565-1570`, coffers `:1580-1583`, UCC `:1577`, persistent save `:1587`. Removal/recovery path `ReturnHouseItemsToOwner` `:937-991` (respects `restore` flag `:964`). Actability bonuses from furniture `GetActAbilityBonusFromHouse` `:1643-1670`.
- **Taxes/demolition:** `HousingTaxTask` scheduled `:199-200`, `UpdateTaxInfo` `:190`, 22h grace after failed tax `:55`, sell/buy flow `CSSellHousePacket`/`BuyHouse` `:1355`.
- **Misc:** permissions `:602`, rename `:620`, for-sale marker doodad 6760 `:53`, `GetHouseAtLocation` `:1627` (used by doodad placement `CSCreateDoodadPacket.cs:63`, `PutDownBackpackEffect.cs:51`).

### What is NOT wired (2 tables — 3 of 5 housing tables wired by M3a-1/M3a-2)

| Table | Data it carries (1.2 game data) | Feature it would enable |
|---|---|---|
| `housing_areas` | Housing-allowed zone/zone-group definitions | **WIRED (M3a-1, feat/homestead-placement @ e1863625a)** — zone-level placement validation in `HousingManager.Build()`/`ConstructHouseTax` via `HousingPlacementValidator.ValidatePlacement`; land-zone check, faction gate, houseless-only zone types, zone-type category rule, overlap spacing. Client errors `HouseCannotLocateInvalidArea`/`HouseCannotLocateOverlapHouse` |
| `housing_deco_limits` | Deco-limit groups (referenced by `housings.housing_deco_limit_id`, read but unused at `HousingGameData.cs:105`) | Per-house decoration-count categories |
| `housing_deco_limit_elems` | Per-limit decoration allowances (which decoration designs count toward a limit, and how many) | Real deco-limit enforcement; currently `deco_limit`/`absolute_deco_limit` are read into `HousingTemplate` (`HousingGameData.cs:103-104`) but **never checked anywhere** — grep for `DecoLimit` in `HousingManager` returns zero enforcement hits; `:1651` has `// TODO: Implement special decor effect limit` |
| `housing_groups` | House-design groups (client UI grouping of designs) | **WIRED (M3a-1)** — loaded into `HousingLandZoneInfo` (zone-type category rules + houseless-only groups 12/13) |
| `housing_group_categories` | Group categories for above | **WIRED (M3a-1)** — loaded into `HousingLandZoneInfo.AllowedCategories`; a zone whose groups allow no categories rejects everything (1.2 group 11) |

**Corroborating stubs:** `SpecialEffects/ExpandDecoLimit.cs:22` logs-only; `SpecialEffects/RebuildHousing.cs:22` logs-only; `DoodadFuncHousingArea.cs:15` logs-only.

### The 38% gap, concretely
- Player **can** place, build up (multi-step), decorate, set permissions, sell, pay taxes, recover furniture.
- Player **cannot** be stopped from over-decorating (no server-side count check; `DecorateHouse` `:1520-1603` never consults `DecoLimit`), cannot be validated against housing zones, and the deco-limit *data model* (`housing_deco_limits`, `housing_deco_limit_elems`) is dead weight.
- The two `housing_groups*` tables are the only ones with genuinely **no** server-side use — they're client-UI taxonomy.

### Priority
1. **Deco-limit enforcement** (load `housing_deco_limits` + `housing_deco_limit_elems`, count decorations per house in `DecorateHouse`, send the right error — `ErrorMessageType.HousingActabilityDecoLimited = 628` exists at `ErrorMessageType.cs:613`). Visible, finishes an already-90%-working feature. Low effort, medium payoff — with friends, the client's own limit is what most players hit first, so this is mainly anti-exploit.
2. **`housing_areas` zone validation** — only if you want "no building in towns" to be true. For a friends server, sandbox placement is arguably a feature; skip unless authenticity matters.
3. `housing_groups*` — skip; no server runtime.

---

## 2. Auction — 0/3 tables wired (scorecard 0%)

### What IS wired
- `AuctionManager.cs` — full runtime: lots in `ConcurrentDictionary<ulong, AuctionLot>` `:26`; load/save/delete against **MySQL** `auction_house` (`:354` `SELECT * FROM auction_house`, `:407` DELETE, `:464` `REPLACE INTO auction_house`); bid `:130`, buyout `:146`, cancel `:91`, expiry mails `:60-89`, listing fee `:29`; `AuctionIdManager.cs:14` allocates ids against the same MySQL table.
- **Search:** `SearchAuctionLots` `:531` filters by keyword/grade/category; category comparison at `:554-564` compares `AuctionLot` settings vs search bytes.
- **Category ids per item are wired — from the `items` table, not the category tables:** `ItemManager.cs:1030-1032` reads `auction_a_category_id`, `auction_b_category_id`, `auction_c_category_id` straight off each item row, stored as `AuctionSettings` `:1039-1045`. The server therefore matches searches correctly without ever loading the category trees.

### What is NOT wired
`auction_a_categories`, `auction_b_categories`, `auction_c_categories` — the three AH category trees (id/name/parent hierarchy) the client renders in the auction-house UI tabs. The client ships its own copy from game_pak; the server only ever needs the numeric ids (which it has).

### Honest assessment
**Lowest-value gap in this whole report.** Search, post, bid, buyout, cancel, mail settlement all work. Loading the category tables would only add name lookups for logging/WebApi (`Services/WebApi/Controllers/AuctionController.cs` already queries MySQL directly). Do not prioritize.

---

## 3. Specialty trade — 3/4 tables wired (75%)

### What IS wired
`SpecialtyManager.cs` (sqlite, `SQLite.CreateConnection()` `:45`):
- `specialties` `:49` — price matrix per zone-group pair (row_zone_group_id, col_zone_group_id, ratio, profit).
- `specialty_bundle_items` `:71` — per-item×bundle profit/ratio; mapped `itemId → bundleId → item` `:87-90`; items resolved post-load via `OnItemsLoaded` `:129-135`.
- `specialty_npcs` `:97` — npc_id → specialty_bundle_id.

**Runtime:** `GetBasePriceForSpecialty` `:176` (distance check, bundle lookup, `profit * ratio/1000 + refund` `:229`); `SellSpecialty` `:232` (labor 60 `:234`, zone ratio, crafter share 80% `:262`, interest 5% `:264`, specialty-coin payout `:271-281`); `GetRatiosForTargetRoute` `:161`; ratio decay/regen tasks `SpecialtyRatioConsumeTask`/`SpecialtyRatioRegenTask` scheduled `:120-127`. Buy-side pack creation: `DoodadFuncCraftPack.cs:13` is a log-only stub — packs in 1.2 come from the specialty NPC vendor / crafting, not this doodad.

### What is NOT wired
- `specialty_bundles` — the bundle **name** table (`specialty_bundle_items.specialty_bundle_id` and `specialty_npcs.specialty_bundle_id` both reference it; neither the id mapping nor gameplay needs its rows loaded). `SpecialtyNpc.cs` and `SpecialtyBundleItem.cs` don't carry bundle names.

### Honest assessment
**Near-complete domain.** The missing table is a label lookup. Only real gap: pack *creation* path (`DoodadFuncCraftPack` stub) — but that's a code gap, not a data-wiring gap, and in 1.2 packs are purchased from the NPC (merchant goods), which works via the normal vendor system. Nothing to do here.

---

## 4. Items — 32/54 tables wired (59%; scorecard says 58%)

### What IS wired
`ItemManager.cs` loads (all sqlite): `item_configs` `:462`, `item_look_convert_required_items` `:485`, `item_look_convert_holdables` `:505`, `item_look_convert_wearables` `:521`, `item_grades` `:537`, `holdables` `:578`, `wearables` `:624`, `wearable_kinds` `:645`, `wearable_slots` `:671`, `dyeable_items` `:690`, `equip_item_attr_modifiers` `:705`, `item_procs` `:728`, `equip_item_set_bonuses` `:753`, `item_armors` `:778`, `item_weapons` `:814`, `item_accessories` `:844`, `item_summon_mates` `:875`, `item_summon_slaves` `:894`, `item_body_parts` `:913`, `item_enchanting_gems` `:938`, `item_backpacks` `:959`, `items` `:998`, `equip_slot_enchanting_costs` `:1054`, `item_grade_enchanting_supports` `:1074`, `item_socket_chances` `:1105`, `item_cap_scales` `:1122`, `loots` `:1147`, `loot_groups` `:1179`, `item_grade_distributions` `:1208`, `loot_pack_dropping_npcs` `:1239`, `doodad_func_convert_fish_items` `:1268`, `item_spawn_doodads` `:1297`, `unit_modifiers(owner_type='Item')` `:1324`, `armor_grade_buffs` `:1348`, `item_sets` `:1372`, `item_set_items` `:1393`; MySQL `item_containers` `:1745`, `items` `:1767`.
Plus `ItemGameData.cs:29` (`item_grade_buffs`), `ItemConversionGameData.cs` (`item_conv_reagent_filters` `:72`, `item_conv_reagents` `:96`, `item_conv_sets` `:118`, `item_convs` `:133`, `item_conv_products`+`item_conv_ppacks` JOIN `:149`), `Dyeing.cs:36` (`item_dyeings`), `ItemProcTemplate.cs` (model), `NpcManager.cs:464` / `CharacterManager.cs:125` (`item_body_parts` for equip packs).

### What is NOT wired (22 tables) — feature mapping

| Tables | Feature they'd enable | Current state |
|---|---|---|
| `item_sockets`, `item_socket_num_limits`, `item_socket_level_limits` | Gem socketing (slots per item, level/number caps) | **`ItemSocketing.cs` special effect is `// TODO ...`** (log-only). Only `item_socket_chances` is loaded and used for the "chance" side (`ItemManager.cs:193`). Gems can't actually be socketed into gear. |
| `item_proc_bindings` | Bind proc effects to specific items (proc-on-hit weapons/armor) | `item_procs` + `ItemProcTemplate` are loaded and `UnitProcs.cs:24` can trigger procs, but **nothing binds a proc to an item** — no item in the game can have a proc. |
| `item_recipes` | Recipe-book items (impl `Recipe = 12`, `ItemImplEnum.cs:17`) open a craft | Crafting itself is wired via `crafts`/`craft_products`/`craft_materials`/`craft_pack_crafts` (`CraftManager.cs:24,52,80,103`) — but recipe items do nothing special. |
| `item_accept_quests` | Items that auto-accept a quest on use (impl `AcceptQuest = 10`) | No consumer found. |
| `item_tools` | Tool items (impl `Tool = 7`; gathering/farming tool behavior) | No consumer found; `ItemImplEnum.Tool` unused in Core. |
| `item_open_papers` | Open-paper items (impl `OpenPaper = 23`) | Only `DoodadFuncOpenPaper.cs:6` log-stub exists (doodad side); the **item** side unwired. |
| `item_bags` | Bag slot-count definitions (impl `Bag = 4`) | No consumer found. |
| `item_assets` / `item_armor_assets` | Item visual asset references | No consumer found. |
| `item_cap_scale_forbids` | Items forbidden from cap-scaling | `item_cap_scales` is wired and used by `ItemCapScale.cs:59` — only the forbid list is missing. |
| `item_grade_skills` | Grade-triggered skills on items | No consumer found. |
| `item_groups` | Named item groups (used by quest/unit reqs in 1.2) | `UnitReqs.cs:319` has the check **commented out**; `OnItemGroupGather/Use` events exist (`UnitEvents.cs:49-51`) but nothing raises them. |
| `item_look_converts` | Look-conversion main table | Odd one: the three `item_look_convert_*` side tables ARE loaded and `Skinize.cs:55` sends proper errors — but the main `item_look_converts` table itself is never read; conversions likely broken. |
| `item_conv_rpacks`, `item_conv_rpack_members`, `item_conv_ppack_members` | Conversion pack *members* (result packs) | `ItemConversionGameData` loads sets/reagents/products but not members — conversion outcomes may be incomplete. |
| `item_shipyards` | Shipyard-design items (impl `Shipyard = 20`) | Shipyard runtime exists (`ShipyardManager`), but the item→shipyard link table is unread. |
| `item_slave_equipments` | Slave equipment slots (impl `SlaveEquipment = 28`) | No consumer found. |
| `item_secure_exceptions` | Items excluded from bank-security | No consumer found. |
| `item_categories` | Item category taxonomy | No consumer found (items.category_id is read but not resolved to a tree). |

### Honest assessment
The three **socketing tables** + the `ItemSocketing` TODO are the single most valuable missing slice in this domain — endgame gear progression in 1.2 runs on gem sockets, and today the effect is a no-op. Second: `item_proc_bindings` (proc weapons are a core combat layer — currently zero items can proc). Third: `item_recipes`/`item_accept_quests`/`item_open_papers` (item-use depth). `item_groups` matters only if quests reference groups (the check is commented out). The look-convert main table is suspicious but lower play value.

---

## 5. Mates — 0/4 tables wired (scorecard 0%), but runtime exists

### What IS wired
- `MateManager.cs` — **runtime-only, zero sqlite reads** (grep for `CommandText` in it: none). Active-mate registry `:34-57`, mount/passenger checks `:65-76`, state/target/rename `:84-120`.
- `MateGameData.cs` — loads `npc_mount_skills`, `mount_skills`, `mount_attached_skills` (rider-skill mapping for mount abilities).
- `ItemManager.cs:875` — `item_summon_mates` (summon items → npc_id).
- **Mate equipment runtime is fully plumbed:** `Units/Mate.cs:506` creates `MateEquipmentContainer`; `ItemContainer.cs:950-952` instantiates it for `SlotType.EquipmentMate`; `ItemManager.cs:1664` maps the slot type; packets `CSChangeMateEquipmentPacket` (handler moves items between inventory and pet container), `SCMateEquipmentChangedPacket`, `CSRepairPetItemsPacket` all exist. `ItemImplEnum.MateArmor = 30` (`ItemImplEnum.cs:35`) exists.

### What is NOT wired
`mate_equip_pack_groups`, `mate_equip_packs`, `mate_equip_pack_items`, `mate_equip_slot_packs` — the data model that says **which items a given pet/mount may equip** (pack = allowed item set per slot group; pets reference a pack). Without it, the container accepts anything with a valid slot, and the client/server can disagree on legality; there's also no data-driven slot layout per pet.

### Honest assessment
**Medium value.** The container, packets, and repair flow already work — what's missing is *validation data*, not scaffolding. For a friends server: pet armor is a nice-to-have (combat pets become viable), and the fix is a data loader + a legality check in `CSChangeMateEquipmentPacket`, not a system build. Worth doing after socketing/procs.

---

## 6. Models — 1/4 tables wired (scorecard 0%)

### What IS wired
`ModelManager.cs` loads `actor_models` `:87`, `ship_models` `:108`, `vehicle_models` `:146`, `models` `:183` (model_id → sub_type/sub_id dispatch), `game_stances` `:203`. Runtime consumers: `GetActorModel/GetShipModel/GetVehicleModels/IsFlyOrSwim` `:25-65`; stance attachment `:230-235`; physics/collision via `ShipController.cs:449`, `Slave.cs:1095`. The `models` table itself counts as wired (`ModelManager.cs:183`) — scorecard's "0%" counts only the three orphan tables.

### What is NOT wired
- `model_attach_point_strings` — attach-point id → string names (debug/client label data).
- `model_bindings` — per-model attach-point world positions (seats, mount points, doodad attachment sockets).
- `model_quest_cameras` — camera rigs for quest cutscenes (client-side cinematic data).

### Honest assessment
**Lowest impact.** These are mostly client-side presentation/rigging data. The only server-adjacent use would be authoritative seat positions for slaves/mates (currently hardcoded per-attach-point elsewhere), and the game runs fine without them. Skip for a friends server.

---

## Prioritized recommendations (private 1.2 server with friends)

1. **Item socketing** — load `item_sockets`, `item_socket_num_limits`, `item_socket_level_limits`; implement the `ItemSocketing` special effect (currently TODO at `Models/Game/Skills/Effects/SpecialEffects/ItemSocketing.cs:29`). Biggest endgame feature gap; data is all there (gem items load, chance table loads).
2. **Item proc bindings** — load `item_proc_bindings` and attach procs to items at `ItemManager` load; the trigger path (`UnitProcs.cs:24`) already exists. Turns on an entire combat layer (proc weapons/armor) with a small loader + one attach point.
3. **Housing deco limits** — load `housing_deco_limits`/`housing_deco_limit_elems`; enforce counts in `HousingManager.DecorateHouse` (`:1520`) using `template.DecoLimit`/`AbsoluteDecoLimit` (already read, never checked); finish the `:1651` TODO. Completes the housing gap cheaply.
4. **Mate equip packs** — load the four `mate_equip_*` tables; add a legality check in `CSChangeMateEquipmentPacket`. Scaffolding exists; this is a data+validation pass.
5. **Recipe/quest/open-paper item impls** (`item_recipes`, `item_accept_quests`, `item_open_papers`) — item-use depth for crafters and questers; medium effort, each is a small effect handler.
6. **Do NOT touch:** `auction_a/b/c_categories` (search works on ids from `items`), `specialty_bundles` (name table), `model_*` orphans (client-side), `housing_areas` (sandbox placement is fine for friends), `housing_groups*` (no server consumer).

## Evidence index (file:line)

| Claim | Evidence |
|---|---|
| Housing templates read deco limits but never enforce | `HousingGameData.cs:103-105`; `HousingManager.cs:1520-1603` (no limit check); `:1651` TODO |
| House placement lacks zone validation | ~~`HousingManager.cs:477` `// TODO validate house by range...`~~ **RESOLVED M3a-1** — `HousingPlacementValidator.ValidatePlacement` wired into `Build()`/`ConstructHouseTax` (land-zone, faction, houseless-only, category, overlap) |
| Build steps work end-to-end | `House.cs:62,70-132`; `CraftEffect.cs:92-133`; `HousingGameData.cs:149` |
| Decoration placement works | `HousingManager.cs:1520-1603`; `CSDecorateHousePacket.cs:35` |
| Auction runtime is MySQL `auction_house` only | `AuctionManager.cs:354,407,464`; `AuctionIdManager.cs:14` |
| Auction category ids come from `items` rows | `ItemManager.cs:1030-1032,1039-1045`; search compare `AuctionManager.cs:554-564` |
| Specialty 3/4 tables loaded | `SpecialtyManager.cs:49,71,97`; sell flow `:176-281` |
| Specialty pack-creation doodad is a stub | `DoodadFuncCraftPack.cs:13-16` |
| Socketing is TODO | `ItemSocketing.cs:29`; only `item_socket_chances` loaded (`ItemManager.cs:1105,193`) |
| Procs loaded but never bound to items | `ItemManager.cs:728`; `ItemProcTemplate.cs:6`; `UnitProcs.cs:24`; `item_proc_bindings` = 0 refs |
| Mate equip runtime exists, data missing | `Mate.cs:506`; `MateEquipmentContainer.cs:6`; `ItemManager.cs:1664`; `CSChangeMateEquipmentPacket.cs`; `mate_equip_*` = 0 refs |
| ModelManager loads 5 tables, 3 orphaned | `ModelManager.cs:87,108,146,183,203`; `model_attach_point_strings`/`model_bindings`/`model_quest_cameras` = 0 refs |
