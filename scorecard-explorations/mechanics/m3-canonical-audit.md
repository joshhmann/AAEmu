# M3 Canonical Circle-Back Audit — HOUSING-01 / FARM-01 / PROPERTY-01 vs live 1.2

**Task:** t_f564d986 (M3 canonical circle-back, Josh ruling 2026-08-11 22:53 — M3 was pivotable; same standard as M4)
**Branch:** m3-canonical-audit (from origin/develop @ a31826b74, post-M3b deploy)
**Date:** 2026-08-11
**Scope:** evidence + scorecard C-dimension updates only; findings become fix cards (Tai) with Rei gates; Rei gates this audit itself.
**Ground truth:** fork `develop` @ a31826b74 (M3a merge 4d0427b96 + M3b-1..4 merges 5dc7c2fbd/71b43e09f/3913932bf/5981246ea + EXIT f5b00c686); canonical 1.2 data surface = `AAEmu.Game/Data/compact.sqlite3` (r208022); client pak `game_pak` (Feb 2023 build — same file the housing dossier used).

---

## 0. TL;DR

| Mechanic | Verdict | Canonical basis | Engine status on develop | C before → after |
|---|---|---|---|---|
| HOUSING-01 placement-zone validation | **Covered by dossier + implemented zone-level** | pak `housing_area.xml` polygons + sqlite `housing_areas`/`housing_groups`/`housing_group_categories` (dossier §2) | `HousingPlacementValidator` (zone-level, not polygon-level) wired into `Build`/`ConstructHouseTax` | 2 → 2 (caveat: polygon containment still open) |
| HOUSING-01 deco limits | **M3a RESOLVED the partial-domains "dead weight" finding** | `housings.deco_limit` (uniform 40) / `absolute_deco_limit` (60, mansion 208) / `housing_deco_limit_id` → `housing_deco_limits` (12) → `housing_deco_limit_elems` (23) | `DecorationLimitEvaluator` + loads at `HousingGameData.cs:240,258` + `DecorateHouse` wiring :1720; errors 124/628 | 2 → 2 (partial-domains.md stale claims need scrub — fix card) |
| FARM-01 crops | **Data-verified canonical loop** | potato seed 15659 → doodad 2259 → seedling 4379 (growth 583: 60 s) → small 4456 (growth 584: 9 min) → mature 4457 → loot pack 6452 (potato 2–4, golden 1, seed 1) | `DoodadFuncCropHarvest`/`DoodadFuncFruitPick` → loot phase; `CropHarvestLoopTests` 6/6 | 2 → 2 (rot timer + watering engine path not test-covered — watch items) |
| FARM-01 livestock | **Growth chain data-verified; interaction funcs are STUBS** | dairy calf 2672: 5780 → growth 791 (12,348,000 ms = 3.43 h) → 5781 → growth 792 (111,132,000 ms = 30.87 h) → 12774 mature cow → milking/butcher chains (5782→5786/5790, loot packs 79/80/81) | growth + restart recovery work (M3b-2, `PhaseStateRestartRecoveryTests` 8/8); **`DoodadFuncFeed`/`DoodadFuncDairyCollect`/`DoodadFuncShear`/`DoodadFuncButcher` are log-only stubs** | 2 → 2 for curated potato loop; **livestock interaction = fix card** |
| PROPERTY-01 persistence | **Canonical semantics identified; R=2 proven** | what 1.2 persists: housings row + persistent doodads row (phase/plant/growth times, transform, owner_type, attach_point, item links, coffer container); what it drops on death: nothing property-related; on demolish: furniture + design returned by mail (wiki + code) | M3b-1..4: furniture rows, phase state, restart restore, orphan/dup prevention, repair tooling; EXIT E2E N=3 crash cycles 16 rows/boot | **U → 2** |

**Overarching deviations found (all fix-card material):**
1. **Livestock interaction funcs are stubs** (feed/milk/shear/butcher) — canonical data exists and is loadable; only growth + phase recovery work. FARM-01's "livestock" half is not player-functional.
2. **Placement validation is zone-level, not polygon-level** — canonical 1.2 checks point-in-polygon against `housing_area.xml` AreaShapes (dossier §2.1); the merged validator checks zone-name membership only (`HousingLandZoneInfo` documents the simplification). Overlap uses garden-radius circles (canonical); terrain 115/116, unit overlap 114, race protection, and `max_construct_count` (loaded, never enforced) remain open.
3. **partial-domains.md deco-limit claims are stale on develop** — lines 46/55/58/163 still call `housing_deco_limits` "dead weight" and "no server-side count check", but M3a wired them. Doc scrub needed.
4. **Fork deliberately disables unpaid-tax demolish** (`HousingManager.cs:650–658` commented, ZeromusXYZ note) — canonical 1.2 demolishes after tax default and mails the design back (wiki-confirmed); fork keeps this off (documented fork choice, note for Josh).

