# AUCTION-01 Domain Dossier (canonical 1.2 audit, 2026-09-10)

Scorecard row at writing: `AUCTION-01 | List, search, bid/buy, settle, cancel, expire | M8`,
W/A/R = 2 (2026-08-23 promotion @ `f3bb787ce`; `AuctionHouseRestartE2eTests` live E2E
PASS 3m26s — post → buy → settle → expiry-mail across kill -9).
Audit HEAD: `402042ae7ae561fe080cfea78eeb24408d46b437` (develop).
C stays U: no live-client packet capture pins the wire format; all wire claims below
are code-read against 1.2 offsets, never client-proven.

UPDATE (2026-09-12): G3 (G-cancel) is CLOSED, and the settled-buy path was found to
carry the same container-orphan defect (recorded as G-settle-buy in §4) — also
closed. Evidence is live-stack E2E on the .165 testing host (bot-driven real C2G
packets), NOT human, and still no live-client capture — so `C` remains `U`.

## 1. Intended behavior (1.2)

A consignment house: a seller lists an item instance for a fixed duration with a
start (bid floor) and buyout price, pays a listing fee up front, and the item leaves
their bag. Buyers search by keyword/category/level/grade, bid (outbidding refunds the
previous bidder), or buy out. On sale the buyer gets the item by mail and the seller
gets 90% of the price by mail; on expiry with no winning bid the item returns to the
seller by mail; a lot with a winning bid at expiry settles as a sale. Cancellation is
allowed only while nobody has bid. All settlement legs are asynchronous mail, never
direct inventory grants.

## 2. Required data (canonical DB, read-only MCP provenance)

Canonical DB md5 `78b3bdbf038db3b927056106efdf91af` (verified `md5sum` this audit).
MCP source: `source_id=compact.sqlite3`,
`path=/root/aaemu-dev/AAEmu.Game/Data/compact.sqlite3`, `version=1.2 r208022`
(`list_sources`, `generated_at=2026-09-10T10:43:15Z` window; per-query `generated_at`
stamps in trace notes).

| # | Claim | Tool / input | Result |
|---|---|---|---|
| D1 | Auction category reference tables present | `query_sql`: `sqlite_master` `instr(name,'auction')` | 4 tables: `auction_a_categories`, `auction_b_categories`, `auction_c_categories`, `doodad_func_auction_uis` |
| D2 | A-category vocabulary (11 rows) | `query_sql`: `SELECT * FROM auction_a_categories` | weapon, armor, accessory, instrument, costume, consumable, craft, machine, pet, etc., declare_siege |
| D3 | B/C-category counts | `query_sql`: `COUNT(*)` | B=39 rows, C=72 rows (each with `parent_category_id` FK-shape into its parent tier) |
| D4 | Auctioneer doodad funcs | `query_sql`: `SELECT * FROM doodad_func_auction_uis` | 2 rows (ids 1–2); the wire NPC a lot is posted "at" is doodad-mediated |
| D5 | Per-item category membership lives on `items` | `lookup_row items 10000` / `5318` + `query_sql` `SELECT auction_a/b/c_category_id ... WHERE id IN (10000,5318)` | Both E2E canonical items carry category ids: 5318 (1.2 first-step dagger) = (1,1,1); 10000 (craft scroll) = (6,21,NULL). `items` n=21482 rows total |
| D6 | Engine consumes D5 | code-read `ItemManager.cs:1087-1102` | `auction_a/b/c_category_id` → `ItemTemplate.AuctionCategoryA/B/C` → `AuctionSettings`; `AuctionManager.SearchAuctionLots` filters lots on `template.AuctionSettings` (CategoryA/B/C, grade, level band, keyword via `ILocalizationManager.MatchItemName`) |
| D7 | No auction reference table for durations/fees | `query_sql` negative: no `instr(name,'item') AND instr(name,'auction')` table; code-read | Durations (6/12/24/48h) and fee math are engine constants (`AuctionDuration` 0–3, `AuctionManager.cs:31-56,333-376,655-660`), NOT reference data — no canonical row to corroborate them against |

