# Trade Packs (Specialty Packs) Mechanic — Canonical 1.2 Behavior + Data (PACK-01 dossier, feeds M4-2)

**Task:** t_29b470ff (mechanic-research lane, dossier; feeds M4-2 t_449d0c41 trade-pack implementation)
**Branch:** trade-packs-dossier
**Date:** 2026-08-11
**Scope:** evidence only, no code changes. Ground truth: joshhmann fork `develop` @ 4ded92c61; canonical 1.2 data surface = `AAEmu.Game/Data/compact.sqlite3` (r208022, 679 tables) + client `game_pak` (24.9 GB, Feb 11 2023 build, 218,069 entries — same file the housing dossier used; FAT read verified). `SpecialtyManager.cs` on the fork is byte-identical to upstream AAEmu/AAEmu `develop` (git diff empty) — every gap listed here applies to upstream too unless noted.

**Citation rule (Josh mandate 2026-08-11):** every claim is tagged **[D]** = data-verified (sqlite/pak rows or fork code read this run), **[W]** = wiki/forum research (source + access date), **[R]** = reconstructed inference (flagged as such). Discrepancies between data and research are called out explicitly.

---

## 1. TL;DR

The canonical 1.2 trade-pack (특산품, "specialty pack") loop is **craft → carry → place → pick up → sell → mail payout**:

- **Create:** at a zone's specialty crafting table (특산품 제작대, doodads 4220–4246/6078/7701) via `craft_packs` recipes — e.g. craft 5403 "황금 평원 마취제": 10 s cast, skill 16766 ("장사: 특산품 제작과 포장", **60 labor**, actability 31 = Commerce), materials = 특산품 품질 인증서 ×1 (5000 g) + zone produce (색이 좋은 기장 ×3, 양귀비 ×10). **[D]** Level 10+ required per the item tooltip ("10레벨 미만은 특산품 제작/판매 불가") **[D]** — **not enforced anywhere in the fork** **[D]**.
- **Backpack occupancy:** the finished pack is auto-equipped into the dedicated **Backpack equipment slot (26)** — not the bag. One pack at a time; a glider in that slot is taken off first (and restored when the pack is placed). `item_backpacks.backpack_type_id` 3 = TradePack; `items.impl_id` 22 = Backpack. **[D]**
- **Place:** the pack's use skill (e.g. 20412) fires `PutDownBackpackEffect` → pack spawns as a **persistent doodad** 1 m in front of the player, facing north; gated on public-farm exclusion, house-interaction permission (when inside a house plot), inventory-full. **[D]** Placed packs **despawn after 6 days** (tooltip **[D]**, wiki **[W]**) — **no server-side timer exists** **[D]**. Picking up resets the timer **[W]**.
- **Pick up:** `DoodadFuncRecoverItem` on the placed-pack doodad — anyone can pick up (no ownership gate in code); the item returns to the backpack slot; anti-dupe via system-container check. **[D]**
- **Ownership:** crafter recorded on the item (`made_unit_id`); pack binds on pickup (`bind_id` 2); placed doodad records `owner_id` (character) and, on house property, `owner_type=Housing` + house DbId. **[D]**
- **Sell:** `CSSellBackpackGoodsPacket` (0x042) → `SpecialtyManager.SellSpecialty` — 60 labor (Commerce), NPC within 2.5 m, pack must be in that NPC's bundle (else error), 5% interest on top, then **mail payout after 22 h** (tooltip **[D]**; fork config = **480 min = 8 h — mismatch** **[D]**). **80% to the seller / 20% to the crafter** when producer ≠ seller (tooltip **[D]**, Fandom **[W]**, fork: hardcoded 0.80 split gated behind the `backpackProfitShare` feature **[D]**).
- **Reward types:** same-continent gold traders pay **gold**; "item" traders pay **stabilizer items** (안정된 흑탄 가루 etc., npc `specialty_coin_id`); cross-continent traders pay **Delphinad Stars** (델피나드의 별, item 23633); Freedich (자유도) accepts both continents. **[D]**
- **Price:** per-pack base = `floor(profit × ratio/1000) + item.refund`; multiplied by a **dynamic route ratio 70–130%** (per pack × destination zone group; decays −0.5 per pack sold per 1-min tick, regens +5 per 60-min tick). **[D]** The static per-route matrix table `specialties` (2162 rows) is **loaded but never used** by the engine **[D]**.

**Engine state:** the sale path works end-to-end in the fork (sell → mail → gold/coins → 80/20), but: placed-pack 6-day despawn **absent**, 22 h mail delay **misconfigured to 8 h**, level-10 gate **absent**, ratio state **not persisted** (resets to 130% on restart), seller-share/interest **hardcoded**, `specialties` matrix and `vendor_exist` **unused** (loader even reads the wrong column), two specialty UI packets **unregistered**.

---

## 2. Canonical data model

### 2.1 The four data layers