---

## 1. HOUSING-01 — placement-zone validation (dossier verify)

### 1.1 Dossier coverage — VERIFIED FULLY COVERED

`scorecard-explorations/mechanics/housing-placement.md` (branch `housing-placement-dossier` @ 53d748585, **not yet merged to develop** — see §6) covers, against the task's checklist:

| Task checklist item | Dossier section | Verdict |
|---|---|---|
| placement-zone validation | §2.1–2.6 (pak polygons, housing_areas/groups/categories matrix, error codes 112–118/229/340/341, reconstruction of 1.2 decision procedure) | **covered** |
| ownership/permissions | §3 claim flow + persistence fields; §4 4-value permission enum + AllowedToInteract | **covered** |
| construction steps | §6 gap 9 + §3 (CurrentStep/NumAction, housing_build_steps, CraftEffect) | **covered** |
| demolish | §3 Demolish (owner-only, mail returns, Monstrosity conversion, unpaid-tax block disabled) | **covered** |
| deco limits | §2.5 (deco_limit/absolute_deco_limit loaded-but-unused as of dossier date) | **covered at dossier date; now superseded by M3a (see §1.3)** |
| wire surface | §5 (CSCreateHousePacket 0x057, SCMyHousePacket 0xc1, error keys) | **covered** |

Dossier data claims re-verified against compact.sqlite3 on this branch (identical file): `housing_areas` 401 rows, `housing_groups` 15, `housing_group_categories` 33, `housings` 269 — all match. Zone-name join key re-verified: 388/401 `housing_areas` rows join `zones` by exact `name` (13 non-joiners = legacy/typo/deleted — e.g. `w_golden_plains_1` rows carry `LevelDesignShape_151_*` comments, zones join by name not comment). **Dossier is accurate and complete for its date.**

### 1.2 What M3a actually merged (vs dossier's engine-gap table)

M3a merge 4d0427b96 added the dossier's §8 recommendations 1–4 **in zone-level form**:

- `HousingLandZoneInfo` (64 lines): loads `housing_areas` + `housing_groups` + `housing_group_categories`, builds zone-name → land-zone map. **Key deviation:** canonical containment is per-polygon (pak AreaShapes); the merged code validates at **zone granularity** — any position inside a housing zone passes the area check. The class comment documents this honestly ("no positional plot shapes… enforced at ZONE granularity").
- `HousingPlacementValidator` (182 lines): land-zone check → faction gate (zone faction vs char mother faction) → houseless-only (groups 12/13) → category rule (union of `AllowedCategories`; group 11 "nothing" rejects all) → overlap (garden-radius sum, floor `MinHouseSpacing` 5 m). Evaluation order matches the dossier's reconstruction (§2.6 steps 1–5 minus terrain/unit).
- `HousingManager.Build` + `ConstructHouseTax` wiring: zone key via `worldManager.GetZoneId` → `zoneManager.GetZoneByKey` → `GetLandZoneByZoneName(zone.Name)`; rejects with `HouseCannotLocateInvalidArea` (112) or `HouseCannotLocateOverlapHouse` (113). **Exactly the two client strings a 1.2 player saw** ("Building doesn't fit in this area", reddit 2015-02-14 — cited in dossier §2.6).
- `CraftEffect` construction + `HousingM3aConstructionTests` 18/18 + `HomesteadPlacementScenarioTests` 29/29 + `M3aExitScenarioTests` (two actors, adjacent 16 m, 10 m overlap REJECTED).

**Still open from the dossier gap table (unchanged on develop):** polygon containment (gap 1 partial), terrain 115/116 (gap 3), unit/NPC overlap 114 (gap 5), race protection (gap 6), `max_construct_count` (loaded `HousingGameData.cs:314`, never enforced — error 766 `house_cannot_construct_in_area_by_max_construct_count` never sent), 229/340/341 codes.

