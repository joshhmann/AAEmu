# DOMINION-01 Domain Dossier (2026-08-25 exploration)

Scorecard row at writing: per `scorecard-explorations/zero-wired-domains.md` §2 "siege" — meaningful dead-end code, **L** full / **M** declare+own+tax. This dossier re-verifies that row against `develop @ 214bed834` and grades every claim.

## Verdict: richest dead-end domain in the repo — complete wire formats and data, zero runtime

- **No manager exists**: no `DominionManager`/`SiegeManager`/`CastleManager` anywhere (`git ls-files`, graphify graph has no such node; only community hub mentions are the packet marshalers and special-effect stubs).
- **Data is fully shipped but ~all orphaned**: 8 compact.sqlite3 tables (see §1); only the two doodad-func tables are loaded, into no-op templates.
- **One half-real write path**: `DeclareDominion` special effect (reachable via skill 13661) broadcasts `SCDominionDataPacket` with hardcoded values — nothing persists, nothing schedules, nothing fights.

---

## 1. DATA (compact.sqlite3 — reference; MySQL — mutable state)

### compact.sqlite3 (`AAEmu.Game/Data/compact.sqlite3`) — 8 siege/dominion tables, 0 named `dominion*`

| Table | Rows | Key columns |
| --- | --- | --- |
| `siege_zones` | 6 | per-castle schedule + economy: `start_siege_weekday/hour/min` (all Sun 21:00), `siege_days/hours/mins` (0d 1h 30m), `pay_weekday/hour/min` (Sun 23:55), `zone_group_id`, `declare_item_id`, `defense_ticket_id`, `offense_ticket_id`, `reinforce_defense_delay_mins` (20), `defense/offense/dominion_merchant_id`, `open_hour`+`open_duration_hours` (168h), `start_auction/start_declare/start_warmup/open_weekday+hour+min` (auction & declare Sat/Sun 22:30, warmup Sun 20:30), `monument_doodad_id` (VERIFIED schema + rows) |
| `siege_plans` | 158 | `id, zone_group_id, week_start` — weekly rotation of which castle is active; dates are 2014 legacy (VERIFIED rows) |
| `siege_settings` | 11 | `total_castles, num_defenders (70/40/45/50), num_reinforcements (0/20/25/30)` (VERIFIED rows) |
| `siege_items` | 13 | 12-column boolean usage matrix per war item × location/phase (`outside_siege_zone`, `during_no_dominion`, `during_declare`, `during_peace`, `outside_siege_area_during_warmup/siege`, `offense_hq_*`, `defense_hq_*`, `siege_circle_*`) + Korean `USAGE` message; items incl. tank mine 26397, palisade 26424, catapult scroll 27300, siege ladder 27302 (VERIFIED rows) |
| `siege_ticket_offense_prices` | 10 | volume pricing `count (2), per_price (2–5)` (VERIFIED rows) |
| `doodad_func_declare_sieges` | 1 | bare `id`=1 (VERIFIED) |
| `doodad_func_siege_periods` | 8 | `siege_period_id, next_phase, defense` — monument doodad phase chain (VERIFIED rows) |
| `doodad_func_purchase_siege_tickets` | 0 | schema only, empty (VERIFIED) |

**The six siege zones** (`zone_groups` join, VERIFIED): 33 `o_salpimari` 살피마리, 34 `o_nuimari` 누이마리, 43 `o_seonyeokmari` 서녘마리, 44 `o_rest_land` 안식의 땅, 54 `o_abyss_gate` 심연의 입구, 56 `o_land_of_sunlights` 태양의 들녘. Monuments `monument_doodad_id` 7229–7234 = `영지 석상` ("Dominion Statue") : 아크리테스/칼레일/이지/곤/호라/누이 (Akrithes, Kaleil, Ezi, Gon, Hora, Nui).

