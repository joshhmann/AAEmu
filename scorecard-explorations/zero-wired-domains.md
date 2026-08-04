# Zero-Wired Domains — Nitty-Gritty Report

**Repo:** /root/aaemu-dev (AAEmu 1.2 fork, .NET 10)
**Data source:** `/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3` (119 MB, 679 tables) on 192.168.0.165 — queried live via ssh+python3 sqlite3
**Runtime DB:** MySQL `aaemu_game` (docker `aaemu-db-1`, mysql 8.0.36) — only **36 tables**; of all 7 domains below, only `music` exists there (0 rows). Nothing else is persisted or loaded at runtime.
**Data flow:** Static 1.2 data is read read-only from `Data/compact.sqlite3` via `SQLite.CreateConnection` (`AAEmu.Game/Utils/DB/SQLite.cs:11-19`; used by AnimationManager.cs:111, ModelManager.cs:83, CharacterManager.cs:103, DoodadManager, etc.). Dynamic state lives in MySQL via `MySQL.CreateConnection` (`AAEmu.Commons/Utils/DB/MySQL.cs:17`). Wiring a domain = add a table load in a manager + (for stateful domains) a MySQL table.

---

## Summary table

| Domain | Tables (rows in 1.2 dump) | Partial code | Est. size | Smallest meaningful slice |
|---|---|---|---|---|
| fx-visuals | 15 tables, 10 non-empty (fx_items 2856, fx_groups 1846, fx_particles 2253, fx_group_fx_items 3162, fx_sounds 422, fx_voices 143, …) | 2 log-only special-effect stubs | **S** (arguably nothing) | Implement/delete `FxGroup`/`FxGroupAnim` stubs; tables are client-only |
| siege | 5 tables + 3 doodad funcs (siege_zones 6, siege_plans 158, siege_settings 11, siege_ticket_offense_prices 10, siege_items 13) | Meaningful: doodad func loads (no-op), DeclareDominion hardcoded broadcast, dominion packet marshalers, ~25 error strings, feature flag ON | **L** (full war) / **M** (declare+own+tax) | Persistent single-castle dominion lifecycle w/o combat: schedule windows, declare at monument, owner+tax state |
| ranks | 5 tables (ranks 4, rank_scopes 40, rank_rewards 40, links 40+40) | 1 orphan packet offset (SCRankRewardMailPacket 0x1f9), 1 error string | **M** | Fishing-contest rank: collect max catch length per player, rank, mail chests |
| premium | 4 tables (premium_benefits 2, premium_configs 1, premium_grades 2, premium_points 0) | Packet surface ~complete; always-on hardcoded state | **M** (shop) / **S-M** (benefits) | Read benefits/grades into manager; drive labor from premium_benefits instead of hardcoded 5000 |
| moulds | 3 tables + 2 doodad funcs — **all 0 rows** | 3 orphan packet offsets, 2 item-task enums, 5 error strings | **S** (data-dead) | None w/ stock data; craft-integration (M) only if data is populated |
| race-tracks | 2 tables (race_tracks 2, race_track_shapes 14) | **Zero** code, zero packets, zero enums | **M** | Single-track time trial: doodad start, loop timer, record, mail racing chest |
| music | 2 tables (instrument_sounds 199, music_note_limits 11) | **Mostly wired already** — full user-song pipeline + playback | **S** | Load instrument_sounds into MusicManager; PlayUserMusic.cs:40 comment names exactly this gap |

---

## 1. fx-visuals

### What the 1.2 data defines
Client-side visual-effect library. 15 tables, 10 populated (all Korean names → client asset refs):
- `fx_groups` (1846): `id, name` — e.g. id 10 `'부유 방울'` (floating bubble), 100 `'어금니 깨물기'` (bite).
- `fx_items` (2856): `id, name, asset_name, fx_event_start_id, fx_event_end_id, fx_location_id, bone_id, offset_x/y/z, fx_detail_id, fx_detail_type, offset_axis_id, fx_scale_id` — asset_name points into client pak (`'abillity_skill_table_m.magic.magic_reinforce_dot4'`), fx_detail_type `'FxParticle'`.
- `fx_group_fx_items` (3162): `fx_group_id → fx_item_id` composition links.
- `fx_particles` (2253): `sound_id, sound_pack_id, in_water, scale`. `fx_sounds` (422): `id, sound_id` (**all NULL** in dump). `fx_voices` (143): `sound_pack_item_name` (`'sv_magic_meteor_form'`).
- Small: `fx_chrs` 12, `fx_shake_cameras` 17 (ang/shift/frequency/duration/RANGE), `fx_ropes` 3, `fx_materials` 3, `fx_motion_blurs` 1, `fx_cam_fovs` 2. Empty: `fx_cgas`, `fx_cgfs`, `fx_decals` (0 rows).