### 1.3 Deco limits — partial-domains "dead weight" claim: CONFIRMED RESOLVED by M3a

The task asked to confirm 1.2 behavior for `housing_deco_limits`. Verified against data + code:

- **1.2 data:** `housings.deco_limit` is uniform **40** (268/269 rows); `absolute_deco_limit` varies (60 typical small/medium, 208 mansion, 51 thatched-farmhouse, 105–161 guild/test); `housing_deco_limit_id` set for 100+ real houses (group 1 "아담한 누이아 주택" … group 9 "누이아 저택", 12 groups total). `housing_deco_limit_elems` = 23 per-(group, deco_actability_group) allowances (e.g. (1,1)→3, (9,1)→5). `deco_actability_groups` = 6 (전문 가구, 전문 가구 (소품), 반려동물, 저택 전용 가구, 보관함, 마력 갈무리 기계).
- **M3a implementation:** `DecorationLimitEvaluator.IsDecorationAllowed` enforces absolute cap → per-actability-group allowance → deco_limit backstop, with client strings `house_too_many_decorations` (124) and `housing_actability_deco_limited` (628); loads at `HousingGameData.cs:240` (`housing_deco_limits`) and `:258` (`housing_deco_limit_elems`); wired in `DecorateHouse` (`HousingManager.cs:1720`).
- **Verdict: the partial-domains "dead weight" finding (2026-08-03) is RESOLVED on develop.** The doc itself is stale — see §6 fix card FIX-3.

---

## 2. FARM-01 — crops (growth timers, harvest semantics)

### 2.1 Crop growth timers — DATA-VERIFIED

Canonical potato loop, every id re-verified from compact.sqlite3 on this branch:

| Step | Phase group | Func | Delay (ms) | → |
|---|---|---|---|---|
| seed item 15659 (감자 씨앗) → spawn doodad 2259 (감자) | `item_spawn_doodads` (15659 → 2259) | — | — | seedling 4379 |
| seedling → small | 4379 has phase func `DoodadFuncGrowth` 583 | 60,000 (1 min) | 4456 |
| small → mature | 4456 has phase func `DoodadFuncGrowth` 584 | 540,000 (9 min) | 4457 |
| mature (감자) | 4457: `DoodadFuncHouseFarm` 144 + `DoodadFuncTimer` 1350 | 174,000,000 (48.33 h — **rot timer**) | 10042 (wilted, ratio-change chain) |
| harvest | 4457 `DoodadFuncUse` 1047 → 4458 `DoodadFuncLootPack` 129 → 4459; loot pack 6452 | — | potato 7992 ×2–4, golden potato 19887 ×1, seed ×1 |

- **Total grow time = 10 minutes at GrowthRate 1.0** (`World.json:15`). Cross-check: archeagecodex item 15659 tooltip — "**Matures in approx. 10m**" (codex mirrors 1.2-era tooltips; retrieved 2026-08-11). The reddit 2014 claim of "30 minute growth time" for potatoes is **contradicted by both the data and the codex** — flag as research-derived, superseded by data.
- Climate bonus: `DoodadFuncGrowth` multiplies delay by 0.73 when `DoodadHasMatchingClimate` — this factor comes from **upstream AAEmu PR #744** ("Fixes doodad related timings"), not from 1.2 data. Consistent with IGN wiki (2014-10-18): "If you place your crops… in the right climate then they will grow/mature faster than the time stated on the tooltip." → **research-derived (upstream), directionally canonical.**
- GrowthRate config is a fork/upstream server knob (1.0 default) — not a 1.2-data value; fine as config.
- **M3a crop fix:** `DoodadFuncCropHarvest`/`DoodadFuncFruitPick` now advance to the loot phase (`ToNextPhase = true`) — without this the mature phase never reached its loot group (M3a-3, ccad02768). Semantics match 1.2: harvest skill 13980 (작물 수확) → loot pack → yield variance ("yield of each plant will vary, occasionally a rare seed or crop" — quest 4417 text).

### 2.2 Watering — DATA-VERIFIED chain, engine path present, not test-covered

- Skill 10126 물 주기 (watering) → `InteractionEffect` 1461; the potato chain carries `DoodadFuncSkillHit` 174 (skill 15601 물 뿌리기) on seedling 4379 → next_phase 4456. So 1.2 data encodes "water advances the seedling to the next stage" — consistent with quest 4417 ("Water your plant to help it grow").
- Engine: `DoodadFuncSkillHit` is implemented (casts the skill on the doodad). **No M3a/M3b test drives the watering path** — watch item, not a defect.

