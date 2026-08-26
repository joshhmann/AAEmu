# ECONOMY Domain Dossier — MERCHANT-01 / LABOR-01 / ECON-01 / MAIL-01 (2026-08-25 exploration)

Scorecard rows at writing (SCORECARD.md:81,90,92,93): LABOR-01 U/U/U/U/U, MERCHANT-01
U/U/U/U/U, ECON-01 U/U/U/U/U, MAIL-01 U/**1**/U/**1**/U. Branch verified: `develop` @
origin/develop head 214bed834. Pure audit — no code touched.

## Verdict: three of four rows are STALE — the substrate exists, is rig-tested AND live-proven; only the mail-return wire is genuinely missing

The engine has a complete NPC-vendor loop (data-driven goods packs, flat price model,
grade-scaled sell-back, buyback container), an account-scoped labor pool with
data-driven consumption + hardcoded caps and a **dead-by-default regen tick**, a working
ledger-reconciliation economy E2E that already PASSED live across a `kill -9` restart,
and a mail subsystem whose return *logic* is complete but whose client-facing *opcode*
is a confirmed 0xfff placeholder with no recoverable 1.2 value.

---

## 1. MERCHANT — how NPC shops actually work

### Data layer (compact.sqlite3, read-only per AGENTS.md rule 3)
- `merchants` (1,983 rows): `npc_id → merchant_pack_id` (schema queried 2026-08-25).
- `merchant_goods` (3,246 rows): `merchant_pack_id, item_id, grade_id`.
- Loaded by `NpcManager` (`Core/Managers/UnitManagers/NpcManager.cs:848-888`) into
  `Dictionary<uint, MerchantGoods> Goods`, resolved via `GetGoods()` (:107-110).
- **No stock system**: `MerchantGoods.AddItemToStock` just appends to a static list;
  duplicates ignored (`Models/Game/Merchant/MerchantGoods.cs:17-24`). No per-item
  quantity, no depletion, no restock timer — VERIFIED from the model.
- **No vendor discount system**: compact table `merchant_price_ratios` exists (schema,
  0 rows) but has ZERO code references — grep of `AAEmu.Game` finds only
  `SpecialtyManager._priceRatios`, which is config-built, not table-fed. Dead table.
- `merchant_packs` (263 rows) has no loader — merchant-ship pack economy unwired
  (already noted in mechanics/trade-packs.md:234).

### Packet flow (real opcodes from CSOffsets.cs)
| Step | Opcode | File |
| --- | --- | --- |
| Buy (incl. buyback re-purchase) | `CSBuyItemsPacket = 0x0ae` | CSOffsets.cs:168 |
| Sell (item → buyback window) | `CSSellItemsPacket = 0x0b0` | CSOffsets.cs:170 |
| Coin-shop item | `CSBuyCoinItemPacket = 0x0af` | CSOffsets.cs:169 |
| Auction sold-list query | `CSListSoldItemPacket = 0x0b1` | CSOffsets.cs:171 |

`CSBuyItemsPacket.Read` (`Core/Packets/C2G/CSBuyItemsPacket.cs`): resolves NPC or
doodad vendor → 3m range gate (`TooFarAway`, :38-58) → validates each requested itemId
against the NPC's goods pack (`pack.SellsItem`, :74) → prices by currency:
`money += template.Price * count` (:87), honor `template.HonorPrice` (:89), vocation
`template.LivingPointPrice` (:91) → funds gate (:119-122) → grants items via
AcquireDefaultItem (:125-130) → buys back buyback-window items (:132-150) → deducts
honor/vocation/money (:152-165) → single `SCItemTaskSuccessPacket(StoreBuy)` (:167).

Price formulas (VERIFIED):
- **Buy price** = flat `items.price` template column × count. No bulk discount, no
  faction modifier, nothing else.
- **Sell-back** = `(int)(template.Refund * grade.RefundMultiplier / 100f) * count`
  (CSSellItemsPacket.cs:61-62) — grade-scaled fraction of the template's `refund`
  column. Sell goes through the non-persisted `BuyBackItems` container; the persisted
  row is queued for deletion to prevent reload-duplication (#1189 fix,
  CSSellItemsPacket.cs:55-59).
- **Buyback re-purchase** pays back exactly the refund credited at sell
  (CSBuyItemsPacket.cs:113-114 uses the identical formula).