### Partial code
- `AAEmu.Game/Models/Game/Skills/Effects/SpecialEffectType.cs:31-32` — `FxGroup = 35`, `FxGroupAnim = 36`.
- `AAEmu.Game/Models/Game/Skills/Effects/SpecialEffects/FxGroup.cs:21-22` and `FxGroupAnim.cs:22` — both `// TODO ...` + `Logger.Debug` only.
- No manager loads any `fx_*` table; no model classes exist.

### Client-facing behavior
None server-side, by design. The 1.2 client resolves fx ids (skill effects, buff effects, doodad anims) against its own game_pak and plays them locally. The server never needs to know fx asset names; the only server touchpoints are the two special-effect types above, which merely need to not break the skill chain (the client plays visuals regardless).

### Size & smallest slice
**S.** Honest answer: this domain needs ~zero server work. Smallest meaningful slice = implement `FxGroup`/`FxGroupAnim` as no-ops with parameter logging (or wire value1 → fx_group_id for future use). Wiring the tables themselves would be cargo-culting.

---

## 2. siege (dominion war)

### What the 1.2 data defines
Full 1.2 dominion-siege config for the two castle zones, **Salpimari (`o_salpimari`, zone_group 33) and Nuimari (`o_nuimari`, 34)**:
- `siege_zones` (6 rows; 2 real + test rows): `start_siege_weekday/hour/min`, `siege_days/hours/mins`, `pay_weekday/hour/min`, `declare_item_id` (21134 Salpimari / 21130 Nuimari — `'공성 진지'` siege-encampment backpacks), `defense_ticket_id` (21314/21313 `'수호의 인장'` defense seals), `offense_ticket_id` (21318/21317 `'진격의 인장'` offense seals), `reinforce_defense_delay_mins` (20), `defense_merchant_id` (12629/12630), `offense_merchant_id` (51/12633), `dominion_merchant_id` (50/12391), `open_hour` (0), `open_duration_hours` (168), full auction/declare/warmup weekday+hour+min schedules, `monument_doodad_id`.
- `siege_plans` (158): `zone_group_id, week_start` — 2014-dated weekly rotation of which castle is siegeable when.
- `siege_settings` (11): `total_castles, num_defenders, num_reinforcements` (default row `0,70,0`; then per-castle e.g. `10,40,30`).
- `siege_ticket_offense_prices` (10): `count, per_price` volume pricing for offense tickets.
- `siege_items` (13): per-phase usage-restriction matrix for war items (tank mines `26397 '전차 지뢰'`, palisades `26424 '나무 방책'` …) — 12 boolean columns (`outside_siege_zone`, `during_no_dominion`, `during_declare`, `during_peace`, `outside_siege_area_during_warmup/siege`, `offense_hq_*`, `defense_hq_*`, `siege_circle_*`) + Korean `USAGE` message shown to the player when restricted.
- Doodad funcs: `doodad_func_declare_sieges` (1 row, id only), `doodad_func_siege_periods` (8 rows: `siege_period_id, next_phase, defense` — phase chain with next-phase doodad ids), `doodad_func_purchase_siege_tickets` (0 rows).

