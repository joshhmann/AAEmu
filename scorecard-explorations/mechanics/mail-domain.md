# MAIL-01 Domain Dossier (2026-08-25 exploration)

Scorecard row at writing: W=1/A=1. Verified on `develop` @ `214bed83` (graphify corpus built from `2b4b99c0`; one mail-touching commit after graph build: `6b2f15a6d` "mail return/expiry" — all findings below are from the live tree, not the stale graph). Return-mail opcode question is owned by the **pvp-packets-dossier** sibling lane; this dossier covers everything else and only summarizes the return leg.

## Verdict: a real, mostly-wired system with two sharp edges

Player-to-player mail is end-to-end implemented (send → delay → deliver → read → take attachments/money → delete → expiry/bounce), persists as **full item instances** (not template refs) in MySQL, and is exercised by five other systems as payout transport (auction, speciality trade, housing tax, cash shop, quest rewards). The two sharpest edges:

1. **Ownership checks are missing on 4 of 5 receive-path entry points** — only `CSTakeAttachmentSequentially` verifies the mail belongs to you; plain take-item, take-money, read, and delete trust the client-supplied `mailId`.
2. **The mailbox-proximity rule is enforced for SENDING only** — receiving/reading/taking has no doodad check at all, so server-side the mailbox is effectively remote-access.

## 1. Packet inventory (excluding CSReturnMailPacket — sibling lane)

### C2G (offsets: `AAEmu.Game/Core/Packets/C2G/CSOffsets.cs`, registration: `Core/Network/Game/GameNetwork.cs:166-174`)

| Opcode | Constant | Packet | Status | Notes |
|---|---|---|---|---|
| 0x098 | `CSSendMailPacket` | `CSSendMailPacket.cs` | **Implemented** | Decodes type/receiver/title/text/3 money fields/extra + 10 (slotType,slot) pairs + mailbox doodad ObjId; enforces mailbox proximity; calls `CharacterMails.SendMailToPlayer`. |
| 0x09a | `CSListMailPacket` | `CSListMailPacket.cs` | **Implemented** | Empty struct → `OpenMailbox()` sends all headers one-per-packet. No proximity check. |
| 0x09b | `CSListMailContinuePacket` | same file | **Stub** | Logs only (`Logger.Debug("ListMailContinue")`). Pagination never needed because `OpenMailbox` sends everything at once. |
| 0x09c | `CSReadMailPacket` | same file | **Implemented, no ownership check** | Calls `ReadMail(isSent, mailId)`; any character can open any mail body by id. |
| 0x09d | `CSTakeAttachmentItemPacket` | same file | **Implemented, no ownership check** | Client echoes full item state (id/grade/flags/count/detail/creation/unsecure/unpack/slot); server discards all of it and passes only the echoed item id through to `GetAttached(mailId, false, true, false, id)`. |
| 0x09e | `CSTakeAttachmentMoneyPacket` | same file | **Implemented, no ownership check** | → `GetAttached(mailId, true, false, true)`. |
| 0x09f | `CSTakeAttachmentSequentially` | same file | **Implemented, ownership checked** | Only packet that verifies `mail.Header.ReceiverId == ActiveChar.Id` ("check for hackers trying to steal mails"). |
| 0x0a0 | `CSPayChargeMoneyPacket` | same file | **Implemented** | → `MailManager.PayChargeMoney` (tax-bill payment only). |
| 0x0a1 | `CSDeleteMailPacket` | same file | **Implemented, no ownership check** | → `DeleteMail(id, isSent)`; refuses delete while `Header.Attachments > 0`; `isSent=true` path silently does nothing (sent-tab mail can never be deleted). |
| 0xfff | `CSReturnMailPacket` | `CSReturnMailPacket.cs` | **Defined, NOT registered** — see pvp-packets-dossier lane | Offset flagged "not in the offsets"; registration commented out at `GameNetwork.cs:174`; engine-side `MailManager.ReturnMail`/expiry bounce exists and is unit-tested regardless. |

### G2C (offsets: `Core/Packets/G2C/SCOffsets.cs:270-284,325`) — all write-implemented

