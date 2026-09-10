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

---

## Addendum A1 (2026-08-25, later) — Client mail UI mined: return button + fee constants

Source: `game/scriptsbin/x2ui/mailbox/**` from the deployed 1.2 `game_pak` — Lua 5.1 bytecode (`.alb`), decompiled with unluac; evidence at `/root/aaemu-pak-lua/dec/x2ui/mailbox/`.

**Return button handler VERIFIED** (`dec/x2ui/mailbox/mail/read_mail.lua:991-1009`, mirrored in `comercialmail/read_mia_mail.lua`): `returnButton:OnClick` → confirmation dialog ("return_title"/"return_content") → `X2Mail:ReturnMailById(window.mailId)`. Return is offered only for readable sender mails (`returnButton:Enable(not isMySelf)` paths, lines 65-96). The opcode itself is native-bound (x2game.dll strings are obfuscated — ASCII and UTF-16 sweeps found nothing), but slot arithmetic pins it: the full `X2Mail:*` send-API set used by the UI enumerates 1:1 onto AAEmu's contiguous C2S mail block — Send=0x098, List/ListContinue=0x09a/b, Read=0x09c, TakeItem=0x09d, TakeMoney=0x09e, TakeSequentially=0x09f, PayChargeMoney=0x0a0, Delete=0x0a1, ReportSpam=0x0a3. Every operation has a known opcode except Return, and the only gap in the block is **0x0a2**. The stale comment at `GameNetwork.cs:174` proposing 0x0a1 collides with `CSDeleteMailPacket=0x0a1`. Grade: **STRONGLY_INFERRED 0x0a2** — wire `CSReturnMailPacket` there.

**Fee constants VERIFIED** (`mailbox/mail/write_mail.lua:125-134`): normal mail = **50 copper + 30 per attachment**; express = **100 + 80 per attachment** — matches the fee schedule already assumed in S3. `MAX_ATTACHMENT_COUNT = 10` (`mailbox/mail/common.lua:2`). `X2Mail:SendMail(mailInfo)` payload fields: `receiver, title, text, gold, silver, copper, doodadId, type, withReceiver` (`write_mail.lua:91-103`) — consistent with `CSSendMailPacket`'s field order expectations. No client-side delay/expiry constants exist (server-owned), supporting the dossier's server-side delay model.

---

## Addendum A2 (2026-09-10) — MAIL-01 canonical 1.2 audit (B7)

Audit HEAD: `402042ae7ae561fe080cfea78eeb24408d46b437` (develop). Canonical DB md5
`78b3bdbf038db3b927056106efdf91af` (verified `md5sum` this audit). C stays U: no
live-client capture pins any mail wire bytes; all wire claims are code-read against
1.2 offsets. **Do NOT edit SCORECARD.md per this audit** (grade flips are a separate
ruling); row at writing still reads W=2/A=2/R=2/C=U on the S3 acceptance.

### A2.1 Required data (canonical DB, read-only MCP provenance)

MCP source for every row below: `source_id=compact.sqlite3`,
`path=/root/aaemu-dev/AAEmu.Game/Data/compact.sqlite3`, `version=1.2 r208022`
(`list_sources`; query window `generated_at=2026-09-10T10:43:15Z`–`10:43:48Z`).

| # | Claim | Tool / input | Result |
|---|---|---|---|
| M-D1 | Mail-adjacent reference tables | `query_sql`: `sqlite_master` `instr(name,'mail')` | 7 tables: `doodad_func_navi_open_mailboxes`, `quest_act_obj_send_mails`, `quest_mail_attachment_items`, `quest_mail_attachments`, `quest_mail_sends`, `quest_mails`, `sphere_quest_mails` |
| M-D2 | Mailbox doodad funcs (send-proximity basis) | `query_sql`: `SELECT * FROM doodad_func_navi_open_mailboxes` | 7 rows (ids 1–7); `duration` is 0 or 1800 — presence/channel-time shape, NOT coordinates; the 5m rule itself is engine-side (`CSSendMailPacket`), uncorroborated |
| M-D3 | Quest-mail template chain | `query_sql`: `SELECT * FROM quest_mails LIMIT 5` + `sql FROM sqlite_master WHERE name='quest_mails'` + counts | n=1 `quest_mails` row (FK `npc_id→npcs(id)`, `quest_mail_attachment_id→quest_mail_attachments(id)` — declared FKs, `exact` linkage); n=2 `quest_mail_attachments` rows (both named "테스트"/test). Engine-side quest-overflow mail (`MailManager.CreateQuestRewardMails`, `SysExpress`) does NOT consume these rows — `QuestActObjSendMail` stays under `Quests/UnusedActs/` (data only). So: reference quest-mail content exists but is unused by the live path |
| M-D4 | Canonical item rows for the S3 flow | `lookup_row items 5318` + `query_sql SELECT ... WHERE id IN (10000,5318)` | 5318 = sellable, max_stack 1, auction cats (1,1,1); `items` n=21482 |
| M-D5 | No fee/delay/expiry reference rows | negative: no mail-fee/delay table among M-D1; A1 client-side finding (no delay/expiry constants in `x2ui/mailbox`) | Normal 30-min delay, 14-day expiry, 50+30n / 100+80n fee schedule are engine+client-UI constants only — implemented and E2E-pinned, never reference-proven |