### Partial code (the most of any domain — but all dead-ends)
- **Doodad func loads exist, funcs are no-ops:** `DoodadManager.cs:979-992` loads `doodad_func_declare_sieges` → `DoodadFuncDeclareSiege` (`DoodadFuncDeclareSiege.cs:15` — `Logger.Trace` only); `DoodadManager.cs:2235-2251` loads `doodad_func_siege_periods` → `DoodadFuncSiegePeriod.cs:15` (trace only).
- **DeclareDominion special effect is half-real:** `DeclareDominion.cs:42-122` builds a complete `DominionData` (TaxRate 50, CurHouseTaxMoney 500000, TerritoryData RadiusDeclare 250 / RadiusDominion 110 / RadiusSiege 250 / RadiusOffenseHq 100, SiegeTimers with SiegePeriod 1) and broadcasts `SCDominionDataPacket`, then consumes the backpack item (line 120). But: **nothing persists, no timer, no ownership store, no war** — the dominion vanishes on restart; the `// Create new dominion data` scaffolding is hardcoded (line 37-38 comments "Get target zone, radius, etc." and "Advance building step on target" are unimplemented).
- **Full packet marshalers exist:** `DominionData.cs` — `DominionData`, `DominionTerritoryData`, `DominionSiegeTimers`, `DominionUnkData`.
- **G2C dominion packets exist:** SCDominionDataPacket 0x1d, SCDominionDeletedPacket 0x1e, SCDominionOwnerChangedPacket 0x1f, SCDominionTaxRatePacket 0x20, SCDominionTaxBalancedPacket 0x23. `CSUpdateDominionTaxRatePacket` is log-only.
- **Siege packet offsets declared, classes absent:** `SCOffsets.cs:229-233` — SCSiegeStatePacket 0xe9, SCSiegeDeclaredPacket 0xea, SCSiegeReinforcePacket 0xeb, SCSiegeMemberPacket 0xec, SCSiegeAlertPacket 0xed. **Zero C2G siege packets** registered (`GameNetwork.cs` has no Siege registrations).
- **Stubs:** `GetSiegeTicket.cs:22`, `TeleportToSiegeHq.cs:22` (SpecialEffectType 64/65) log-only. `BuySiegeTicket.cs:15` just forwards to `doodad.Use`. `WorldInteraction.cs:116-117` DeclareSiege=103, BuySiegeTicket=104; `BackpackType.cs:8` SiegeDeclare=4; `ItemManager.cs:971` loads `declare_siege_zone_group_id`.
- **Error surface complete:** `ErrorMessageType.cs:342-348, 360, 386, 431-452, 456-465, 469, 484, 568` — ~25 siege/dominion strings (siege_declare_no_dominion, siege_participant_full, siege_war_period_only, siege_master_only …).
- **Feature flag ON:** `Feature.cs:7` `siege = 0` (fset[0]&1), sent enabled in `SCInitialConfigPacket.cs:13,49`.
- **Periphery:** `ShipSiegeAoEHit.cs` (ship-vs-siege-circle AoE math, wired into Skill.cs:928-950); `ExpeditionManager.cs:136,143-144,352` loads `dominion_declare`/`siege_master`/`join_siege` perms; dominion unit-reqs (`UnitReqsKindType.cs:59-94`) are marked unused and commented out (`UnitReqs.cs:247`).
- **No** CastleManager / SiegeManager / DominionManager; no MySQL persistence tables; no timers/scheduler.

### Client-facing behavior needed
1.2 dominion war: expeditions claim castles by planting the siege-encampment backpack at the monument during the declare window → dominion ownership with **house-tax rate control** (owner expedition sets national tax; taxes paid into dominion coffers) → weekly **war window** (warmup → siege) where offense/defense seals grant entry, HQ placement, siege-item usage gated by `siege_items` matrix, reinforcement delay, pirate exclusion → winner takes ownership, mail/tax payouts. `siege_plans` rotates which castle is active per week.

### Size & smallest slice
**L** full (war combat rules, ownership transfer, tax economy, 5 packet classes, scheduling engine, MySQL persistence). **M** for declare+own+tax without combat.
Smallest meaningful slice (M-): a `DominionManager` that (1) loads `siege_zones` + `siege_plans` into memory, (2) implements the declare flow end-to-end for one castle: monument doodad phase → consume backpack → persist owner/expedition + timestamps (new MySQL table) → broadcast SCDominionData/OwnerChanged, (3) a cron tick that flips siege/peace phases and broadcasts SCSiegeAlertPacket per `siege_zones` schedule, (4) tax-rate update via existing CSUpdateDominionTaxRate + SCDominionTaxRate. Combat, seals, HQ, reinforcement and `siege_items` gating can all come later.