| Opcode | Packet | Used |
|---|---|---|
| 0x115 | `SCMailFailedPacket` (err + echoed slots + money flag) | yes, send failure path |
| 0x116 | `SCCountUnreadMailPacket` | yes |
| 0x117 | `SCMailSentPacket` (header + echoed slots) | yes |
| 0x118 | `SCGotMailPacket` (header + unread count + optional full body for Charged) | yes, delivery notification |
| 0x119 / 0x11a | `SCMailListPacket` / `SCMailListEndPacket` | yes, mailbox open |
| 0x11b | `SCMailBodyPacket` | yes, on read |
| 0x11c | `SCMailReceiverOpenedPacket` | **never constructed anywhere** (dead code) |
| 0x11d | `SCAttachmentTakenPacket` | yes — note workaround: sent **one per item** to fix client delivery glitch (`CharacterMails.cs:237-255`) |
| 0x11e | `SCChargeMoneyPaidPacket` | yes, tax payment |
| 0x11f | `SCMailDeletedPacket` | yes |
| 0x121 | `SCMailReturnedPacket` | only via legacy `BaseMail.ReturnToSender` (itself dead — see §4) |
| 0x122 | `SCMailStatusUpdatedPacket` | yes, unread→read transitions |
| 0x123 | `SCMailRemovedPacket` | **never constructed anywhere** (dead code) |
| 0x14e | `SCQuestRewardedByMailPacket` | yes, `Quest.cs:322` when overflow rewards go to mail |

## 2. MailManager wiring quality & attachment model

**Model**: every mail is a `BaseMail` (`Models/Game/Mails/BaseMail.cs`) = `MailHeader` (status, sender id/name, attachment byte count, receiver id/name, Returned flag, `Extra` payload) + `MailBody` (text, three money fields, send/recv/open dates, `List<Item> Attachments` capped at 10, `MailBody.MaxMailAttachments`). Subclasses: `MailPlayerToPlayer` (fee math + inventory→mail moves), `MailForAuction`, `MailForSpeciality`, `MailForTax`, `CommercialMail`.

**Attachment-model verdict: INSTANCE-FAITHFUL, not lossy.** Attachments are real `Item` objects moved out of the sender's bag into the sender's `Inventory.MailAttachments` container (`SlotType.Mail = 5`) via `AddOrMoveExistingItem` (`MailPlayerToPlayer.FinalizeAttachments`, `MailPlayerToPlayer.cs:80-101`) — the *same* object identity travels, preserving grade, flags, count, bound state, creation time, UCC, and the `details` blob (56-byte Equipment detail incl. enchant/durability; `Item.ReadDetails`/`WriteDetails`, `Item.cs:255-351`). Persistence is by item-id reference column pair (`mails.attachment0..9` → rows in `items`), not serialization into the mail row. System-side created items (auction payouts, GM/WebApi mails) are freshly minted instances with `OwnerId` retargeted before first save — also instance-faithful going forward.

**Fee math** (`MailPlayerToPlayer.GetMailFee`, `MailPlayerToPlayer.cs:22-49`): base Normal=50 copper (+30 per attachment beyond 1 free), Express=100 (+80 each); money attachment counts as an attachment for fee purposes; fee + attached money is withdrawn together only after successful send (`CharacterMails.SendMailToPlayer:106-124`); insufficient funds → `MailResult.InsufficientCoins`. Delivery latency: `MailType.Normal` gets `RecvDate = now + 30 min` (`NormalMailDelay`, tunable at runtime via `/settradepackmaildelay`); Express/SysExpress deliver instantly. GM command `/testmail <type> ...` mints dummy mails of any of the 44 `MailType` values incl. the `.sellBackpack` body-format examples (`Scripts/Commands/TestMails.cs:32-36`).

**Wiring quality**: good overall — DI-constructed manager (`MailManager.cs:20`), dirty-flag based persistence hooked into `SaveManager` transactional batch (`SaveManager.cs:117-121`), delivery+expiry tick scheduled every 5s after load (`MailManager.cs:210-211`). Weak spots: `Load()` is raw `SELECT *` with per-row warn on orphaned item ids but **no cleanup** of them (orphaned attachment ids permanently inflate nothing but log spam — count is recomputed from reality, which is good); `BaseMail.CanReturnMail` requires `IsDelivered == false` while `MailManager.ReturnMail` requires `Status == Read` (post-delivery), so `CanReturnMail` can never be true on the manager's path — the two return policies contradict; `BaseMail.ReturnToSender` (`BaseMail.cs:56-81`) still exists as the old, validation-free implementation that nothing calls (should be deleted once sibling lane settles).

## 3. Persistence schema audit (MySQL aaemu_game)

`mails` table (`SQL/aaemu_game.sql:354-384`): `id` PK, `type`, `status`, `title`/`text` TEXT (widened 2022-09-22), `sender_id`/`sender_name`, `receiver_id`/`receiver_name`, `attachment_count`, `open_date`/`send_date`/`received_date`, `returned`, `extra` bigint, `money_amount_1..3`, `attachment0..9` bigint item-id columns (2020-05-10 update dropped the old `mails_items` blob table in favor of references).