Known engine bugs surfaced by the rig (documented, deliberately NOT fixed —
`AAEmu.UnitTests/Game/Core/Managers/MerchantRigTests.cs:28-52`):
1. **Insolvent buy proceeds**: funds gate joins three checks with `&&` not `||`
   (CSBuyItemsPacket.cs:119-122) — with honor/vocation both 0 (normal case) the gate
   never fires; money drives NEGATIVE because `ChangeMoney(None→Inventory)` has no
   funds guard (Character.cs:1588-1590).
2. **Sell refund on refused move**: refund accumulated outside the success branch of
   the buyback move (CSSellItemsPacket.cs:49-63) — container-full ⇒ item kept AND
   money paid (dupe vector).
3. **Grant failure ignored**: `AcquireDefaultItem` return unchecked (:125-130); full
   bag ⇒ money charged, no item (price deduction at :162-165 runs regardless).

### The paradox — RESOLVED: capability matrix is right, the SCORECARD row is stale
Evidence chain, weakest to strongest:
1. `MerchantRigTests` (root `AAEmu.UnitTests/Game/Core/Managers/`, `[NotInParallel]`)
   drives the REAL `CSBuyItemsPacket`/`CSSellItemsPacket` classes over capture-backed
   connections with real NpcManager goods packs + real inventory/currency services
   (header :14-27); plus `CSBuyItemsPacketTests` (root UnitTests C2G folder).
2. Bot contract actions `GameplayActor.Buy`/`.Sell`
   (`Core/Managers/Bots/GameplayActor.cs:1862-1968`) replicate the packet seam call-for-
   call (same goods-pack gate, 3m range, price/refund formulas, AcquireDefaultItem +
   ChangeMoney), adding correct pre-flight where the packet is buggy (:1897-1903).
3. `EconomyDayCycleScenario` BUY→PLANT→HARVEST→CRAFT→SELL→DEPOSIT stages ride those
   actions under a per-stage LEDGER built only from observable character state
   (`Core/Managers/Bots/EconomyDayCycleScenario.cs:44-50,643-652,820-857`).