Named items (VERIFIED via `items`): declare backpack `공성 진지` (siege encampment) 21134 Salpimari / 21130 Nuimari; defense seal `수호의 인장` 21314/21313; offense seal `진격의 인장` 21318/21317.

### MySQL `SQL/aaemu_game.sql` — NO dominion/siege state tables at all

Only expedition role-permission flags: `expedition_role_policies.dominion_declare`, `.siege_master`, `.join_siege` (aaemu_game.sql:244,251-252). Ownership/tax/persistence would need a new table. (VERIFIED by grep of SQL/ tree.)

---

## 2. CODE — wired vs orphaned

**Wired (executes today):**
- `DoodadManager.cs:987-992` loads `doodad_func_declare_sieges` → `DoodadFuncDeclareSiege`; `DoodadManager.cs:2243-2252` loads `doodad_func_siege_periods` → `DoodadFuncSiegePeriod` — but both `Use()` bodies are trace-log no-ops (`DoodadFuncDeclareSiege.cs:15`; `DoodadFuncSiegePeriod.cs:15` sets `owner.OverridePhase = NextPhase` for characters, with an in-code admission it's a guess: "I think this is used to reschedule anything…"). VERIFIED.
- `SpecialEffect.Apply` (`SpecialEffect.cs:25,35`) reflect-dispatches `SpecialType.DeclareDominion`(=50, `SpecialEffectType.cs:46`) → `DeclareDominion.Execute`. Reachable via skill **13661** `영지선포 - 수호탑` (Territory Declaration – Guard Tower; effects row 8390 SpecialEffect→2218, VERIFIED in `skill_effects`/`effects`). Also InteractionEffect (16805) / SpawnEffect (26925) / DamageEffect (15852) rows point at the same special effect. VERIFIED.
- `DeclareDominion.cs:42-115`: builds a **complete hardcoded** `DominionData` (TaxRate 50, CurHouseTaxMoney 500000, TerritoryData radii 250/110/250/100, SiegePeriod 1) keyed off `target is House lodestone` (line 34), broadcasts `SCDominionDataPacket(dominion, true, true)` server-wide (line 115), consumes the caster's equipped backpack (lines 119-120). Guard-tower prerequisite comments at lines 33-39 ("Get target zone…", "Advance building step") are unimplemented. VERIFIED.
- Periphery wired: `ShipSiegeAoEHit.cs:13-17` ship-hull-aware siege AoE targeting, called from the Skill damage path; `ExpeditionManager.cs:136,143-144,352` round-trips the three siege perms; feature flag `Feature.cs:7 siege = 0` bit sent enabled (`SCInitialConfigPacket.cs:13,49`).

**Orphaned (loads/defines but nothing consumes):**
- All five pure-data tables (`siege_zones`, `siege_plans`, `siege_settings`, `siege_items`, `siege_ticket_offense_prices`) — **zero readers** (grep across AAEmu.Game hits nothing but `ErrorMessageType.cs:449`). VERIFIED.
- Stub special effects: `GetSiegeTicket` (64), `TeleportToSiegeHq` (65), `StartDominionNonPvpDuration` (110; one data row value1=120), `DominionTaxInKind` (138) — all "// TODO …" + debug log (`SpecialEffectType.cs:60-61,106,135`). VERIFIED.
- `BuySiegeTicket` world interaction (=104, `WorldInteraction.cs:117`) just forwards `doodad.Use` (`BuySiegeTicket.cs:15`). `BackpackType.SiegeDeclare = 4` (`BackpackType.cs:8`) and `BackpackTemplate.DeclareSiegeZoneGroupId` load (`ItemManager.cs:971-972`) but nothing branches on them.
- `HousingManager.cs:1030`: `hostileTaxRate = 0; // NOTE: When castles are added, this needs to be updated depending on ruling guild's settings` — the castle-tax hookpoint is explicitly waiting.
- No GM command touches dominion/siege (`Scripts/` grep: zero hits). VERIFIED.
- Dominion unit-reqs kinds marked unused/commented (`UnitReqsKindType.cs`, per zero-wired §2).

---

## 3. PACKETS (Game direction)

### C2G — exactly 1, stub
| Opcode | Packet | Status |
| --- | --- | --- |
| `0x012` | `CSUpdateDominionTaxRatePacket` (`CSOffsets.cs:22`) | Registered in `GameNetwork.cs:39`; `Read` decodes id+taxRate and **logs only** (`CSUpdateDominionTaxRatePacket.cs:13`) |

No other C2G siege/dominion opcodes exist in `CSOffsets.cs`. Zero declare/join/reinforce packets. VERIFIED.

### G2C — 5 implemented marshalers (only 1 ever sent) + 9 bare offsets, classes absent
| Opcode | Class | Sent? |
| --- | --- | --- |
| `0x1d` `SCDominionDataPacket` (`SCOffsets.cs:34`) | full `DominionData` marshaler | YES — only from `DeclareDominion.cs:115` |
| `0x1e` `SCDominionDeletedPacket` (:35) | class exists | never sent |
| `0x1f` `SCDominionOwnerChangedPacket` (:36) | class exists (`id, unkId, rst, bestowed`) | never sent |
| `0x20` `SCDominionTaxRatePacket` (:37) | class exists | never sent |
| `0x23` `SCDominionTaxBalancedPacket` (:40) | class exists | never sent |
| `0x21` `SCNationalTaxRatePacket`, `0x22` `SCNationalMonumentChangedPacket`, `0x24` `CDominionStartUnkPacket`, `0x25` `SCDominionEndUnkPacket` (:38-42) | offset constants only, no classes | absent |
| `0xe9` `SCSiegeStatePacket`, `0xea` `SCSiegeDeclaredPacket`, `0xeb` `SCSiegeReinforcePacket`, `0xec` `SCSiegeMemberPacket`, `0xed` `SCSiegeAlertPacket` (:229-233) | offset constants only, no classes | absent |

Wire payload is fully modeled in `DominionData.cs`: `DominionData` (ZoneId, ExpeditionId, House, TaxRate, coords, house/hunt/peace tax money+AaPoint, LastPaidTime/LastSiegeEndTime/ReignStartTime, national-tax fields, TerritoryData, SiegeTimers, NonPvP*) + `DominionTerritoryData` (MaxGates/MaxWalls, RadiusDeclare/Dominion/Siege/OffenseHq) + `DominionSiegeTimers` (5 durations, Started/Fixed, Bdm, SiegePeriod, 2× `DominionUnkData` HQ blobs) (`DominionData.cs:36-151`). VERIFIED marshalers; **field semantics vs live 1.2 client UNVERIFIED** (no client-side capture).

Adjacent-but-out-of-scope: `SCHouseTaxInfoPacket.dominionTaxRate` field and `MailForTax` body's castle-rate slot (`MailForTax.cs:68`) render whatever housing computes — currently always 0. Conflict-zone packets (0xee/0xef) → pvp-domain dossier.

---

## 4. BEHAVIORAL CONTRACT — the 1.2 dominion cycle, graded

1. **Claim attempt (declaration)** — GRADE: STRONGLY_INFERRED (data) / VERIFIED (code fragment). During the declare window (`start_declare_weekday/hour/min` = Sat 22:30 per `siege_zones`), an expedition with declare rights plants the `공성 진지` siege-encampment backpack (a `BackpackType.SiegeDeclare` item carrying `declare_siege_zone_group_id`) at the zone's Dominion Statue monument. In-repo execution: cast skill 13661 at a **House** target → `DeclareDominion` consumes the backpack and broadcasts `SCDominionData(newlyDeclared=true)`.
2. **Declaration window enforcement** — GRADE: **UNKNOWN/absent**. Nothing reads the schedule columns; there is no gate, no auction step (despite `start_auction_*` data and `siege_ticket_offense_prices`), no error-string checks fired (~25 ready-made strings like `siege_master_only`=459, `siege_war_period_only` sit unused in `ErrorMessageType.cs`).
3. **Warmup → siege battle phases** — GRADE: **UNKNOWN** (data-complete, code-zero). `start_warmup` Sun 20:30, battle Sun 21:00 for 1h30m; attacker/defender roles implied by `num_defenders`/`num_reinforcements` (`siege_settings`), offense/defense seals as entry tickets, HQ blobs in `DominionUnkData`, `reinforce_defense_delay_mins`=20, `siege_items` gating matrix, `DoodadFuncSiegePeriod` phase chain on the monument. **None of this executes**: the five `SCSiege*` packet classes don't exist.
4. **Outcome / ownership transfer** — GRADE: PLAUSIBLE shape, UNKNOWN semantics. `SCDominionOwnerChanged(id, unkId, rst, bestowed)` signature hints at conqueror-vs-bestowal paths, but it has never been sent and nothing tracks owners.
5. **Taxes / benefits of holding** — GRADE: VERIFIED gap. Owner sets tax rate (C2G 0x012 arrives, is logged, discarded; `SCDominionTaxRate` never echoes). Housing tax mail has a castle-rate slot hardwired to 0 (`HousingManager.cs:1030`). `DominionData`'s coffer fields (CurHouseTaxMoney etc.) exist only inside the hardcoded broadcast blob.
6. **Persistence / upkeep** — GRADE: VERIFIED absent. No MySQL table, no store of any kind — a declared dominion evaporates on restart. Post-siege non-PvP grace (`StartDominionNonPvpDuration`, data value 120 min) and in-kind tax (`DominionTaxInKind`) are TODO stubs; `pay_weekday/hour/min` (Sun 23:55 payout moment) unread.

**Cycle verdict:** steps 1's broadcast fragment is the *only* executing piece end-to-end. Everything between "plant the pack" and "collect the taxes" is either inert data or missing code.

---

## 5. SIZED SLICE PLAN

**Slice 1 (smallest viable, size S-M): "dominions exist, on schedule, and persist" — zero combat.**
Scope:
1. New `DominionManager` (DI singleton, `ILoadable`/`IInitializable`, ctor deps per AGENTS.md convention #6) that loads `siege_zones` + `siege_settings` (+ `siege_plans` rotation index) into memory.
2. MySQL additive table `aaemu_game.dominions` (zone_group_id PK, expedition_id, tax_rate, timestamps) + base-file patch per SQL workflow.
3. Wire the existing edges instead of inventing new ones: `CSUpdateDominionTaxRatePacket` validates sender is dominion-owner expedition member with `DominionDeclare` policy → update store → echo `SCDominionTaxRatePacket`; re-broadcast stored `SCDominionDataPacket`s to clients on enter-world (alongside `SCInitialConfigPacket`).
4. A `TickManager` cron that flips phase enum (Peace/Declare/Warmup/Siege/Payoff) per each zone's `siege_zones` columns and announces via `SCSiegeAlertPacket` (new 5-line marshaler — offset already known).

**PASS criteria:** server boots logging 6 siege zones loaded; a test/GM-declared dominion survives game-server restart (MySQL row → re-broadcast); a connected 1.2 client displays the dominion entry after `SCDominionDataPacket` (manual or bot probe); tax-rate change round-trips C2G→store→G2C; phase transitions logged at correct weekday/hour for at least one simulated week. Explicitly out: combat, seals/HQ, `siege_items` gating, ownership-transfer battles.

Follow-on slices: 2 = declare flow made real (replace hardcoded `DeclareDominion` values with zone data + monument doodad target + permission checks); 3 = siege membership (`SCSiegeState/Member/Declared`) + ticket purchase; 4 = combat/gates/walls + ownership transfer; 5 = taxes/coffers/payout mail.

## Sharpest UNKNOWN
**How the live 1.2 client actually initiates the whole thing.** The repo defines zero C2G declare/join packets, so whether the client sends a dedicated packet, a skill cast (as skill 13661 implies), or a doodad interaction on the Dominion Statue — and whether `DeclareDominion`'s `target is House` requirement reflects real client behavior or a misreading — cannot be pinned from this repository alone. It needs client-side (game_pak lua/packet-capture) evidence before any declare-flow slice is trustworthy.

---
*Boundary notes: crime/trial/prison → justice-domain.md; flagging/factions/honor/conflict-zones → pvp-domain.md. Cross-ref: `scorecard-explorations/zero-wired-domains.md` §2 (this dossier independently re-verified its claims against `214bed834`; no discrepancies found).*

---

## Addendum A1 (2026-08-25, later) — Client game_pak mining: declare trigger RESOLVED

Method: `game/scriptsbin/x2ui/**` in the deployed 1.2 `game_pak` are **standard Lua 5.1 bytecode** (`.alb`, header `\27LuaQ` v0x51), fully decompiled with unluac → `/root/aaemu-pak-lua/dec/x2ui/` (730 files, zero failures; evidence tree kept session-scoped). Cross-checked against client `Data/compact.sqlite3` (SELECT-only).

**Finding 1 — there is NO declare/join send API anywhere in the client UI.** Exhaustive sweep of every `X2Dominion:*` / `X2Faction:*` / `X2Nation:*` call across all decompiled UI: only getters plus write-APIs `ChangeDominionTaxRate`, `SetNationalTaxRate`, `WithdrawToNation`, `DeclareIndependence` (nation feature, not siege). `expedMgmt.RP.DOMINION_DECLARE = 8` (`dec/x2ui/expedition/expedition_management.lua:13`) and `locale.expedition.joinSiege`/`dominionDeclare` (`baselib/locale_helper.lua:5950-5954`) are **expedition role-policy permission labels**, not buttons. The expedition siege tab (`expedition_management_siege_tab.lua`) is display-only (schedules, participant counts, guard-tower HP). This independently explains why the repo defines zero C2G declare/join packets: the client never sends one.

**Finding 2 — declaration is world-driven via a planted pack.** compact.sqlite3:
+
- `doodad_funcs` id 6855 = `actual_func_type='DoodadFuncDeclareSiege'`, `next_phase=7627`.
- `doodad_func_groups` 7627 = name `"공성 선포 상태"` ("siege-declared state"), model `prefab://prefabs/backpack.xml/backpack.ocom_backpack_b_all`; its `doodad_almighties` 3304 = `"내려놓은 공성 선포 등짐"` ("planted siege-declaration backpack").
- `siege_zones` (6 rows): `monument_doodad_id` = **7229–7234** (the 영지 석상 monuments), `declare_item_id` = "공성 진지 : <zone>" siege-camp items (21130/21134/21135/21136/27744/27745), defense/offense ticket ids ("수호의 인장"/"진격의 인장" seals), declare window columns (`start_declare_weekday=5`, 22:30), auction + payout columns.

So the live flow is: carry/plant the siege-declaration pack at the monument during the declare window → generic putdown/doodad-create traffic (`CSCreateDoodadPacket` family, i.e. item-use skill-driven) → server-side phase transition into `DoodadFuncDeclareSiege` state 7627. Ticket purchase is likewise doodad/merchant-driven (`doodad_func_purchase_siege_tickets` table exists; prices in `siege_ticket_offense_prices`: count=2 per zone, per_price 2–5). No dedicated opcode exists to reverse-engineer.

Verdict on §Sharpest UNKNOWN: **RESOLVED at the trigger level (VERIFIED from client data)** — it is neither a dedicated packet nor a UI button; it is a doodad putdown of the declaration pack, and `DeclareDominion`'s house/pack-target requirement is consistent with real client behavior.