What survives restart:
- **Mail row itself** (type/status/text/money/dates/returned/extra): yes — `REPLACE INTO mails` on dirty, `DELETE` via `_deletedMailIds` inside the SaveManager transaction (`MailManager.Save:214-294`).
- **Item attachments incl. enchant/durability/grade/binding**: yes — items live as ordinary rows in `items` (`slot_type=5`, `owner`, `details` blob written via `WriteDetails`, `ItemManager.cs:1617-1640`); `ItemManager.Load` bulk-reads ALL items at startup (`SELECT * FROM items`, `ItemManager.cs:1823`) and `MailManager.Load` re-links by id (`itemManager.GetItemByItemId`, `MailManager.cs:169-183`).
- **Read state**: yes (`status` column; `OpenDate` set on read, persisted).
- **Expiry timers**: no dedicated timer column — derived at runtime from `received_date - now >= MailExpireDelay (14d)` (`CheckAllMailTimings`, `MailManager.cs:494-500`), so expiry survives restart implicitly. `MailExpireDelay` comment claims retail default may be 30 days (`MailManager.cs:35`).
- **In-memory only**: `_deletedMailIds` recycling set (rebuilt trivially), `IsDelivered` flags (recomputed from `RecvDate <= now`, `MailManager.cs:197`).
- **Gap**: crash between send and periodic save loses the mail AND orphans the item in sender's `MailAttachments` container (recovered as sender-owned mail-slot item on next boot — item not lost, mail is). Save cadence is SaveManager's global tick, standard R-dimension exposure shared with inventory.
- **Gap**: `attachment_count` vs actual attachments mismatch only warns (`MailManager.cs:191-192`), never repairs the header until recount overwrites it (it does self-heal on line 194).

## 4. Behavioral contract per leg

