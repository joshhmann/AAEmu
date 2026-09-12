# MERCHANT-01 Canonical Audit Dossier (C) — 2026-09-10

Source/test HEAD: `463d46d8608e55d03ea73abc448b2374e8ab7ab5` (develop).
Canonical DB: `AAEmu.Game/Data/compact.sqlite3`, 679 tables,
md5 `78b3bdbf038db3b927056106efdf91af` (verified on repo copy and E2E lane copy).
Confidence: `exact` for all data facts (direct `sqlite3` reads); `exact` for code
paths (read at HEAD). No live/client/H evidence claimed here.

## 1. Vendor surface (breadth)

| Table | Rows | Shape |
|---|---|---|
| `merchants` (npc_id → merchant_pack_id) | 1983 | vendor link table |
| `merchant_packs` (id, name, owner_npc_id, kind_id) | 263 | pack headers |
| `merchant_goods` (id, merchant_pack_id, item_id, grade_id) | 3246 / 2036 distinct items | pack membership |
| `merchant_price_ratios` | **0 (empty)** | no per-NPC price scaling in 1.2 data |

Live representatives: seed merchant NPC 8522 → pack 171 (41 goods, sells potato
seed 15659); general merchant NPC 8524 → pack 145 (26 goods). Both spawner-backed
(`npc_spawner_npcs` has rows for both).

Load path (`AAEmu.Game/Core/Managers/UnitManagers/NpcManager.cs:846-888`):
`merchants` → `NpcTemplate.MerchantPackId`; `merchant_goods` →
`MerchantGoods` pack via `AddItemToStock`; per-line buy gate is
`pack == null || !pack.SellsItem(itemId)` → skip
(`AAEmu.Game/Core/Packets/C2G/CSBuyItemsPacket.cs:74`).

## 2. Stock

**Unlimited by data.** `merchant_goods` carries no quantity, stock, or refresh
column (4 columns total); neither do `merchant_packs` / `merchants`.
`AddItemToStock` (`AAEmu.Game/Models/Game/Merchant/MerchantGoods.cs:17`) is a
list-add. Pack membership is the only availability gate — no stock depletion,
restock timer, or per-player quota exists in code or data.

## 3. Currency pools & pricing

Pricing comes from `items.price` / `honor_price` / `living_point_price`
(there is deliberately no currency column on `merchant_goods` — noted in code at
`CSBuyItemsPacket.cs:119-122`). Breadth in data: 15960 money-priced items,
120 honor-priced, 81 vocation-priced (e.g. 14879 price 100000/honor 100;
16000 price 50/vocation 3). The client declares one currency per shop line;
post-trio (merge `e5db6d390`) each pool has an independent refusal gate with
matching feedback (`NotEnoughMoney` / `NotEnoughHonorPoint` /
`NotEnoughLivingPoint`, `CSBuyItemsPacket.cs:127-141`). `ShopCurrencyType`
(`AAEmu.Game/Models/Game/Items/ShopCurrencyType.cs`) also defines `SiegeShop`,
which the packet does **not** branch on → fail-closed `Unknown currency type`
(`CSBuyItemsPacket.cs:94`), no charge, no grant.

## 4. Refund & buyback semantics

- Sellable templates: 15699; refund > 0: 15938.
- Refund formula: `Template.Refund × grade RefundMultiplier / 100 × count`
  (`CSSellItemsPacket.cs:64-65`); `item_grades.refund_multiplier` runs
  100 (grade 0 common) … 600 (grade 11).
- Refund is paid **only** when the item actually leaves the bag for BuyBack
  (trio fix `beaf9b82e`, `CSSellItemsPacket.cs:49-56`).
- `BuyBackItems` is `ItemContainer(SlotType.None, false)` — non-persisted
  (`Character.cs:2628`); sold rows are queued for DB deletion (no #1189 dupe
  on relogin); rebuy moves buyback → bag at the same refund price.
- **BuyBack does not survive relogin by design**: wiped on enter-world
  (`EnterWorldManager.cs:210`) and on shutdown (`ShutdownTask.cs:81`).
  Conservation across restart therefore means money + persisted rows, and sold
  goods are expected to be absent post-restart.

## 5. Wire gates (post-trio, both shop packets)

NPC must resolve + `Template.Merchant` + pack ≠ 0; ≤ 3 m distance
(`TooFarAway`); pack must sell each buy line; per-currency funds gates; shop
grants run through `AcquireDefaultItemEx` with per-line stack snapshots and any
failure rolls the whole purchase back atomically before any charge (`BagFull`,
trio fix `3ba33b3af`, `CSBuyItemsPacket.cs:143-193`); sell requires `Sellable`
and a successful buyback move. Doodad shops are an unvalidated TODO
(`CSBuyItemsPacket.cs:77-81`).

## 6. Residuals

- **R1 — RESOLVED (`c00090c97`, atomic buyback rebuy):** the rebuy path checks `AddOrMoveExistingItem`, and on failure rolls back merged stacks, removes granted items, and returns the item to buyback before charging.
- **R2 — SCOPE LIMITATION (comment corrected; no code defect).** `GameplayActor.Buy`'s stale packet comment was corrected in `8f1d0f263`; the actor remains money-only and single-line, with no atomic multi-line rollback. Bot-driven merchant evidence therefore stays proxy/A-level; R-level claims require the wire paths (as the B6 run uses).
- **R3 — SERVER-SIDE CHAIN IMPLEMENTED:** doodad current `FuncGroupId` resolves `DoodadFuncStoreUi` via `DoodadManager.GetFuncsForGroup`/`GetFuncTemplate`, then `MerchantPackId` → `NpcManager.GetGoods`; unconditional `SellsItem` membership enforcement keeps crafted templates closed. `DoodadFuncStoreUi.Use()` remains a `Logger.Trace` no-op, so the client cannot yet be told/presented the shop. No existing rig/test depends on doodad buying.


## 7. Promotion link

MERCHANT-01 R=2 rests on `B6MerchantConservationE2eTests` (PASS 1/1, 3m35s,
isolated lane, real `CSBuyItemsPacket`/`CSSellItemsPacket` frames, kill-9 with
PID-verified death, MySQL money + item-row byte-equality, post-restart rebuy):
`/root/aaemu-e2e-b6merchant/logs/b6-merchant-conservation-reconcile.md`.
Rig headless A-evidence: `MerchantRigTests` 6/6 on this HEAD. H stays UNKNOWN.