---

## 3. ranks (contests + rank rewards)

### What the 1.2 data defines
This is **not a ladder/ELO system** — it's the 1.2 **weekly contest ranking** system (fishing contests, racing contest, arena) with mail-out rewards:
- `ranks` (4 rows): contest definitions with full windows — `검투장 지배자` (Arena Dominator; zone_group 77 `instance_training_camp_1on1`, metrics `'검투장 점수'`/`'검투장 승리 횟수'`), `월척 낚시꾼` (Big Catch Fisherman; zone_group 49 `arche_mall` Mirage Island, metric `'최고 길이'` max length, weekly Sun 20:00-22:00, `reset_week` 1, start/end alarm strings `'잠시후 신기루 낚시 대회가 시작됩니다.'`/`'신기루 섬 낚시대회가 종료되었습니다.'`), `싹쓸이 낚시꾼` (Sweep Fisherman; zone_group 3 `w_garangdol_plains`, metric `'무게 합산'` total weight). Columns: `st_*/ed_*` datetime, `day_of_week_id`, `start_time/end_time`, `rank_kind_id`, `reset_week`, `v1/v2` (metric names), alarm msg columns.
- `rank_scopes` (40): rank **brackets by position** per contest — `'신화' 1~10`, `'전설' 11~20`, `'서사' 21~50`, `'경이' 51~100`, `'유물' 101~300`, `'유일' 301~500`, `'영웅' 501~1000`, `'고대' 1001~2000`, `'희귀' 2001~3000`, `'고급' 3001~5000` (e.g. fishing-length scopes 11-20, fishing-weight scopes 21-30, racing scopes 31-40, battlefield scopes 1-10 with flavor comments `'20등까지 상위권'`…).
- `rank_rewards` (40): bracket name → reward chest item, `weeks` (1 or 4): fishing chests 30219-30238 (`'낚시 대회 보상 상자 : 신화'` …), **racing chests 30239-30248 (`'레이싱 대회 순위 보상'`)**, battlefield chests 32488-32498 (weeks=4). `appellation_id` all NULL in dump.
- `rank_reward_links` (40) / `rank_scope_links` (40): rank_id ↔ scope_id ↔ reward_id joins.

### Partial code
- `SCOffsets.cs:492` — `SCRankRewardMailPacket = 0x1f9` (**no packet class exists**).
- `ErrorMessageType.cs:748` — `RankRewardSent = 769` ("rank_reward_sent").
- Nothing else. No manager, no persistence, no metric tracking. (Fishing itself is wired — `FishingLoot.cs`, `FishSchoolManager`, `DoodadFuncConvertFish`, fish-length items — but **no per-player catch-length/weight metric is collected anywhere**.)

### Client-facing behavior needed
Recurring contests on a schedule (`ranks`): players fish/race/arena during the window; server ranks participants by the contest metric (`v1/v2`), maps rank position → scope bracket → reward, and **mails the reward chests** (SCRankRewardMailPacket) at window end with alarm broadcasts at start/end. The client shows contest banners; all ranking math is server-side.

### Size & smallest slice
**M.** Smallest meaningful slice (M-): **fishing max-length contest only** — (1) `RankManager` loads `ranks`/`rank_scopes`/`rank_rewards`/links from sqlite; (2) cron ticks contest windows from `ranks` rows and broadcasts the alarm strings; (3) record each player's best caught-fish length during the window (hook into the fishing loot path — needs a length source, e.g. from `FishDetailsGameData`/fish items, or use a server-side roll), (4) at window end, bucket into scopes, mail chests via a new SCRankRewardMailPacket implementation + MySQL persistence of standings. Racing/battlefield contests are independent follow-ons (racing needs race-tracks wired first — see below).

---

## 4. premium (patron service)