- **Send with attachments**: client must target a doodad whose current funcs include `DoodadFuncNaviOpenMailbox` within 5m (`CSSendMailPacket`; group-based check deliberately avoided since some mailboxes are Housing-Furniture group). Items verified against sender inventory (`PrepareAttachmentItems` rejects non-inventory slots → `InvalidSlot`), fee computed, items moved to `MailAttachments` container, receiver existence verified twice (NameManager lookup + `Send()` re-verification `MailManager.cs:58-70`), then `SCMailSentPacket` + money debit. Failure states surface as `SCMailFailedPacket(MailResult)` or error messages (`MailFailMailboxNotFound`).
- **Receive/read**: `CSListMail` dumps headers (sent-tab = your senderId, received-tab = your receiverId; self-addressed mail appears in both, `OpenMailbox:31-45`). Undelivered Normal mail becomes visible only after `RecvDate`; online receivers get push `SCGotMailPacket` from either `NotifyNewMailByNameIfOnline` at send-time or the 5s sweep. Reading flips Unread→Read, stamps OpenDate, decrements unread counters. **No ownership or proximity check** on read.
- **Take attachments**: money first (flat add to copper; auction-money takes cost 1 Commerce labor, `GetAttached:139-150`), then items — per-item free-space/stack check with graceful `BagFull` error and partial take preserved (`GetAttached:173-215`); taken items keep identity (stack merges move counts). Attachment counter decremented; mail auto-marks Read on first take. Empty-bag edge is handled correctly (res=false, mail intact).
- **Sender copy/delete**: there is NO sender-side copy — the sent tab reads the same single mail object (`GetCurrentMailList` includes mails where you're sender). `DeleteMail` refuses while any attachment remains and ignores `isSent=true`, so a sent-but-unclaimed mail cannot be removed by its sender.
- **Money-only mail**: works; counts as 1 attachment for fee; `AttachMoney` + zero item slots.
- **Charged (COD) mail**: type exists, `SCGotMailPacket` carries the body for it, but there is **no COD enforcement anywhere in `GetAttached`** — attachments/money are free to take; only tax `Billing` mail has a payment gate (`PayChargeMoney`, tax-certificate-first with gold fallback, house-id packed into `Header.Extra` bits 0-31/48-63).
- **Expired bounce** (rig-tested in commit `6b2f15a6d`; summarized, not redone — tests `AAEmu.UnitTests/Game/Core/Managers/MailReturnTests.cs`): delivered+unclaimed past 14 days → player Normal/Express mail bounces to original sender with ALL attachments and money intact (`ProcessExpiredMail` → `BounceMailToOriginalSender`, ownership retargeted, `Returned=true` prevents second bounce); system mail or second-time-expired mail is destroyed with attachments trashed (`trashItems: true` removes them from their container). Tests cover: bounce-intact, double-return refused, unread-return refused, non-owner return refused, expiry bounce, system-mail destruction, retention-window pass-through.

## 5. Mass mail / guild mail

1.2 had no client-initiated guild mail UI, and none is wired here. Server/admin mass mail exists exclusively through WebApi `POST /api/mail/send` (`Services/WebApi/Controllers/MailController.cs:17-225`): recipients can be Character list, Expedition members, Family members, All-online, or All-characters (raw SQL over `characters`), with money/billing/item-template attachments minted per recipient. `QuestActObjSendMail` lives under `Quests/UnusedActs/` and is loaded as data only — quest overflow rewards instead flow through `CreateQuestRewardMails` (`MailManager.cs:653-706`) chunked into ≤10-attachment SysExpress mails from `.questReward`.

## 6. Cross-references to other lanes

- **AUCTION**: `MailForAuction` pays sellers (buyout minus recalculated fee) and buyers (won item as attachment), fail/cancel refunds — `AuctionManager.cs:47-52,96-99,139-141,174-176,190-192`. Auction money take costs 1 labor (§4).
- **PACK/speciality**: `MailForSpeciality` dual-mails seller + crafter shares with `.body(...)` formatted text — `SpecialtyManager.cs:324-339`.
- **HOUSING/TAX**: `MailForTax` weekly bills; paid via `PayChargeMoney`; demolition returns design/furniture/tax certs as mail attachments (`HousingManager.cs:1099-1110,1199-1353`).
- **CASH SHOP**: `CommercialMail` gifting/refunds — `CashShopBuyTask.cs:264`.
- **QUESTS**: reward-by-mail overflow + `SCQuestRewardedByMailPacket` (`Quest.cs:313-322`).
- **RETURN OPCODE**: owned by `pvp-packets-dossier` (running); local facts for them: offset placeholder `0xfff` flagged TODO, registration commented out at `GameNetwork.cs:174`, engine path `MailManager.ReturnMail` fully validated+tested, legacy duplicate `BaseMail.ReturnToSender` dead.

## Gaps (why A=1 was fair)

1. **Security**: read/take-money/take-item/delete lack `ReceiverId` checks (§1) — any authenticated character who learns/guesses a mail id can loot someone else's mail. Sequential-take shows the intended pattern; the other four packets predate it.
2. **No COD**: Charged mail type advertised to client but never charges.
3. **Dead code**: `SCMailReceiverOpenedPacket`, `SCMailRemovedPacket` never sent; `BaseMail.ReturnToSender` contradicts `MailManager.ReturnMail` semantics.
4. **Sent tab unmanageable**: cannot delete sent entries (`isSent=true` no-op).
5. Crash window mail-vs-items save atomicity is shared-SaveManager-level, plus orphaned attachment ids only warn.

## Sized slice plan to reach A=2/R=2

**S1 (S) — Ownership hardening.** Add `ReceiverId == ActiveChar.Id` guard to `ReadMail`, `GetAttached`, and `DeleteMail` paths (mirror `CSTakeAttachmentSequentially`). PASS: unit test — non-owner read/take/delete all refused with `ErrorMessageType.MailInvalid`; owner flows unchanged; existing MailTests/MailReturnTests stay green.

**S2 (S) — Sent-tab deletion.** Honor `isSent=true`: allow sender to purge a mail whose attachments were all claimed OR whose receiver no longer holds claim rights; send `SCMailDeletedPacket(isSent=true,...)`. PASS: E2E bot test deletes own sent mail after receiver drains it; refusal while attachments remain.

**S3 (M) — Attach-item mail E2E across restart (the headline slice).** Bot A near a real mailbox doodad sends enchanted/equipment item + copper to bot B via `CSSendMailPacket`; server restarts; B logs in, lists, opens, takes. PASS assertions: item instance identical post-restart — same item id, grade, count, `details` blob decoded enchant/durability equal, `SlotType.Mail=5` in DB between restart legs; copper exact; fee (50+30n or express schedule) debited from A within ±0; unread counter correct; `SCAttachmentTakenPacket` per-item observed; mail deletable afterwards.

**S4 (M) — Expiry/bounce E2E (promote rig test to integration).** With `NormalMailDelay`/`MailExpireDelay` shrunk via the existing static setters in a test host: unclaimed player mail bounces to sender with attachments intact after restart-spanning window; already-returned mail is destroyed with attachment rows removed from `items`. PASS: DB-level assertion that bounced mail has swapped ids, `returned=1`, and destroyed mail leaves zero orphaned `attachmentN` references.

**S5 (S, optional QoL) — COD enforcement or feature-flag Charged off.** Either gate `GetAttached` on payment like `PayChargeMoney`, or map incoming Charged sends to SysExpress with a logged deviation. PASS: documented choice + test proving attachments unreachable without payment (or that Charged never reaches the wire).

Sequencing: S1+S2 independent, land first (security); S3 is the A=2 anchor; S4 converts the existing unit-rig into restart-proof evidence (R=2); S5 discretionary.