### 2.3 Rot / wilt — DATA-VERIFIED, engine machinery exists, not test-covered

- Mature potato carries `DoodadFuncTimer` 1350 (48.33 h) → wilted phase 10042 (ratio changes 408/409 + timer 3403 → 6112). So 1.2 data has a **harvest-window rot mechanic**: unharvested crops wilt after ~2 days. `DoodadFuncTimer` is the same phase-func machinery proven by the door-revert test (M3b-2) — the timer will fire; no test pins the crop-rot chain specifically. Watch item.

### 2.4 Public farms — DATA-VERIFIED guard time

`common_farms.guard_time` = **86,400,000 ms = 24 h** — matches IGN (2014-10-18): "whatever you've planted will be protected for 24 hours." (Quest 4417's "protect your crops for three days" is era-2/quest-flavor text; the r208022 data says 24 h.) `PublicFarmManager` gates via subzones (dossier §2.7) — unchanged on develop.

---

## 3. FARM-01 — livestock (data-verified chains; interaction STUBS)

### 3.1 Canonical 1.2 dairy-calf chain (verified from data)

Doodad 2672 젖소 송아지 (dairy calf):

| Phase group | Name (KR) | Phase funcs | Delay | → |
|---|---|---|---|---|
| 5780 | 작은 젖소 송아지 (small calf) | `DoodadFuncGrowth` 791 + Animate 189 | 12,348,000 ms = **3.43 h** | 5781 |
| 5781 | 젖소 송아지 (calf) | `DoodadFuncGrowth` 792 + Animate 191; Use 496 → 12786 (butcher-calf loot chain) | 111,132,000 ms = **30.87 h** | 12774 |
| 12774 | (mature cow) | Animate 1345 + RatioChange 853–855 + Timer 6598 (500 ms) | — | 5782 |
| 5782 | 젖소 (cow) | Timer 3946 (345,600,000 ms = **4-day milk cycle**) + Animate 192 + HouseFarm 21; Use 497 → 5786 happy cow, Use 498 → 5790 butchered | — | 5783 / 5786 / 5790 |
| 5786 | 행복한 젖소 (happy cow) | Timer 1220 (4 days → back to 5782); Use 501 → 8436 milked cow → LootPack 81 | — | milk 8055 (우유) etc. |
| 5790 | 도축된 젖소 (butchered) | LootPack 79/80 → 9907 (beef 8048 소고기 via LootItem 2604) | — | — |

Total calf→cow = **~34.3 h**; milk every **4 days**; butcher yields beef. Feeds exist in data (`doodad_func_feeds`: 11 rows, e.g. feed item 14310, count 0…; `doodad_func_livestock_growths` table present but **0 rows** — the growth delays live in `doodad_func_growths`, which is what the engine reads).

### 3.2 Engine status — growth works, interaction funcs are STUBS

- **Works (M3b-2):** `DoodadFuncGrowth` drives the calf chain; `PhaseStateRestartRecoveryTests` pins calf 5780 mid-growth recovery (remaining-time resume), overdue catch-up, no duplication (8/8).
- **STUBS (log-only `Use()`, no behavior):** `DoodadFuncFeed` (item_id/count loaded, nothing consumed), `DoodadFuncDairyCollect`, `DoodadFuncShear` (ShearTerm loaded, unused), `DoodadFuncButcher` (CorpseModel loaded, unused). All are loaded by `DoodadManager` (lines ~334/970/1183/1262) so the data is present — the interactions simply do nothing.
- **Impact:** FARM-01's scope text is "Place, grow, harvest, and recover curated **crops/livestock**". A player can plant a calf, watch it grow, and recover it across restarts — but **cannot feed, milk, shear, or butcher** any livestock. The scorecard grades FARM-01 C/W/H/A = 2 for the curated **potato** loop; livestock deserves its own fix card (FIX-1).

---

## 4. PROPERTY-01 — furniture/storage/phase/attachment persistence vs 1.2

### 4.1 What 1.2 actually persists (canonical contract)

Reconstructed from the 1.2-era data model + MySQL schema + client wire (all verified):

| Object | Persisted state (1.2) | Where (fork) | M3b evidence |
|---|---|---|---|
| House | owner/co-owner/account, template, transform (x/y/z/yaw/pitch/roll), build step (`current_step`), permission, place/protection dates, sell state | MySQL `housings` (aaemu_game.sql:286) | EXIT E2E 16 rows/boot; `ShouldLoadHouseRow` (M3b-3 11/11) |
| Furniture (decorations) | doodad row: template, **house-relative local transform**, attach point, owner, item link | MySQL `doodads` (owner_type=Housing, attach_point, house_id, item_id, item_template_id) | M3b-1 `M3bFurniturePersistenceE2eTests` 7/7 rows ×2 SIGKILL; local-position seeding (ae2b67939) |
| Crops/livestock phase | doodad row: `current_phase_id`, `plant_time`, `growth_time`, `phase_time`, transform | MySQL `doodads` | M3b-2 `PhaseStateRestartRecoveryTests` 8/8 (remaining-time resume, overdue catch-up, door revert timer) |
| Door/window phase | phase id + phase_time (open/closed + revert timer) | same doodads row | M3b-2 door tests (1.6 s revert restored) |
| Storage (coffers) | item container bound to doodad (`item_container_id`), capacity from `doodad_func_coffers` (10/20/50/100), perms from `doodad_func_coffer_perms` | `CofferContainer` + `OpenCofferDoodad` | M3b-1..4 E2E includes coffer rows; W=2 |
| Attachment integrity | parent_doodad, house_id, attach_point | doodads row | M3b-3 `ShouldLoadPersistentDoodad` (orphan skip); EXIT E2E attachment assert |

### 4.2 What 1.2 drops on death / restart / demolish

- **Death:** nothing property-related — death penalty is XP/durability/labor, not property. No 1.2 mechanic removes housing/furniture/crops on player death (no data table, no wire code; consistent with Wikipedia ArcheAge housing description).
- **Restart (server):** everything persists — the M3b EXIT gate proves N=3 crash cycles (restart, kill -9 mid-save with INNODB_TRX-observed open transaction, MySQL container kill during harvest) with **16 rows asserted intact per boot, zero loss/dup** (f5b00c686, PASS 7m08s). This is the R=2 evidence.
- **Demolish (owner-initiated):** house row removed; **furniture + design returned by mail** — code `ReturnHouseItemsToOwner` (`HousingManager.cs:881+`, respects `restore` flag); design returned (TODO grades). Wiki-confirmed for 1.2-era: Fandom Housing — "If you have defaulted for two weeks on your tax payment, your house is demolished and the plan [is] send back to you by mail"; reddit 2016 — "the house design is mailed back to the owner within 24 hours"; archeagecodex Full-Kit Beanstalk — "Security Deposit: 100 Tax Certificates… returned when the building is demolished (Not returned if the building is removed because of late payments)."
- **Unpaid-tax demolish:** canonical 1.2 auto-demolishes after tax default (2 weeks per wiki; 22 h mail delay per codex). **The fork deliberately disables this** (`HousingManager.cs:650–658` commented out, ZeromusXYZ note, dossier §3). Documented fork choice for a friends server — **flag to Josh, not a defect.**

### 4.3 C-dimension: U → 2

PROPERTY-01 C was U. The canonical persistence semantics are now identified and evidenced (this audit §4.1–4.2 + M3b merge + M3b-1..4 commits): what persists, what drops, the exact row contract, and the client strings. **Promote C U→2** in SCORECARD.md with this audit + M3b commits as evidence links.

---

## 5. Source register (cite + date + flag)

| # | Source | Date | Fact used | Flag |
|---|---|---|---|---|
| S1 | compact.sqlite3 r208022 (this repo) | data surface | all id/delay/limit values in §1–4 | **data-verified** |
| S2 | client pak `game_pak` housing_area.xml (62 zones, 380 shapes) | Feb 2023 build | polygon containment (dossier §2.2) | **data-verified** (server doesn't read it — gap) |
| S3 | archeagecodex.com item 15659 Potato Eyes | retrieved 2026-08-11 (mirrors 1.2-era tooltips) | "Matures in approx. 10m" — confirms §2.1 grow time | **data-verified (tooltip mirror)** |
| S4 | archeagecodex.com item 46094 Full-Kit Beanstalk | retrieved 2026-08-11 | deposit = 2 weekly payments, returned on demolish, not on late payment; 22 h mail delay | **data-verified (tooltip mirror)** |
| S5 | IGN ArcheAge Guide — Farms | 2014-10-18 | climate growth bonus; farm sizes 8×8/16×16/underwater 16×16; public farm 10 plants / 5 F2P; 24 h protection | **research-derived (contemporary wiki)** |
| S6 | ArcheRage/ArcheAge quest 4417 text (Planting Potatoes) | era-2 mirror, retrieved 2026-08-11 | watering speeds growth; harvest yield varies + rare seed chance; wild plants stealable; "three days" public-farm protection (contradicts S5 24 h — data says 24 h) | **research-derived** |
| S7 | reddit r/archeage — Housing & Farms wiki | 2014–2015 | land claiming, taxes, zones | **research-derived** |
| S8 | reddit r/archeage 2lrob1 ("New to ArcheAge") | 2014-11 | potato "30 minute growth time" — **contradicted by S1+S3 (10 min)** | research-derived, superseded |
| S9 | reddit r/archeage 4jy8qp ("Where did my house go?") | 2016-06 | design mailed back within 24 h after tax demolish | **research-derived (matches S4)** |
| S10 | Fandom ArcheAge Wiki — Housing | 1.2-era, retrieved 2026-08-11 | 2-week tax default → demolish, plan mailed back | **research-derived (matches code)** |
| S11 | Wikipedia — ArcheAge | 1.2-era | designated non-instanced zones, free placement, taxes; farm tax failure → scarecrow vulnerable | **research-derived** |
| S12 | upstream AAEmu PR #744 ("Fixes doodad related timings") | upstream history | 0.73 climate growth factor; GrowthRate config | **research-derived (upstream code, not 1.2 data)** |
| S13 | Gaming StackExchange 185722 (land demolition) | 2015-era | demolish → mail with design + paid taxes | **research-derived** |

Legend: data-verified = read from the 1.2 data surface in this repo; research-derived = contemporary wiki/forum/upstream, flagged because it can disagree with data (S6/S8 are the two disagreements found; both resolved in favor of data).

---

## 6. Findings → fix cards (Tai, Rei-gated)

| ID | Finding | Evidence | Suggested card |
|---|---|---|---|
| FIX-1 | **Livestock interaction funcs are log-only stubs** — feed/milk/shear/butcher do nothing; canonical chains exist in data (calf 2672, cow 5782→5786/5790, feeds 11 rows, loot packs 79/80/81) | §3.2; `DoodadFuncFeed/DairyCollect/Shear/Butcher` `Use()` bodies | Tai card: implement livestock interactions + E2E milk/shear/butcher loop |
| FIX-2 | **Polygon-level placement containment missing** — zone-level only; terrain 115/116, unit overlap 114, `max_construct_count` (error 766) unenforced; race protection absent | dossier §6 gaps 1,3,5,6; §1.2 | Tai card: pak AreaShape point-in-polygon + terrain/unit checks + max_construct_count (M3a-1 completion) |
| FIX-3 | **partial-domains.md stale deco-limit claims** on develop (lines 46/55/58/163) — calls `housing_deco_limits` dead weight while M3a enforces them | §1.3 | Tai doc-scrub card (or fold into Nei doc refresh) |
| FIX-4 | **Crop rot timer (48.33 h) + watering path untested** — engine machinery exists, no test pins the chains | §2.2, §2.3 | Tai card: pin rot + watering chains in CropHarvestLoopTests |
| NOTE | Unpaid-tax demolish disabled in fork (canonical 1.2 demolishes after 2 weeks) | §4.2 | Josh ruling request (not a defect) |

**Also noted:** the housing dossier branch `housing-placement-dossier` (53d748585) is **not merged to develop** — the audit relies on it; recommend merging it (docs-only) or tracking its merge as a follow-up card so the SCORECARD evidence link resolves.

---

## 7. Scorecard C-dimension updates (this commit)

SCORECARD.md rows changed:

- **HOUSING-01** — C stays 2; evidence string extended with this audit (canonical deco-limit confirmation + zone-vs-polygon caveat pointing at FIX-2).
- **FARM-01** — C stays 2 for the curated potato loop (growth timers now data-verified with codex cross-check); evidence notes livestock interaction stubs (FIX-1) so the C grade is not read as covering livestock interactions.
- **PROPERTY-01** — **C U→2** (canonical persistence semantics identified: §4.1 contract, §4.2 death/restart/demolish semantics, wiki + code evidence).

*Audit only — no code changed on this branch.*