Mutable state (NOT canonical reference — provenance is repo file @ HEAD, not MCP):
`SQL/aaemu_game.sql:63-86` `auction_house` (id, duration, item_id, post/end dates UTC,
world/client/bidder ids+names, start/direct/bid money, extra). Item instance itself
stays in `items` under `SlotType.Auction = 6` while listed.

## 3. Engine-path mapping

Manager: `AAEmu.Game/Core/Managers/AuctionManager.cs` (DI + `Singleton`, `ILoadable`;
`Load()` `SELECT * FROM auction_house` + `ItemManager.GetItemByItemId` re-link;
`Save()` dirty-lot `REPLACE` + `DeletedAuctionItemIds` delete inside `SaveManager`
transaction; 5s `AuctionHouseTask` → `UpdateAuctionHouse` expiry sweep).

| Leg | Wire in (C2G) | Engine | Wire out (G2C) |
|---|---|---|---|
| Post | `CSAuctionPostPacket` 0x0b7 → `PostLotOnAuction` | fee = buyout×1%×(duration+1), cap 100g; `ChangeMoney` debit; item → `Inventory.AuctionAttachments`; `CreateAuctionLot` (EndTime = now+6/12/24/48h) | `SCAuctionPostedPacket` 0x12e |
| Search/list | `CSAuctionSearchPacket` 0x0b8 → `SearchAuctionLots` | in-memory filter over `AuctionLots` (D6), sort, 9-per-page split | `SCAuctionSearchedPacket` 0x12f |
| My bids | `CSAuctionMyBidListPacket` 0x0bb → `GetBidAuctionLots` | filter `BidderId == player.Id`, same 9/page | 0x12f |
| Lowest price | `CSAuctionLowestPricePacket` 0x0bc → `CheapestAuctionLot` | min `DirectMoney` per template | `SCAuctionLowestPricePacket` 0x130 |
| Bid | `CSBidAuctionPacket` 0x0b9 → `BidOnAuctionLot` bid branch | outbid previous bidder (refund BY MAIL `FinalizeForBidFail`); `SubtractMoney` bid; `IsDirty=true` | `SCAuctionBidPacket` 0x131 |
| Buyout | `CSBidAuctionPacket` 0x0b9 → `BidOnAuctionLot` buy-now branch (`bid.Money >= DirectMoney`) | `SubtractMoney` full price; `RemoveAuctionLotSold` → seller mail 90% (`FinalizeForSaleSeller`, `MailType.AucOffSuccess`=14) + buyer mail with item (`FinalizeForSaleBuyer`, `AucBidWin`=16; relocates the SOLD instance into the buyer's MAIL container — see G-settle-buy, §4) | lot removed; mails via mail sweep |
| Cancel | `CSCancelAuctionPacket` 0x0ba → `CancelAuctionLot` | refused if any bidder; else cancel mail (`FinalizeForCancel`, returning the ORIGINAL instance and relocating it into the seller's MAIL container — G3 CLOSED, §4) + `SCAuctionCanceledPacket` 0x132 | `SCAuctionCanceledPacket` 0x132 |
| Expire sweep | `AuctionHouseTask` (5s) → `UpdateAuctionHouse` → per-lot try/catch isolation | bid lot → `RemoveAuctionLotSold` (settle-as-sale); no-bid lot → `RemoveAuctionLotFail` → return mail (`FinalizeForFail`, `AucOffFail`=15; relocates the ORIGINAL instance into the seller's MAIL container — same defect as G3, §4); missing-item lot expires WITHOUT mail (warn, never wedges sweep); mail exception logged, lot still expires | settlement mails |
| `SCAuctionMessagePacket` 0x133 | — | NEVER constructed anywhere in tree (offset defined, zero send sites) — dead wire slot | — |

Settlement mail model: `Models/Game/Mails/MailForAuction.cs` — titles
"Successful Auction Notice" / "Failed Auction Notice" / "Succesfull Purchase" [sic] /
"Failed Bid Notice" / "Cancelled Auction Notice" (all flagged `TODO: verify title
names` — uncorroborated against 1.2 client strings); sender "Auctioneer";
`Body.RecvDate = now` (instant delivery, no 30-min delay).

E2E pin (W/A/R=2 basis): `AAEmu.IntegrationTests/E2e/AuctionHouseRestartE2eTests.cs`
`Auction_PostBuySettle_AndExpiryMail_SurviveKill9` — post L1 → save → kill -9 →
listing live from MySQL, searchable, buyout settles (buyer −1000, `AucBidWin` mail
carries item at `SlotType.Mail=5`, seller `AucOffSuccess` 900c) → second kill -9 with
`end_time` time-traveled while down → real 5s task fires → `AucOffFail` expiry mail
with item attached, row leaves `auction_house`. MailTypes pinned in-test:
AucOffSuccess=14, AucOffFail=15, AucBidWin=16 (match `MailType.cs:18-22`).

## 4. Explicit gap list

1. **No auctioneer-proximity check (G-post/bid).** `PostLotOnAuction` and
   `BidOnAuctionLot` accept `npcId/npcId2` (auctioneer doodad/NPC) but never use
   them (`AuctionManager.cs:641-673,154-212`) — lots can be posted/bought from
   anywhere. Contrast mail-send, which enforces 5m mailbox proximity. Retail
   almost certainly gates on the auctioneer (D4 funcs exist); STRONGLY_INFERRED,
   never verified — stays a gap.
2. **Buy-now / bid money gate unchecked (G-settle).** `player.SubtractMoney(...)`
   return is ignored in both branches (`:179,205`). If `SubtractMoney` can fail
   open (lag/race), the lot settles without payment. Needs a fail-closed assert
   + test; code-read only.
3. **~~Cancel path mints a FRESH item, orphans the listed instance (G-cancel).~~
   CLOSED (2026-09-12, instance-faithful return + container relocation).**
   Two distinct defects were found on this path, and both are fixed:
   a. `CancelAuctionLot` used to `itemManager.Create(template,count,grade)` and
      mail the copy, losing the listed instance's enchant/durability/details
      (fixed earlier by commit `e2abd29b`: it now mails the ORIGINAL instance).
   b. The cancel mail only stamped `OwnerId`/`SlotType` and never relocated the
      instance into the recipient's MAIL container, so `ItemManager.Save`
      persisted `container_id` = the seller's listing (auction) container
      (`ItemManager.Save` writes `container_id` from `item._holdingContainer`,
      `ItemManager.cs:1636`). On reboot `ItemManager.LoadUserItems` re-added the
      row to that container and `ItemContainer.AddOrMoveExistingItem` re-stamped
      `SlotType` from the container's type (`ItemContainer.cs:433`) — the
      returned item booted back as a `SlotType.Auction` orphan.
   Fixed by routing cancel, expiry AND settled-buy attachments through one
   `MailForAuction.AttachItemForReturn(item, receiverId)` helper that relocates
   the SAME instance via `ItemManager.GetItemContainerForCharacter(receiverId,
   SlotType.Mail, null, 0)` — the offline-safe owner-id path (interface
   `IItemManager.cs:50`, impl `ItemManager.cs:1701`; same owner-id shape as
   `AuctionController.cs:173` for `SlotType.Auction` and `HousingManager.cs:1277`
   for `SlotType.System`), so it works while the recipient is offline. The
   lookup uses `PeekInstance` (the documented headless-degrade idiom,
   `Singleton.cs`) so DI-less unit rigs stamp-and-log instead of throwing; a
   failed relocation also falls back to the stamp with a loud Error, preserving
   the "never wedge the return, never lose the instance" invariant.
   EVIDENCE (live-stack E2E on the .165 testing host — NOT human, and NOT a live
   client, so `C` stays `U`): `AAEmu.IntegrationTests.E2e.
   AuctionHouseRestartE2eTests.Auction_CancelEnchantedListing_
   ReturnsOriginalInstance_AndSurvivesKill9` — RED before the fix
   (`soak-artifacts/auction-cancel/20260912-025046`: `reloaded_attachment_slot_type
   = 6`, `auction_orphans_post_restart = 1`, `persistence_round_trip_mail =
   false`), GREEN after (`soak-artifacts/auction-cancel/20260912-035828`:
   `slot_type = 5`, `auction_orphans_post_restart = 0`,
   `persistence_round_trip_mail = true`, details blob byte-identical across
   before/after-cancel/after-restart). The leg drives the REAL
   `CSAuectionPostPacket`/`CSCancelAuctionPacket` over the game link, so the
   cancel wire round trip is exercised — but the bytes remain bot-driven, not
   client-captured.
3b. **Settled-buy path had the IDENTICAL container orphan (G-settle-buy).
   CLOSED (2026-09-12).** `PostLotOnAuction` lists the item out of the seller's
   Auction container (`AuctionManager.cs:677`) and `RemoveAuctionLotSold`
   re-fetches that SAME instance (`AuctionManager.cs:37`) for the buyer's mail,
   but `FinalizeForSaleBuyer` only stamped `OwnerId`/`SlotType` +
   `Body.Attachments.Add`. The sold item therefore persisted `container_id` =
   the seller's listing container and booted back as a `SlotType.Auction` orphan
   owned by the buyer. Fixed by the same helper
   (`AttachItemForReturn(_item, _buyerId)`). This survived the original audit
   because AUCTION-01 asserted `slot_type`/`owner` but NEVER `container_id`, and
   never rebooted after a settle — the existing AUCTION-01 leg now rides its
   second kill -9 to pin container-id identity on the sold item.
   EVIDENCE: RED with the `FinalizeForSaleBuyer` hunk reverted (`sold item
   16777236 persisted container_id=65541 (want the buyer's Mail container
   65547)`; with the settle-time check bypassed, the reboot assertion fired:
   `reloaded with container_id=65541 ... slot_type=6`), GREEN after
   (`auction-restart-e2e-report.json` `sold_item_leg`: `settle_container_id =
   buyer_mail_container_id = 65547`, `reloaded_slot_type = 5`,
   `auction_orphans_post_restart = 0`).
4. **Bid refunds are mail-locked, not instant (B-bid).** Outbid money returns via
   `FinalizeForBidFail` mail subject to the 14-day mail expiry/bounce cycle —
   a griefer-adjacent lockup vector vs instant refund. Intended 1.2 behavior
   UNKNOWN (no canonical row; mail titles unverified, §3) — stays a gap.
5. **Fee schedule uncorroborated (D-fee).** 1%/duration-multiplier/cap-100g and the
   90/10 sale split are engine constants with no canonical reference counterpart
   (D7). The 90% split is E2E-pinned as *implemented*, not as *retail-correct*.
6. **Dead wire slot 0x133 (W).** `SCAuctionMessagePacket` defined, never sent.
   Either retail uses it for an unmapped event or it is vestigial — unknown.
7. **Search is memory-only, no-text-index path (A/R).** `SearchAuctionLots`
   iterates the whole `AuctionLots` dict per query; fine at current scale, no
   pagination-state persistence. No correctness gap, noted for scale.
8. **Missing-item expiry is silent to the seller (R-expire).** Pre-boot rows
   whose item is absent from `ItemManager` memory expire with a warn log and NO
   return mail (`:74-84`). Deliberate (never wedge the sweep) but the seller's
   item is unrecoverable — needs a reconcile/re-grant story.
9. **C stays U.** No live-client capture of any 0x0b7–0x0bc / 0x12e–0x133 bytes;
   bot-contract evidence drives the same engine calls the packets make but is not
   wire proof. The `H` (human feel: fee fairness, bid-war UX) dimension is
   unmeasured by construction.

## 5. Slice plan to close (not in scope for this audit)

S1 (S): cancel-leg E2E with enchanted gear — DONE (2026-09-12): proves G3 fixed
(instance-faithful return, no orphan `SlotType.Auction` row) and additionally
closed G-settle-buy (same container-orphan defect in `FinalizeForSaleBuyer`), by
routing cancel/expiry/settled-buy attachments through one
`AttachItemForReturn` helper and pinning container-id identity across a real
kill -9 reload. S2 (S): fail-closed money gates on
bid/buyout + insufficient-funds test. S3 (M): auctioneer-proximity gate (needs a
live-client or 1.2-script corroboration of the intended radius first — D4 + client
`auction` UI strings). S4 (S, optional): retire or wire 0x133.
