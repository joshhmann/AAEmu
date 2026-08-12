# Housing Placement Mechanic — Canonical 1.2 Behavior + Data (HOUSING-01 dossier #1)

**Task:** t_83adf94c (mechanic-research lane, dossier #1; feeds M3a-1 t_69ab0dd9 placement/zone validation + ownership/permissions)
**Branch:** housing-placement-dossier
**Date:** 2026-08-10
**Scope:** evidence only, no code changes. Ground truth: joshhmann fork `develop` @ e24f885bb (HousingManager byte-identical to upstream AAEmu/AAEmu `develop`); canonical 1.2 data surface = `AAEmu.Game/Data/compact.sqlite3` (r208022, 679 tables) + client `game_pak` (24.9 GB, Feb 2023 build, 218,069 entries — same file Recon A/B used; this copy has its first bytes overwritten by a keybindings text, but the FAT is intact and all reads below are verified).

> **STATUS (2026-08-10, D2 closeout t_16dca6c1):** the server-side gap documented below is now **implemented on the fork** — M3a-1 (t_69ab0dd9, `feat/homestead-placement` @ e1863625a) wires zone-level placement validation into `HousingManager.Build()`/`ConstructHouseTax` via `HousingPlacementValidator.ValidatePlacement` (land-zone, faction, houseless-only, category, overlap), and the stale `// TODO validate house by range` marker is removed. The canonical polygon-shape / terrain-band analysis in §2 remains the reference for what a *polygon-precise* implementation would still add on top of the zone-level rules (see §1 note below).

---

## 1. TL;DR

Canonical 1.2 placement is a **two-sided rule set**:

- **Where you may place** is defined by per-zone **polygon shapes** in the client pak (`game/worlds/main_world/level_design/zone/<zoneKey>/client/housing_area.xml`, 62 zones, 380 shapes) — joined to the server-side sqlite table **`housing_areas`** (401 rows) via the level-design entity name stored in `housing_areas.comments`. Each area belongs to a **`housing_groups`** row (15 groups: general residential, house-size tiers, farm-only zones, marine, water-housing, "nothing can be built", homeless-only, 7-day scarecrow, thatched-farmhouse-only).
- **What may be built in an area** is the **`housing_group_categories`** matrix: `(housing_group_id, housings.category_id)` pairs, e.g. group 5 (premium) allows only categories 10/11/12 (medium/large/mansion), group 2 allows only category 16 (straw scarecrow farm), group 11 allows **nothing**. One row carries `max_construct_count` (3 for category 8 "guild hall" in general residential).

The client communicates the three placement failure families via error codes the server is expected to send: **invalid area (112)**, **overlap house (113) / overlap unit (114)**, **terrain too high (115) / too low (116)**, plus 117/118 (connector/neighbor — 2.x-era mechanics), 229 (`no_housing_area`), 340 (`not_dominated_zone`), 341 (near guard house).

**The fork now implements zone-level validation (M3a-1, 2026-08-10); upstream AAEmu develop still does none of this.** `HousingManager.Build()` (`HousingManager.cs:474`) originally carried the TODO trio — `// TODO validate house by range... // TODO remove itemId // TODO minus moneyAmount` — that survived since the 1.2-era original commit (4ead51fb4 "Housing Start" → still present in 2020-era 1f9ee9355). The M3a-1 fork (t_69ab0dd9) replaced the placement TODO with `HousingPlacementValidator.ValidatePlacement` — the server now checks the zone (land zone from `housing_areas`, faction gate, houseless-only zone types, category rule) and overlap spacing on `Build()`/`ConstructHouseTax`; the remaining TODO pair (`remove itemId`/`minus moneyAmount`) is a separate tax/consumption flow. **Still not implemented on the fork:** polygon-precise shape checks from the pak `housing_area.xml` polygons and the terrain bands (`ExtraHeightAbove/Below`, `AutoZ` beyond overlap); `housing_areas`/`housing_groups`/`housing_group_categories` are loaded and used, but only zone-level. Upstream issue #41 ("Player Housing", 2019) lists "Collision map (when placing a house, find a way to make sure no other house is overlapping)" as an open checkbox — still unimplemented upstream.

---

## 2. Canonical placement decision — data model

### 2.1 The three data layers

| Layer | Source | What it defines |
|---|---|---|
| Placement polygons | pak `level_design/zone/<zoneKey>/client/housing_area.xml` | *Where* a plot may exist (per world-zone polygon shapes, `EntityClass="AreaShape"`, `Layer="Main"`) |
| Zoning rules | sqlite `housing_areas` → `housing_groups` → `housing_group_categories` | *What* may be built in each polygon (house size tier / farm type / nothing), ownership restrictions |
| Building footprint params | sqlite `housings` | Per-template geometry: `garden_radius` (plot radius), `alley` (spacing), `extra_height_above/below` (terrain band), `auto_z` + offsets, `always_public`, `heavy_tax` |

The join key between layer 1 and layer 2 is the **entity name**: `housing_areas.comments` holds the exact `LevelDesignShape_<zoneKey>_<name>_<n>` string that appears as `<Entity Name="...">` in the pak XML (verified: 375 of 401 `housing_areas` rows join to a shape in the matching zone; the 26 non-joiners are deleted shapes (`삭제`), a zone-name typo (`245longsnad`), and legacy/renamed zones).

### 2.2 `housing_area.xml` — placement polygons (pak census)

- Files: **111** `housing_area.xml` under `level_design/zone/<id>/client/` (90 zones; some zones also have `cn/` and `na/` locale variants, e.g. zone 283). **62 zones** carry real content (>12 bytes) = **380 `AreaShape` entities** total.
- Folder ids are **world.xml zone ids** (= sqlite `zones.zone_key`, NOT `zones.id`; e.g. `w_solzreed_1` is `zones.id=9`, `zone_key=142`, folder `zone/142/`).
- Format per shape:
  ```xml
  <Entity Name="LevelDesignShape_142_anne_2" Pos="423.32227,788.87012,95.75293" EntityClass="AreaShape" EntityId="11743" Layer="Main" cellX="1" cellY="0">
    <Area Id="0" Group="1" Proximity="0" Priority="0" value1="393" value2="0" flags="0" Height="0">
      <Points><Point Pos="0,0,0"/> ... </Points>
    </Area>
  </Entity>
  ```
  `Pos` is the world position; `Points` are local offsets → polygon. `value1` is a per-zone sequential shape index (no semantic meaning: 0..402, unique within a zone); `Group="1"` constant.
- Non-housing neighbours in the same folders: `subzone_area.xml` (113 files, zone subdivision polygons — used by `SubZoneManager` for farm placement), `common_farm.xml` (8 files — public farms), `race_track.xml`, `transfer_path.xml`.
- The 12-byte files (`<Objects />`) are instance/empty zones.

Zone coverage: housing shapes exist in both factions' continents and the sea zones (e.g. 129 w_gweonid_forest_1, 133 w_marianople_1, 138–143 Nuia/Haranya mainland, 150 s_silent_sea_1, 195 s_freedom_island, 197 o_shining_shore_1). **Solzreed (the curated M2/M3 reference region):** zone 142 (w_solzreed_1) = 17 shapes, 178 (w_solzreed_2) = 1, 179 (w_solzreed_3) = 8 → 26 polygons, matched by 27 `housing_areas` rows (see 2.3).

### 2.3 `housing_areas` + `housing_groups` — the zoning rules (canonical 1.2)

`housing_areas` (401 rows): `id, name (zone name), housing_group_id, comments (LevelDesignShape entity name)`.

`housing_groups` (15 rows) — the canonical area types, with the Korean design text that IS the 1.2 rule:

| group | name | desc (KR, 1.2 data) | doodad_id | houseless | allowed_tax_delay_week | can_extend |
|---|---|---|---|---|---|---|
| 1 | 일반 주거 지역 (general residential) | 모든 주택 및 텃밭을 건설할 수 있습니다 (all houses + farms) | 6225 | f | 1 | t |
| 2 | 밀짚모자 허수아비 텃밭 (straw scarecrow farm) | scarecrow-farm only | — | f | 0 | t |
| 3 | 해양 주거 지역 (marine) | 양식장 (aquaculture) only | 6226 | f | 0 | t |
| 4 | 일반 주택 지역 (general house) | small + medium houses | — | f | 0 | t |
| 5 | 고급 주택 지역 (premium house) | medium, large, mansion | 6227 | f | 0 | t |
| 6 | 농업 지역 (agricultural) | farms + thatched house | — | f | 0 | t |
| 7 | 수상 주거 지역 (water housing) | water houses | 6985 | f | — | t |
| 8 | 최고급 주택 지역 (top-tier) | large + mansion | — | f | 0 | t |
| 9 | 호박머리 허수아비 텃밭 (pumpkin scarecrow) | pumpkin-farm only, server-wide 1 per character | 6228 | f | 1 | t |
| 10 | 초가지붕 농장 지역 (thatched farmhouse) | thatched farmhouse only, server-wide 1 per character | 6229 | f | 1 | t |
| 11 | 아무것도 지을 수 없는 터 (nothing) | nothing may be built | — | f | 1 | t |
| 12 | 무주택자 전용_테스트 (homeless-only, test) | placeable only if character owns no building (server-wide) | — | t | 1 | t |
| 13 | 밀짚모자 허수아비 텃밭 지역 (scarecrow zone) | scarecrow only, owner must own no building, **auto-demolished after 7 days** | 6984 | t | 0 | f |
| 14 | 작은 주택 지역 (small-house zone) | small houses + straw & pumpkin scarecrows | — | f | 1 | t |
| 15 | 중형 주택 지역 (medium-house zone) | medium houses + thatched farmhouse + pumpkin scarecrow | — | f | 1 | t |

Notes: `doodad_id` = zone marker doodads (6225–6229, 6984, 6985; present in `doodads`). `houseless` = placement gated on owning no building server-wide (groups 12, 13). `can_extend` = whether the plot can later be extended (the 2.x "expand" mechanic; group 13 cannot). `allowed_tax_delay_week` = weeks of tax delay permitted (group 13 = 0 → the 7-day auto-demolish).

### 2.4 `housing_group_categories` — the buildable-type matrix

33 rows: `(housing_group_id, category_id, max_construct_count)`. Decoded (category names from `housings`):

- group 1 (general): 1, 9, 10, 11, 12, 16, 17, 18 + **8 with max 3** (guild hall limited to 3 per area)
- group 2 (scarecrow): 16 · group 3 (marine): 7 · group 4 (general house): 1, 10 · group 5 (premium): 10, 11, 12 · group 6 (agricultural): 16, 17, 18 · group 7 (water): 15 · group 8 (top-tier): 11, 12 · group 9 (pumpkin): 17 · group 10 (thatched): 18 · group 12 (homeless): 1 · group 13 (7-day scarecrow): 16 · group 14 (small-house): 1, 17, 16 · group 15 (medium-house): 18, 10, 17

House category tiers (`housings.category_id`, 269 templates): 1 = houses (incl. test/legacy, 86), 2 = castle towers, 3 = castle walls, 4 = gates/stairs, 5 = stronghold, 6 = foundation, 7 = 양식장 aquaculture, 8 = guild hall, 9 = crafting workbenches, 10 = medium (세련된/refined), 11 = large (화려한/splendid), 12 = mansion, 14 = castle gates, 15 = water houses, 16 = straw scarecrow farm, 17 = pumpkin scarecrow farms, 18 = thatched farmhouse.

**Solzreed breakdown** (27 areas): group 15 medium-house ×16, group 9 pumpkin ×4, group 1 general ×2, group 12 homeless ×1, group 13 7-day scarecrow ×1, group 14 small-house ×1.

### 2.5 `housings` template fields that encode the canonical placement geometry

All loaded by `HousingGameData.Load` (`GameData/HousingGameData.cs:72–144`) but **unused by any game logic** (grep: only loader + model references):

- `garden_radius` — plot footprint radius (e.g. 9.5 for Nuian medium houses `housings` id 140–148; 0 for towers). This is the canonical overlap/spacing unit.
- `alley` — inter-plot spacing (0.0 across the 1.2 data).
- `extra_height_above` / `extra_height_below` — terrain height band for placement (mostly 0 / 10.0 in 1.2 data; e.g. towers have `extra_height_below=10`).
- `auto_z` + `auto_z_offset_x/y/z` — auto ground-snapping on placement (true for most houses).
- `always_public` (e.g. archeum lodestones id 139/184–192 area), `heavy_tax` (tax class), `is_sellable`, `demolish_refund_item_id`, `deco_limit`/`absolute_deco_limit`.

### 2.6 How the original 1.2 server decided (reconstruction)

No original-server code is available; the canonical decision procedure is reconstructed from the data model above + the client's error-string table (see §5):

1. **Area check** — target (x,y) must fall inside a housing-area polygon (pak shapes); else error 112 `house_cannot_locate_invalid_area` (player-visible 1.2 string: "Building doesn't fit in this area" — confirmed in period play reports, e.g. reddit.com/r/archeage 2015-02-14 "Housing bug? Can't place land on open land").
2. **Zone group check** — the area's `housing_groups` + the design's `housings.category_id` must match a `housing_group_categories` row (incl. `max_construct_count` per area, `houseless` server-wide ownership gate, `can_extend`); failure → 112 family.
3. **Overlap check** — no other house's `garden_radius` circle may intersect the new plot (error 113 `house_cannot_locate_overlap_house`); NPC/unit overlap → 114 `house_cannot_locate_overlap_unit`.
4. **Terrain check** — ground height within the template's `extra_height_above/below` band relative to placement Z (errors 115/116); water placement restricted to water-housing areas (group 7).
5. **Ownership/faction checks** — 340 `house_cannot_locate_not_dominated_zone` (dominion/siege land), 341 (guard-house proximity).
6. **Finance** — deposit (base tax ×2) + first week's tax, paid in tax certificates or gold (error 120 `house_cannot_create_lack_money`); then consume the design item (via `item_housings` design→item mapping) and create the house (error 119 `house_cannot_create` on failure).

The client enforces the same rules visually (green/red preview from the same pak shapes); the server must re-validate because the client is not trusted.

### 2.7 Related placement data (farms — same zone machinery)

- Public farms: `common_farm.xml` (8 zones) + sqlite `common_farms`; server-side placement gate in `PublicFarmManager` via **subzones**: `InPublicFarm`/`GetFarmType` use `SubZoneManager.GetSubZoneByPosition` (point-in-polygon over `subzone_area.xml`, `SubZoneManager.cs:325–359`) against hardcoded farm subzone ids 998/966/967/968/974 (`PublicFarmManager.cs:145–156`); `CanPlace` enforces per-type max counts and allowed doodad lists (`PublicFarmManager.cs:91–111`).
- Private farms (scarecrows) on owned land are gated by `CSCreateDoodadPacket`: `HousingManager.GetHouseAtLocation(x,y)` + `House.AllowedToInteract` (`CSCreateDoodadPacket.cs:63–73`) — the only consumer of `GetHouseAtLocation`.

---

## 3. Ownership model

**Claim flow** — `CSCreateHousePacket` (0x057) → `HousingManager.Build` (`HousingManager.cs:474–594`):
1. Verify the design item exists and is owned (`Build` 481–487; error `BagInvalidItem`).
2. Compute tax (`CalculateBuildingTaxInfo` 726–769: base tax × multiplier; heavy-tax count capped at 10; multiplier = heavyCount×0.5 above 2 heavy properties; deposit = base×2).
3. Pay — tax certificates (bound first, then unbound; `taxItem` feature) or gold (534–544; error `MailNotEnoughMoneyToPayTaxes`).
4. Consume 1 design item (546–550).
5. Create `House` (`Create` 99–124): new `Id` (HousingIdManager), `TlId` (HousingTldManager), faction = player faction, `Permission` forced `Public` if template `AlwaysPublic`.
6. Bind ownership: `OwnerId = CoOwnerId = char.Id`, `AccountId = account`, `Permission = Private` (582–586), `PlaceDate = now`, `ProtectionEndDate = now + DaysForTaxPayment` (config `World.json:16` = 7 days; 588).
7. Insert into `_houses`/`_housesTl`, send `SCMyHousePacket` (0xc1), `house.Spawn()` (591–593; broadcasts `SCUnitStatePacket` + `SCHouseStatePacket` 0xbc to nearby — `House.AddVisibleObject`, `House.cs:239–255`).
8. Rotation quantization: 1.2 protocol sends zRot as float but `SCUnitStatePacket` encodes it in 1 byte → server snaps to one of 256 rotations (`Build` 566–576 — documented 1.2 quirk; positions of bound doodads rely on server/client rotation agreement).

**Persistence fields** — MySQL `housings` (`SQL/aaemu_game.sql:286–309`, write in `House.Save` `House.cs:281–326`): `id, account_id, owner, co_owner, template_id, name, x, y, z, yaw, pitch, roll, current_step, current_action, permission, place_date, protected_until, faction_id, sell_to, sell_price, allow_recover`. Seed rows: 12 "Archeum Lodestone" (id 1–12, template 139/184–192/271/272, permission 2=public, protected until 2043).

**Logout** — `SaveManager` persists all dirty houses (`SaveManager.cs:82` `housingManager.Save(connection, transaction)` → `REPLACE INTO`), including via `House.IsDirty` flags on every mutating property (`House.cs:46–146`). Load on boot: `LoadPlayerHousing` (`HousingManager.cs:130–203`) reads `SELECT * FROM housings`, restores transform/zone, rebuilds `_houses`/`_housesTl`, kicks the `HousingTaxTask` (30 s initial, 10 s period), and backfills 14 days of protection for rows where `place_date == protected_until` (187–188). `SpawnAll` re-spawns at startup (242–250). **Placement state survives logout/restart only for placed (and per-step) progress; there is no pre-placement claim/queue state in 1.2 data** (the 1.2 "landrush/claim" flows postdate 1.2).

**Deed / demolish** — `Demolish` (`HousingManager.cs:640–693`): owner-only (else `InvalidHouseInfo`); marks `ProtectionEndDate` expired, returns furniture/design by mail (`ReturnHouseItemsToOwner` 881+, design returned with TODO "proper grades for design"), zeroes owner/co-owner/account/sell fields, sets `Permission = Public`, broadcasts `SCHouseDemolishedPacket` (0xc0) + `SCMyHouseRemovedPacket` (0xc2) to owner, converts house to `FactionsEnum.Monstrosity` ("killable"), queues `_removedHousings` → DELETE on next save. Note the fork deliberately **disables** the unpaid-tax demolish block (commented out 650–658, ZeromusXYZ note). Dead houses removed by `RemoveDeadHouse` (699–712). Sell: `CSSellHousePacket`/`CSBuyHousePacket` (0x05e/0x060) → `SetForSale`/`BuyHouse` with `SCHouseSetForSalePacket` 0xc8 / `SCHouseSoldPacket` 0xca.

---

## 4. Permissions model

Enum (`Models/Game/Housing/HousingPermission.cs`): `Private=0, Guild=1, Public=2, Family=3`.

- **Change**: `CSChangeHousePermissionPacket` (0x05a) → `HousingManager.ChangeHousePermission` (602–612): owner-only, sets `house.Permission`, broadcasts `SCHousePermissionChangedPacket` (0xbe). (Historic 2020 code additionally mapped Guild/Family into `CoOwnerId`; current fork keeps a single `Permission` byte.)
- **Access gate**: `House.AllowedToInteract` (`House.cs:374–395`) — used for door/chest/doodad interaction (and planting on the plot via `CSCreateDoodadPacket`):
  - `AlwaysPublic` templates or unfinished (`CurrentStep != -1`) → everyone.
  - `Private` → owner, or same **account** (via `NameManager.GetCharacterAccount(OwnerId)`).
  - `Family` → members of the owner's family.
  - `Guild` → members of the owner's expedition.
  - `Public` → everyone.
- **Data side**: `always_public` per template; `permission` per house row. There is **no party/guest list in 1.2 housing data** — the canonical 1.2 access model is exactly the 4-value enum above (guests get access via `Public` or via being family/guild). Chest-level perms are separate: `doodad_func_coffer_perms` (coffer permission table, loaded by DoodadManager — scope of PROPERTY-01, not this dossier).

---

## 5. Wire surface

Placement request (C2G, level 1):
- **`CSCreateHousePacket` 0x057** (`Core/Packets/C2G/CSCreateHousePacket.cs`): `designId u32, x i64 (Helpers.ConvertLongX), y i64 (ConvertLongY), z f32, zRot f32, itemId u64, moneyAmount i32, ht i32, autoUseAaPoint bool` → `HousingManager.Build`.
- **`CSConstructHouseTaxPacket` 0x056** (tax preview while placing): `designId u32, x i64, y i64, z f32` → `ConstructHouseTax` (`HousingManager.cs:393–414`; note `// TODO validation position and some range...`).
- Related: `CSChangeHousePermissionPacket` 0x05a, `CSChangeHouseNamePacket` 0x059, `CSRequestHouseTaxPacket` 0x05c, `CSSellHousePacket` 0x05e, `CSSellHouseCancelPacket` 0x05f, `CSBuyHousePacket` 0x060, `CSDecorateHousePacket` 0x058, `CSAllowHousingRecoverPacket`, `CSChangeHousePayPacket` 0xfff (unmapped TODO).

Server responses (G2C): `SCMyHousePacket` 0xc1 (per-char house list on create/login), `SCHouseStatePacket` 0xbc (state broadcast), `SCLoginCharInfoHousePacket` 0x57 (house list at char select — `SCLoginCharInfoHouse.cs`), `SCConstructHouseTaxPacket` 0xc5 (tax preview: designId, heavyCount, normalCount, isHeavy, base, deposit, total — `SCConstructHouseTaxPacket.cs`), `SCHouseBuildProgressPacket` 0xbd, `SCHousePermissionChangedPacket` 0xbe, `SCHouseDemolishedPacket` 0xc0, `SCMyHouseRemovedPacket` 0xc2, `SCHouseFarmPacket` 0xc3, `SCHouseTaxInfoPacket` 0xc4, `SCHouseSetForSalePacket` 0xc8 / `Reset` 0xc9 / `Sold` 0xca / `OwnerNameChanged` 0xcb. House wire layout: `House.Write` (`House.cs:328–366`): tlId, dbId, objId(Bc), templateId, modelId, coOwner, owner, ownerName, accountId, permission byte, build progress (allStep/curStep), tax, x/y (longs), z, name, allowRecover, sellPrice, sellToId, sellToName.

**Error codes the 1.2 client expects** (`Models/Game/ErrorMessageType.cs`, key = client string):
- 112 `house_cannot_locate_invalid_area` · 113 `house_cannot_locate_overlap_house` · 114 `house_cannot_locate_overlap_unit` · 115 `house_cannot_locate_terrain_too_high` · 116 `house_cannot_locate_terrain_too_low` · 117 `house_cannot_locate_connector_missed` · 118 `house_cannot_locate_not_connected_neighbor` · 119 `house_cannot_create` · 120 `house_cannot_create_lack_money` · 121 `house_cannot_spawn` · 122–127 decoration errors · 128 `house_cannot_change_permission` · 229 `no_housing_area` · 340 `house_cannot_locate_not_dominated_zone` · 341 `house_cannot_locate_near_others_guard_house`.
- Currently the fork only ever sends `BagInvalidItem` / `MailNotEnoughMoneyToPayTaxes` / `InvalidHouseInfo` / `NoPerm` on the placement path — **112–118/229/340/341 are never sent** (grep confirms no usages).

---

## 6. Engine gaps — fork vs upstream

| # | Gap | Fork evidence | Upstream status |
|---|---|---|---|
| 1 | **No zone/area check** | `Build` TODO (`HousingManager.cs:477` "validate house by range"); `housing_areas`, `housing_groups`, `housing_group_categories` never queried (grep: no references in AAEmu.Game besides commented-out load stubs `HousingManager.cs:138–139`, `HousingGameData.cs:31–32`); `housing_area.xml` never read (no loader; compare `SubZoneManager` which DOES load `subzone_area.xml` via `ClientFileManager`, `SubZoneManager.cs:50`) | identical (file diff = 0 lines vs upstream develop 2026-08-10) |
| 2 | **No overlap check on build** | `GetHouseAtLocation` exists (`HousingManager.cs:1627–1641`, square `GardenRadius` bounds, TODO "Check if all houses actually use a square shape aligned to grid" + "Add world and/or instance checks") but is only called from `CSCreateDoodadPacket` (planting), never from `Build` | upstream issue #41 (2019): "Collision map (when placing a house…) no other house is overlapping" — checkbox never checked; issue closed without it |
| 3 | **No terrain/height check** | `Build` trusts client `posZ` (565); `AutoZ`, `Alley`, `ExtraHeightAbove/Below` loaded but unreferenced in game logic (grep: only `HousingTemplate.cs` + `HousingGameData.cs`) | same |
| 4 | **No spacing / rotation-space handling** | no `alley` use; rotation quantized to 256 steps by design (566–576) | same |
| 5 | **No unit/NPC overlap check** | no SpawnManager query in `Build` (grep: only bound-doodad add/despawn lines 296/711) | same |
| 6 | **No race protection** | `Build` mutates shared `_houses`/`_housesTl` without a lock; `GameProtocolHandler.OnReceive` decodes synchronously per connection (`GameProtocolHandler.cs:93–199`) with no cross-connection serialization → two simultaneous placements of the same plot both succeed | same |
| 7 | **DoodadFuncHousingArea is a stub** | `Models/Game/DoodadObj/Funcs/DoodadFuncHousingArea.cs:13–17` — empty `Use` (faction_id/radius from `doodad_func_housing_areas`, 2 rows in data) | same |
| 8 | **Bound doodad persistence** | fork-only: `UsePersistentHouseDoodads` config (`World.json:26`, default false) + `HousingManager.ReconcileBoundDoodads` (256–317) — upstream merged as PR #1416 | upstream: merged PR #1416 (persist housing bound doodads), PR #1476 (release coffer on owner logout) |
| 9 | **Build steps / construction** | present: `CurrentStep`/`NumAction` + `SCHouseBuildProgressPacket`, build actions via `housing_build_steps` skill consumption (`HousingManager.AddBuildAction` `House.cs:172–194`; `CSChangeHousePayPacket` unmapped) | same |

Verdict: **the fork's housing engine is byte-identical to upstream develop on every placement-relevant path.** Everything canonical that must be built for M3a-1 (zone containment from pak polygons + sqlite zoning, overlap, terrain, race) is *new* engine work — there is no hidden upstream implementation to port.

---

## 7. Edge cases

1. **Overlap with NPC spawns** — nothing prevents it: `Build` never consults `SpawnManager`/`NpcManager` (verified by grep), and the canonical `house_cannot_locate_overlap_unit` (114) is never sent. Data risk is real: housing polygons (e.g. Solzreed `anne`/`moang` shapes) sit inside zones that also carry NPC spawns (spawns table, 25,118 rows for main_world per Recon B census); the original server presumably excluded unit-occupied cells via 114.
2. **Water / steep terrain** — no server-side check at all: client Z is stored verbatim (568–576 use it only for rotation math), `auto_z` never applied, no water test (the `HeightMaps`/`GeoData` height query used elsewhere — `WorldManager.GetHeight` 825+ — is never called on the Build path). Canonical codes 115/116 exist precisely for this; water placement should additionally be restricted to group 7 areas (category 15).
3. **Two players placing simultaneously (race)** — both succeed: `Build` is unlocked, checks nothing, and `_houses.Add` (589) is a plain dictionary insert; even after adding validation, the check-then-insert must be atomic (lock or single-threaded marshalling, cf. the fork's serialized-marshal contract for PlayerBot steps) or the pair of players can double-claim the same plot.
4. **House-on-house** — currently allowed (no check); `GetHouseAtLocation`'s square-vs-circle approximation (1634–1637) is also wrong for rotated plots — the canonical check should use `garden_radius` circles + `alley` spacing, per §2.5.
5. **Building on public farms / subzones** — no interaction: `Build` doesn't consult `PublicFarmManager.InPublicFarm` or `SubZoneManager`; a house could be placed inside a public farm polygon.
6. **Under-construction houses** — `CurrentStep != -1` houses occupy their plot the same as finished ones (no distinction in any check), and bound doodads are only spawned at `CurrentStep == -1` (`House.cs:78–108`).
7. **Instance worlds** — `GetHouseAtLocation` carries a TODO for world/instance checks (1630); `Build` pins the house to `connection.ActiveChar.ParentWorld` (553) so cross-world safety is inherent there, but any future zone lookup must key on world+zone.
8. **Faction/dominion land** — canonical codes 340/341 exist; 1.2 data has no dominion/guard-house placement tables wired (siege zones: `siege_zones`, `conflict_zones` exist in sqlite but nothing reads them on the Build path).

---

## 8. Implications for M3a-1 (t_69ab0dd9) — design notes only

The implementation must add, in order of canonicality:

1. **Load** `housing_areas` + `housing_groups` + `housing_group_categories` (sqlite) and the pak `housing_area.xml` polygons (loader pattern: `SubZoneManager.Load` + `Point.IsInside`), keyed by world.xml zone id (= `zones.zone_key`).
2. **Validate in `Build` before any item/tax consumption**: point-in-polygon containment → `housing_group_categories` membership for the design's `category_id` (via `item_housings`→`housings`) → `max_construct_count` → `houseless` gate → overlap vs `_houses` using `garden_radius` (+`alley`) → terrain band `extra_height_above/below` vs `WorldManager.GetHeight` → faction/dominion (later).
3. **Send the canonical error ids** (112/113/114/115/116/229/340/341) via `SendErrorMessage` on each failure, keeping the client's exact strings.
4. **Serialize the check-then-create** (lock around validation + `_houses.Add`) to close the race.
5. **Respect 1.2 rotation quantization** (already done) and keep Z as client-sent for now unless terrain checks land.

---

## 9. Evidence appendix

**Data (compact.sqlite3 r208022):** `housing_areas` (401 rows; schema + samples), `housing_groups` (15 rows; KR desc quoted), `housing_group_categories` (33 rows), `housings` (269 rows; `garden_radius` 9.5 for ids 140–148, `extra_height_below` 10, `auto_z` t; category distribution 1..18), `item_housings` (181), `housing_binding_doodads` (717), `doodad_func_housing_areas` (2), `zones` (218; `zone_key` = pak folder id space), `zone_groups` (faction_chat_region_id 2/3 = Nuia/Haranya), `sub_zones`, `common_farms`.

**Pak (game_pak, Feb 2023 build, 218,069 entries):** `level_design/zone/<zoneKey>/client/housing_area.xml` — 111 files / 90 zones / 62 non-empty / 380 AreaShapes (census via `housing_dossier_pak.py`, read-only AAPak TypeA FAT lister, key from `AAEmu.Commons/Utils/AAPak/AAPak.cs:41`; this copy's leading bytes are overwritten — FAT read from end-of-file, verified entries + world.xml). Zone 142 (w_solzreed_1) sample shapes `LevelDesignShape_142_anne_2` etc.; `common_farm.xml` ×8; `subzone_area.xml` ×113.

**Code paths (fork develop @ e24f885bb):** `Core/Managers/HousingManager.cs:474–594` (Build), `:393–414` (ConstructHouseTax), `:602–612` (ChangeHousePermission), `:640–693` (Demolish), `:1627–1641` (GetHouseAtLocation), `:130–203` (LoadPlayerHousing), `:256–317` (ReconcileBoundDoodads); `GameData/HousingGameData.cs:24–227` (loads; areas commented out); `Models/Game/Housing/House.cs:281–326` (Save), `:328–366` (wire Write), `:374–395` (AllowedToInteract), `:70–132` (CurrentStep/build); `Models/Game/Housing/HousingPermission.cs`; `Core/Packets/C2G/CSCreateHousePacket.cs`, `CSConstructHouseTaxPacket.cs`, `CSBuyHousePacket.cs`, `CSChangeHousePermissionPacket.cs`; `Core/Packets/G2C/SCMyHousePacket.cs`, `SCHouseStatePacket.cs`, `SCLoginCharInfoHouse.cs`, `SCConstructHouseTaxPacket.cs`; `Core/Packets/C2G/CSOffsets.cs:84–95`, `Core/Packets/G2C/SCOffsets.cs:91–200`; `Models/Game/ErrorMessageType.cs:119–128, 233, 344–345`; `SQL/aaemu_game.sql:286–325`; `Core/Managers/SaveManager.cs:82`; `Core/Managers/PublicFarmManager.cs:64–156`; `Core/Managers/SubZoneManager.cs:325–359`; `Core/Network/Game/GameProtocolHandler.cs:93–199`; `Models/Game/DoodadObj/Funcs/DoodadFuncHousingArea.cs:13–17`; `Core/Packets/C2G/CSCreateDoodadPacket.cs:44–89`; `Configurations/World.json:16,26`.

**History:** original 1.2-era commits `4ead51fb4` ("Housing Start", loads `housing_areas` by same 1.2 schema), `c342ead33`, `1f9ee9355` (2020, Build still TODO-only validation). Upstream: `HousingManager.cs` develop == fork (diff 0); issue #41 "Player Housing" (collision map unchecked, closed 2019); PR #1416 bound-doodad persistence (fork-merged); PR #1476 coffer release.

**Period behavior:** reddit.com/r/archeage 2015-02-14 ("Building doesn't fit in this area" on 1.2-era live); Wikipedia ArcheAge (free placement in designated non-instanced zones; tax upkeep).

*Dossier only — no code changed on this branch.*
