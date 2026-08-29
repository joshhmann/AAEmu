# Mail return opcode `0x0a2` — evidence boundary & COD/expiry lifecycle gaps (2026-08-29)

Docs-only evidence note. No code/data modified. Worktree: `.worktrees/mail-return-archaeology` @ `22e02d3d2a98f81a02f15976bcc11b488b142abf` (origin/develop HEAD). Scope: the C2S mail-return opcode question owned by the pvp-packets-dossier lane, plus the COD/expiry lifecycle gaps flagged in `scorecard-explorations/mechanics/mail-domain.md` (§4, Gaps 2/5, S4/S5).

## 1. Verdict: `0x0a2` is STRONGLY_INFERRED (not VERIFIED, not PLAUSIBLE-only)

| Claim | Grade | Evidence |
|---|---|---|
| The 1.2 client has a Return-mail C2S opcode (existence) | **VERIFIED** | Decompiled 1.2 `game_pak` Lua: `x2ui/mailbox/mail/read_mail.lua:991-1009` — `returnButton:OnClick` → confirm dialog → `X2Mail:ReturnMailById(window.mailId)`; mirrored at `x2ui/mailbox/comercialmail/read_mia_mail.lua:137` (`X2MiaMail:ReturnMailById`). Server-side ack exists: `SCMailReturnedPacket = 0x121` (`SCOffsets.cs:282`), sent by `MailManager.ReturnMail` (`MailManager.cs:400`). Later clients both define the packet: 3.0.3.0 r330995 `CSReturnMailPacket = 0x0B7`, 8.0.3.12 r558734 `CSReturnMailPacket = 0x150` (git history `742fb0bb0`, `3e55d80f3`). |
| The opcode is `0x0a2` | **STRONGLY_INFERRED** | 1.2 r208022 C2S mail block is contiguous and fully assigned except two holes: `0x098` Send, `0x09a` List, `0x09b` ListContinue, `0x09c` Read, `0x09d` TakeItem, `0x09e` TakeMoney, `0x09f` TakeSequentially, `0x0a0` PayChargeMoney, `0x0a1` Delete, `0x0a3` ReportSpam (`CSOffsets.cs:146-156`; upstream AAEmu develop byte-identical, re-read 2026-08-29). `0x0a2` is the only free slot **between Delete and ReportSpam** — the exact position a Return opcode occupies in the operation ordering. The stale upstream guess `//public const ushort CSReturnMailPacket = 0x0a1` (`CSOffsets.cs:157` pre-`531a732fe`) collides with `CSDeleteMailPacket = 0x0a1` and is dead. Community list (forum.zone-game.info tid=29159) attributes return to `0x0a1`, but its mail block ordering contradicts 1.2 (its SendMail=0xB8 vs 1.2's 0x098) — it describes a later build and cannot be cited (AGENTS.md: do not invent opcodes). |
| Payload = single int64 `mailId` | **STRONGLY_INFERRED** | Identical payload in both later clients (`stream.ReadInt64()` in 3.0.3.0/8.0.3.12 `CSReturnMailPacket.cs`); consistent with sibling 1.2 mail packets (Delete/ReportSpam/Spam all read int64 mailId). |
| `0x0a2` is unassigned in the maintained 1.2 table | **VERIFIED (absence)** | No constant, no registration, no comment names `0x0a2` anywhere in `CSOffsets.cs`/`GameNetwork.cs` before `531a732fe`; `GameNetwork.cs:174-177` now registers `CSReturnMailPacket` there with an explicit STRONGLY_INFERRED comment. |
| Client Return-button gating | **VERIFIED (client)** | `read_mail.lua:75-99`: received user mail → `returnButton:Enable(not isMySelf)`; sent tab → `returnButton:Enable(false)`. No read-state gate in the Lua — the client may offer Return on unread mail, which the server refuses (`MailManager.ReturnMail` requires `Status == Read`). |
| Server return semantics | **VERIFIED (code + unit tests)** | `MailManager.ReturnMail` (`MailManager.cs:360-409`): receiver-only, `Status == Read`, once-only (`Returned` flag), bounce swaps sender/receiver with attachments intact, `SCMailReturnedPacket` to the returner. `MailReturnTests` (7 cases) + `MailOwnershipGuardTests` (packet-level roundtrip) cover bounce-intact, double-return refused, unread refused, non-owner refused, expiry bounce, system-mail destruction, retention pass-through. |

**Honest caveat on "only free slot":** `0x099` is *also* unassigned and sits inside the mail block (between Send `0x098` and List `0x09a`; `0x095`/`0x097` are unassigned too, just outside it). The slot-arithmetic argument is therefore "the only free slot between Delete and ReportSpam", not "the only free slot in the block". `0x0a2` remains the best candidate because it matches the operation ordering and the 2019-era registration order (Delete → ReportSpam → Return at `0x09f/0x0a0/0x0a1` in `8f3d7a3d1`), but only a capture can rule out `0x099` or a non-adjacent assignment.

**Why not PLAUSIBLE:** the existence of the opcode is client-UI-verified, the payload shape is cross-version-verified, and the candidate slot is uniquely positioned in the 1.2 table — three independent strands. **Why not VERIFIED:** no client offset dump names `0x0a2`; `x2game.dll` is obfuscated (ASCII/UTF-16 string sweeps found nothing — mail-domain.md Addendum A1); no `.pcap`/client packet log exists on this host (searched `/root/aaemu-dev`, `/root/aaemu-pak-lua`, `/tmp`); the W4-1 QAT designed as the confirmation experiment (`Docs/JOSH-QAT-WAVE4.md:62-100`) has no recorded verdict — `EVIDENCE-LEDGER.md` contains no `0x0a2`/W4-1 entry.

## 2. Exact evidence that closes `0x0a2` → VERIFIED

**Primary (capture):** run a real 1.2 r208022 client against a server ≥ `531a732fe` (W4-1 setup: A sends item+copper to B, B reads, B clicks Return, confirms dialog) while capturing the game-port traffic (tcpdump on the client host, or a client-side packet logger if the 1.2 client exposes one). The capture must show, in order:

1. **C2S frame on Return click**: opcode byte == `0x0a2` (or the actual value — report, don't debug, if different) and payload == int64 `mailId` (8 bytes, little-endian, matching the mail id the client holds).
2. **S2C ack**: `SCMailReturnedPacket` `0x121` to the returner (server already sends it; the capture confirms the client accepts it — no error popup, mail leaves the box).
3. **Negative controls**: (a) Return on *unread* mail — does the client even send? (server refuses with `MailNotAllowedToReturn`); (b) Return on *self-addressed* mail — client disables the button (`not isMySelf`), so no frame should appear; (c) double-return — second click refused server-side.
4. **Sent-tab delete mismatch** (same capture session): the client enables Delete on the sent tab (`read_mail.lua:93-96` `deleteButton:Enable(true)`) but the server silently ignores `isSent=true` (`DeleteMail` no-op). Capture the sent-tab delete frame (`0x0a1` + int64 + bool `isSent=true`) and the client's expectation (does it expect `SCMailDeletedPacket`?).

**Alternative (offset dump):** a runtime memory dump of the client's opcode dispatch table, or symbol recovery in a non-obfuscated 1.2 client build, naming `0x0a2` = ReturnMail. Any named source beats slot arithmetic.

**Disposition rules:** capture shows `0x0a2` + int64 → flip comments to VERIFIED, keep registration. Capture shows a different opcode → move `CSOffsets.CSReturnMailPacket` + `GameNetwork` registration to the observed value (two-line change; payload/logic already implemented). Capture shows **no frame** on Return click → the 1.2 client lacks a wired return (button dead or feature absent) → revert registration to `0xfff` and close the client-facing return path as N/A-canonical, leaving the rig-tested bounce/expiry logic as the whole story (economy-domain.md §4 branch).

## 3. COD / expiry lifecycle: states & timers in code/data, test coverage, capture requirements

### 3.1 What exists in code/data

| Element | Code/data state | Notes |
|---|---|---|
| `MailStatus` enum | `Unread=0, Read=1, Unpaid=2` (`MailStatus.cs`) | `Unpaid` is set **only** by `MailForTax` (`MailForTax.cs:82`, Billing mail). `PayChargeMoney` (`MailManager.cs:513-610`) transitions Unpaid→Read, then deletes the mail. No other code path sets or consumes `Unpaid`. |
| `MailType.Charged = 9` | `CommercialMail` (cash shop) only (`CommercialMail.cs:39`); `SCGotMailPacket` carries body for Charged (`MailManager.cs:316,335`); unread counter buckets Charged+Promotion as Commercial (`CountUnreadMail.cs:30`) | **No payment gate anywhere in `GetAttached`** (`CharacterMails.cs:146-260`) — attachments/money are free to take. Only `MailType.Billing` has a payment gate. **Key finding: the 1.2 client Lua `MAIL_TYPE` enum has NO `Charged = 9` entry** (`common.lua:6-26`: 0-8, 13-17, 19, 21, 23-25, TYPE_MAX=26) and `write_mail.lua` offers only Normal/Express — there is no COD send UI in 1.2. "Charged" in 1.2 = cash-shop (Mia) mail, which the client UI treats as free-to-take (`read_mia_mail.lua` has no pay button; only return/delete/reply). The dossier's "COD enforcement" gap (S5) is therefore a **server-side-only question**: the server advertises Charged but never charges, and no 1.2 client flow is known to expect a charge. |
| Delivery delay | `NormalMailDelay = 30 min` (`MailManager.cs:34`); Normal mail `RecvDate = now + delay` (`CharacterMails.cs:130`); Express/SysExpress instant | Tunable at runtime via BotDriveBridge `delay` op (`BotDriveBridge.cs:2060`). |
| Delivery sweep | `MailDeliveryTask` every 5 s after load (`MailManager.cs:210-211`, `MailDeliveryTask.cs`) → `CheckAllMailTimings` (`MailManager.cs:492-508`) | Delivers due mails (`NotifyNewMailByNameIfOnline`) and expires old ones. |
| Expiry window | `MailExpireDelay = 14 days` (`MailManager.cs:35`, comment: "Default is 30 days ?") | Derived at runtime from `received_date - now >= MailExpireDelay`; **no dedicated timer column** in `mails` (`SQL/aaemu_game.sql:354-384`) — survives restart implicitly. Client Lua has no expiry constants (mail-domain.md A1). |
| Expiry fate | `ProcessExpiredMail` (`MailManager.cs:468-490`): delivered+unclaimed P2P Normal/Express → `BounceMailToOriginalSender` (attachments intact, `Returned=true` prevents second bounce); system mail (`SenderId=0`) or already-returned → `DeleteMail(trashItems:true)` destroys attachments | `BounceMailToOriginalSender` (`MailManager.cs:411-465`) re-verifies the original sender resolves, swaps roles, retargets attachment `OwnerId`, reuses `Send()`. |
| Manual return | `MailManager.ReturnMail` (`MailManager.cs:360-409`): receiver-only, `Status == Read`, once-only, bounce intact, `SCMailReturnedPacket` | Contradiction: `BaseMail.CanReturnMail` requires `IsDelivered == false` while the manager requires `Status == Read` (post-delivery) — the two policies can never both hold; `BaseMail.ReturnToSender` (`BaseMail.cs:56-81`) is dead code (nothing calls it). |
| Sent-tab delete | `DeleteMail(id, isSent)` — `isSent=true` silently does nothing (mail-domain.md §4) | Client enables Delete on sent tab (see §2.4) — client/server mismatch. |

### 3.2 Test coverage matrix

| Lifecycle leg | Unit rig | Integration (real packets) | Gap |
|---|---|---|---|
| Send → fee → delay → deliver | `MailTests` (money, player-not-found) | `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets` (PASS 1/1, 2m39s, `31045d033`): real `CSSendMailPacket`, mailbox proximity, equipment+copper, kill-9/restart, `SlotType.Mail=5`, unread recount, read transition, sequential take, delete persistence | Delivery *timing* (30-min window) not asserted — E2E used the `delay` tuning op |
| Read / take / delete ownership | `MailOwnershipGuardTests` (packet-level, 10+ cases) | S3 E2E (owner flows) | — |
| Manual return (server semantics) | `MailReturnTests` 7 cases + `MailOwnershipGuardTests` return roundtrip | **none** | Real-client opcode + ack (W4-1) |
| Expiry bounce / destruction | `MailReturnTests` (in-memory `CheckAllMailTimings` calls) | **none** | Restart-spanning expiry (dossier S4): shrink `MailExpireDelay`, restart, assert DB-level `returned=1`, swapped ids, attachment rows intact; destroyed mail leaves zero orphaned `attachmentN` refs |
| `Unpaid` lifecycle (tax) | **none** — no test references `PayChargeMoney`/`MailForTax`/`Unpaid` | **none** | Unit tests for cert-first payment, gold fallback, Unpaid→Read→delete |
| Charged/COD | **none** | **none** | Decision + capture: does any 1.2 client flow send `0x0a0` outside Billing? (Client Lua says no COD UI exists) |
| Sent-tab delete | **none** | **none** | Client sends `0x0a1`+`isSent=true`; server no-ops — capture the frame, then decide server behavior |
| Retail expiry constant | — | — | `MailExpireDelay` "30 days ?" needs a retail source (patch notes/wiki), not a capture |

### 3.3 What a real-client capture must prove (beyond §2)

1. **Expiry while mailbox open**: with `MailExpireDelay` shrunk, does the client accept `SCMailDeletedPacket` (`0x11f`, what the server sends via `NotifyDeleteMailByNameIfOnline`) for an expired mail, or does it expect the never-sent `SCMailRemovedPacket` (`0x123`, dead code)? This decides whether `SCMailRemovedPacket` needs wiring.
2. **`0x0a0` usage**: capture every `CSPayChargeMoneyPacket` emission — confirm it only fires for Billing (tax) mail, proving no COD flow exists in 1.2.
3. **Return on unread mail**: whether the client sends the return frame for unread mail (server refuses) — determines if the client gates on read state internally.
4. **Returned-mail re-delivery**: after a return, the original sender should get `SCGotMailPacket` (`0x118`) — capture confirms the bounce notification path end-to-end.

## 4. Next actions (exact, ordered)

| # | Action | Acceptance | Owner |
|---|---|---|---|
| N1 | Run W4-1 QAT (`Docs/JOSH-QAT-WAVE4.md:62-100`) with a real 1.2 client + game-port capture; record R1-R4 + console capture on FAIL | Capture shows C2S `0x0a2` + int64 mailId and S2C `0x121`; verdict sheet filled; EVIDENCE-LEDGER class-7 entry | Josh (client) / lane |
| N2 | If N1 opcode ≠ `0x0a2`: move `CSOffsets.CSReturnMailPacket` + `GameNetwork` registration to observed value; if no frame: revert to `0xfff` | Diff-checked, committed, comments updated to VERIFIED or N/A | lane |
| N3 | Promote expiry to integration (dossier S4): `MailExpireDelay` shrink + restart-spanning bounce/destruction with DB-level assertions | New E2E PASS; `returned=1`, swapped ids, zero orphaned `attachmentN` refs | lane |
| N4 | Add `Unpaid` lifecycle unit tests (`MailForTax` → `PayChargeMoney`: cert-first, gold fallback, Unpaid→Read→delete) | Tests PASS; currently zero coverage | lane |
| N5 | Record the Charged/COD decision: 1.2 client has no COD UI (MAIL_TYPE lacks 9; write_mail offers Normal/Express only) — keep Charged as cash-shop-only, document that "COD enforcement" is N/A-canonical unless a capture shows otherwise | Decision note in mail-domain.md; no code change | lane |
| N6 | Capture sent-tab delete frame (`0x0a1` + `isSent=true`) and decide server behavior (currently silent no-op) | Capture + decision recorded | lane |
| N7 | Resolve `MailExpireDelay` retail constant ("30 days ?") from a retail-era source | Source cited; constant comment updated if warranted | lane |

## 5. Sources (all read from the worktree @ `22e02d3d2` unless noted)

- `AAEmu.Game/Core/Packets/C2G/CSOffsets.cs:146-164` (1.2 mail block; STRONGLY_INFERRED comment at :157-163)
- `AAEmu.Game/Core/Network/Game/GameNetwork.cs:166-177` (registration; stale-0x0a1 warning)
- `AAEmu.Game/Core/Packets/C2G/CSReturnMailPacket.cs` (int64 payload)
- `AAEmu.Game/Core/Managers/MailManager.cs:34-35,360-508,513-610` (return/expiry/pay)
- `AAEmu.Game/Models/Game/Char/CharacterMails.cs:26-100,146-260,332-335` (open/read/take/return)
- `AAEmu.Game/Models/Game/Mails/` (`MailStatus.cs`, `MailType.cs`, `BaseMail.cs:56-81`, `MailForTax.cs:82`, `CommercialMail.cs`, `CountUnreadMail.cs:30`)
- `AAEmu.Game/Core/Packets/G2C/SCOffsets.cs:270-284` (`SCMailReturnedPacket=0x121`, dead `0x11c`/`0x123`)
- `AAEmu.UnitTests/Game/Core/Managers/MailReturnTests.cs`, `MailOwnershipGuardTests.cs`; `AAEmu.IntegrationTests/E2e/MailS3RestartE2eTests.cs`
- Git history: `531a732fe` (0x0a2 wiring), `8f3d7a3d1` (2019 mail block: Delete/ReportSpam/Return at 0x09f/0x0a0/0x0a1), `742fb0bb0` (3.0.3.0: Return=0x0B7), `3e55d80f3` (8.0.3.12: Return=0x150), `e1b7ca4d4` (original CSOffsets)
- Decompiled 1.2 `game_pak` Lua: `/root/aaemu-pak-lua/dec/x2ui/mailbox/mail/read_mail.lua:39-99,991-1009`, `comercialmail/read_mia_mail.lua:137`, `mail/common.lua:6-26` (MAIL_TYPE), `write_mail.lua:29-35,125-134` (Normal/Express only)
- `Docs/JOSH-QAT-WAVE4.md:62-100,453` (W4-1 protocol; capture REQUIRED on FAIL), `EVIDENCE-LEDGER.md` (no W4-1/0x0a2 entry — QAT not yet recorded), `STATUS.md:295`, `SCORECARD.md:386`, `ROADMAP.md:2059,2142`, `PROJECT-CONTROL.md:160`
- `scorecard-explorations/mechanics/mail-domain.md` (§1, §4, Addendum A1), `pvp-domain.md:248-261`, `economy-domain.md:222-232`

## 2026-08-29 follow-up: return opcode verification + expiry sweep gap

Re-read at HEAD `14388b702` (main worktree, not the `22e02d3d2` archaeology worktree). Commits between evidence doc (`fef64f07e`) and HEAD touching this domain: `69861b73c` (COD payment dispatch + sent-tab deletion + `MailCodLifecycleTests`), `fa56915e6` (sent-tab deletion state fix, NameManager O(1)), `ac8953813` (per-item attachment split preserved). `fef64f07e` is an ancestor of HEAD.

### Part 1 — return opcode & server reply at HEAD

| Claim | Grade (now) | Evidence (HEAD) |
|---|---|---|
| C2S Return opcode `0x0a2` | **STRONGLY_INFERRED — unchanged, still not capture-verified** | `CSOffsets.cs:157-164` keeps the STRONGLY_INFERRED comment + `CSReturnMailPacket = 0x0a2`; registration `GameNetwork.cs:174-177`; `CSReturnMailPacket.cs:8-14` reads `stream.ReadInt64()` mailId. No new capture evidence landed: W4-1 verdict sheet still blank (`Docs/JOSH-QAT-WAVE4.md:452`), no `0x0a2`/W4-1 entry in `EVIDENCE-LEDGER.md`. Slot-arithmetic caveat from the doc stands (`0x099` hole, `CSOffsets.cs:149-150`). |
| S2C reply `SCMailReturnedPacket = 0x121` | **VERIFIED (server side) / layout only INFERRED** | `SCOffsets.cs:282`; sent at `MailManager.cs:400` (only on successful bounce, to the returner, `mailId` + swapped `MailHeader`). Byte layout is server-authored: `SCMailReturnedPacket.cs:9-13` writes `int64 mailId` + `MailHeader.Write` (`MailHeader.cs:20-32`: long MailId, byte Type, byte Status, string Title, string SenderName, byte Attachments, string ReceiverName, DateTime OpenDate, bool Returned, long Extra). Same header serializer family as `SCGotMailPacket`/`SCMailListPacket`, but **no 1.2 client offset dump or capture verifies `0x121`'s shape** — that confirmation is still W4-1 R2 (client accepts ack, no error popup). |
| Return moves items/money back | **VERIFIED (code + unit tests)** | `BounceMailToOriginalSender` (`MailManager.cs:411-465`): swaps SenderId↔ReceiverId (`:430-434`), retargets attachment `OwnerId` to original sender (`:445-446`), CopperCoins/BillingAmount untouched in Body, re-registers via `Send()` (`:451-454`). `MailReturnTests.cs:110-161` covers bounce-intact + double-return refused. |
| Return marks mail deleted | **No — mail survives as same row, roles swapped** | `Returned=true`, `Status=Unread`, `OpenDate` cleared (`MailManager.cs:437-439`); row persists in `_allPlayerMails`; delete only on later expiry (`ProcessExpiredMail` `:455-489`). |
| Return handles COD refund | **NO refund, gap (see G3)** | `ReturnMail`/bounce never touch `Header.Extra`; a returned `MailType.Charged` mail keeps `Extra > 0` so the original sender must pay COD to reclaim their own items. |
| Expiry sweep exists | **VERIFIED — server sweeps expired mail** | `MailDeliveryTask` (`Models/Tasks/Mails/MailDeliveryTask.cs`) scheduled in `MailManager.Load` (`MailManager.cs:210-211`, 30 s start / 5 s period) → `CheckAllMailTimings` (`MailManager.cs:492-509`): expire set = `IsDelivered && RecvDate <= now - MailExpireDelay` (`:505-506`). `ProcessExpiredMail` (`:455-489`): not-returned P2P Normal/Express → bounce to sender intact; else `NotifyDeleteMailByNameIfOnline` + `DeleteMail(trashItems:true)` destroys attachments (`:487-488`). Expiry window `MailExpireDelay = 14 d` (`MailManager.cs:35`, comment still "Default is 30 days ?"). No dedicated expiry column; `RecvDate` persisted (`received_date`), so window survives restart in-memory only. Unit coverage: `MailReturnTests.cs:180-225` (expiry bounce / system-mail destruction / retention pass-through). |

### Part 2 — doc gap re-check at HEAD (mail-domain.md §4, Gaps 1-5 / evidence-file N-actions)

| Doc gap | Status at HEAD | Evidence |
|---|---|---|
| No COD enforcement (`CharacterMails.cs` free take) | **CLOSED by `69861b73c`** | Gate: insufficient money refuses + `MailNotEnoughMoney` (`CharacterMails.cs:186-193`); deduction + `Extra=0` + `[COD]` payment mail to sender (`:261-288`); tests `MailCodLifecycleTests.cs` (insufficient: retains items/Extra/money; success: deducts 400, dispatch mail; partial bag: head item transferred, remainder retained; per-item `SCAttachmentTakenPacket` split `:312-320`). |
| COD refund on return | **OPEN — G3** | see Part 1 row. |
| COD expiry | **OPEN — G3 sub-item** | `ProcessExpiredMail` `canBounce` restricts to Normal/Express (`MailManager.cs:473-474`); Charged falls to the destruction branch (attach trashed) with no refund. No test. |
| Partial take on COD | **COVERED for items (partial bag), not for money** | `MailCodLifecycleTests.GetAttached_PartialBagFull_TransfersHeadAndRetainsRemainder`; money-take (`CharacterMails.cs:174-179`) has no COD interplay (COD is item-gated only). |
| Sent-tab delete `isSent=true` no-op | **CLOSED by `69861b73c`/`fa56915e6`** | Ownership check sender-id (`CharacterMails.cs:344-354`), `SCMailDeletedPacket(true,…)` + `DeleteMail(id)` (`:357-363`); test `DeleteMail_SentTab_SenderOwns_EmitsDeletedPacket`. Client-side capture of the `0x0a1`+`isSent=true` frame still not done (was doc N6). |
| Ownership security (read/take/delete) | **CLOSED by `531a732fe`** | `ReadMail` (`CharacterMails.cs:55-66`), `GetAttached` (`:151-156`), `DeleteMail` (`:344-355`), `ReturnMail` (`MailManager.cs:366-376`). |
| Dead code | **PARTIALLY CLOSED** | `SCMailRemovedPacket 0x123` and `SCMailReceiverOpenedPacket 0x11c` still have zero callers (dead). `BaseMail.ReturnToSender` is **no longer dead**: called by `CharacterManager.cs:625` (character-delete asset return, present since pre-`fef64f07e`). Doc's contradiction stands: `CanReturnMail` requires `IsDelivered == false` (`BaseMail.cs:51-53`) while `MailManager.ReturnMail` requires `Status == Read` (`MailManager.cs:378-383`) — two disjoint policies; delete-path uses legacy, interactive path uses manager. |
| Unpaid lifecycle tests (N4) | **OPEN** | Zero references to `PayChargeMoney`/`MailForTax`/`Unpaid` in `AAEmu.UnitTests`/`AAEmu.IntegrationTests`. |
| Restart-spanning expiry E2E (doc S4/N3) | **OPEN** | Only in-memory unit rig (`MailReturnTests`); no integration test. |
| `MailExpireDelay` retail constant (N7) | **OPEN** | Comment still "Default is 30 days ?" (`MailManager.cs:35`). |

### Open gaps (HEAD)

1. **G1 — Opcode `0x0a2` unverified by capture (major).** Slot arithmetic + client-UI + payload cross-version only. Fix when captured: move `CSOffsets.cs:164` + `GameNetwork.cs:177` to observed value, or revert to `0xfff` if Return unused (doc §2 disposition rules; W4-1).
2. **G2 — `SCMailReturnedPacket 0x121` byte layout unverified against 1.2 client table (major).** Server-side existence VERIFIED; shape only inferred (int64 + MailHeader). Fix: W4-1 R2 capture; adjust `SCMailReturnedPacket.cs`/`MailHeader.cs` if client rejects.
3. **G3 — No COD refund/settlement on return or expiry (minor).** Bounce keeps `Header.Extra` (`MailManager.cs:437-441` omits `Extra`); returned Charged mail re-levies COD on the original sender; expiry destroys Charged attachments without refund (destroy branch `MailManager.cs:487-488`). Fix would touch: `MailManager.ReturnMail`/`BounceMailToOriginalSender` (clear `Extra` and/or emit `[COD]` refund mail like `CharacterMails.cs:261-288`), plus `ProcessExpiredMail` Charged branch. Minor because 1.2 client has no COD send UI (doc §3.1) so Charged reaches users only via WebApi/admin.
4. **G4 — No restart-spanning expiry E2E (major).** Sweep is in-memory (loads `RecvDate` from `received_date`); no integration proof of bounce/destroy across restart with DB-level assertions (doc S4/N3). Fix: E2E shrinking `MailExpireDelay`, restart, assert `returned=1`, swapped ids, zero orphaned `attachmentN` rows.
5. **G5 — `Unpaid` tax lifecycle zero coverage (minor).** `PayChargeMoney` cert-first/gold-fallback/Unpaid→Read→delete untested (doc N4). Fix: unit tests `MailForTax` → `PayChargeMoney`.
6. **G6 — Dead G2C packets + expiry notification mismatch risk (minor).** `SCMailRemovedPacket 0x123`, `SCMailReceiverOpenedPacket 0x11c` unsent; expiry uses `SCMailDeletedPacket 0x11f` — client acceptance unverified (doc §3.3 Q1). Fix: capture verdict → wire `0x123` or confirm `0x11f` suffices.
7. **G7 — Retail `MailExpireDelay` constant unknown, crash-window save atomicity (minor, unchanged).** `MailManager.cs:35` "30 days ?"; mail-vs-item save is shared-tick, orphaned attachment ids warn-only (doc Gap 5).