Mutable persistence (provenance: repo file @ HEAD, not MCP): `mails` table
`SQL/aaemu_game.sql:354-384` (unchanged since the 2026-08-25 audit §3 — id/type/
status/title/text/sender+receiver ids+names/attachment_count/3 dates/returned/extra/
3 money fields/`attachment0..9` item-id refs). Attachments persist as full `items`
rows under `SlotType.Mail=5`.

### A2.2 S3 flow restatement (W/A/R=2 basis, no re-run — READ-ONLY audit)

`AAEmu.IntegrationTests/E2e/MailS3RestartE2eTests.cs`
`Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets` (commit `31045d033`,
PASS 1/1, 2m39s): sender rigs equipment template 5318 (grade 3, durability 77,
rune 1234, temper 3/4) + 1234 copper → REAL `CSSendMailPacket` near a mailbox →
kill -9/restart → receiver over an authenticated link uses `CSListMail`/`CSReadMail`/
`CSTakeAttachmentSequentially`/`CSDeleteMail`. Assertions: instance-faithful item
(grade/durability/rune/temper/`details` blob), exact copper, Normal fee 50+30 debited,
`SlotType.Mail=5` in DB across the restart, unread recount 1, per-item
`SCAttachmentTakenPacket`, mail deletable afterwards. This is the W/A/R=2 anchor for
send/attach/receive/take/persist.

### A2.3 What changed since the 2026-08-25 baseline (§§1–6)

1. **0x0a2 is now REGISTERED (was "defined, NOT registered").** `CSOffsets.cs:164`
   still flags `CSReturnMailPacket = 0x0a2` as STRONGLY_INFERRED with the same slot-
   arithmetic evidence chain, but `GameNetwork.cs:174-177` now REGISTERS it
   (`RegisterPacket(CSReturnMailPacket, 1, typeof(CSReturnMailPacket))`, with a
   do-not-move-to-0x0a1 guard comment). `CSReturnMailPacket.Read` reads one int64
   mailId → `ActiveChar.Mails.ReturnMail(mailId)` (`CSReturnMailPacket.cs:11-19`).
   The opcode value itself REMAINS an inference (no client capture) — see gap G1.
2. **Return path hardened (was "engine-side ReturnMail validated+tested").**
   `MailManager.ReturnMail` (`MailManager.cs:357-401`) is now fail-closed: unknown
   mail → `MailNotFound`; non-receiver → `MailNotAllowedToReturn` + warn; unread →
   refused; already-`Returned` → refused; sender must still resolve via `NameManager`
   or the bounce refuses (`BounceMailToOriginalSender`, `:408-433`); interactive
   return emits `SCMailReturnedPacket`, expiry-bounce stays silent. This narrows —
   but does NOT close — the §1 ownership finding: read/take-item/take-money/delete
   still lack `ReceiverId` checks (only SequentialTake + now ReturnMail check).
3. **Packet inventory otherwise unchanged** (§§1–2, 6 verified current): dead
   `SCMailReceiverOpenedPacket` / `SCMailRemovedPacket` still never constructed;
   `CSListMailContinuePacket` still a stub; `BaseMail.ReturnToSender` legacy path
   still present alongside `MailManager.ReturnMail`.

### A2.4 Explicit gap list (canonical-audit view: send/attach/return/expire/persist + COD)

1. **G1 — Return opcode 0x0a2 STRONGLY_INFERRED, never inferred closed.** Value rests
   on UI `X2Mail:ReturnMailById` + contiguous-block slot arithmetic (A1) — the only
   free slot between Delete=0x0a1 and ReportSpam=0x0a3. Now registered and E2E/rig
   exercised, but a live-client capture is still required before VERIFIED. Per task
   rule this MUST stay a gap.
2. **G2 — COD unenforced (unchanged).** `Charged` mail type advertised; `GetAttached`
   hands over attachments/money with no payment gate. Only tax `Billing` mail gates
   via `PayChargeMoney`. Fix-or-flag choice from §S5 still open.
3. **G3 — Ownership checks missing on 4 receive paths (narrowed, not closed).**
   Read / take-item / take-money / delete trust client `mailId`; SequentialTake and
   ReturnMail now check. S1 slice (mirror the check into the other four) still stands.
4. **G4 — Expiry/bounce gaps.** (a) Expiry derived from `received_date` + 14d engine
   constant (M-D5: no canonical row; code comment itself suggests retail may be 30d —
   value unproven). (b) System-mail/second-expiry destruction trashes attachments
   (`trashItems: true` deletes `items` rows) — no seller/owner recourse, no GM restore
   path. (c) Crash between send and `SaveManager` tick loses the mail (item survives
   as sender-owned `SlotType.Mail` row — recoverable but orphaned from any header).
   (d) No promotion of the rig-tested bounce to a restart-spanning E2E (S4 slice open).
5. **G5 — Sent-tab unmanageable (unchanged).** `isSent=true` delete no-op; sender
   cannot purge sent-but-unclaimed mail. S2 slice open.
6. **G6 — Mailbox proximity is send-only (unchanged).** Receiving/reading/taking has
   no doodad check (M-D2: func rows exist but encode no radius). Server-side mailbox
   is effectively remote-access for reads.
7. **G7 — Quest-mail reference chain unused (new).** M-D3: canonical `quest_mails`
   (+2 attachments, FK-exact) exists but the live quest-overflow path mints
   `SysExpress` mails from code instead. Either the reference chain should drive the
   content or the divergence should be documented as intentional.
8. **G8 — C stays U; H UNKNOWN.** No live-client bytes for any mail opcode; no human
   mailbox session. S3 is bot-contract + real-packet evidence, not human feel.