| Layer | Source | What it defines |
|---|---|---|
| Pack item | `items` (`impl_id`=22) + `item_backpacks` | The pack as an item: origin zone group (`specialty_zone_id`), craft-cost refund (`price`/`refund`), put-down skill (`use_skill_id`), visuals (`asset_id`), weight class (`heavy`) |
| Place-down | `put_down_backpack_effects` + `effects` + `doodad_almighties` | Skill effect → placed-pack doodad template (per pack type) |
| Trade routes | `specialty_bundle_items` + `specialty_npcs` + `specialty_bundles` | (pack × destination trader) profit/ratio pairs; which NPC belongs to which bundle |
| Route matrix | `specialties` (row/col zone-group × ratio/profit/vendor_exist) | Static origin→destination matrix; **loaded by the fork, never used** |

### 2.2 Pack items (`items` + `item_backpacks`)

- 437 `items` rows have `impl_id`=22 (`ItemImplEnum.Backpack`); **255 have `specialty_zone_id` > 0** = the actual trade packs (rest are quest/cargo/glider backpacks, e.g. 23589 비상식량 등짐 = emergency rations, `specialty_zone_id`=0). **[D]**
- Pack fields (verified on 26488 황금 평원 마취제 "Golden Plains Anesthetic"): `max_stack_size`=1, `bind_id`=2 (bind on pickup), `price`=`refund`=20000 (some packs 50000/10000 — the refund is the craft-cost rebate added to the sale base price, see §8), `pickup_limit`=1, `use_skill_id`=20412 (put-down skill, named "특산품 내려놓기: 황금 평원 마취제"), `specialty_zone_id`=22 (origin zone group = Golden Plains), `auction_a_category_id`=1. **[D]**
- `item_backpacks` (466 rows, `item_id` FK): `backpack_type_id` enum `BackpackType` {1 CastleClaim, 2 Glider, 3 **TradePack**, 4 SiegeDeclare, 5 NationFlag, 6 Fish, 7 ToyFlag}; `heavy` flag (the heavy/light pack distinction), `normal_specialty`, `use_as_stat`, `asset_id`/`asset2_id` (carry models). All 231 route packs have a row here (0 missing). **[D]**
- **Tooltip (canonical 1.2 rules, verbatim in data, item 26488 `description`):** ① produced at the zone's 특산품 제작대; ② sellable at Nuia '금화 교역상' (gold traders) and at Haranya + Freedich '물품 교역상' (goods traders), **NOT at the production-zone trader**; ③ "내려놓은 등짐은 6일 후 소멸" (a placed-down pack disappears after 6 days); ④ "10레벨 미만은 특산품 제작/판매 불가" (under level 10: no craft/sell); ⑤ "판매 시 22시간 후 우편으로 대금 지급" (payment mailed 22 h after sale); ⑥ producer ≠ seller ⇒ payout split "생산비 20% / 운송비 80%" (production 20% / transport 80%). **[D]**

### 2.3 Place-down chain (`put_down_backpack_effects`)