### What the 1.2 data defines
- `premium_benefits` (2): grade 1 → `online_labor 5, offline_labor 0, max_labor 2000, icon_id 0`; grade 2 → `online_labor 10, offline_labor 5, max_labor 5000`.
- `premium_configs` (1): `max_grade 1, connect_point 1, disconnect_point 0, deactivate_point 0, max_point 1` (premium-point accrual rules: +1 point on connect, nothing on disconnect/deactivate, cap 1 point → max grade 1… note max_grade=1 conflicts with 2 grades existing).
- `premium_grades` (2): `grade_id 1 → point 0`, `grade_id 2 → point 1` (grade thresholds by accumulated points).
- `premium_points` (0 rows): `id, name, premium_id, time, grade, sell_type, money` — purchasable premium-time products (empty in dump).

### Partial code (packet surface is ~complete; state is hardcoded)
- `AccountPayment.cs:11-23` — **premium is always-on for every account**: `Method = PaymentMethodType.Premium`, `StartTime = DateTime.MinValue`, `EndTime = 2030-01-01`, `PremiumState` = time-window check. Instantiated at `GameConnection.cs:45`; **nothing ever loads/persists it** (AccountManager has no Payment fields).
- `TimedRewardsManager.cs:15,22-24,35,64,72,81,105` — uses `PremiumState` for labor cap (`MaxLaborPremium = 5000` hardcoded — which exactly matches `premium_benefits` grade 2's `max_labor 5000`) and per-tick labor/credits/loyalty amounts from `Configurations.cs:239-243` (`TickAmountPremium`).
- **Service shop stubbed:** `CSPremiumServiceListPacket.cs:16-20` replies with a hardcoded Russian premium product (CId 8000001, "Премиум-подпиcка (30 дней)", PId 1, 720h); `CSPremiumServiceBuyPacket.cs:12` log-only; `CSPremiumServiceMsgPacket.cs:12-13` sends `SCAccountWarnedPacket(2, "Premium ...")`.
- Packets all exist: SCPremiumServiceListPacket 0x1d8, SCUpdatePremiumPointPacket 0x201, SCPremiumPointChangedPacket 0x202 (sent hardcoded `(1,1,1)` at `FinishStatePacket.cs:71`).
- Feature flag `premium = 4` (`Feature.cs:11`), enabled (`SCInitialConfigPacket.cs:14,48`); `SCWorldQueuePacket` carries `isPremium`; error strings 772-775 (`premium_service_buy_success/fail/not_enough_aa_cash/not_enough_aa_point`); `ItemTaskType.BuyPremiumService = 115`.
- **No table is read.** No MySQL persistence (no premium state table in aaemu_game).

### Client-facing behavior needed
Premium patron service: buy premium time with AA cash/points (CSPremiumServiceBuy → premium_points products), premium grade = accumulated premium points (connect/disconnect accrual per `premium_configs`), benefits per grade (`premium_benefits`: labor regen 5→10/h, offline 0→5, max labor 2000→5000), premium queue priority (`SCWorldQueuePacket.isPremium`), labor-tick scaling (`TickAmountPremium`), and the premium service list UI (SCPremiumServiceListPacket with real products instead of the Russian stub).

### Size & smallest slice
**M** full (shop + points + persistence), **S-M** for benefits-only.
Smallest meaningful slice (S-M): (1) `PremiumManager` loading `premium_configs`/`premium_grades`/`premium_benefits`; (2) replace `TimedRewardsManager.MaxLaborPremium = 5000` and tick amounts with lookups from `premium_benefits` keyed by the account's grade; (3) persist grade/expiry in MySQL (new `account_premium` table or columns) and populate `AccountPayment` from it at login; (4) implement `CSPremiumServiceBuy` to grant time for the products in `premium_points` (once populated). The packet layer needs almost nothing new.

---

## 5. moulds (crafting moulds)

### What the 1.2 data defines
**All tables are empty (0 rows)** in the 1.2 dump: `moulds` (`id, name, craft_id, delay`), `mould_packs` (`id, name`), `mould_pack_items` (`id, mould_pack_id, mould_id`), plus `doodad_func_moulds` (id only) and `doodad_func_mould_items` (`doodad_func_mould_id, mould_pack_id`). The feature exists in the schema but was never populated for 1.2 (moulds became a real crafting mechanic in later versions — a mould item placed in the craft window alters the recipe output).

### Partial code
- `SCOffsets.cs:482-484` — SCMouldListPacket 0x1ee, SCMouldAskedPacket 0x1ef, SCMouldTakenPacket 0x1f0 (**no packet classes**).
- `ItemTaskType.cs:112-113` — `AskMould = 106`, `TakeMould = 107`.
- `ErrorMessageType.cs:693-697` — ChTransferHasMould 712, CraftNotHasMould 713, CraftAlreadyMould 714, CraftMouldNotFound 715, CraftMouldNotReady 716.
- `DoodadManager` does **not** load `doodad_func_moulds`/`doodad_func_mould_items` (no DoodadFuncMould class exists).

### Client-facing behavior needed
In later versions: crafting UI mould slot — pick a mould pack, "ask mould" applies it to the active craft, changing the output/grade; "take mould" returns it. With zero 1.2 data, no client flow is even reachable on stock data.

### Size & smallest slice
**S** against stock data (there is literally nothing to wire — 0 rows everywhere). If this is wanted regardless (server-authored moulds): **M** — requires craft-system integration (`CraftManager` recipe modification), the three SCMould* packets, and a hand-populated `moulds`/`mould_pack*` set. Recommendation: deprioritize; the domain is data-dead in 1.2.

---

## 6. race-tracks (Mirage Island racing)

### What the 1.2 data defines
- `race_tracks` (2 rows): id 1 `'test_신기루섬'` (zone 108 `instance_silent_colossus` — test track, all zeros) and id 2 `'신기루섬 경주'` (zone 183 `arche_mall` = Mirage Island, the real one): `race_loop 300000` (ms — 5-min loop?), `record_min 10000` (ms — 10 s best-record floor), `doodad_id 7369` (race registration doodad), `wait_delay 60000`, `ready_delay 60000`, `start_delay 5000`, `doodad_group_id 81`, `ready_npc_id/ready_buff_id/start_npc_id/start_buff_id/end_npc_id/end_buff_id` all 0/NULL.
- `race_track_shapes` (14 rows): `id, race_track_id, shape_order, v1` — 4 columns only; rows like `(10, 2, 5, 5)` (track 2 has shape_order 1..~7; v1 mirrors shape_order). The dump carries **no polygon geometry** — the actual track path lives client-side; `v1` is likely a checkpoint/type marker. A server implementation cannot reconstruct the course from this table alone.

### Partial code
**None.** `grep -rni 'racetrack\|race_track\|RaceTrack' AAEmu.Game/ --include='*.cs'` → 0 matches. No manager, no packets, no offsets, no enums, no doodad func.

### Client-facing behavior needed
1.2 Mirage Island racing: players register at the race doodad (7369) → wait/ready/start countdown phases (`wait_delay`/`ready_delay`/`start_delay`) → race N loops of the course (`race_loop`) on mounts → finish times recorded (best time ≥ `record_min`) → standings feed the **racing contest ranks** (rank_rewards 30239-30248 `'레이싱 대회 순위 보상'` — see ranks section; rank_scopes 31-40 are the racing brackets). Coupled to the ranks domain.

### Size & smallest slice
**M.** Smallest meaningful slice (M-): `RaceManager` for a single track — (1) load `race_tracks`/`race_track_shapes`; (2) doodad interaction (7369) registers the player and runs the 3-phase countdown (buff-based ready/start per the nullable buff columns); (3) loop counting via zone checkpoints (server-defined, since shapes carry no geometry) or a lap timer on a trigger volume; (4) persist best times (new MySQL table) and expose them to the ranks pipeline for the racing contest; (5) start/end NPC + buff wiring from the track columns. Real geometry-based lap validation is the hard part; a trigger-volume lap counter is the pragmatic 1.2 approach.

---

## 7. music (instrument sounds + note limits)

### What the 1.2 data defines
- `instrument_sounds` (199 rows): `id, item_id, midi` — maps each instrument item to its MIDI program: 20488 `'에페리움의 물결 현악기'` (Eperium Wave Lute) → midi 29, 23161 `'로즈메린의 관악기'` (Rosemerin Wind) → 73, 23214 `'누군가의 피리'` → 73, 26134 `'아슈칼툼 피리'` → 73, etc.
- `music_note_limits` (11 rows): `id, step, note_length` — step 0→200 ms … step 4→1000 ms, steps 5-10 all 1000 ms. Client-side note-duration caps per musical step (octave/semitone ladder).

### Partial code — **this domain is already ~90% wired; only the two tables are unread**
- `MusicManager.cs` (163 lines, functional): loads/saves user songs from MySQL `music` table (`Load()` :27-49, `Save()` :51-78), upload queue (`UploadSong` :80-91), `CreateSheetMusic` (:93-142, consumes music paper, creates `MusicSheetItem`), midi cache (:151-162). (MySQL `music` exists, 0 rows.)
- Packets all implemented: `CSSaveUserMusicNotesPacket.cs` (uploads song), `CSRequestMusicNotesPacket.cs` (requests → `SCUserNotesLoadedPacket` or `ScoreMemorized` buff), `SCSendUserMusicPacket`/`SCPauseUserMusicPacket`/`SCUserNotesLoadedPacket` classes exist.
- Playback: `PlayUserMusic.cs:32-63` broadcasts the midi cache to nearby players and applies instrument buffs — but **hardcodes the instrument mapping by ItemCategory switch** (Lute→LutePlay, Flute→FlutePlay, comment at :40: *"I'm sure we can get this relation info from the tables somewhere, but can't find it"* — that relation **is** `instrument_sounds`); `PauseUserMusic.cs` counterpart exists.
- Periphery: `EquipmentItemSlotType.Instrument = 21` (EquipmentContainer.cs:85), `PlotConditionType.InstrumentType = 10` implemented (`PlotCondition.cs:189`), `ItemTaskType.SaveMusicNotes = 93`, `ErrorMessageType.MustEquipInstrumentItem = 641`, `UnitReqsKindType.EquipRanged` comment re: instruments (:35).

### Client-facing behavior needed
Instrument play: the client enforces per-instrument note ranges and note lengths locally (from its own pak — `music_note_limits` is client-side constraint data; the server does not need it). The server's jobs are (a) relay user-music midi streams to nearby players (done), (b) apply the correct play buff per instrument (done via the ItemCategory switch, but improvable via `instrument_sounds`), (c) persist song sheets (done).

### Size & smallest slice
**S.** Smallest meaningful slice: add an `instrument_sounds` load to `MusicManager` and use the `item_id → midi` mapping in `PlayUserMusic` (replacing or augmenting the category switch), so every instrument item (e.g. 26134 Ashkaltum flute, currently falling into `default:` and logging a trace) gets the right buff. `music_note_limits` needs **no server work** (client-side). This domain is essentially "finish the last 10%" + data loader.

---

## Cross-domain notes

1. **ranks ↔ race-tracks are coupled**: racing contest rewards (30239-30248) and racing brackets (rank_scopes 31-40) depend on race results, so racing ranks need RaceManager output. Fishing ranks need a catch-metric hook that doesn't exist yet. Battlefield/arena ranks (32488-32498, weeks=4) depend on arena/instance infrastructure.
2. **siege ↔ dominion**: the 1.2 dominion data (DeclareDominion, DominionData marshalers, SCDominion* packets) is the siege precursor already half-built; `siege_zones` even names `dominion_merchant_id`. Wiring siege without finishing the dominion ownership/persistence layer first is building on sand.
3. **Data flow asymmetry**: static 1.2 defs are read from `compact.sqlite3` via `SQLite.CreateConnection`; state (dominion ownership, premium grants, rank standings, race records) needs **new MySQL tables** — the runtime DB currently has none for these domains.
4. **premium is quietly always-on** in this fork (AccountPayment defaults: Premium + end 2030) — the labor cap of 5000 hardcodes `premium_benefits` grade 2. Any premium work must decide whether to keep always-on as a server setting.
5. Recommended order by value/effort: **music (S) → fx-visuals (S) → premium benefits (S-M) → ranks fishing contest (M-) → siege declare+own (M-) → race-tracks (M) → moulds (skip; data-dead)**.

*Report generated 2026-08-03. All row counts/samples from live queries against 192.168.0.165 compact.sqlite3 and aaemu-db-1 MySQL; all code claims verified by grep against /root/aaemu-dev.*