4. **LIVE PROOF**: `AAEmu.IntegrationTests/E2e/EconomyDayCycleE2eTests.cs` ("M8
   auditable-economy assertion", header :10-31) ran GREEN on this host —
   `/root/aaemu-e2e/logs/m8-economy-cycle-report.json` `passed: true`, stage coverage
   BUY-SEEDS/PLANT/HARVEST/CRAFT/SELL/DEPOSIT asserted (:78-80), and
   `/root/aaemu-e2e/logs/m8-economy-cycle-reconcile.md`: "money 99900 == 99900, bank
   120 == 120, items pre == post … verdict: PASS (copper/bank/item conservation held
   across a process-level restart)".

So the capability-matrix Vendors row "✅ economy cycle live-proven"
(mechanics/playerbot-capability-matrix.md:13) is accurate; **MERCHANT-01 W should be 2**
(real engine path end-to-end, live) and **A ≥ 2** (live + restart persistence via the m8
report). What keeps H honest at UNKNOWN: no human-client run through the actual shop UI
window has been captured; the m8 run is bot/proxy-driven (same caveat style as TRADE-01
A=1 "kept honest", SCORECARD.md:89).

**Curated A=2 scenario**: pin a variant of EconomyDayCycleE2eTests on a pure vendor pair
(buy N seeds from merchant 8522/pack 171 → sell crafted product back) asserting per-stage
`ReconcileCurrency`/`ReconcileStageSums` EXACT equality plus the restart byte-compare —
that is literally the existing m8 circuit minus farm/craft legs; smallest change is an
options profile, not new machinery.

## 2. LABOR — the reconstructed model

### Storage & persistence (account-scoped, VERIFIED)
- Labor lives on the ACCOUNT, not the character: `accounts.labor INT`,
  `last_labor_tick DATETIME` (`SQL/aaemu_game.sql:24,29`).
- `Character.LaborPower` setter write-through to `AccountManager.UpdateLabor`
  (`Models/Game/Char/Character.cs:82-92`); cache initialized from account details at
  load (`UnitManagers/CharacterManager.cs:495`, `Character.cs:2289-2299`).
- Per-character counter `consumed_lp` persisted (`Character.cs:2299`,
  `CharacterCounters.cs:9`).

### Consumption (data-driven, with three hardcoded exceptions)
- Base values are per-SKILL in compact.sqlite3: `skills.consume_lp`
  (`Managers/SkillManager.cs:394`). Live-exercised values: planting seed skill 25536
  초본 식생 심기 `consume_lp=1` (queried); fishing skill 21571 labor **5**, actability
  group 7, charged in `Skill.EndSkill → ChangeLabor(-5, 7)`
  (mechanics/fishing-domain.md:35,43).
- Effective cost = `round(base × actability labor-cost multiplier)`, floored at 1 when
  base > 0 (`Models/Game/Skills/Skill.cs:1394-1407`).
- Hardcoded: specialty pack sale **60** labor (`World/SpecialtyManager.cs:256-260`);
  COD-mail money withdrawal **1** labor (`Char/CharacterMails.cs:141-147`); doodad
  `req_lp` column feeds `DoodadFuncNaviRemove` (`DoodadManager.cs:1699`).
- Every consumption funnels through `ChangeLabor(change, actabilityId)`
  (`Character.cs:1615-1644`): negative change ⇒ XP granted via
  `FormulaKind.ExpByLaborPower` (=19) × actability exp-multiplier; actability points grow
  by `|change| × World.ActabilityRate` (:1619-1626). Formula text VERIFIED in
  compact.sqlite3 `formulas` id 19: `(( ( pc_level * 4.5 ) + 37.5 ) / 5) * labor_power`.

### Regeneration (machinery exists; effectively DEAD by default)
- Online: `TimedRewardsManager.Initialize` schedules `TimedRewardsTask` every minute
  (`Managers/TimedRewardsManager.cs:17-20`); `DoTick` adds
  `Labor.TickAmount(/Premium)` per `Labor.TickMinutes` (:52-66).
  **But no caller of `Initialize()` exists anywhere** — grep over the whole Game project
  finds only DI registration (`Program.cs:376-377`); the tick task is never scheduled.
  Consistent with zero labor/NRE mentions in live logs (`/root/aaemu-e2e-pb003/logs/game*.log`).
- Rates are config-data, not constants: `CurrencyValuesConfig` CLR defaults
  `TickMinutes=5, TickAmount=0, TickAmountPremium=0`
  (`Models/Game/Configurations.cs:264-274`); shipped configs define **no** `Labor` /
  `LaborOffline` section (absent from repo Config.json and Configurations/*.json —
  grep-verified), so even if scheduled, default regen would be 0/min.
- Offline: `AddOfflineLabor` IS called — every game-connection add
  (`AccountManager.cs:40`) — computing
  `floor(minutesSinceLastLogin / LaborOffline.TickMinutes) × LaborOffline.TickAmount`
  (TimedRewardsManager.cs:99-107), then clamped by the same cap logic.

### Caps (hardcoded, VERIFIED)
- `MaxLabor = 2000`, `MaxLaborPremium = 5000` (TimedRewardsManager.cs:14-15); enforced
  ONLY as a clamp on regen additions (`DoAddLabor`, :35-38) — consumption can push below
  zero-guard freely, GM `addlabor` command bypasses caps entirely
  (`Scripts/Commands/AddLabor.cs`), and skill-effect classes
  `ConsumeLaborPower`/`AddLaborPower` (Skills/Effects/SpecialEffects/) apply raw deltas.
- Cap also surfaces in unit requirement checks (`Units/UnitReqs.cs:280`).
- **No bonus-cap concept** (no housing/buff cap extensions) in 1.2 fork code.

## 3. ECON CONSERVATION — invariant set (the ECON-01 C-dimension definition)

Ten assertable invariants (each currently checkable headlessly via the m8 ledger
pattern — observable character state only, `EconomyDayCycleScenario.cs:44-50,323-330`):

1. **Vendor-buy sink**: Δbuyer.money == −Σ(`template.Price × count`); vendor holds no
   money pool — currency destroyed at purchase (CSBuyItemsPacket.cs:87,164).
2. **Vendor-sell source & roundtrip**: Δseller.money == Σ refund formula
   (CSSellItemsPacket.cs:61-65); buyback re-purchase charges exactly the refund paid
   (CSBuyItemsPacket.cs:113-114) ⇒ sell+rebuy is money-neutral and item-neutral.
3. **Bank pairing**: deposit/withdraw conserve money+bank pairwise
   (CSDepositMoneyPacket/CSWithdrawMoneyPacket; exercised by DEPOSIT stages).
4. **Mail-send sink**: fee + attached coins leave sender; fee destroyed
   (`CharacterMails.SendMailToPlayer` subtracts `mailFee + money0`,
   CharacterMails.cs:104-124). Fee schedule: normal 50c + 30c/attachment past the first
   free one, express 100c/80c (MailManager.cs:29-33; MailPlayerToPlayer.cs:22-48).
5. **Mail-take transfer**: attached copper lands receiver 1:1
   (CharacterMails.cs:151-157); COD adds PayChargeMoney transfer + 1-labor tax.
6. **Auction sinks**: listing fee `direct_money × 1% × (duration+1)` capped 100g
   (AuctionManager.cs:658-666, cap :31); settlement keeps seller 90% (:40) — 10% sink;
   cancel/fail/expiry refund escrow minus listing fee (:88-99,135-142).
7. **Specialty payout law**: pack item destroyed; payout mail ==
   `round(base × ratio% ) + interest` with ratio bounded to config band 70–130 and
   decay/regrowth ticks (SpecialtyManager.cs:256-340, Configurations.cs:282-302);
   scenario asserts the closed form `round(base × ratio% × 1.05)` against observed mail
   delta (EconomyDayCycleScenario.cs:1091-1104).
8. **Labor accounting**: Σ documented per-action consume_lp == persisted accounts.labor
   delta within tolerance, XP gain == formula-19 output (ChangeLabor; scenario
   ReconcileLabor, EconomyDayCycleScenario.cs:417-425,1137-1141).
9. **Item-instance conservation**: every StoreBuy/StoreSell/SendMail item-task batch
   creates/destroys exactly the intended instances — currently violated by documented
   bugs MERCHANT #2/#3 (rig asserts the buggy behavior, i.e. the invariant is KNOWN-BROKEN
   on those paths, not silently).
10. **Restart persistence**: entire ledger (money, bank money2, per-container template
    counts) byte-equal across `kill -9` + reboot (m8 pattern, EconomyDayCycleE2eTests.cs:21-28).

Instrumentation inventory TODAY: the m8 ledger + six `Reconcile*` criteria
(currency/bank/stage-sums/labor/seeds/specialty-payout + lifecycle-trace,
EconomyDayCycleScenario.cs:386-450,570-576), rig-level corrupt-and-fail tests
(EconomyDayCycleScenarioRigTests.cs), live PASS artifacts
(`/root/aaemu-e2e/logs/m8-economy-cycle-reconcile.md`), and the separate auction
restart E2E (`/root/aaemu-e2e/logs/auction-restart-e2e-report.json`). MISSING: any
server-side ledger/metrics surface — all reconciliation lives inside bot scenarios;
invariants 4-6 (mail/auction fees) have no reconcile coverage yet.

## 4. MAIL GAP — CSReturnMailPacket verdict: placeholder CONFIRMED; real 1.2 opcode UNRECOVERABLE from available evidence

Confirmed exactly as scored: `CSReturnMailPacket = 0xfff // TODO: this packet is not in
the offsets` (CSOffsets.cs:157); registration commented out in `GameNetwork.cs:174`
(the old 0x0a1 guess collides with CSDeleteMailPacket, CSOffsets.cs:155).

What EXISTS and works server-side (so the gap is purely the wire constant):
- Full bounce logic: ownership check, double-return guard, sender/receiver swap,
  `SCMailReturnedPacket` to an online original receiver
  (`Managers/MailManager.ReturnMail`, MailManager.cs:360-451; `BaseMail.cs:64-65`).
- SC constant already defined: `SCMailReturnedPacket = 0x121` (SCOffsets.cs:282).
- Expiry semantics: 14-day retention (`MailExpireDelay`, MailManager.cs:35);
  unclaimed P2P mail bounces to sender with attachments intact, system/already-bounced
  mail is deleted with attachments destroyed (`ProcessExpiredMail`, :458-480) —
  rig-tested per SCORECARD MAIL-01 note (6b2f15a6d).

Opcode hunt outcome: no `.client_files`/client binary on this host to sniff; web
sources offer `ReturnMailPacket = 0x00C0` / `SCMailReturnedPacket = 0x011B` from a
community enum list (forum.zone-game.info tid=29159), but that list's mail-block
ordering contradicts AAEmu's VERIFIED 1.2 offsets (its SendMail=0xB8 vs 1.2's
CSSendMailPacket=0x098) ⇒ it describes a LATER build and cannot be cited for
r208022. One structural hint worth recording: AAEmu's own 1.2 table has an
UNASSIGNED slot **0x0a2** embedded in the mail block (Delete 0x0a1 … Spam 0x0a3,
CSOffsets.cs:155-156) — a plausible candidate, PLAUSIBLE-grade only.

Grading consequence: the gap stays a **canonical-behavior question** — either the
opcode is recovered by client-side capture (then the slice is two lines: offsets
constant + RegisterPacket; payload/logic already implemented), or 1.2 r208022 truly
lacked a client-initiated return (return-by-button introduced post-1.2), in which case
MAIL-01's client-facing return path closes as N/A-canonical and the rig-tested bounce/
expiry logic becomes the whole story. Neither branch changes A=1 today.

## 5. SLICE PLAN (smallest safe slices, PASS criteria)

| Slice | Size | PASS criteria |
| --- | --- | --- |
| **MER-A**: promote scorecard from existing evidence | S | Re-run `EconomyDayCycleE2eTests` green on current develop; flip MERCHANT-01 to W=2/A=2/H=U citing m8 report path + rig bug register. No code. |
| **MER-B**: vendor-only conservation profile | S | New options profile (pure BUY→SELL roundtrip vs merchant 8522): `currency-conservation` + `ledger-stage-sums-reconcile` EXACT, buyback rebuy net-zero, restart byte-compare PASS. |
| **MER-C**: fix funds gate `&&`→`\|\|` (+ grant-failure guard) | S-M | Rig tests inverted: insolvent buy rejected with money unchanged; full-bag buy charges nothing. Conservation invariant 9 holds on buy path. |
| **LAB-A**: schedule or delete the dead regen tick | S | Decision recorded; if scheduled: integration test shows +TickAmount after TickMinutes with cap clamp at 2000/5000; offline path adds floor(minutes/offTickMinutes)×amount on fresh login. If deleted: remove task + config stubs (clean cutover). |
| **LAB-B**: labor ledger in m8 criteria | S | `ReconcileLabor(tolerance=0)` green with documented consume_lp sum incl. the 60-labor specialty charge and 1-labor COD charge. |
| **ECON-A**: extend ledger to mail+auction fees | M | Criteria added for invariants 4-6; auction-restart E2E extended to assert 90%/10% split and listing-fee cap against observed mails. |
| **MAIL-A**: return-opcode recovery spike | S | Client capture (HitL session) of the 1.2 mailbox Return button; if captured ⇒ wire `0x??` + register, E2E: send→return→sender receives attachments intact, second return refused. If absent ⇒ document canonical-N/A in SCORECARD, close gap as resolved. |

## Sources (primary file:line anchors)
CSOffsets.cs:65-69,145-157,168-171 · GameNetwork.cs:163-174 · CSBuyItemsPacket.cs:33-167 ·
CSSellItemsPacket.cs:13-71 · MerchantRigTests.cs:14-52 · GameplayActor.cs:1859-1968 ·
MerchantGoods.cs:3-31 · NpcManager.cs:107-110,393-394,848-888 · Character.cs:82-108,1559-1608,1615-1644,2289-2299 ·
Skill.cs:1393-1435 · SkillManager.cs:394 · CharacterMails.cs:72-156,298-302 · MailManager.cs:29-35,360-480,494-500 ·
MailPlayerToPlayer.cs:22-48 · BaseMail.cs:64-65 · SpecialtyManager.cs:28-40,155-156,256-290,366-407 ·
TimedRewardsManager.cs:14-15,33-50,52-86,99-107 · AccountManager.cs:27-41 · Configurations.cs:264-302 ·
AppConfiguration.cs:35-39 · Program.cs:376-377 · UnitReqs.cs:280 · CharacterCounters.cs:9 ·
SQL/aaemu_game.sql:23-34 · formulas(id=19) · fishing-domain.md:35,43-45 · trade-packs.md:234 ·
playerbot-capability-matrix.md:13 · EconomyDayCycleScenario.cs:29-50,323-450,570-605,1074-1107 ·
EconomyDayCycleE2eTests.cs:10-88 · /root/aaemu-e2e/logs/{m8-economy-cycle-report.json,m8-economy-cycle-reconcile.md,auction-restart-e2e-report.json} ·
forum.zone-game.info showthread.php?tid=29159 (later-build enum, weight-limited as above).