- `put_down_backpack_effects` (341 rows): `id` → `backpack_doodad_id`. `effects` row 27200 (for pack 26488's use skill 20412) has `actual_type`=PutDownBackpackEffect, `actual_id`=109 → `put_down_backpack_effects` 109 → **doodad 6068 "황금 평원 마취제 꾸러미"** ("Golden Plains Anesthetic bundle"). So each pack's put-down skill maps through the effect table to a per-pack placed doodad template. **[D]**
- Placed-pack doodad templates (`doodad_almighties`, e.g. 3463/6059/6060/6068): `group_id`=38, `percent`=100, `min_time`=600000 / `max_time`=700000 ms, `sim_radius`=750, `no_collision`=f, `childable`=t. One func group (kind 1) with a single func: **`DoodadFuncRecoverItem`**, `next_phase`=-1, `func_skill_id`=11361 (skill "회수"/Recover). No growth funcs ⇒ `TotalDoodadGrowthTime`=0 ⇒ the doodad never "matures" server-side and never self-despawns (see §9). **[D]**

### 2.4 Route tables (`specialties`, `specialty_bundles`, `specialty_bundle_items`, `specialty_npcs`)

- **`specialty_bundles` (30 rows)** — per-trader purchase lists. Names are the design: `금화_서대륙_<zone>` (west gold), `금화_동대륙_<zone>` (east gold), `아이템_<대륙>_<zone>` (item traders), `금화_빛너울_바다` (sea gold), `해외_델피나드의 별_<zone>` (overseas star traders), and bundle 1 "물건을 사지 않아야 하는 NPC용" ("for NPCs that must not buy anything"). **[D]**
- **`specialty_bundle_items` (3311 rows; 231 pack items × 29 bundles)**: `(item_id, specialty_bundle_id, profit, ratio)` — the per-pack, per-destination **base profit** and **static route ratio**. Example: 26488 → Solzreed gold bundle 10: profit 12800, ratio 3785; → Cross Plains bundle 13: profit 12100, ratio 3273. **[D]**
- **`specialty_npcs` (37 rows)**: `(npc_id, specialty_bundle_id)` — trader NPC ↔ bundle. Decoded (with `npcs.specialty_coin_id`):
  - **Gold traders** (bundles 2–15, coin 0): east 10643/10691/10650/10659/10642/1548/14767, west 10658 (Gweonid)/10664 (**Solzreed**)/10641 (Marianople)/10660 (Two Crowns)/4472 (Cross Plains)/4473 (Longshore)/14766 (Karkasse).
  - **Item traders** (bundles 16–22): pay `specialty_coin_id` 32103 안정된 흑탄 가루 (stabilized charcoal powder), 32104 안정된 암염 조각 (rock salt fragment), 32105 안정된 심층수 결정 (deepwater crystal), 32106 안정된 용골 정수 (keel essence) — incl. west item traders 4474 (Solzreed), 4477 (Two Crowns), 4478 (Cross Plains), sea trader 10817 (빛너울 바다).
  - **Delphinad-star traders** (bundles 8000001–8000007, coin **23633 델피나드의 별**): 8000001 Two Crowns, 8000002 Dawnshore, 8000003 **Solzreed**, 8000004 Songland, 8000005 Cross Plains, 8000006 Innisterre, 8000007 **Freedich (자유도)**.
  - **Disabled star NPCs** (bundle 1, names "…델피나드의 별 비활성화" = deactivated): 4475/4476/4479/4480/4481/10819 — the star traders that exist in data but were turned off in 1.2 (Gweonid/Marianople/Rainbow Plains/Hawk Plateau/Mahadevi/Longshore). **[D]**
- **`specialties` (2162 rows)**: `(row_zone_group_id = origin, col_zone_group_id = destination, ratio, profit, vendor_exist)`. 239 rows `vendor_exist`=t (a trader exists at the destination). Example: (22 Golden Plains → 5 Solzreed): ratio 2500, profit 5000; (22 → 20 Cross Plains): 2500/7500. **Loaded by `SpecialtyManager.Load` but never read by any game logic** **[D]** — the client's route map (shift+O) data source; the server rebuilds prices from bundle_items + dynamic ratio instead.

### 2.5 Craft chain (`doodad_func_craft_packs` → `craft_packs` → `craft_pack_crafts` → `crafts`)

- Specialty crafting tables: `doodad_almighties` 4220–4246 "…의 특산품 제작대" (one per zone, incl. **4220 솔즈리드 반도의 특산품 제작대** — Solzreed's table), 6078 generic, 7701 주민 전용 (resident-only). Each carries a `DoodadFuncCraftPack` func → `craft_pack_id`. **[D]**
- Example recipe **craft 5403 "특산품: 황금 평원 마취제"**: `cast_delay`=10000 ms, `skill_id`=16766, materials = **4747 특산품 품질 인증서 ×1** (Specialty Quality Certificate, 5000 g) + **19909 색이 좋은 기장 ×3** (100 g) + **8009 양귀비 ×10** (1340 g); product 26488 ×1, rate 100. Gweonid's pack (craft 6202) additionally pins `req_doodad_id`=4221 (its own table). Skill 16766: "장사: 특산품 제작과 포장", `consume_lp`=**60**, `casting_time`=6000 ms, `actability_group_id`=31 (장사/Commerce). **[D]**
- The fork's craft executor (`CharacterCraft`) consumes `skill.Template.ConsumeLaborPower` and routes pack products to the backpack slot (§4). **[D]**

### 2.6 Solzreed reference numbers (golden route)

- Solzreed zone group = **5** (`zone_groups` 5 = w_solzreed; zones 142/178/179). **[D]**
- Solzreed gold trader: NPC **10664** (미스티), bundle 10 — accepts west packs (e.g. 20091 Gweonid IS in bundle 10), subject only to the origin-exclusion rule of §7.1 (a pack never appears in its own zone's trader bundle). **[D]**
- Solzreed item trader: NPC 4474, bundle 20 (west + east packs — item traders accept both continents). **[D]**
- Solzreed star trader: NPC 8000003, bundle 8000003 (east packs only; Freedich 8000007 = both). **[D]**

---

## 3. Creation requirements

1. **Workbench:** the zone's 특산품 제작대 doodad (craft_pack wiring, §2.5). Data lists exactly one table per zone + 2 special ones (6078 generic test, 7701 resident-only). **[D]**
2. **Recipe + materials:** `crafts`/`craft_materials`/`craft_products` — certificate + zone produce (5403 example above). **[D]**
3. **Labor:** 60 LP via skill 16766 (`consume_lp`=60, "60의 노동력을 소비해 지역 특산품을 제작합니다"). **[D]** Research agrees packs cost labor on both ends ("Both crafting and delivering these packs costs labor" — AAClassic, 2026-08-11). **[W]**
4. **Level:** ≥10 per tooltip **[D]**; period guides echo a level gate for specialty crafting **[W: reddit r/archeage wiki "Archeage Trading & Commerce" — though that text says 30+ and refers to construction packs; treat the item tooltip as authoritative for 1.2]**. **Fork: no level check on the craft path (CharacterCraft) nor on SellSpecialty** **[D]** — gap.
5. **Backpack slot free:** the finished pack is force-equipped into the Backpack slot; if a pack (non-glider) is already there, craft fails with `BackpackOccupied` (315) + `CraftCantActAnyMore` (CharacterCraft.cs:250–255, 314–319). **[D]**

---

## 4. Backpack occupancy (carrying)

- **Slot model:** `EquipmentItemSlot.Backpack` = 26, `EquipmentItemSlotType.Backpack` = 30; `EquipmentContainer` routes `BackpackTemplate` items to that slot (EquipmentContainer.cs:117–140). **[D]**
- **Auto-equip:** `ItemManager.IsAutoEquipTradePack` (ItemManager.cs:1968) = `template is BackpackTemplate && !BindType.BindOnEquip` — used by `Inventory.TryAddNewItem`/`TryEquipNewBackPack` (Inventory.cs:707–733), the craft path (CharacterCraft.cs:250/314, with `crafterId=Owner.Id` → `made_unit_id`), quest rewards (`QuestActSupplyItem.cs:41`, `Quest.cs:329`), loot (`LootingContainer.cs:624–628` — picking a pack off a corpse auto-equips it, taking off a glider first). **[D]**
- **One pack at a time:** `TakeoffBackpack(glidersOnly:true)` (Inventory.cs:676–696) must move the equipped glider into the bag before a pack can be equipped; a non-glider in the slot blocks replacement ("Something other than a glider is equipped… don't allow replacing check", Inventory.cs:666–668). So canonical occupancy = exactly 1 item in the Backpack slot, pack or glider. **[D]**
- **Swap-back on place:** `PutDownBackpackEffect` remembers `PreviousBackPackItemId` and restores the glider to the Backpack slot after the pack is placed (PutDownBackpackEffect.cs:41–44, 95–98). **[D]**
- **Carry penalty:** movement slow-down while carrying is client-side animation/state in 1.2 (no speed modifier found in server data); AAClassic: packs "slowing their movement speed" **[W]**. Flag as research-derived; verify during M4-2 E2E.

---

## 5. Placement / pickup rules

### 5.1 Put down (`PutDownBackpackEffect`, triggered by the pack's use skill)

1. Pack must be in the **Backpack slot** (`Equipment.GetItemByItemId`, PutDownBackpackEffect.cs:32). **[D]**
2. **Public farm exclusion:** `PublicFarmManager.InPublicFarm` → error `CommonFarmNotAllowedType` (494). **[D]**
3. **Position:** 1 m in front of the player (`AddDistanceToFront(1f)`), rotation forced to north (0,0,0). **[D]**
4. **House gate:** if the spot is inside a house plot (`HousingManager.GetHouseAtLocation`), the house must `AllowedToInteract` (owner/perm) else error `Backpack`. **[D]** — this is the "storage on property" rule: you may store a pack on land you may interact with; on success the doodad binds to the house (`OwnerDbId=house.Id`, `OwnerType=Housing`, parented) — PutDownBackpackEffect.cs:51–88. **[D]**
5. **Item transfer:** pack item moves to the character's **SystemContainer** (survives restart; §10) — the doodad records `ItemId`, `ItemTemplateId`, `UccId`, `PlantTime=now`, `IsPersistent=true`, and `Save()`s to MySQL `doodads`. **[D]**
6. Glider swap-back (§4). Broadcast `SCUnitEquipmentsChangedPacket` (Backpack slot cleared). **[D]**

### 5.2 Pick up (`DoodadFuncRecoverItem`, func on the placed-pack doodad, skill 11361)

1. Item must still exist and sit in a **System container** — otherwise "already picked up by somebody else" → `InteractionRecoverParent` (14), no dupe. **[D]**
2. If the doodad is bound to a house/slave, `AllowedToInteract` check → `InteractionPermissionDeny` (232). **[D]**
3. Trade pack (`IsAutoEquipTradePack`) → `TakeoffBackpack` then into the **Backpack slot** (`RecoverDoodadItem` task type); non-pack recoverables go to the bag. Broadcast `SCUnitEquipmentsChangedPacket`. **[D]**
4. **No ownership gate:** any character can pick up a placed pack (only the item-exists and property-access checks run). Canonical 1.2 matches — placed packs are stealable loot; the 6-day timer is the only protection (Fandom: expire in 6 days, "picking it up and placing it back down will reset the timer") **[W]**.
5. **Death drop:** dying while carrying a trade pack drops it as the same put-down doodad (CharacterCombat.cs:340–365 — reuses the pack's `PutDownBackpackEffect` doodad id). **[D]**

---

## 6. Ownership

- **Crafter:** `Item.MadeUnitId` (set at craft, CharacterCraft.cs:250 with `Owner.Id`; persisted in MySQL `items.made_unit_id`; ItemManager.cs:1570–1617). `SellSpecialty` uses it for the 80/20 split (SpecialtyManager.cs:261: `crafterId = backpack.MadeUnitId != player.Id ? backpack.MadeUnitId : 0` — note: if the maker is unknown the seller keeps 100%). **[D]**
- **Item owner:** `bind_id`=2 (bind on pickup) — the pack binds to whoever picks it up; `owner` column persists on the item. **[D]**
- **Placed doodad:** `owner_id` = placing character (doodad `OwnerId`), `owner_type` = Housing when stored on property (MySQL `doodads`). **[D]**
- **Storage on property:** allowed only on plots the character may interact with (§5.1.4); the doodad is persistent and parented to the house — survives restart (M3b machinery: `SpawnPersistentDoodads`, SpawnManager.cs:695–755 restores `ItemId`/template/Ucc). **[D]** Research: "Trade Packs may be placed on player owned land" (Fandom snippet, 2026-08-11) **[W]**.

---

## 7. Sale location / route ratios

### 7.1 Trader acceptance matrix (data-verified)

| Trader class | Bundles | Accepts | Pays |
|---|---|---|---|
| Gold (금화_서/동대륙) | 2–15 | packs of **its own continent only** (east packs never in west gold bundles and vice versa — verified for 20091/20093/26488) | gold |
| Item (아이템_서/동) | 16–22 (+19 sea) | **any continent** (east packs in west item bundles 20/21/22 and sea 19) | stabilizer items via `specialty_coin_id` |
| Delphinad star (해외) | 8000001–8000007 | **opposite continent only**; Freedich (8000007) accepts **both** | 델피나드의 별 (23633) |
| Origin trader | — | the pack's **own zone's trader never lists it** (verified: 20091 ∉ bundle 9) | — |

Origin-exclusion is encoded as **absence from the bundle**: `GetBasePriceForSpecialty` fails the bundle lookup → error `Invalid` (SpecialtyManager.cs:209–219). The tooltip ("생산지 교역상에게는 판매 불가") and a period guide ("Specialty Packs must not be made in the same zone as where you are selling them" — ArcheRage NA forums, 3.5 trade guide 2019-03-23) agree **[D][W]**. Note the fork also sends `StoreCantSellSameZone` (512) — but only for NPCs that are not specialty traders at all (SpecialtyManager.cs:201–205), so the canonical "same zone" error message is effectively never used for its data meaning.

### 7.2 Route ratios (two-layer)

1. **Static per-route base** (`specialty_bundle_items`): `base = floor(profit × ratio/1000) + item.refund` (SpecialtyManager.cs:229) — e.g. 26488 at Solzreed: `floor(12800×3.785)+20000` = 68,448 c; at Cross Plains: 59,603 c. **[D]**
2. **Dynamic market ratio per (pack × destination zone group)**: 70–130% (config `MaxSpecialtyRatio`/`MinSpecialtyRatio`), initialized to max 130; **−0.5 per pack sold** in the destination zone per 1-min tick (`RatioDecreasePerPack`/`RatioDecreaseTickMinutes`), **+5 per 60-min tick** (`RatioIncreasePerTick`/`RatioRegenTickMinutes`) — `ConsumeRatio`/`RegenRatio` (SpecialtyManager.cs:339–371), `SpecialtyRatioConsumeTask`/`RegenTask` scheduled at `Initialize`. Sold counts are per (pack, zone) in-memory (`_soldPackAmountInTick`). **[D]** Research: "the 80-130% modifier is frequency of the type of pack delivered to the trader" (reddit 2h1vle, 2014-09-29) **[W]**; "percent… ranges from 70% to 130%. As players turn in packs, the percent… slowly decreases… slowly creep back up until capped at 130%" (AAClassic Commerce, 2026-08-11) **[W]**.
3. **The `specialties` matrix (row/col, vendor_exist)** is the canonical static route map but is **unused** by the fork engine (loaded, never read) **[D]**; the client's shift+O route view is fed by `CSRequestSpecialtyCurrentPacket` (0x131) → `SCSpecialtyCurrentPacket` using dynamic ratios only. **[D]**

---

## 8. Reward math (sale)

`SellSpecialty` (SpecialtyManager.cs:232–337), step by step:

1. **Labor gate:** `LaborPower < 60` → `NotEnoughLaborPower`; else **−60 Commerce labor** (`ChangeLabor(-60, ActabilityType.Commerce)`). Research: "You can trade in a pack for 60 LP" (reddit 2hrkvp, 2014-10-06) **[W]**.
2. **Range/validity:** NPC within **2.5 m** (`TooFarAway`); NPC must be a specialty trader (`StoreCantSellSameZone`); pack must be in the NPC's bundle (`Invalid`); pack must be on the back (`StoreBackpackNogoods` 361). **[D]**
3. **Base price:** `floor(profit × ratio/1000) + item.refund` (bundle row + pack refund = craft-cost rebate). **[D]**
4. **Dynamic ratio:** `priceRatio = _priceRatios[pack][currentZoneGroup]` (70–130; default 130). **[D]**
5. **Interest:** hardcoded **+5%** (`interestRate=5`, SpecialtyManager.cs:264) — `final = base × ratio/100 × 1.05`; `amountBonus` (negotiation) is a 0 TODO. **[D]** No 1.2 source found for a 5% interest term — flagged **[R]**, verify during M4-2 (the mail `body()` template from the 1.2 client does carry an interest-rate parameter, MailForSpeciality.cs:28–42 — so the client-side mail window is built to display it). **[D]**
6. **Payout unit:** no `specialty_coin_id` → **gold** (`Item.Coins`, amount in copper); with coin → `round(amount/10000)` units of the coin item (data stores coin amounts at 10 000× gold rate; the fork comment says exactly this, SpecialtyManager.cs:277–283). **[D]**
7. **80/20 split:** if `backpack.MadeUnitId` ≠ seller and the `backpackProfitShare` feature is on: seller gets `round(total × 0.80)`, crafter the remainder (SpecialtyManager.cs:260–297). Hardcoded 0.80 — **not data-driven**; if the feature is off, seller keeps 100%. Tooltip: "생산비 20% / 운송비 80%" **[D]**; Fandom: "80% of its profit goes to the player who delivered it, while 20% goes to the player who crafted it" **[W]**.
8. **Mail payout:** `MailForSpeciality` (`MailType.SysSellBackpack` 19): seller mail always; crafter mail when split applies; `Body.RecvDate = now + TradePackMailDelayInMinutes` (**config 480 = 8 h**; canonical tooltip = **22 h** — mismatch). Mail body is the 1.2 client's `body(...)` Lua format (pack name, rate, base, payout, receiver case 0/1/2, coin type, coin counts) — taken from Trino 1.2 per the code comment. **[D]** Research: 22 h delay confirmed (reddit 2hrkvp 2014-10-06; tooltip). **[W][D]**
9. **Consume:** the pack is consumed from the Backpack slot (`ConsumeItem(SellBackpack, …)`); the mail send failure path refunds nothing but returns the base price (pack already consumed on success). **[D]**

**Worked examples (all data-verified from bundle rows):**

| Pack | Trader | profit | ratio | base | payout @100% | payout @130% |
|---|---|---|---|---|---|---|
| 26488 황금 평원 마취제 (refund 20000 c) | Solzreed gold (bundle 10) | 12800 | 3785 | 68,448 c | 71,870 c ≈ 7.19 g | 93,431 c ≈ 9.34 g |
| 26488 | Cross Plains gold (bundle 13) | 12100 | 3273 | 59,603 c | 62,583 c | 81,358 c |
| 20093 무지개 벌판 기름 (refund 50000 c) | Solzreed item (bundle 20) | 15000 | 4333 | 114,995 c | 12 × 안정된 흑탄 가루 | 16 × |
| 20093 | Solzreed star (bundle 8000003) | 750 | 1000 | 50,750 c | 5 × 델피나드의 별 | 7 × |

(base = floor(profit×ratio/1000)+refund; payout = base×ratio/100×1.05; coin routes ÷10000. The item traders' per-coin values: 32103–32106 = 100 g/10 g refund.)

---

## 9. Maturation / expiry timers

The task's "maturation timer" maps to three distinct canonical timers:

1. **Placed-pack despawn — 6 days.** Tooltip: "내려놓은 등짐은 6일 후 소멸" **[D]**; Fandom: "Trade Packages expire within 6 days. Simply picking it up and placing it back down will reset the timer" **[W, 2026-08-11]**. **Fork: NOT implemented** — placed packs are persistent doodads with no expiry task; the doodad template's min/max_time (600000/700000 ms) feeds nothing (no growth funcs ⇒ `TotalDoodadGrowthTime`=0, DoodadManager.cs:2714–2730); `TimeLeft` on the wire is 0. A placed pack survives indefinitely (until picked up or deleted). **[D]**
2. **Payout mail delay — 22 h.** Tooltip **[D]** + reddit 2014-10-06 **[W]**. **Fork: 8 h** (`TradePackMailDelayInMinutes`=480 in Specialty.json; overridable via `SetTradePackMailDelay` command). **[D]** — must be 1320 (22×60) for canonical behavior.
3. **Market ratio decay/regen — 1-min decay tick / 60-min regen tick.** Config-verified **[D]**. **Not persisted** — `_soldPackAmountInTick` is memory-only, so every restart resets all route ratios to 130% (canonical 1.2 persisted the state server-side; restart behavior to be asserted by M4-2). **[D][R]**

---

## 10. Persistence / restart surface

- Placed pack doodad: MySQL `doodads` row (IsPersistent), restored at boot by SpawnManager (SpawnManager.cs:695–755: template, phase, position, `item_id`, `item_template_id`, Ucc re-derived from the live item). **[D]**
- Pack item: lives in the character's **SystemContainer** while placed — `PartOfPlayerInventory`=false (ItemContainer.cs:97–110) but items are saved via the global `_allItems` loop (`REPLACE INTO items … made_unit_id …`, ItemManager.cs:1531–1617) with slot_type System; restored at boot from MySQL. So the pack + crafter survive restart, and `DoodadFuncRecoverItem`'s system-container check keeps working after reboot. **[D]**
- Carry state: pack in the Backpack slot = normal equipment item, saved with the character. **[D]**
- **Not persisted:** sold-pack counters / dynamic ratios (above), 6-day expiry (absent), mail delay value is config not state. **[D]**

---

## 11. Engine gaps — fork vs upstream

| # | Gap | Fork evidence | Upstream status |
|---|---|---|---|
| 1 | **6-day placed-pack despawn absent** | no timer/task anywhere (grep despawn/expire on pack paths); doodad growth 0 | identical (SpecialtyManager diff = 0; Doodad code shared) |
| 2 | **Mail delay 8 h vs canonical 22 h** | `Specialty.json` `TradePackMailDelayInMinutes` 480; command `SetTradePackMailDelay` exists to tweak | identical |
| 3 | **Level-10 gate absent** | no level check in `SellSpecialty` or `CharacterCraft`; tooltip requires 10 | identical |
| 4 | **`specialties` matrix unused** | loaded (SpecialtyManager.cs:49–67) but never read; only bundle_items + dynamic ratio used | identical |
| 5 | **`vendor_exist` never read (loader bug)** | `VendorExist = reader.GetBoolean("id", true)` reads the id column — always true (SpecialtyManager.cs:62) | identical |
| 6 | **80/20 split hardcoded + feature-gated** | `sellerShare = 0.80f` (SpecialtyManager.cs:262) behind `Feature.backpackProfitShare`; if off, 100% to seller | identical |
| 7 | **5% interest hardcoded** | `interestRate = 5` (SpecialtyManager.cs:264); no data table found; client body() supports it | identical |
| 8 | **Negotiation bonus TODO** | `amountBonus = 0` (SpecialtyManager.cs:268) | identical |
| 9 | **Ratio state not persisted** | `_soldPackAmountInTick` memory-only; restart ⇒ all routes 130% | identical |
| 10 | **Trader UI packets missing** | `CSBuySpecialtyItemPacket` 0xfff + `CSSpecialtyRecordLoadPacket` 0xfff unregistered (GameNetwork.cs:85–86); `CSListSpecialtyGoodsPacket` (0x044) registered but no-op | identical |
| 11 | **`StoreCantSellSameZone` misused** | sent for non-trader NPCs (SpecialtyManager.cs:203), never for the actual same-zone rule (which yields `Invalid`) | identical |
| 12 | **No carry-speed data** | no movement modifier found; client-side in 1.2 | n/a (client) |
| 13 | **No craft/sell distance-from-table check on craft** | `req_doodad_id` present on some craft rows (6202→4221) — check CharacterCraft for enforcement during M4-2 | shared craft engine |

Verdict: **the fork's trade-pack engine is upstream-identical; everything canonical that must be built for M4-2 is small, targeted work** — a placed-pack expiry task (6 days, PlantTime-based, reset on pickup), the 22 h mail delay (config), the level-10 gate (craft + sell), optional use of the `specialties` matrix/vendor_exist (fix the loader), persistence of sold counts, and E2E assertions for coin routes + 80/20 split + restart behavior.

---

## 12. Implications for M4-2 (t_449d0c41) — design notes only

1. **Pack lifecycle E2E on the golden route:** craft at Solzreed table (4220) or a zone table → auto-equip → carry → place → pick up (timer reset) → sell at Solzreed gold trader 10664 → mail after delay. Assert: base price formula, dynamic ratio 70–130, 5% interest, 80/20 with a second crafter character, gold vs coin conversion.
2. **Restart assertions (per-object, per ROADMAP 2026-08-09 audit):** placed pack (doodad + item + crafter) survives `kill -9`; sell counter/ratios reset (document as known divergence or implement persistence); mail `RecvDate` delay honored.
3. **Canonical fixes to include:** 6-day expiry task on persistent pack doodads (PlantTime + 6 d; clear on pickup), `TradePackMailDelayInMinutes` 1320, level-10 gate, `specialties`/`vendor_exist` loader fix, error-code mapping (`StoreCantSellSameZone` for origin-zone attempts instead of `Invalid`).
4. **Out of scope for M4-2** (flag for later): vehicle/cargo packs (SLAVE-01), merchant-ship packs (`merchant_packs` table, 263 rows — unused by any manager), fish/other BackpackType paths.

---

## 13. Evidence appendix

**Data (compact.sqlite3 r208022):** `items` (437 impl_id=22, 255 specialty packs; 26488 tooltip quoted), `item_backpacks` (466; type 3 = TradePack), `put_down_backpack_effects` (341; 109→doodad 6068), `effects` (27200→PutDownBackpackEffect), `skills` (20412 put-down per-pack; 11361 Recover; 16766 craft skill consume_lp=60, actability 31 장사), `doodad_almighties` (4220–4246/6078/7701 특산품 제작대; 6068 placed pack; min/max_time 600000/700000), `doodad_func_groups`+`doodad_funcs` (RecoverItem funcs, next_phase −1, func_skill 11361), `crafts`/`craft_materials`/`craft_products`/`craft_pack_crafts`/`craft_packs`/`doodad_func_craft_packs` (5403 example; 6202 req_doodad 4221), `specialties` (2162; 239 vendor t; (22,5)=2500/5000), `specialty_bundles` (30; KR names quoted), `specialty_bundle_items` (3311/231/29; worked-example rows), `specialty_npcs` (37; gold/item/star/disabled), `npcs` (`specialty_coin_id` 23633/32103–32106; traders 10664/4474/8000003…), `zone_groups` (5=w_solzreed), `zones` (142/178/179).

**Code paths (fork develop @ 4ded92c61):** `Core/Managers/World/SpecialtyManager.cs` (whole file; == upstream), `Configurations/Specialty.json`, `Models/Game/Mails/MailForSpeciality.cs` (body() Lua template "from Trino 1.2"), `Models/Game/Skills/Effects/PutDownBackpackEffect.cs`, `Models/Game/DoodadObj/Funcs/DoodadFuncRecoverItem.cs`, `Models/Game/Char/CharacterCraft.cs:243–321`, `Models/Game/Char/Inventory.cs:676–733`, `Models/Game/Items/Containers/EquipmentContainer.cs:117–140`, `Models/Game/Char/CharacterCombat.cs:340–365` (death drop), `Models/Game/Items/Containers/LootingContainer.cs:624–628`, `Core/Managers/ItemManager.cs:1968, 1440–1443, 1531–1617`, `Core/Managers/World/SpawnManager.cs:695–755`, `Core/Network/Game/GameNetwork.cs:82–86, 286`, `Core/Packets/C2G/CSSellBackpackGoodsPacket.cs` (0x042), `CSSpecialtyRatioPacket.cs` (0x043), `CSRequestSpecialtyCurrentPacket.cs` (0x131), `CSListSpecialtyGoodsPacket.cs` (0x044), `Core/Packets/G2C/SCSpecialtyRatioPacket.cs`, `SCSpecialtyCurrentPacket.cs`, `Models/Game/ErrorMessageType.cs` (14/169/232/315/361/494/512), `Models/Game/Items/Templates/BackpackType.cs`, `Models/Game/Items/EquipmentItemSlot.cs:31`, `Models/StaticValues/ItemImplEnum.cs:27`, `Models/Game/Mails/MailType.cs:24`, `Models/Game/World/WorldInteraction.cs:118`.

**Pak (game_pak, Feb 11 2023 build, 218,069 entries):** `game/scriptsbin/x2ui/specialty/{form,info}.alb` (+locale/ru.alb, toc.g) — the client's specialty route/trade UI; item tooltips confirmed in-sqlite (the compact DB is the client's own data dump). No server-relevant pack tables live only in the pak.

**History / lineage:** fork `SpecialtyManager` diff vs upstream develop = 0 lines (2026-08-11). The 1.2-era AAEmu specialty code (Trino-era) carried the same mail body() contract (comment in MailForSpeciality.cs). `merchant_packs` (263 rows) has no loader/manager in the fork — the merchant-ship pack economy is unwired (SLAVE-01/MERCHANT-01 scope).

**Period behavior (research, access 2026-08-11):** Fandom "Trade Packages" (6-day expiry + reset on pickup; 80/20 split) — page fetch 503s, quotes via search snippet; reddit r/archeage 2014-09-29 "Basic trade run step-by-step" (80–130% per pack-type frequency); reddit r/archeage 2014-10-06 "Why Do You Need To Wait 22 Hours For Trade Pack" (60 LP sell; 22 h anti-inflation delay); AAClassic wiki "Commerce" rev 2024-08-24 (gold/gilda/resource traders; 70–130% turn-in percent; labor on both ends; Freedich any-continent); ArcheRage NA forums "3.5 Trade System Guide" 2019-03-23 (no same-zone sales).

*Dossier only — no code changed on this branch.*
